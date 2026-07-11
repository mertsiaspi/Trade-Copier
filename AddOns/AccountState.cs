using System;
using NinjaTrader.Cbi;

namespace NinjaTrader.NinjaScript.AddOns
{
	public enum AccountRole
	{
		Master,
		Follower
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
		public decimal TrailingDrawdownLimit { get; set; }
		public decimal HighWaterMark { get; set; }

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
