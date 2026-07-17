using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns;

// GUI classes go in NinjaTrader.Gui.NinjaScript, matching NinjaTrader's own
// AddOn window examples - the plain logic classes (AccountState, RiskManager,
// CopierEngine) stay in NinjaTrader.NinjaScript.AddOns.
namespace NinjaTrader.Gui.NinjaScript
{
	// Session-lifetime singleton: owns the actually-running RiskManager,
	// CopierEngine and the evaluation timer. Deliberately NOT owned by
	// CopierWindow - per CLAUDE.md's architecture decision, risk management
	// and copying must keep running independent of whether any dashboard
	// window happens to be open. CopierWindow only attaches to this as a
	// viewer/controller and detaches (never stops the engine) when closed.
	public static class CopierManager
	{
		private static DispatcherTimer timer;
		private static int reconciliationTickCounter;
		private const int ReconciliationEveryNTicks = 5; // ~5s at a 1s tick, per CLAUDE.md's suggested interval

		// NinjaTrader itself resets AccountItem.RealizedProfitLoss at
		// midnight Central Time (confirmed via NinjaTrader support forum),
		// so daily counters/locks/EOD-drawdown-baseline are rolled over on
		// the same boundary for consistency. If your prop firm's own "daily"
		// cutoff is defined differently, this needs to change.
		private static DateTime lastRolloverDate = GetCentralTimeDate();

		private static DateTime GetCentralTimeDate()
		{
			try
			{
				TimeZoneInfo centralZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
				return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, centralZone).Date;
			}
			catch (Exception)
			{
				return DateTime.UtcNow.Date;
			}
		}

		public static AccountState Master { get; private set; }
		public static List<AccountState> Followers { get; private set; }
		public static RiskManager RiskManager { get; private set; }
		public static CopierEngine Engine { get; private set; }
		public static bool IsRunning { get { return Engine != null; } }

		// Fired on every evaluation tick so open dashboard windows can refresh.
		public static event EventHandler Tick;

		public static void Start(AccountState master, IEnumerable<AccountState> followers)
		{
			if (IsRunning)
				throw new InvalidOperationException("Copier is already running - stop it first.");

			// Baseline "today" against the moment tracking actually starts,
			// not whenever this type happened to load - otherwise arming on
			// a later calendar day than that would immediately trigger a
			// spurious rollover and wipe the PnL CopierEngine.Start() just
			// seeded from the account.
			lastRolloverDate = GetCentralTimeDate();

			Master = master;
			Followers = new List<AccountState>(followers);
			RiskManager = new RiskManager();
			Engine = new CopierEngine(Master, Followers);

			RiskManager.BreachDetected += OnBreachDetected;
			Engine.Start();

			reconciliationTickCounter = 0;
			timer = new DispatcherTimer(DispatcherPriority.Normal, NinjaTrader.Core.Globals.RandomDispatcher);
			timer.Interval = TimeSpan.FromSeconds(1);
			timer.Tick += OnTimerTick;
			timer.Start();
		}

		public static void Stop()
		{
			if (!IsRunning)
				return;

			timer.Stop();
			timer.Tick -= OnTimerTick;
			timer = null;

			RiskManager.BreachDetected -= OnBreachDetected;
			Engine.Stop();

			Engine = null;
			RiskManager = null;
			Master = null;
			Followers = null;
		}

		private static void OnBreachDetected(object sender, RiskBreachEventArgs e)
		{
			Engine.FlattenAccount(e.Account, e.Reason);
		}

		private static void OnTimerTick(object sender, EventArgs e)
		{
			DateTime today = GetCentralTimeDate();
			if (today != lastRolloverDate)
			{
				lastRolloverDate = today;
				SafeRolloverAccount(Master);
				foreach (AccountState follower in Followers)
					SafeRolloverAccount(follower);
			}

			SafeEvaluateAccount(Master);
			foreach (AccountState follower in Followers)
				SafeEvaluateAccount(follower);

			reconciliationTickCounter++;
			if (reconciliationTickCounter >= ReconciliationEveryNTicks)
			{
				reconciliationTickCounter = 0;
				// RunReconciliation already isolates per-follower failures
				// internally and logs them - this only guards the call itself.
				try
				{
					Engine.RunReconciliation();
				}
				catch (Exception ex)
				{
					Engine.Log(LogSeverity.Error, "Reconciliation cycle failed - " + ex.Message);
				}
			}

			EventHandler handler = Tick;
			if (handler != null)
				handler(null, EventArgs.Empty);
		}

		// One account's failure here must never stop the rest from being
		// evaluated/rolled over - this is the same bug class reported and
		// fixed in CopierEngine's per-follower loops, applied here too since
		// this loop has the identical shape.
		private static void SafeEvaluateAccount(AccountState state)
		{
			try
			{
				RiskManager.Evaluate(state);
			}
			catch (Exception ex)
			{
				Engine.Log(LogSeverity.Error, string.Format("{0}: risk evaluation failed - {1}. Will retry next tick.", state.DisplayName, ex.Message));
			}
		}

		private static void SafeRolloverAccount(AccountState state)
		{
			try
			{
				RiskManager.RolloverToNewTradingDay(state, RiskManager.GetLiveEquity(state));
			}
			catch (Exception ex)
			{
				Engine.Log(LogSeverity.Error, string.Format("{0}: end-of-day rollover failed - {1}.", state.DisplayName, ex.Message));
			}
		}
	}

	// Thin shell that injects a "Trade Copier" menu item into the Control
	// Center's New menu - mirrors NinjaTrader's own documented AddOn pattern
	// (FindFirst("ControlCenterMenuItemNew"), NTMenuItem, clean removal on
	// window destroy).
	public class CopierAddOn : AddOnBase
	{
		private NTMenuItem existingMenuItem;
		private NTMenuItem copierMenuItem;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "NT8 Trade Copier + Risk Manager dashboard";
				Name = "Trade Copier";
			}
		}

		protected override void OnWindowCreated(Window window)
		{
			ControlCenter controlCenter = window as ControlCenter;
			if (controlCenter == null)
				return;

			existingMenuItem = controlCenter.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
			if (existingMenuItem == null)
				return;

			copierMenuItem = new NTMenuItem
			{
				Header = "Trade Copier",
				Style = Application.Current.TryFindResource("MainMenuItem") as Style
			};
			existingMenuItem.Items.Add(copierMenuItem);
			copierMenuItem.Click += OnMenuItemClick;
		}

		protected override void OnWindowDestroyed(Window window)
		{
			if (copierMenuItem != null && window is ControlCenter)
			{
				if (existingMenuItem != null && existingMenuItem.Items.Contains(copierMenuItem))
					existingMenuItem.Items.Remove(copierMenuItem);

				copierMenuItem.Click -= OnMenuItemClick;
				copierMenuItem = null;
			}
		}

		private void OnMenuItemClick(object sender, RoutedEventArgs e)
		{
			NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(new Action(() => new CopierWindow().Show()));
		}
	}

	// One row per connected account, shared by both dashboard tabs (Tab 1
	// shows a filtered view that excludes whichever account is master). All
	// property setters proxy straight onto the underlying AccountState so
	// edits take effect immediately - live-tunable multiplier/risk limits are
	// intentional, not an oversight. The one exception is starting balance,
	// which only applies (and reseeds the trailing high-water-mark) when the
	// user clicks Arm/Rearm - see CopierWindow.ApplyPendingStartingBalance.
	public class AccountRow : INotifyPropertyChanged
	{
		public event PropertyChangedEventHandler PropertyChanged;

		private void Raise(string name)
		{
			PropertyChangedEventHandler handler = PropertyChanged;
			if (handler != null)
				handler(this, new PropertyChangedEventArgs(name));
		}

		public AccountState State { get; }

		public AccountRow(AccountState state)
		{
			State = state;
			PendingStartingBalance = state.StartingBalance.ToString(CultureInfo.InvariantCulture);
		}

		public string Name { get { return State.DisplayName; } }
		public bool IsMaster { get { return State.Role == AccountRole.Master; } }

		// ----- Tab 1: Trade Copier -----

		private bool includeAsFollower;
		public bool IncludeAsFollower
		{
			get { return includeAsFollower; }
			set { includeAsFollower = value; Raise("IncludeAsFollower"); }
		}

		public string Multiplier
		{
			get { return State.QuantityMultiplier.ToString(CultureInfo.InvariantCulture); }
			set
			{
				decimal parsed;
				if (decimal.TryParse(value, out parsed))
					State.QuantityMultiplier = parsed;
				Raise("Multiplier");
			}
		}

		private string position = "-";
		public string Position { get { return position; } set { position = value; Raise("Position"); } }

		private string realizedPnl = "-";
		public string RealizedPnl { get { return realizedPnl; } set { realizedPnl = value; Raise("RealizedPnl"); } }

		private Brush realizedPnlBrush = Brushes.Gainsboro;
		public Brush RealizedPnlBrush { get { return realizedPnlBrush; } set { realizedPnlBrush = value; Raise("RealizedPnlBrush"); } }

		private string unrealizedPnl = "-";
		public string UnrealizedPnl { get { return unrealizedPnl; } set { unrealizedPnl = value; Raise("UnrealizedPnl"); } }

		private Brush unrealizedPnlBrush = Brushes.Gainsboro;
		public Brush UnrealizedPnlBrush { get { return unrealizedPnlBrush; } set { unrealizedPnlBrush = value; Raise("UnrealizedPnlBrush"); } }

		private string status = "Not armed";
		public string Status { get { return status; } set { status = value; Raise("Status"); } }

		private Brush statusBrush = Brushes.Gray;
		public Brush StatusBrush { get { return statusBrush; } set { statusBrush = value; Raise("StatusBrush"); } }

		// ----- Tab 2: Risk Monitor & Settings -----

		private string netLiquidation = "-";
		public string NetLiquidation { get { return netLiquidation; } set { netLiquidation = value; Raise("NetLiquidation"); } }

		private string autoLiquidateFloor = "-";
		public string AutoLiquidateFloor { get { return autoLiquidateFloor; } set { autoLiquidateFloor = value; Raise("AutoLiquidateFloor"); } }

		private string distanceToAuto = "-";
		public string DistanceToAuto { get { return distanceToAuto; } set { distanceToAuto = value; Raise("DistanceToAuto"); } }

		private Brush distanceToAutoBrush = Brushes.Gainsboro;
		public Brush DistanceToAutoBrush { get { return distanceToAutoBrush; } set { distanceToAutoBrush = value; Raise("DistanceToAutoBrush"); } }

		public string DailyRiskBudget
		{
			get { return State.DailyRiskBudget.ToString(CultureInfo.InvariantCulture); }
			set
			{
				decimal parsed;
				if (decimal.TryParse(value, out parsed))
					State.DailyRiskBudget = parsed;
				Raise("DailyRiskBudget");
			}
		}

		public string MaxDrawdown
		{
			get { return State.MaxDrawdownAmount.ToString(CultureInfo.InvariantCulture); }
			set
			{
				decimal parsed;
				if (decimal.TryParse(value, out parsed))
					State.MaxDrawdownAmount = parsed;
				Raise("MaxDrawdown");
			}
		}

		public DrawdownType DrawdownTypeValue
		{
			get { return State.DrawdownType; }
			set { State.DrawdownType = value; Raise("DrawdownTypeValue"); }
		}

		public string FreezeOffset
		{
			get { return State.TrailingStopFreezeOffset.ToString(CultureInfo.InvariantCulture); }
			set
			{
				decimal parsed;
				if (decimal.TryParse(value, out parsed))
					State.TrailingStopFreezeOffset = parsed;
				Raise("FreezeOffset");
			}
		}

		// Plain pending text, deliberately NOT proxied straight onto State -
		// see the class-level comment above.
		public string PendingStartingBalance { get; set; }
	}

	public class CopierWindow : NTWindow, IWorkspacePersistence
	{
		// ----- Hand-rolled dark theme -----
		// NOTE: deliberately not using Application.Current.FindResource(...)
		// with guessed NinjaTrader theme keys here - a wrong key throws at
		// runtime when the window opens (worse than just looking slightly
		// off), and I could not confirm exact key names for a plain
		// window/DataGrid background. These colors are hand-picked to match
		// NinjaTrader's own dark skin closely; tell me if they clash.
		private static readonly Brush ThemeBackground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
		private static readonly Brush ThemePanelBackground = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
		private static readonly Brush ThemeRowBackground = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
		private static readonly Brush ThemeRowAltBackground = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x28));
		private static readonly Brush ThemeHeaderBackground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x36));
		private static readonly Brush ThemeBorder = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
		private static readonly Brush ThemeForeground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));

		// Display-only heuristics for warning colors - these do NOT affect
		// actual risk enforcement, RiskManager decides that independently.
		private const decimal WarningThresholdFraction = 0.2m;
		private const decimal DistanceDangerDollars = 500m;
		private const decimal DistanceWarningDollars = 1000m;

		private readonly ObservableCollection<AccountRow> accountRows = new ObservableCollection<AccountRow>();
		private readonly ObservableCollection<string> logLines = new ObservableCollection<string>();
		private readonly CollectionViewSource followerView = new CollectionViewSource();

		private ComboBox masterCombo;
		private Button armButton;
		private TextBlock masterSummaryText;
		private Border masterStatusIndicator;
		private CopierEngine subscribedEngine;

		public CopierWindow()
		{
			Caption = "Trade Copier";
			Width = 1100;
			Height = 650;
			Background = ThemeBackground;

			followerView.Source = accountRows;
			followerView.Filter += (s, e) => { e.Accepted = !((AccountRow)e.Item).IsMaster; };

			TabControl tabControl = new TabControl { Background = ThemeBackground, BorderThickness = new Thickness(0) };
			tabControl.Items.Add(new TabItem { Header = "Trade Copier", Content = BuildTab1(), Background = ThemePanelBackground, Foreground = ThemeForeground });
			tabControl.Items.Add(new TabItem { Header = "Risk Monitor & Settings", Content = BuildTab2(), Background = ThemePanelBackground, Foreground = ThemeForeground });
			Content = tabControl;

			Closed += OnClosed;

			Loaded += (o, e) =>
			{
				if (WorkspaceOptions == null)
					WorkspaceOptions = new WorkspaceOptions("CopierWindow" + Guid.NewGuid().ToString("N"), this);

				RebuildAccountRows();
				RefreshAllRows();
				CopierManager.Tick += OnManagerTick;
			};
		}

		// IWorkspacePersistence members - this window has no tab control that
		// needs workspace state, so these are safe no-ops.
		public void Restore(XDocument document, XElement element) { }
		public void Save(XDocument document, XElement element) { }
		public WorkspaceOptions WorkspaceOptions { get; set; }

		private void OnClosed(object sender, EventArgs e)
		{
			// Detach only - CopierManager keeps running with the window
			// closed, per CLAUDE.md's "risk management must not be chart/
			// window bound".
			CopierManager.Tick -= OnManagerTick;
			if (subscribedEngine != null)
				subscribedEngine.LogMessage -= OnEngineLogMessage;
		}

		private void OnManagerTick(object sender, EventArgs e)
		{
			Dispatcher.BeginInvoke(new Action(RefreshAllRows));
		}

		private void OnEngineLogMessage(object sender, CopierLogEventArgs e)
		{
			Dispatcher.BeginInvoke(new Action(() => AppendLog(string.Format("[{0}] {1}", e.Severity, e.Message))));
		}

		private void AppendLog(string line)
		{
			logLines.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + line);
			while (logLines.Count > 500)
				logLines.RemoveAt(logLines.Count - 1);
		}

		// ----- Account list -----

		private static bool IsConnected(Account account)
		{
			try
			{
				return account.Connection != null && account.Connection.Status == ConnectionStatus.Connected;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private void RebuildAccountRows()
		{
			List<Account> connected = new List<Account>();
			lock (Account.All)
			{
				foreach (Account account in Account.All)
					if (IsConnected(account))
						connected.Add(account);
			}

			Dictionary<Account, AccountRow> existing = new Dictionary<Account, AccountRow>();
			foreach (AccountRow row in accountRows)
				existing[row.State.NinjaAccount] = row;

			Account previouslySelectedMaster = masterCombo.SelectedItem as Account;

			accountRows.Clear();
			foreach (Account account in connected)
			{
				AccountRow row;
				if (!existing.TryGetValue(account, out row))
					row = new AccountRow(new AccountState(account, AccountRole.Follower));
				accountRows.Add(row);
			}

			masterCombo.ItemsSource = connected;
			if (previouslySelectedMaster != null && connected.Contains(previouslySelectedMaster))
				masterCombo.SelectedItem = previouslySelectedMaster;
			else if (connected.Count > 0)
				masterCombo.SelectedIndex = 0;

			RecomputeRoles();
		}

		private void OnMasterComboSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			RecomputeRoles();
		}

		private void RecomputeRoles()
		{
			Account selected = masterCombo.SelectedItem as Account;
			foreach (AccountRow row in accountRows)
				row.State.Role = (selected != null && ReferenceEquals(row.State.NinjaAccount, selected))
					? AccountRole.Master
					: AccountRole.Follower;

			followerView.View.Refresh();
		}

		private AccountRow FindRow(Account account)
		{
			foreach (AccountRow row in accountRows)
				if (ReferenceEquals(row.State.NinjaAccount, account))
					return row;
			return null;
		}

		// ----- Arm / Rearm / Stop / Kill switch -----

		private void OnArmClick(object sender, RoutedEventArgs e)
		{
			Account selectedMaster = masterCombo.SelectedItem as Account;
			if (selectedMaster == null)
			{
				MessageBox.Show(this, "Choose a master account first.", "Trade Copier", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			AccountRow masterRow = FindRow(selectedMaster);
			List<AccountState> newFollowers = new List<AccountState>();
			foreach (AccountRow row in accountRows)
			{
				if (ReferenceEquals(row, masterRow))
					continue;
				if (row.IncludeAsFollower)
					newFollowers.Add(row.State);
			}

			if (newFollowers.Count == 0)
			{
				MessageBox.Show(this, "Check at least one follower account (the On column) first.", "Trade Copier", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			ApplyPendingStartingBalance(masterRow);
			foreach (AccountRow row in accountRows)
				if (!ReferenceEquals(row, masterRow) && row.IncludeAsFollower)
					ApplyPendingStartingBalance(row);

			if (CopierManager.IsRunning)
				CopierManager.Stop();

			masterRow.State.Role = AccountRole.Master;
			CopierManager.Start(masterRow.State, newFollowers);

			RefreshAllRows();
		}

		// Only reseeds the trailing high-water-mark/EOD baseline if the
		// starting balance field actually changed - otherwise a routine
		// rearm (e.g. just to add one more follower) would silently reset an
		// already-running account's trailing-drawdown progress.
		private static void ApplyPendingStartingBalance(AccountRow row)
		{
			decimal pending;
			if (decimal.TryParse(row.PendingStartingBalance, out pending) && pending != row.State.StartingBalance)
				row.State.InitializeBalances(pending);
		}

		private void OnStopCopierClick(object sender, RoutedEventArgs e)
		{
			if (!CopierManager.IsRunning)
				return;

			MessageBoxResult result = MessageBox.Show(this,
				"Stop the copier entirely? This does NOT flatten positions - use Kill Switch for that. " +
				"No further copying, stop-mirroring or risk monitoring will happen until re-armed.",
				"Stop Copier", MessageBoxButton.YesNo, MessageBoxImage.Warning);

			if (result != MessageBoxResult.Yes)
				return;

			CopierManager.Stop();
			RefreshAllRows();
		}

		private void OnKillSwitchClick(object sender, RoutedEventArgs e)
		{
			if (!CopierManager.IsRunning)
			{
				MessageBox.Show(this, "Nothing is armed yet.", "Trade Copier", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			MessageBoxResult result = MessageBox.Show(this,
				"Flatten and lock ALL accounts (master + every follower) right now?",
				"Kill Switch", MessageBoxButton.YesNo, MessageBoxImage.Warning);

			if (result != MessageBoxResult.Yes)
				return;

			CopierManager.RiskManager.ManualFlatten(CopierManager.Master, "Kill switch");
			foreach (AccountState follower in CopierManager.Followers)
				CopierManager.RiskManager.ManualFlatten(follower, "Kill switch");
		}

		private void OnRowFlattenClick(object sender, RoutedEventArgs e)
		{
			FrameworkElement element = sender as FrameworkElement;
			AccountRow row = element != null ? element.DataContext as AccountRow : null;
			if (row == null)
				return;

			bool tracked = CopierManager.IsRunning
				&& (ReferenceEquals(row.State, CopierManager.Master) || CopierManager.Followers.Contains(row.State));
			if (!tracked)
			{
				MessageBox.Show(this, "This account isn't armed yet.", "Trade Copier", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			MessageBoxResult result = MessageBox.Show(this,
				string.Format("Flatten {0} now? This does not lock the account.", row.Name),
				"Flatten Account", MessageBoxButton.YesNo, MessageBoxImage.Warning);

			if (result != MessageBoxResult.Yes)
				return;

			CopierManager.Engine.FlattenAccount(row.State, "Manual flatten (dashboard button)");
		}

		// ----- Tab 1: Trade Copier -----

		private FrameworkElement BuildTab1()
		{
			DockPanel root = new DockPanel { Background = ThemeBackground, Margin = new Thickness(8) };

			Border topBar = BuildTopBar();
			DockPanel.SetDock(topBar, Dock.Top);
			root.Children.Add(topBar);

			DataGrid grid = BuildThemedDataGrid();
			grid.ItemsSource = followerView.View;

			grid.Columns.Add(new DataGridCheckBoxColumn { Header = "On", Binding = new Binding("IncludeAsFollower") { Mode = BindingMode.TwoWay } });
			grid.Columns.Add(new DataGridTextColumn { Header = "Account", Binding = new Binding("Name"), IsReadOnly = true });

			DataGridTextColumn multiplierColumn = new DataGridTextColumn { Header = "Multiplier", Binding = new Binding("Multiplier") { Mode = BindingMode.TwoWay } };
			multiplierColumn.EditingElementStyle = BuildEditingTextBoxStyle();
			grid.Columns.Add(multiplierColumn);

			grid.Columns.Add(new DataGridTextColumn { Header = "Position", Binding = new Binding("Position"), IsReadOnly = true });
			grid.Columns.Add(BuildColoredTextColumn("Realized PnL", "RealizedPnl", "RealizedPnlBrush"));
			grid.Columns.Add(BuildColoredTextColumn("Unrealized PnL", "UnrealizedPnl", "UnrealizedPnlBrush"));
			grid.Columns.Add(BuildColoredTextColumn("Status", "Status", "StatusBrush"));
			grid.Columns.Add(BuildFlattenColumn());

			root.Children.Add(grid);
			return root;
		}

		private Border BuildTopBar()
		{
			Border border = new Border
			{
				Background = ThemePanelBackground,
				BorderBrush = ThemeBorder,
				BorderThickness = new Thickness(1),
				Padding = new Thickness(8),
				Margin = new Thickness(0, 0, 0, 8)
			};

			StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };

			row.Children.Add(new TextBlock { Text = "Master:", Foreground = ThemeForeground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });

			masterCombo = new ComboBox { DisplayMemberPath = "Name", Width = 160, Margin = new Thickness(0, 0, 12, 0) };
			masterCombo.SelectionChanged += OnMasterComboSelectionChanged;
			row.Children.Add(masterCombo);

			Button refreshButton = new Button { Content = "Refresh Accounts", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 12, 0) };
			refreshButton.Click += (s, e) => { RebuildAccountRows(); RefreshAllRows(); };
			row.Children.Add(refreshButton);

			armButton = new Button { Content = "Arm", Width = 90, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 12, 0) };
			armButton.Click += OnArmClick;
			row.Children.Add(armButton);

			masterStatusIndicator = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Brushes.Gray, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
			row.Children.Add(masterStatusIndicator);

			masterSummaryText = new TextBlock { Foreground = ThemeForeground, VerticalAlignment = VerticalAlignment.Center, Width = 460, TextTrimming = TextTrimming.CharacterEllipsis };
			row.Children.Add(masterSummaryText);

			Button killSwitch = new Button { Content = "KILL SWITCH - Flatten All", Background = Brushes.DarkRed, Foreground = Brushes.White, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(12, 0, 6, 0) };
			killSwitch.Click += OnKillSwitchClick;
			row.Children.Add(killSwitch);

			Button stopButton = new Button { Content = "Stop Copier", Padding = new Thickness(8, 4, 8, 4) };
			stopButton.Click += OnStopCopierClick;
			row.Children.Add(stopButton);

			border.Child = row;
			return border;
		}

		// ----- Tab 2: Risk Monitor & Settings -----

		private FrameworkElement BuildTab2()
		{
			DockPanel root = new DockPanel { Background = ThemeBackground, Margin = new Thickness(8) };

			DataGrid grid = BuildThemedDataGrid();
			grid.ItemsSource = accountRows;

			grid.Columns.Add(new DataGridTextColumn { Header = "Account", Binding = new Binding("Name"), IsReadOnly = true });
			grid.Columns.Add(new DataGridTextColumn { Header = "Net Liquidation", Binding = new Binding("NetLiquidation"), IsReadOnly = true });
			grid.Columns.Add(new DataGridTextColumn { Header = "Auto Liquidate", Binding = new Binding("AutoLiquidateFloor"), IsReadOnly = true });
			grid.Columns.Add(BuildColoredTextColumn("Distance to Auto", "DistanceToAuto", "DistanceToAutoBrush"));

			DataGridTextColumn drbColumn = new DataGridTextColumn { Header = "Daily Risk Budget", Binding = new Binding("DailyRiskBudget") { Mode = BindingMode.TwoWay } };
			drbColumn.EditingElementStyle = BuildEditingTextBoxStyle();
			grid.Columns.Add(drbColumn);

			DataGridTextColumn maxDdColumn = new DataGridTextColumn { Header = "Max Drawdown", Binding = new Binding("MaxDrawdown") { Mode = BindingMode.TwoWay } };
			maxDdColumn.EditingElementStyle = BuildEditingTextBoxStyle();
			grid.Columns.Add(maxDdColumn);

			grid.Columns.Add(new DataGridComboBoxColumn
			{
				Header = "Drawdown Type",
				ItemsSource = Enum.GetValues(typeof(DrawdownType)),
				SelectedItemBinding = new Binding("DrawdownTypeValue") { Mode = BindingMode.TwoWay }
			});

			// Starting Balance isn't in the original column list but is
			// required for the drawdown floor math (freeze level = starting
			// balance + freeze offset) - added so the Risk Monitor tab can
			// actually configure a working account instead of leaving this
			// unreachable.
			DataGridTextColumn startBalColumn = new DataGridTextColumn { Header = "Starting Balance", Binding = new Binding("PendingStartingBalance") { Mode = BindingMode.TwoWay } };
			startBalColumn.EditingElementStyle = BuildEditingTextBoxStyle();
			grid.Columns.Add(startBalColumn);

			DataGridTextColumn freezeColumn = new DataGridTextColumn { Header = "Freeze Offset", Binding = new Binding("FreezeOffset") { Mode = BindingMode.TwoWay } };
			freezeColumn.EditingElementStyle = BuildEditingTextBoxStyle();
			grid.Columns.Add(freezeColumn);

			root.Children.Add(grid);
			return root;
		}

		// ----- Themed WPF building blocks -----

		private DataGrid BuildThemedDataGrid()
		{
			DataGrid grid = new DataGrid
			{
				AutoGenerateColumns = false,
				CanUserAddRows = false,
				HeadersVisibility = DataGridHeadersVisibility.Column,
				GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
				Background = ThemeBackground,
				Foreground = ThemeForeground,
				RowBackground = ThemeRowBackground,
				AlternatingRowBackground = ThemeRowAltBackground,
				BorderBrush = ThemeBorder,
				BorderThickness = new Thickness(1),
				HorizontalGridLinesBrush = ThemeBorder,
				VerticalGridLinesBrush = ThemeBorder,
				RowHeaderWidth = 0
			};

			Style cellStyle = new Style(typeof(DataGridCell));
			cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
			cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, ThemeForeground));
			cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
			cellStyle.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
			grid.CellStyle = cellStyle;

			Style headerStyle = new Style(typeof(DataGridColumnHeader));
			headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, ThemeHeaderBackground));
			headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, ThemeForeground));
			headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
			headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBorder));
			grid.ColumnHeaderStyle = headerStyle;

			Style rowStyle = new Style(typeof(DataGridRow));
			rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
			grid.RowStyle = rowStyle;

			return grid;
		}

		private static Style BuildEditingTextBoxStyle()
		{
			Style style = new Style(typeof(TextBox));
			style.Setters.Add(new Setter(Control.BackgroundProperty, ThemeRowBackground));
			style.Setters.Add(new Setter(Control.ForegroundProperty, ThemeForeground));
			style.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBorder));
			return style;
		}

		private static DataGridTemplateColumn BuildColoredTextColumn(string header, string textPath, string brushPath)
		{
			FrameworkElementFactory textFactory = new FrameworkElementFactory(typeof(TextBlock));
			textFactory.SetBinding(TextBlock.TextProperty, new Binding(textPath));
			textFactory.SetBinding(TextBlock.ForegroundProperty, new Binding(brushPath));
			textFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 6, 0));
			textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

			return new DataGridTemplateColumn { Header = header, CellTemplate = new DataTemplate { VisualTree = textFactory } };
		}

		private DataGridTemplateColumn BuildFlattenColumn()
		{
			FrameworkElementFactory buttonFactory = new FrameworkElementFactory(typeof(Button));
			buttonFactory.SetValue(ContentControl.ContentProperty, "Flatten");
			buttonFactory.SetValue(Control.PaddingProperty, new Thickness(6, 2, 6, 2));
			buttonFactory.AddHandler(Button.ClickEvent, (RoutedEventHandler)OnRowFlattenClick);

			return new DataGridTemplateColumn { Header = "Flatten", CellTemplate = new DataTemplate { VisualTree = buttonFactory } };
		}

		// ----- Refresh -----

		private void RefreshAllRows()
		{
			SyncLogSubscription();
			UpdateTopBar();

			foreach (AccountRow row in accountRows)
				RefreshRow(row);
		}

		private void SyncLogSubscription()
		{
			if (ReferenceEquals(subscribedEngine, CopierManager.Engine))
				return;

			if (subscribedEngine != null)
				subscribedEngine.LogMessage -= OnEngineLogMessage;

			subscribedEngine = CopierManager.Engine;

			if (subscribedEngine != null)
				subscribedEngine.LogMessage += OnEngineLogMessage;
		}

		private void UpdateTopBar()
		{
			armButton.Content = CopierManager.IsRunning ? "Rearm" : "Arm";

			if (!CopierManager.IsRunning)
			{
				masterSummaryText.Text = "Not armed yet - choose a master, check followers on the On column, click Arm.";
				masterStatusIndicator.Background = Brushes.Gray;
				return;
			}

			AccountState masterState = CopierManager.Master;
			decimal equity = CopierManager.RiskManager.GetLiveEquity(masterState);

			masterSummaryText.Text = string.Format("{0}  |  {1}  |  Realized {2:C}  Unrealized {3:C}",
				masterState.DisplayName, CopierManager.Engine.DescribePosition(masterState),
				masterState.DailyRealizedPnL, masterState.DailyUnrealizedPnL);

			if (masterState.IsLocked)
				masterStatusIndicator.Background = Brushes.OrangeRed;
			else if (IsNearLimit(masterState, equity))
				masterStatusIndicator.Background = Brushes.Gold;
			else
				masterStatusIndicator.Background = Brushes.LimeGreen;
		}

		private void RefreshRow(AccountRow row)
		{
			AccountState state = row.State;
			bool tracked = CopierManager.IsRunning
				&& (ReferenceEquals(state, CopierManager.Master) || CopierManager.Followers.Contains(state));

			if (!tracked)
			{
				row.Position = "-";
				row.RealizedPnl = "-";
				row.RealizedPnlBrush = ThemeForeground;
				row.UnrealizedPnl = "-";
				row.UnrealizedPnlBrush = ThemeForeground;
				row.Status = "Not armed";
				row.StatusBrush = Brushes.Gray;
				row.NetLiquidation = "-";
				row.AutoLiquidateFloor = "-";
				row.DistanceToAuto = "-";
				row.DistanceToAutoBrush = ThemeForeground;
				return;
			}

			decimal equity = CopierManager.RiskManager.GetLiveEquity(state);

			row.Position = CopierManager.Engine.DescribePosition(state);
			row.RealizedPnl = state.DailyRealizedPnL.ToString("C");
			row.RealizedPnlBrush = PnlBrush(state.DailyRealizedPnL);
			row.UnrealizedPnl = state.DailyUnrealizedPnL.ToString("C");
			row.UnrealizedPnlBrush = PnlBrush(state.DailyUnrealizedPnL);

			row.NetLiquidation = equity.ToString("C");

			if (state.MaxDrawdownAmount > 0)
			{
				row.AutoLiquidateFloor = state.DrawdownFloor.ToString("C");
				decimal distance = equity - state.DrawdownFloor;
				row.DistanceToAuto = distance.ToString("C");
				row.DistanceToAutoBrush = DistanceBrush(distance);
			}
			else
			{
				row.AutoLiquidateFloor = "off";
				row.DistanceToAuto = "off";
				row.DistanceToAutoBrush = ThemeForeground;
			}

			if (state.IsLocked)
			{
				row.Status = "LOCKED: " + state.LockReason;
				row.StatusBrush = Brushes.OrangeRed;
			}
			else if (IsNearLimit(state, equity))
			{
				row.Status = "Warning";
				row.StatusBrush = Brushes.Gold;
			}
			else
			{
				row.Status = "OK";
				row.StatusBrush = Brushes.LimeGreen;
			}
		}

		private static Brush PnlBrush(decimal value)
		{
			if (value > 0) return Brushes.LimeGreen;
			if (value < 0) return Brushes.Red;
			return ThemeForeground;
		}

		private static Brush DistanceBrush(decimal distance)
		{
			if (distance < DistanceDangerDollars) return Brushes.Red;
			if (distance < DistanceWarningDollars) return Brushes.Orange;
			return Brushes.LimeGreen;
		}

		private static bool IsNearLimit(AccountState state, decimal equity)
		{
			if (state.MaxDrawdownAmount > 0 && (equity - state.DrawdownFloor) <= state.MaxDrawdownAmount * WarningThresholdFraction)
				return true;

			if (state.DailyRiskBudget > 0)
			{
				decimal used = state.DailyPnL < 0 ? -state.DailyPnL : 0m;
				if (used >= state.DailyRiskBudget * (1 - WarningThresholdFraction))
					return true;
			}

			return false;
		}
	}
}
