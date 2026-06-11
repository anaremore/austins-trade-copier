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
using System.Windows.Input;
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

        private enum ReconcileOutcome
        {
            Processed,
            SkippedOffline,
            SkippedRisk,
            SkippedInvalid
        }

        private const int DefaultFixedQuantity = 1;
        private const double DefaultMultiplier = 1.0;
        private const string ProfileFolderName = "AustinTradeCopier";
        private const string ProfileFileExtension = ".xml";
        private const int MaxEventLogLines = 500;
        private const int StatusMessageHoldSeconds = 6;

        private readonly ObservableCollection<AccountCopyRow> accountRows = new ObservableCollection<AccountCopyRow>();
        private readonly ObservableCollection<string> connectedAccountNames = new ObservableCollection<string>();
        private readonly Dictionary<string, int> mirroredTargetQuantities = new Dictionary<string, int>();
        private readonly Dictionary<string, int> lockedVirtualPositions = new Dictionary<string, int>();
        private readonly Dictionary<string, int> maxNetVirtualPositions = new Dictionary<string, int>();
        private readonly Dictionary<string, Account> subscribedLeadAccounts = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<AccountCopyRow> observedAccountRows = new HashSet<AccountCopyRow>();
        private readonly Queue<string> eventLogLines = new Queue<string>();
        private readonly List<EnumOption> copyModeOptions = new List<EnumOption>
        {
            new EnumOption(TradeCopyMode.All, "All"),
            new EnumOption(TradeCopyMode.ExitsOnly, "Exits only")
        };
        private readonly List<EnumOption> sizingModeOptions = new List<EnumOption>
        {
            new EnumOption(SizingMode.OneToOne, "1:1"),
            new EnumOption(SizingMode.Multiplier, "Multiplier"),
            new EnumOption(SizingMode.Fixed, "Fixed qty"),
            new EnumOption(SizingMode.BalanceRatio, "Balance ratio"),
            new EnumOption(SizingMode.Disabled, "Off")
        };
        private readonly List<EnumOption> limitActionOptions = new List<EnumOption>
        {
            new EnumOption(RiskAction.SoftLock, "Lock entries"),
            new EnumOption(RiskAction.HardFlatten, "Auto close")
        };
        private readonly DispatcherTimer telemetryTimer;

        private List<Account> connectedAccounts = new List<Account>();
        private bool isCopying;
        private bool dryRunMode;
        private bool suppressEnableValidation;
        private bool suppressLiveSettingsPause;
        private bool suppressManualLockHandling;
        private bool rowRefreshPending;
        private string heldStatusMessage = string.Empty;
        private DateTime heldStatusUntil = DateTime.MinValue;

        private ComboBox profileComboBox;
        private TextBox profileNameTextBox;
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

            var saveProfileButton = CreateButton("Save Profile", Brushes.DimGray, "Save the current table setup, including disabled rows, per-row leads, sizing, and risk settings.");
            saveProfileButton.Click += SaveProfileButton_Click;
            profilePanel.Children.Add(saveProfileButton);

            var loadProfileButton = CreateButton("Load Profile", Brushes.DimGray, "Load the named profile. Copying must be paused before loading.");
            loadProfileButton.Click += LoadProfileButton_Click;
            profilePanel.Children.Add(loadProfileButton);

            var deleteProfileButton = CreateButton("Delete Profile", Brushes.DimGray, "Delete the named saved profile after confirmation.");
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
            startPauseButton = CreateButton("Start Copying", Brushes.SeaGreen, "Start or pause copying for enabled rows after preflight validation.");
            startPauseButton.Width = 130;
            startPauseButton.Click += StartPauseButton_Click;
            sessionRiskRow.Children.Add(startPauseButton);

            dryRunCheckBox = new CheckBox
            {
                Content = "Dry Run",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                ToolTip = "Simulate copied and reconcile orders without submitting live orders."
            };
            sessionRiskRow.Children.Add(dryRunCheckBox);

            sessionRiskRow.Children.Add(CreateToolbarLabel("Risk"));
            var flattenFollowersButton = CreateButton("Flatten Enabled", Brushes.Firebrick, "Flatten enabled rows' managed positions and manual-lock entries afterward. Symbol filters are respected.");
            flattenFollowersButton.Click += FlattenFollowersButton_Click;
            sessionRiskRow.Children.Add(flattenFollowersButton);

            var flattenSelectedButton = CreateButton("Flatten Selected", Brushes.Firebrick, "Flatten selected rows' managed positions and manual-lock entries afterward. Symbol filters are respected.");
            flattenSelectedButton.Click += FlattenSelectedButton_Click;
            sessionRiskRow.Children.Add(flattenSelectedButton);

            var flattenAllButton = CreateButton("Flatten All", Brushes.DarkRed, "Flatten every table account plus lead accounts used by enabled rows while leaving the copier state unchanged.");
            flattenAllButton.Click += FlattenAllButton_Click;
            sessionRiskRow.Children.Add(flattenAllButton);
            actionPanel.Children.Add(sessionRiskRow);

            var selectionRow = CreateToolbarRow();
            selectionRow.Children.Add(CreateToolbarLabel("Selected Rows"));
            var reconcileSelectedButton = CreateButton("Reconcile Selected", Brushes.DimGray, "Adjust selected rows toward each row's lead positions using current sizing and limits.");
            reconcileSelectedButton.Click += ReconcileSelectedButton_Click;
            selectionRow.Children.Add(reconcileSelectedButton);

            var enableSelectedButton = CreateButton("Enable Selected", Brushes.DimGray, "Enable selected valid rows. Invalid rows are skipped with reasons.");
            enableSelectedButton.Click += EnableSelectedButton_Click;
            selectionRow.Children.Add(enableSelectedButton);

            var removeSelectedButton = CreateButton("Disable Selected", Brushes.DimGray, "Disable selected rows. Rows stay visible and saved in profiles.");
            removeSelectedButton.Click += RemoveSelectedButton_Click;
            selectionRow.Children.Add(removeSelectedButton);

            var unlockSelectedButton = CreateButton("Unlock Selected", Brushes.DimGray, "Clear manual and risk locks on selected rows. Risk-locked rows also reset baselines.");
            unlockSelectedButton.Click += UnlockSelectedButton_Click;
            selectionRow.Children.Add(unlockSelectedButton);

            var resetBaselineButton = CreateButton("Reset Baselines", Brushes.DimGray, "Reset selected rows' session PnL baselines and clear auto risk locks.");
            resetBaselineButton.Click += ResetBaselinesButton_Click;
            selectionRow.Children.Add(resetBaselineButton);

            var copyLeadSettingsButton = CreateButton("Copy Settings To Same Lead", Brushes.DimGray, "Copy mode, sizing, risk limits, limit action, and symbols to rows that use the selected row's lead. Lead selections stay unchanged.");
            copyLeadSettingsButton.Click += CopyLeadSettingsButton_Click;
            selectionRow.Children.Add(copyLeadSettingsButton);
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
                FrozenColumnCount = 6,
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
            accountsGrid.CurrentCellChanged += AccountsGrid_CurrentCellChanged;

            Grid.SetRow(accountsGrid, 2);
            root.Children.Add(accountsGrid);

            statusTextBlock = new TextBlock
            {
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = "Ready. Enable accounts, choose each row's lead, sizing, and risk, then start copying."
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

            var exportLogButton = CreateButton("Export Log", Brushes.DimGray, "Export the current event log to the profile logs folder.");
            exportLogButton.Click += ExportLogButton_Click;
            logButtonPanel.Children.Add(exportLogButton);

            var clearLogButton = CreateButton("Clear Log", Brushes.DimGray, "Clear the visible event log for this window session.");
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

        private Button CreateButton(string text, Brush background, string tooltip = null)
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
                MinHeight = 28,
                ToolTip = tooltip
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

            var selectedTrigger = new Trigger
            {
                Property = DataGridCell.IsSelectedProperty,
                Value = true
            };
            selectedTrigger.Setters.Add(new Setter(DataGridCell.BackgroundProperty, BrushRgb(64, 88, 118)));
            selectedTrigger.Setters.Add(new Setter(DataGridCell.ForegroundProperty, Brushes.White));
            selectedTrigger.Setters.Add(new Setter(DataGridCell.BorderBrushProperty, BrushRgb(111, 165, 226)));
            selectedTrigger.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty, new Thickness(0, 1, 0, 1)));
            style.Triggers.Add(selectedTrigger);

            return style;
        }

        private void AddGridColumns(DataGrid grid)
        {
            grid.Columns.Add(CreateTextColumn("Sel", "SelectionMarker", 42, null, true, "Rows marked SEL are selected for the Selected Rows buttons."));
            grid.Columns.Add(CreateCheckBoxColumn("On", "Enabled", 40, "Enable this account row. Disabled rows stay visible but do not receive copied orders."));

            grid.Columns.Add(CreateTextColumn("Account", "AccountName", 112, null, true, "Connected NinjaTrader account."));
            grid.Columns.Add(CreateTextColumn("Role", "RoleSummary", 72, null, true, "Available, Lead, Copy row, or Conflict based on the enabled rows."));
            grid.Columns.Add(CreateComboBoxColumn("Lead", "LeadAccountName", connectedAccountNames, null, null, 112, "Account whose filled orders this row mirrors."));
            grid.Columns.Add(CreateTextColumn("Plan", "PlanSummary", 210, null, true, "Readable summary of this row's lead, sizing, copy mode, and risk limits."));
            grid.Columns.Add(CreateComboBoxColumn("Copy", "CopyMode", copyModeOptions, "Label", "Value", 78, "All copies entries and exits. Exits only blocks new entries while allowing exits."));
            grid.Columns.Add(CreateComboBoxColumn("Sizing", "SizingMode", sizingModeOptions, "Label", "Value", 98, "1:1 uses lead quantity. Multiplier scales it. Fixed qty uses Fixed Qty. Balance ratio scales by account value."));

            grid.Columns.Add(CreateTextBoxColumn("Multiplier", "Multiplier", 70, "{0:0.##}", TextAlignment.Right, true, true, "Editing this value switches Sizing to Multiplier. 2 copies twice the lead quantity."));
            grid.Columns.Add(CreateTextBoxColumn("Fixed Qty", "FixedQuantity", 64, null, TextAlignment.Right, true, false, "Editing this value switches Sizing to Fixed qty."));
            grid.Columns.Add(CreateTextBoxColumn("Max Qty", "MaxQuantity", 64, null, TextAlignment.Right, true, false, "Caps total copied quantity for each lead order. 0 disables the cap."));

            grid.Columns.Add(CreateTextBoxColumn("Max Pos", "MaxNetPosition", 70, null, TextAlignment.Right, true, false, "Caps this account row's net position size. 0 disables the cap."));
            grid.Columns.Add(CreateTextBoxColumn("Max Loss", "DailyLossLimit", 72, "{0:0}", TextAlignment.Right, true, true, "While copying, triggers Limit Action when session PnL reaches this loss. 0 disables the limit."));
            grid.Columns.Add(CreateTextBoxColumn("Max DD", "MaxDrawdown", 70, "{0:0}", TextAlignment.Right, true, true, "While copying, triggers Limit Action when drawdown from peak session PnL reaches this amount. 0 disables the limit."));
            grid.Columns.Add(CreateTextBoxColumn("Profit Target", "ProfitTarget", 86, "{0:0}", TextAlignment.Right, true, true, "While copying, triggers Limit Action after this session profit target is reached. 0 disables the target."));
            grid.Columns.Add(CreateComboBoxColumn("Limit Action", "LimitAction", limitActionOptions, "Label", "Value", 104, "Lock entries blocks new copied entries and allows reducing exits. Auto close immediately flattens this row's managed positions and blocks copied orders."));
            grid.Columns.Add(CreateCheckBoxColumn("Manual Lock", "ManualLock", 92, "Blocks entries for this row while still allowing exits."));

            grid.Columns.Add(CreateTextColumn("Status", "Status", 132, null, true, "Current copier state for this row."));
            grid.Columns.Add(CreateTextColumn("Pnl", "SessionPnl", 72, "{0:C0}", true, "Session PnL relative to this row's current baseline."));
            grid.Columns.Add(CreateTextColumn("DD", "Drawdown", 72, "{0:C0}", true, "Drawdown from peak session PnL."));
            grid.Columns.Add(CreateTextColumn("Pos", "PositionSummary", 112, null, true, "Current account position summary."));
            grid.Columns.Add(CreateTextBoxColumn("Symbols", "InstrumentFilter", 96, null, TextAlignment.Left, false, false, "Optional comma-separated instrument filters. Leave blank to copy all symbols."));
            grid.Columns.Add(CreateTextColumn("Conn", "ConnectionStatus", 86, null, true, "Current NinjaTrader connection status."));
            grid.Columns.Add(CreateTextColumn("Last Action", "LastAction", 200, null, true, "Most recent copier action or skip reason for this row."));
        }

        private DataGridTemplateColumn CreateCheckBoxColumn(string header, string propertyName, double width, string tooltip)
        {
            var factory = new FrameworkElementFactory(typeof(CheckBox));
            factory.SetValue(ToggleButton.IsThreeStateProperty, false);
            factory.SetValue(UIElement.FocusableProperty, false);
            factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(FrameworkElement.ToolTipProperty, tooltip);
            factory.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(CheckBoxCell_PreviewMouseLeftButtonDown));
            factory.SetBinding(ToggleButton.IsCheckedProperty, new Binding(propertyName)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });

            return new DataGridTemplateColumn
            {
                Header = CreateColumnHeader(header, tooltip),
                CellTemplate = new DataTemplate { VisualTree = factory },
                Width = new DataGridLength(width)
            };
        }

        private void CheckBoxCell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var checkBox = sender as CheckBox;
            var row = checkBox != null ? checkBox.DataContext as AccountCopyRow : null;
            if (checkBox == null || row == null || !checkBox.IsEnabled)
                return;

            CommitGridEdits();
            SelectRowForDirectCellAction(row);
            checkBox.IsChecked = checkBox.IsChecked != true;

            var binding = checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty);
            if (binding != null)
                binding.UpdateSource();

            e.Handled = true;
            UpdateSelectionMarkers();
            RefreshStatusSummary();
        }

        private void SelectRowForDirectCellAction(AccountCopyRow row)
        {
            if (accountsGrid == null || row == null)
                return;

            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None)
            {
                if (!accountsGrid.SelectedItems.Contains(row))
                    accountsGrid.SelectedItems.Add(row);

                return;
            }

            if (accountsGrid.SelectedItems.Count != 1 || !accountsGrid.SelectedItems.Contains(row))
            {
                accountsGrid.SelectedItems.Clear();
                accountsGrid.SelectedItems.Add(row);
            }
        }

        private void EditableCell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            SelectEditableCellRow(sender);
        }

        private void EditableCell_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            SelectEditableCellRow(sender);
        }

        private void SelectEditableCellRow(object sender)
        {
            var element = sender as FrameworkElement;
            var row = element != null ? element.DataContext as AccountCopyRow : null;
            if (row == null)
                return;

            SelectRowForDirectCellAction(row);
            UpdateSelectionMarkers();
            RefreshStatusSummary();
        }

        private DataGridTemplateColumn CreateComboBoxColumn(string header, string propertyName, object itemsSource, string displayMemberPath, string selectedValuePath, double width, string tooltip)
        {
            var factory = new FrameworkElementFactory(typeof(ComboBox));
            factory.SetValue(ItemsControl.ItemsSourceProperty, itemsSource);
            factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(Control.PaddingProperty, new Thickness(2, 0, 2, 0));
            factory.SetValue(Control.MinHeightProperty, 22.0);
            factory.SetValue(Control.BackgroundProperty, BrushRgb(64, 65, 70));
            factory.SetValue(Control.ForegroundProperty, Brushes.White);
            factory.SetValue(Control.BorderBrushProperty, BrushRgb(92, 96, 104));
            factory.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(EditableCell_PreviewMouseLeftButtonDown));
            factory.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(EditableCell_GotKeyboardFocus));

            if (!string.IsNullOrWhiteSpace(displayMemberPath))
                factory.SetValue(ItemsControl.DisplayMemberPathProperty, displayMemberPath);

            var binding = new Binding(propertyName)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };

            if (string.IsNullOrWhiteSpace(selectedValuePath))
            {
                factory.SetBinding(Selector.SelectedItemProperty, binding);
            }
            else
            {
                factory.SetValue(Selector.SelectedValuePathProperty, selectedValuePath);
                factory.SetBinding(Selector.SelectedValueProperty, binding);
            }

            return new DataGridTemplateColumn
            {
                Header = CreateColumnHeader(header, tooltip),
                CellTemplate = new DataTemplate { VisualTree = factory },
                Width = new DataGridLength(width)
            };
        }

        private DataGridTemplateColumn CreateTextBoxColumn(string header, string propertyName, double width, string stringFormat, TextAlignment textAlignment, bool numericOnly, bool allowDecimal, string tooltip)
        {
            var factory = new FrameworkElementFactory(typeof(TextBox));
            factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(Control.PaddingProperty, new Thickness(3, 0, 3, 0));
            factory.SetValue(Control.MinHeightProperty, 22.0);
            factory.SetValue(Control.BackgroundProperty, BrushRgb(48, 49, 54));
            factory.SetValue(Control.ForegroundProperty, Brushes.White);
            factory.SetValue(Control.BorderBrushProperty, BrushRgb(82, 88, 96));
            factory.SetValue(TextBox.TextAlignmentProperty, textAlignment);
            factory.SetValue(FrameworkElement.ToolTipProperty, tooltip);
            factory.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(EditableCell_PreviewMouseLeftButtonDown));
            factory.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(EditableCell_GotKeyboardFocus));
            if (numericOnly)
            {
                factory.SetValue(FrameworkElement.TagProperty, allowDecimal ? "decimal" : "integer");
                factory.AddHandler(UIElement.PreviewTextInputEvent, new TextCompositionEventHandler(NumericTextBox_PreviewTextInput));
                factory.AddHandler(DataObject.PastingEvent, new DataObjectPastingEventHandler(NumericTextBox_Pasting));
                factory.AddHandler(UIElement.LostFocusEvent, new RoutedEventHandler(NumericTextBox_LostFocus));
            }

            var binding = new Binding(propertyName)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
            };

            if (!string.IsNullOrEmpty(stringFormat))
                binding.StringFormat = stringFormat;

            factory.SetBinding(TextBox.TextProperty, binding);

            return new DataGridTemplateColumn
            {
                Header = CreateColumnHeader(header, tooltip),
                CellTemplate = new DataTemplate { VisualTree = factory },
                Width = new DataGridLength(width)
            };
        }

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null)
                return;

            e.Handled = !IsValidNumericText(GetProposedText(textBox, e.Text), NumericTextBoxAllowsDecimal(textBox));
        }

        private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null || !e.DataObject.GetDataPresent(DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }

            var pastedText = e.DataObject.GetData(DataFormats.Text) as string;
            if (!IsValidNumericText(GetProposedText(textBox, pastedText ?? string.Empty), NumericTextBoxAllowsDecimal(textBox)))
                e.CancelCommand();
        }

        private void NumericTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null)
                return;

            NormalizeNumericTextBox(textBox);
            var binding = textBox.GetBindingExpression(TextBox.TextProperty);
            if (binding != null)
                binding.UpdateSource();
        }

        private void NormalizeNumericTextBox(TextBox textBox)
        {
            if (textBox == null || textBox.Tag == null)
                return;

            if (string.IsNullOrEmpty(textBox.Text))
                textBox.Text = "0";
        }

        private string GetProposedText(TextBox textBox, string insertedText)
        {
            var currentText = textBox.Text ?? string.Empty;
            var selectionStart = Math.Max(0, Math.Min(textBox.SelectionStart, currentText.Length));
            var selectionLength = Math.Max(0, Math.Min(textBox.SelectionLength, currentText.Length - selectionStart));

            return currentText.Remove(selectionStart, selectionLength).Insert(selectionStart, insertedText ?? string.Empty);
        }

        private bool NumericTextBoxAllowsDecimal(TextBox textBox)
        {
            return string.Equals(textBox.Tag as string, "decimal", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsValidNumericText(string text, bool allowDecimal)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            var trimmed = text.Trim();
            if (trimmed.Length != text.Length)
                return false;

            if (!allowDecimal)
                return trimmed.All(char.IsDigit);

            var decimalCount = trimmed.Count(c => c == '.');
            if (decimalCount > 1)
                return false;

            return trimmed.All(c => char.IsDigit(c) || c == '.')
                && trimmed.Any(char.IsDigit);
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
                UpdateSourceTrigger = readOnly ? UpdateSourceTrigger.PropertyChanged : UpdateSourceTrigger.LostFocus
            };

            if (!string.IsNullOrEmpty(stringFormat))
                binding.StringFormat = stringFormat;

            return new DataGridTextColumn
            {
                Header = CreateColumnHeader(header, tooltip),
                Binding = binding,
                Width = new DataGridLength(width),
                IsReadOnly = readOnly,
                ElementStyle = CreateTextCellStyle(propertyName, stringFormat)
            };
        }

        private Style CreateTextCellStyle(string propertyName, string stringFormat)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));

            var tooltipBinding = new Binding(propertyName);
            if (!string.IsNullOrEmpty(stringFormat))
                tooltipBinding.StringFormat = stringFormat;

            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, tooltipBinding));
            return style;
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
            AddSelectedRowTrigger(style);

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

        private void AddSelectedRowTrigger(Style style)
        {
            var trigger = new Trigger
            {
                Property = DataGridRow.IsSelectedProperty,
                Value = true
            };
            trigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, BrushRgb(64, 88, 118)));
            trigger.Setters.Add(new Setter(DataGridRow.ForegroundProperty, Brushes.White));
            trigger.Setters.Add(new Setter(DataGridRow.BorderBrushProperty, BrushRgb(111, 165, 226)));
            trigger.Setters.Add(new Setter(DataGridRow.BorderThicknessProperty, new Thickness(0, 1, 0, 1)));
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

            var names = connectedAccounts
                .Select(a => a.Name)
                .Concat(accountRows.Select(r => r.LeadAccountName).Where(name => !string.IsNullOrWhiteSpace(name)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name)
                .ToList();

            foreach (var accountName in names)
                connectedAccountNames.Add(accountName);
        }

        private void SyncAccountRowsWithConnectedAccounts()
        {
            foreach (var account in connectedAccounts.OrderBy(a => a.Name))
            {
                var row = accountRows.FirstOrDefault(r => AccountNamesEqual(r.AccountName, account.Name));
                if (row != null)
                {
                    var wasUnavailable = row.Account == null || IsUnavailableConnectionStatus(row.ConnectionStatus);
                    row.RefreshAccount(account);
                    if (wasUnavailable)
                    {
                        row.ResetBaseline(ReadAccountPnl(account), false);
                        row.LastAction = "Account reconnected - baseline reset";
                        ClearLockedVirtualPositions(row);
                        ClearMaxNetVirtualPositions(row);
                        Log(row.AccountName + " reconnected; risk baseline reset.");
                    }

                    continue;
                }

                row = new AccountCopyRow(account, ReadAccountPnl(account));
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

        private bool IsUnavailableConnectionStatus(string status)
        {
            return !string.IsNullOrWhiteSpace(status)
                && !string.Equals(status, "Connected", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase);
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
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var row in observedAccountRows.ToList())
                    DetachAccountRow(row);

                foreach (var row in accountRows)
                    AttachAccountRow(row);

                return;
            }

            if (e.OldItems != null)
            {
                foreach (AccountCopyRow row in e.OldItems)
                    DetachAccountRow(row);
            }

            if (e.NewItems != null)
            {
                foreach (AccountCopyRow row in e.NewItems)
                    AttachAccountRow(row);
            }
        }

        private void AttachAccountRow(AccountCopyRow row)
        {
            if (row == null || observedAccountRows.Contains(row))
                return;

            row.PropertyChanged += AccountRow_PropertyChanged;
            observedAccountRows.Add(row);
        }

        private void DetachAccountRow(AccountCopyRow row)
        {
            if (row == null || !observedAccountRows.Contains(row))
                return;

            row.PropertyChanged -= AccountRow_PropertyChanged;
            observedAccountRows.Remove(row);
        }

        private void AccountRow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.PropertyName))
                return;

            var row = sender as AccountCopyRow;
            ApplySizingModeFromEditedQuantityField(row, e.PropertyName);

            if (e.PropertyName == "Enabled")
                HandleEnabledStateChange(row);
            else if (e.PropertyName == "ManualLock")
                HandleManualLockStateChange(row);
            else if (RowPropertyCanInvalidateEnabledRow(e.PropertyName))
                ValidateEnabledRowAfterEdit(row);

            if (e.PropertyName == "LeadAccountName" || e.PropertyName == "Enabled" || e.PropertyName == "SizingMode")
                SyncLeadAccountSubscriptions();

            if (RowPropertyAppliesRiskImmediately(e.PropertyName))
                ApplyRiskSettingsEdit(row);

            if (RowPropertyPausesLiveRow(e.PropertyName))
                PauseLiveRowAfterSettingsEdit(row);

            if (RowPropertyAffectsReadiness(e.PropertyName))
                QueueRowRefresh();
        }

        private void ApplySizingModeFromEditedQuantityField(AccountCopyRow row, string propertyName)
        {
            if (suppressEnableValidation || row == null)
                return;

            if (propertyName == "Multiplier"
                && row.Multiplier > 0
                && Math.Abs(row.Multiplier - DefaultMultiplier) > 0.0000001
                && row.SizingMode != SizingMode.Multiplier)
            {
                SetSizingModeWithoutLivePause(row, SizingMode.Multiplier);
                return;
            }

            if (propertyName == "FixedQuantity"
                && row.FixedQuantity > 0
                && row.FixedQuantity != DefaultFixedQuantity
                && row.SizingMode != SizingMode.Fixed)
            {
                SetSizingModeWithoutLivePause(row, SizingMode.Fixed);
            }
        }

        private void SetSizingModeWithoutLivePause(AccountCopyRow row, SizingMode sizingMode)
        {
            var wasSuppressed = suppressLiveSettingsPause;
            suppressLiveSettingsPause = true;
            try
            {
                row.SizingMode = sizingMode;
            }
            finally
            {
                suppressLiveSettingsPause = wasSuppressed;
            }
        }

        private bool RowPropertyCanInvalidateEnabledRow(string propertyName)
        {
            switch (propertyName)
            {
                case "LeadAccountName":
                case "SizingMode":
                case "Multiplier":
                case "FixedQuantity":
                    return true;
                default:
                    return false;
            }
        }

        private void HandleEnabledStateChange(AccountCopyRow row)
        {
            if (suppressEnableValidation || row == null)
                return;

            if (!row.Enabled)
            {
                suppressEnableValidation = true;
                try
                {
                    SetManualLockWithoutAction(row, false);
                    row.LastAction = row.AutoLocked ? "Disabled - risk lock preserved" : "Disabled";
                    ClearLockedVirtualPositions(row);
                    ClearMaxNetVirtualPositions(row);
                }
                finally
                {
                    suppressEnableValidation = false;
                }

                return;
            }

            string skipReason;
            if (!CanEnableRow(row, BuildDesiredLeadNames(new[] { row }), out skipReason))
            {
                suppressEnableValidation = true;
                try
                {
                    row.Enabled = false;
                    row.LastAction = "Enable skipped: " + skipReason;
                    ClearLockedVirtualPositions(row);
                    ClearMaxNetVirtualPositions(row);
                }
                finally
                {
                    suppressEnableValidation = false;
                }

                var message = row.AccountName + " was not enabled: " + skipReason + ".";
                SetStatus(message);
                Log(message);
                return;
            }

            suppressEnableValidation = true;
            try
            {
                PrepareRowAfterEnable(row, true);
            }
            finally
            {
                suppressEnableValidation = false;
            }

            var enabledMessage = row.AccountName + (isCopying
                ? row.AutoLocked ? " enabled while copying; still risk locked." : " enabled while copying; risk baseline reset."
                : " enabled.");
            SetStatus(enabledMessage);
            Log(enabledMessage);
        }

        private void ValidateEnabledRowAfterEdit(AccountCopyRow row)
        {
            if (suppressEnableValidation || row == null || !row.Enabled)
                return;

            string skipReason;
            if (CanEnableRow(row, BuildDesiredLeadNames(new[] { row }), out skipReason))
                return;

            suppressEnableValidation = true;
            try
            {
                row.Enabled = false;
                row.LastAction = "Disabled: " + skipReason;
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
            }
            finally
            {
                suppressEnableValidation = false;
            }

            var message = row.AccountName + " was disabled: " + skipReason + ".";
            SetStatus(message);
            Log(message);
        }

        private void HandleManualLockStateChange(AccountCopyRow row)
        {
            if (suppressEnableValidation || suppressManualLockHandling || row == null)
                return;

            ClearLockedVirtualPositions(row);
            ClearMaxNetVirtualPositions(row);

            if (row.ManualLock)
            {
                row.LastAction = "Manual lock on";
                Log(row.AccountName + " manual lock enabled. Entries are blocked; exits remain allowed.");
            }
            else
            {
                row.LastAction = "Manual lock off";
                Log(row.AccountName + " manual lock cleared.");
            }
        }

        private void SetManualLockWithoutAction(AccountCopyRow row, bool value)
        {
            if (row == null)
                return;

            var wasSuppressed = suppressManualLockHandling;
            suppressManualLockHandling = true;
            try
            {
                row.ManualLock = value;
            }
            finally
            {
                suppressManualLockHandling = wasSuppressed;
            }
        }

        private bool RowPropertyPausesLiveRow(string propertyName)
        {
            switch (propertyName)
            {
                case "LeadAccountName":
                case "CopyMode":
                case "SizingMode":
                case "Multiplier":
                case "FixedQuantity":
                case "MaxQuantity":
                case "MaxNetPosition":
                case "InstrumentFilter":
                    return true;
                default:
                    return false;
            }
        }

        private bool RowPropertyAppliesRiskImmediately(string propertyName)
        {
            switch (propertyName)
            {
                case "DailyLossLimit":
                case "MaxDrawdown":
                case "ProfitTarget":
                case "LimitAction":
                    return true;
                default:
                    return false;
            }
        }

        private void ApplyRiskSettingsEdit(AccountCopyRow row)
        {
            if (suppressEnableValidation || suppressLiveSettingsPause || row == null || !isCopying || !row.Enabled || row.Account == null)
                return;

            RefreshRowMetrics(row);
            if (row.AutoLocked)
            {
                if (row.LimitAction == RiskAction.HardFlatten)
                    RequestRiskAutoClose(row, string.IsNullOrEmpty(row.LockReason) ? "Risk limit" : row.LockReason);

                return;
            }

            TryTriggerRiskLock(row);
        }

        private void PauseLiveRowAfterSettingsEdit(AccountCopyRow row)
        {
            if (suppressEnableValidation || suppressLiveSettingsPause || !isCopying || row == null || !row.Enabled || row.Account == null)
                return;

            var wasManualLocked = row.ManualLock;
            row.ResetBaseline(ReadAccountPnl(row.Account), false);
            SetManualLockWithoutAction(row, true);
            row.LastAction = wasManualLocked ? "Live settings edited - baseline reset" : "Live settings edited - row paused";
            ClearLockedVirtualPositions(row);
            ClearMaxNetVirtualPositions(row);

            var message = row.AccountName + " was paused after a live settings edit; unlock the row when ready.";
            SetStatus(message);
            Log(message);
        }

        private void AccountsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectionMarkers();
            RefreshStatusSummary();
        }

        private void AccountsGrid_CurrentCellChanged(object sender, EventArgs e)
        {
            UpdateSelectionMarkers();
            RefreshStatusSummary();
        }

        private void UpdateSelectionMarkers()
        {
            if (accountsGrid == null)
                return;

            var selectedRows = new HashSet<AccountCopyRow>(GetSelectedRows());
            foreach (var row in accountRows)
                row.SelectionMarker = selectedRows.Contains(row) ? "SEL" : string.Empty;
        }

        private bool RowPropertyAffectsReadiness(string propertyName)
        {
            switch (propertyName)
            {
                case "Enabled":
                case "LeadAccountName":
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

        private void CommitGridEdits()
        {
            if (accountsGrid == null)
                return;

            var focusedTextBox = Keyboard.FocusedElement as TextBox;
            if (focusedTextBox != null)
            {
                NormalizeNumericTextBox(focusedTextBox);
                var binding = focusedTextBox.GetBindingExpression(TextBox.TextProperty);
                if (binding != null)
                    binding.UpdateSource();
            }

            accountsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            accountsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var profileName = profileComboBox.SelectedItem as string;
            if (!string.IsNullOrEmpty(profileName))
                profileNameTextBox.Text = profileName;
        }

        private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

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
            document.AppendChild(root);

            foreach (var row in accountRows)
            {
                var rowElement = document.CreateElement("Follower");
                SetAttribute(rowElement, "account", row.AccountName);
                SetAttribute(rowElement, "leadAccount", row.LeadAccountName);
                SetAttribute(rowElement, "enabled", row.Enabled);
                SetAttribute(rowElement, "manualLocked", row.ManualLock);
                SetAttribute(rowElement, "autoLocked", row.AutoLocked);
                SetAttribute(rowElement, "lockReason", row.LockReason);
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
                    Log("Profile row " + accountName + " is not connected; loaded disabled until it reconnects.");
                }

                var rowLeadName = GetOptionalStringAttribute(element, "leadAccount", leadAccountName);
                var rowEnabled = GetBoolAttribute(element, "enabled", true);
                var rowWasManualLocked = GetBoolAttribute(element, "manualLocked", false);
                var rowWasAutoLocked = GetBoolAttribute(element, "autoLocked", false);
                var rowLockReason = GetOptionalStringAttribute(element, "lockReason", rowWasAutoLocked ? "Risk limit" : string.Empty);
                Account rowLead = null;

                if (account == null && rowEnabled)
                {
                    rowEnabled = false;
                    Log("Profile disabled " + accountName + " until its account reconnects.");
                }

                if (rowWasAutoLocked && rowEnabled)
                {
                    rowEnabled = false;
                    Log("Profile loaded " + accountName + " disabled because it was risk-locked when saved" + (string.IsNullOrWhiteSpace(rowLockReason) ? string.Empty : " by " + rowLockReason) + ".");
                }

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

                var row = account != null
                    ? new AccountCopyRow(account, ReadAccountPnl(account))
                    : new AccountCopyRow(accountName, 0);
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
                row.ManualLock = rowEnabled && rowWasManualLocked;
                row.AutoLocked = rowWasAutoLocked;
                row.LockReason = rowWasAutoLocked ? rowLockReason : string.Empty;
                NormalizeLegacySizingMode(row);
                row.LastAction = rowWasAutoLocked ? "Loaded risk lock" : row.Enabled ? row.ManualLock ? "Loaded manual lock" : "Loaded profile" : "Loaded disabled";

                accountRows.Add(row);
                seenAccounts.Add(accountName);
            }
            SyncAccountRowsWithConnectedAccounts();
            RefreshConnectedAccountNames();
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

        private void NormalizeLegacySizingMode(AccountCopyRow row)
        {
            if (row == null || row.SizingMode != SizingMode.OneToOne)
                return;

            if (row.Multiplier > 0 && Math.Abs(row.Multiplier - DefaultMultiplier) > 0.0000001)
            {
                row.SizingMode = SizingMode.Multiplier;
                return;
            }

            if (row.FixedQuantity > 0 && row.FixedQuantity != DefaultFixedQuantity)
                row.SizingMode = SizingMode.Fixed;
        }

        private void StartPauseButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

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

            var validationIssues = ValidateReadyToStart();
            if (validationIssues.Count > 0)
            {
                var validationMessage = FormatStartBlockedMessage(validationIssues);
                SetStatus(validationMessage);
                foreach (var issue in validationIssues)
                    Log("Start blocked: " + issue + ".");

                RefreshAllRows();
                return;
            }

            SyncLeadAccountSubscriptions();
            ResetActiveRiskBaselines();
            mirroredTargetQuantities.Clear();
            lockedVirtualPositions.Clear();
            maxNetVirtualPositions.Clear();
            dryRunMode = dryRunCheckBox != null && dryRunCheckBox.IsChecked == true;
            isCopying = true;
            startPauseButton.Content = "Pause Copying";
            startPauseButton.Background = Brushes.DarkOrange;
            if (dryRunCheckBox != null)
                dryRunCheckBox.IsEnabled = false;

            var startMessage = BuildStartStatusMessage();
            SetStatus(startMessage);
            Log(startMessage);
            RefreshAllRows();
        }

        private string BuildStartStatusMessage()
        {
            var leadCount = GetConfiguredLeadAccounts().Count;
            var entryActiveCount = accountRows.Count(r => IsConnectedCopyRow(r) && !RowIsReduceOnly(r));
            var exitsOnlyCount = accountRows.Count(r => IsConnectedCopyRow(r) && RowIsReduceOnly(r));
            var lockedCount = accountRows.Count(r => IsConnectedCopyRow(r) && r.IsEntryLocked);

            var message = (dryRunMode ? "Dry run active" : "Copying active")
                + ": " + leadCount + " lead(s), "
                + entryActiveCount + " entry row(s)";

            if (exitsOnlyCount > 0)
                message += ", " + exitsOnlyCount + " exits-only row(s)";

            if (lockedCount > 0)
                message += ", " + lockedCount + " locked row(s)";

            message += dryRunMode ? ". Orders simulated only." : ".";
            return message;
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

        private void ResetActiveRiskBaselines()
        {
            var rows = accountRows
                .Where(r => r.Enabled && r.SizingMode != SizingMode.Disabled && r.Account != null)
                .ToList();

            foreach (var row in rows)
            {
                row.ResetBaseline(ReadAccountPnl(row.Account), false);
                row.LastAction = row.AutoLocked ? "Session baseline reset - still locked" : "Session baseline reset";
            }

            if (rows.Count > 0)
                Log("Reset session risk baselines for " + rows.Count + " active row(s).");
        }

        private void OnOrderUpdate(object sender, OrderEventArgs args)
        {
            if (!isCopying || args.Order == null || args.Order.Account == null)
                return;

            if (IsCopierGeneratedOrder(args.Order))
                return;

            if (!subscribedLeadAccounts.ContainsKey(args.Order.Account.Name))
                return;

            if (args.Order.OrderState != OrderState.PartFilled && args.Order.OrderState != OrderState.Filled)
                return;

            Dispatcher.InvokeAsync(() => CopyOrderToFollowerAccounts(args.Order));
        }

        private bool IsCopierGeneratedOrder(Order order)
        {
            if (order == null || string.IsNullOrWhiteSpace(order.Name))
                return false;

            return string.Equals(order.Name, "ATC Copy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(order.Name, "ATC Flatten", StringComparison.OrdinalIgnoreCase)
                || string.Equals(order.Name, "ATC Reconcile", StringComparison.OrdinalIgnoreCase);
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

                RefreshRowMetrics(row);
                var riskLockedBeforeCopy = TryTriggerRiskLock(row);
                if (riskLockedBeforeCopy && row.LimitAction == RiskAction.HardFlatten)
                    continue;

                if (row.AutoLocked && row.LimitAction == RiskAction.HardFlatten)
                {
                    row.LastAction = "Auto close active";
                    continue;
                }

                var targetKey = GetTargetMirrorKey(sourceOrder, row);
                var alreadyMirrored = mirroredTargetQuantities.ContainsKey(targetKey) ? mirroredTargetQuantities[targetKey] : 0;
                var desiredQuantity = CalculateDesiredTargetQuantity(row, sourceOrder);
                if (desiredQuantity <= 0)
                {
                    if (mirroredTargetQuantities.ContainsKey(targetKey) && alreadyMirrored == desiredQuantity)
                        continue;

                    mirroredTargetQuantities[targetKey] = desiredQuantity;
                    row.LastAction = "Sizing produced 0";
                    Log(row.AccountName + " skipped " + GetInstrumentName(sourceOrder.Instrument) + " because sizing produced 0 contracts.");
                    continue;
                }

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
                    var reduceOnlyReason = GetReduceOnlyReason(row);
                    row.LastAction = "Blocked entry - " + reduceOnlyReason;
                    Log(row.AccountName + " blocked copied entry because " + reduceOnlyReason + ". Entries are blocked; exits remain allowed.");
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

        private List<string> ValidateReadyToStart()
        {
            var issues = new List<string>();
            var activeRows = accountRows.Where(r => r.Enabled && r.SizingMode != SizingMode.Disabled).ToList();
            var desiredLeadNames = BuildDesiredLeadNames(activeRows);

            foreach (var row in activeRows)
            {
                string skipReason;
                if (!CanEnableRow(row, desiredLeadNames, out skipReason))
                {
                    AddStartIssue(issues, row, DescribeStartSkipReason(skipReason));
                    continue;
                }

                if (row.SizingMode == SizingMode.BalanceRatio)
                {
                    var rowLead = ResolveLeadAccountForRow(row);
                    double leadBalance;
                    double rowBalance;
                    if (rowLead == null || !TryGetSizingBalance(rowLead, out leadBalance) || leadBalance <= 0)
                    {
                        AddStartIssue(issues, row, "balance-ratio sizing needs usable lead value data");
                        continue;
                    }

                    if (!TryGetSizingBalance(row.Account, out rowBalance) || rowBalance <= 0)
                    {
                        AddStartIssue(issues, row, "balance-ratio sizing needs usable account value data");
                        continue;
                    }
                }
            }

            return issues;
        }

        private void AddStartIssue(List<string> issues, AccountCopyRow row, string issue)
        {
            var accountName = row != null && !string.IsNullOrWhiteSpace(row.AccountName) ? row.AccountName : "Unknown";
            issues.Add(accountName + ": " + issue);

            if (row != null)
                row.LastAction = "Start blocked: " + issue;
        }

        private string FormatStartBlockedMessage(List<string> issues)
        {
            if (issues.Count == 0)
                return string.Empty;

            if (issues.Count == 1)
                return "Start blocked: " + issues[0] + ".";

            return "Start blocked: " + issues.Count + " row issues. First: " + issues[0] + ".";
        }

        private string DescribeStartSkipReason(string skipReason)
        {
            switch (skipReason)
            {
                case "no account":
                    return "no account object is available";
                case "disconnected":
                    return "account is disconnected";
                case "sizing disabled":
                    return "sizing is disabled";
                case "no lead":
                    return "choose a lead account";
                case "lead disconnected":
                    return "lead account is disconnected";
                case "self-copy":
                    return "cannot copy from itself";
                case "lead is copy row":
                    return "lead is already an active copy row";
                case "used as lead":
                    return "account is used as a lead by another active row";
                case "bad multiplier":
                    return "multiplier must be greater than 0";
                case "bad fixed qty":
                    return "fixed quantity must be greater than 0";
                default:
                    return string.IsNullOrWhiteSpace(skipReason) ? "not ready" : skipReason;
            }
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

        private string GetReduceOnlyReason(AccountCopyRow row)
        {
            if (row == null)
                return "reduce-only mode";

            if (row.AutoLocked)
                return string.IsNullOrEmpty(row.LockReason) ? "risk limit is locked" : row.LockReason + " is locked";

            if (row.ManualLock)
                return "manual lock is on";

            if (row.CopyMode == TradeCopyMode.ExitsOnly)
                return "copy mode is exits only";

            return "reduce-only mode";
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
            CommitGridEdits();

            var rows = accountRows.Where(r => r.Enabled).ToList();
            if (rows.Count == 0)
            {
                SetStatus("No enabled account rows to flatten.");
                return;
            }

            if (MessageBox.Show(BuildFlattenRowsPrompt("enabled", rows), "Confirm Flatten Enabled", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            FlattenRows(rows, "Manual enabled flatten");
            RefreshAllRows();
        }

        private void FlattenSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

            var rows = GetSelectedRows();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to flatten.");
                return;
            }

            if (MessageBox.Show(BuildFlattenRowsPrompt("selected", rows), "Confirm Flatten Selected", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            FlattenRows(rows, "Manual selected flatten");
            RefreshAllRows();
        }

        private void FlattenAllButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

            var wasCopying = isCopying;
            var leadAccounts = GetConfiguredLeadAccounts();
            var leadCount = leadAccounts.Count;
            var accounts = leadAccounts
                .Concat(accountRows.Select(r => r.Account))
                .Where(a => a != null)
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (accounts.Count == 0)
            {
                SetStatus("No connected table or lead accounts to flatten.");
                return;
            }

            if (MessageBox.Show(BuildFlattenAllPrompt(accounts, leadCount, wasCopying), "Confirm Flatten All", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            mirroredTargetQuantities.Clear();

            foreach (var account in accounts)
                FlattenAccount(account, "Manual flatten all");

            var message = wasCopying ? "Flatten all requested; copying remains active." : "Flatten all requested.";
            SetStatus(message);
            Log(message);
            RefreshAllRows();
        }

        private void LockRowsForManualFlatten(IEnumerable<AccountCopyRow> rows, string lastAction)
        {
            foreach (var row in rows.Where(r => r != null).Distinct().ToList())
            {
                SetManualLockWithoutAction(row, true);
                row.LastAction = lastAction + " locked";
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
            }

            Log("Locked " + rows.Count(r => r != null) + " row(s) after flatten request. Entries are blocked; exits remain allowed.");
        }

        private void FlattenRows(IList<AccountCopyRow> rows, string reason)
        {
            var targetRows = rows == null ? new List<AccountCopyRow>() : rows.Where(r => r != null).Distinct().ToList();
            LockRowsForManualFlatten(targetRows, reason);

            var connectedRows = targetRows.Where(RowHasConnectedAccount).ToList();
            var offlineRows = targetRows.Where(r => !RowHasConnectedAccount(r)).ToList();

            foreach (var row in connectedRows)
                FlattenRow(row, reason);

            foreach (var row in offlineRows)
                row.LastAction = reason + " skipped - offline";

            var message = "Flatten requested for " + connectedRows.Count + " connected row(s)";
            if (offlineRows.Count > 0)
                message += "; skipped " + offlineRows.Count + " offline row(s)";

            message += ". Rows are manual-locked.";
            SetStatus(message);
            Log(message);
        }

        private string BuildFlattenRowsPrompt(string scope, IList<AccountCopyRow> rows)
        {
            var rowCount = rows == null ? 0 : rows.Count(r => r != null);
            var filteredCount = rows == null ? 0 : rows.Count(HasInstrumentFilter);
            var offlineCount = rows == null ? 0 : rows.Count(r => r != null && !RowHasConnectedAccount(r));
            var prompt = "Flatten " + rowCount + " " + scope + " row(s)?\n\n"
                + "This cancels active orders and closes managed positions for those rows.";

            if (offlineCount > 0)
                prompt += "\n" + offlineCount + " offline row(s) will be manual-locked but cannot submit flatten orders.";

            if (filteredCount > 0)
                prompt += "\n" + filteredCount + " row(s) will only flatten matching Symbols filters.";
            else
                prompt += "\nNo row symbol filters are set.";

            prompt += "\nRows will be manual-locked afterward so new entries stay blocked.";
            if (isCopying)
                prompt += "\nCopying remains active.";

            return prompt;
        }

        private string BuildFlattenAllPrompt(IList<Account> accounts, int leadCount, bool copyingActive)
        {
            var accountCount = accounts == null ? 0 : accounts.Count(a => a != null);
            var prompt = "Flatten all " + accountCount + " account(s), including " + leadCount + " active lead account(s)?\n\n"
                + "This cancels active orders and closes open positions across each account.\n"
                + "Row symbol filters are not applied to Flatten All.";

            if (copyingActive)
                prompt += "\nCopying remains active after the request.";

            return prompt;
        }

        private void ReconcileSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

            var rows = GetSelectedRows();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to reconcile.");
                return;
            }

            if (MessageBox.Show(BuildReconcileRowsPrompt(rows), "Confirm Reconcile Selected", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var processedCount = 0;
            var offlineCount = 0;
            var riskCount = 0;
            var invalidCount = 0;
            foreach (var row in rows)
            {
                var outcome = ReconcileAccountToLead(row);
                if (outcome == ReconcileOutcome.Processed)
                    processedCount++;
                else if (outcome == ReconcileOutcome.SkippedOffline)
                    offlineCount++;
                else if (outcome == ReconcileOutcome.SkippedRisk)
                    riskCount++;
                else
                    invalidCount++;
            }

            var message = "Reconcile processed " + processedCount + " selected row(s)";
            if (offlineCount > 0)
                message += "; skipped " + offlineCount + " offline row(s)";

            if (riskCount > 0)
                message += "; skipped " + riskCount + " auto-close row(s)";

            if (invalidCount > 0)
                message += "; skipped " + invalidCount + " invalid row(s)";

            message += ".";
            SetStatus(message);
            Log(message);
            RefreshAllRows();
        }

        private string BuildReconcileRowsPrompt(IList<AccountCopyRow> rows)
        {
            var rowCount = rows == null ? 0 : rows.Count(r => r != null);
            var filteredCount = rows == null ? 0 : rows.Count(HasInstrumentFilter);
            var autoCloseCount = rows == null ? 0 : rows.Count(r => r != null && r.AutoLocked && r.LimitAction == RiskAction.HardFlatten);
            var lockedCount = rows == null ? 0 : rows.Count(r => r != null && RowIsReduceOnly(r) && !(r.AutoLocked && r.LimitAction == RiskAction.HardFlatten));
            var cappedCount = rows == null ? 0 : rows.Count(r => r != null && r.MaxNetPosition > 0);
            var offlineCount = rows == null ? 0 : rows.Count(r => r != null && !RowHasConnectedAccount(r));

            var prompt = "Reconcile " + rowCount + " selected row(s) to their lead positions?\n\n"
                + "This may submit market orders using each row's lead, sizing, copy mode, and max position settings.";

            if (offlineCount > 0)
                prompt += "\n" + offlineCount + " offline row(s) will be skipped.";

            if (filteredCount > 0)
                prompt += "\n" + filteredCount + " row(s) will only reconcile matching Symbols filters.";

            if (lockedCount > 0)
                prompt += "\n" + lockedCount + " locked or exits-only row(s) will only reduce current exposure.";

            if (autoCloseCount > 0)
                prompt += "\n" + autoCloseCount + " auto-close risk-locked row(s) will be skipped.";

            if (cappedCount > 0)
                prompt += "\n" + cappedCount + " row(s) have Max Pos caps.";

            if (IsDryRunSelected())
                prompt += "\nDry Run is on, so reconcile orders will be simulated.";

            return prompt;
        }

        private ReconcileOutcome ReconcileAccountToLead(AccountCopyRow row)
        {
            if (row == null)
                return ReconcileOutcome.SkippedInvalid;

            if (!RowHasConnectedAccount(row))
            {
                MarkReconcileOffline(row);
                return ReconcileOutcome.SkippedOffline;
            }

            RefreshRowMetrics(row);
            TryTriggerRiskLock(row);
            if (row.AutoLocked && row.LimitAction == RiskAction.HardFlatten)
            {
                RequestRiskAutoClose(row, string.IsNullOrEmpty(row.LockReason) ? "Risk limit" : row.LockReason);
                row.LastAction = "Reconcile skipped - auto close active";
                Log(row.AccountName + " reconcile skipped because auto close is active.");
                return ReconcileOutcome.SkippedRisk;
            }

            var validationMessage = ValidateRowForReconcile(row);
            if (!string.IsNullOrEmpty(validationMessage))
            {
                row.LastAction = "Reconcile skipped";
                Log(row.AccountName + " reconcile skipped: " + validationMessage);
                return ReconcileOutcome.SkippedInvalid;
            }

            int zeroSizingCount;
            var desiredPositions = BuildDesiredPositions(row, out zeroSizingCount);
            var targetPositions = GetManagedPositionSnapshots(row, row.Account);
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
                row.LastAction = submitted > 0 ? "Dry run reconcile " + submitted + " order(s)" : zeroSizingCount > 0 ? "Reconcile sizing 0" : "Already reconciled";
                Log(row.AccountName + " dry-run reconcile complete; orders simulated: " + submitted + ".");
            }
            else
            {
                row.LastAction = submitted > 0 ? "Reconcile sent " + submitted + " order(s)" : zeroSizingCount > 0 ? "Reconcile sizing 0" : "Already reconciled";
                Log(row.AccountName + " reconcile complete; orders sent: " + submitted + ".");
            }

            if (zeroSizingCount > 0)
                Log(row.AccountName + " reconcile skipped " + zeroSizingCount + " lead position(s) because sizing produced 0 contracts.");

            return ReconcileOutcome.Processed;
        }

        private void MarkReconcileOffline(AccountCopyRow row)
        {
            if (row == null)
                return;

            row.SetStatus("Error", row.Account == null ? "No account" : "Disconnected");
            row.LastAction = "Reconcile skipped - offline";
            Log(row.AccountName + " reconcile skipped because account is offline.");
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

        private Dictionary<string, PositionSnapshot> BuildDesiredPositions(AccountCopyRow row, out int zeroSizingCount)
        {
            zeroSizingCount = 0;
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
                {
                    zeroSizingCount++;
                    continue;
                }

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
            FlattenAccount(account, reason, null, null);
        }

        private void FlattenRow(AccountCopyRow row, string reason)
        {
            if (row == null)
                return;

            var hasSymbolFilter = HasInstrumentFilter(row);
            FlattenAccount(row.Account, reason, hasSymbolFilter ? new Func<Instrument, bool>(instrument => RowAllowsInstrument(row, instrument)) : null, row);
        }

        private void FlattenAccount(Account account, string reason, Func<Instrument, bool> instrumentFilter, AccountCopyRow statusRow)
        {
            if (account == null)
                return;

            try
            {
                CancelActiveOrders(account, instrumentFilter);
                CloseOpenPositions(account, instrumentFilter);
                Log(account.Name + " flatten requested" + (instrumentFilter == null ? string.Empty : " for matching symbols") + ": " + reason + ".");
            }
            catch (Exception ex)
            {
                Log("ERROR " + account.Name + " flatten failed: " + ex.Message);
                var row = statusRow ?? accountRows.FirstOrDefault(r => r.Account == account);
                if (row != null)
                {
                    row.SetStatus("Error", "Flatten failed");
                    row.LastAction = "Flatten failed";
                }
            }
        }

        private void CancelActiveOrders(Account account, Func<Instrument, bool> instrumentFilter)
        {
            Order[] orders;
            lock (account.Orders)
                orders = account.Orders.ToArray();

            foreach (var order in orders)
            {
                if (!IsActiveOrder(order))
                    continue;

                if (instrumentFilter != null && !instrumentFilter(order.Instrument))
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

        private void CloseOpenPositions(Account account, Func<Instrument, bool> instrumentFilter)
        {
            Position[] positions;
            lock (account.Positions)
                positions = account.Positions.ToArray();

            foreach (var position in positions)
            {
                if (position.Quantity == 0 || position.MarketPosition == MarketPosition.Flat)
                    continue;

                if (instrumentFilter != null && !instrumentFilter(position.Instrument))
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
            CommitGridEdits();

            var rows = GetSelectedRows();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to enable.");
                return;
            }

            EnableRows(rows, "selected");
        }

        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

            var rows = GetSelectedRows();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to disable.");
                return;
            }

            foreach (var row in rows)
            {
                row.Enabled = false;
                SetManualLockWithoutAction(row, false);
                row.LastAction = row.AutoLocked ? "Disabled - risk lock preserved" : "Disabled";
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
            }

            SyncLeadAccountSubscriptions();
            RefreshAllRows();
            var message = "Disabled " + rows.Count + " selected row(s).";
            SetStatus(message);
            Log(message);
        }

        private void UnlockSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

            var rows = GetSelectedRows();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to unlock.");
                return;
            }

            var autoResetCount = 0;
            var skippedBaselineCount = 0;
            foreach (var row in rows)
            {
                var wasAutoLocked = row.AutoLocked;
                var baselineReset = false;
                if (wasAutoLocked)
                {
                    if (RowHasConnectedAccount(row))
                    {
                        row.ResetBaseline(ReadAccountPnl(row.Account), true);
                        autoResetCount++;
                        baselineReset = true;
                    }
                    else
                    {
                        skippedBaselineCount++;
                    }
                }

                SetManualLockWithoutAction(row, false);
                row.AutoLocked = false;
                row.LockReason = string.Empty;
                row.AutoCloseRequested = false;
                row.AutoCloseDryRunRequested = false;
                row.LastAction = wasAutoLocked
                    ? baselineReset ? "Unlocked - baseline reset" : "Unlocked - baseline unchanged"
                    : "Unlocked";
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
            }

            var message = "Unlocked " + rows.Count + " selected row(s)" + (autoResetCount > 0 ? "; reset baselines for " + autoResetCount + " risk-locked row(s)" : string.Empty) + ".";
            if (skippedBaselineCount > 0)
                message = message.TrimEnd('.') + "; skipped baseline reset for " + skippedBaselineCount + " offline row(s).";

            SetStatus(message);
            Log(message);
            RefreshAllRows();
        }

        private void ResetBaselinesButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

            var rows = GetSelectedRows();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows before resetting baselines.");
                return;
            }

            var resetCount = 0;
            var skippedCount = 0;
            foreach (var row in rows)
            {
                if (!RowHasConnectedAccount(row))
                {
                    if (row != null)
                        row.LastAction = "Baseline reset skipped - offline";

                    skippedCount++;
                    continue;
                }

                row.ResetBaseline(ReadAccountPnl(row.Account));
                row.LastAction = "Baseline reset";
                resetCount++;
            }

            var message = "Reset risk baselines for " + resetCount + " selected row(s)";
            if (skippedCount > 0)
                message += "; skipped " + skippedCount + " offline row(s)";

            message += ".";
            SetStatus(message);
            Log(message);
            RefreshAllRows();
        }

        private void EnableRows(IEnumerable<AccountCopyRow> rows, string scopeDescription)
        {
            var targetRows = rows.Where(r => r != null).Distinct().ToList();
            if (targetRows.Count == 0)
            {
                SetStatus("No rows to enable.");
                return;
            }

            var desiredLeadNames = BuildDesiredLeadNames(targetRows);

            var enabledCount = 0;
            var liveBaselineResetCount = 0;
            var skipReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in targetRows)
            {
                var shouldResetLiveBaseline = isCopying && !row.Enabled;
                string skipReason;
                if (!TryEnableRow(row, desiredLeadNames, out skipReason))
                {
                    row.LastAction = "Enable skipped: " + skipReason;
                    IncrementReason(skipReasons, skipReason);
                    continue;
                }

                enabledCount++;
                if (shouldResetLiveBaseline)
                    liveBaselineResetCount++;
            }

            SyncLeadAccountSubscriptions();
            RefreshAllRows();
            var message = "Enabled " + enabledCount + " " + scopeDescription + " row(s)" + FormatSkipReasons(skipReasons);
            if (liveBaselineResetCount > 0)
                message += "; reset baselines for " + liveBaselineResetCount + " live row(s)";

            message += ".";
            SetStatus(message);
            Log(message);
        }

        private List<string> BuildDesiredLeadNames(IEnumerable<AccountCopyRow> targetRows)
        {
            return accountRows
                .Where(r => r.Enabled && r.SizingMode != SizingMode.Disabled && !string.IsNullOrWhiteSpace(r.LeadAccountName))
                .Concat(targetRows.Where(r => r != null && r.SizingMode != SizingMode.Disabled && !string.IsNullOrWhiteSpace(r.LeadAccountName)))
                .Select(r => r.LeadAccountName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool TryEnableRow(AccountCopyRow row, List<string> desiredLeadNames, out string skipReason)
        {
            if (!CanEnableRow(row, desiredLeadNames, out skipReason))
                return false;

            var shouldResetLiveBaseline = !row.Enabled;
            suppressEnableValidation = true;
            try
            {
                PrepareRowAfterEnable(row, shouldResetLiveBaseline);
            }
            finally
            {
                suppressEnableValidation = false;
            }

            return true;
        }

        private void PrepareRowAfterEnable(AccountCopyRow row, bool shouldResetLiveBaseline)
        {
            row.Enabled = true;
            SetManualLockWithoutAction(row, false);
            if (shouldResetLiveBaseline && isCopying && row.Account != null)
                row.ResetBaseline(ReadAccountPnl(row.Account), false);

            row.LastAction = GetEnableLastAction(row, shouldResetLiveBaseline);
            ClearLockedVirtualPositions(row);
            ClearMaxNetVirtualPositions(row);
        }

        private string GetEnableLastAction(AccountCopyRow row, bool liveBaselineReset)
        {
            if (isCopying)
            {
                if (row.AutoLocked)
                    return liveBaselineReset ? "Enabled live - still risk locked" : "Enabled - still risk locked";

                return liveBaselineReset ? "Enabled live - baseline reset" : "Enabled";
            }

            return row.AutoLocked ? "Enabled - still risk locked" : "Enabled";
        }

        private bool CanEnableRow(AccountCopyRow row, List<string> desiredLeadNames, out string skipReason)
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

            if (IsActiveCopyAccount(rowLead.Name, row))
            {
                skipReason = "lead is copy row";
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

            return true;
        }

        private bool IsActiveCopyAccount(string accountName, AccountCopyRow exceptRow)
        {
            return accountRows.Any(r =>
                r != exceptRow
                && r.Enabled
                && r.SizingMode != SizingMode.Disabled
                && AccountNamesEqual(r.AccountName, accountName));
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

        private void CopyLeadSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

            var selectedRows = GetSelectedRows();
            if (selectedRows.Count == 0)
            {
                SetStatus("Select one source row before copying settings to rows with the same lead.");
                return;
            }

            if (selectedRows.Count > 1)
            {
                SetStatus("Select exactly one source row before copying settings to rows with the same lead.");
                return;
            }

            var source = selectedRows[0];
            if (source.SizingMode == SizingMode.Disabled)
            {
                SetStatus("Choose a source row with active sizing before copying settings to rows with the same lead.");
                return;
            }

            if (source.SizingMode == SizingMode.Multiplier && source.Multiplier <= 0)
            {
                SetStatus("Set the selected row's multiplier above 0 before copying settings.");
                return;
            }

            if (source.SizingMode == SizingMode.Fixed && source.FixedQuantity <= 0)
            {
                SetStatus("Set the selected row's fixed quantity above 0 before copying settings.");
                return;
            }

            var leadName = source.LeadAccountName;
            if (string.IsNullOrWhiteSpace(leadName))
            {
                SetStatus("Select a copy row with a lead before copying settings to rows with the same lead.");
                return;
            }

            var rows = accountRows.Where(r => r != source && AccountNamesEqual(r.LeadAccountName, leadName)).ToList();
            if (rows.Count == 0)
            {
                SetStatus("No other rows use lead " + leadName + ".");
                return;
            }

            if (isCopying)
            {
                var prompt = "Copy settings from " + source.AccountName + " to " + rows.Count + " row(s) that use lead " + leadName + " while copying is active? Active target row baselines will be reset. Lead selections stay unchanged.";
                if (MessageBox.Show(prompt, "Confirm Live Settings Copy", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            var appliedCount = 0;
            var liveBaselineResetCount = 0;
            suppressLiveSettingsPause = true;
            try
            {
                foreach (var row in rows)
                {
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
                    row.LastAction = "Copied settings from " + source.AccountName;
                    if (isCopying && row.Enabled && row.SizingMode != SizingMode.Disabled && row.Account != null)
                    {
                        row.ResetBaseline(ReadAccountPnl(row.Account), false);
                        row.LastAction = "Copied live settings from " + source.AccountName;
                        liveBaselineResetCount++;
                    }

                    ClearLockedVirtualPositions(row);
                    ClearMaxNetVirtualPositions(row);
                    appliedCount++;
                }
            }
            finally
            {
                suppressLiveSettingsPause = false;
            }

            mirroredTargetQuantities.Clear();
            SyncLeadAccountSubscriptions();
            var message = "Copied settings from " + source.AccountName + " to " + appliedCount + " row(s) that use lead " + leadName;
            if (liveBaselineResetCount > 0)
                message += "; reset baselines for " + liveBaselineResetCount + " live row(s)";

            message += ". Lead selections were left unchanged.";
            SetStatus(message);
            Log(message);
            RefreshAllRows();
        }

        private List<AccountCopyRow> GetSelectedRows()
        {
            if (accountsGrid == null)
                return new List<AccountCopyRow>();

            var rows = accountsGrid.SelectedItems.OfType<AccountCopyRow>().Distinct().ToList();
            if (rows.Count > 0)
                return rows;

            var currentRow = accountsGrid.CurrentItem as AccountCopyRow;
            if (currentRow != null)
                rows.Add(currentRow);

            return rows;
        }

        private void TelemetryTimer_Tick(object sender, EventArgs e)
        {
            SyncLeadAccountSubscriptions();
            RefreshAllRows();
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

            RefreshStatusSummary();
        }

        private void RefreshRowMetrics(AccountCopyRow row)
        {
            if (row.Account == null)
                return;

            var previousConnectionStatus = row.ConnectionStatus;
            row.ConnectionStatus = row.Account.ConnectionStatus.ToString();
            HandleRowConnectionStatusChange(row, previousConnectionStatus);
            var currentPnl = ReadAccountPnl(row.Account);
            row.SessionPnl = currentPnl - row.BaselinePnl;
            row.PeakPnl = Math.Max(row.PeakPnl, row.SessionPnl);
            row.Drawdown = Math.Max(0, row.PeakPnl - row.SessionPnl);
            row.NetPosition = GetNetPosition(row.Account);
            row.PositionSummary = GetPositionSummary(row.Account);
        }

        private void HandleRowConnectionStatusChange(AccountCopyRow row, string previousConnectionStatus)
        {
            if (row == null || !IsConfiguredCopyRow(row))
                return;

            if (!string.Equals(previousConnectionStatus, "Connected", StringComparison.OrdinalIgnoreCase))
                return;

            if (row.Account != null && row.Account.ConnectionStatus == ConnectionStatus.Connected)
                return;

            row.LastAction = "Account disconnected";
            ClearLockedVirtualPositions(row);
            ClearMaxNetVirtualPositions(row);
            Log(row.AccountName + " disconnected; copied orders blocked until reconnect.");
        }

        private void EvaluateRisk(AccountCopyRow row)
        {
            if (isCopying
                && row != null
                && row.AutoLocked
                && row.LimitAction == RiskAction.HardFlatten
                && !row.AutoCloseRequested
                && (!dryRunMode || !row.AutoCloseDryRunRequested))
            {
                RequestRiskAutoClose(row, string.IsNullOrEmpty(row.LockReason) ? "Risk limit" : row.LockReason);
                return;
            }

            TryTriggerRiskLock(row);
        }

        private bool TryTriggerRiskLock(AccountCopyRow row)
        {
            if (!isCopying || row.AutoLocked || row.Account == null || !row.Enabled || row.SizingMode == SizingMode.Disabled)
                return false;

            var reason = GetRiskLimitReason(row);
            if (string.IsNullOrEmpty(reason))
                return false;

            TriggerRiskLock(row, reason);
            return true;
        }

        private string GetRiskLimitReason(AccountCopyRow row)
        {
            if (row.DailyLossLimit > 0 && row.SessionPnl <= -Math.Abs(row.DailyLossLimit))
                return "Daily loss limit";

            if (row.MaxDrawdown > 0 && row.Drawdown >= Math.Abs(row.MaxDrawdown))
                return "Drawdown limit";

            if (row.ProfitTarget > 0 && row.SessionPnl >= Math.Abs(row.ProfitTarget))
                return "Profit target";

            return string.Empty;
        }

        private void TriggerRiskLock(AccountCopyRow row, string reason)
        {
            row.AutoLocked = true;
            SetManualLockWithoutAction(row, false);
            row.LockReason = reason;

            if (row.LimitAction == RiskAction.HardFlatten)
                RequestRiskAutoClose(row, reason);
            else
            {
                row.LastAction = reason + " - entries locked";
                Log(row.AccountName + " locked entries by " + reason + ". New copied entries are blocked; reducing exits are allowed.");
            }
        }

        private void RequestRiskAutoClose(AccountCopyRow row, string reason)
        {
            if (row == null || row.Account == null)
                return;

            if (row.AutoCloseRequested)
            {
                row.LastAction = reason + " - auto close already requested";
                return;
            }

            if (dryRunMode && row.AutoCloseDryRunRequested)
            {
                row.LastAction = reason + " - dry run auto close already simulated";
                return;
            }

            if (dryRunMode)
                row.AutoCloseDryRunRequested = true;
            else
            {
                row.AutoCloseRequested = true;
                row.AutoCloseDryRunRequested = false;
            }

            AutoCloseRiskLockedRow(row, reason);
        }

        private void AutoCloseRiskLockedRow(AccountCopyRow row, string reason)
        {
            if (row == null || row.Account == null)
                return;

            if (dryRunMode)
            {
                row.LastAction = reason + " - dry run auto close";
                Log("DRY RUN " + row.AccountName + " would auto-close managed positions by " + reason + ". New copied orders are blocked.");
                return;
            }

            row.LastAction = reason + " - auto close";
            Log(row.AccountName + " auto-closing managed positions by " + reason + ". New copied orders are blocked.");
            FlattenRow(row, reason);
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

            if (row.AutoLocked)
            {
                row.SetStatus("Locked", GetRiskLockStatusText(row));
                return;
            }

            if (!row.Enabled || row.SizingMode == SizingMode.Disabled)
            {
                row.SetStatus("Disabled", GetDisabledRowStatusText(row));
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

            if (row.ManualLock)
            {
                row.SetStatus("Locked", "Manual lock - exits only");
                return;
            }

            var desyncStatus = GetDesyncStatusText(row);
            if (!string.IsNullOrEmpty(desyncStatus))
            {
                row.SetStatus("Desynced", desyncStatus);
                return;
            }

            if (row.CopyMode == TradeCopyMode.ExitsOnly)
            {
                row.SetStatus("ExitsOnly", isCopying ? "Exits only" : "Ready exits only");
                return;
            }

            var nearRiskStatus = GetNearRiskLimitStatusText(row);
            if (!string.IsNullOrEmpty(nearRiskStatus))
            {
                row.SetStatus("Warning", nearRiskStatus);
                return;
            }

            if (row.LastAction == "Sizing produced 0" || row.LastAction == "Reconcile sizing 0")
            {
                row.SetStatus("Warning", "Sizing 0");
                return;
            }

            row.SetStatus(isCopying ? "Active" : "Ready", isCopying ? "Copying" : "Ready");
        }

        private string GetRiskLockStatusText(AccountCopyRow row)
        {
            var action = row != null && row.LimitAction == RiskAction.HardFlatten ? "Auto close" : "Entries locked";
            var reason = row == null ? string.Empty : FormatRiskReasonForStatus(row.LockReason);
            return string.IsNullOrEmpty(reason) ? action : action + " - " + reason;
        }

        private string FormatRiskReasonForStatus(string reason)
        {
            switch (reason)
            {
                case "Daily loss limit":
                    return "max loss";
                case "Drawdown limit":
                    return "max DD";
                case "Profit target":
                    return "target";
                default:
                    return string.IsNullOrWhiteSpace(reason) ? string.Empty : reason;
            }
        }

        private string GetDisabledRowStatusText(AccountCopyRow row)
        {
            if (row.SizingMode == SizingMode.Disabled)
                return "Sizing off";

            if (IsConfiguredLeadAccount(row.AccountName))
                return "Lead account";

            if (string.IsNullOrWhiteSpace(row.LeadAccountName))
                return "Needs lead";

            var rowLead = ResolveLeadAccountForRow(row);
            if (rowLead == null)
                return "Lead missing";

            if (AccountNamesEqual(row.AccountName, rowLead.Name))
                return "Self-copy";

            if (row.SizingMode == SizingMode.Multiplier && row.Multiplier <= 0)
                return "Bad multiplier";

            if (row.SizingMode == SizingMode.Fixed && row.FixedQuantity <= 0)
                return "Bad fixed qty";

            return "Ready disabled";
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

        private string GetNearRiskLimitStatusText(AccountCopyRow row)
        {
            if (row.DailyLossLimit > 0 && row.SessionPnl <= -Math.Abs(row.DailyLossLimit) * 0.9)
                return "Near max loss";

            if (row.MaxDrawdown > 0 && row.Drawdown >= Math.Abs(row.MaxDrawdown) * 0.9)
                return "Near max DD";

            if (row.ProfitTarget > 0 && row.SessionPnl >= Math.Abs(row.ProfitTarget) * 0.9)
                return "Near target";

            return string.Empty;
        }

        private string GetDesyncStatusText(AccountCopyRow row)
        {
            var rowLead = ResolveLeadAccountForRow(row);
            if (!isCopying || rowLead == null || row.Account == null || RowIsReduceOnly(row) || !row.Enabled || row.SizingMode == SizingMode.Disabled)
                return string.Empty;

            var expectedSignedPositions = BuildExpectedSignedPositionsForStatus(row, rowLead);
            var leadPositions = GetOpenPositionSnapshots(rowLead);
            var targetPositions = GetManagedPositionSnapshots(row, row.Account);

            if (leadPositions.Count == 0 || expectedSignedPositions.Count == 0)
            {
                var extraPosition = targetPositions.FirstOrDefault(p => p.SignedQuantity != 0);
                return extraPosition == null ? string.Empty : "Extra " + DescribeSignedPosition(extraPosition.InstrumentName, extraPosition.SignedQuantity);
            }

            var instrumentNames = expectedSignedPositions.Keys
                .Union(targetPositions.Select(p => p.InstrumentName), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var instrumentName in instrumentNames)
            {
                var expectedSigned = expectedSignedPositions.ContainsKey(instrumentName) ? expectedSignedPositions[instrumentName] : 0;
                var targetPosition = targetPositions.FirstOrDefault(p => string.Equals(p.InstrumentName, instrumentName, StringComparison.OrdinalIgnoreCase));
                var currentSigned = targetPosition != null ? targetPosition.SignedQuantity : 0;
                if (currentSigned != expectedSigned)
                    return BuildDesyncMismatchText(instrumentName, expectedSigned, currentSigned);
            }

            return string.Empty;
        }

        private string BuildDesyncMismatchText(string instrumentName, int expectedSigned, int currentSigned)
        {
            if (expectedSigned == 0)
                return "Extra " + DescribeSignedPosition(instrumentName, currentSigned);

            if (currentSigned == 0)
                return "Need " + DescribeSignedPosition(instrumentName, expectedSigned);

            return "Need " + DescribeSignedPosition(instrumentName, expectedSigned) + " has " + DescribeSignedQuantity(currentSigned);
        }

        private string DescribeSignedPosition(string instrumentName, int signedQuantity)
        {
            return (instrumentName ?? string.Empty) + " " + DescribeSignedQuantity(signedQuantity);
        }

        private string DescribeSignedQuantity(int signedQuantity)
        {
            if (signedQuantity == 0)
                return "flat";

            return Math.Abs(signedQuantity).ToString(CultureInfo.InvariantCulture) + (signedQuantity > 0 ? "L" : "S");
        }

        private Dictionary<string, int> BuildExpectedSignedPositionsForStatus(AccountCopyRow row, Account rowLead)
        {
            var expectedPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var leadPosition in GetOpenPositionSnapshots(rowLead))
            {
                if (!RowAllowsInstrument(row, leadPosition.Instrument))
                    continue;

                int desiredQuantity;
                if (!TryCalculateDesiredQuantityForStatus(row, leadPosition.Quantity, out desiredQuantity) || desiredQuantity <= 0)
                    continue;

                var desiredSigned = leadPosition.MarketPosition == MarketPosition.Short ? -desiredQuantity : desiredQuantity;
                desiredSigned = CapDesiredSignedPositionToMaxNet(row, desiredSigned);
                if (desiredSigned != 0)
                    expectedPositions[leadPosition.InstrumentName] = desiredSigned;
            }

            return expectedPositions;
        }

        private bool TryCalculateDesiredQuantityForStatus(AccountCopyRow row, int baseQuantity, out int desiredQuantity)
        {
            desiredQuantity = 0;
            switch (row.SizingMode)
            {
                case SizingMode.OneToOne:
                    desiredQuantity = baseQuantity;
                    break;
                case SizingMode.Multiplier:
                    desiredQuantity = row.Multiplier > 0 ? (int)Math.Floor(baseQuantity * row.Multiplier) : 0;
                    break;
                case SizingMode.Fixed:
                    desiredQuantity = baseQuantity > 0 ? Math.Max(0, row.FixedQuantity) : 0;
                    break;
                case SizingMode.BalanceRatio:
                    double leadBalance;
                    double followerBalance;
                    var rowLead = ResolveLeadAccountForRow(row);
                    if (rowLead == null || !TryGetSizingBalance(rowLead, out leadBalance) || leadBalance <= 0 || !TryGetSizingBalance(row.Account, out followerBalance) || followerBalance <= 0)
                        return false;

                    desiredQuantity = (int)Math.Floor(baseQuantity * followerBalance / leadBalance);
                    break;
                default:
                    desiredQuantity = 0;
                    break;
            }

            if (row.MaxQuantity > 0)
                desiredQuantity = Math.Min(desiredQuantity, row.MaxQuantity);

            desiredQuantity = Math.Max(0, desiredQuantity);
            return true;
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

        private List<PositionSnapshot> GetManagedPositionSnapshots(AccountCopyRow row, Account account)
        {
            return GetOpenPositionSnapshots(account)
                .Where(position => RowAllowsInstrument(row, position.Instrument))
                .ToList();
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

        private bool HasInstrumentFilter(AccountCopyRow row)
        {
            return row != null && ParseInstrumentFilter(row.InstrumentFilter).Count > 0;
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

        private string BuildSummaryStatus()
        {
            var mode = isCopying ? dryRunMode ? "Dry Run" : "Copying" : "Paused";
            if (accountRows.Count == 0)
                return mode + " | No connected accounts";

            var entryActiveCount = accountRows.Count(r => IsConnectedCopyRow(r) && !RowIsReduceOnly(r));
            var exitsOnlyCount = accountRows.Count(r => IsConnectedCopyRow(r) && RowIsReduceOnly(r));
            var armedLeadCount = GetConfiguredLeadAccounts().Count;
            var lockedCount = accountRows.Count(r => IsConnectedCopyRow(r) && r.IsEntryLocked);
            var errorCount = accountRows.Count(r => r.StatusLevel == "Error" || r.StatusLevel == "Desynced");
            var offlineCount = accountRows.Count(IsOfflineRow);
            var summary = mode + " | Leads: " + armedLeadCount + " | Entries active: " + entryActiveCount + " | Exits only: " + exitsOnlyCount + " | Locked: " + lockedCount + " | Attention: " + errorCount;
            if (offlineCount > 0)
                summary += " | Offline: " + offlineCount;

            var selectionSummary = BuildSelectionSummary();
            return string.IsNullOrEmpty(selectionSummary) ? summary : summary + " | " + selectionSummary;
        }

        private bool IsConfiguredCopyRow(AccountCopyRow row)
        {
            return row != null && row.Enabled && row.SizingMode != SizingMode.Disabled;
        }

        private bool IsConnectedCopyRow(AccountCopyRow row)
        {
            return IsConfiguredCopyRow(row) && RowHasConnectedAccount(row);
        }

        private bool RowHasConnectedAccount(AccountCopyRow row)
        {
            return row != null && row.Account != null && row.Account.ConnectionStatus == ConnectionStatus.Connected;
        }

        private bool IsOfflineRow(AccountCopyRow row)
        {
            return row != null && (row.Account == null || row.Account.ConnectionStatus != ConnectionStatus.Connected);
        }

        private string BuildSelectionSummary()
        {
            if (accountsGrid == null)
                return string.Empty;

            var rows = GetSelectedRows();
            if (rows.Count == 0)
                return string.Empty;

            if (rows.Count > 1)
            {
                var enabledCount = rows.Count(IsConfiguredCopyRow);
                var lockedCount = rows.Count(r => IsConfiguredCopyRow(r) && r.IsEntryLocked);
                var attentionCount = rows.Count(r => r.StatusLevel == "Error" || r.StatusLevel == "Desynced");
                return "Selected: " + rows.Count + " rows | Enabled: " + enabledCount + " | Locked: " + lockedCount + " | Attention: " + attentionCount;
            }

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

            if (parts.Count == 0)
                return "no risk limits";

            var action = row.LimitAction == RiskAction.HardFlatten ? "auto close" : "lock entries";
            return string.Join(", ", parts) + "; " + action;
        }

        private void SetStatus(string message)
        {
            heldStatusMessage = message ?? string.Empty;
            heldStatusUntil = DateTime.Now.AddSeconds(StatusMessageHoldSeconds);
            RefreshStatusSummary();
        }

        private void RefreshStatusSummary()
        {
            if (statusTextBlock == null)
                return;

            var summary = BuildSummaryStatus();
            if (!string.IsNullOrWhiteSpace(heldStatusMessage) && DateTime.Now <= heldStatusUntil)
            {
                statusTextBlock.Text = heldStatusMessage + " | " + summary;
                statusTextBlock.ToolTip = summary;
                return;
            }

            heldStatusMessage = string.Empty;
            statusTextBlock.Text = summary;
            statusTextBlock.ToolTip = summary;
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
            foreach (var row in observedAccountRows.ToList())
                DetachAccountRow(row);

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

        private class EnumOption
        {
            public EnumOption(object value, string label)
            {
                Value = value;
                Label = label;
            }

            public object Value { get; private set; }
            public string Label { get; private set; }
        }

        private class AccountCopyRow : INotifyPropertyChanged
        {
            private bool enabled;
            private string leadAccountName = string.Empty;
            private TradeCopyMode copyMode = TradeCopyMode.All;
            private string connectionStatus = "Unknown";
            private string status = "Ready";
            private string statusLevel = "Ready";
            private string roleSummary = "Available";
            private string selectionMarker = string.Empty;
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
            private bool autoCloseRequested;
            private bool autoCloseDryRunRequested;
            private string lockReason = string.Empty;
            private string lastAction = "Ready";

            public AccountCopyRow(Account account, double baselinePnl)
            {
                Account = account;
                AccountName = account != null ? account.Name : string.Empty;
                BaselinePnl = baselinePnl;
                PeakPnl = 0;
            }

            public AccountCopyRow(string accountName, double baselinePnl)
            {
                AccountName = accountName ?? string.Empty;
                BaselinePnl = baselinePnl;
                connectionStatus = "Not connected";
                positionSummary = "No account";
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

            public string SelectionMarker
            {
                get { return selectionMarker; }
                set { SetField(ref selectionMarker, value ?? string.Empty, "SelectionMarker"); }
            }

            public string PlanSummary
            {
                get { return BuildPlanSummary(); }
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

            public bool AutoCloseRequested
            {
                get { return autoCloseRequested; }
                set { SetField(ref autoCloseRequested, value, "AutoCloseRequested"); }
            }

            public bool AutoCloseDryRunRequested
            {
                get { return autoCloseDryRunRequested; }
                set { SetField(ref autoCloseDryRunRequested, value, "AutoCloseDryRunRequested"); }
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
                ResetBaseline(baselinePnl, true);
            }

            public void ResetBaseline(double baselinePnl, bool clearAutoLock)
            {
                BaselinePnl = baselinePnl;
                PeakPnl = 0;
                SessionPnl = 0;
                Drawdown = 0;
                if (clearAutoLock)
                {
                    AutoLocked = false;
                    AutoCloseRequested = false;
                    AutoCloseDryRunRequested = false;
                    LockReason = string.Empty;
                }

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
                NotifyDerivedProperties(propertyName);
                return true;
            }

            private string BuildPlanSummary()
            {
                var parts = new List<string>
                {
                    string.IsNullOrWhiteSpace(LeadAccountName) ? "No lead" : "Lead " + LeadAccountName,
                    BuildSizingSummary(),
                    BuildRiskSummary()
                };

                var symbolSummary = BuildSymbolSummary();
                if (!string.IsNullOrEmpty(symbolSummary))
                    parts.Add(symbolSummary);

                if (CopyMode == TradeCopyMode.ExitsOnly)
                    parts.Add("exits only");

                if (ManualLock)
                    parts.Add("manual lock");

                if (AutoLocked)
                    parts.Add(string.IsNullOrWhiteSpace(LockReason) ? "risk locked" : "locked: " + LockReason.ToLowerInvariant());

                return string.Join(" | ", parts);
            }

            private string BuildSizingSummary()
            {
                string sizing;
                switch (SizingMode)
                {
                    case SizingMode.Multiplier:
                        sizing = "x" + Multiplier.ToString("0.##", CultureInfo.InvariantCulture);
                        break;
                    case SizingMode.Fixed:
                        sizing = "fixed " + FixedQuantity.ToString(CultureInfo.InvariantCulture);
                        break;
                    case SizingMode.BalanceRatio:
                        sizing = "balance ratio";
                        break;
                    case SizingMode.Disabled:
                        sizing = "off";
                        break;
                    default:
                        sizing = "1:1";
                        break;
                }

                var caps = new List<string>();
                if (MaxQuantity > 0)
                    caps.Add("order max " + MaxQuantity.ToString(CultureInfo.InvariantCulture));

                if (MaxNetPosition > 0)
                    caps.Add("pos max " + MaxNetPosition.ToString(CultureInfo.InvariantCulture));

                return caps.Count == 0 ? sizing : sizing + " (" + string.Join(", ", caps) + ")";
            }

            private string BuildSymbolSummary()
            {
                if (string.IsNullOrWhiteSpace(InstrumentFilter))
                    return string.Empty;

                var symbols = InstrumentFilter
                    .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(token => token.Trim())
                    .Where(token => token.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return symbols.Count == 0 ? string.Empty : "symbols " + string.Join(",", symbols);
            }

            private string BuildRiskSummary()
            {
                var limits = new List<string>();
                if (DailyLossLimit > 0)
                    limits.Add("loss " + DailyLossLimit.ToString("0", CultureInfo.InvariantCulture));

                if (MaxDrawdown > 0)
                    limits.Add("DD " + MaxDrawdown.ToString("0", CultureInfo.InvariantCulture));

                if (ProfitTarget > 0)
                    limits.Add("target " + ProfitTarget.ToString("0", CultureInfo.InvariantCulture));

                if (limits.Count == 0)
                    return "no limits";

                var action = LimitAction == RiskAction.HardFlatten ? "auto close" : "lock entries";
                return action + " " + string.Join(", ", limits);
            }

            private void NotifyDerivedProperties(string propertyName)
            {
                switch (propertyName)
                {
                    case "LeadAccountName":
                    case "CopyMode":
                    case "SizingMode":
                    case "Multiplier":
                    case "FixedQuantity":
                    case "MaxQuantity":
                    case "MaxNetPosition":
                    case "InstrumentFilter":
                    case "DailyLossLimit":
                    case "MaxDrawdown":
                    case "ProfitTarget":
                    case "LimitAction":
                    case "ManualLock":
                    case "AutoLocked":
                    case "LockReason":
                        OnPropertyChanged("PlanSummary");
                        break;
                }
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
