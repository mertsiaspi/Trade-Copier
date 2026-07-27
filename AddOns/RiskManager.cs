using System;
using NinjaTrader.Cbi;

namespace NinjaTrader.NinjaScript.AddOns
{
	public enum RiskBreachType
	{
		TrailingDrawdown,
		DailyRiskBudget,
		Manual // kill switch / operator-triggered flatten
	}

	public class RiskBreachEventArgs : EventArgs
	{
		public AccountState Account { get; }
		public RiskBreachType BreachType { get; }
		public string Reason { get; }

		public RiskBreachEventArgs(AccountState account, RiskBreachType breachType, string reason)
		{
			Account = account;
			BreachType = breachType;
			Reason = reason;
		}
	}

	// Decides when an account has to be flattened and locked. Deliberately
	// does NOT submit any orders itself - it only flips AccountState.IsLocked
	// and raises BreachDetected. The actual flatten order is sent by whoever
	// owns order submission (CopierEngine), so there is exactly one code path
	// in the whole project that calls Account.Submit.
	//
	// Threading note: Evaluate() mutates AccountState's drawdown fields
	// (HighWaterMark, DrawdownFloor, ...) without its own lock. It is only
	// safe to call Evaluate() for a given account from one thread at a time -
	// the caller (CopierEngine's timer/event dispatch) must serialize calls
	// per account.
	public class RiskManager
	{
		public event EventHandler<RiskBreachEventArgs> BreachDetected;

		// Call periodically (e.g. every 1-5s) or on account/position update
		// events for each tracked account. No-op if the account is already
		// locked - it stays locked until ResetForNewDay/RolloverToNewTradingDay.
		public void Evaluate(AccountState state)
		{
			if (state == null)
				throw new ArgumentNullException("state");

			if (state.IsLocked)
				return;

			UpdateDrawdownFloor(state);

			decimal equity = GetLiveEquity(state);

			if (state.MaxDrawdownAmount > 0 && equity <= state.EffectiveDrawdownFloor)
			{
				RaiseBreach(state, RiskBreachType.TrailingDrawdown,
					string.Format("{0}: equity {1:C} <= drawdown floor {2:C} (raw floor {3:C} + {4:C} safety buffer) ({5})",
						state.DisplayName, equity, state.EffectiveDrawdownFloor, state.DrawdownFloor,
						state.FloorSafetyBuffer, state.DrawdownType));
				return;
			}

			if (state.DailyRiskBudget > 0 && state.DailyPnL <= -state.DailyRiskBudget)
			{
				RaiseBreach(state, RiskBreachType.DailyRiskBudget,
					string.Format("{0}: daily PnL {1:C} <= -DRB {2:C}",
						state.DisplayName, state.DailyPnL, state.DailyRiskBudget));
			}
		}

		// Kill switch / manual "flatten this account now" entry point. Bypasses
		// the IsLocked check in Evaluate on purpose - a kill switch has to work
		// even if the account is already locked.
		public void ManualFlatten(AccountState state, string reason)
		{
			if (state == null)
				throw new ArgumentNullException("state");

			RaiseBreach(state, RiskBreachType.Manual, reason);
		}

		// Called once per new trading day for this account (exact rollover
		// trigger - session-end event vs. fixed clock time - still needs to be
		// confirmed per instrument/prop firm before this is wired up from
		// CopierEngine).
		public void RolloverToNewTradingDay(AccountState state, decimal closingBalance)
		{
			if (state == null)
				throw new ArgumentNullException("state");

			state.EndOfDayBalance = closingBalance;
			state.ResetForNewDay();
		}

		private void UpdateDrawdownFloor(AccountState state)
		{
			switch (state.DrawdownType)
			{
				case DrawdownType.IntradayTrailing:
					UpdateIntradayTrailingFloor(state);
					break;

				case DrawdownType.EndOfDay:
					// Floor is pinned to yesterday's close - does not move with
					// today's open P&L, per firm rule.
					state.DrawdownFloor = state.EndOfDayBalance - state.MaxDrawdownAmount;
					break;
			}
		}

		private void UpdateIntradayTrailingFloor(AccountState state)
		{
			decimal equity = GetLiveEquity(state);
			if (equity > state.HighWaterMark)
				state.HighWaterMark = equity;

			decimal freezeLevel = state.StartingBalance + state.TrailingStopFreezeOffset;
			decimal risingFloor = state.HighWaterMark - state.MaxDrawdownAmount;

			// TrailingStopFreezeOffset == 0 means "never freezes" for firms
			// without an Apex-style lock, so only cap the floor when an offset
			// is actually configured.
			if (state.TrailingStopFreezeOffset > 0 && risingFloor >= freezeLevel)
			{
				state.DrawdownFloor = freezeLevel;
				state.IsTrailingFrozen = true;
			}
			else
			{
				state.DrawdownFloor = risingFloor;
			}
		}

		// NOTE: this is the one call in this file to double-check in the
		// NinjaScript Editor. AccountItem.NetLiquidation / Currency.UsDollar
		// are the standard NT8 members for "cash + open P&L", but please
		// confirm on compile before this runs against a funded account.
		// Public so CopierWindow can show the same live equity figure on the
		// dashboard instead of re-implementing this call a second time.
		public decimal GetLiveEquity(AccountState state)
		{
			double netLiquidation = state.NinjaAccount.Get(AccountItem.NetLiquidation, Currency.UsDollar);
			return (decimal)netLiquidation;
		}

		private void RaiseBreach(AccountState state, RiskBreachType breachType, string reason)
		{
			state.Lock(reason);

			EventHandler<RiskBreachEventArgs> handler = BreachDetected;
			if (handler != null)
				handler(this, new RiskBreachEventArgs(state, breachType, reason));
		}
	}
}
