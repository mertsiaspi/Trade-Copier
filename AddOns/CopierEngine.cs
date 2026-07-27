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

		// How long a tracked in-flight order is allowed to sit unresolved
		// before the sync gate it's holding closed gets force-cleared. Every
		// order NT8 accepts is expected to eventually reach a terminal state
		// on its own - this is only a backstop for a dropped/missed event
		// (e.g. a disconnected follower account) that would otherwise leave
		// a follower+instrument permanently un-synced with no further
		// attempts and no obvious symptom beyond silence.
		private static readonly TimeSpan PendingOrderStaleThreshold = TimeSpan.FromSeconds(60);

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

		// The sync/reconcile order (if any) this engine is currently waiting
		// to resolve, per follower+instrument. GetPosition() cannot reflect a
		// fill NT8 hasn't confirmed yet, so the target-based math in
		// SyncFollowerPositions/RunReconciliation is only trustworthy between
		// fills, not while one of our own orders is still working. Without
		// this gate, two master executions arriving close together (e.g. a
		// close filling as two partial fills a few ms apart) each recompute
		// delta from the same stale followerQty and both send a correction -
		// the second one on top of the first, not instead of it. Cleared the
		// moment OnFollowerOrderUpdate sees this exact order reach a terminal
		// state, which also re-runs the sync so a still-outstanding delta
		// isn't stuck waiting for the next master execution.
		private readonly Dictionary<AccountState, Dictionary<Instrument, PendingSyncOrder>> pendingSyncOrderByInstrument =
			new Dictionary<AccountState, Dictionary<Instrument, PendingSyncOrder>>();

		// Pairs the tracked order with when it was submitted, so a stuck one
		// (see PendingOrderStaleThreshold) can be detected and force-cleared
		// instead of gating a follower+instrument shut forever.
		private class PendingSyncOrder
		{
			public Order Order;
			public DateTime SubmittedAtUtc;
		}

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
				pendingSyncOrderByInstrument.Clear();
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
						PurgeStalePendingSyncOrders(follower);

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

							// Same in-flight gate as SyncFollowerPositions: keep
							// warning every cycle (above) even while a correction
							// is already working, but never send a second order
							// on top of one whose fill hasn't come back yet.
							PendingSyncOrder pendingReconcileOrder;
							if (GetPendingSyncMap(follower).TryGetValue(instrument, out pendingReconcileOrder) && pendingReconcileOrder != null)
								continue;

							// NOTE: uses the plain Buy/Sell pair, not SellShort/BuyToCover -
							// verify this is the right action pair for how your follower
							// accounts are configured (futures accounts are usually fine
							// with Buy/Sell for both opening and closing).
							OrderAction action = delta > 0 ? OrderAction.Buy : OrderAction.Sell;
							Order sentReconcileOrder = SubmitOrder(follower, instrument, action, OrderType.Market, Math.Abs(delta), 0, 0,
								"Reconcile-" + follower.DisplayName);

							if (sentReconcileOrder != null)
							{
								GetPendingSyncMap(follower)[instrument] = new PendingSyncOrder
								{
									Order = sentReconcileOrder,
									SubmittedAtUtc = DateTime.UtcNow
								};
							}
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

			bool clearedPendingSync;
			lock (engineLock)
			{
				if (!TryMarkExecutionProcessed(execution.ExecutionId))
					return;

				clearedPendingSync = UpdatePositionFromExecution(follower, execution);
			}

			// This exact fill just resolved the sync/reconcile order (or the
			// cancelled mirrored stop - see SyncFollowerPositions) this
			// follower+instrument was gated on. AdjustPosition and the gate
			// clear happened together, under the same lock, in
			// UpdatePositionFromExecution, so GetPosition() is guaranteed
			// caught up for this fill specifically - safe to immediately
			// check whether a delta was left waiting behind it, rather than
			// only ever picking it up on the next master execution.
			if (clearedPendingSync)
				SyncFollowerPositions(execution.Instrument);

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

			if (!IsOrderTerminal(order.OrderState))
				return;

			// A terminal state here means one of two gates this engine holds
			// open elsewhere may now be clear: (a) a sync/reconcile order this
			// follower+instrument was waiting on (see pendingSyncOrderByInstrument),
			// or (b) a mirrored stop SyncFollowerPositions cancelled and
			// deferred a flatten behind.
			bool wasPendingSync;
			bool wasTrackedStop;
			lock (engineLock)
			{
				Dictionary<Instrument, PendingSyncOrder> pendingMap = GetPendingSyncMap(follower);
				PendingSyncOrder pendingOrder;
				wasPendingSync = pendingMap.TryGetValue(order.Instrument, out pendingOrder) && ReferenceEquals(pendingOrder.Order, order);
				if (wasPendingSync)
					pendingMap.Remove(order.Instrument);

				Dictionary<Instrument, Order> stopMap = GetFollowerStopMap(follower);
				Order trackedStop;
				wasTrackedStop = stopMap.TryGetValue(order.Instrument, out trackedStop) && ReferenceEquals(trackedStop, order);
				if (wasTrackedStop)
					stopMap.Remove(order.Instrument);
			}

			// Filled specifically is deliberately NOT retriggered from here -
			// OnFollowerExecutionUpdate/ClearPendingSyncOrderIfMatching is
			// what does that (see there), because NT8 documents no ordering
			// guarantee between this OrderUpdate event and the
			// ExecutionUpdate event for the same fill. Re-deriving position
			// from Account.Positions here and then letting a not-yet-arrived
			// ExecutionUpdate for this exact fill ALSO apply its own
			// incremental delta afterward would double-count that one fill
			// (this was caught before it shipped - see git history).
			// Cancelled/Rejected have no execution coming at all - whatever
			// partial fills happened were already applied incrementally by
			// the normal execution path before the cancel/reject, so
			// GetPosition() is already correct and retriggering immediately
			// here is safe - and is in fact the only place that ever will
			// for those two states.
			if ((wasPendingSync || wasTrackedStop) && order.OrderState != OrderState.Filled)
				SyncFollowerPositions(order.Instrument);
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
						// A sync/reconcile order from an earlier call may still
						// be working - GetPosition() cannot yet reflect a fill
						// NT8 hasn't confirmed, so recomputing delta now would
						// double-submit on top of it instead of replacing it.
						// OnFollowerOrderUpdate/OnFollowerExecutionUpdate clear
						// this and re-run the sync themselves the moment the
						// order resolves either way.
						PendingSyncOrder pendingOrder;
						if (GetPendingSyncMap(follower).TryGetValue(instrument, out pendingOrder) && pendingOrder != null)
						{
							RaiseLog(LogSeverity.Info, string.Format(
								"{0}: sync on {1} deferred - previous sync order still in flight.",
								follower.DisplayName, instrument.FullName));
							continue;
						}

						int masterQty = GetPosition(master, instrument);
						int followerQty = GetPosition(follower, instrument);
						int targetQty = (int)Math.Round(masterQty * follower.QuantityMultiplier, MidpointRounding.AwayFromZero);
						int delta = targetQty - followerQty;

						if (delta == 0)
							continue;

						// Master going flat means the follower's own mirrored
						// stop (if any) is about to become meaningless. Firing
						// the market flatten below WHILE that stop is still
						// working races it - if the stop fills too, the
						// follower flips to the opposite side instead of
						// landing flat (this happened in production). Cancel
						// the stop and defer the flatten entirely rather than
						// guessing who wins the race: OnFollowerOrderUpdate
						// re-runs this sync once the cancel (or a beat-us-to-it
						// fill) confirms, by which point GetPosition is ground
						// truth again and the remaining delta, if any, is safe
						// to send.
						if (targetQty == 0)
						{
							Dictionary<Instrument, Order> followerStops = GetFollowerStopMap(follower);
							Order existingStop;
							if (followerStops.TryGetValue(instrument, out existingStop) && existingStop != null
								&& !IsOrderTerminal(existingStop.OrderState))
							{
								followerStops.Remove(instrument);
								CancelOrder(follower, existingStop, "master flat - cancelling mirrored stop before flattening follower");

								// Track the stop itself as what this follower+
								// instrument is waiting on - reuses the exact
								// same in-flight gate the market sync order
								// below uses (whichever order is tracked here,
								// OnFollowerOrderUpdate/ClearPendingSyncOrderIfMatching
								// clear it by reference on its own terminal
								// state, not by caring which method sent it).
								// That is what fires the actual flatten once
								// the cancel - or a beat-us-to-it fill - confirms,
								// and what stops a second trigger in the
								// meantime from cancelling the same stop twice.
								GetPendingSyncMap(follower)[instrument] = new PendingSyncOrder
								{
									Order = existingStop,
									SubmittedAtUtc = DateTime.UtcNow
								};

								RaiseLog(LogSeverity.Info, string.Format(
									"{0}: flatten on {1} deferred until mirrored stop cancel/fill confirms - avoids racing both closes at once.",
									follower.DisplayName, instrument.FullName));
								continue;
							}
						}

						// MaxContracts is treated as a total-across-all-instruments
						// cap on this account, matching NetPositionQuantity's
						// rollup. Only ever clips a move that grows the position's
						// magnitude - closing/reducing must always go through in
						// full.
						bool isIncreasing = Math.Abs(followerQty + delta) > Math.Abs(followerQty);
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
						Order sentOrder = SubmitOrder(follower, instrument, action, OrderType.Market, Math.Abs(delta), 0, 0,
							"Copier-" + follower.DisplayName + "-" + instrument.FullName);

						if (sentOrder != null)
						{
							GetPendingSyncMap(follower)[instrument] = new PendingSyncOrder
							{
								Order = sentOrder,
								SubmittedAtUtc = DateTime.UtcNow
							};
						}
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
							&& !IsOrderTerminal(existingStop.OrderState)
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
		// Returns whether this execution just cleared this follower+
		// instrument's in-flight sync gate - see ClearPendingSyncOrderIfMatching.
		// Callers use this to decide whether to retrigger SyncFollowerPositions.
		private bool UpdatePositionFromExecution(AccountState state, Execution execution)
		{
			if (execution.Order != null)
			{
				int signedDelta = (execution.Order.OrderAction == OrderAction.Buy || execution.Order.OrderAction == OrderAction.BuyToCover)
					? execution.Quantity
					: -execution.Quantity;
				AdjustPosition(state, execution.Instrument, signedDelta);
				return ClearPendingSyncOrderIfMatching(state, execution.Instrument, execution.Order);
			}

			ResyncPositionFromAccount(state, execution.Instrument);
			return false;
		}

		// Must be called while holding engineLock. Opens the in-flight gate
		// back up as soon as a real fill for the order it was waiting on has
		// been applied to position - deliberately in the same lock
		// acquisition as AdjustPosition above, not from the separate
		// OrderUpdate event, so there is no window where the gate is clear
		// but GetPosition() still hasn't caught up. Returns whether this
		// order was actually the tracked one (i.e. whether the gate just
		// opened), so the caller knows whether re-checking for a leftover
		// delta is warranted.
		private bool ClearPendingSyncOrderIfMatching(AccountState state, Instrument instrument, Order order)
		{
			Dictionary<Instrument, PendingSyncOrder> pendingMap = GetPendingSyncMap(state);
			PendingSyncOrder pendingOrder;
			if (pendingMap.TryGetValue(instrument, out pendingOrder) && ReferenceEquals(pendingOrder.Order, order))
			{
				pendingMap.Remove(instrument);
				return true;
			}
			return false;
		}

		// Must be called while holding engineLock. Backstop for an in-flight
		// order that never reached a terminal state we observed - a dropped
		// event, a disconnected follower account, etc. Without this, a
		// follower+instrument stuck behind PendingOrderStaleThreshold would
		// stay silently un-synced indefinitely, with no further attempts and
		// no obvious symptom beyond an old "still in flight" log line.
		// Force-clearing does not touch the actual order on the broker side
		// - only what this engine is waiting on - so the next reconciliation
		// pass recomputes fresh from current GetPosition(), same as any
		// other cycle.
		private void PurgeStalePendingSyncOrders(AccountState follower)
		{
			Dictionary<Instrument, PendingSyncOrder> pendingMap = GetPendingSyncMap(follower);
			if (pendingMap.Count == 0)
				return;

			List<Instrument> stale = null;
			DateTime now = DateTime.UtcNow;
			foreach (KeyValuePair<Instrument, PendingSyncOrder> entry in pendingMap)
			{
				if (now - entry.Value.SubmittedAtUtc > PendingOrderStaleThreshold)
				{
					if (stale == null)
						stale = new List<Instrument>();
					stale.Add(entry.Key);
				}
			}

			if (stale == null)
				return;

			foreach (Instrument instrument in stale)
			{
				pendingMap.Remove(instrument);
				RaiseLog(LogSeverity.Warning, string.Format(
					"{0}: sync gate on {1} was stuck waiting on an order for over {2}s with no resolving event - clearing it so " +
					"reconciliation can recompute. This should not happen in normal operation - check whether that order is still " +
					"actually working on the broker/account.",
					follower.DisplayName, instrument.FullName, (int)PendingOrderStaleThreshold.TotalSeconds));
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

		private Dictionary<Instrument, PendingSyncOrder> GetPendingSyncMap(AccountState state)
		{
			Dictionary<Instrument, PendingSyncOrder> map;
			if (!pendingSyncOrderByInstrument.TryGetValue(state, out map))
			{
				map = new Dictionary<Instrument, PendingSyncOrder>();
				pendingSyncOrderByInstrument[state] = map;
			}
			return map;
		}

		// Matches the "still alive" check SyncFollowerStops already relied on
		// (existingStop.OrderState != Cancelled/Rejected/Filled) - factored
		// out since the in-flight sync gate needs the identical check.
		private static bool IsOrderTerminal(OrderState state)
		{
			return state == OrderState.Filled || state == OrderState.Cancelled || state == OrderState.Rejected;
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
