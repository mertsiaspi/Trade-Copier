using System;
using NinjaTrader.Cbi;

namespace NinjaTrader.NinjaScript.AddOns
{
	public enum AccountRole
	{
		Master,
		Follower
	}

	// Different prop firms compute the drawdown floor differently - never
	// hardcode one firm's rule, always dispatch on this per-account setting.
	public enum DrawdownType
	{
		// Floor trails the account's real-time equity high-water mark. Apex:
		// the floor stops rising once it reaches StartingBalance + freeze
		// offset (see AccountState.TrailingStopFreezeOffset).
		IntradayTrailing,

		// Floor is fixed off the previous day's end-of-day realized balance
		// and does not move with the current day's open P&L.
		EndOfDay
	}

	// Plain per-account state holder: identity, copy settings, configured risk
	// limits, live PnL/position, lock state, and copy stats. No risk decisions
	// or trailing-DD math here - that belongs in RiskManager, which reads and
	// updates this object.
	public class AccountState
	{
		private readonly object stateLock = new object();

		public Account NinjaAccount { get; }
		public string DisplayName { get; }
		public AccountRole Role { get; set; }

		// Per-account copy settings
		public decimal QuantityMultiplier { get; set; } = 1m;
		public int MaxContracts { get; set; } = int.MaxValue;

		// Configured risk limits (raw values, set from CopierSettings)
		public decimal DailyRiskBudget { get; set; }

		// Drawdown configuration - per account, per prop firm's actual rules.
		// See DrawdownType. Set via InitializeBalances/CopierSettings, never
		// hardcoded for "all Apex accounts" or similar, since firm rules and
		// account sizes vary per account.
		public DrawdownType DrawdownType { get; set; } = DrawdownType.IntradayTrailing;
		public decimal MaxDrawdownAmount { get; set; }
		public decimal StartingBalance { get; private set; }

		// Apex-style trailing freezes once the floor reaches StartingBalance +
		// this offset (Apex uses 100). Set to 0 for firms whose trailing never
		// freezes and simply keeps trailing the whole account lifetime.
		public decimal TrailingStopFreezeOffset { get; set; }

		// Live drawdown tracking - maintained by RiskManager.Evaluate, not set
		// by hand. Exposed here so the dashboard can read them directly.
		public decimal HighWaterMark { get; set; }
		public decimal EndOfDayBalance { get; set; }
		public decimal DrawdownFloor { get; set; }
		public bool IsTrailingFrozen { get; set; }

		// Live PnL, refreshed by the engine from account/position events
		public decimal DailyRealizedPnL { get; set; }
		public decimal DailyUnrealizedPnL { get; set; }
		public decimal DailyPnL
		{
			get { return DailyRealizedPnL + DailyUnrealizedPnL; }
		}

		public int NetPositionQuantity { get; set; }
		public MarketPosition PositionDirection { get; set; } = MarketPosition.Flat;

		public bool IsLocked { get; private set; }
		public string LockReason { get; private set; }

		// Copy stats. All mutation goes through the methods below so callers on
		// different event threads (execution, order update, reconciliation timer)
		// can't race on the counters - this was bug #7 in SimpleTradeCopierV2.
		public int OrdersSent { get; private set; }
		public int OrdersFilled { get; private set; }
		public int OrdersRejected { get; private set; }

		public AccountState(Account account, AccountRole role)
		{
			if (account == null)
				throw new ArgumentNullException("account");

			NinjaAccount = account;
			DisplayName = account.Name;
			Role = role;
		}

		// Seeds the balances drawdown tracking is measured from. Call once,
		// when the account's starting/funded balance becomes known from
		// CopierSettings, before RiskManager.Evaluate runs for this account.
		// StartingBalance has a private setter specifically so it can't be
		// changed without also reseeding HighWaterMark/EndOfDayBalance.
		public void InitializeBalances(decimal startingBalance)
		{
			StartingBalance = startingBalance;
			HighWaterMark = startingBalance;
			EndOfDayBalance = startingBalance;
		}

		public void Lock(string reason)
		{
			lock (stateLock)
			{
				IsLocked = true;
				LockReason = reason;
			}
		}

		// Called at the start of a new trading day to reset daily figures.
		// Does not clear IsLocked implicitly for anything other than the daily
		// lock - a manual kill-switch lock is expected to be cleared separately.
		public void ResetForNewDay()
		{
			lock (stateLock)
			{
				IsLocked = false;
				LockReason = null;
				DailyRealizedPnL = 0m;
				DailyUnrealizedPnL = 0m;
				OrdersSent = 0;
				OrdersFilled = 0;
				OrdersRejected = 0;
			}
		}

		public void IncrementOrdersSent()
		{
			lock (stateLock) { OrdersSent++; }
		}

		public void IncrementOrdersFilled()
		{
			lock (stateLock) { OrdersFilled++; }
		}

		public void IncrementOrdersRejected()
		{
			lock (stateLock) { OrdersRejected++; }
		}
	}
}
