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
				// follower is now fully isolated. The decide-and-act sequence
				// also runs under one lock acquisition now (matching
				// SyncFollowerStops/SyncFollowerPositions), since deciding
				// under the lock and acting after it let an overlapping call
				// - the periodic timer tick and the immediate on-flat check
				// firing close together, for example - double-send the same
				// correction.
				try
				{
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

							if (!AutoCorrectReconciliation)
								continue;

							// NOTE: uses the plain Buy/Sell pair, not SellShort/BuyToCover -
							// verify this is the right action pair for how your follower
							// accounts are configured (futures accounts are usually fine
							// with Buy/Sell for both opening and closing).
							OrderAction action = delta > 0 ? OrderAction.Buy : OrderAction.Sell;
							SubmitOrder(follower, instrument, action, OrderType.Market, Math.Abs(delta), 0, 0,
								"Reconcile-" + follower.DisplayName);
						}
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

			lock (engineLock)
			{
				if (!TryMarkExecutionProcessed(execution.ExecutionId))
					return;

				// Position bookkeeping always uses Order.OrderAction when
				// available (unambiguous), and only falls back to a direct
				// ground-truth resync from the account when Order is null -
				// never a guess. See UpdatePositionFromExecution.
				UpdatePositionFromExecution(master, execution);

				// If this execution completed the stop tracked for this
				// instrument, stop tracking it - SyncFollowerStops needs to
				// know master no longer has an active stop so it won't try
				// to keep mirroring a filled one. SyncFollowerPositions below
				// does not need this distinction at all: it is driven by
				// actual position numbers, not by what kind of order caused
				// them to change.
				Order masterOrder = execution.Order;
				Order trackedStop;
				if (masterOrder != null
					&& masterStopByInstrument.TryGetValue(execution.Instrument, out trackedStop)
					&& ReferenceEquals(trackedStop, masterOrder))
				{
					masterStopByInstrument.Remove(execution.Instrument);
				}
			}

			// Always syncs every follower toward master's ACTUAL current
			// position x multiplier, rather than replaying this one
			// execution's own delta. That distinction is exactly what fixed
			// two real bugs: (1) closing master's position while a follower
			// was already out of sync (e.g. its own mirrored stop never
			// fired) used to send that follower a naked order that OPENED a
			// new position instead of closing nothing; a target-based sync
			// computes 0 in that case. (2) A follower whose own mirrored
			// stop failed to fire for any reason is now caught immediately
			// here instead of only by a possibly-disabled auto-correct
			// reconciliation cycle. This is idempotent - a no-op for a
			// follower already at its target - so it is safe to call after
			// every execution unconditionally, stop fills included.
			SyncFollowerPositions(execution.Instrument);
			SyncFollowerStops(execution.Instrument);

			// Master going flat (via the "Close" button, a limit fill, a
			// stop, anything) is the highest-stakes moment to also sweep any
			// OTHER instrument a follower might be holding that master isn't
			// (SyncFollowerPositions above only checked this one instrument).
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

		// Target-based sync for one instrument: computes what each follower's
		// position SHOULD be (master's current position x multiplier) and
		// sends whatever delta closes the gap - not a replay of one
		// execution's own delta. This is idempotent: a follower already at
		// its target produces delta 0 and nothing is sent, so it is safe to
		// call after every master execution unconditionally, including stop
		// fills the follower's own mirrored stop may or may not have already
		// handled. This is also exactly what makes it self-healing for a
		// follower that was out of sync for any reason going in.
		// Held for the whole decide-and-act sequence per follower, same
		// reasoning as SyncFollowerStops: this can be triggered from more
		// than one event for what amounts to the same logical moment (a
		// master execution and a shortly-following follower execution both
		// calling this for the same instrument), and deciding under the lock
		// but acting after it let those overlap into duplicate orders.
		private void SyncFollowerPositions(Instrument instrument)
		{
			lock (engineLock)
			{
				foreach (AccountState follower in followers)
				{
					if (follower.IsLocked)
						continue;

					// One follower's failure must never stop the rest from
					// being processed - an uncaught exception here used to
					// silently abort the loop partway through the follower
					// list.
					try
					{
						int masterQty = GetPosition(master, instrument);
						int followerQty = GetPosition(follower, instrument);
						int targetQty = (int)Math.Round(masterQty * follower.QuantityMultiplier, MidpointRounding.AwayFromZero);
						int delta = targetQty - followerQty;

						// MaxContracts is treated as a total-across-all-instruments
						// cap on this account, matching NetPositionQuantity's
						// rollup. Only ever clips a move that grows the position's
						// magnitude - closing/reducing must always go through in
						// full.
						bool isIncreasing = delta != 0 && Math.Abs(followerQty + delta) > Math.Abs(followerQty);
						if (isIncreasing && follower.MaxContracts > 0 && follower.MaxContracts < int.MaxValue)
						{
							int allowedRoom = follower.MaxContracts - follower.NetPositionQuantity;
							if (allowedRoom <= 0)
							{
								RaiseLog(LogSeverity.Warning, string.Format(
									"{0}: sync on {1} skipped - already at MaxContracts ({2}).",
									follower.DisplayName, instrument.FullName, follower.MaxContracts));
								delta = 0;
							}
							else if (Math.Abs(delta) > allowedRoom)
							{
								int clipped = delta > 0 ? allowedRoom : -allowedRoom;
								RaiseLog(LogSeverity.Warning, string.Format(
									"{0}: sync on {1} clipped from {2} to {3} - MaxContracts limit ({4}).",
									follower.DisplayName, instrument.FullName, delta, clipped, follower.MaxContracts));
								delta = clipped;
							}
						}

						if (delta == 0)
							continue;

						OrderAction action = delta > 0 ? OrderAction.Buy : OrderAction.Sell;
						SubmitOrder(follower, instrument, action, OrderType.Market, Math.Abs(delta), 0, 0,
							"Copier-" + follower.DisplayName + "-" + instrument.FullName);
					}
					catch (Exception ex)
					{
						RaiseLog(LogSeverity.Error, string.Format(
							"{0}: position sync failed on {1} - {2}. Other followers are still processed.",
							follower.DisplayName, instrument.FullName, ex.Message));
					}
				}
			}
		}

		// Reconciles master's currently-tracked stop for one instrument against
		// every follower's mirrored stop, submitting/cancelling as needed.
		// Called both when master's stop changes AND when a follower's own
		// position changes, so a follower whose entry fills late still gets a
		// correctly-sized stop once its position catches up.
		//
		// The whole decide-and-act sequence runs under one lock acquisition,
		// deliberately including the Cancel/Submit calls. This used to decide
		// under the lock and act afterward, which let two near-simultaneous
		// triggers for the same logical stop placement - NT8 firing
		// OrderState.Accepted then Working as separate OnMasterOrderUpdate
		// events for the same new order, for example - both decide "no
		// mirrored stop exists yet" before either had recorded its own
		// submission. Each independently submitted one, multiplying the
		// mirrored stop's effective quantity by however many redundant
		// triggers overlapped. Order submission is fire-and-forget (it
		// doesn't block on a fill), so holding the lock this long is not a
		// real contention concern - it is the fix.
		private void SyncFollowerStops(Instrument instrument)
		{
			lock (engineLock)
			{
				Order masterStop;
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
								CancelOrder(follower, existingStop, "master stop removed");
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

						// Remove from tracking BEFORE submitting, not after -
						// so if this same method somehow re-entered for this
						// follower before the submit below returns, it would
						// see no tracked stop and correctly fall through to
						// this same branch rather than a stale "up to date"
						// read, instead of silently doing nothing.
						followerStops.Remove(instrument);

						if (existingStop != null)
						{
							// Follower is briefly unprotected between this
							// cancel and the resubmit below - a cancel+replace
							// is simpler and safer to get right than
							// Account.Change(), but flag the gap.
							CancelOrder(follower, existingStop, "resyncing to master stop change");
						}

						Order newStop = SubmitOrder(follower, instrument, masterStop.OrderAction, masterStop.OrderType,
							desiredQuantity, masterStop.LimitPrice, masterStop.StopPrice,
							"Copier-Stop-" + follower.DisplayName);

						if (newStop != null)
							followerStops[instrument] = newStop;
					}
					catch (Exception ex)
					{
						RaiseLog(LogSeverity.Error, string.Format(
							"{0}: stop sync failed on {1} - {2}. Other followers still processed.",
							follower.DisplayName, instrument.FullName, ex.Message));
					}
				}
			}
		}

		// Must be called while holding engineLock. Uses Order.OrderAction when
		// available (unambiguous); when Order is null, resyncs this
		// instrument's position directly from the account's actual current
		// position instead of guessing a sign from execution.MarketPosition -
		// that guess turned out to be wrong and sent a follower an unintended
		// position in production. See git history for the incident.
		private void UpdatePositionFromExecution(AccountState state, Execution execution)
		{
			if (execution.Order != null)
			{
				int signedDelta = (execution.Order.OrderAction == OrderAction.Buy || execution.Order.OrderAction == OrderAction.BuyToCover)
					? execution.Quantity
					: -execution.Quantity;
				AdjustPosition(state, execution.Instrument, signedDelta);
			}
			else
			{
				ResyncPositionFromAccount(state, execution.Instrument);
			}
		}

		// Must be called while holding engineLock. Ground truth from
		// Account.Positions (Position.MarketPosition, not the less certain
		// Execution.MarketPosition) rather than an incremental guess.
		private void ResyncPositionFromAccount(AccountState state, Instrument instrument)
		{
			int actualSignedQty = 0;
			foreach (Position position in state.NinjaAccount.Positions)
			{
				if (ReferenceEquals(position.Instrument, instrument))
				{
					actualSignedQty = position.MarketPosition == MarketPosition.Long ? position.Quantity
						: position.MarketPosition == MarketPosition.Short ? -position.Quantity
						: 0;
					break;
				}
			}

			Dictionary<Instrument, int> map = GetPositionMap(state);
			map[instrument] = actualSignedQty;

			int totalAbs = 0;
			foreach (int qty in map.Values)
				totalAbs += Math.Abs(qty);

			state.NetPositionQuantity = totalAbs;
			state.PositionDirection = actualSignedQty > 0 ? MarketPosition.Long
				: actualSignedQty < 0 ? MarketPosition.Short
				: MarketPosition.Flat;
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
