#region Using declarations
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

//This namespace holds Add ons in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.AddOns
{
    public class TradeCopier : NinjaTrader.NinjaScript.AddOnBase
    {
        private TradeCopierWindow window;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Multi-Account Trade Copier Addon";
                Name = "Austin's Trade Copier";
            }
            else if (State == State.Active)
            {
                if (window == null || !window.IsVisible)
                {
                    window = new TradeCopierWindow();
                    window.Show();
                }
                else
                {
                    window.Focus();
                }
            }
            else if (State == State.Terminated)
            {
                if (window != null)
                {
                    window.Close();
                    window = null;
                }
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            if (window is TradeCopierWindow)
                this.window = window as TradeCopierWindow;
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (window is TradeCopierWindow)
                this.window = null;
        }
    }

    public class TradeCopierWindow : NTWindow
    {
        private enum SizingMode
        {
            OneToOne,
            Multiplier,
            Fixed,
            BalanceRatio,
            Disabled
        }

        private enum RiskAction
        {
            SoftLock,
            HardFlatten
        }

        private enum TradeCopyMode
        {
            All,
            ExitsOnly
        }

        private const int DefaultFixedQuantity = 1;
        private const double DefaultMultiplier = 1.0;
        private const string DefaultGroupName = "Default";
        private const string ProfileFolderName = "AustinTradeCopier";
        private const string ProfileFileExtension = ".xml";
        private const int MaxEventLogLines = 500;

        private readonly ObservableCollection<AccountCopyRow> accountRows = new ObservableCollection<AccountCopyRow>();
        private readonly ObservableCollection<string> connectedAccountNames = new ObservableCollection<string>();
        private readonly Dictionary<string, int> mirroredTargetQuantities = new Dictionary<string, int>();
        private readonly Dictionary<string, int> lockedVirtualPositions = new Dictionary<string, int>();
        private readonly Dictionary<string, int> maxNetVirtualPositions = new Dictionary<string, int>();
        private readonly Dictionary<string, Account> subscribedLeadAccounts = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> eventLogLines = new Queue<string>();
        private readonly DispatcherTimer telemetryTimer;

        private List<Account> connectedAccounts = new List<Account>();
        private bool isCopying;
        private bool dryRunMode;
        private bool rowRefreshPending;
        private string lastGroupListSignature = string.Empty;

        private ComboBox profileComboBox;
        private TextBox profileNameTextBox;
        private ComboBox groupComboBox;
        private DataGrid accountsGrid;
        private Button startPauseButton;
        private CheckBox dryRunCheckBox;
        private TextBlock statusTextBlock;
        private TextBox eventLogTextBox;

        public TradeCopierWindow()
        {
            Caption = "Austin's Trade Copier";
            Width = 1220;
            Height = 760;
            MinWidth = 980;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = BrushRgb(30, 31, 34);

            accountRows.CollectionChanged += AccountRows_CollectionChanged;

            CreateUI();
            RefreshAccountList();
            RefreshProfileList();

            Account.AccountStatusUpdate += OnAccountStatusUpdate;

            telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            telemetryTimer.Tick += TelemetryTimer_Tick;
            telemetryTimer.Start();

            Closing += TradeCopierWindow_Closing;
        }

        private void CreateUI()
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(240) });

            var profilePanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0)
            };

            profilePanel.Children.Add(CreateLabel("Profile"));
            profileComboBox = new ComboBox
            {
                Width = 180,
                Height = 28,
                Margin = new Thickness(8, 0, 8, 0),
                Padding = new Thickness(4)
            };
            profileComboBox.SelectionChanged += ProfileComboBox_SelectionChanged;
            profilePanel.Children.Add(profileComboBox);

            profileNameTextBox = new TextBox
            {
                Text = "Default",
                Width = 160,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            profilePanel.Children.Add(profileNameTextBox);

            var saveProfileButton = CreateButton("Save Profile", Brushes.DimGray);
            saveProfileButton.Click += SaveProfileButton_Click;
            profilePanel.Children.Add(saveProfileButton);

            var loadProfileButton = CreateButton("Load Profile", Brushes.DimGray);
            loadProfileButton.Click += LoadProfileButton_Click;
            profilePanel.Children.Add(loadProfileButton);

            var deleteProfileButton = CreateButton("Delete Profile", Brushes.DimGray);
            deleteProfileButton.Click += DeleteProfileButton_Click;
            profilePanel.Children.Add(deleteProfileButton);

            var profileSection = CreateSection("Profiles", profilePanel);
            Grid.SetRow(profileSection, 0);
            root.Children.Add(profileSection);

            var actionPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0)
            };

            var sessionRiskRow = CreateToolbarRow();
            sessionRiskRow.Children.Add(CreateToolbarLabel("Session"));
            startPauseButton = CreateButton("Start Copying", Brushes.SeaGreen);
            startPauseButton.Width = 130;
            startPauseButton.Click += StartPauseButton_Click;
            sessionRiskRow.Children.Add(startPauseButton);

            dryRunCheckBox = new CheckBox
            {
                Content = "Dry Run",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            sessionRiskRow.Children.Add(dryRunCheckBox);

            sessionRiskRow.Children.Add(CreateToolbarLabel("Risk"));
            var flattenFollowersButton = CreateButton("Flatten Enabled", Brushes.Firebrick);
            flattenFollowersButton.Click += FlattenFollowersButton_Click;
            sessionRiskRow.Children.Add(flattenFollowersButton);

            var flattenSelectedButton = CreateButton("Flatten Selected", Brushes.Firebrick);
            flattenSelectedButton.Click += FlattenSelectedButton_Click;
            sessionRiskRow.Children.Add(flattenSelectedButton);

            var flattenAllButton = CreateButton("Flatten All", Brushes.DarkRed);
            flattenAllButton.Click += FlattenAllButton_Click;
            sessionRiskRow.Children.Add(flattenAllButton);
            actionPanel.Children.Add(sessionRiskRow);

            var groupRow = CreateToolbarRow();
            groupRow.Children.Add(CreateToolbarLabel("Group"));
            groupRow.Children.Add(CreateLabel("Group"));
            groupComboBox = new ComboBox
            {
                Width = 140,
                Height = 28,
                Margin = new Thickness(8, 0, 8, 0),
                Padding = new Thickness(4)
            };
            groupRow.Children.Add(groupComboBox);

            var enableGroupButton = CreateButton("Enable Group", Brushes.DimGray);
            enableGroupButton.Click += EnableGroupButton_Click;
            groupRow.Children.Add(enableGroupButton);

            var pauseGroupButton = CreateButton("Pause Group", Brushes.DimGray);
            pauseGroupButton.Click += PauseGroupButton_Click;
            groupRow.Children.Add(pauseGroupButton);

            var flattenGroupButton = CreateButton("Flatten Group", Brushes.Firebrick);
            flattenGroupButton.Click += FlattenGroupButton_Click;
            groupRow.Children.Add(flattenGroupButton);
            actionPanel.Children.Add(groupRow);

            var selectionRow = CreateToolbarRow();
            selectionRow.Children.Add(CreateToolbarLabel("Selection"));
            var reconcileSelectedButton = CreateButton("Reconcile Selected", Brushes.DimGray);
            reconcileSelectedButton.Click += ReconcileSelectedButton_Click;
            selectionRow.Children.Add(reconcileSelectedButton);

            var enableSelectedButton = CreateButton("Enable Selected", Brushes.DimGray);
            enableSelectedButton.Click += EnableSelectedButton_Click;
            selectionRow.Children.Add(enableSelectedButton);

            var removeSelectedButton = CreateButton("Disable Selected", Brushes.DimGray);
            removeSelectedButton.Click += RemoveSelectedButton_Click;
            selectionRow.Children.Add(removeSelectedButton);

            var unlockSelectedButton = CreateButton("Unlock Selected", Brushes.DimGray);
            unlockSelectedButton.Click += UnlockSelectedButton_Click;
            selectionRow.Children.Add(unlockSelectedButton);

            var resetBaselineButton = CreateButton("Reset Baselines", Brushes.DimGray);
            resetBaselineButton.Click += ResetBaselinesButton_Click;
            selectionRow.Children.Add(resetBaselineButton);

            var applyGroupSettingsButton = CreateButton("Apply Row Settings To Group", Brushes.DimGray);
            applyGroupSettingsButton.Click += ApplyGroupSettingsButton_Click;
            selectionRow.Children.Add(applyGroupSettingsButton);
            actionPanel.Children.Add(selectionRow);

            var actionSection = CreateSection("Controls", actionPanel);
            Grid.SetRow(actionSection, 1);
            root.Children.Add(actionSection);

            accountsGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                FrozenColumnCount = 4,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                ItemsSource = accountRows,
                RowStyle = CreateRowStyle(),
                Background = BrushRgb(32, 33, 36),
                Foreground = Brushes.White,
                RowBackground = BrushRgb(42, 43, 47),
                AlternatingRowBackground = BrushRgb(36, 37, 41),
                HorizontalGridLinesBrush = BrushRgb(64, 66, 72),
                VerticalGridLinesBrush = BrushRgb(64, 66, 72),
                BorderBrush = BrushRgb(82, 88, 96),
                BorderThickness = new Thickness(1),
                RowHeaderWidth = 0,
                ColumnHeaderHeight = 28,
                RowHeight = 24,
                FontSize = 12,
                ColumnHeaderStyle = CreateGridHeaderStyle(),
                CellStyle = CreateGridCellStyle(),
                Margin = new Thickness(0, 0, 0, 8)
            };
            AddGridColumns(accountsGrid);
            accountsGrid.SelectionChanged += AccountsGrid_SelectionChanged;

            Grid.SetRow(accountsGrid, 2);
            root.Children.Add(accountsGrid);

            statusTextBlock = new TextBlock
            {
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
                Text = "Ready. Enable accounts, choose each row's lead/group/sizing/risk, then start copying."
            };
            Grid.SetRow(statusTextBlock, 3);
            root.Children.Add(statusTextBlock);

            var logPanel = new Grid();
            logPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            logPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var logHeader = new Grid
            {
                Margin = new Thickness(0, 0, 0, 7)
            };
            logHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            logHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var logTitleBlock = new TextBlock
            {
                Text = "EVENT LOG",
                Foreground = BrushRgb(177, 184, 194),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(logTitleBlock, 0);
            logHeader.Children.Add(logTitleBlock);

            var logButtonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var exportLogButton = CreateButton("Export Log", Brushes.DimGray);
            exportLogButton.Click += ExportLogButton_Click;
            logButtonPanel.Children.Add(exportLogButton);

            var clearLogButton = CreateButton("Clear Log", Brushes.DimGray);
            clearLogButton.Click += ClearLogButton_Click;
            logButtonPanel.Children.Add(clearLogButton);

            Grid.SetColumn(logButtonPanel, 1);
            logHeader.Children.Add(logButtonPanel);

            Grid.SetRow(logHeader, 0);
            logPanel.Children.Add(logHeader);

            eventLogTextBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                MinHeight = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = BrushRgb(18, 19, 21),
                Foreground = Brushes.Gainsboro,
                BorderBrush = BrushRgb(64, 66, 72),
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetRow(eventLogTextBox, 1);
            logPanel.Children.Add(eventLogTextBox);

            var logSection = new Border
            {
                Background = BrushRgb(38, 39, 43),
                BorderBrush = BrushRgb(58, 61, 67),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0),
                Child = logPanel
            };
            Grid.SetRow(logSection, 4);
            root.Children.Add(logSection);

            Content = root;
        }

        private Brush BrushRgb(byte red, byte green, byte blue)
        {
            return new SolidColorBrush(Color.FromRgb(red, green, blue));
        }

        private Border CreateSection(string title, UIElement content)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var titleBlock = new TextBlock
            {
                Text = title.ToUpperInvariant(),
                Foreground = BrushRgb(177, 184, 194),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 7)
            };
            Grid.SetRow(titleBlock, 0);
            grid.Children.Add(titleBlock);

            Grid.SetRow(content, 1);
            grid.Children.Add(content);

            return new Border
            {
                Background = BrushRgb(38, 39, 43),
                BorderBrush = BrushRgb(58, 61, 67),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid
            };
        }

        private WrapPanel CreateToolbarRow()
        {
            return new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 2)
            };
        }

        private Label CreateLabel(string text)
        {
            return new Label
            {
                Content = text,
                Foreground = BrushRgb(236, 238, 241),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 4, 6)
            };
        }

        private Label CreateToolbarLabel(string text)
        {
            return new Label
            {
                Content = text.ToUpperInvariant(),
                Foreground = BrushRgb(177, 184, 194),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0, 8, 6)
            };
        }

        private Button CreateButton(string text, Brush background)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 6),
                Background = background,
                Foreground = Brushes.White,
                BorderBrush = BrushRgb(92, 96, 104),
                MinWidth = 92,
                MinHeight = 28
            };
        }

        private Style CreateGridHeaderStyle()
        {
            var style = new Style(typeof(DataGridColumnHeader));
            style.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, BrushRgb(52, 55, 61)));
            style.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, BrushRgb(236, 238, 241)));
            style.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, BrushRgb(73, 77, 85)));
            style.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            style.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(6, 0, 6, 0)));
            style.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.Bold));
            return style;
        }

        private Style CreateGridCellStyle()
        {
            var style = new Style(typeof(DataGridCell));
            style.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(DataGridCell.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(DataGridCell.PaddingProperty, new Thickness(6, 0, 6, 0)));
            return style;
        }

        private void AddGridColumns(DataGrid grid)
        {
            grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = CreateColumnHeader("On", "Enable this account row. Disabled rows stay visible but do not receive copied orders."),
                Binding = new Binding("Enabled") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(42)
            });

            grid.Columns.Add(CreateTextColumn("Account", "AccountName", 120, null, true, "Connected NinjaTrader account."));
            grid.Columns.Add(CreateTextColumn("Role", "RoleSummary", 86, null, true, "Available, Lead, Copy row, or Conflict based on the enabled rows."));
            grid.Columns.Add(new DataGridComboBoxColumn
            {
                Header = CreateColumnHeader("Lead", "Account whose filled orders this row mirrors."),
                ItemsSource = connectedAccountNames,
                SelectedItemBinding = new Binding("LeadAccountName") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(120)
            });
            grid.Columns.Add(CreateTextColumn("Group", "GroupName", 90, null, false, "Free-form group name used by group actions."));
            grid.Columns.Add(new DataGridComboBoxColumn
            {
                Header = CreateColumnHeader("Copy", "All copies entries and exits. ExitsOnly blocks new entries while allowing exits."),
                ItemsSource = Enum.GetValues(typeof(TradeCopyMode)),
                SelectedItemBinding = new Binding("CopyMode") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(90)
            });

            grid.Columns.Add(new DataGridComboBoxColumn
            {
                Header = CreateColumnHeader("Sizing", "OneToOne uses lead quantity. Multiplier scales it. Fixed uses Fixed Qty. BalanceRatio scales by account value."),
                ItemsSource = Enum.GetValues(typeof(SizingMode)),
                SelectedItemBinding = new Binding("SizingMode") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(105)
            });

            grid.Columns.Add(CreateTextColumn("Multiplier", "Multiplier", 82, "{0:0.##}", false, "Used only when Sizing is Multiplier. 2 copies twice the lead quantity."));
            grid.Columns.Add(CreateTextColumn("Fixed Qty", "FixedQuantity", 72, null, false, "Used only when Sizing is Fixed."));
            grid.Columns.Add(CreateTextColumn("Max Qty", "MaxQuantity", 72, null, false, "Caps the quantity for each copied order. 0 disables the cap."));
            grid.Columns.Add(CreateTextColumn("Max Net", "MaxNetPosition", 76, null, false, "Caps the row's net position size. 0 disables the cap."));
            grid.Columns.Add(CreateTextColumn("Loss Limit", "DailyLossLimit", 82, "{0:0}", false, "Locks this row when session PnL reaches this loss. 0 disables the limit."));
            grid.Columns.Add(CreateTextColumn("Max DD", "MaxDrawdown", 78, "{0:0}", false, "Locks this row when drawdown from peak session PnL reaches this amount. 0 disables the limit."));
            grid.Columns.Add(CreateTextColumn("Profit Target", "ProfitTarget", 96, "{0:0}", false, "Locks this row after this session profit target is reached. 0 disables the target."));

            grid.Columns.Add(new DataGridComboBoxColumn
            {
                Header = CreateColumnHeader("Limit Action", "SoftLock blocks entries and allows exits. HardFlatten also flattens the row account."),
                ItemsSource = Enum.GetValues(typeof(RiskAction)),
                SelectedItemBinding = new Binding("LimitAction") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(105)
            });

            grid.Columns.Add(CreateTextColumn("Symbols", "InstrumentFilter", 100, null, false, "Optional comma-separated instrument filters. Leave blank to copy all symbols."));
            grid.Columns.Add(CreateTextColumn("Conn", "ConnectionStatus", 90, null, true, "Current NinjaTrader connection status."));
            grid.Columns.Add(CreateTextColumn("Status", "Status", 150, null, true, "Current copier state for this row."));
            grid.Columns.Add(CreateTextColumn("Pos", "PositionSummary", 125, null, true, "Current account position summary."));
            grid.Columns.Add(CreateTextColumn("Pnl", "SessionPnl", 80, "{0:C0}", true, "Session PnL relative to this row's current baseline."));
            grid.Columns.Add(CreateTextColumn("DD", "Drawdown", 80, "{0:C0}", true, "Drawdown from peak session PnL."));

            grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = CreateColumnHeader("Manual Lock", "Blocks entries for this row while still allowing exits."),
                Binding = new Binding("ManualLock") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(95)
            });

            grid.Columns.Add(CreateTextColumn("Last Action", "LastAction", 220, null, true, "Most recent copier action or skip reason for this row."));
        }

        private TextBlock CreateColumnHeader(string text, string tooltip)
        {
            var header = new TextBlock { Text = text };
            if (!string.IsNullOrWhiteSpace(tooltip))
                header.ToolTip = tooltip;

            return header;
        }

        private DataGridTextColumn CreateTextColumn(string header, string propertyName, double width, string stringFormat, bool readOnly, string tooltip)
        {
            var binding = new Binding(propertyName)
            {
                Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };

            if (!string.IsNullOrEmpty(stringFormat))
                binding.StringFormat = stringFormat;

            return new DataGridTextColumn
            {
                Header = CreateColumnHeader(header, tooltip),
                Binding = binding,
                Width = new DataGridLength(width),
                IsReadOnly = readOnly
            };
        }

        private Style CreateRowStyle()
        {
            var style = new Style(typeof(DataGridRow));
            style.Setters.Add(new Setter(DataGridRow.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(50, 50, 54))));

            AddRowTrigger(style, "Active", Color.FromRgb(39, 70, 50));
            AddRowTrigger(style, "Ready", Color.FromRgb(50, 50, 54));
            AddRowTrigger(style, "Warning", Color.FromRgb(94, 75, 33));
            AddRowTrigger(style, "Locked", Color.FromRgb(88, 48, 35));
            AddRowTrigger(style, "ExitsOnly", Color.FromRgb(62, 65, 82));
            AddRowTrigger(style, "Error", Color.FromRgb(92, 38, 42));
            AddRowTrigger(style, "Disabled", Color.FromRgb(54, 54, 58));
            AddRowTrigger(style, "Desynced", Color.FromRgb(92, 38, 42));

            return style;
        }

        private void AddRowTrigger(Style style, string level, Color color)
        {
            var trigger = new DataTrigger
            {
                Binding = new Binding("StatusLevel"),
                Value = level
            };
            trigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(color)));
            style.Triggers.Add(trigger);
        }

        private void RefreshAccountList()
        {
            try
            {
                lock (Account.All)
                    connectedAccounts = Account.All.Where(a => a.ConnectionStatus == ConnectionStatus.Connected).ToList();
            }
            catch (Exception ex)
            {
                connectedAccounts = new List<Account>();
                Log("Unable to refresh account list: " + ex.Message);
            }

            Dispatcher.InvokeAsync(() =>
            {
                RefreshConnectedAccountNames();
                SyncAccountRowsWithConnectedAccounts();
                SyncLeadAccountSubscriptions();
                RefreshGroupList();
                RefreshAllRows();
            });
        }

        private void OnAccountStatusUpdate(object sender, AccountStatusEventArgs e)
        {
            RefreshAccountList();
        }

        private void RefreshConnectedAccountNames()
        {
            connectedAccountNames.Clear();
            connectedAccountNames.Add(string.Empty);

            foreach (var accountName in connectedAccounts.Select(a => a.Name).OrderBy(name => name))
                connectedAccountNames.Add(accountName);
        }

        private void SyncAccountRowsWithConnectedAccounts()
        {
            foreach (var account in connectedAccounts.OrderBy(a => a.Name))
            {
                var row = accountRows.FirstOrDefault(r => AccountNamesEqual(r.AccountName, account.Name));
                if (row != null)
                {
                    row.RefreshAccount(account);
                    continue;
                }

                row = new AccountCopyRow(account, DefaultGroupName, ReadAccountPnl(account));
                row.Enabled = false;
                row.LastAction = "Discovered";
                accountRows.Add(row);
            }
        }

        private Account ResolveLeadAccountForRow(AccountCopyRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.LeadAccountName))
                return null;

            return connectedAccounts.FirstOrDefault(a => AccountNamesEqual(a.Name, row.LeadAccountName));
        }

        private List<Account> GetConfiguredLeadAccounts()
        {
            return accountRows
                .Where(r => r.Enabled && r.SizingMode != SizingMode.Disabled)
                .Select(ResolveLeadAccountForRow)
                .Where(a => a != null)
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private bool IsConfiguredLeadAccount(string accountName)
        {
            return accountRows.Any(r => r.Enabled && r.SizingMode != SizingMode.Disabled && AccountNamesEqual(r.LeadAccountName, accountName));
        }

        private bool AccountNamesEqual(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private void SyncLeadAccountSubscriptions()
        {
            var desiredLeads = GetConfiguredLeadAccounts();

            foreach (var existing in subscribedLeadAccounts.ToList())
            {
                var desired = desiredLeads.FirstOrDefault(a => AccountNamesEqual(a.Name, existing.Key));
                if (desired == null)
                {
                    existing.Value.OrderUpdate -= OnOrderUpdate;
                    subscribedLeadAccounts.Remove(existing.Key);
                    continue;
                }

                if (!object.ReferenceEquals(existing.Value, desired))
                {
                    existing.Value.OrderUpdate -= OnOrderUpdate;
                    desired.OrderUpdate += OnOrderUpdate;
                    subscribedLeadAccounts[existing.Key] = desired;
                }
            }

            foreach (var lead in desiredLeads.Where(a => !subscribedLeadAccounts.ContainsKey(a.Name)))
            {
                lead.OrderUpdate += OnOrderUpdate;
                subscribedLeadAccounts[lead.Name] = lead;
            }
        }

        private void UnsubscribeAllLeadAccounts()
        {
            foreach (var lead in subscribedLeadAccounts.Values.ToList())
                lead.OrderUpdate -= OnOrderUpdate;

            subscribedLeadAccounts.Clear();
        }

        private void AccountRows_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (AccountCopyRow row in e.OldItems)
                    row.PropertyChanged -= AccountRow_PropertyChanged;
            }

            if (e.NewItems != null)
            {
                foreach (AccountCopyRow row in e.NewItems)
                {
                    row.PropertyChanged -= AccountRow_PropertyChanged;
                    row.PropertyChanged += AccountRow_PropertyChanged;
                }
            }
        }

        private void AccountRow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.PropertyName))
                return;

            if (e.PropertyName == "LeadAccountName" || e.PropertyName == "Enabled" || e.PropertyName == "SizingMode")
                SyncLeadAccountSubscriptions();

            if (e.PropertyName == "GroupName")
            {
                lastGroupListSignature = string.Empty;
                RefreshGroupList();
            }

            if (RowPropertyAffectsReadiness(e.PropertyName))
                QueueRowRefresh();
        }

        private void AccountsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SetStatus(BuildSummaryStatus());
        }

        private bool RowPropertyAffectsReadiness(string propertyName)
        {
            switch (propertyName)
            {
                case "Enabled":
                case "LeadAccountName":
                case "GroupName":
                case "CopyMode":
                case "SizingMode":
                case "Multiplier":
                case "FixedQuantity":
                case "MaxQuantity":
                case "MaxNetPosition":
                case "DailyLossLimit":
                case "MaxDrawdown":
                case "ProfitTarget":
                case "LimitAction":
                case "InstrumentFilter":
                case "ManualLock":
                    return true;
                default:
                    return false;
            }
        }

        private void QueueRowRefresh()
        {
            if (rowRefreshPending)
                return;

            rowRefreshPending = true;
            Dispatcher.InvokeAsync(() =>
            {
                rowRefreshPending = false;
                RefreshAllRows();
            });
        }

        private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var profileName = profileComboBox.SelectedItem as string;
            if (!string.IsNullOrEmpty(profileName))
                profileNameTextBox.Text = profileName;
        }

        private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var profileName = NormalizeProfileName(profileNameTextBox.Text);
            if (string.IsNullOrEmpty(profileName))
            {
                SetStatus("Enter a profile name before saving.");
                return;
            }

            try
            {
                SaveProfile(profileName);
                RefreshProfileList();
                profileComboBox.SelectedItem = profileName;
                SetStatus("Saved profile " + profileName + ".");
                Log("Saved profile " + profileName + ".");
            }
            catch (Exception ex)
            {
                SetStatus("Profile save failed.");
                Log("ERROR profile save failed: " + ex.Message);
            }
        }

        private void LoadProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (isCopying)
            {
                SetStatus("Pause copying before loading a profile.");
                return;
            }

            var profileName = NormalizeProfileName(profileNameTextBox.Text);
            if (string.IsNullOrEmpty(profileName))
            {
                SetStatus("Select or enter a profile name before loading.");
                return;
            }

            try
            {
                LoadProfile(profileName);
                SetStatus("Loaded profile " + profileName + ".");
                Log("Loaded profile " + profileName + ".");
            }
            catch (Exception ex)
            {
                SetStatus("Profile load failed.");
                Log("ERROR profile load failed: " + ex.Message);
            }
        }

        private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var profileName = NormalizeProfileName(profileNameTextBox.Text);
            if (string.IsNullOrEmpty(profileName))
            {
                SetStatus("Select or enter a profile name before deleting.");
                return;
            }

            var path = GetProfilePath(profileName);
            if (!File.Exists(path))
            {
                SetStatus("Profile " + profileName + " does not exist.");
                return;
            }

            if (MessageBox.Show("Delete profile " + profileName + "?", "Confirm Delete Profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                File.Delete(path);
                RefreshProfileList();
                SetStatus("Deleted profile " + profileName + ".");
                Log("Deleted profile " + profileName + ".");
            }
            catch (Exception ex)
            {
                SetStatus("Profile delete failed.");
                Log("ERROR profile delete failed: " + ex.Message);
            }
        }

        private void ExportLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var logDirectory = GetLogDirectoryPath();
                Directory.CreateDirectory(logDirectory);

                var fileName = "trade-copier-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt";
                var path = Path.Combine(logDirectory, fileName);
                File.WriteAllLines(path, eventLogLines.ToArray());
                SetStatus("Exported event log.");
                Log("Exported event log to " + path + ".");
            }
            catch (Exception ex)
            {
                SetStatus("Event log export failed.");
                Log("ERROR event log export failed: " + ex.Message);
            }
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            eventLogLines.Clear();
            if (eventLogTextBox != null)
                eventLogTextBox.Clear();

            SetStatus("Event log cleared.");
        }

        private void SaveProfile(string profileName)
        {
            Directory.CreateDirectory(GetProfileDirectoryPath());

            var document = new XmlDocument();
            var root = document.CreateElement("TradeCopierProfile");
            root.SetAttribute("version", "2");
            root.SetAttribute("savedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            root.SetAttribute("leadAccount", accountRows.Where(r => r.Enabled).Select(r => r.LeadAccountName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty);
            document.AppendChild(root);

            foreach (var row in accountRows)
            {
                var rowElement = document.CreateElement("Follower");
                SetAttribute(rowElement, "account", row.AccountName);
                SetAttribute(rowElement, "leadAccount", row.LeadAccountName);
                SetAttribute(rowElement, "group", row.GroupName);
                SetAttribute(rowElement, "enabled", row.Enabled);
                SetAttribute(rowElement, "copyMode", row.CopyMode.ToString());
                SetAttribute(rowElement, "sizingMode", row.SizingMode.ToString());
                SetAttribute(rowElement, "multiplier", row.Multiplier);
                SetAttribute(rowElement, "fixedQuantity", row.FixedQuantity);
                SetAttribute(rowElement, "maxQuantity", row.MaxQuantity);
                SetAttribute(rowElement, "maxNetPosition", row.MaxNetPosition);
                SetAttribute(rowElement, "dailyLossLimit", row.DailyLossLimit);
                SetAttribute(rowElement, "maxDrawdown", row.MaxDrawdown);
                SetAttribute(rowElement, "profitTarget", row.ProfitTarget);
                SetAttribute(rowElement, "limitAction", row.LimitAction.ToString());
                SetAttribute(rowElement, "instrumentFilter", row.InstrumentFilter);
                root.AppendChild(rowElement);
            }

            document.Save(GetProfilePath(profileName));
        }

        private void LoadProfile(string profileName)
        {
            var path = GetProfilePath(profileName);
            if (!File.Exists(path))
            {
                SetStatus("Profile " + profileName + " does not exist.");
                return;
            }

            var accounts = GetConnectedAccountsSnapshot();
            connectedAccounts = accounts;
            RefreshConnectedAccountNames();

            var document = new XmlDocument();
            document.Load(path);

            var root = document.DocumentElement;
            if (root == null || root.Name != "TradeCopierProfile")
                throw new InvalidOperationException("Invalid profile file.");

            var leadAccountName = root.GetAttribute("leadAccount");

            accountRows.Clear();
            mirroredTargetQuantities.Clear();
            lockedVirtualPositions.Clear();
            maxNetVirtualPositions.Clear();

            var seenAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in root.SelectNodes("Follower"))
            {
                var element = node as XmlElement;
                if (element == null)
                    continue;

                var accountName = element.GetAttribute("account");
                if (string.IsNullOrWhiteSpace(accountName))
                    continue;

                if (seenAccounts.Contains(accountName))
                {
                    Log("Profile skipped duplicate row " + accountName + ".");
                    continue;
                }

                var account = accounts.FirstOrDefault(a => string.Equals(a.Name, accountName, StringComparison.OrdinalIgnoreCase));
                if (account == null)
                {
                    Log("Profile row " + accountName + " is not connected.");
                    continue;
                }

                var groupName = NormalizeGroupName(GetStringAttribute(element, "group", DefaultGroupName));
                var rowLeadName = GetOptionalStringAttribute(element, "leadAccount", leadAccountName);
                var rowEnabled = GetBoolAttribute(element, "enabled", true);
                Account rowLead = null;

                if (!string.IsNullOrWhiteSpace(rowLeadName))
                {
                    rowLead = accounts.FirstOrDefault(a => AccountNamesEqual(a.Name, rowLeadName));
                    if (rowLead == null)
                    {
                        Log("Profile lead account " + rowLeadName + " for " + accountName + " is not connected.");
                        if (rowEnabled)
                        {
                            rowEnabled = false;
                            Log("Profile disabled " + accountName + " until its lead reconnects.");
                        }
                    }
                }
                else if (rowEnabled)
                {
                    rowEnabled = false;
                    Log("Profile disabled " + accountName + " because no lead is saved.");
                }

                var row = new AccountCopyRow(account, groupName, ReadAccountPnl(account));
                row.LeadAccountName = rowLead != null ? rowLead.Name : rowLeadName;
                row.Enabled = rowEnabled;
                row.CopyMode = GetEnumAttribute(element, "copyMode", TradeCopyMode.All);
                row.SizingMode = GetEnumAttribute(element, "sizingMode", SizingMode.OneToOne);
                row.Multiplier = GetDoubleAttribute(element, "multiplier", DefaultMultiplier);
                row.FixedQuantity = GetIntAttribute(element, "fixedQuantity", DefaultFixedQuantity);
                row.MaxQuantity = GetIntAttribute(element, "maxQuantity", 0);
                row.MaxNetPosition = GetIntAttribute(element, "maxNetPosition", 0);
                row.DailyLossLimit = GetDoubleAttribute(element, "dailyLossLimit", 0);
                row.MaxDrawdown = GetDoubleAttribute(element, "maxDrawdown", 0);
                row.ProfitTarget = GetDoubleAttribute(element, "profitTarget", 0);
                row.LimitAction = GetEnumAttribute(element, "limitAction", RiskAction.SoftLock);
                row.InstrumentFilter = GetStringAttribute(element, "instrumentFilter", string.Empty);
                row.LastAction = row.Enabled ? "Loaded profile" : "Loaded disabled";

                accountRows.Add(row);
                seenAccounts.Add(accountName);
            }
            lastGroupListSignature = string.Empty;
            SyncAccountRowsWithConnectedAccounts();
            RefreshGroupList();
            SyncLeadAccountSubscriptions();
            RefreshAllRows();
        }

        private List<Account> GetConnectedAccountsSnapshot()
        {
            try
            {
                lock (Account.All)
                    return Account.All.Where(a => a.ConnectionStatus == ConnectionStatus.Connected).ToList();
            }
            catch (Exception ex)
            {
                Log("Unable to refresh account list: " + ex.Message);
                return new List<Account>();
            }
        }

        private void RefreshProfileList()
        {
            if (profileComboBox == null)
                return;

            var selected = profileComboBox.SelectedItem as string;
            var profiles = GetProfileNames();
            profileComboBox.ItemsSource = profiles;

            if (!string.IsNullOrEmpty(selected) && profiles.Any(p => string.Equals(p, selected, StringComparison.OrdinalIgnoreCase)))
                profileComboBox.SelectedItem = profiles.First(p => string.Equals(p, selected, StringComparison.OrdinalIgnoreCase));
            else if (profiles.Count > 0)
                profileComboBox.SelectedIndex = 0;
        }

        private List<string> GetProfileNames()
        {
            var directory = GetProfileDirectoryPath();
            if (!Directory.Exists(directory))
                return new List<string>();

            return Directory.GetFiles(directory, "*" + ProfileFileExtension)
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(name => name)
                .ToList();
        }

        private string GetProfileDirectoryPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NinjaTrader 8",
                "templates",
                ProfileFolderName);
        }

        private string GetLogDirectoryPath()
        {
            return Path.Combine(GetProfileDirectoryPath(), "Logs");
        }

        private string GetProfilePath(string profileName)
        {
            return Path.Combine(GetProfileDirectoryPath(), NormalizeProfileName(profileName) + ProfileFileExtension);
        }

        private string NormalizeProfileName(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                return string.Empty;

            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(profileName.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return sanitized.Trim();
        }

        private void SetAttribute(XmlElement element, string name, bool value)
        {
            element.SetAttribute(name, value ? "true" : "false");
        }

        private void SetAttribute(XmlElement element, string name, int value)
        {
            element.SetAttribute(name, value.ToString(CultureInfo.InvariantCulture));
        }

        private void SetAttribute(XmlElement element, string name, double value)
        {
            element.SetAttribute(name, value.ToString(CultureInfo.InvariantCulture));
        }

        private void SetAttribute(XmlElement element, string name, string value)
        {
            element.SetAttribute(name, value ?? string.Empty);
        }

        private string GetStringAttribute(XmlElement element, string name, string fallback)
        {
            var value = element.GetAttribute(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private string GetOptionalStringAttribute(XmlElement element, string name, string fallback)
        {
            return element.HasAttribute(name) ? element.GetAttribute(name).Trim() : fallback;
        }

        private bool GetBoolAttribute(XmlElement element, string name, bool fallback)
        {
            bool value;
            return bool.TryParse(element.GetAttribute(name), out value) ? value : fallback;
        }

        private int GetIntAttribute(XmlElement element, string name, int fallback)
        {
            int value;
            return int.TryParse(element.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private double GetDoubleAttribute(XmlElement element, string name, double fallback)
        {
            double value;
            return double.TryParse(element.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private TEnum GetEnumAttribute<TEnum>(XmlElement element, string name, TEnum fallback) where TEnum : struct
        {
            TEnum value;
            return Enum.TryParse(element.GetAttribute(name), true, out value) ? value : fallback;
        }

        private void StartPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isCopying)
                StartCopyingTrades();
            else
                PauseCopyingTrades();
        }

        private void StartCopyingTrades()
        {
            if (!accountRows.Any(r => r.Enabled && r.SizingMode != SizingMode.Disabled))
            {
                SetStatus("Enable at least one account row before starting.");
                return;
            }

            var validationMessage = ValidateReadyToStart();
            if (!string.IsNullOrEmpty(validationMessage))
            {
                SetStatus(validationMessage);
                Log("Start blocked: " + validationMessage);
                return;
            }

            SyncLeadAccountSubscriptions();
            mirroredTargetQuantities.Clear();
            lockedVirtualPositions.Clear();
            maxNetVirtualPositions.Clear();
            dryRunMode = dryRunCheckBox != null && dryRunCheckBox.IsChecked == true;
            isCopying = true;
            startPauseButton.Content = "Pause Copying";
            startPauseButton.Background = Brushes.DarkOrange;
            if (dryRunCheckBox != null)
                dryRunCheckBox.IsEnabled = false;

            SetStatus(dryRunMode ? "Dry run active. Orders are simulated only." : "Copying active. Configured leads are armed.");
            Log(dryRunMode ? "Dry run started. No copied orders will be submitted." : "Copying started.");
            RefreshAllRows();
        }

        private void PauseCopyingTrades(bool silent)
        {
            isCopying = false;
            dryRunMode = false;
            if (startPauseButton != null)
            {
                startPauseButton.Content = "Start Copying";
                startPauseButton.Background = Brushes.SeaGreen;
            }

            if (dryRunCheckBox != null)
                dryRunCheckBox.IsEnabled = true;

            if (silent)
                return;

            SetStatus("Copying paused. Positions were left untouched.");
            Log("Copying paused.");
            RefreshAllRows();
        }

        private void PauseCopyingTrades()
        {
            PauseCopyingTrades(false);
        }

        private void OnOrderUpdate(object sender, OrderEventArgs args)
        {
            if (!isCopying || args.Order == null || args.Order.Account == null)
                return;

            if (!subscribedLeadAccounts.ContainsKey(args.Order.Account.Name))
                return;

            if (args.Order.OrderState != OrderState.PartFilled && args.Order.OrderState != OrderState.Filled)
                return;

            Dispatcher.InvokeAsync(() => CopyOrderToFollowerAccounts(args.Order));
        }

        private void CopyOrderToFollowerAccounts(Order sourceOrder)
        {
            if (sourceOrder == null || sourceOrder.Account == null || sourceOrder.Filled <= 0)
                return;

            var sourceLeadName = sourceOrder.Account.Name;
            foreach (var row in accountRows.ToList())
            {
                if (row.Account == null || !row.Enabled || row.SizingMode == SizingMode.Disabled)
                    continue;

                if (!AccountNamesEqual(row.LeadAccountName, sourceLeadName))
                    continue;

                if (row.Account.ConnectionStatus != ConnectionStatus.Connected)
                {
                    row.SetStatus("Error", "Disconnected");
                    row.LastAction = "Skipped disconnected";
                    Log(row.AccountName + " skipped because account is disconnected.");
                    continue;
                }

                var rowLead = ResolveLeadAccountForRow(row);
                if (rowLead == null)
                {
                    row.SetStatus("Error", "No lead");
                    row.LastAction = "Skipped no lead";
                    Log(row.AccountName + " skipped because lead " + row.LeadAccountName + " is not connected.");
                    continue;
                }

                if (AccountNamesEqual(row.AccountName, rowLead.Name))
                {
                    row.SetStatus("Error", "Self-copy blocked");
                    row.LastAction = "Skipped self-copy";
                    continue;
                }

                if (!RowAllowsInstrument(row, sourceOrder.Instrument))
                {
                    row.LastAction = "Skipped filtered symbol";
                    Log(row.AccountName + " skipped " + GetInstrumentName(sourceOrder.Instrument) + " due to symbol filter.");
                    continue;
                }

                var targetKey = GetTargetMirrorKey(sourceOrder, row);
                var alreadyMirrored = mirroredTargetQuantities.ContainsKey(targetKey) ? mirroredTargetQuantities[targetKey] : 0;
                var desiredQuantity = CalculateDesiredTargetQuantity(row, sourceOrder);
                var quantityToSubmit = desiredQuantity - alreadyMirrored;

                if (quantityToSubmit <= 0)
                    continue;

                var originalQuantity = quantityToSubmit;
                var reduceOnlyMode = RowIsReduceOnly(row);
                if (reduceOnlyMode)
                    quantityToSubmit = CapLockedQuantityToReducingOnly(row, sourceOrder, quantityToSubmit);

                if (quantityToSubmit <= 0)
                {
                    mirroredTargetQuantities[targetKey] = desiredQuantity;
                    row.LastAction = "Blocked entry";
                    Log(row.AccountName + " blocked copied entry while in reduce-only mode.");
                    continue;
                }

                if (quantityToSubmit < originalQuantity)
                    Log(row.AccountName + " capped reduce-only exit from " + originalQuantity + " to " + quantityToSubmit + ".");

                var beforeMaxNetQuantity = quantityToSubmit;
                quantityToSubmit = CapQuantityToMaxNetPosition(row, sourceOrder.Instrument, sourceOrder.OrderAction, quantityToSubmit);
                var maxNetCapped = quantityToSubmit < beforeMaxNetQuantity;

                if (quantityToSubmit <= 0)
                {
                    mirroredTargetQuantities[targetKey] = desiredQuantity;
                    row.LastAction = "Blocked by max net";
                    Log(row.AccountName + " blocked " + GetInstrumentName(sourceOrder.Instrument) + " because max net position would be exceeded.");
                    continue;
                }

                if (maxNetCapped)
                    Log(row.AccountName + " capped " + GetInstrumentName(sourceOrder.Instrument) + " copy from " + beforeMaxNetQuantity + " to " + quantityToSubmit + " by max net position.");

                try
                {
                    if (dryRunMode)
                    {
                        if (reduceOnlyMode)
                            ApplyLockedVirtualFill(row, sourceOrder.Instrument, sourceOrder.OrderAction, quantityToSubmit);

                        ApplyMaxNetVirtualFill(row, sourceOrder.Instrument, sourceOrder.OrderAction, quantityToSubmit);
                        mirroredTargetQuantities[targetKey] = (reduceOnlyMode && quantityToSubmit < originalQuantity) || maxNetCapped
                            ? desiredQuantity
                            : alreadyMirrored + quantityToSubmit;
                        row.LastAction = "Dry run " + DescribeOrder(sourceOrder.OrderAction, quantityToSubmit, sourceOrder.Instrument);
                        Log("DRY RUN " + row.AccountName + " would send " + DescribeOrder(sourceOrder.OrderAction, quantityToSubmit, sourceOrder.Instrument) + ".");
                        continue;
                    }

                    var copiedOrder = CreateAccountOrder(
                        row.Account,
                        sourceOrder.Instrument,
                        sourceOrder.OrderAction,
                        sourceOrder.OrderType,
                        sourceOrder.TimeInForce,
                        quantityToSubmit,
                        sourceOrder.LimitPrice,
                        sourceOrder.StopPrice,
                        "ATC Copy");

                    row.Account.Submit(new[] { copiedOrder });
                    if (reduceOnlyMode)
                        ApplyLockedVirtualFill(row, sourceOrder.Instrument, sourceOrder.OrderAction, quantityToSubmit);

                    ApplyMaxNetVirtualFill(row, sourceOrder.Instrument, sourceOrder.OrderAction, quantityToSubmit);
                    mirroredTargetQuantities[targetKey] = (reduceOnlyMode && quantityToSubmit < originalQuantity) || maxNetCapped
                        ? desiredQuantity
                        : alreadyMirrored + quantityToSubmit;
                    row.LastAction = "Sent " + DescribeOrder(sourceOrder.OrderAction, quantityToSubmit, sourceOrder.Instrument);
                    Log(row.AccountName + " sent " + DescribeOrder(sourceOrder.OrderAction, quantityToSubmit, sourceOrder.Instrument) + ".");
                }
                catch (Exception ex)
                {
                    row.SetStatus("Error", "Submit failed");
                    row.LastAction = "Submit failed";
                    Log("ERROR " + row.AccountName + " submit failed: " + ex.Message);
                }
            }

            RefreshAllRows();
        }

        private int CalculateDesiredTargetQuantity(AccountCopyRow row, Order sourceOrder)
        {
            return CalculateDesiredQuantityFromBase(row, sourceOrder.Filled);
        }

        private int CalculateDesiredQuantityFromBase(AccountCopyRow row, int baseQuantity)
        {
            int desiredQuantity = 0;

            switch (row.SizingMode)
            {
                case SizingMode.OneToOne:
                    desiredQuantity = baseQuantity;
                    break;
                case SizingMode.Multiplier:
                    desiredQuantity = row.Multiplier > 0
                        ? (int)Math.Floor(baseQuantity * row.Multiplier)
                        : 0;
                    break;
                case SizingMode.Fixed:
                    desiredQuantity = baseQuantity > 0 ? Math.Max(0, row.FixedQuantity) : 0;
                    break;
                case SizingMode.BalanceRatio:
                    desiredQuantity = CalculateBalanceRatioQuantity(row, baseQuantity);
                    break;
                case SizingMode.Disabled:
                    desiredQuantity = 0;
                    break;
            }

            if (row.MaxQuantity > 0)
                desiredQuantity = Math.Min(desiredQuantity, row.MaxQuantity);

            return Math.Max(0, desiredQuantity);
        }

        private string ValidateReadyToStart()
        {
            var activeRows = accountRows.Where(r => r.Enabled && r.SizingMode != SizingMode.Disabled).ToList();

            foreach (var row in activeRows)
            {
                if (row.Account == null)
                    return "Account " + row.AccountName + " has no account.";

                var rowLead = ResolveLeadAccountForRow(row);
                if (rowLead == null)
                    return "Account " + row.AccountName + " needs a connected lead.";

                if (AccountNamesEqual(row.AccountName, rowLead.Name))
                    return "Account " + row.AccountName + " cannot copy from itself.";

                if (IsConfiguredLeadAccount(row.AccountName))
                    return "Account " + row.AccountName + " is also used as a lead by another row.";

                if (row.Account.ConnectionStatus != ConnectionStatus.Connected)
                    return "Account " + row.AccountName + " is disconnected.";

                if (row.SizingMode == SizingMode.Multiplier && row.Multiplier <= 0)
                    return "Account " + row.AccountName + " needs a multiplier greater than 0.";

                if (row.SizingMode == SizingMode.Fixed && row.FixedQuantity <= 0)
                    return "Account " + row.AccountName + " needs a fixed quantity greater than 0.";
            }

            if (activeRows.Any(r => r.SizingMode == SizingMode.BalanceRatio))
            {
                foreach (var row in activeRows.Where(r => r.SizingMode == SizingMode.BalanceRatio))
                {
                    var rowLead = ResolveLeadAccountForRow(row);
                    double leadBalance;
                    if (rowLead == null || !TryGetSizingBalance(rowLead, out leadBalance) || leadBalance <= 0)
                        return "Balance-ratio sizing needs usable lead value data for " + row.AccountName + ".";

                    double followerBalance;
                    if (!TryGetSizingBalance(row.Account, out followerBalance) || followerBalance <= 0)
                        return "Balance-ratio sizing needs usable value data for " + row.AccountName + ".";
                }
            }

            return string.Empty;
        }

        private int CalculateBalanceRatioQuantity(AccountCopyRow row, int sourceFilledQuantity)
        {
            double leadBalance;
            double followerBalance;
            var rowLead = ResolveLeadAccountForRow(row);

            if (rowLead == null || !TryGetSizingBalance(rowLead, out leadBalance) || !TryGetSizingBalance(row.Account, out followerBalance))
            {
                row.SetStatus("Error", "No balance data");
                row.LastAction = "Balance sizing skipped";
                Log(row.AccountName + " skipped balance-ratio sizing because balance data is unavailable.");
                return 0;
            }

            if (leadBalance <= 0 || followerBalance <= 0)
                return 0;

            return (int)Math.Floor(sourceFilledQuantity * followerBalance / leadBalance);
        }

        private int CapLockedQuantityToReducingOnly(AccountCopyRow row, Order sourceOrder, int requestedQuantity)
        {
            var signedPosition = GetLockedVirtualPosition(row, sourceOrder.Instrument);

            if (signedPosition == 0)
                return 0;

            if (IsBuyAction(sourceOrder.OrderAction) && signedPosition < 0)
                return Math.Min(requestedQuantity, Math.Abs(signedPosition));

            if (IsSellAction(sourceOrder.OrderAction) && signedPosition > 0)
                return Math.Min(requestedQuantity, Math.Abs(signedPosition));

            return 0;
        }

        private bool RowIsReduceOnly(AccountCopyRow row)
        {
            return row.IsEntryLocked || row.CopyMode == TradeCopyMode.ExitsOnly;
        }

        private int CapQuantityToMaxNetPosition(AccountCopyRow row, Instrument instrument, OrderAction action, int requestedQuantity)
        {
            if (row.MaxNetPosition <= 0 || requestedQuantity <= 0)
                return requestedQuantity;

            var currentSigned = GetMaxNetVirtualPosition(row, instrument);
            var signedDelta = IsBuyAction(action) ? requestedQuantity : -requestedQuantity;
            var requestedResult = currentSigned + signedDelta;

            if (Math.Abs(requestedResult) <= row.MaxNetPosition)
                return requestedQuantity;

            if (signedDelta > 0)
                return Math.Min(requestedQuantity, Math.Max(0, row.MaxNetPosition - currentSigned));

            return Math.Min(requestedQuantity, Math.Max(0, currentSigned + row.MaxNetPosition));
        }

        private int GetMaxNetVirtualPosition(AccountCopyRow row, Instrument instrument)
        {
            var key = GetAccountInstrumentKey(row, instrument);
            var actualSigned = GetSignedPosition(row.Account, instrument);
            int virtualSigned;

            if (!maxNetVirtualPositions.TryGetValue(key, out virtualSigned))
            {
                maxNetVirtualPositions[key] = actualSigned;
                return actualSigned;
            }

            if (ShouldPreferActualPosition(actualSigned, virtualSigned))
            {
                maxNetVirtualPositions[key] = actualSigned;
                return actualSigned;
            }

            return virtualSigned;
        }

        private bool ShouldPreferActualPosition(int actualSigned, int virtualSigned)
        {
            if (actualSigned == 0)
                return false;

            if (Math.Sign(actualSigned) != Math.Sign(virtualSigned))
                return true;

            return Math.Abs(actualSigned) > Math.Abs(virtualSigned);
        }

        private void ApplyMaxNetVirtualFill(AccountCopyRow row, Instrument instrument, OrderAction action, int quantity)
        {
            if (row.MaxNetPosition <= 0 || quantity <= 0)
                return;

            var key = GetAccountInstrumentKey(row, instrument);
            var signedPosition = GetMaxNetVirtualPosition(row, instrument);
            var signedDelta = IsBuyAction(action) ? quantity : -quantity;
            maxNetVirtualPositions[key] = signedPosition + signedDelta;
        }

        private int GetLockedVirtualPosition(AccountCopyRow row, Instrument instrument)
        {
            var key = GetLockedVirtualPositionKey(row, instrument);
            if (lockedVirtualPositions.ContainsKey(key))
                return lockedVirtualPositions[key];

            var signedPosition = GetSignedPosition(row.Account, instrument);
            lockedVirtualPositions[key] = signedPosition;
            return signedPosition;
        }

        private void ApplyLockedVirtualFill(AccountCopyRow row, Instrument instrument, OrderAction action, int quantity)
        {
            var key = GetLockedVirtualPositionKey(row, instrument);
            var signedPosition = GetLockedVirtualPosition(row, instrument);
            var signedDelta = IsBuyAction(action) ? quantity : -quantity;
            var nextPosition = signedPosition + signedDelta;

            if ((signedPosition > 0 && nextPosition < 0) || (signedPosition < 0 && nextPosition > 0))
                nextPosition = 0;

            lockedVirtualPositions[key] = nextPosition;
        }

        private string GetLockedVirtualPositionKey(AccountCopyRow row, Instrument instrument)
        {
            return GetAccountInstrumentKey(row, instrument);
        }

        private string GetAccountInstrumentKey(AccountCopyRow row, Instrument instrument)
        {
            var instrumentName = instrument != null ? instrument.FullName : string.Empty;
            return row.AccountName + "|" + instrumentName;
        }

        private void ClearLockedVirtualPositions(AccountCopyRow row)
        {
            var prefix = row.AccountName + "|";
            foreach (var key in lockedVirtualPositions.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                lockedVirtualPositions.Remove(key);
        }

        private void ClearMaxNetVirtualPositions(AccountCopyRow row)
        {
            var prefix = row.AccountName + "|";
            foreach (var key in maxNetVirtualPositions.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                maxNetVirtualPositions.Remove(key);
        }

        private bool IsBuyAction(OrderAction action)
        {
            return action == OrderAction.Buy || action == OrderAction.BuyToCover;
        }

        private bool IsSellAction(OrderAction action)
        {
            return action == OrderAction.Sell || action == OrderAction.SellShort;
        }

        private void FlattenFollowersButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = accountRows.Where(r => r.Enabled).ToList();
            if (rows.Count == 0)
            {
                SetStatus("No enabled account rows to flatten.");
                return;
            }

            if (MessageBox.Show("Flatten all enabled account rows?", "Confirm Flatten Enabled", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            LockRowsForManualFlatten(rows, "Manual enabled flatten");
            foreach (var row in rows)
                FlattenAccount(row.Account, "Manual enabled flatten");

            RefreshAllRows();
        }

        private void FlattenSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRows();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to flatten.");
                return;
            }

            if (MessageBox.Show("Flatten selected account rows?", "Confirm Flatten Selected", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            LockRowsForManualFlatten(rows, "Manual selected flatten");
            foreach (var row in rows)
                FlattenAccount(row.Account, "Manual selected flatten");

            RefreshAllRows();
        }

        private void FlattenAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Flatten every table account and configured lead?", "Confirm Flatten All", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            PauseCopyingTrades();

            var accounts = GetConfiguredLeadAccounts()
                .Concat(accountRows.Select(r => r.Account))
                .Where(a => a != null)
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            foreach (var account in accounts)
                FlattenAccount(account, "Manual flatten all");

            RefreshAllRows();
        }

        private void FlattenGroupButton_Click(object sender, RoutedEventArgs e)
        {
            var groupName = GetSelectedGroupName();
            if (string.IsNullOrEmpty(groupName))
            {
                SetStatus("Select a group before flattening.");
                return;
            }

            var rows = accountRows.Where(r => r.Enabled && GroupEquals(r.GroupName, groupName)).ToList();
            if (rows.Count == 0)
            {
                SetStatus("No enabled account rows in group " + groupName + ".");
                return;
            }

            if (MessageBox.Show("Flatten enabled account rows in group " + groupName + "?", "Confirm Flatten Group", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            LockRowsForManualFlatten(rows, "Manual group flatten");
            foreach (var row in rows)
                FlattenAccount(row.Account, "Manual group flatten");

            RefreshAllRows();
        }

        private void LockRowsForManualFlatten(IEnumerable<AccountCopyRow> rows, string lastAction)
        {
            foreach (var row in rows.Where(r => r != null).Distinct().ToList())
            {
                row.ManualLock = true;
                row.LastAction = lastAction + " locked";
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
            }

            Log("Locked " + rows.Count(r => r != null) + " row(s) after flatten request. Entries are blocked; exits remain allowed.");
        }

        private void ReconcileSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRows();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to reconcile.");
                return;
            }

            if (MessageBox.Show("Reconcile selected account rows to each row's lead using its sizing rules?", "Confirm Reconcile Selected", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            foreach (var row in rows)
                ReconcileAccountToLead(row);

            RefreshAllRows();
        }

        private void ReconcileAccountToLead(AccountCopyRow row)
        {
            if (row == null || row.Account == null)
                return;

            if (row.Account.ConnectionStatus != ConnectionStatus.Connected)
            {
                row.SetStatus("Error", "Disconnected");
                row.LastAction = "Reconcile skipped";
                Log(row.AccountName + " reconcile skipped because account is disconnected.");
                return;
            }

            var validationMessage = ValidateRowForReconcile(row);
            if (!string.IsNullOrEmpty(validationMessage))
            {
                row.LastAction = "Reconcile skipped";
                Log(row.AccountName + " reconcile skipped: " + validationMessage);
                return;
            }

            var desiredPositions = BuildDesiredPositions(row);
            var targetPositions = GetOpenPositionSnapshots(row.Account);
            var instrumentNames = desiredPositions.Keys
                .Union(targetPositions.Select(p => p.InstrumentName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var submitted = 0;
            foreach (var instrumentName in instrumentNames)
            {
                var desired = desiredPositions.ContainsKey(instrumentName) ? desiredPositions[instrumentName] : null;
                var target = targetPositions.FirstOrDefault(p => string.Equals(p.InstrumentName, instrumentName, StringComparison.OrdinalIgnoreCase));
                var instrument = desired != null ? desired.Instrument : target != null ? target.Instrument : null;
                if (instrument == null)
                    continue;

                var currentSigned = target != null ? target.SignedQuantity : 0;
                var desiredSigned = desired != null ? desired.SignedQuantity : 0;

                if (RowIsReduceOnly(row))
                    desiredSigned = CalculateLockedReconcileTarget(currentSigned, desiredSigned);

                desiredSigned = CapDesiredSignedPositionToMaxNet(row, desiredSigned);

                var delta = desiredSigned - currentSigned;
                if (delta == 0)
                    continue;

                if (SubmitReconcileAdjustment(row, instrument, delta))
                    submitted++;
            }

            if (IsDryRunSelected())
            {
                row.LastAction = submitted > 0 ? "Dry run reconcile " + submitted + " order(s)" : "Already reconciled";
                Log(row.AccountName + " dry-run reconcile complete; orders simulated: " + submitted + ".");
            }
            else
            {
                row.LastAction = submitted > 0 ? "Reconcile sent " + submitted + " order(s)" : "Already reconciled";
                Log(row.AccountName + " reconcile complete; orders sent: " + submitted + ".");
            }
        }

        private string ValidateRowForReconcile(AccountCopyRow row)
        {
            if (!row.Enabled || row.SizingMode == SizingMode.Disabled)
                return "row is disabled";

            if (row.SizingMode == SizingMode.Multiplier && row.Multiplier <= 0)
                return "multiplier must be greater than 0";

            if (row.SizingMode == SizingMode.Fixed && row.FixedQuantity <= 0)
                return "fixed quantity must be greater than 0";

            var rowLead = ResolveLeadAccountForRow(row);
            if (rowLead == null)
                return "lead is not connected";

            if (AccountNamesEqual(row.AccountName, rowLead.Name))
                return "row account is also the lead";

            if (row.SizingMode == SizingMode.BalanceRatio)
            {
                double leadBalance;
                double followerBalance;
                if (!TryGetSizingBalance(rowLead, out leadBalance) || leadBalance <= 0)
                    return "lead balance data is unavailable";

                if (!TryGetSizingBalance(row.Account, out followerBalance) || followerBalance <= 0)
                    return "account balance data is unavailable";
            }

            return string.Empty;
        }

        private Dictionary<string, PositionSnapshot> BuildDesiredPositions(AccountCopyRow row)
        {
            var desiredPositions = new Dictionary<string, PositionSnapshot>(StringComparer.OrdinalIgnoreCase);
            var rowLead = ResolveLeadAccountForRow(row);
            if (rowLead == null)
                return desiredPositions;

            foreach (var leadPosition in GetOpenPositionSnapshots(rowLead))
            {
                if (!RowAllowsInstrument(row, leadPosition.Instrument))
                    continue;

                var quantity = CalculateDesiredQuantityFromBase(row, leadPosition.Quantity);
                if (quantity <= 0)
                    continue;

                desiredPositions[leadPosition.InstrumentName] = new PositionSnapshot
                {
                    Instrument = leadPosition.Instrument,
                    InstrumentName = leadPosition.InstrumentName,
                    MarketPosition = leadPosition.MarketPosition,
                    Quantity = quantity
                };
            }

            return desiredPositions;
        }

        private int CalculateLockedReconcileTarget(int currentSigned, int desiredSigned)
        {
            if (currentSigned == 0)
                return 0;

            if (currentSigned > 0)
                return desiredSigned > 0 ? Math.Min(currentSigned, desiredSigned) : 0;

            return desiredSigned < 0 ? Math.Max(currentSigned, desiredSigned) : 0;
        }

        private int CapDesiredSignedPositionToMaxNet(AccountCopyRow row, int desiredSigned)
        {
            if (row.MaxNetPosition <= 0)
                return desiredSigned;

            return Math.Max(-row.MaxNetPosition, Math.Min(row.MaxNetPosition, desiredSigned));
        }

        private bool SubmitReconcileAdjustment(AccountCopyRow row, Instrument instrument, int signedDelta)
        {
            var action = signedDelta > 0 ? OrderAction.Buy : OrderAction.Sell;
            var requestedQuantity = Math.Abs(signedDelta);
            var quantity = CapQuantityToMaxNetPosition(row, instrument, action, requestedQuantity);

            if (quantity <= 0)
            {
                row.LastAction = "Reconcile blocked by max net";
                Log(row.AccountName + " reconcile blocked " + GetInstrumentName(instrument) + " because max net position would be exceeded.");
                return false;
            }

            if (quantity < requestedQuantity)
                Log(row.AccountName + " capped reconcile from " + requestedQuantity + " to " + quantity + " by max net position.");

            try
            {
                if (IsDryRunSelected())
                {
                    Log("DRY RUN " + row.AccountName + " would reconcile with " + DescribeOrder(action, quantity, instrument) + ".");
                    ApplyMaxNetVirtualFill(row, instrument, action, quantity);
                    return true;
                }

                var order = CreateAccountOrder(
                    row.Account,
                    instrument,
                    action,
                    OrderType.Market,
                    TimeInForce.Day,
                    quantity,
                    0,
                    0,
                    "ATC Reconcile");

                row.Account.Submit(new[] { order });
                ApplyMaxNetVirtualFill(row, instrument, action, quantity);
                Log(row.AccountName + " reconcile sent " + DescribeOrder(action, quantity, instrument) + ".");
                return true;
            }
            catch (Exception ex)
            {
                row.SetStatus("Error", "Reconcile failed");
                row.LastAction = "Reconcile failed";
                Log("ERROR " + row.AccountName + " reconcile failed: " + ex.Message);
                return false;
            }
        }

        private void FlattenAccount(Account account, string reason)
        {
            if (account == null)
                return;

            try
            {
                CancelActiveOrders(account);
                CloseOpenPositions(account);
                Log(account.Name + " flatten requested: " + reason + ".");
            }
            catch (Exception ex)
            {
                Log("ERROR " + account.Name + " flatten failed: " + ex.Message);
                var row = accountRows.FirstOrDefault(r => r.Account == account);
                if (row != null)
                {
                    row.SetStatus("Error", "Flatten failed");
                    row.LastAction = "Flatten failed";
                }
            }
        }

        private void CancelActiveOrders(Account account)
        {
            Order[] orders;
            lock (account.Orders)
                orders = account.Orders.ToArray();

            foreach (var order in orders)
            {
                if (!IsActiveOrder(order))
                    continue;

                try
                {
                    account.Cancel(new[] { order });
                    Log(account.Name + " canceled " + DescribeOrder(order.OrderAction, order.Quantity, order.Instrument) + ".");
                }
                catch (Exception ex)
                {
                    Log("ERROR " + account.Name + " cancel failed: " + ex.Message);
                }
            }
        }

        private bool IsActiveOrder(Order order)
        {
            return order != null
                && (order.OrderState == OrderState.Accepted
                    || order.OrderState == OrderState.ChangePending
                    || order.OrderState == OrderState.ChangeSubmitted
                    || order.OrderState == OrderState.Submitted
                    || order.OrderState == OrderState.TriggerPending
                    || order.OrderState == OrderState.Working);
        }

        private void CloseOpenPositions(Account account)
        {
            Position[] positions;
            lock (account.Positions)
                positions = account.Positions.ToArray();

            foreach (var position in positions)
            {
                if (position.Quantity == 0 || position.MarketPosition == MarketPosition.Flat)
                    continue;

                var closeAction = position.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                var quantity = Math.Abs(position.Quantity);

                try
                {
                    var closeOrder = CreateAccountOrder(
                        account,
                        position.Instrument,
                        closeAction,
                        OrderType.Market,
                        TimeInForce.Day,
                        quantity,
                        0,
                        0,
                        "ATC Flatten");

                    account.Submit(new[] { closeOrder });
                    MarkFlattenRequested(account, position.Instrument);
                    Log(account.Name + " closing " + DescribeOrder(closeAction, quantity, position.Instrument) + ".");
                }
                catch (Exception ex)
                {
                    Log("ERROR " + account.Name + " close failed: " + ex.Message);
                }
            }
        }

        private void MarkFlattenRequested(Account account, Instrument instrument)
        {
            var row = accountRows.FirstOrDefault(r => r.Account == account);
            if (row == null)
                return;

            var key = GetAccountInstrumentKey(row, instrument);
            lockedVirtualPositions[key] = 0;
            maxNetVirtualPositions[key] = 0;
        }

        private Order CreateAccountOrder(Account account, Instrument instrument, OrderAction action, OrderType orderType, TimeInForce timeInForce, int quantity, double limitPrice, double stopPrice, string name)
        {
            return account.CreateOrder(instrument, action, orderType, OrderEntry.Automated, timeInForce, quantity, limitPrice, stopPrice, string.Empty, name, NinjaTrader.Core.Globals.MaxDate, null);
        }

        private void EnableSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = accountsGrid.SelectedItems.OfType<AccountCopyRow>().ToList();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to enable.");
                return;
            }

            EnableRows(rows, "selected");
        }

        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = accountsGrid.SelectedItems.OfType<AccountCopyRow>().ToList();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to disable.");
                return;
            }

            foreach (var row in rows)
            {
                row.Enabled = false;
                row.ManualLock = false;
                row.AutoLocked = false;
                row.LockReason = string.Empty;
                row.LastAction = "Disabled";
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
            }

            SyncLeadAccountSubscriptions();
            RefreshGroupList();
            RefreshAllRows();
            Log("Disabled " + rows.Count + " row(s).");
        }

        private void UnlockSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrAll();
            foreach (var row in rows)
            {
                row.ManualLock = false;
                row.AutoLocked = false;
                row.LockReason = string.Empty;
                row.LastAction = "Unlocked";
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
            }

            Log("Unlocked " + rows.Count + " row(s).");
            RefreshAllRows();
        }

        private void ResetBaselinesButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrAll();
            foreach (var row in rows)
            {
                row.ResetBaseline(ReadAccountPnl(row.Account));
                row.LastAction = "Baseline reset";
            }

            Log("Reset risk baselines for " + rows.Count + " row(s).");
            RefreshAllRows();
        }

        private void EnableGroupButton_Click(object sender, RoutedEventArgs e)
        {
            var groupName = GetSelectedGroupName();
            if (string.IsNullOrEmpty(groupName))
            {
                SetStatus("Select a group before enabling.");
                return;
            }

            EnableRows(accountRows.Where(r => GroupEquals(r.GroupName, groupName)), "group " + groupName);
        }

        private void EnableRows(IEnumerable<AccountCopyRow> rows, string scopeDescription)
        {
            var targetRows = rows.Where(r => r != null).Distinct().ToList();
            if (targetRows.Count == 0)
            {
                SetStatus("No rows to enable.");
                return;
            }

            var desiredLeadNames = accountRows
                .Where(r => r.Enabled && r.SizingMode != SizingMode.Disabled && !string.IsNullOrWhiteSpace(r.LeadAccountName))
                .Concat(targetRows.Where(r => r.SizingMode != SizingMode.Disabled && !string.IsNullOrWhiteSpace(r.LeadAccountName)))
                .Select(r => r.LeadAccountName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var enabledCount = 0;
            var skipReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in targetRows)
            {
                string skipReason;
                if (!TryEnableRow(row, desiredLeadNames, out skipReason))
                {
                    row.LastAction = "Enable skipped: " + skipReason;
                    IncrementReason(skipReasons, skipReason);
                    continue;
                }

                enabledCount++;
            }

            SyncLeadAccountSubscriptions();
            RefreshAllRows();
            var message = "Enabled " + enabledCount + " " + scopeDescription + " row(s)" + FormatSkipReasons(skipReasons) + ".";
            SetStatus(message);
            Log(message);
        }

        private bool TryEnableRow(AccountCopyRow row, List<string> desiredLeadNames, out string skipReason)
        {
            skipReason = string.Empty;
            if (row == null || row.Account == null)
            {
                skipReason = "no account";
                return false;
            }

            if (row.Account.ConnectionStatus != ConnectionStatus.Connected)
            {
                skipReason = "disconnected";
                return false;
            }

            if (row.SizingMode == SizingMode.Disabled)
            {
                skipReason = "sizing disabled";
                return false;
            }

            if (string.IsNullOrWhiteSpace(row.LeadAccountName))
            {
                skipReason = "no lead";
                return false;
            }

            var rowLead = ResolveLeadAccountForRow(row);
            if (rowLead == null)
            {
                skipReason = "lead disconnected";
                return false;
            }

            if (AccountNamesEqual(row.AccountName, rowLead.Name))
            {
                skipReason = "self-copy";
                return false;
            }

            if (desiredLeadNames.Any(leadName => AccountNamesEqual(leadName, row.AccountName)))
            {
                skipReason = "used as lead";
                return false;
            }

            if (row.SizingMode == SizingMode.Multiplier && row.Multiplier <= 0)
            {
                skipReason = "bad multiplier";
                return false;
            }

            if (row.SizingMode == SizingMode.Fixed && row.FixedQuantity <= 0)
            {
                skipReason = "bad fixed qty";
                return false;
            }

            row.Enabled = true;
            row.ManualLock = false;
            row.AutoLocked = false;
            row.LockReason = string.Empty;
            row.LastAction = "Enabled";
            ClearLockedVirtualPositions(row);
            ClearMaxNetVirtualPositions(row);
            return true;
        }

        private void IncrementReason(Dictionary<string, int> skipReasons, string reason)
        {
            if (skipReasons.ContainsKey(reason))
                skipReasons[reason]++;
            else
                skipReasons[reason] = 1;
        }

        private string FormatSkipReasons(Dictionary<string, int> skipReasons)
        {
            if (skipReasons.Count == 0)
                return string.Empty;

            var parts = skipReasons
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value + " " + pair.Key)
                .ToList();

            return "; skipped " + skipReasons.Values.Sum() + " (" + string.Join(", ", parts) + ")";
        }

        private void PauseGroupButton_Click(object sender, RoutedEventArgs e)
        {
            var groupName = GetSelectedGroupName();
            if (string.IsNullOrEmpty(groupName))
            {
                SetStatus("Select a group before pausing.");
                return;
            }

            var rows = accountRows.Where(r => r.Enabled && GroupEquals(r.GroupName, groupName)).ToList();
            if (rows.Count == 0)
            {
                SetStatus("No enabled account rows in group " + groupName + ".");
                return;
            }

            foreach (var row in rows)
            {
                row.ManualLock = true;
                row.LastAction = "Group paused";
            }

            Log("Paused " + rows.Count + " row(s) in group " + groupName + ". Entries are blocked; exits remain allowed.");
            RefreshAllRows();
        }

        private void ApplyGroupSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var source = accountsGrid.SelectedItem as AccountCopyRow;
            if (source == null)
            {
                SetStatus("Select a row whose settings should be copied to its group.");
                return;
            }

            if (string.IsNullOrWhiteSpace(source.LeadAccountName))
            {
                SetStatus("Choose a lead on the selected row before applying it to the group.");
                return;
            }

            if (AccountNamesEqual(source.AccountName, source.LeadAccountName))
            {
                SetStatus("The selected row cannot copy from itself.");
                return;
            }

            var rows = accountRows.Where(r => r != source && GroupEquals(r.GroupName, source.GroupName)).ToList();
            var appliedCount = 0;
            var skippedLeadRows = 0;
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(source.LeadAccountName) && AccountNamesEqual(row.AccountName, source.LeadAccountName))
                {
                    row.LeadAccountName = string.Empty;
                    row.Enabled = false;
                    row.LastAction = "Lead row skipped";
                    ClearLockedVirtualPositions(row);
                    ClearMaxNetVirtualPositions(row);
                    skippedLeadRows++;
                    continue;
                }

                row.LeadAccountName = source.LeadAccountName;
                row.SizingMode = source.SizingMode;
                row.CopyMode = source.CopyMode;
                row.Multiplier = source.Multiplier;
                row.FixedQuantity = source.FixedQuantity;
                row.MaxQuantity = source.MaxQuantity;
                row.MaxNetPosition = source.MaxNetPosition;
                row.DailyLossLimit = source.DailyLossLimit;
                row.MaxDrawdown = source.MaxDrawdown;
                row.ProfitTarget = source.ProfitTarget;
                row.LimitAction = source.LimitAction;
                row.InstrumentFilter = source.InstrumentFilter;
                row.LastAction = "Group settings applied";
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
                appliedCount++;
            }

            mirroredTargetQuantities.Clear();
            SyncLeadAccountSubscriptions();
            Log("Applied " + source.AccountName + " settings to " + appliedCount + " row(s) in group " + source.GroupName + (skippedLeadRows > 0 ? "; skipped " + skippedLeadRows + " lead row(s)." : "."));
            RefreshAllRows();
        }

        private List<AccountCopyRow> GetSelectedRows()
        {
            return accountsGrid.SelectedItems.OfType<AccountCopyRow>().ToList();
        }

        private List<AccountCopyRow> GetSelectedRowsOrAll()
        {
            var selected = accountsGrid.SelectedItems.OfType<AccountCopyRow>().ToList();
            return selected.Count > 0 ? selected : accountRows.ToList();
        }

        private void TelemetryTimer_Tick(object sender, EventArgs e)
        {
            SyncLeadAccountSubscriptions();
            RefreshAllRows();
            RefreshGroupList();
        }

        private void RefreshAllRows()
        {
            foreach (var row in accountRows.ToList())
            {
                if (!RowIsReduceOnly(row))
                    ClearLockedVirtualPositions(row);

                if (!isCopying)
                    ClearMaxNetVirtualPositions(row);

                RefreshRowMetrics(row);
                EvaluateRisk(row);
                UpdateRowStatus(row);
                UpdateRowRole(row);
            }

            SetStatus(BuildSummaryStatus());
        }

        private void RefreshRowMetrics(AccountCopyRow row)
        {
            if (row.Account == null)
                return;

            row.ConnectionStatus = row.Account.ConnectionStatus.ToString();
            var currentPnl = ReadAccountPnl(row.Account);
            row.SessionPnl = currentPnl - row.BaselinePnl;
            row.PeakPnl = Math.Max(row.PeakPnl, row.SessionPnl);
            row.Drawdown = Math.Max(0, row.PeakPnl - row.SessionPnl);
            row.NetPosition = GetNetPosition(row.Account);
            row.PositionSummary = GetPositionSummary(row.Account);
        }

        private void EvaluateRisk(AccountCopyRow row)
        {
            if (row.AutoLocked || row.Account == null || !row.Enabled || row.SizingMode == SizingMode.Disabled)
                return;

            if (row.DailyLossLimit > 0 && row.SessionPnl <= -Math.Abs(row.DailyLossLimit))
            {
                TriggerRiskLock(row, "Daily loss limit");
                return;
            }

            if (row.MaxDrawdown > 0 && row.Drawdown >= Math.Abs(row.MaxDrawdown))
            {
                TriggerRiskLock(row, "Drawdown limit");
                return;
            }

            if (row.ProfitTarget > 0 && row.SessionPnl >= Math.Abs(row.ProfitTarget))
                TriggerRiskLock(row, "Profit target");
        }

        private void TriggerRiskLock(AccountCopyRow row, string reason)
        {
            row.AutoLocked = true;
            row.ManualLock = false;
            row.LockReason = reason;

            if (row.LimitAction == RiskAction.HardFlatten)
            {
                row.LastAction = reason + " - hard flatten";
                Log(row.AccountName + " hard locked by " + reason + "; flatten requested.");
                FlattenAccount(row.Account, reason);
            }
            else
            {
                row.LastAction = reason + " - soft lock";
                Log(row.AccountName + " soft locked by " + reason + ". Entries blocked; exits allowed.");
            }
        }

        private void UpdateRowStatus(AccountCopyRow row)
        {
            if (row.Account == null)
            {
                row.SetStatus("Error", "No account");
                return;
            }

            if (row.Account.ConnectionStatus != ConnectionStatus.Connected)
            {
                row.SetStatus("Error", "Disconnected");
                return;
            }

            if (!row.Enabled || row.SizingMode == SizingMode.Disabled)
            {
                row.SetStatus("Disabled", "Disabled");
                return;
            }

            var rowLead = ResolveLeadAccountForRow(row);
            if (rowLead == null)
            {
                row.SetStatus("Error", "Lead missing");
                return;
            }

            if (AccountNamesEqual(row.AccountName, rowLead.Name) || IsConfiguredLeadAccount(row.AccountName))
            {
                row.SetStatus("Error", "Also used as lead");
                return;
            }

            if (row.AutoLocked)
            {
                row.SetStatus("Locked", string.IsNullOrEmpty(row.LockReason) ? "Auto locked" : row.LockReason);
                return;
            }

            if (row.ManualLock)
            {
                row.SetStatus("Locked", "Manual lock - exits only");
                return;
            }

            if (IsPotentiallyDesynced(row))
            {
                row.SetStatus("Desynced", "Check position sync");
                return;
            }

            if (row.CopyMode == TradeCopyMode.ExitsOnly)
            {
                row.SetStatus("ExitsOnly", isCopying ? "Exits only" : "Ready exits only");
                return;
            }

            if (IsNearRiskLimit(row))
            {
                row.SetStatus("Warning", "Near risk limit");
                return;
            }

            row.SetStatus(isCopying ? "Active" : "Ready", isCopying ? "Copying" : "Ready");
        }

        private void UpdateRowRole(AccountCopyRow row)
        {
            if (row.Account == null || row.Account.ConnectionStatus != ConnectionStatus.Connected)
            {
                row.RoleSummary = "Offline";
                return;
            }

            var usedAsLead = IsConfiguredLeadAccount(row.AccountName);
            var activeCopyRow = row.Enabled && row.SizingMode != SizingMode.Disabled;

            if (activeCopyRow && usedAsLead)
            {
                row.RoleSummary = "Conflict";
                return;
            }

            if (activeCopyRow)
            {
                row.RoleSummary = "Copy row";
                return;
            }

            row.RoleSummary = usedAsLead ? "Lead" : "Available";
        }

        private bool IsNearRiskLimit(AccountCopyRow row)
        {
            return (row.DailyLossLimit > 0 && row.SessionPnl <= -Math.Abs(row.DailyLossLimit) * 0.9)
                || (row.MaxDrawdown > 0 && row.Drawdown >= Math.Abs(row.MaxDrawdown) * 0.9)
                || (row.ProfitTarget > 0 && row.SessionPnl >= Math.Abs(row.ProfitTarget) * 0.9);
        }

        private bool IsPotentiallyDesynced(AccountCopyRow row)
        {
            var rowLead = ResolveLeadAccountForRow(row);
            if (!isCopying || rowLead == null || row.Account == null || RowIsReduceOnly(row) || !row.Enabled || row.SizingMode == SizingMode.Disabled)
                return false;

            var leadPositions = GetOpenPositionSnapshots(rowLead);
            var targetPositions = GetOpenPositionSnapshots(row.Account);

            if (leadPositions.Count == 0)
                return targetPositions.Count > 0;

            foreach (var leadPosition in leadPositions)
            {
                var targetPosition = targetPositions.FirstOrDefault(p => p.InstrumentName == leadPosition.InstrumentName);
                if (targetPosition == null || targetPosition.MarketPosition != leadPosition.MarketPosition)
                    return true;
            }

            return false;
        }

        private int GetNetPosition(Account account)
        {
            return GetOpenPositionSnapshots(account).Sum(p => p.SignedQuantity);
        }

        private string GetPositionSummary(Account account)
        {
            var positions = GetOpenPositionSnapshots(account);
            if (positions.Count == 0)
                return "Flat";

            return string.Join(", ", positions.Select(p => p.InstrumentName + " " + Math.Abs(p.Quantity) + (p.MarketPosition == MarketPosition.Long ? "L" : "S")));
        }

        private List<PositionSnapshot> GetOpenPositionSnapshots(Account account)
        {
            var snapshots = new List<PositionSnapshot>();
            if (account == null)
                return snapshots;

            Position[] positions;
            lock (account.Positions)
                positions = account.Positions.ToArray();

            foreach (var position in positions)
            {
                if (position.Quantity == 0 || position.MarketPosition == MarketPosition.Flat)
                    continue;

                snapshots.Add(new PositionSnapshot
                {
                    Instrument = position.Instrument,
                    InstrumentName = position.Instrument != null ? position.Instrument.FullName : string.Empty,
                    MarketPosition = position.MarketPosition,
                    Quantity = Math.Abs(position.Quantity)
                });
            }

            return snapshots;
        }

        private int GetSignedPosition(Account account, Instrument instrument)
        {
            var position = GetOpenPositionSnapshots(account).FirstOrDefault(p => InstrumentsMatch(p.Instrument, instrument));
            if (position == null)
                return 0;

            return position.SignedQuantity;
        }

        private bool InstrumentsMatch(Instrument left, Instrument right)
        {
            if (left == null || right == null)
                return false;

            return left == right || left.FullName == right.FullName;
        }

        private bool TryGetSizingBalance(Account account, out double balance)
        {
            balance = 0;
            if (account == null)
                return false;

            if (TryReadAccountValue(account, AccountItem.NetLiquidation, out balance) && balance > 0)
                return true;

            return TryReadAccountValue(account, AccountItem.CashValue, out balance) && balance > 0;
        }

        private double ReadAccountPnl(Account account)
        {
            if (account == null)
                return 0;

            double realized;
            double unrealized;
            TryReadAccountValue(account, AccountItem.RealizedProfitLoss, out realized);
            TryReadAccountValue(account, AccountItem.UnrealizedProfitLoss, out unrealized);
            return realized + unrealized;
        }

        private bool TryReadAccountValue(Account account, AccountItem item, out double value)
        {
            value = 0;
            try
            {
                value = Convert.ToDouble(account.Get(item, Currency.UsDollar));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string GetTargetMirrorKey(Order sourceOrder, AccountCopyRow row)
        {
            return GetSourceOrderKey(sourceOrder) + "|" + row.AccountName;
        }

        private string GetSourceOrderKey(Order order)
        {
            var accountName = order.Account != null ? order.Account.Name : string.Empty;
            var instrumentName = order.Instrument != null ? order.Instrument.FullName : string.Empty;
            if (!string.IsNullOrEmpty(order.OrderId))
                return accountName + "|" + instrumentName + "|" + order.OrderId;

            var orderName = string.IsNullOrEmpty(order.Name) ? "no-name" : order.Name;
            return accountName + "|" + instrumentName + "|" + orderName + "|" + order.Time.Ticks;
        }

        private string DescribeOrder(OrderAction action, int quantity, Instrument instrument)
        {
            var instrumentName = instrument != null ? instrument.FullName : "Unknown";
            return action + " " + quantity + " " + instrumentName;
        }

        private bool RowAllowsInstrument(AccountCopyRow row, Instrument instrument)
        {
            var filters = ParseInstrumentFilter(row.InstrumentFilter);
            if (filters.Count == 0)
                return true;

            var fullName = GetInstrumentName(instrument);
            var rootName = GetInstrumentRootName(instrument);

            return filters.Any(filter =>
                string.Equals(filter, fullName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(filter, rootName, StringComparison.OrdinalIgnoreCase));
        }

        private List<string> ParseInstrumentFilter(string filterText)
        {
            if (string.IsNullOrWhiteSpace(filterText))
                return new List<string>();

            return filterText
                .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim())
                .Where(token => token.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private string GetInstrumentName(Instrument instrument)
        {
            return instrument != null ? instrument.FullName : string.Empty;
        }

        private string GetInstrumentRootName(Instrument instrument)
        {
            var fullName = GetInstrumentName(instrument);
            if (string.IsNullOrWhiteSpace(fullName))
                return string.Empty;

            return fullName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? fullName;
        }

        private string NormalizeGroupName(string groupName)
        {
            return string.IsNullOrWhiteSpace(groupName) ? DefaultGroupName : groupName.Trim();
        }

        private string GetSelectedGroupName()
        {
            return groupComboBox.SelectedItem as string;
        }

        private bool GroupEquals(string left, string right)
        {
            return string.Equals(NormalizeGroupName(left), NormalizeGroupName(right), StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshGroupList()
        {
            var groups = accountRows
                .Select(r => NormalizeGroupName(r.GroupName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g)
                .ToList();

            var signature = string.Join("|", groups);
            if (signature == lastGroupListSignature)
                return;

            var selected = GetSelectedGroupName();
            groupComboBox.ItemsSource = groups;
            if (!string.IsNullOrEmpty(selected) && groups.Any(g => GroupEquals(g, selected)))
                groupComboBox.SelectedItem = groups.First(g => GroupEquals(g, selected));
            else if (groups.Count > 0)
                groupComboBox.SelectedIndex = 0;

            lastGroupListSignature = signature;
        }

        private string BuildSummaryStatus()
        {
            var entryActiveCount = accountRows.Count(r => r.Enabled && r.SizingMode != SizingMode.Disabled && !RowIsReduceOnly(r) && r.Account != null && r.Account.ConnectionStatus == ConnectionStatus.Connected);
            var exitsOnlyCount = accountRows.Count(r => r.Enabled && r.SizingMode != SizingMode.Disabled && RowIsReduceOnly(r) && r.Account != null && r.Account.ConnectionStatus == ConnectionStatus.Connected);
            var lockedCount = accountRows.Count(r => r.IsEntryLocked);
            var errorCount = accountRows.Count(r => r.StatusLevel == "Error" || r.StatusLevel == "Desynced");
            var mode = isCopying ? dryRunMode ? "Dry Run" : "Copying" : "Paused";
            var summary = mode + " | Entries active: " + entryActiveCount + " | Exits only: " + exitsOnlyCount + " | Locked: " + lockedCount + " | Attention: " + errorCount;
            var selectionSummary = BuildSelectionSummary();
            return string.IsNullOrEmpty(selectionSummary) ? summary : summary + " | " + selectionSummary;
        }

        private string BuildSelectionSummary()
        {
            if (accountsGrid == null)
                return string.Empty;

            var rows = accountsGrid.SelectedItems.OfType<AccountCopyRow>().ToList();
            if (rows.Count == 0)
                return string.Empty;

            if (rows.Count > 1)
                return "Selected: " + rows.Count + " rows";

            var row = rows[0];
            var lead = string.IsNullOrWhiteSpace(row.LeadAccountName) ? "no lead" : row.LeadAccountName;
            var sizing = DescribeSizing(row);
            var risk = DescribeRisk(row);
            return "Selected: " + row.AccountName + " <- " + lead + " | " + sizing + " | " + risk;
        }

        private string DescribeSizing(AccountCopyRow row)
        {
            switch (row.SizingMode)
            {
                case SizingMode.Multiplier:
                    return "x" + row.Multiplier.ToString("0.##", CultureInfo.InvariantCulture);
                case SizingMode.Fixed:
                    return "fixed " + row.FixedQuantity.ToString(CultureInfo.InvariantCulture);
                case SizingMode.BalanceRatio:
                    return "balance ratio";
                case SizingMode.Disabled:
                    return "sizing disabled";
                default:
                    return "1:1";
            }
        }

        private string DescribeRisk(AccountCopyRow row)
        {
            var parts = new List<string>();
            if (row.DailyLossLimit > 0)
                parts.Add("loss " + row.DailyLossLimit.ToString("0", CultureInfo.InvariantCulture));

            if (row.MaxDrawdown > 0)
                parts.Add("DD " + row.MaxDrawdown.ToString("0", CultureInfo.InvariantCulture));

            if (row.ProfitTarget > 0)
                parts.Add("target " + row.ProfitTarget.ToString("0", CultureInfo.InvariantCulture));

            return parts.Count == 0 ? "no risk limits" : string.Join(", ", parts);
        }

        private void SetStatus(string message)
        {
            if (statusTextBlock != null)
                statusTextBlock.Text = message;
        }

        private void Log(string message)
        {
            if (eventLogTextBox == null)
                return;

            eventLogLines.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            while (eventLogLines.Count > MaxEventLogLines)
                eventLogLines.Dequeue();

            eventLogTextBox.Text = string.Join(Environment.NewLine, eventLogLines.ToArray());
            eventLogTextBox.AppendText(Environment.NewLine);
            eventLogTextBox.ScrollToEnd();
        }

        private bool IsDryRunSelected()
        {
            return dryRunMode || (dryRunCheckBox != null && dryRunCheckBox.IsChecked == true);
        }

        private void TradeCopierWindow_Closing(object sender, CancelEventArgs e)
        {
            PauseCopyingTrades(true);
            UnsubscribeAllLeadAccounts();
            accountRows.CollectionChanged -= AccountRows_CollectionChanged;
            foreach (var row in accountRows)
                row.PropertyChanged -= AccountRow_PropertyChanged;

            if (accountsGrid != null)
                accountsGrid.SelectionChanged -= AccountsGrid_SelectionChanged;

            Account.AccountStatusUpdate -= OnAccountStatusUpdate;

            if (telemetryTimer != null)
            {
                telemetryTimer.Stop();
                telemetryTimer.Tick -= TelemetryTimer_Tick;
            }
        }

        private class PositionSnapshot
        {
            public Instrument Instrument { get; set; }
            public string InstrumentName { get; set; }
            public MarketPosition MarketPosition { get; set; }
            public int Quantity { get; set; }

            public int SignedQuantity
            {
                get
                {
                    if (MarketPosition == MarketPosition.Long)
                        return Quantity;
                    if (MarketPosition == MarketPosition.Short)
                        return -Quantity;
                    return 0;
                }
            }
        }

        private class AccountCopyRow : INotifyPropertyChanged
        {
            private bool enabled;
            private string leadAccountName = string.Empty;
            private string groupName;
            private TradeCopyMode copyMode = TradeCopyMode.All;
            private string connectionStatus = "Unknown";
            private string status = "Ready";
            private string statusLevel = "Ready";
            private string roleSummary = "Available";
            private string positionSummary = "Flat";
            private string instrumentFilter = string.Empty;
            private double sessionPnl;
            private double drawdown;
            private double peakPnl;
            private int netPosition;
            private SizingMode sizingMode = SizingMode.OneToOne;
            private double multiplier = DefaultMultiplier;
            private int fixedQuantity = DefaultFixedQuantity;
            private int maxQuantity;
            private int maxNetPosition;
            private double dailyLossLimit;
            private double maxDrawdown;
            private double profitTarget;
            private RiskAction limitAction = RiskAction.SoftLock;
            private bool manualLock;
            private bool autoLocked;
            private string lockReason = string.Empty;
            private string lastAction = "Ready";

            public AccountCopyRow(Account account, string groupName, double baselinePnl)
            {
                Account = account;
                AccountName = account != null ? account.Name : string.Empty;
                this.groupName = groupName;
                BaselinePnl = baselinePnl;
                PeakPnl = 0;
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public Account Account { get; private set; }
            public string AccountName { get; private set; }
            public double BaselinePnl { get; private set; }

            public void RefreshAccount(Account account)
            {
                Account = account;
                AccountName = account != null ? account.Name : string.Empty;
                OnPropertyChanged("AccountName");
            }

            public bool Enabled
            {
                get { return enabled; }
                set { SetField(ref enabled, value, "Enabled"); }
            }

            public string LeadAccountName
            {
                get { return leadAccountName; }
                set { SetField(ref leadAccountName, value == null ? string.Empty : value.Trim(), "LeadAccountName"); }
            }

            public string GroupName
            {
                get { return groupName; }
                set { SetField(ref groupName, string.IsNullOrWhiteSpace(value) ? DefaultGroupName : value.Trim(), "GroupName"); }
            }

            public TradeCopyMode CopyMode
            {
                get { return copyMode; }
                set { SetField(ref copyMode, value, "CopyMode"); }
            }

            public string ConnectionStatus
            {
                get { return connectionStatus; }
                set { SetField(ref connectionStatus, value, "ConnectionStatus"); }
            }

            public string Status
            {
                get { return status; }
                private set { SetField(ref status, value, "Status"); }
            }

            public string StatusLevel
            {
                get { return statusLevel; }
                private set { SetField(ref statusLevel, value, "StatusLevel"); }
            }

            public string RoleSummary
            {
                get { return roleSummary; }
                set { SetField(ref roleSummary, value, "RoleSummary"); }
            }

            public string PositionSummary
            {
                get { return positionSummary; }
                set { SetField(ref positionSummary, value, "PositionSummary"); }
            }

            public string InstrumentFilter
            {
                get { return instrumentFilter; }
                set { SetField(ref instrumentFilter, value == null ? string.Empty : value.Trim(), "InstrumentFilter"); }
            }

            public double SessionPnl
            {
                get { return sessionPnl; }
                set { SetField(ref sessionPnl, value, "SessionPnl"); }
            }

            public double Drawdown
            {
                get { return drawdown; }
                set { SetField(ref drawdown, value, "Drawdown"); }
            }

            public double PeakPnl
            {
                get { return peakPnl; }
                set { SetField(ref peakPnl, value, "PeakPnl"); }
            }

            public int NetPosition
            {
                get { return netPosition; }
                set { SetField(ref netPosition, value, "NetPosition"); }
            }

            public SizingMode SizingMode
            {
                get { return sizingMode; }
                set { SetField(ref sizingMode, value, "SizingMode"); }
            }

            public double Multiplier
            {
                get { return multiplier; }
                set { SetField(ref multiplier, Math.Max(0, value), "Multiplier"); }
            }

            public int FixedQuantity
            {
                get { return fixedQuantity; }
                set { SetField(ref fixedQuantity, Math.Max(0, value), "FixedQuantity"); }
            }

            public int MaxQuantity
            {
                get { return maxQuantity; }
                set { SetField(ref maxQuantity, Math.Max(0, value), "MaxQuantity"); }
            }

            public int MaxNetPosition
            {
                get { return maxNetPosition; }
                set { SetField(ref maxNetPosition, Math.Max(0, value), "MaxNetPosition"); }
            }

            public double DailyLossLimit
            {
                get { return dailyLossLimit; }
                set { SetField(ref dailyLossLimit, Math.Max(0, value), "DailyLossLimit"); }
            }

            public double MaxDrawdown
            {
                get { return maxDrawdown; }
                set { SetField(ref maxDrawdown, Math.Max(0, value), "MaxDrawdown"); }
            }

            public double ProfitTarget
            {
                get { return profitTarget; }
                set { SetField(ref profitTarget, Math.Max(0, value), "ProfitTarget"); }
            }

            public RiskAction LimitAction
            {
                get { return limitAction; }
                set { SetField(ref limitAction, value, "LimitAction"); }
            }

            public bool ManualLock
            {
                get { return manualLock; }
                set
                {
                    if (SetField(ref manualLock, value, "ManualLock"))
                        OnPropertyChanged("IsEntryLocked");
                }
            }

            public bool AutoLocked
            {
                get { return autoLocked; }
                set
                {
                    if (SetField(ref autoLocked, value, "AutoLocked"))
                        OnPropertyChanged("IsEntryLocked");
                }
            }

            public string LockReason
            {
                get { return lockReason; }
                set { SetField(ref lockReason, value ?? string.Empty, "LockReason"); }
            }

            public string LastAction
            {
                get { return lastAction; }
                set { SetField(ref lastAction, value, "LastAction"); }
            }

            public bool IsEntryLocked
            {
                get { return ManualLock || AutoLocked; }
            }

            public void ResetBaseline(double baselinePnl)
            {
                BaselinePnl = baselinePnl;
                PeakPnl = 0;
                SessionPnl = 0;
                Drawdown = 0;
                AutoLocked = false;
                LockReason = string.Empty;
                OnPropertyChanged("BaselinePnl");
            }

            public void SetStatus(string level, string text)
            {
                StatusLevel = level;
                Status = text;
            }

            private bool SetField<T>(ref T field, T value, string propertyName)
            {
                if (EqualityComparer<T>.Default.Equals(field, value))
                    return false;

                field = value;
                OnPropertyChanged(propertyName);
                return true;
            }

            private void OnPropertyChanged(string propertyName)
            {
                var handler = PropertyChanged;
                if (handler != null)
                    handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
