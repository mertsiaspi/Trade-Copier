using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;

namespace NinjaTrader.NinjaScript.AddOns
{
	public enum LogSeverity
	{
		Info,
		Warning,
		Error
	}

	public class CopierLogEventArgs : EventArgs
	{
		public string Message { get; }
		public LogSeverity Severity { get; }

		public CopierLogEventArgs(string message, LogSeverity severity)
		{
			Message = message;
			Severity = severity;
		}
	}

	// Execution-based copier: listens to the master account's fills and stop
	// orders, mirrors them to followers (scaled per-follower), and reconciles
	// positions periodically. This is the ONLY class in the project that
	// calls Account.Submit/Cancel - RiskManager only decides and locks, this
	// class is the single place that actually sends orders.
	//
	// Threading: Account events can fire concurrently on different threads
	// per account (master vs. each follower). All mutable engine state below
	// is protected by one coarse lock (engineLock) rather than fine-grained
	// locks per dictionary - simpler to reason about correctly, and order
	// volume here is human-driven (one discretionary trader), not high
	// frequency, so a coarse lock is not a performance concern.
	public class CopierEngine
	{
		private readonly object engineLock = new object();

		private readonly AccountState master;
		private readonly List<AccountState> followers;

		private readonly HashSet<string> processedExecutionIds = new HashSet<string>();
		private readonly Queue<string> processedExecutionIdOrder = new Queue<string>();
		private const int MaxProcessedExecutionIds = 2000;

		// Per-instrument signed position, per account. AccountState.NetPositionQuantity
		// only holds a simplified direction-agnostic total for the dashboard -
		// this is the real breakdown, needed because master can hold MYM and
		// MES at once.
		private readonly Dictionary<AccountState, Dictionary<Instrument, int>> positionByInstrument =
			new Dictionary<AccountState, Dictionary<Instrument, int>>();

		// Master's currently-working stop order per instrument, and each
		// follower's mirrored copy of it. Used to (a) detect new/changed/
		// removed master stops so they can be mirrored, and (b) recognize
		// when a master execution came from a stop we already mirrored, so
		// it is NOT re-copied as a fresh order - the follower's own mirrored
		// stop fills independently.
		private readonly Dictionary<Instrument, Order> masterStopByInstrument =
			new Dictionary<Instrument, Order>();

		private readonly Dictionary<AccountState, Dictionary<Instrument, Order>> followerStopByInstrument =
			new Dictionary<AccountState, Dictionary<Instrument, Order>>();

		public event EventHandler<CopierLogEventArgs> LogMessage;

		// Reconciliation default is warn-only. Per CLAUDE.md: when unsure,
		// don't send an order, log loudly instead. Flip this on deliberately.
		public bool AutoCorrectReconciliation { get; set; }

		public CopierEngine(AccountState master, IEnumerable<AccountState> followers)
		{
			if (master == null)
				throw new ArgumentNullException("master");
			if (followers == null)
				throw new ArgumentNullException("followers");

			this.master = master;
			this.followers = new List<AccountState>(followers);
		}

		public void Start()
		{
			master.NinjaAccount.ExecutionUpdate += OnMasterExecutionUpdate;
			master.NinjaAccount.OrderUpdate += OnMasterOrderUpdate;
			master.NinjaAccount.AccountItemUpdate += OnAccountItemUpdate;

			foreach (AccountState follower in followers)
			{
				follower.NinjaAccount.ExecutionUpdate += OnFollowerExecutionUpdate;
				follower.NinjaAccount.OrderUpdate += OnFollowerOrderUpdate;
				follower.NinjaAccount.AccountItemUpdate += OnAccountItemUpdate;
			}

			lock (engineLock)
			{
				SeedAccountPositions(master);
				foreach (AccountState follower in followers)
					SeedAccountPositions(follower);
			}

			SeedAccountPnl(master);
			foreach (AccountState follower in followers)
				SeedAccountPnl(follower);
		}

		public void Stop()
		{
			master.NinjaAccount.ExecutionUpdate -= OnMasterExecutionUpdate;
			master.NinjaAccount.OrderUpdate -= OnMasterOrderUpdate;
			master.NinjaAccount.AccountItemUpdate -= OnAccountItemUpdate;

			foreach (AccountState follower in followers)
			{
				follower.NinjaAccount.ExecutionUpdate -= OnFollowerExecutionUpdate;
				follower.NinjaAccount.OrderUpdate -= OnFollowerOrderUpdate;
				follower.NinjaAccount.AccountItemUpdate -= OnAccountItemUpdate;
			}

			lock (engineLock)
			{
				positionByInstrument.Clear();
				masterStopByInstrument.Clear();
				followerStopByInstrument.Clear();
				processedExecutionIds.Clear();
				processedExecutionIdOrder.Clear();
			}
		}

		// Kill switch / RiskManager.BreachDetected entry point. Flattens every
		// instrument this account currently holds. Not gated by IsLocked - the
		// whole point is to work even when (especially when) the account just
		// got locked.
		public void FlattenAccount(AccountState state, string reason)
		{
			if (state == null)
				throw new ArgumentNullException("state");

			List<KeyValuePair<Instrument, int>> positions;
			lock (engineLock)
			{
				positions = new List<KeyValuePair<Instrument, int>>(GetPositionMap(state));
			}

			foreach (KeyValuePair<Instrument, int> position in positions)
			{
				if (position.Value == 0)
					continue;

				OrderAction action = position.Value > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
				SubmitOrder(state, position.Key, action, OrderType.Market, Math.Abs(position.Value), 0, 0,
					"Flatten-" + state.DisplayName);
			}

			RaiseLog(LogSeverity.Warning, string.Format("{0}: FLATTENED - {1}", state.DisplayName, reason));
		}

		// Call periodically (timer owned by CopierWindow, e.g. every 5s) to
		// catch drift between master and follower positions - a rejected
		// follower order, a manual trade on a follower account, etc.
		public void RunReconciliation()
		{
			foreach (AccountState follower in followers)
			{
				if (follower.IsLocked)
					continue;

				// This was the reported critical bug: an uncaught exception
				// for one follower used to abort the whole foreach, silently
				// leaving every subsequent follower unreconciled - possibly
				// forever, if the same exception recurred every cycle. Each
				// follower is now fully isolated.
				try
				{
					List<Tuple<Instrument, int>> corrections = new List<Tuple<Instrument, int>>();

					lock (engineLock)
					{
						HashSet<Instrument> instruments = new HashSet<Instrument>(GetPositionMap(master).Keys);
						foreach (Instrument instrument in GetPositionMap(follower).Keys)
							instruments.Add(instrument);

						foreach (Instrument instrument in instruments)
						{
							int masterQty = GetPosition(master, instrument);
							int followerQty = GetPosition(follower, instrument);
							int targetQty = (int)Math.Round(masterQty * follower.QuantityMultiplier, MidpointRounding.AwayFromZero);
							int delta = targetQty - followerQty;

							if (delta == 0)
								continue;

							RaiseLog(LogSeverity.Warning, string.Format(
								"{0}: reconciliation mismatch on {1} - master implies {2}, follower has {3} (delta {4}).",
								follower.DisplayName, instrument.FullName, targetQty, followerQty, delta));

							if (AutoCorrectReconciliation)
								corrections.Add(new Tuple<Instrument, int>(instrument, delta));
						}
					}

					foreach (Tuple<Instrument, int> correction in corrections)
					{
						// NOTE: uses the plain Buy/Sell pair, not SellShort/BuyToCover -
						// verify this is the right action pair for how your follower
						// accounts are configured (futures accounts are usually fine
						// with Buy/Sell for both opening and closing).
						OrderAction action = correction.Item2 > 0 ? OrderAction.Buy : OrderAction.Sell;
						SubmitOrder(follower, correction.Item1, action, OrderType.Market, Math.Abs(correction.Item2), 0, 0,
							"Reconcile-" + follower.DisplayName);
					}
				}
				catch (Exception ex)
				{
					RaiseLog(LogSeverity.Error, string.Format(
						"{0}: reconciliation failed - {1}. Other accounts are still processed; will retry next cycle.",
						follower.DisplayName, ex.Message));
				}
			}
		}

		private void OnMasterExecutionUpdate(object sender, ExecutionEventArgs e)
		{
			Execution execution = e.Execution;
			if (execution == null)
				return;

			bool isReCopyOfMirroredStop;

			lock (engineLock)
			{
				if (!TryMarkExecutionProcessed(execution.ExecutionId))
					return;

				UpdatePositionFromExecution(master, execution);

				// Checked via the TRACKED STOP'S OWN state, not by matching
				// execution.Order against it by reference. This used to
				// require execution.Order to be the exact same object, which
				// silently failed whenever it was null - and that turns out
				// to happen for stop fills too, not just limit/ATM-style
				// exits. OrderUpdate(Filled) always fires before
				// ExecutionUpdate for the same fill (mutating the same Order
				// object in place), so by the time we get here the tracked
				// stop's own OrderState already reflects Filled if it was
				// this fill that completed it - regardless of whether this
				// particular Execution carries an Order reference at all.
				Order trackedStop;
				isReCopyOfMirroredStop = masterStopByInstrument.TryGetValue(execution.Instrument, out trackedStop)
					&& trackedStop.OrderState == OrderState.Filled;

				if (isReCopyOfMirroredStop)
					masterStopByInstrument.Remove(execution.Instrument);
			}

			if (isReCopyOfMirroredStop)
			{
				RaiseLog(LogSeverity.Info, string.Format(
					"{0}: master stop filled on {1} ({2} x{3}) - follower stops fill on their own, not re-copying.",
					master.DisplayName, execution.Instrument.FullName, execution.MarketPosition, execution.Quantity));
			}
			else
			{
				// Deliberately NOT gated on execution.Order being non-null
				// (that was the original bug: some fills - e.g. profit-
				// target/ATM-style exits - report a null Order, which meant
				// this whole copy was skipped and silently left to whatever
				// reconciliation cycle came next). Direction only needs
				// execution.MarketPosition, which is always populated.
				CopyExecutionToFollowers(execution);
				SyncFollowerStops(execution.Instrument);
			}

			// Master going flat (via the "Close" button, a limit fill, a
			// stop, anything) is the single highest-stakes moment to confirm
			// every follower actually got out too - including the stop-refill
			// branch above, since a follower whose own mirrored stop was
			// never successfully submitted has nothing to close it otherwise.
			// Don't wait for the next scheduled reconciliation tick, check now.
			if (GetPosition(master, execution.Instrument) == 0)
				RunReconciliation();
		}

		private void OnMasterOrderUpdate(object sender, OrderEventArgs e)
		{
			Order order = e.Order;
			if (order == null)
				return;

			if (order.OrderState == OrderState.Rejected)
				RaiseLog(LogSeverity.Warning, string.Format("{0}: order rejected - {1}", master.DisplayName, DescribeOrder(order)));

			if (order.OrderType != OrderType.StopMarket && order.OrderType != OrderType.StopLimit)
				return;

			switch (order.OrderState)
			{
				case OrderState.Working:
				case OrderState.Accepted:
					lock (engineLock)
					{
						masterStopByInstrument[order.Instrument] = order;
					}
					SyncFollowerStops(order.Instrument);
					break;

				case OrderState.Cancelled:
					lock (engineLock)
					{
						masterStopByInstrument.Remove(order.Instrument);
					}
					SyncFollowerStops(order.Instrument);
					break;

				case OrderState.Filled:
					// OnMasterExecutionUpdate already removed the tracked stop
					// and skipped re-copying it - nothing further to do here.
					break;
			}
		}

		private void OnFollowerExecutionUpdate(object sender, ExecutionEventArgs e)
		{
			Account account = sender as Account;
			Execution execution = e.Execution;
			if (account == null || execution == null)
				return;

			AccountState follower = FindFollower(account);
			if (follower == null)
				return;

			lock (engineLock)
			{
				if (!TryMarkExecutionProcessed(execution.ExecutionId))
					return;

				UpdatePositionFromExecution(follower, execution);
			}

			// Re-check stop sizing now that this follower's position just
			// changed - handles the race where master's stop update arrives
			// before this follower's own copied entry has filled.
			SyncFollowerStops(execution.Instrument);
		}

		private void OnFollowerOrderUpdate(object sender, OrderEventArgs e)
		{
			Account account = sender as Account;
			Order order = e.Order;
			if (account == null || order == null)
				return;

			AccountState follower = FindFollower(account);
			if (follower == null)
				return;

			if (order.OrderState == OrderState.Rejected)
			{
				follower.IncrementOrdersRejected();
				RaiseLog(LogSeverity.Error, string.Format("{0}: order REJECTED - {1}", follower.DisplayName, DescribeOrder(order)));
			}
			else if (order.OrderState == OrderState.Filled)
			{
				follower.IncrementOrdersFilled();
			}
		}

		// NOTE: AccountItem.RealizedProfitLoss is reported by NinjaTrader
		// already reset to 0 at midnight Central Time, so it can be assigned
		// straight to DailyRealizedPnL - no separate baseline/subtraction
		// needed here. UnrealizedProfitLoss is inherently a live snapshot
		// (mark-to-market of the open position), not cumulative, so the same
		// direct assignment applies.
		private void OnAccountItemUpdate(object sender, AccountItemEventArgs e)
		{
			Account account = sender as Account;
			if (account == null)
				return;

			AccountState state = FindTrackedAccount(account);
			if (state == null)
				return;

			if (e.AccountItem == AccountItem.RealizedProfitLoss)
				state.DailyRealizedPnL = (decimal)e.Value;
			else if (e.AccountItem == AccountItem.UnrealizedProfitLoss)
				state.DailyUnrealizedPnL = (decimal)e.Value;
		}

		// Reads the current values once at Start() so the dashboard doesn't
		// sit at 0/0 until the next actual account item change comes in.
		private void SeedAccountPnl(AccountState state)
		{
			try
			{
				state.DailyRealizedPnL = (decimal)state.NinjaAccount.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
				state.DailyUnrealizedPnL = (decimal)state.NinjaAccount.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
			}
			catch (Exception ex)
			{
				RaiseLog(LogSeverity.Warning, string.Format("{0}: could not seed starting PnL - {1}", state.DisplayName, ex.Message));
			}
		}

		// Uses execution.MarketPosition (always populated) to get a signed
		// position delta rather than replaying masterOrder.OrderAction - this
		// works even when execution.Order is null (documented to happen for
		// some fills, e.g. ATM/profit-target style exits), which used to mean
		// the whole copy was skipped and left entirely to the next
		// reconciliation cycle.
		private void CopyExecutionToFollowers(Execution execution)
		{
			int masterSignedDelta = execution.MarketPosition == MarketPosition.Long ? execution.Quantity
				: execution.MarketPosition == MarketPosition.Short ? -execution.Quantity
				: 0;

			if (masterSignedDelta == 0)
				return;

			foreach (AccountState follower in followers)
			{
				if (follower.IsLocked)
				{
					RaiseLog(LogSeverity.Warning, string.Format(
						"{0}: skipped copy on {1} (master delta {2}{3}) - account is locked.",
						follower.DisplayName, execution.Instrument.FullName, masterSignedDelta > 0 ? "+" : "", masterSignedDelta));
					continue;
				}

				// One follower's failure must never stop the rest from being
				// processed - this was the second bug: an uncaught exception
				// here (or in reconciliation/stop-sync below) silently
				// aborted the loop partway through the follower list.
				try
				{
					int signedDeltaToSend;
					lock (engineLock)
					{
						signedDeltaToSend = ComputeFollowerSignedDelta(follower, execution.Instrument, masterSignedDelta);
					}

					if (signedDeltaToSend == 0)
						continue;

					OrderAction action = signedDeltaToSend > 0 ? OrderAction.Buy : OrderAction.Sell;
					SubmitOrder(follower, execution.Instrument, action, OrderType.Market,
						Math.Abs(signedDeltaToSend), 0, 0, "Copier-" + execution.ExecutionId);
				}
				catch (Exception ex)
				{
					RaiseLog(LogSeverity.Error, string.Format(
						"{0}: copy failed on {1} - {2}. Other followers are still processed; reconciliation will catch any resulting drift.",
						follower.DisplayName, execution.Instrument.FullName, ex.Message));
				}
			}
		}

		// Must be called while holding engineLock - reads/writes CopyQuantityCarry
		// and reads NetPositionQuantity. Truncate (not Floor) is used so the
		// carry remainder behaves correctly for negative (closing/short)
		// deltas too, not just positive ones.
		private int ComputeFollowerSignedDelta(AccountState follower, Instrument instrument, int masterSignedDelta)
		{
			decimal exact = masterSignedDelta * follower.QuantityMultiplier + follower.CopyQuantityCarry;
			int wholeContracts = (int)Math.Truncate(exact);
			follower.CopyQuantityCarry = exact - wholeContracts;

			if (wholeContracts == 0)
				return 0;

			int currentPosition = GetPosition(follower, instrument);
			bool isIncreasing = Math.Abs(currentPosition + wholeContracts) > Math.Abs(currentPosition);

			// MaxContracts is treated as a total-across-all-instruments cap on
			// this account, matching NetPositionQuantity's rollup. If you
			// intended a per-instrument cap instead, this needs revisiting.
			// Only ever clips a position-increasing move - a closing/reducing
			// trade must always be allowed through in full.
			if (isIncreasing && follower.MaxContracts > 0 && follower.MaxContracts < int.MaxValue)
			{
				int allowedRoom = follower.MaxContracts - follower.NetPositionQuantity;
				if (allowedRoom <= 0)
				{
					RaiseLog(LogSeverity.Warning, string.Format(
						"{0}: copy of {1} contracts skipped - already at MaxContracts ({2}).",
						follower.DisplayName, wholeContracts, follower.MaxContracts));
					return 0;
				}
				if (Math.Abs(wholeContracts) > allowedRoom)
				{
					int clipped = wholeContracts > 0 ? allowedRoom : -allowedRoom;
					RaiseLog(LogSeverity.Warning, string.Format(
						"{0}: copy clipped from {1} to {2} contracts - MaxContracts limit ({3}).",
						follower.DisplayName, wholeContracts, clipped, follower.MaxContracts));
					wholeContracts = clipped;
				}
			}

			return wholeContracts;
		}

		// Reconciles master's currently-tracked stop for one instrument against
		// every follower's mirrored stop, submitting/cancelling as needed.
		// Called both when master's stop changes AND when a follower's own
		// position changes, so a follower whose entry fills late still gets a
		// correctly-sized stop once its position catches up.
		private void SyncFollowerStops(Instrument instrument)
		{
			Order masterStop;
			List<Tuple<AccountState, StopAction>> actions = new List<Tuple<AccountState, StopAction>>();

			lock (engineLock)
			{
				bool hasMasterStop = masterStopByInstrument.TryGetValue(instrument, out masterStop);

				foreach (AccountState follower in followers)
				{
					// Isolate each follower - one bad Order/state shouldn't
					// stop stop-sizing being checked for the rest.
					try
					{
						Dictionary<Instrument, Order> followerStops = GetFollowerStopMap(follower);
						Order existingStop;
						followerStops.TryGetValue(instrument, out existingStop);

						if (!hasMasterStop)
						{
							if (existingStop != null)
							{
								followerStops.Remove(instrument);
								actions.Add(new Tuple<AccountState, StopAction>(follower,
									StopAction.Cancel(existingStop, "master stop removed")));
							}
							continue;
						}

						if (follower.IsLocked)
							continue;

						int desiredQuantity = Math.Abs(GetPosition(follower, instrument));
						if (desiredQuantity <= 0)
							continue; // follower's copied entry hasn't filled yet - retried on its next execution

						bool upToDate = existingStop != null
							&& existingStop.OrderState != OrderState.Cancelled
							&& existingStop.OrderState != OrderState.Rejected
							&& existingStop.OrderState != OrderState.Filled
							&& existingStop.StopPrice == masterStop.StopPrice
							&& existingStop.Quantity == desiredQuantity;

						if (upToDate)
							continue;

						if (existingStop != null)
							followerStops.Remove(instrument);

						actions.Add(new Tuple<AccountState, StopAction>(follower,
							StopAction.Resubmit(existingStop, masterStop, desiredQuantity)));
					}
					catch (Exception ex)
					{
						RaiseLog(LogSeverity.Error, string.Format(
							"{0}: stop sync check failed on {1} - {2}. Other followers still checked.",
							follower.DisplayName, instrument.FullName, ex.Message));
					}
				}
			}

			foreach (Tuple<AccountState, StopAction> entry in actions)
			{
				AccountState follower = entry.Item1;
				StopAction action = entry.Item2;

				// CancelOrder/SubmitOrder already catch internally, but wrap
				// the whole per-follower entry too - consistent with every
				// other per-follower loop in this file, so nothing here can
				// ever skip a later follower in the same batch.
				try
				{
					if (action.OrderToCancel != null)
						CancelOrder(follower, action.OrderToCancel, action.CancelReason);

					if (action.MasterStopToMirror != null)
					{
						// Follower is briefly unprotected between the cancel
						// above and this resubmit - a cancel+replace is
						// simpler and safer to get right than
						// Account.Change(), but flag the gap.
						Order newStop = SubmitOrder(follower, instrument, action.MasterStopToMirror.OrderAction,
							action.MasterStopToMirror.OrderType, action.DesiredQuantity,
							action.MasterStopToMirror.LimitPrice, action.MasterStopToMirror.StopPrice,
							"Copier-Stop-" + follower.DisplayName);

						if (newStop != null)
						{
							lock (engineLock)
							{
								GetFollowerStopMap(follower)[instrument] = newStop;
							}
						}
					}
				}
				catch (Exception ex)
				{
					RaiseLog(LogSeverity.Error, string.Format(
						"{0}: stop sync action failed on {1} - {2}. Other followers still processed.",
						follower.DisplayName, instrument.FullName, ex.Message));
				}
			}
		}

		// Tiny local record for the two things SyncFollowerStops can decide to
		// do per follower - kept as a struct instead of two parallel lists so
		// the cancel-then-resubmit ordering per follower can't drift apart.
		private struct StopAction
		{
			public Order OrderToCancel;
			public string CancelReason;
			public Order MasterStopToMirror;
			public int DesiredQuantity;

			public static StopAction Cancel(Order order, string reason)
			{
				return new StopAction { OrderToCancel = order, CancelReason = reason };
			}

			public static StopAction Resubmit(Order existingOrNull, Order masterStop, int desiredQuantity)
			{
				return new StopAction
				{
					OrderToCancel = existingOrNull,
					CancelReason = "resyncing to master stop change",
					MasterStopToMirror = masterStop,
					DesiredQuantity = desiredQuantity
				};
			}
		}

		private void UpdatePositionFromExecution(AccountState state, Execution execution)
		{
			int signedDelta = execution.MarketPosition == MarketPosition.Long ? execution.Quantity
				: execution.MarketPosition == MarketPosition.Short ? -execution.Quantity
				: 0;

			AdjustPosition(state, execution.Instrument, signedDelta);
		}

		// Must be called while holding engineLock.
		private void AdjustPosition(AccountState state, Instrument instrument, int delta)
		{
			if (delta == 0)
				return;

			Dictionary<Instrument, int> map = GetPositionMap(state);
			int current;
			map.TryGetValue(instrument, out current);
			int updated = current + delta;
			map[instrument] = updated;

			int totalAbs = 0;
			foreach (int qty in map.Values)
				totalAbs += Math.Abs(qty);

			state.NetPositionQuantity = totalAbs;
			state.PositionDirection = updated > 0 ? MarketPosition.Long
				: updated < 0 ? MarketPosition.Short
				: MarketPosition.Flat;
		}

		private void SeedAccountPositions(AccountState state)
		{
			foreach (Position position in state.NinjaAccount.Positions)
			{
				int signedQty = position.MarketPosition == MarketPosition.Long ? position.Quantity
					: position.MarketPosition == MarketPosition.Short ? -position.Quantity
					: 0;

				if (signedQty != 0)
					AdjustPosition(state, position.Instrument, signedQty);
			}
		}

		private Dictionary<Instrument, int> GetPositionMap(AccountState state)
		{
			Dictionary<Instrument, int> map;
			if (!positionByInstrument.TryGetValue(state, out map))
			{
				map = new Dictionary<Instrument, int>();
				positionByInstrument[state] = map;
			}
			return map;
		}

		private int GetPosition(AccountState state, Instrument instrument)
		{
			int qty;
			return GetPositionMap(state).TryGetValue(instrument, out qty) ? qty : 0;
		}

		private Dictionary<Instrument, Order> GetFollowerStopMap(AccountState follower)
		{
			Dictionary<Instrument, Order> map;
			if (!followerStopByInstrument.TryGetValue(follower, out map))
			{
				map = new Dictionary<Instrument, Order>();
				followerStopByInstrument[follower] = map;
			}
			return map;
		}

		private AccountState FindFollower(Account account)
		{
			foreach (AccountState follower in followers)
				if (ReferenceEquals(follower.NinjaAccount, account))
					return follower;
			return null;
		}

		// Like FindFollower, but also matches master - used by the account
		// item (PnL) handler, which fires for master too.
		private AccountState FindTrackedAccount(Account account)
		{
			if (ReferenceEquals(master.NinjaAccount, account))
				return master;
			return FindFollower(account);
		}

		// Must be called while holding engineLock.
		private bool TryMarkExecutionProcessed(string executionId)
		{
			if (!processedExecutionIds.Add(executionId))
				return false; // duplicate event delivery, already handled

			processedExecutionIdOrder.Enqueue(executionId);
			while (processedExecutionIdOrder.Count > MaxProcessedExecutionIds)
			{
				string oldest = processedExecutionIdOrder.Dequeue();
				processedExecutionIds.Remove(oldest);
			}
			return true;
		}

		// Read-only summary for the dashboard, e.g. "MYM +2, MES -1" or "Flat".
		public string DescribePosition(AccountState state)
		{
			lock (engineLock)
			{
				Dictionary<Instrument, int> map = GetPositionMap(state);
				List<string> parts = new List<string>();
				foreach (KeyValuePair<Instrument, int> position in map)
				{
					if (position.Value == 0)
						continue;
					parts.Add(string.Format("{0} {1}{2}", position.Key.FullName,
						position.Value > 0 ? "+" : "", position.Value));
				}
				return parts.Count == 0 ? "Flat" : string.Join(", ", parts);
			}
		}

		// The single place in the project that calls Account.CreateOrder/Submit.
		// NOTE: verify Account.CreateOrder's exact parameter order against the
		// NinjaScript Editor - a wrong same-typed parameter (e.g. limitPrice
		// and stopPrice swapped) would compile fine but send the wrong price.
		private Order SubmitOrder(AccountState state, Instrument instrument, OrderAction action, OrderType orderType,
			int quantity, double limitPrice, double stopPrice, string name)
		{
			try
			{
				Order order = state.NinjaAccount.CreateOrder(instrument, action, orderType, OrderEntry.Automated,
					TimeInForce.Day, quantity, limitPrice, stopPrice, string.Empty, name,
					NinjaTrader.Core.Globals.MaxDate, null);

				state.NinjaAccount.Submit(new[] { order });
				state.IncrementOrdersSent();

				RaiseLog(LogSeverity.Info, string.Format("{0}: sent {1} {2} x{3} on {4} ({5})",
					state.DisplayName, action, orderType, quantity, instrument.FullName, name));

				return order;
			}
			catch (Exception ex)
			{
				RaiseLog(LogSeverity.Error, string.Format(
					"{0}: order submission threw - {1}. No order sent.", state.DisplayName, ex.Message));
				return null;
			}
		}

		private void CancelOrder(AccountState state, Order order, string reason)
		{
			try
			{
				state.NinjaAccount.Cancel(new[] { order });
				RaiseLog(LogSeverity.Info, string.Format("{0}: cancelling {1} - {2}", state.DisplayName, DescribeOrder(order), reason));
			}
			catch (Exception ex)
			{
				RaiseLog(LogSeverity.Error, string.Format("{0}: cancel failed - {1}", state.DisplayName, ex.Message));
			}
		}

		private string DescribeOrder(Order order)
		{
			return string.Format("{0} {1} {2} x{3} @ stop {4:0.00} (state: {5})",
				order.Instrument, order.OrderAction, order.OrderType, order.Quantity, order.StopPrice, order.OrderState);
		}

		private void RaiseLog(LogSeverity severity, string message)
		{
			EventHandler<CopierLogEventArgs> handler = LogMessage;
			if (handler != null)
				handler(this, new CopierLogEventArgs(message, severity));
		}

		// Lets other parts of the AddOn (CopierManager's per-account
		// RiskManager.Evaluate/RolloverToNewTradingDay calls) surface
		// failures into the same log feed the dashboard already displays,
		// since RiskManager has no logging channel of its own.
		public void Log(LogSeverity severity, string message)
		{
			RaiseLog(severity, message);
		}
	}
}
