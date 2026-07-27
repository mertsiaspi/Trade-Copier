using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace NinjaTrader.NinjaScript.AddOns
{
	// Persists per-account configuration AND the live risk state RiskManager
	// computes (HighWaterMark, DrawdownFloor, lock state, daily counters) to
	// a plain XML file, so a crash or restart mid-day doesn't silently
	// reseed the trailing high-water-mark from whatever equity happens to be
	// showing when the AddOn comes back up, and doesn't silently unlock an
	// account RiskManager had already flattened-and-locked earlier that day.
	//
	// Deliberately does NOT persist which account is master, which accounts
	// are checked as followers, or auto-start copying on load - restoring
	// those and resuming automatically would mean live orders can start
	// flowing the moment NT8 opens, with no human confirmation. Settings and
	// risk state are restored quietly; actually copying still always
	// requires an explicit Arm click, same as before this file existed.
	//
	// NOTE: NinjaTrader.Core.Globals.UserDataDir is the one call in this file
	// to double-check in the NinjaScript Editor - it is the standard NT8 way
	// for an AddOn to locate the user's Documents\NinjaTrader 8 folder for
	// custom files, but please confirm it resolves and compiles before this
	// runs against a funded account.
	public static class CopierSettings
	{
		private static readonly object fileLock = new object();

		public static event EventHandler<CopierLogEventArgs> LogMessage;

		private static string FilePath
		{
			get { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "CopierSettings.xml"); }
		}

		// Loads this account's persisted snapshot (matched by account name)
		// and applies it directly onto the freshly-constructed state, if one
		// was ever saved. No-op - state keeps its just-constructed defaults -
		// if nothing matches, or if the file is missing/unreadable/corrupt.
		//
		// Call this BEFORE the state is handed to an AccountRow: AccountRow's
		// constructor reads StartingBalance immediately to pre-fill the
		// starting-balance text box, and restoring here first is what makes
		// "don't touch the field, just hit Rearm" preserve the real trailing
		// HighWaterMark instead of CopierWindow.ApplyPendingStartingBalance
		// re-seeding it from today's current equity.
		public static void TryRestore(AccountState state)
		{
			if (state == null)
				return;

			lock (fileLock)
			{
				try
				{
					if (!File.Exists(FilePath))
						return;

					XElement root = XElement.Load(FilePath);
					XElement accountElement = root.Elements("Account")
						.FirstOrDefault(x => (string)x.Attribute("name") == state.DisplayName);

					if (accountElement == null)
						return;

					AccountSnapshot snapshot = ReadSnapshot(accountElement);

					string savedOnDate = (string)accountElement.Attribute("savedOnDate");
					bool sameTradingDay = savedOnDate == GetCentralTimeDateString();

					if (!sameTradingDay)
					{
						// A day boundary passed while this tool wasn't
						// tracking this account (offline, or its first time
						// being armed) - carrying yesterday's lock/counters
						// into a day that hasn't been evaluated yet would be
						// wrong, so those fields drop back to fresh-day
						// defaults, matching what RiskManager.RolloverToNewTradingDay
						// would have done had it been running continuously.
						// HighWaterMark/DrawdownFloor/EndOfDayBalance are
						// NOT reset here - trailing drawdown is multi-day by
						// design (RolloverToNewTradingDay never touches
						// HighWaterMark/DrawdownFloor either), but if a
						// rollover was actually missed while offline,
						// EndOfDayBalance can be stale - flagged below so a
						// human checks it rather than this guessing.
						snapshot.IsLocked = false;
						snapshot.LockReason = null;
						snapshot.OrdersSent = 0;
						snapshot.OrdersFilled = 0;
						snapshot.OrdersRejected = 0;

						RaiseLog(LogSeverity.Warning, string.Format(
							"{0}: restored saved risk state from {1} (a previous trading day) - lock state and daily counters reset for today. " +
							"If a rollover was missed while this was offline, EndOfDayBalance may be stale - verify on the Risk Monitor tab.",
							state.DisplayName, savedOnDate ?? "unknown date"));
					}

					state.RestoreSnapshot(snapshot);

					RaiseLog(LogSeverity.Info, string.Format("{0}: restored saved settings/risk state.", state.DisplayName));
				}
				catch (Exception ex)
				{
					RaiseLog(LogSeverity.Error, string.Format(
						"{0}: could not restore saved settings - starting from defaults. {1}", state.DisplayName, ex.Message));
				}
			}
		}

		// Snapshots every tracked account's current config + risk state to
		// disk. Cheap enough to call once per ~1s timer tick for a handful of
		// accounts - a crash can then never lose more than the last tick's
		// worth of state, including which accounts are currently locked.
		public static void SaveAll(IEnumerable<AccountState> accounts)
		{
			if (accounts == null)
				return;

			lock (fileLock)
			{
				try
				{
					string today = GetCentralTimeDateString();

					XElement root = new XElement("CopierAccounts");
					foreach (AccountState state in accounts)
					{
						XElement accountElement = new XElement("Account",
							new XAttribute("name", state.DisplayName),
							new XAttribute("savedOnDate", today));
						WriteSnapshot(accountElement, state.CaptureSnapshot());
						root.Add(accountElement);
					}

					// Write to a temp file and swap it in rather than saving
					// directly over the real file, so a process kill mid-write
					// can never leave a half-written, corrupt settings file
					// behind - worst case, the temp file is orphaned and the
					// previous good save is untouched.
					string tempPath = FilePath + ".tmp";
					root.Save(tempPath);

					if (File.Exists(FilePath))
						File.Delete(FilePath);
					File.Move(tempPath, FilePath);
				}
				catch (Exception ex)
				{
					RaiseLog(LogSeverity.Error, "Could not save settings/risk state - " + ex.Message);
				}
			}
		}

		private static AccountSnapshot ReadSnapshot(XElement accountElement)
		{
			return new AccountSnapshot
			{
				QuantityMultiplier = ReadDecimal(accountElement, "QuantityMultiplier", 1m),
				MaxContracts = ReadInt(accountElement, "MaxContracts", int.MaxValue),
				DailyRiskBudget = ReadDecimal(accountElement, "DailyRiskBudget", 0m),
				DrawdownType = ReadDrawdownType(accountElement, "DrawdownType", DrawdownType.IntradayTrailing),
				MaxDrawdownAmount = ReadDecimal(accountElement, "MaxDrawdownAmount", 0m),
				FloorSafetyBuffer = ReadDecimal(accountElement, "FloorSafetyBuffer", 0m),
				StartingBalance = ReadDecimal(accountElement, "StartingBalance", 0m),
				TrailingStopFreezeOffset = ReadDecimal(accountElement, "TrailingStopFreezeOffset", 0m),
				HighWaterMark = ReadDecimal(accountElement, "HighWaterMark", 0m),
				EndOfDayBalance = ReadDecimal(accountElement, "EndOfDayBalance", 0m),
				DrawdownFloor = ReadDecimal(accountElement, "DrawdownFloor", 0m),
				IsTrailingFrozen = ReadBool(accountElement, "IsTrailingFrozen", false),
				IsLocked = ReadBool(accountElement, "IsLocked", false),
				LockReason = (string)accountElement.Element("LockReason"),
				OrdersSent = ReadInt(accountElement, "OrdersSent", 0),
				OrdersFilled = ReadInt(accountElement, "OrdersFilled", 0),
				OrdersRejected = ReadInt(accountElement, "OrdersRejected", 0)
			};
		}

		private static void WriteSnapshot(XElement accountElement, AccountSnapshot snapshot)
		{
			accountElement.Add(
				new XElement("QuantityMultiplier", snapshot.QuantityMultiplier.ToString(CultureInfo.InvariantCulture)),
				new XElement("MaxContracts", snapshot.MaxContracts.ToString(CultureInfo.InvariantCulture)),
				new XElement("DailyRiskBudget", snapshot.DailyRiskBudget.ToString(CultureInfo.InvariantCulture)),
				new XElement("DrawdownType", snapshot.DrawdownType.ToString()),
				new XElement("MaxDrawdownAmount", snapshot.MaxDrawdownAmount.ToString(CultureInfo.InvariantCulture)),
				new XElement("FloorSafetyBuffer", snapshot.FloorSafetyBuffer.ToString(CultureInfo.InvariantCulture)),
				new XElement("StartingBalance", snapshot.StartingBalance.ToString(CultureInfo.InvariantCulture)),
				new XElement("TrailingStopFreezeOffset", snapshot.TrailingStopFreezeOffset.ToString(CultureInfo.InvariantCulture)),
				new XElement("HighWaterMark", snapshot.HighWaterMark.ToString(CultureInfo.InvariantCulture)),
				new XElement("EndOfDayBalance", snapshot.EndOfDayBalance.ToString(CultureInfo.InvariantCulture)),
				new XElement("DrawdownFloor", snapshot.DrawdownFloor.ToString(CultureInfo.InvariantCulture)),
				new XElement("IsTrailingFrozen", snapshot.IsTrailingFrozen.ToString(CultureInfo.InvariantCulture)),
				new XElement("IsLocked", snapshot.IsLocked.ToString(CultureInfo.InvariantCulture)),
				new XElement("LockReason", snapshot.LockReason ?? string.Empty),
				new XElement("OrdersSent", snapshot.OrdersSent.ToString(CultureInfo.InvariantCulture)),
				new XElement("OrdersFilled", snapshot.OrdersFilled.ToString(CultureInfo.InvariantCulture)),
				new XElement("OrdersRejected", snapshot.OrdersRejected.ToString(CultureInfo.InvariantCulture)));
		}

		private static decimal ReadDecimal(XElement parent, string name, decimal fallback)
		{
			decimal value;
			string text = (string)parent.Element(name);
			return text != null && decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ? value : fallback;
		}

		private static int ReadInt(XElement parent, string name, int fallback)
		{
			int value;
			string text = (string)parent.Element(name);
			return text != null && int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ? value : fallback;
		}

		private static bool ReadBool(XElement parent, string name, bool fallback)
		{
			bool value;
			string text = (string)parent.Element(name);
			return text != null && bool.TryParse(text, out value) ? value : fallback;
		}

		private static DrawdownType ReadDrawdownType(XElement parent, string name, DrawdownType fallback)
		{
			string text = (string)parent.Element(name);
			DrawdownType value;
			return text != null && Enum.TryParse(text, out value) ? value : fallback;
		}

		// Same Central Time boundary CopierManager already rolls daily state
		// over on (NinjaTrader itself resets AccountItem.RealizedProfitLoss
		// at midnight CT) - kept as its own small helper here rather than
		// reaching into CopierWindow's private one, since this is the only
		// other place that needs it.
		private static string GetCentralTimeDateString()
		{
			try
			{
				TimeZoneInfo centralZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
				DateTime centralDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, centralZone).Date;
				return centralDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			}
			catch (Exception)
			{
				return DateTime.UtcNow.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			}
		}

		private static void RaiseLog(LogSeverity severity, string message)
		{
			EventHandler<CopierLogEventArgs> handler = LogMessage;
			if (handler != null)
				handler(null, new CopierLogEventArgs(message, severity));
		}
	}
}
