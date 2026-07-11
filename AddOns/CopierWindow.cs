using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
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

		public static AccountState Master { get; private set; }
		public static List<AccountState> Followers { get; private set; }
		public static RiskManager RiskManager { get; private set; }
		public static CopierEngine Engine { get; private set; }
		public static bool IsRunning { get { return Engine != null; } }

		// Fired on every evaluation tick so open dashboard windows can refresh.
		public static event EventHandler Tick;
		public static event EventHandler Started;
		public static event EventHandler Stopped;

		public static void Start(AccountState master, IEnumerable<AccountState> followers)
		{
			if (IsRunning)
				throw new InvalidOperationException("Copier is already running - stop it first.");

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

			RaiseEvent(Started);
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

			RaiseEvent(Stopped);
		}

		private static void OnBreachDetected(object sender, RiskBreachEventArgs e)
		{
			Engine.FlattenAccount(e.Account, e.Reason);
		}

		private static void OnTimerTick(object sender, EventArgs e)
		{
			RiskManager.Evaluate(Master);
			foreach (AccountState follower in Followers)
				RiskManager.Evaluate(follower);

			reconciliationTickCounter++;
			if (reconciliationTickCounter >= ReconciliationEveryNTicks)
			{
				reconciliationTickCounter = 0;
				Engine.RunReconciliation();
			}

			RaiseEvent(Tick);
		}

		private static void RaiseEvent(EventHandler handler)
		{
			if (handler != null)
				handler(null, EventArgs.Empty);
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

	// One row bound into the live follower DataGrid. Plain properties raise
	// PropertyChanged so the grid updates on each CopierManager.Tick without
	// touching the grid itself.
	public class FollowerRow : INotifyPropertyChanged
	{
		public event PropertyChangedEventHandler PropertyChanged;

		private void Raise(string name)
		{
			PropertyChangedEventHandler handler = PropertyChanged;
			if (handler != null)
				handler(this, new PropertyChangedEventArgs(name));
		}

		public AccountState State { get; }

		public FollowerRow(AccountState state)
		{
			State = state;
		}

		public string Name { get { return State.DisplayName; } }

		private string connection = "Unknown";
		public string Connection { get { return connection; } set { connection = value; Raise("Connection"); } }

		private string position = "Flat";
		public string Position { get { return position; } set { position = value; Raise("Position"); } }

		private string realizedPnl = "";
		public string RealizedPnl { get { return realizedPnl; } set { realizedPnl = value; Raise("RealizedPnl"); } }

		private string unrealizedPnl = "";
		public string UnrealizedPnl { get { return unrealizedPnl; } set { unrealizedPnl = value; Raise("UnrealizedPnl"); } }

		private string drb = "off";
		public string Drb { get { return drb; } set { drb = value; Raise("Drb"); } }

		private string trailingDd = "off";
		public string TrailingDd { get { return trailingDd; } set { trailingDd = value; Raise("TrailingDd"); } }

		private string stats = "0/0/0";
		public string Stats { get { return stats; } set { stats = value; Raise("Stats"); } }

		private string status = "OK";
		public string Status { get { return status; } set { status = value; Raise("Status"); } }

		private Brush statusBrush = Brushes.Gray;
		public Brush StatusBrush { get { return statusBrush; } set { statusBrush = value; Raise("StatusBrush"); } }
	}

	public class CopierWindow : NTWindow, IWorkspacePersistence
	{
		// Display-only heuristic for the dashboard's "warning" color - does
		// NOT affect actual risk enforcement, RiskManager decides that
		// independently. Warns once 80% of a configured budget is used.
		private const decimal WarningThresholdFraction = 0.2m;

		private class FollowerSetupRow
		{
			public Account Account;
			public CheckBox EnabledCheckBox;
			public TextBox MultiplierBox;
			public TextBox MaxContractsBox;
			public TextBox DailyRiskBudgetBox;
			public TextBox MaxDrawdownBox;
			public ComboBox DrawdownTypeCombo;
			public TextBox StartingBalanceBox;
			public TextBox FreezeOffsetBox;
		}

		private readonly Grid rootGrid = new Grid();
		private readonly List<FollowerSetupRow> followerSetupRows = new List<FollowerSetupRow>();
		private readonly ObservableCollection<FollowerRow> followerRows = new ObservableCollection<FollowerRow>();
		private readonly ObservableCollection<string> logLines = new ObservableCollection<string>();

		private ComboBox masterCombo;
		private FrameworkElement setupPanel;
		private FrameworkElement dashboardPanel;

		private TextBlock masterNameText;
		private TextBlock masterConnectionText;
		private TextBlock masterPositionText;
		private TextBlock masterPnlText;
		private TextBlock masterStatusText;
		private Border masterStatusIndicator;

		public CopierWindow()
		{
			Caption = "Trade Copier";
			Width = 900;
			Height = 600;

			Content = rootGrid;

			setupPanel = BuildSetupPanel();
			dashboardPanel = BuildDashboardPanel();
			rootGrid.Children.Add(setupPanel);
			rootGrid.Children.Add(dashboardPanel);

			Closed += OnClosed;

			Loaded += (o, e) =>
			{
				if (WorkspaceOptions == null)
					WorkspaceOptions = new WorkspaceOptions("CopierWindow" + Guid.NewGuid().ToString("N"), this);

				AttachOrShowSetup();
			};
		}

		// IWorkspacePersistence members - this window has no tab control, so
		// these are safe no-ops.
		public void Restore(XDocument document, XElement element) { }
		public void Save(XDocument document, XElement element) { }
		public WorkspaceOptions WorkspaceOptions { get; set; }

		private void AttachOrShowSetup()
		{
			if (CopierManager.IsRunning)
				AttachToRunningCopier();
			else
				ShowSetupScreen();

			CopierManager.Started += OnManagerStarted;
			CopierManager.Tick += OnManagerTick;
		}

		private void OnClosed(object sender, EventArgs e)
		{
			// Detach only - CopierManager keeps running with the window closed,
			// per CLAUDE.md's "risk management must not be chart/window bound".
			CopierManager.Started -= OnManagerStarted;
			CopierManager.Tick -= OnManagerTick;

			if (CopierManager.Engine != null)
				CopierManager.Engine.LogMessage -= OnEngineLogMessage;
		}

		private void OnManagerStarted(object sender, EventArgs e)
		{
			AttachToRunningCopier();
		}

		private void AttachToRunningCopier()
		{
			followerRows.Clear();
			foreach (AccountState follower in CopierManager.Followers)
				followerRows.Add(new FollowerRow(follower));

			CopierManager.Engine.LogMessage += OnEngineLogMessage;

			setupPanel.Visibility = Visibility.Collapsed;
			dashboardPanel.Visibility = Visibility.Visible;

			RefreshDashboard();
		}

		private void OnManagerTick(object sender, EventArgs e)
		{
			Dispatcher.BeginInvoke(new Action(RefreshDashboard));
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

		// ----- Setup screen -----

		private FrameworkElement BuildSetupPanel()
		{
			StackPanel panel = new StackPanel { Margin = new Thickness(12) };

			panel.Children.Add(new TextBlock
			{
				Text = "Master account",
				FontWeight = FontWeights.Bold,
				Margin = new Thickness(0, 0, 0, 4)
			});

			List<Account> allAccounts = new List<Account>();
			lock (Account.All)
			{
				allAccounts.AddRange(Account.All);
			}

			masterCombo = new ComboBox { ItemsSource = allAccounts, DisplayMemberPath = "Name", Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
			panel.Children.Add(masterCombo);

			panel.Children.Add(new TextBlock
			{
				Text = "Follower accounts",
				FontWeight = FontWeights.Bold,
				Margin = new Thickness(0, 16, 0, 4)
			});

			StackPanel headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
			foreach (string header in new[] { "On", "Account", "Multiplier", "MaxContracts", "DailyRiskBudget", "MaxDrawdown", "DrawdownType", "StartBalance", "FreezeOffset" })
				headerRow.Children.Add(new TextBlock { Text = header, Width = 95, FontWeight = FontWeights.Bold });
			panel.Children.Add(headerRow);

			foreach (Account account in allAccounts)
				panel.Children.Add(BuildFollowerSetupRow(account));

			Button armButton = new Button { Content = "Arm", Width = 120, Height = 30, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
			armButton.Click += OnArmClick;
			panel.Children.Add(armButton);

			panel.Children.Add(new TextBlock
			{
				Text = "Leave a limit field empty/0 to disable that check for that account. Master's own risk limits aren't configurable here yet.",
				Margin = new Thickness(0, 8, 0, 0),
				Opacity = 0.7,
				TextWrapping = TextWrapping.Wrap
			});

			return panel;
		}

		private StackPanel BuildFollowerSetupRow(Account account)
		{
			FollowerSetupRow row = new FollowerSetupRow { Account = account };

			StackPanel rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

			row.EnabledCheckBox = new CheckBox { Width = 95, VerticalAlignment = VerticalAlignment.Center };
			rowPanel.Children.Add(row.EnabledCheckBox);

			rowPanel.Children.Add(new TextBlock { Text = account.Name, Width = 95, VerticalAlignment = VerticalAlignment.Center });

			row.MultiplierBox = new TextBox { Text = "1", Width = 95 };
			rowPanel.Children.Add(row.MultiplierBox);

			row.MaxContractsBox = new TextBox { Text = "", Width = 95 };
			rowPanel.Children.Add(row.MaxContractsBox);

			row.DailyRiskBudgetBox = new TextBox { Text = "", Width = 95 };
			rowPanel.Children.Add(row.DailyRiskBudgetBox);

			row.MaxDrawdownBox = new TextBox { Text = "", Width = 95 };
			rowPanel.Children.Add(row.MaxDrawdownBox);

			row.DrawdownTypeCombo = new ComboBox { ItemsSource = Enum.GetValues(typeof(DrawdownType)), SelectedIndex = 0, Width = 95 };
			rowPanel.Children.Add(row.DrawdownTypeCombo);

			row.StartingBalanceBox = new TextBox { Text = "", Width = 95 };
			rowPanel.Children.Add(row.StartingBalanceBox);

			row.FreezeOffsetBox = new TextBox { Text = "100", Width = 95 };
			rowPanel.Children.Add(row.FreezeOffsetBox);

			followerSetupRows.Add(row);
			return rowPanel;
		}

		private void OnArmClick(object sender, RoutedEventArgs e)
		{
			Account selectedMaster = masterCombo.SelectedItem as Account;
			if (selectedMaster == null)
			{
				MessageBox.Show(this, "Choose a master account first.", "Trade Copier", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			List<AccountState> newFollowers = new List<AccountState>();
			foreach (FollowerSetupRow row in followerSetupRows)
			{
				if (row.EnabledCheckBox.IsChecked != true)
					continue;

				if (ReferenceEquals(row.Account, selectedMaster))
				{
					MessageBox.Show(this, "An account can't be both master and follower.", "Trade Copier", MessageBoxButton.OK, MessageBoxImage.Warning);
					return;
				}

				AccountState followerState = new AccountState(row.Account, AccountRole.Follower);
				ApplyRowSettings(row, followerState);
				newFollowers.Add(followerState);
			}

			if (newFollowers.Count == 0)
			{
				MessageBox.Show(this, "Choose at least one follower account.", "Trade Copier", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			AccountState masterState = new AccountState(selectedMaster, AccountRole.Master);
			CopierManager.Start(masterState, newFollowers);
		}

		private static void ApplyRowSettings(FollowerSetupRow row, AccountState state)
		{
			state.QuantityMultiplier = ParseDecimalOrDefault(row.MultiplierBox.Text, 1m);
			state.MaxContracts = ParseIntOrDefault(row.MaxContractsBox.Text, int.MaxValue);
			state.DailyRiskBudget = ParseDecimalOrDefault(row.DailyRiskBudgetBox.Text, 0m);
			state.MaxDrawdownAmount = ParseDecimalOrDefault(row.MaxDrawdownBox.Text, 0m);
			state.TrailingStopFreezeOffset = ParseDecimalOrDefault(row.FreezeOffsetBox.Text, 0m);
			state.DrawdownType = row.DrawdownTypeCombo.SelectedItem is DrawdownType
				? (DrawdownType)row.DrawdownTypeCombo.SelectedItem
				: DrawdownType.IntradayTrailing;

			decimal startingBalance = ParseDecimalOrDefault(row.StartingBalanceBox.Text, 0m);
			state.InitializeBalances(startingBalance);
		}

		private static decimal ParseDecimalOrDefault(string text, decimal fallback)
		{
			decimal value;
			return decimal.TryParse(text, out value) ? value : fallback;
		}

		private static int ParseIntOrDefault(string text, int fallback)
		{
			int value;
			return int.TryParse(text, out value) ? value : fallback;
		}

		private void ShowSetupScreen()
		{
			setupPanel.Visibility = Visibility.Visible;
			dashboardPanel.Visibility = Visibility.Collapsed;
		}

		// ----- Dashboard screen -----

		private FrameworkElement BuildDashboardPanel()
		{
			DockPanel dashboard = new DockPanel { Margin = new Thickness(12), Visibility = Visibility.Collapsed };

			dashboard.Children.Add(BuildMasterSummary());
			dashboard.Children.Add(BuildLogPanel());
			dashboard.Children.Add(BuildFollowerGrid());

			return dashboard;
		}

		private FrameworkElement BuildMasterSummary()
		{
			Border border = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8) };
			DockPanel.SetDock(border, Dock.Top);

			StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };

			masterStatusIndicator = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Brushes.Gray, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
			row.Children.Add(masterStatusIndicator);

			masterNameText = new TextBlock { FontWeight = FontWeights.Bold, Width = 140, VerticalAlignment = VerticalAlignment.Center };
			row.Children.Add(masterNameText);

			masterConnectionText = new TextBlock { Width = 100, VerticalAlignment = VerticalAlignment.Center };
			row.Children.Add(masterConnectionText);

			masterPositionText = new TextBlock { Width = 200, VerticalAlignment = VerticalAlignment.Center };
			row.Children.Add(masterPositionText);

			masterPnlText = new TextBlock { Width = 200, VerticalAlignment = VerticalAlignment.Center };
			row.Children.Add(masterPnlText);

			masterStatusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
			row.Children.Add(masterStatusText);

			Button killSwitch = new Button { Content = "KILL SWITCH - Flatten All", Background = Brushes.DarkRed, Foreground = Brushes.White, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(16, 0, 8, 0) };
			killSwitch.Click += OnKillSwitchClick;
			row.Children.Add(killSwitch);

			Button stopButton = new Button { Content = "Stop Copier", Padding = new Thickness(8, 4, 8, 4) };
			stopButton.Click += OnStopCopierClick;
			row.Children.Add(stopButton);

			border.Child = row;
			return border;
		}

		private FrameworkElement BuildLogPanel()
		{
			Border border = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0), Height = 140 };
			DockPanel.SetDock(border, Dock.Bottom);

			ListBox logBox = new ListBox { ItemsSource = logLines, FontFamily = new FontFamily("Consolas") };
			border.Child = logBox;
			return border;
		}

		private DataGrid BuildFollowerGrid()
		{
			DataGrid grid = new DataGrid
			{
				ItemsSource = followerRows,
				AutoGenerateColumns = false,
				IsReadOnly = true,
				CanUserAddRows = false
			};

			grid.Columns.Add(new DataGridTextColumn { Header = "Account", Binding = new Binding("Name") });
			grid.Columns.Add(new DataGridTextColumn { Header = "Connection", Binding = new Binding("Connection") });
			grid.Columns.Add(new DataGridTextColumn { Header = "Position", Binding = new Binding("Position") });
			grid.Columns.Add(new DataGridTextColumn { Header = "Realized PnL", Binding = new Binding("RealizedPnl") });
			grid.Columns.Add(new DataGridTextColumn { Header = "Unrealized PnL", Binding = new Binding("UnrealizedPnl") });
			grid.Columns.Add(new DataGridTextColumn { Header = "DRB used/limit", Binding = new Binding("Drb") });
			grid.Columns.Add(new DataGridTextColumn { Header = "Trailing DD room", Binding = new Binding("TrailingDd") });
			grid.Columns.Add(new DataGridTextColumn { Header = "Sent/Filled/Rejected", Binding = new Binding("Stats") });
			grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status") });

			DataGridTemplateColumn statusColumn = new DataGridTemplateColumn { Header = "" };
			FrameworkElementFactory ellipseFactory = new FrameworkElementFactory(typeof(Ellipse));
			ellipseFactory.SetValue(Ellipse.WidthProperty, 12.0);
			ellipseFactory.SetValue(Ellipse.HeightProperty, 12.0);
			ellipseFactory.SetBinding(Ellipse.FillProperty, new Binding("StatusBrush"));
			statusColumn.CellTemplate = new DataTemplate { VisualTree = ellipseFactory };
			grid.Columns.Insert(0, statusColumn);

			DataGridTemplateColumn flattenColumn = new DataGridTemplateColumn { Header = "Flatten" };
			FrameworkElementFactory buttonFactory = new FrameworkElementFactory(typeof(Button));
			buttonFactory.SetValue(ContentControl.ContentProperty, "Flatten");
			buttonFactory.AddHandler(Button.ClickEvent, (RoutedEventHandler)OnRowFlattenClick);
			flattenColumn.CellTemplate = new DataTemplate { VisualTree = buttonFactory };
			grid.Columns.Add(flattenColumn);

			return grid;
		}

		private void OnKillSwitchClick(object sender, RoutedEventArgs e)
		{
			MessageBoxResult result = MessageBox.Show(this,
				"Flatten and lock ALL accounts (master + every follower) right now?",
				"Kill Switch", MessageBoxButton.YesNo, MessageBoxImage.Warning);

			if (result != MessageBoxResult.Yes)
				return;

			CopierManager.RiskManager.ManualFlatten(CopierManager.Master, "Kill switch");
			foreach (AccountState follower in CopierManager.Followers)
				CopierManager.RiskManager.ManualFlatten(follower, "Kill switch");
		}

		private void OnStopCopierClick(object sender, RoutedEventArgs e)
		{
			MessageBoxResult result = MessageBox.Show(this,
				"Stop the copier entirely? This does NOT flatten positions - use Kill Switch for that. " +
				"No further copying, stop-mirroring or risk monitoring will happen until re-armed.",
				"Stop Copier", MessageBoxButton.YesNo, MessageBoxImage.Warning);

			if (result != MessageBoxResult.Yes)
				return;

			CopierManager.Stop();
			ShowSetupScreen();
		}

		private void OnRowFlattenClick(object sender, RoutedEventArgs e)
		{
			FrameworkElement element = sender as FrameworkElement;
			FollowerRow row = element != null ? element.DataContext as FollowerRow : null;
			if (row == null)
				return;

			MessageBoxResult result = MessageBox.Show(this,
				string.Format("Flatten {0} now? This does not lock the account.", row.Name),
				"Flatten Account", MessageBoxButton.YesNo, MessageBoxImage.Warning);

			if (result != MessageBoxResult.Yes)
				return;

			CopierManager.Engine.FlattenAccount(row.State, "Manual flatten (dashboard button)");
		}

		// ----- Refresh -----

		private void RefreshDashboard()
		{
			if (!CopierManager.IsRunning)
				return;

			RefreshMasterSummary();
			foreach (FollowerRow row in followerRows)
				RefreshFollowerRow(row);
		}

		private void RefreshMasterSummary()
		{
			AccountState state = CopierManager.Master;
			decimal equity = CopierManager.RiskManager.GetLiveEquity(state);

			masterNameText.Text = "MASTER: " + state.DisplayName;
			masterConnectionText.Text = DescribeConnection(state);
			masterPositionText.Text = CopierManager.Engine.DescribePosition(state);
			masterPnlText.Text = string.Format("Realized {0:C}  Unrealized {1:C}", state.DailyRealizedPnL, state.DailyUnrealizedPnL);

			if (state.IsLocked)
			{
				masterStatusText.Text = "LOCKED: " + state.LockReason;
				masterStatusIndicator.Background = Brushes.OrangeRed;
			}
			else if (IsNearLimit(state, equity))
			{
				masterStatusText.Text = "Warning";
				masterStatusIndicator.Background = Brushes.Gold;
			}
			else
			{
				masterStatusText.Text = "OK";
				masterStatusIndicator.Background = Brushes.LimeGreen;
			}
		}

		private void RefreshFollowerRow(FollowerRow row)
		{
			AccountState state = row.State;
			decimal equity = CopierManager.RiskManager.GetLiveEquity(state);

			row.Connection = DescribeConnection(state);
			row.Position = CopierManager.Engine.DescribePosition(state);
			row.RealizedPnl = state.DailyRealizedPnL.ToString("C");
			row.UnrealizedPnl = state.DailyUnrealizedPnL.ToString("C");
			row.Stats = string.Format("{0}/{1}/{2}", state.OrdersSent, state.OrdersFilled, state.OrdersRejected);

			decimal drbUsed = state.DailyPnL < 0 ? -state.DailyPnL : 0m;
			row.Drb = state.DailyRiskBudget > 0 ? string.Format("{0:C} / {1:C}", drbUsed, state.DailyRiskBudget) : "off";
			row.TrailingDd = state.MaxDrawdownAmount > 0 ? (equity - state.DrawdownFloor).ToString("C") : "off";

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

		// NOTE: verify Account.Connection/.Status against the NinjaScript
		// Editor - defensively caught so a display-only mistake here can't
		// take the whole dashboard down.
		private static string DescribeConnection(AccountState state)
		{
			try
			{
				Connection connection = state.NinjaAccount.Connection;
				return connection != null ? connection.Status.ToString() : "None";
			}
			catch (Exception)
			{
				return "Unknown";
			}
		}
	}
}
