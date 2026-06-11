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
        private const string BuildVersion = "0.1.0-dev";
        private const string BuildCommit = "source";
        private const string BuildTag = "v" + BuildVersion + "+" + BuildCommit;
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
            new EnumOption(RiskAction.SoftLock, "Lock entries only"),
            new EnumOption(RiskAction.HardFlatten, "Auto-close row")
        };
        private readonly List<RowPresetOption> rowPresetOptions = new List<RowPresetOption>
        {
            new RowPresetOption("1:1 copy", "Copy entries and exits at the lead account quantity.", TradeCopyMode.All, SizingMode.OneToOne, DefaultMultiplier, DefaultFixedQuantity, null),
            new RowPresetOption("Multiplier x2", "Copy entries and exits at twice the lead account quantity.", TradeCopyMode.All, SizingMode.Multiplier, 2.0, DefaultFixedQuantity, null),
            new RowPresetOption("Fixed 1", "Copy entries and exits with one contract per lead order.", TradeCopyMode.All, SizingMode.Fixed, DefaultMultiplier, 1, null),
            new RowPresetOption("Exits only", "Follow reducing exits only; block new or increasing exposure.", TradeCopyMode.ExitsOnly, SizingMode.OneToOne, DefaultMultiplier, DefaultFixedQuantity, null),
            new RowPresetOption("Auto-close limits", "Keep sizing as-is and auto-close matching managed positions when limits are hit.", null, null, null, null, RiskAction.HardFlatten),
            new RowPresetOption("Lock-entry limits", "Keep sizing as-is and block new entries when limits are hit.", null, null, null, null, RiskAction.SoftLock)
        };
        private readonly DispatcherTimer telemetryTimer;

        private List<Account> connectedAccounts = new List<Account>();
        private bool isCopying;
        private bool dryRunMode;
        private bool suppressEnableValidation;
        private bool suppressLiveSettingsPause;
        private bool suppressManualLockHandling;
        private bool suppressSizingModeAutoSwitch;
        private bool suppressLeadRoleRefresh;
        private bool rowRefreshPending;
        private string heldStatusMessage = string.Empty;
        private string heldStatusDetail = string.Empty;
        private DateTime heldStatusUntil = DateTime.MinValue;

        private ComboBox profileComboBox;
        private TextBox profileNameTextBox;
        private DataGrid accountsGrid;
        private Button startPauseButton;
        private Button flattenOnButton;
        private Button flattenSelectedButton;
        private Button flattenAllButton;
        private Button reconcileSelectedButton;
        private Button toggleSelectedButton;
        private Button unlockSelectedButton;
        private Button resetBaselineButton;
        private Button copyLeadSettingsButton;
        private ComboBox rowPresetComboBox;
        private Button applyRowPresetButton;
        private CheckBox dryRunCheckBox;
        private TextBlock selectedRowsTextBlock;
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
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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

            var saveProfileButton = CreateButton("Save Profile", Brushes.DimGray, "Save the current table setup, including off rows, per-row leads, sizing, and risk settings. Existing profiles require overwrite confirmation.");
            saveProfileButton.Click += SaveProfileButton_Click;
            profilePanel.Children.Add(saveProfileButton);

            var loadProfileButton = CreateButton("Load Profile", Brushes.DimGray, "Load the named profile after confirmation. This replaces the current table setup; positions and orders are not changed.");
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
            startPauseButton = CreateButton("Start Copying", Brushes.SeaGreen, "Start or pause copying. Starting resets active row risk baselines after preflight validation.");
            startPauseButton.Width = 130;
            startPauseButton.Click += StartPauseButton_Click;
            sessionRiskRow.Children.Add(startPauseButton);

            dryRunCheckBox = new CheckBox
            {
                Content = "Dry Run",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                ToolTip = "Check before starting to simulate copied and reconcile orders without submitting live orders. Dry Run is locked for the session once started."
            };
            dryRunCheckBox.Checked += DryRunCheckBox_CheckedChanged;
            dryRunCheckBox.Unchecked += DryRunCheckBox_CheckedChanged;
            ToolTipService.SetShowOnDisabled(dryRunCheckBox, true);
            sessionRiskRow.Children.Add(dryRunCheckBox);
            UpdateStartPauseButtonState();

            sessionRiskRow.Children.Add(CreateToolbarLabel("Risk"));
            flattenOnButton = CreateButton("Flatten On", Brushes.Firebrick, "Flatten On rows' managed positions and manual-lock entries afterward. Symbol filters are respected.");
            flattenOnButton.Click += FlattenOnButton_Click;
            sessionRiskRow.Children.Add(flattenOnButton);

            flattenSelectedButton = CreateButton("Flatten Selection", Brushes.Firebrick, "Flatten selected rows' managed positions and manual-lock entries afterward. Symbol filters are respected.");
            flattenSelectedButton.Click += FlattenSelectedButton_Click;
            sessionRiskRow.Children.Add(flattenSelectedButton);

            flattenAllButton = CreateButton("Flatten All", Brushes.DarkRed, "Flatten every table account plus lead accounts used by On rows while leaving the copier state unchanged.");
            flattenAllButton.Click += FlattenAllButton_Click;
            sessionRiskRow.Children.Add(flattenAllButton);
            actionPanel.Children.Add(sessionRiskRow);

            var selectionRow = CreateToolbarRow();
            selectionRow.Children.Add(CreateToolbarLabel("Selection"));
            selectedRowsTextBlock = new TextBlock
            {
                Text = "No selection",
                Foreground = BrushRgb(210, 216, 224),
                FontWeight = FontWeights.Bold,
                Width = 300,
                Margin = new Thickness(0, 0, 12, 6),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = "Current selected table row(s)."
            };
            selectionRow.Children.Add(selectedRowsTextBlock);

            reconcileSelectedButton = CreateButton("Reconcile", Brushes.DimGray, "Adjust selected rows toward each row's lead positions using current sizing and limits.");
            reconcileSelectedButton.Click += ReconcileSelectedButton_Click;
            selectionRow.Children.Add(reconcileSelectedButton);

            toggleSelectedButton = CreateButton("Turn On/Off", Brushes.DimGray, "If every selected row is on, turn those rows off. Otherwise turn on selected rows that are ready; invalid rows are skipped with reasons.");
            toggleSelectedButton.Width = 128;
            toggleSelectedButton.Click += ToggleSelectedEnabledButton_Click;
            selectionRow.Children.Add(toggleSelectedButton);

            unlockSelectedButton = CreateButton("Unlock", Brushes.DimGray, "Clear manual and risk locks on selected rows. Risk-locked rows require confirmation and reset baselines when connected.");
            unlockSelectedButton.Click += UnlockSelectedButton_Click;
            selectionRow.Children.Add(unlockSelectedButton);

            resetBaselineButton = CreateButton("Reset Baselines", Brushes.DimGray, "Reset selected rows' session PnL baselines. Risk-locked rows require confirmation because this clears auto risk locks.");
            resetBaselineButton.Click += ResetBaselinesButton_Click;
            selectionRow.Children.Add(resetBaselineButton);

            copyLeadSettingsButton = CreateButton("Copy Setup", Brushes.DimGray, "Copy mode, sizing, risk limits, Limit Action, and Symbols to other rows that use the selected row's lead. Lead selections stay unchanged.");
            copyLeadSettingsButton.Click += CopyLeadSettingsButton_Click;
            selectionRow.Children.Add(copyLeadSettingsButton);
            actionPanel.Children.Add(selectionRow);

            var presetRow = CreateToolbarRow();
            presetRow.Children.Add(CreateToolbarLabel("Row Preset"));
            rowPresetComboBox = new ComboBox
            {
                Width = 150,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(4),
                ItemsSource = rowPresetOptions,
                DisplayMemberPath = "Label",
                SelectedIndex = 0,
                ToolTip = "Choose a common setup to apply to selected rows. Leads, Symbols, On state, and risk amounts are preserved."
            };
            rowPresetComboBox.SelectionChanged += RowPresetComboBox_SelectionChanged;
            presetRow.Children.Add(rowPresetComboBox);

            applyRowPresetButton = CreateButton("Apply", Brushes.DimGray, "Apply the chosen row preset to selected rows.");
            applyRowPresetButton.Width = 72;
            applyRowPresetButton.IsEnabled = false;
            applyRowPresetButton.Click += ApplyRowPresetButton_Click;
            presetRow.Children.Add(applyRowPresetButton);
            UpdateRowPresetToolTip();
            actionPanel.Children.Add(presetRow);

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
                FrozenColumnCount = 5,
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
                Text = "Ready. Turn copy rows On, choose each row's lead, sizing, and risk, then start copying."
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

            var buildTagBlock = new TextBlock
            {
                Text = BuildTag,
                Foreground = BrushRgb(128, 136, 146),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 2, 0),
                ToolTip = "Austin's Trade Copier " + BuildTag + ". Include this tag in screenshots or support notes to identify the build."
            };
            Grid.SetRow(buildTagBlock, 5);
            root.Children.Add(buildTagBlock);

            Content = root;
            UpdateSelectedActionButtons();
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
            var button = new Button
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
            ToolTipService.SetShowOnDisabled(button, true);
            return button;
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
            selectedTrigger.Setters.Add(new Setter(DataGridCell.BackgroundProperty, BrushRgb(70, 104, 142)));
            selectedTrigger.Setters.Add(new Setter(DataGridCell.ForegroundProperty, Brushes.White));
            selectedTrigger.Setters.Add(new Setter(DataGridCell.BorderBrushProperty, BrushRgb(126, 184, 244)));
            selectedTrigger.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty, new Thickness(0, 1, 0, 1)));
            style.Triggers.Add(selectedTrigger);

            return style;
        }

        private void AddGridColumns(DataGrid grid)
        {
            grid.Columns.Add(CreateCheckBoxColumn("On", "Enabled", 40, "Turn this copy row on. A row must have a Lead and active Sizing to receive copied orders.", "EnableTooltip", "CanToggleEnabled"));

            grid.Columns.Add(CreateTextColumn("Account", "AccountName", 112, null, true, "Connected NinjaTrader account."));
            grid.Columns.Add(CreateTextColumn("Role", "RoleSummary", 72, null, true, "Role is based on setup. Accounts followed by another row are Lead; rows with a Lead are Copy."));
            grid.Columns.Add(CreateComboBoxColumn("Lead", "LeadAccountName", null, null, null, 112, "Choose the account this row should copy. The list hides this row and active copy rows; leave blank for lead-only or unused accounts.", "LeadSelectionTooltip", "CanEditLeadSelection", "LeadAccountOptions"));
            grid.Columns.Add(CreateTextColumn("Plan", "PlanSummary", 210, null, true, "Readable summary of this row's lead, sizing, copy mode, symbol filter, and risk limits."));
            grid.Columns.Add(CreateComboBoxColumn("Copy", "CopyMode", copyModeOptions, "Label", "Value", 78, BuildCopySetupColumnTooltip("All copies entries and exits. Exits only blocks new entries while allowing exits."), null, "CanEditCopySetup"));
            grid.Columns.Add(CreateTextBoxColumn("Symbols", "InstrumentFilter", 128, null, TextAlignment.Left, false, false, BuildCopySetupColumnTooltip("Optional symbol filter for this row. Leave blank to copy every instrument. Use roots or full contract names separated by commas, for example MES, MNQ, MES JUN26. Filters apply to copy, reconcile, Flatten Selection, Flatten On, and auto-close row."), null, "CanEditCopySetup"));
            grid.Columns.Add(CreateComboBoxColumn("Sizing", "SizingMode", sizingModeOptions, "Label", "Value", 98, BuildCopySetupColumnTooltip("Choose how this row sizes copied orders. 1:1 follows the lead fill. Multiplier uses floor(lead fill x multiplier). Fixed qty sends the Fixed Qty value. Balance ratio uses account value data and skips orders if values are unavailable."), null, "CanEditCopySetup"));

            grid.Columns.Add(CreateTextBoxColumn("Multiplier", "Multiplier", 70, "{0:0.##}", TextAlignment.Right, true, true, BuildCopySetupColumnTooltip("Editing this value switches Sizing to Multiplier. Uses floor(lead fill x multiplier), so small multipliers can round to 0."), null, "CanEditCopySetup"));
            grid.Columns.Add(CreateTextBoxColumn("Fixed Qty", "FixedQuantity", 64, null, TextAlignment.Right, true, false, BuildCopySetupColumnTooltip("Editing this value switches Sizing to Fixed qty. Sends this target quantity once per copied lead order."), null, "CanEditCopySetup"));
            grid.Columns.Add(CreateTextBoxColumn("Max Qty", "MaxQuantity", 64, null, TextAlignment.Right, true, false, BuildCopySetupColumnTooltip("Caps the final copied quantity after sizing. 0 disables the cap."), null, "CanEditCopySetup"));

            grid.Columns.Add(CreateTextBoxColumn("Max Net", "MaxNetPosition", 70, null, TextAlignment.Right, true, false, BuildCopySetupColumnTooltip("Caps this account row's net position size. 0 disables the cap."), null, "CanEditCopySetup"));
            grid.Columns.Add(CreateTextBoxColumn("Max Loss", "DailyLossLimit", 72, "{0:0}", TextAlignment.Right, true, true, BuildCopySetupColumnTooltip("While copying, the Limit Action is triggered when session PnL reaches this loss. 0 disables the limit."), null, "CanEditCopySetup"));
            grid.Columns.Add(CreateTextBoxColumn("Max DD", "MaxDrawdown", 70, "{0:0}", TextAlignment.Right, true, true, BuildCopySetupColumnTooltip("While copying, the Limit Action is triggered when drawdown from peak session PnL reaches this amount. 0 disables the limit."), null, "CanEditCopySetup"));
            grid.Columns.Add(CreateTextBoxColumn("Profit Target", "ProfitTarget", 86, "{0:0}", TextAlignment.Right, true, true, BuildCopySetupColumnTooltip("While copying, the Limit Action is triggered after this session profit target is reached. 0 disables the target."), null, "CanEditCopySetup"));
            grid.Columns.Add(CreateComboBoxColumn("Limit Action", "LimitAction", limitActionOptions, "Label", "Value", 118, BuildCopySetupColumnTooltip("What to do when Max Loss, Max DD, or Profit Target is hit. Lock entries only blocks new copied entries and allows reducing exits. Auto-close row immediately flattens this row's matching managed positions and blocks copied orders."), null, "CanEditCopySetup"));
            grid.Columns.Add(CreateCheckBoxColumn("Manual Lock", "ManualLock", 92, "Blocks entries for this row while still allowing exits.", "ManualLockTooltip", "CanToggleManualLock"));

            grid.Columns.Add(CreateTextColumn("Status", "Status", 104, null, true, "Current copier state for this row."));
            grid.Columns.Add(CreateTextColumn("PnL", "SessionPnl", 72, "{0:C0}", true, "Session PnL relative to this row's current baseline."));
            grid.Columns.Add(CreateTextColumn("DD", "Drawdown", 72, "{0:C0}", true, "Drawdown from peak session PnL."));
            grid.Columns.Add(CreateTextColumn("Risk Now", "RiskProgressSummary", 150, null, true, "Current progress from this row's session baseline toward Max Loss, Max DD, and Profit Target limits."));
            grid.Columns.Add(CreateTextColumn("Pos", "PositionSummary", 112, null, true, "Current account position summary."));
            grid.Columns.Add(CreateTextColumn("Conn", "ConnectionStatus", 86, null, true, "Current NinjaTrader connection status."));
            grid.Columns.Add(CreateTextColumn("Last Action", "LastAction", 200, null, true, "Most recent copier action or skip reason for this row."));
        }

        private string BuildCopySetupColumnTooltip(string tooltip)
        {
            return tooltip + " Lead rows are read-only because copy setup and row risk apply to copy rows.";
        }

        private DataGridTemplateColumn CreateCheckBoxColumn(string header, string propertyName, double width, string tooltip)
        {
            return CreateCheckBoxColumn(header, propertyName, width, tooltip, null, null);
        }

        private DataGridTemplateColumn CreateCheckBoxColumn(string header, string propertyName, double width, string tooltip, string tooltipPropertyName, string isEnabledPropertyName)
        {
            var factory = new FrameworkElementFactory(typeof(CheckBox));
            factory.SetValue(ToggleButton.IsThreeStateProperty, false);
            factory.SetValue(UIElement.FocusableProperty, false);
            factory.SetValue(FrameworkElement.TagProperty, propertyName);
            factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(ToolTipService.ShowOnDisabledProperty, true);
            if (string.IsNullOrWhiteSpace(tooltipPropertyName))
                factory.SetValue(FrameworkElement.ToolTipProperty, tooltip);
            else
                factory.SetBinding(FrameworkElement.ToolTipProperty, new Binding(tooltipPropertyName));

            if (!string.IsNullOrWhiteSpace(isEnabledPropertyName))
                factory.SetBinding(UIElement.IsEnabledProperty, new Binding(isEnabledPropertyName));

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

            e.Handled = true;
            CommitFocusedLeadComboBoxEdit();
            CommitFocusedTextBoxEdit();
            var leadSnapshot = CaptureLeadSelections();
            SetDirectCheckBoxValue(row, checkBox.Tag as string, checkBox.IsChecked != true);

            var binding = checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty);
            if (binding != null)
                binding.UpdateTarget();

            SelectRowForDirectCellAction(row);
            RepairLeadSelectionsAfterDirectCellAction(leadSnapshot);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RepairLeadSelectionsAfterDirectCellAction(leadSnapshot);
            }), DispatcherPriority.Background);

            UpdateSelectedActionButtons();
            RefreshStatusSummary();
        }

        private void SetDirectCheckBoxValue(AccountCopyRow row, string propertyName, bool value)
        {
            if (row == null)
                return;

            if (string.Equals(propertyName, "Enabled", StringComparison.Ordinal))
            {
                row.Enabled = value;
                return;
            }

            if (string.Equals(propertyName, "ManualLock", StringComparison.Ordinal))
                row.ManualLock = value;
        }

        private void CommitFocusedLeadComboBoxEdit()
        {
            var focusedComboBox = Keyboard.FocusedElement as ComboBox;
            if (focusedComboBox == null)
                return;

            CommitLeadComboBoxSelection(focusedComboBox);
        }

        private Dictionary<AccountCopyRow, string> CaptureLeadSelections()
        {
            return accountRows
                .Where(row => row != null)
                .ToDictionary(row => row, row => row.LeadAccountName ?? string.Empty);
        }

        private void RepairLeadSelectionsAfterDirectCellAction(Dictionary<AccountCopyRow, string> leadSnapshot)
        {
            if (!RestoreLeadSelectionsAfterDirectCellAction(leadSnapshot))
                return;

            ValidateEnabledRowsAfterLeadRestore();
            RefreshLeadRoleState();
            SyncLeadAccountSubscriptions();
        }

        private bool RestoreLeadSelectionsAfterDirectCellAction(Dictionary<AccountCopyRow, string> leadSnapshot)
        {
            if (leadSnapshot == null || leadSnapshot.Count == 0)
                return false;

            var restoredAny = false;
            var wasEnableSuppressed = suppressEnableValidation;
            var wasLiveSuppressed = suppressLiveSettingsPause;
            var wasLeadRefreshSuppressed = suppressLeadRoleRefresh;
            suppressEnableValidation = true;
            suppressLiveSettingsPause = true;
            suppressLeadRoleRefresh = true;
            try
            {
                foreach (var pair in leadSnapshot)
                {
                    var row = pair.Key;
                    if (row == null || SnapshotHadFollowers(leadSnapshot, row))
                        continue;

                    var savedLead = pair.Value ?? string.Empty;
                    if (AccountNamesEqual(row.LeadAccountName, savedLead))
                        continue;

                    row.LeadAccountName = savedLead;
                    restoredAny = true;
                }
            }
            finally
            {
                suppressEnableValidation = wasEnableSuppressed;
                suppressLiveSettingsPause = wasLiveSuppressed;
                suppressLeadRoleRefresh = wasLeadRefreshSuppressed;
            }

            return restoredAny;
        }

        private bool SnapshotHadFollowers(Dictionary<AccountCopyRow, string> leadSnapshot, AccountCopyRow row)
        {
            if (leadSnapshot == null || row == null || string.IsNullOrWhiteSpace(row.AccountName))
                return false;

            return leadSnapshot.Any(pair =>
                pair.Key != row
                && pair.Key != null
                && !string.IsNullOrWhiteSpace(pair.Value)
                && !AccountNamesEqual(pair.Key.AccountName, pair.Value)
                && AccountNamesEqual(pair.Value, row.AccountName));
        }

        private void ValidateEnabledRowsAfterLeadRestore()
        {
            foreach (var row in accountRows.Where(r => r != null && r.Enabled).ToList())
                ValidateEnabledRowAfterEdit(row);
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
            UpdateSelectedActionButtons();
            RefreshStatusSummary();
        }

        private DataGridTemplateColumn CreateComboBoxColumn(string header, string propertyName, object itemsSource, string displayMemberPath, string selectedValuePath, double width, string tooltip, string tooltipPropertyName = null, string isEnabledPropertyName = null, string itemsSourceBindingPropertyName = null)
        {
            var factory = new FrameworkElementFactory(typeof(ComboBox));
            if (string.IsNullOrWhiteSpace(itemsSourceBindingPropertyName))
                factory.SetValue(ItemsControl.ItemsSourceProperty, itemsSource);
            else
                factory.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(itemsSourceBindingPropertyName));
            factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(Control.PaddingProperty, new Thickness(2, 0, 2, 0));
            factory.SetValue(Control.MinHeightProperty, 22.0);
            factory.SetValue(Control.BackgroundProperty, BrushRgb(64, 65, 70));
            factory.SetValue(Control.ForegroundProperty, Brushes.White);
            factory.SetValue(Control.BorderBrushProperty, BrushRgb(92, 96, 104));
            factory.SetValue(FrameworkElement.TagProperty, propertyName);
            factory.SetValue(FrameworkElement.ToolTipProperty, tooltip);
            factory.SetValue(ToolTipService.ShowOnDisabledProperty, true);
            var isLeadColumn = string.Equals(propertyName, "LeadAccountName", StringComparison.Ordinal);
            factory.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(EditableCell_PreviewMouseLeftButtonDown));
            factory.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(EditableCell_GotKeyboardFocus));
            factory.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(ComboBoxCell_SelectionChanged));
            if (isLeadColumn)
                factory.AddHandler(UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(LeadComboBoxCell_LostKeyboardFocus));

            if (!string.IsNullOrWhiteSpace(tooltipPropertyName))
                factory.SetBinding(FrameworkElement.ToolTipProperty, new Binding(tooltipPropertyName));

            if (!string.IsNullOrWhiteSpace(isEnabledPropertyName))
                factory.SetBinding(UIElement.IsEnabledProperty, new Binding(isEnabledPropertyName));

            if (!string.IsNullOrWhiteSpace(displayMemberPath))
                factory.SetValue(ItemsControl.DisplayMemberPathProperty, displayMemberPath);

            var binding = new Binding(propertyName)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = isLeadColumn ? UpdateSourceTrigger.Explicit : UpdateSourceTrigger.PropertyChanged
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

        private void ComboBoxCell_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var comboBox = sender as ComboBox;
            if (comboBox == null || !string.Equals(comboBox.Tag as string, "LeadAccountName", StringComparison.Ordinal))
                return;

            if (!comboBox.IsDropDownOpen)
                return;

            CommitLeadComboBoxSelection(comboBox);
        }

        private void LeadComboBoxCell_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            CommitLeadComboBoxSelection(sender as ComboBox);
        }

        private void CommitLeadComboBoxSelection(ComboBox comboBox)
        {
            if (comboBox == null || !string.Equals(comboBox.Tag as string, "LeadAccountName", StringComparison.Ordinal))
                return;

            if (comboBox.SelectedItem == null && comboBox.SelectedValue == null)
                return;

            var row = comboBox.DataContext as AccountCopyRow;
            var previousLead = row != null ? row.LeadAccountName : string.Empty;

            var selectedItemBinding = comboBox.GetBindingExpression(Selector.SelectedItemProperty);
            if (selectedItemBinding != null)
                selectedItemBinding.UpdateSource();

            var selectedValueBinding = comboBox.GetBindingExpression(Selector.SelectedValueProperty);
            if (selectedValueBinding != null)
                selectedValueBinding.UpdateSource();

            if (row != null && AccountNamesEqual(previousLead, row.LeadAccountName))
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshLeadRoleState();
                SyncLeadAccountSubscriptions();
            }), DispatcherPriority.Background);
        }

        private DataGridTemplateColumn CreateTextBoxColumn(string header, string propertyName, double width, string stringFormat, TextAlignment textAlignment, bool numericOnly, bool allowDecimal, string tooltip, string tooltipPropertyName = null, string isEnabledPropertyName = null)
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
            factory.SetValue(ToolTipService.ShowOnDisabledProperty, true);
            factory.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(EditableCell_PreviewMouseLeftButtonDown));
            factory.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(EditableCell_GotKeyboardFocus));

            if (!string.IsNullOrWhiteSpace(tooltipPropertyName))
                factory.SetBinding(FrameworkElement.ToolTipProperty, new Binding(tooltipPropertyName));

            if (!string.IsNullOrWhiteSpace(isEnabledPropertyName))
                factory.SetBinding(UIElement.IsEnabledProperty, new Binding(isEnabledPropertyName));

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

            var tooltipBinding = new Binding(propertyName == "Status" ? "StatusDetail" : propertyName);
            if (propertyName != "Status" && !string.IsNullOrEmpty(stringFormat))
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
            trigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, BrushRgb(70, 104, 142)));
            trigger.Setters.Add(new Setter(DataGridRow.ForegroundProperty, Brushes.White));
            trigger.Setters.Add(new Setter(DataGridRow.BorderBrushProperty, BrushRgb(126, 184, 244)));
            trigger.Setters.Add(new Setter(DataGridRow.BorderThicknessProperty, new Thickness(3, 1, 0, 1)));
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
                SyncAccountRowsWithConnectedAccounts();
                RefreshConnectedAccountNames();
                RefreshLeadAccountOptions();
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

        private void RefreshLeadAccountOptions()
        {
            foreach (var row in accountRows.ToList())
                RefreshLeadAccountOptions(row);
        }

        private void RefreshLeadAccountOptions(AccountCopyRow row)
        {
            if (row == null)
                return;

            var options = BuildLeadAccountOptions(row);
            if (row.LeadAccountOptions.SequenceEqual(options, StringComparer.OrdinalIgnoreCase))
                return;

            SyncLeadAccountOptions(row.LeadAccountOptions, options);
        }

        private void SyncLeadAccountOptions(ObservableCollection<string> currentOptions, IList<string> desiredOptions)
        {
            if (currentOptions == null || desiredOptions == null)
                return;

            // Do not Clear() here. If a selected Lead disappears even briefly, WPF can write
            // a blank SelectedItem back to LeadAccountName while another cell is being clicked.
            foreach (var option in desiredOptions)
            {
                if (!currentOptions.Any(existing => AccountNamesEqual(existing, option)))
                    currentOptions.Add(option);
            }

            for (var index = currentOptions.Count - 1; index >= 0; index--)
            {
                var current = currentOptions[index];
                if (!desiredOptions.Any(option => AccountNamesEqual(option, current)))
                    currentOptions.RemoveAt(index);
            }

            for (var desiredIndex = 0; desiredIndex < desiredOptions.Count; desiredIndex++)
            {
                var existingIndex = IndexOfLeadAccountOption(currentOptions, desiredOptions[desiredIndex]);
                if (existingIndex >= 0 && existingIndex != desiredIndex)
                    currentOptions.Move(existingIndex, desiredIndex);
            }
        }

        private int IndexOfLeadAccountOption(IList<string> options, string accountName)
        {
            if (options == null)
                return -1;

            for (var index = 0; index < options.Count; index++)
            {
                if (AccountNamesEqual(options[index], accountName))
                    return index;
            }

            return -1;
        }

        private List<string> BuildLeadAccountOptions(AccountCopyRow row)
        {
            var options = new List<string> { string.Empty };
            if (row == null)
                return options;

            foreach (var accountName in connectedAccounts
                .Select(a => a.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name))
            {
                if (AccountNamesEqual(accountName, row.AccountName))
                    continue;

                if (IsActiveCopyAccount(accountName, row) && !AccountNamesEqual(accountName, row.LeadAccountName))
                    continue;

                AddLeadAccountOption(options, accountName);
            }

            AddLeadAccountOption(options, row.LeadAccountName);
            return options;
        }

        private void AddLeadAccountOption(ICollection<string> options, string accountName)
        {
            if (options == null || string.IsNullOrWhiteSpace(accountName))
                return;

            if (options.Any(option => AccountNamesEqual(option, accountName)))
                return;

            options.Add(accountName);
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
                        row.LastAction = row.AutoLocked
                            ? "Reconnected - risk lock preserved"
                            : "Account reconnected - baseline reset";
                        ClearLockedVirtualPositions(row);
                        ClearMaxNetVirtualPositions(row);
                        ClearMirroredTargetQuantities(row);
                        Log(row.AccountName + (row.AutoLocked
                            ? " reconnected; risk baseline reset and risk lock preserved."
                            : " reconnected; risk baseline reset."));
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

        private bool AccountHasCopyRows(string accountName)
        {
            return AccountHasCopyRows(accountName, null);
        }

        private bool AccountHasCopyRows(string accountName, AccountCopyRow exceptRow)
        {
            return CountCopyRows(accountName, exceptRow) > 0;
        }

        private int CountCopyRows(string accountName, AccountCopyRow exceptRow)
        {
            if (string.IsNullOrWhiteSpace(accountName))
                return 0;

            // Lead role is a setup relationship: if any other row follows this account, it is a lead immediately.
            return accountRows.Count(r =>
                r != exceptRow
                && !string.IsNullOrWhiteSpace(r.LeadAccountName)
                && !AccountNamesEqual(r.AccountName, r.LeadAccountName)
                && AccountNamesEqual(r.LeadAccountName, accountName));
        }

        private int GetReferencedLeadAccountCount()
        {
            return accountRows
                .Where(r => !AccountNamesEqual(r.AccountName, r.LeadAccountName))
                .Select(r => r.LeadAccountName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
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

            if (e.PropertyName == "LeadAccountName" && !suppressLeadRoleRefresh)
                RefreshLeadRoleState();

            if (e.PropertyName == "LeadAccountName")
            {
                SyncLeadAccountSubscriptions();
            }
            else if (e.PropertyName == "Enabled" || e.PropertyName == "SizingMode")
            {
                SyncLeadAccountSubscriptions();
            }

            if (e.PropertyName == "LimitAction")
                HandleLimitActionStateChange(row);

            if (RowPropertyAppliesRiskImmediately(e.PropertyName))
                ApplyRiskSettingsEdit(row);

            if (RowPropertyPausesLiveRow(e.PropertyName))
                PauseLiveRowAfterSettingsEdit(row);

            if (RowPropertyAffectsReadiness(e.PropertyName))
                QueueRowRefresh();

            if (SelectedActionButtonsDependOnProperty(e.PropertyName))
            {
                UpdateSelectedActionButtons();
                RefreshStatusSummary();
            }
        }

        private bool SelectedActionButtonsDependOnProperty(string propertyName)
        {
            switch (propertyName)
            {
                case "Enabled":
                case "LeadAccountName":
                case "CopyMode":
                case "InstrumentFilter":
                case "SizingMode":
                case "Multiplier":
                case "FixedQuantity":
                case "MaxQuantity":
                case "MaxNetPosition":
                case "DailyLossLimit":
                case "MaxDrawdown":
                case "ProfitTarget":
                case "LimitAction":
                case "ManualLock":
                case "AutoLocked":
                case "IsEntryLocked":
                case "ConnectionStatus":
                case "RoleSummary":
                case "Status":
                    return true;
                default:
                    return false;
            }
        }

        private void ApplySizingModeFromEditedQuantityField(AccountCopyRow row, string propertyName)
        {
            if (suppressEnableValidation || suppressSizingModeAutoSwitch || row == null)
                return;

            if (propertyName == "Multiplier"
                && row.Multiplier > 0
                && row.SizingMode != SizingMode.Multiplier)
            {
                SetSizingModeWithoutLivePause(row, SizingMode.Multiplier);
                return;
            }

            if (propertyName == "FixedQuantity"
                && row.FixedQuantity > 0
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
                    row.LastAction = row.AutoLocked ? "Turned off - risk lock preserved" : "Turned off";
                    ClearLockedVirtualPositions(row);
                    ClearMaxNetVirtualPositions(row);
                    ClearMirroredTargetQuantities(row);
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
                var friendlyReason = DescribeReadinessSkipReason(skipReason);
                suppressEnableValidation = true;
                try
                {
                    row.Enabled = false;
                    row.LastAction = "Turn-on skipped: " + friendlyReason;
                    ClearLockedVirtualPositions(row);
                    ClearMaxNetVirtualPositions(row);
                    ClearMirroredTargetQuantities(row);
                }
                finally
                {
                    suppressEnableValidation = false;
                }

                var message = row.AccountName + " was not turned on: " + friendlyReason + ".";
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
                ? row.AutoLocked ? " turned on while copying; still risk locked." : " turned on while copying; risk baseline reset."
                : " turned on.");
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

            var friendlyReason = DescribeReadinessSkipReason(skipReason);
            suppressEnableValidation = true;
            try
            {
                row.Enabled = false;
                row.LastAction = "Turned off: " + friendlyReason;
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
                ClearMirroredTargetQuantities(row);
            }
            finally
            {
                suppressEnableValidation = false;
            }

            var message = row.AccountName + " was turned off: " + friendlyReason + ".";
            SetStatus(message);
            Log(message);
        }

        private void HandleManualLockStateChange(AccountCopyRow row)
        {
            if (suppressEnableValidation || suppressManualLockHandling || row == null)
                return;

            ClearLockedVirtualPositions(row);
            ClearMaxNetVirtualPositions(row);
            ClearMirroredTargetQuantities(row);

            if (row.ManualLock)
            {
                row.LastAction = "Manual lock on";
                Log(row.AccountName + " manual lock turned on. Entries are blocked; exits remain allowed.");
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

        private void HandleLimitActionStateChange(AccountCopyRow row)
        {
            if (row == null || row.LimitAction == RiskAction.HardFlatten)
                return;

            if (!ClearAutoCloseState(row))
                return;

            if (!row.AutoLocked)
                return;

            var reason = string.IsNullOrWhiteSpace(row.LockReason) ? "Risk limit" : row.LockReason;
            row.LastAction = reason + " - entries locked";
            Log(row.AccountName + " auto-close state cleared; entries remain locked by " + reason + ".");
        }

        private bool ClearAutoCloseState(AccountCopyRow row)
        {
            if (row == null)
                return false;

            var hadAutoCloseState = row.AutoCloseRequested
                || row.AutoCloseDryRunRequested
                || row.AutoCloseRetryPending
                || row.AutoCloseNeedsReview;

            row.AutoCloseRequested = false;
            row.AutoCloseDryRunRequested = false;
            row.AutoCloseRetryPending = false;
            row.AutoCloseNeedsReview = false;
            return hadAutoCloseState;
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
            if (suppressEnableValidation || suppressLiveSettingsPause || row == null || !isCopying || !row.Enabled || !RowHasConnectedAccount(row))
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
            ClearMirroredTargetQuantities(row);

            var message = row.AccountName + " was paused after a live settings edit; unlock the row when ready.";
            SetStatus(message);
            Log(message);
        }

        private void AccountsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectedActionButtons();
            RefreshStatusSummary();
        }

        private void AccountsGrid_CurrentCellChanged(object sender, EventArgs e)
        {
            UpdateSelectedActionButtons();
            RefreshStatusSummary();
        }

        private void RowPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateRowPresetToolTip();
        }

        private void UpdateSelectedActionButtons()
        {
            UpdateStartPauseButtonState();
            var rows = GetSelectedRows();
            var hasSelection = rows.Count > 0;
            UpdateSelectedRowsText(rows);
            UpdateFlattenActionButtons(rows);

            if (reconcileSelectedButton != null)
            {
                var reconcileEligibleCount = rows.Count(CanAttemptReconcileRow);
                reconcileSelectedButton.IsEnabled = reconcileEligibleCount > 0;
                reconcileSelectedButton.ToolTip = BuildReconcileSelectedTooltip(rows.Count, reconcileEligibleCount);
            }

            if (unlockSelectedButton != null)
            {
                var unlockableCount = rows.Count(IsUnlockableRow);
                unlockSelectedButton.IsEnabled = unlockableCount > 0;
                unlockSelectedButton.ToolTip = hasSelection
                    ? unlockableCount > 0
                        ? "Clear manual and risk locks on " + unlockableCount + " selected row(s). Risk-locked rows require confirmation and reset baselines when connected."
                        : "Selected rows are not locked."
                    : "Select one or more locked rows to unlock.";
            }

            if (resetBaselineButton != null)
            {
                var resettableCount = rows.Count(IsBaselineResettableRow);
                resetBaselineButton.IsEnabled = resettableCount > 0;
                resetBaselineButton.ToolTip = hasSelection
                    ? resettableCount > 0
                        ? "Reset session PnL baselines for " + resettableCount + " connected selected row(s). Risk-locked rows require confirmation because this clears auto risk locks."
                        : "Selected rows are offline, so baselines cannot be reset."
                    : "Select one or more connected rows before resetting baselines.";
            }

            if (copyLeadSettingsButton != null)
            {
                var sourceRow = rows.Count == 1 ? rows[0] : null;
                var sourceHasLead = sourceRow != null && !string.IsNullOrWhiteSpace(sourceRow.LeadAccountName);
                var sourceBlockReason = GetCopySettingsSourceBlockReason(sourceRow);
                var targetCount = string.IsNullOrEmpty(sourceBlockReason) ? GetCopySettingsTargetRows(sourceRow).Count : 0;
                copyLeadSettingsButton.IsEnabled = sourceRow != null && sourceHasLead && string.IsNullOrEmpty(sourceBlockReason) && targetCount > 0;
                copyLeadSettingsButton.ToolTip = GetCopySettingsTooltip(rows.Count, sourceHasLead, sourceBlockReason, sourceRow, targetCount);
            }

            if (applyRowPresetButton != null)
            {
                applyRowPresetButton.IsEnabled = rows.Any(CanApplyRowPresetToRow);
                UpdateRowPresetToolTip();
            }

            if (toggleSelectedButton == null)
                return;

            toggleSelectedButton.IsEnabled = hasSelection;
            if (rows.Count == 0)
            {
                toggleSelectedButton.Content = "Turn On/Off";
                toggleSelectedButton.ToolTip = "Select rows to turn them on or off.";
                return;
            }

            var onCount = rows.Count(r => r.Enabled);
            var offCount = rows.Count - onCount;
            var enableableOffCount = rows.Count(r => !r.Enabled && r.CanToggleEnabled);
            var skippedOffCount = offCount - enableableOffCount;

            if (enableableOffCount == 0 && onCount == 0)
            {
                toggleSelectedButton.Content = "Turn On";
                toggleSelectedButton.IsEnabled = false;
                toggleSelectedButton.ToolTip = "Selected rows are not ready to turn on. Connect the account, choose a Lead, and use active Sizing; lead accounts stay off.";
                return;
            }

            if (enableableOffCount == 0)
            {
                toggleSelectedButton.Content = "Turn Off";
                toggleSelectedButton.ToolTip = "Turn off " + onCount + " selected On row(s). Rows stay visible and saved in profiles.";
                return;
            }

            toggleSelectedButton.Content = onCount == 0 && skippedOffCount == 0 ? "Turn On" : "Turn On Ready";
            toggleSelectedButton.ToolTip = "Turn on " + enableableOffCount + " ready row(s).";
            if (skippedOffCount > 0)
                toggleSelectedButton.ToolTip += " " + skippedOffCount + " selected row(s) stay off because they are disconnected, leads, missing a Lead, self-copy, using an active copy row as Lead, or Sizing is off.";
            if (onCount > 0)
                toggleSelectedButton.ToolTip += " " + onCount + " selected row(s) already on.";
        }

        private void UpdateFlattenActionButtons(IList<AccountCopyRow> selectedRows)
        {
            if (flattenOnButton != null)
            {
                var onRows = accountRows.Where(r => r != null && r.Enabled).ToList();
                var connectedOnCount = onRows.Count(RowHasConnectedAccount);
                var offlineOnCount = onRows.Count - connectedOnCount;
                flattenOnButton.IsEnabled = connectedOnCount > 0;
                flattenOnButton.ToolTip = BuildFlattenOnTooltip(onRows.Count, connectedOnCount, offlineOnCount);
            }

            if (flattenSelectedButton != null)
            {
                var rows = selectedRows == null ? new List<AccountCopyRow>() : selectedRows.Where(r => r != null).Distinct().ToList();
                var connectedSelectedCount = rows.Count(RowHasConnectedAccount);
                var offlineSelectedCount = rows.Count - connectedSelectedCount;
                flattenSelectedButton.IsEnabled = connectedSelectedCount > 0;
                flattenSelectedButton.ToolTip = BuildFlattenSelectedTooltip(rows.Count, connectedSelectedCount, offlineSelectedCount);
            }

            if (flattenAllButton != null)
            {
                var leadAccounts = GetConfiguredLeadAccounts();
                var candidateAccounts = GetFlattenAllCandidateAccounts(leadAccounts);
                var connectedCount = candidateAccounts.Count(a => a.ConnectionStatus == ConnectionStatus.Connected);
                var skippedOfflineCount = CountSkippedOfflineFlattenAllAccounts(candidateAccounts);
                flattenAllButton.IsEnabled = connectedCount > 0;
                flattenAllButton.ToolTip = BuildFlattenAllTooltip(connectedCount, leadAccounts.Count, skippedOfflineCount);
            }
        }

        private string BuildFlattenOnTooltip(int onCount, int connectedCount, int offlineCount)
        {
            if (onCount == 0)
                return "No On rows. Turn rows On before using Flatten On.";

            if (connectedCount == 0)
                return onCount + " On row(s) are offline; no flatten orders can be submitted.";

            var tooltip = "Flatten " + connectedCount + " connected On row(s) and manual-lock them afterward. Symbol filters are respected.";
            if (offlineCount > 0)
                tooltip += " " + offlineCount + " offline On row(s) will be manual-locked but cannot submit flatten orders.";

            return tooltip;
        }

        private string BuildFlattenSelectedTooltip(int selectedCount, int connectedCount, int offlineCount)
        {
            if (selectedCount == 0)
                return "Select one or more connected rows to flatten.";

            if (connectedCount == 0)
                return selectedCount + " selected row(s) are offline; no flatten orders can be submitted.";

            var tooltip = "Flatten " + connectedCount + " connected selected row(s) and manual-lock them afterward. Symbol filters are respected.";
            if (offlineCount > 0)
                tooltip += " " + offlineCount + " offline selected row(s) will be manual-locked but cannot submit flatten orders.";

            return tooltip;
        }

        private string BuildFlattenAllTooltip(int connectedCount, int leadCount, int skippedOfflineCount)
        {
            if (connectedCount == 0)
                return "No connected table or active lead accounts to flatten.";

            var tooltip = "Flatten " + connectedCount + " connected table/lead account(s). Copying state is unchanged.";
            if (leadCount > 0)
                tooltip = "Flatten " + connectedCount + " connected table/lead account(s), including " + leadCount + " active lead account(s). Copying state is unchanged.";

            if (skippedOfflineCount > 0)
                tooltip += " " + skippedOfflineCount + " offline account(s) will be skipped.";

            return tooltip;
        }

        private void UpdateSelectedRowsText(List<AccountCopyRow> rows)
        {
            if (selectedRowsTextBlock == null)
                return;

            if (rows == null || rows.Count == 0)
            {
                selectedRowsTextBlock.Text = "No selection";
                selectedRowsTextBlock.ToolTip = "Click or select row(s) before using selection actions.";
                return;
            }

            if (rows.Count == 1)
            {
                var row = rows[0];
                selectedRowsTextBlock.Text = BuildSelectedRowLabel(row);
                selectedRowsTextBlock.ToolTip = BuildSelectionSummary();
                return;
            }

            selectedRowsTextBlock.Text = BuildSelectedRowsLabel(rows);
            selectedRowsTextBlock.ToolTip = BuildSelectionSummary();
        }

        private string BuildSelectedRowsLabel(List<AccountCopyRow> rows)
        {
            if (rows == null || rows.Count == 0)
                return "No selection";

            if (rows.Count == 1)
                return BuildSelectedRowLabel(rows[0]);

            var onCount = rows.Count(r => r.Enabled);
            var offCount = rows.Count - onCount;
            var offlineCount = rows.Count(IsOfflineRow);
            var attentionCount = rows.Count(IsAttentionRow);
            var label = rows.Count + " selected | " + BuildAccountNamePreview(rows, 2);
            if (onCount > 0)
                label += " | On " + onCount;

            if (offCount > 0)
                label += " | Off " + offCount;

            if (offlineCount > 0)
                label += " | Offline " + offlineCount;

            if (attentionCount > 0)
                label += " | Review " + attentionCount;

            return label;
        }

        private void UpdateRowPresetToolTip()
        {
            var preset = rowPresetComboBox != null ? rowPresetComboBox.SelectedItem as RowPresetOption : null;
            var description = preset != null ? preset.Description : "Choose a common setup to apply to selected rows.";
            var preservation = "Leads, Symbols, On state, and risk amounts are preserved.";
            var tooltip = description + " " + preservation;

            if (rowPresetComboBox != null)
                rowPresetComboBox.ToolTip = tooltip;

            if (applyRowPresetButton == null)
                return;

            var rows = GetSelectedRows();
            var eligibleCount = rows.Count(CanApplyRowPresetToRow);
            var presetLabel = preset != null ? preset.Label : "the chosen preset";
            if (rows.Count == 0)
            {
                applyRowPresetButton.IsEnabled = false;
                applyRowPresetButton.ToolTip = "Select one or more rows before applying a row preset. " + tooltip;
                return;
            }

            applyRowPresetButton.IsEnabled = eligibleCount > 0;
            if (eligibleCount == 0)
            {
                applyRowPresetButton.ToolTip = "Selected rows are lead accounts. Row presets apply to copy or available rows only. " + tooltip;
                return;
            }

            applyRowPresetButton.ToolTip = eligibleCount < rows.Count
                ? "Apply " + presetLabel + " to " + eligibleCount + " eligible row(s); lead rows are skipped. " + tooltip
                : "Apply " + presetLabel + " to " + rows.Count + " selected row(s). " + tooltip;
        }

        private string GetCopySettingsTooltip(int selectedRowCount, bool sourceHasLead, string sourceBlockReason, AccountCopyRow sourceRow, int targetCount)
        {
            if (selectedRowCount == 0)
                return "Select one source copy row before copying settings.";

            if (selectedRowCount > 1)
                return "Select exactly one source copy row before copying settings.";

            if (!sourceHasLead)
                return "Select a copy row with a lead before copying settings.";

            if (!string.IsNullOrEmpty(sourceBlockReason))
                return sourceBlockReason;

            if (targetCount == 0)
            {
                var leadName = sourceRow == null ? string.Empty : sourceRow.LeadAccountName;
                return string.IsNullOrWhiteSpace(leadName)
                    ? "No peer rows use this source row's lead."
                    : "No peer rows use lead " + leadName + ".";
            }

            return "Copy mode, sizing, risk limits, Limit Action, and Symbols to " + targetCount + " peer row(s) using the same lead. Lead selections stay unchanged.";
        }

        private string BuildReconcileSelectedTooltip(int selectedRowCount, int eligibleCount)
        {
            if (selectedRowCount == 0)
                return "Select On copy rows with a connected Lead to reconcile.";

            if (eligibleCount == 0)
                return "Selected rows cannot reconcile. Use On copy rows with a connected Lead; lead, available, off, and auto-close-locked rows are skipped.";

            if (eligibleCount < selectedRowCount)
                return "Reconcile " + eligibleCount + " eligible row(s); lead, available, off, invalid, and auto-close-locked rows are skipped.";

            return "Adjust selected On copy rows toward their lead positions using sizing and limits.";
        }

        private string GetCopySettingsSourceBlockReason(AccountCopyRow row)
        {
            if (row == null)
                return "Select one source copy row before copying settings.";

            if (string.Equals(row.RoleSummary, "Lead", StringComparison.OrdinalIgnoreCase))
                return "Choose a copy row, not a lead account, before copying settings.";

            if (AccountNamesEqual(row.AccountName, row.LeadAccountName))
                return "Choose a row that copies a different lead before copying settings.";

            if (string.Equals(row.Status, "Lead missing", StringComparison.OrdinalIgnoreCase))
                return "Choose a row with a connected lead before copying settings.";

            if (string.Equals(row.Status, "Lead copying", StringComparison.OrdinalIgnoreCase))
                return "Choose a row whose Lead is not an active copy row before copying settings.";

            if (row.SizingMode == SizingMode.Disabled)
                return "Choose active sizing on the selected row before copying settings.";

            if (row.SizingMode == SizingMode.Multiplier && row.Multiplier <= 0)
                return "Set the selected row's multiplier above 0 before copying settings.";

            if (row.SizingMode == SizingMode.Fixed && row.FixedQuantity <= 0)
                return "Set the selected row's fixed quantity above 0 before copying settings.";

            return string.Empty;
        }

        private List<AccountCopyRow> GetCopySettingsTargetRows(AccountCopyRow source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.LeadAccountName))
                return new List<AccountCopyRow>();

            var leadName = source.LeadAccountName;
            return accountRows
                .Where(r => r != source
                    && AccountNamesEqual(r.LeadAccountName, leadName)
                    && !AccountHasCopyRows(r.AccountName, r))
                .ToList();
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

            CommitFocusedTextBoxEdit();
            accountsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            accountsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private void CommitFocusedTextBoxEdit()
        {
            var focusedTextBox = Keyboard.FocusedElement as TextBox;
            if (focusedTextBox != null)
            {
                NormalizeNumericTextBox(focusedTextBox);
                var binding = focusedTextBox.GetBindingExpression(TextBox.TextProperty);
                if (binding != null)
                    binding.UpdateSource();
            }
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

            string profilePath;
            if (!TryGetProfilePathForAction(profileName, "save", out profilePath))
                return;

            if (File.Exists(profilePath)
                && MessageBox.Show(BuildOverwriteProfilePrompt(profileName), "Confirm Save Profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
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

        private string BuildOverwriteProfilePrompt(string profileName)
        {
            var rowCount = accountRows.Count;
            var enabledCount = accountRows.Count(IsConfiguredCopyRow);
            return "Overwrite profile " + profileName + "?\n\n"
                + "The saved profile will be replaced with the current table setup.\n"
                + "Current table: " + rowCount + " row(s), " + enabledCount + " active copy row(s).\n\n"
                + "Open positions and working orders are not changed.";
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

            string profilePath;
            if (!TryGetProfilePathForAction(profileName, "load", out profilePath))
                return;

            if (!File.Exists(profilePath))
            {
                SetStatus("Profile " + profileName + " does not exist.");
                return;
            }

            if (MessageBox.Show(BuildLoadProfilePrompt(profileName), "Confirm Load Profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                var loadedOffCount = LoadProfile(profileName);
                var message = BuildLoadedProfileMessage(profileName, loadedOffCount);
                SetStatus(message);
                Log(message);
            }
            catch (Exception ex)
            {
                SetStatus("Profile load failed.");
                Log("ERROR profile load failed: " + ex.Message);
            }
        }

        private string BuildLoadProfilePrompt(string profileName)
        {
            var rowCount = accountRows.Count;
            var enabledCount = accountRows.Count(IsConfiguredCopyRow);
            return "Load profile " + profileName + "?\n\n"
                + "This replaces the current table setup, including On rows, leads, sizing, symbols, and risk settings.\n"
                + "Current table: " + rowCount + " row(s), " + enabledCount + " active copy row(s).\n\n"
                + "Open positions and working orders are not changed.";
        }

        private string BuildLoadedProfileMessage(string profileName, int loadedOffCount)
        {
            var rowCount = accountRows.Count;
            var onCount = accountRows.Count(r => r != null && r.Enabled);
            var offCount = Math.Max(0, rowCount - onCount);
            var leadCount = GetReferencedLeadAccountCount();
            var offlineCount = accountRows.Count(IsOfflineRow);

            var parts = new List<string>
            {
                "Loaded profile " + profileName + ": " + rowCount + " row(s)",
                onCount + " On"
            };

            if (offCount > 0)
                parts.Add(offCount + " Off");

            if (leadCount > 0)
                parts.Add(leadCount + " lead(s)");

            if (offlineCount > 0)
                parts.Add(offlineCount + " offline");

            if (loadedOffCount > 0)
                parts.Add(loadedOffCount + " kept Off for review");

            return string.Join(", ", parts) + ".";
        }

        private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var profileName = NormalizeProfileName(profileNameTextBox.Text);
            if (string.IsNullOrEmpty(profileName))
            {
                SetStatus("Select or enter a profile name before deleting.");
                return;
            }

            string path;
            if (!TryGetProfilePathForAction(profileName, "delete", out path))
                return;

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
            root.SetAttribute("version", "3");
            root.SetAttribute("savedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            document.AppendChild(root);

            foreach (var row in accountRows)
            {
                var rowElement = document.CreateElement("Row");
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

        private int LoadProfile(string profileName)
        {
            var path = GetProfilePath(profileName);
            if (!File.Exists(path))
            {
                SetStatus("Profile " + profileName + " does not exist.");
                return 0;
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
            var loadedOffCount = 0;
            foreach (var element in GetProfileRowElements(root))
            {
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
                }

                var rowLeadName = GetOptionalStringAttribute(element, "leadAccount", leadAccountName);
                var rowEnabled = GetBoolAttribute(element, "enabled", true);
                var rowWasManualLocked = GetBoolAttribute(element, "manualLocked", false);
                var rowWasAutoLocked = GetBoolAttribute(element, "autoLocked", false);
                var rowLockReason = GetOptionalStringAttribute(element, "lockReason", rowWasAutoLocked ? "Risk limit" : string.Empty);
                var rowLoadedAvailableBecauseNoLead = false;
                var rowLoadedOffReason = string.Empty;
                Account rowLead = null;

                if (account == null && rowEnabled)
                {
                    rowEnabled = false;
                    rowLoadedOffReason = "account is disconnected";
                    loadedOffCount++;
                    Log("Profile loaded " + accountName + " Off because the account is disconnected. Reconnect it, then turn it On.");
                }

                if (rowWasAutoLocked && rowEnabled)
                {
                    rowEnabled = false;
                    rowLoadedOffReason = string.IsNullOrWhiteSpace(rowLockReason) ? "saved risk lock" : rowLockReason;
                    loadedOffCount++;
                    Log("Profile loaded " + accountName + " Off because it was risk-locked when saved" + (string.IsNullOrWhiteSpace(rowLockReason) ? string.Empty : " by " + rowLockReason) + ".");
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
                            rowLoadedOffReason = "lead account is disconnected";
                            loadedOffCount++;
                            Log("Profile loaded " + accountName + " Off because lead " + rowLeadName + " is disconnected. Reconnect the lead, then turn it On.");
                        }
                    }
                }
                else if (rowEnabled)
                {
                    rowEnabled = false;
                    rowLoadedAvailableBecauseNoLead = true;
                    rowLoadedOffReason = "choose a lead account";
                    loadedOffCount++;
                    Log("Profile kept " + accountName + " available because no lead is saved. Choose a Lead and turn On to copy.");
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
                var invalidSizingReason = DisableLoadedRowWithInvalidSizing(row);
                if (!string.IsNullOrWhiteSpace(invalidSizingReason))
                {
                    rowLoadedOffReason = invalidSizingReason;
                    loadedOffCount++;
                }

                row.LastAction = GetLoadedProfileLastAction(row, rowWasAutoLocked, rowLoadedAvailableBecauseNoLead, rowLoadedOffReason);

                accountRows.Add(row);
                seenAccounts.Add(accountName);
            }
            SyncAccountRowsWithConnectedAccounts();
            RefreshConnectedAccountNames();
            RefreshAllRows();
            loadedOffCount += DisableLoadedRowsThatCannotStart();
            SyncLeadAccountSubscriptions();
            RefreshAllRows();
            return loadedOffCount;
        }

        private int DisableLoadedRowsThatCannotStart()
        {
            var targetRows = accountRows
                .Where(r => r != null && r.Enabled && r.SizingMode != SizingMode.Disabled)
                .ToList();
            if (targetRows.Count == 0)
                return 0;

            var disabledCount = 0;
            var desiredLeadNames = BuildDesiredLeadNames(targetRows);
            var wasSuppressed = suppressEnableValidation;
            suppressEnableValidation = true;
            try
            {
                foreach (var row in targetRows)
                {
                    string skipReason;
                    if (CanEnableRow(row, desiredLeadNames, out skipReason))
                        continue;

                    var friendlyReason = DescribeReadinessSkipReason(skipReason);
                    row.Enabled = false;
                    row.ManualLock = false;
                    row.LastAction = "Loaded Off: " + friendlyReason;
                    ClearLockedVirtualPositions(row);
                    ClearMaxNetVirtualPositions(row);
                    ClearMirroredTargetQuantities(row);
                    Log("Profile loaded " + row.AccountName + " Off because " + friendlyReason + ".");
                    disabledCount++;
                }
            }
            finally
            {
                suppressEnableValidation = wasSuppressed;
            }

            return disabledCount;
        }

        private string GetLoadedProfileLastAction(AccountCopyRow row, bool wasAutoLocked, bool loadedAvailableBecauseNoLead, string loadedOffReason)
        {
            if (wasAutoLocked)
                return "Loaded risk lock";

            if (row != null && row.Enabled)
                return row.ManualLock ? "Loaded manual lock" : "Loaded profile";

            if (loadedAvailableBecauseNoLead)
                return "Loaded available";

            return string.IsNullOrWhiteSpace(loadedOffReason)
                ? "Loaded Off"
                : "Loaded Off: " + loadedOffReason;
        }

        private string DisableLoadedRowWithInvalidSizing(AccountCopyRow row)
        {
            if (row == null || !row.Enabled)
                return string.Empty;

            var reason = GetInvalidSizingReason(row);
            if (string.IsNullOrWhiteSpace(reason))
                return string.Empty;

            row.Enabled = false;
            row.ManualLock = false;
            Log("Profile loaded " + row.AccountName + " Off because " + reason + ".");
            return reason;
        }

        private string GetInvalidSizingReason(AccountCopyRow row)
        {
            if (row == null)
                return "row is unavailable";

            if (row.SizingMode == SizingMode.Disabled)
                return "sizing is Off";

            if (row.SizingMode == SizingMode.Multiplier && row.Multiplier <= 0)
                return "multiplier must be greater than 0";

            if (row.SizingMode == SizingMode.Fixed && row.FixedQuantity <= 0)
                return "fixed quantity must be greater than 0";

            return string.Empty;
        }

        private IEnumerable<XmlElement> GetProfileRowElements(XmlElement root)
        {
            if (root == null)
                yield break;

            foreach (XmlNode node in root.SelectNodes("Row"))
            {
                var element = node as XmlElement;
                if (element != null)
                    yield return element;
            }

            foreach (XmlNode node in root.SelectNodes("Follower"))
            {
                var element = node as XmlElement;
                if (element != null)
                    yield return element;
            }
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
            try
            {
                var directory = GetProfileDirectoryPath();
                if (!Directory.Exists(directory))
                    return new List<string>();

                return Directory.GetFiles(directory, "*" + ProfileFileExtension)
                    .Select(Path.GetFileNameWithoutExtension)
                    .OrderBy(name => name)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log("Profile list unavailable: " + ex.Message);
                return new List<string>();
            }
        }

        private string GetProfileDirectoryPath()
        {
            return Path.Combine(
                GetCurrentUserDocumentsPath(),
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

        private string GetCurrentUserDocumentsPath()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
                throw new InvalidOperationException("Unable to resolve the current Windows user's Documents folder. Check Windows known-folder settings or reinstall NinjaTrader for this Windows user.");

            return documents;
        }

        private bool TryGetProfilePathForAction(string profileName, string action, out string profilePath)
        {
            profilePath = string.Empty;
            try
            {
                profilePath = GetProfilePath(profileName);
                return true;
            }
            catch (Exception ex)
            {
                var actionText = string.IsNullOrWhiteSpace(action) ? "use" : action;
                SetStatus("Cannot " + actionText + " profile.", ex.Message);
                Log("ERROR cannot " + actionText + " profile: " + ex.Message);
                return false;
            }
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

        private void DryRunCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (!isCopying)
                UpdateStartPauseButtonState();
        }

        private void UpdateStartPauseButtonState()
        {
            if (startPauseButton == null)
                return;

            if (isCopying)
            {
                startPauseButton.IsEnabled = true;
                startPauseButton.Content = dryRunMode ? "Pause Dry Run" : "Pause Copying";
                startPauseButton.Background = Brushes.DarkOrange;
                startPauseButton.ToolTip = dryRunMode
                    ? "Pause the dry-run simulation. Copier-submitted positions were simulated only."
                    : "Pause live copying. Existing positions are left untouched.";
                return;
            }

            var dryRunArmed = dryRunCheckBox != null && dryRunCheckBox.IsChecked == true;
            startPauseButton.Content = dryRunArmed ? "Start Dry Run" : "Start Copying";
            startPauseButton.Background = dryRunArmed ? Brushes.SteelBlue : Brushes.SeaGreen;
            var hasOnRows = accountRows.Any(IsConfiguredCopyRow);
            var hasConnectedOnRows = accountRows.Any(IsConnectedCopyRow);
            startPauseButton.IsEnabled = hasConnectedOnRows;
            if (!hasOnRows)
            {
                startPauseButton.ToolTip = dryRunArmed
                    ? "Turn at least one copy row On before starting a dry run."
                    : "Turn at least one copy row On before starting.";
                return;
            }

            if (!hasConnectedOnRows)
            {
                startPauseButton.ToolTip = dryRunArmed
                    ? "Reconnect at least one On copy row before starting a dry run."
                    : "Reconnect at least one On copy row before starting.";
                return;
            }

            startPauseButton.ToolTip = dryRunArmed
                ? "Start a simulation session after preflight validation; active row risk baselines reset. Copied and reconcile orders are logged only."
                : "Start live copying for On rows after preflight validation; active row risk baselines reset.";
        }

        private void StartCopyingTrades()
        {
            var onRows = accountRows.Where(IsConfiguredCopyRow).ToList();
            if (onRows.Count == 0)
            {
                SetStatus("Turn on at least one copy row before starting.");
                return;
            }

            if (!onRows.Any(RowHasConnectedAccount))
            {
                SetStatus("Reconnect at least one On copy row before starting.");
                return;
            }

            var validationIssues = ValidateReadyToStart();
            if (validationIssues.Count > 0)
            {
                var validationMessage = FormatStartBlockedMessage(validationIssues);
                SetStatus(validationMessage, FormatStartBlockedDetail(validationIssues));
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
            if (dryRunCheckBox != null)
                dryRunCheckBox.IsEnabled = false;

            UpdateStartPauseButtonState();
            var startMessage = BuildStartStatusMessage();
            SetStatus(startMessage);
            Log(startMessage);
            RefreshAllRows();
        }

        private string BuildStartStatusMessage()
        {
            var leadCount = GetConfiguredLeadAccounts().Count;
            var entryActiveCount = accountRows.Count(IsEntryActiveRow);
            var exitsOnlyCount = accountRows.Count(IsExitsOnlyCopyRow);
            var lockedCount = accountRows.Count(IsLockedCopyRow);

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
            if (dryRunCheckBox != null)
                dryRunCheckBox.IsEnabled = true;

            UpdateStartPauseButtonState();
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
                .Where(IsConnectedCopyRow)
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

            Dispatcher.InvokeAsync(() => CopyOrderToCopyRows(args.Order));
        }

        private bool IsCopierGeneratedOrder(Order order)
        {
            if (order == null || string.IsNullOrWhiteSpace(order.Name))
                return false;

            return string.Equals(order.Name, "ATC Copy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(order.Name, "ATC Flatten", StringComparison.OrdinalIgnoreCase)
                || string.Equals(order.Name, "ATC Reconcile", StringComparison.OrdinalIgnoreCase);
        }

        private void CopyOrderToCopyRows(Order sourceOrder)
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
                    var zeroSizingReason = DescribeZeroSizingReason(row, sourceOrder.Filled);
                    row.LastAction = "Sizing 0: " + zeroSizingReason;
                    Log(row.AccountName + " skipped " + GetInstrumentName(sourceOrder.Instrument) + " because " + zeroSizingReason + ".");
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

                var maxNetCap = GetMaxNetCapResult(row, sourceOrder.Instrument, sourceOrder.OrderAction, quantityToSubmit);
                var beforeMaxNetQuantity = maxNetCap.RequestedQuantity;
                quantityToSubmit = maxNetCap.Quantity;
                var maxNetCapped = maxNetCap.WasCapped;

                if (quantityToSubmit <= 0)
                {
                    mirroredTargetQuantities[targetKey] = desiredQuantity;
                    row.LastAction = "Max net blocked: " + BuildMaxNetCapSummary(maxNetCap);
                    Log(row.AccountName + " blocked " + GetInstrumentName(sourceOrder.Instrument) + " because max net would be exceeded (" + BuildMaxNetCapSummary(maxNetCap) + ").");
                    continue;
                }

                if (maxNetCapped)
                    Log(row.AccountName + " capped " + GetInstrumentName(sourceOrder.Instrument) + " copy from " + beforeMaxNetQuantity + " to " + quantityToSubmit + " by max net (" + BuildMaxNetCapSummary(maxNetCap) + ").");

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
                        row.LastAction = "Dry run " + DescribeOrder(sourceOrder.OrderAction, quantityToSubmit, sourceOrder.Instrument) + (maxNetCapped ? " (max net cap)" : string.Empty);
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
                    row.LastAction = "Sent " + DescribeOrder(sourceOrder.OrderAction, quantityToSubmit, sourceOrder.Instrument) + (maxNetCapped ? " (max net cap)" : string.Empty);
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

        private string DescribeZeroSizingReason(AccountCopyRow row, int baseQuantity)
        {
            if (row == null)
                return "sizing produced 0 contracts";

            if (baseQuantity <= 0)
                return "lead quantity is 0";

            switch (row.SizingMode)
            {
                case SizingMode.Multiplier:
                    return "floor(" + baseQuantity.ToString(CultureInfo.InvariantCulture)
                        + " x " + row.Multiplier.ToString("0.####", CultureInfo.InvariantCulture)
                        + ") = 0";
                case SizingMode.Fixed:
                    return row.FixedQuantity <= 0 ? "fixed quantity is 0" : "lead quantity is 0";
                case SizingMode.BalanceRatio:
                    return DescribeBalanceRatioZeroSizing(row, baseQuantity);
                case SizingMode.Disabled:
                    return "sizing is off";
                default:
                    return "sizing produced 0 contracts";
            }
        }

        private string DescribeBalanceRatioZeroSizing(AccountCopyRow row, int baseQuantity)
        {
            double leadBalance;
            double copyRowBalance;
            var rowLead = ResolveLeadAccountForRow(row);
            if (rowLead == null || !TryGetSizingBalance(rowLead, out leadBalance) || !TryGetSizingBalance(row.Account, out copyRowBalance) || leadBalance <= 0 || copyRowBalance <= 0)
                return "balance-ratio data is unavailable";

            var ratio = copyRowBalance / leadBalance;
            return "floor(" + baseQuantity.ToString(CultureInfo.InvariantCulture)
                + " x " + ratio.ToString("0.####", CultureInfo.InvariantCulture)
                + ") = 0";
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
                    AddStartIssue(issues, row, DescribeReadinessSkipReason(skipReason));
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

        private string FormatStartBlockedDetail(List<string> issues)
        {
            if (issues == null || issues.Count == 0)
                return string.Empty;

            return "Start blocked issues:" + Environment.NewLine
                + string.Join(Environment.NewLine, issues.Select(issue => "- " + issue));
        }

        private string DescribeReadinessSkipReason(string skipReason)
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
                case "lead account":
                    return "lead accounts stay off";
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
            double copyRowBalance;
            var rowLead = ResolveLeadAccountForRow(row);

            if (rowLead == null || !TryGetSizingBalance(rowLead, out leadBalance) || !TryGetSizingBalance(row.Account, out copyRowBalance))
            {
                row.SetStatus("Error", "No balance data");
                row.LastAction = "Balance sizing skipped";
                Log(row.AccountName + " skipped balance-ratio sizing because balance data is unavailable.");
                return 0;
            }

            if (leadBalance <= 0 || copyRowBalance <= 0)
                return 0;

            return (int)Math.Floor(sourceFilledQuantity * copyRowBalance / leadBalance);
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

        private bool IsEntryActiveRow(AccountCopyRow row)
        {
            return IsConnectedCopyRow(row) && !row.IsEntryLocked && row.CopyMode != TradeCopyMode.ExitsOnly;
        }

        private bool IsExitsOnlyCopyRow(AccountCopyRow row)
        {
            return IsConnectedCopyRow(row) && !row.IsEntryLocked && row.CopyMode == TradeCopyMode.ExitsOnly;
        }

        private bool IsLockedCopyRow(AccountCopyRow row)
        {
            return IsConnectedCopyRow(row) && row.IsEntryLocked;
        }

        private bool IsUnlockableRow(AccountCopyRow row)
        {
            return row != null && row.IsEntryLocked;
        }

        private bool IsBaselineResettableRow(AccountCopyRow row)
        {
            return RowHasConnectedAccount(row);
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
            return GetMaxNetCapResult(row, instrument, action, requestedQuantity).Quantity;
        }

        private MaxNetCapResult GetMaxNetCapResult(AccountCopyRow row, Instrument instrument, OrderAction action, int requestedQuantity)
        {
            var result = new MaxNetCapResult
            {
                RequestedQuantity = Math.Max(0, requestedQuantity),
                Quantity = Math.Max(0, requestedQuantity),
                Limit = row != null ? row.MaxNetPosition : 0,
                CurrentSigned = 0,
                RequestedSignedResult = 0
            };

            if (row == null || row.MaxNetPosition <= 0 || requestedQuantity <= 0)
                return result;

            result.CurrentSigned = GetMaxNetVirtualPosition(row, instrument);
            var signedDelta = IsBuyAction(action) ? requestedQuantity : -requestedQuantity;
            result.RequestedSignedResult = result.CurrentSigned + signedDelta;

            if (Math.Abs(result.RequestedSignedResult) <= row.MaxNetPosition)
                return result;

            result.Quantity = signedDelta > 0
                ? Math.Min(requestedQuantity, Math.Max(0, row.MaxNetPosition - result.CurrentSigned))
                : Math.Min(requestedQuantity, Math.Max(0, result.CurrentSigned + row.MaxNetPosition));

            return result;
        }

        private string BuildMaxNetCapSummary(MaxNetCapResult result)
        {
            if (result == null || result.Limit <= 0)
                return "no max net cap";

            return "current " + DescribeSignedQuantity(result.CurrentSigned)
                + ", requested " + DescribeSignedQuantity(result.RequestedSignedResult)
                + ", limit " + result.Limit.ToString(CultureInfo.InvariantCulture);
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

        private void ClearMirroredTargetQuantities(AccountCopyRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.AccountName))
                return;

            var suffix = "|" + row.AccountName;
            foreach (var key in mirroredTargetQuantities.Keys.Where(k => k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList())
                mirroredTargetQuantities.Remove(key);
        }

        private bool IsBuyAction(OrderAction action)
        {
            return action == OrderAction.Buy || action == OrderAction.BuyToCover;
        }

        private bool IsSellAction(OrderAction action)
        {
            return action == OrderAction.Sell || action == OrderAction.SellShort;
        }

        private void FlattenOnButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

            var rows = accountRows.Where(r => r.Enabled).ToList();
            if (rows.Count == 0)
            {
                SetStatus("No On rows to flatten.");
                return;
            }

            if (MessageBox.Show(BuildFlattenRowsPrompt("On", rows), "Confirm Flatten On", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            FlattenRows(rows, "Manual On-row flatten");
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

            if (MessageBox.Show(BuildFlattenRowsPrompt("selected", rows), "Confirm Flatten Selection", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            FlattenRows(rows, "Manual selection flatten");
            RefreshAllRows();
        }

        private void FlattenAllButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

            var wasCopying = isCopying;
            var leadAccounts = GetConfiguredLeadAccounts();
            var leadCount = leadAccounts.Count;
            var candidateAccounts = GetFlattenAllCandidateAccounts(leadAccounts);
            var accounts = candidateAccounts
                .Where(a => a.ConnectionStatus == ConnectionStatus.Connected)
                .ToList();
            var skippedOfflineCount = CountSkippedOfflineFlattenAllAccounts(candidateAccounts);

            if (accounts.Count == 0)
            {
                var noAccountsMessage = "No connected table or lead accounts to flatten.";
                if (skippedOfflineCount > 0)
                    noAccountsMessage = noAccountsMessage.TrimEnd('.') + "; skipped " + skippedOfflineCount + " offline account(s).";

                SetStatus(noAccountsMessage);
                return;
            }

            if (MessageBox.Show(BuildFlattenAllPrompt(accounts, leadCount, skippedOfflineCount, wasCopying), "Confirm Flatten All", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            mirroredTargetQuantities.Clear();

            foreach (var account in accounts)
                FlattenAccount(account, "Manual flatten all");

            foreach (var row in accountRows.Where(IsOfflineRow))
                row.LastAction = "Manual flatten all skipped - offline";

            var message = "Flatten all requested for " + accounts.Count + " connected account(s)";
            if (skippedOfflineCount > 0)
                message += "; skipped " + skippedOfflineCount + " offline account(s)";

            message += wasCopying ? "; copying remains active." : ".";
            SetStatus(message);
            Log(message);
            RefreshAllRows();
        }

        private List<Account> GetFlattenAllCandidateAccounts(IEnumerable<Account> leadAccounts)
        {
            var safeLeadAccounts = leadAccounts ?? Enumerable.Empty<Account>();
            return safeLeadAccounts
                .Concat(accountRows.Select(r => r.Account))
                .Where(a => a != null)
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private int CountSkippedOfflineFlattenAllAccounts(IEnumerable<Account> candidateAccounts)
        {
            var safeCandidateAccounts = candidateAccounts ?? Enumerable.Empty<Account>();
            return safeCandidateAccounts
                .Where(a => a != null && a.ConnectionStatus != ConnectionStatus.Connected)
                .Select(a => a.Name)
                .Concat(accountRows.Where(r => r.Account == null).Select(r => r.AccountName))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        private void LockRowsForManualFlatten(IEnumerable<AccountCopyRow> rows, string lastAction)
        {
            foreach (var row in rows.Where(r => r != null).Distinct().ToList())
            {
                SetManualLockWithoutAction(row, true);
                row.LastAction = lastAction + " locked";
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
                ClearMirroredTargetQuantities(row);
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
            var accountSummary = BuildRowAccountPromptLine(rows);
            if (!string.IsNullOrEmpty(accountSummary))
                prompt += "\n" + accountSummary;

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

        private string BuildRowAccountPromptLine(IList<AccountCopyRow> rows)
        {
            var accountNames = rows == null
                ? new List<string>()
                : rows
                    .Where(r => r != null)
                    .Select(r => string.IsNullOrWhiteSpace(r.AccountName) ? "Unknown" : r.AccountName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (accountNames.Count == 0)
                return string.Empty;

            var preview = accountNames.Take(8).ToList();
            var message = "Accounts: " + string.Join(", ", preview);
            if (accountNames.Count > preview.Count)
                message += ", +" + (accountNames.Count - preview.Count).ToString(CultureInfo.InvariantCulture) + " more";

            return message + ".";
        }

        private string BuildFlattenAllPrompt(IList<Account> accounts, int leadCount, int skippedOfflineCount, bool copyingActive)
        {
            var accountCount = accounts == null ? 0 : accounts.Count(a => a != null);
            var prompt = "Flatten all " + accountCount + " account(s)"
                + (leadCount > 0 ? ", including " + leadCount + " active lead account(s)" : string.Empty)
                + "?\n\n"
                + "This cancels active orders and closes open positions across each account.\n"
                + "Row symbol filters are not applied to Flatten All.";

            if (skippedOfflineCount > 0)
                prompt += "\n" + skippedOfflineCount + " offline account(s) will be skipped.";

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

            if (!rows.Any(CanAttemptReconcileRow))
            {
                SetStatus("Select an On copy row with a connected Lead to reconcile.");
                return;
            }

            if (MessageBox.Show(BuildReconcileRowsPrompt(rows), "Confirm Reconcile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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
            var invalidReasons = BuildReconcileInvalidReasons(rows);
            var invalidCount = invalidReasons.Values.Sum();

            var prompt = "Reconcile " + rowCount + " selected row(s) to their lead positions?\n\n"
                + "This may submit market orders using each row's lead, sizing, copy mode, and max position settings.";
            var accountSummary = BuildRowAccountPromptLine(rows);
            if (!string.IsNullOrEmpty(accountSummary))
                prompt += "\n" + accountSummary;

            if (offlineCount > 0)
                prompt += "\n" + offlineCount + " offline row(s) will be skipped.";

            if (filteredCount > 0)
                prompt += "\n" + filteredCount + " row(s) will only reconcile matching Symbols filters.";

            if (lockedCount > 0)
                prompt += "\n" + lockedCount + " locked or exits-only row(s) will only reduce current exposure.";

            if (autoCloseCount > 0)
                prompt += "\n" + autoCloseCount + " auto-close risk-locked row(s) will be skipped.";

            if (invalidCount > 0)
                prompt += "\n" + invalidCount + " row(s) are not eligible and will be skipped: " + FormatReasonCounts(invalidReasons) + ".";

            if (cappedCount > 0)
                prompt += "\n" + cappedCount + " row(s) have Max Net caps.";

            if (IsDryRunSelected())
                prompt += "\nDry Run is on, so reconcile orders will be simulated.";

            return prompt;
        }

        private Dictionary<string, int> BuildReconcileInvalidReasons(IList<AccountCopyRow> rows)
        {
            var reasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (rows == null)
                return reasons;

            foreach (var row in rows.Where(r => r != null))
            {
                if (!RowHasConnectedAccount(row))
                    continue;

                if (row.AutoLocked && row.LimitAction == RiskAction.HardFlatten)
                    continue;

                var reason = ValidateRowForReconcile(row);
                if (!string.IsNullOrWhiteSpace(reason))
                    IncrementReason(reasons, reason);
            }

            return reasons;
        }

        private bool CanAttemptReconcileRow(AccountCopyRow row)
        {
            if (row == null || !RowHasConnectedAccount(row))
                return false;

            if (row.AutoLocked && row.LimitAction == RiskAction.HardFlatten)
                return false;

            return string.IsNullOrEmpty(ValidateRowForReconcile(row));
        }

        private string FormatReasonCounts(Dictionary<string, int> reasons)
        {
            if (reasons == null || reasons.Count == 0)
                return string.Empty;

            return string.Join(", ", reasons
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key)
                .Select(pair => pair.Value + " " + pair.Key));
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

            var reduceOnlyReason = RowIsReduceOnly(row) ? GetReduceOnlyReason(row) : string.Empty;
            if (!string.IsNullOrEmpty(reduceOnlyReason))
                Log(row.AccountName + " reconcile is reduce-only because " + reduceOnlyReason + ". New exposure is blocked.");

            int zeroSizingCount;
            string zeroSizingReason;
            var desiredPositions = BuildDesiredPositions(row, out zeroSizingCount, out zeroSizingReason);
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
                row.LastAction = submitted > 0
                    ? "Dry run reconcile " + submitted + " order(s)"
                    : zeroSizingCount > 0 ? "Reconcile sizing 0: " + zeroSizingReason : string.IsNullOrEmpty(reduceOnlyReason) ? "Already reconciled" : "Reduce-only reconciled";
                Log(row.AccountName + " dry-run reconcile complete; orders simulated: " + submitted + (string.IsNullOrEmpty(reduceOnlyReason) ? "." : "; reduce-only."));
            }
            else
            {
                row.LastAction = submitted > 0
                    ? "Reconcile sent " + submitted + " order(s)"
                    : zeroSizingCount > 0 ? "Reconcile sizing 0: " + zeroSizingReason : string.IsNullOrEmpty(reduceOnlyReason) ? "Already reconciled" : "Reduce-only reconciled";
                Log(row.AccountName + " reconcile complete; orders sent: " + submitted + (string.IsNullOrEmpty(reduceOnlyReason) ? "." : "; reduce-only."));
            }

            if (zeroSizingCount > 0)
                Log(row.AccountName + " reconcile skipped " + zeroSizingCount + " lead position(s) because " + zeroSizingReason + ".");

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
            if (AccountHasCopyRows(row.AccountName, row))
                return "row is a lead account";

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
                double copyRowBalance;
                if (!TryGetSizingBalance(rowLead, out leadBalance) || leadBalance <= 0)
                    return "lead balance data is unavailable";

                if (!TryGetSizingBalance(row.Account, out copyRowBalance) || copyRowBalance <= 0)
                    return "account balance data is unavailable";
            }

            return string.Empty;
        }

        private Dictionary<string, PositionSnapshot> BuildDesiredPositions(AccountCopyRow row, out int zeroSizingCount, out string zeroSizingReason)
        {
            zeroSizingCount = 0;
            zeroSizingReason = string.Empty;
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
                    if (string.IsNullOrEmpty(zeroSizingReason))
                        zeroSizingReason = DescribeZeroSizingReason(row, leadPosition.Quantity);
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
            var maxNetCap = GetMaxNetCapResult(row, instrument, action, requestedQuantity);
            var quantity = maxNetCap.Quantity;

            if (quantity <= 0)
            {
                row.LastAction = "Reconcile max net blocked: " + BuildMaxNetCapSummary(maxNetCap);
                Log(row.AccountName + " reconcile blocked " + GetInstrumentName(instrument) + " because max net would be exceeded (" + BuildMaxNetCapSummary(maxNetCap) + ").");
                return false;
            }

            if (maxNetCap.WasCapped)
                Log(row.AccountName + " capped reconcile from " + maxNetCap.RequestedQuantity + " to " + quantity + " by max net (" + BuildMaxNetCapSummary(maxNetCap) + ").");

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

        private FlattenRequestResult FlattenAccount(Account account, string reason)
        {
            return FlattenAccount(account, reason, null, null);
        }

        private FlattenRequestResult FlattenRow(AccountCopyRow row, string reason)
        {
            if (row == null)
                return new FlattenRequestResult { FlattenFailed = true };

            var hasSymbolFilter = HasInstrumentFilter(row);
            return FlattenAccount(row.Account, reason, hasSymbolFilter ? new Func<Instrument, bool>(instrument => RowAllowsInstrument(row, instrument)) : null, row);
        }

        private FlattenRequestResult FlattenAccount(Account account, string reason, Func<Instrument, bool> instrumentFilter, AccountCopyRow statusRow)
        {
            var result = new FlattenRequestResult();
            if (account == null)
            {
                result.FlattenFailed = true;
                return result;
            }

            try
            {
                result.CancelOrderFailures = CancelActiveOrders(account, instrumentFilter);
                result.CloseOrdersSubmitted = CloseOpenPositions(account, instrumentFilter, result);
                Log(account.Name + " flatten requested" + (instrumentFilter == null ? string.Empty : " for matching symbols") + ": " + reason + ".");

                if (result.HasFailures)
                {
                    var row = statusRow ?? accountRows.FirstOrDefault(r => r.Account == account);
                    if (row != null)
                    {
                        row.SetStatus("Error", result.CloseOrdersSubmitted > 0 ? "Flatten partial" : "Flatten failed");
                        row.LastAction = result.CloseOrdersSubmitted > 0 ? "Flatten partial" : "Flatten failed";
                    }
                }
            }
            catch (Exception ex)
            {
                result.FlattenFailed = true;
                Log("ERROR " + account.Name + " flatten failed: " + ex.Message);
                var row = statusRow ?? accountRows.FirstOrDefault(r => r.Account == account);
                if (row != null)
                {
                    row.SetStatus("Error", "Flatten failed");
                    row.LastAction = "Flatten failed";
                }
            }

            return result;
        }

        private int CancelActiveOrders(Account account, Func<Instrument, bool> instrumentFilter)
        {
            var failures = 0;
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
                    failures++;
                    Log("ERROR " + account.Name + " cancel failed: " + ex.Message);
                }
            }

            return failures;
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

        private int CloseOpenPositions(Account account, Func<Instrument, bool> instrumentFilter, FlattenRequestResult result)
        {
            var submitted = 0;
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
                    submitted++;
                    Log(account.Name + " closing " + DescribeOrder(closeAction, quantity, position.Instrument) + ".");
                }
                catch (Exception ex)
                {
                    if (result != null)
                        result.CloseOrderFailures++;

                    Log("ERROR " + account.Name + " close failed: " + ex.Message);
                }
            }

            return submitted;
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

        private void ToggleSelectedEnabledButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

            var rows = GetSelectedRows();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to turn on or off.");
                return;
            }

            var offRows = rows.Where(r => !r.Enabled).ToList();
            if (offRows.Any(r => r.CanToggleEnabled))
            {
                EnableRows(offRows, "selected");
                return;
            }

            var onRows = rows.Where(r => r.Enabled).ToList();
            if (onRows.Count > 0)
            {
                DisableRows(onRows, "selected");
                return;
            }

            SetStatus("Selected rows are not ready to turn on.");
        }

        private void DisableRows(IEnumerable<AccountCopyRow> rows, string scopeDescription)
        {
            var targetRows = rows == null ? new List<AccountCopyRow>() : rows.Where(r => r != null).Distinct().ToList();
            if (targetRows.Count == 0)
            {
                SetStatus("No rows to turn off.");
                return;
            }

            foreach (var row in targetRows)
            {
                row.Enabled = false;
                SetManualLockWithoutAction(row, false);
                row.LastAction = row.AutoLocked ? "Turned off - risk lock preserved" : "Turned off";
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
                ClearMirroredTargetQuantities(row);
            }

            SyncLeadAccountSubscriptions();
            RefreshAllRows();
            var message = "Turned off " + targetRows.Count + " " + scopeDescription + " row(s).";
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

            var unlockableRows = rows.Where(IsUnlockableRow).ToList();
            if (unlockableRows.Count == 0)
            {
                SetStatus("Selected rows are not locked.");
                return;
            }

            if (!ConfirmRiskLockClear(unlockableRows, "Confirm Unlock Risk Locks", "Unlock", "This clears auto risk locks. Connected risk-locked rows will also reset their session PnL baselines. On rows may become eligible to copy again."))
                return;

            var autoResetCount = 0;
            var skippedBaselineCount = 0;
            foreach (var row in unlockableRows)
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
                row.AutoCloseRetryPending = false;
                row.AutoCloseNeedsReview = false;
                row.LastAction = wasAutoLocked
                    ? baselineReset ? "Unlocked - baseline reset" : "Unlocked - baseline unchanged"
                    : "Unlocked";
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
                ClearMirroredTargetQuantities(row);
            }

            var message = "Unlocked " + unlockableRows.Count + " selected row(s)" + (autoResetCount > 0 ? "; reset baselines for " + autoResetCount + " risk-locked row(s)" : string.Empty) + ".";
            if (skippedBaselineCount > 0)
                message = message.TrimEnd('.') + "; skipped baseline reset for " + skippedBaselineCount + " offline row(s).";

            SetStatus(message);
            Log(message);
            RefreshAllRows();
        }

        private bool ConfirmRiskLockClear(IList<AccountCopyRow> rows, string title, string actionName, string consequence)
        {
            var riskLockedRows = rows == null
                ? new List<AccountCopyRow>()
                : rows.Where(r => r != null && r.AutoLocked).Distinct().ToList();

            if (riskLockedRows.Count == 0)
                return true;

            var prompt = actionName + " " + riskLockedRows.Count + " risk-locked row(s)?\n\n" + consequence;
            var accountSummary = BuildRowAccountPromptLine(riskLockedRows);
            if (!string.IsNullOrEmpty(accountSummary))
                prompt += "\n" + accountSummary;

            return MessageBox.Show(prompt, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
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

            var resettableRows = rows.Where(IsBaselineResettableRow).ToList();
            if (resettableRows.Count == 0)
            {
                SetStatus("Selected rows are offline, so baselines cannot be reset.");
                return;
            }

            if (!ConfirmRiskLockClear(resettableRows, "Confirm Reset Risk Locks", "Reset baselines for", "This sets session PnL baselines to current account PnL and clears auto risk locks. On rows may become eligible to copy again."))
                return;

            var resetCount = 0;
            var skippedCount = 0;
            foreach (var row in rows.Except(resettableRows))
            {
                if (row != null)
                    row.LastAction = "Baseline reset skipped - offline";

                skippedCount++;
            }

            foreach (var row in resettableRows)
            {
                row.ResetBaseline(ReadAccountPnl(row.Account));
                row.LastAction = "Baseline reset";
                ClearLockedVirtualPositions(row);
                ClearMaxNetVirtualPositions(row);
                ClearMirroredTargetQuantities(row);
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
                SetStatus("No rows to turn on.");
                return;
            }

            var targetRowSet = new HashSet<AccountCopyRow>(targetRows);
            var desiredLeadNames = accountRows
                .Where(r => !targetRowSet.Contains(r) && r.Enabled && r.SizingMode != SizingMode.Disabled && !string.IsNullOrWhiteSpace(r.LeadAccountName))
                .Select(r => r.LeadAccountName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var enabledCount = 0;
            var liveBaselineResetCount = 0;
            var skipReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in targetRows)
            {
                var shouldResetLiveBaseline = isCopying && !row.Enabled;
                string skipReason;
                if (!TryEnableRow(row, desiredLeadNames, out skipReason))
                {
                    var friendlyReason = DescribeReadinessSkipReason(skipReason);
                    row.LastAction = "Turn-on skipped: " + friendlyReason;
                    IncrementReason(skipReasons, friendlyReason);
                    continue;
                }

                enabledCount++;
                AddDesiredLeadName(row, desiredLeadNames);
                if (shouldResetLiveBaseline)
                    liveBaselineResetCount++;
            }

            SyncLeadAccountSubscriptions();
            RefreshAllRows();
            var message = "Turned on " + enabledCount + " " + scopeDescription + " row(s)" + FormatSkipReasons(skipReasons);
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

        private void AddDesiredLeadName(AccountCopyRow row, List<string> desiredLeadNames)
        {
            if (row == null
                || row.SizingMode == SizingMode.Disabled
                || string.IsNullOrWhiteSpace(row.LeadAccountName)
                || desiredLeadNames.Any(leadName => AccountNamesEqual(leadName, row.LeadAccountName)))
                return;

            desiredLeadNames.Add(row.LeadAccountName);
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
            ClearMirroredTargetQuantities(row);
        }

        private string GetEnableLastAction(AccountCopyRow row, bool liveBaselineReset)
        {
            if (isCopying)
            {
                if (row.AutoLocked)
                    return liveBaselineReset ? "Turned on live - still risk locked" : "Turned on - still risk locked";

                return liveBaselineReset ? "Turned on live - baseline reset" : "Turned on";
            }

            return row.AutoLocked ? "Turned on - still risk locked" : "Turned on";
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

            if (AccountHasCopyRows(row.AccountName, row))
            {
                skipReason = "lead account";
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
                SetStatus("Select one source copy row before copying setup to peers.");
                return;
            }

            if (selectedRows.Count > 1)
            {
                SetStatus("Select exactly one source copy row before copying setup to peers.");
                return;
            }

            var source = selectedRows[0];
            var sourceBlockReason = GetCopySettingsSourceBlockReason(source);
            if (!string.IsNullOrEmpty(sourceBlockReason))
            {
                SetStatus(sourceBlockReason);
                return;
            }

            var leadName = source.LeadAccountName;
            if (string.IsNullOrWhiteSpace(leadName))
            {
                SetStatus("Select a copy row with a lead before copying setup to peers.");
                return;
            }

            var rows = GetCopySettingsTargetRows(source);
            if (rows.Count == 0)
            {
                SetStatus("No peer rows use lead " + leadName + ".");
                return;
            }

            if (isCopying)
            {
                var prompt = "Copy setup from " + source.AccountName + " to " + rows.Count + " peer row(s) using lead " + leadName + " while copying is active? Active target row baselines will be reset. Lead selections stay unchanged.";
                var accountSummary = BuildRowAccountPromptLine(rows);
                if (!string.IsNullOrEmpty(accountSummary))
                    prompt += "\n" + accountSummary;

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
                    if (isCopying && IsConnectedCopyRow(row))
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

            var autoCloseRequestedCount = ApplyPendingAutoCloseActions(rows);
            mirroredTargetQuantities.Clear();
            SyncLeadAccountSubscriptions();
            var message = "Copied setup from " + source.AccountName + " to " + appliedCount + " peer row(s) using lead " + leadName;
            if (liveBaselineResetCount > 0)
                message += "; reset baselines for " + liveBaselineResetCount + " live row(s)";

            if (autoCloseRequestedCount > 0)
                message += "; auto-close requested for " + autoCloseRequestedCount + " risk-locked row(s)";

            message += ". Lead selections were left unchanged.";
            SetStatus(message);
            Log(message);
            RefreshAllRows();
        }

        private void ApplyRowPresetButton_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();

            var rows = GetSelectedRows();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows before applying a row preset.");
                return;
            }

            var preset = rowPresetComboBox != null ? rowPresetComboBox.SelectedItem as RowPresetOption : null;
            if (preset == null)
            {
                SetStatus("Choose a row preset before applying.");
                return;
            }

            var targetRows = rows.Where(CanApplyRowPresetToRow).ToList();
            var skippedLeadCount = rows.Count - targetRows.Count;
            if (targetRows.Count == 0)
            {
                SetStatus("Row presets apply to copy or available rows; selected lead rows were skipped.");
                return;
            }

            if (isCopying)
            {
                var prompt = "Apply row preset " + preset.Label + " to " + targetRows.Count + " selected row(s) while copying is active?\n\n"
                    + "Connected live copy rows will be paused with baselines reset so you can review before unlocking.";
                if (skippedLeadCount > 0)
                    prompt += "\n" + skippedLeadCount + " lead row(s) will be skipped.";

                var accountSummary = BuildRowAccountPromptLine(targetRows);
                if (!string.IsNullOrEmpty(accountSummary))
                    prompt += "\n" + accountSummary;

                if (MessageBox.Show(prompt, "Confirm Live Row Preset", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            var appliedCount = 0;
            var livePausedCount = 0;
            suppressLiveSettingsPause = true;
            try
            {
                foreach (var row in targetRows)
                {
                    ApplyRowPreset(row, preset);
                    row.LastAction = "Applied preset " + preset.Label;
                    if (isCopying && IsConnectedCopyRow(row))
                    {
                        row.ResetBaseline(ReadAccountPnl(row.Account), false);
                        SetManualLockWithoutAction(row, true);
                        row.LastAction = "Preset " + preset.Label + " applied - row paused";
                        livePausedCount++;
                    }

                    ClearLockedVirtualPositions(row);
                    ClearMaxNetVirtualPositions(row);
                    ClearMirroredTargetQuantities(row);
                    appliedCount++;
                }
            }
            finally
            {
                suppressLiveSettingsPause = false;
            }

            var autoCloseRequestedCount = ApplyPendingAutoCloseActions(targetRows);
            SyncLeadAccountSubscriptions();
            var message = "Applied row preset " + preset.Label + " to " + appliedCount + " selected row(s)";
            if (livePausedCount > 0)
                message += "; paused " + livePausedCount + " live row(s) for review";

            if (autoCloseRequestedCount > 0)
                message += "; auto-close requested for " + autoCloseRequestedCount + " risk-locked row(s)";

            if (skippedLeadCount > 0)
                message += "; skipped " + skippedLeadCount + " lead row(s)";

            message += ".";
            SetStatus(message);
            Log(message);
            RefreshAllRows();
        }

        private bool CanApplyRowPresetToRow(AccountCopyRow row)
        {
            return row != null && !AccountHasCopyRows(row.AccountName, row);
        }

        private void ApplyRowPreset(AccountCopyRow row, RowPresetOption preset)
        {
            if (row == null || preset == null)
                return;

            var wasSuppressed = suppressSizingModeAutoSwitch;
            suppressSizingModeAutoSwitch = true;
            try
            {
                if (preset.Multiplier.HasValue)
                    row.Multiplier = preset.Multiplier.Value;

                if (preset.FixedQuantity.HasValue)
                    row.FixedQuantity = preset.FixedQuantity.Value;

                if (preset.CopyMode.HasValue)
                    row.CopyMode = preset.CopyMode.Value;

                if (preset.LimitAction.HasValue)
                    row.LimitAction = preset.LimitAction.Value;

                if (preset.SizingMode.HasValue)
                    row.SizingMode = preset.SizingMode.Value;
            }
            finally
            {
                suppressSizingModeAutoSwitch = wasSuppressed;
            }
        }

        private int ApplyPendingAutoCloseActions(IEnumerable<AccountCopyRow> rows)
        {
            if (!isCopying || rows == null)
                return 0;

            var requestedCount = 0;
            foreach (var row in rows.Where(r => r != null).Distinct().ToList())
            {
                if (!ShouldRequestPendingAutoClose(row))
                    continue;

                RequestRiskAutoClose(row, string.IsNullOrEmpty(row.LockReason) ? "Risk limit" : row.LockReason);
                requestedCount++;
            }

            return requestedCount;
        }

        private bool ShouldRequestPendingAutoClose(AccountCopyRow row)
        {
            return row != null
                && row.Enabled
                && row.AutoLocked
                && row.LimitAction == RiskAction.HardFlatten
                && RowHasConnectedAccount(row)
                && !row.AutoCloseRequested
                && (!dryRunMode || !row.AutoCloseDryRunRequested);
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
            RefreshLeadAccountOptions();
            ClearLeadSelectionsForLeadRows();
            EnforceLeadRowsStayOff();

            foreach (var row in accountRows.ToList())
            {
                if (!RowIsReduceOnly(row))
                    ClearLockedVirtualPositions(row);

                if (!isCopying)
                    ClearMaxNetVirtualPositions(row);

                RefreshRowMetrics(row);
                EvaluateRisk(row);
                UpdateRowRole(row);
                UpdateRowStatus(row);
            }

            RefreshLeadAccountOptions();
            UpdateSelectedActionButtons();
            RefreshStatusSummary();
        }

        private void EnforceLeadRowsStayOff()
        {
            var rows = accountRows
                .Where(r => r != null && r.Enabled && AccountHasCopyRows(r.AccountName, r))
                .ToList();

            if (rows.Count == 0)
                return;

            var wasSuppressed = suppressEnableValidation;
            suppressEnableValidation = true;
            try
            {
                foreach (var row in rows)
                {
                    row.Enabled = false;
                    SetManualLockWithoutAction(row, false);
                    row.LastAction = "Lead account - turned off";
                    ClearLockedVirtualPositions(row);
                    ClearMaxNetVirtualPositions(row);
                    ClearMirroredTargetQuantities(row);
                }
            }
            finally
            {
                suppressEnableValidation = wasSuppressed;
            }

            SyncLeadAccountSubscriptions();
            var message = "Lead rows turned off: " + rows.Count + ".";
            SetStatus(message, "Lead accounts drive copy rows and stay off.");
            Log(message + " Lead accounts drive copy rows and stay off.");
        }

        private void RefreshLeadRoleState()
        {
            RefreshLeadAccountOptions();
            ClearLeadSelectionsForLeadRows();
            EnforceLeadRowsStayOff();

            foreach (var candidate in accountRows.ToList())
            {
                UpdateRowRole(candidate);
                UpdateRowStatus(candidate);
            }

            UpdateSelectedActionButtons();
            RefreshLeadAccountOptions();
            RefreshStatusSummary();
        }

        private void ClearLeadSelectionsForLeadRows()
        {
            var leadRows = accountRows
                .Where(r => r != null
                    && !string.IsNullOrWhiteSpace(r.LeadAccountName)
                    && AccountHasCopyRows(r.AccountName, r))
                .ToList();

            if (leadRows.Count == 0)
                return;

            var wasEnableSuppressed = suppressEnableValidation;
            var wasLiveSuppressed = suppressLiveSettingsPause;
            var wasLeadRefreshSuppressed = suppressLeadRoleRefresh;
            suppressEnableValidation = true;
            suppressLiveSettingsPause = true;
            suppressLeadRoleRefresh = true;
            try
            {
                foreach (var row in leadRows)
                {
                    row.LeadAccountName = string.Empty;
                    row.LastAction = "Lead account - own lead cleared";
                    ClearLockedVirtualPositions(row);
                    ClearMaxNetVirtualPositions(row);
                    ClearMirroredTargetQuantities(row);
                }
            }
            finally
            {
                suppressEnableValidation = wasEnableSuppressed;
                suppressLiveSettingsPause = wasLiveSuppressed;
                suppressLeadRoleRefresh = wasLeadRefreshSuppressed;
            }
        }

        private void RefreshRowMetrics(AccountCopyRow row)
        {
            if (row.Account == null)
                return;

            var previousConnectionStatus = row.ConnectionStatus;
            row.ConnectionStatus = row.Account.ConnectionStatus.ToString();
            HandleRowConnectionStatusChange(row, previousConnectionStatus);
            if (row.Account.ConnectionStatus != ConnectionStatus.Connected)
            {
                row.NetPosition = 0;
                row.PositionSummary = "Offline";
                return;
            }

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
            ClearMirroredTargetQuantities(row);
            Log(row.AccountName + " disconnected; copied orders blocked until reconnect.");
        }

        private void EvaluateRisk(AccountCopyRow row)
        {
            if (isCopying
                && row != null
                && row.AutoLocked
                && RowHasConnectedAccount(row)
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
            if (!isCopying || row.AutoLocked || !RowHasConnectedAccount(row) || !row.Enabled || row.SizingMode == SizingMode.Disabled)
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
            if (!RowHasConnectedAccount(row))
                return;

            if (row.AutoCloseRequested)
            {
                if (!row.AutoCloseNeedsReview)
                    row.LastAction = reason + " - auto close already requested";
                return;
            }

            if (dryRunMode && row.AutoCloseDryRunRequested)
            {
                row.LastAction = reason + " - dry run auto close already simulated";
                return;
            }

            if (dryRunMode)
            {
                row.AutoCloseDryRunRequested = true;
                row.AutoCloseRetryPending = false;
                row.AutoCloseNeedsReview = false;
                AutoCloseRiskLockedRow(row, reason);
                return;
            }

            row.AutoCloseDryRunRequested = false;
            var result = AutoCloseRiskLockedRow(row, reason);
            if (result.ShouldRememberAutoCloseRequest)
            {
                row.AutoCloseRequested = true;
                row.AutoCloseRetryPending = false;
                row.AutoCloseNeedsReview = result.HasFailures;
                if (row.AutoCloseNeedsReview)
                    UpdateAutoLockedRowStatus(row);

                return;
            }

            row.AutoCloseRequested = false;
            row.AutoCloseRetryPending = true;
            row.AutoCloseNeedsReview = false;
            row.LastAction = reason + " - auto close retry pending";
            UpdateAutoLockedRowStatus(row);
            Log(row.AccountName + " auto-close by " + reason + " did not submit a close order; will retry while the risk lock remains active.");
        }

        private FlattenRequestResult AutoCloseRiskLockedRow(AccountCopyRow row, string reason)
        {
            if (row == null || row.Account == null)
                return new FlattenRequestResult { FlattenFailed = true };

            ClearLockedVirtualPositions(row);
            ClearMaxNetVirtualPositions(row);
            ClearMirroredTargetQuantities(row);

            if (dryRunMode)
            {
                row.LastAction = reason + " - dry run auto close";
                Log("DRY RUN " + row.AccountName + " would auto-close managed positions by " + reason + ". New copied orders are blocked.");
                return new FlattenRequestResult();
            }

            row.LastAction = reason + " - auto close";
            Log(row.AccountName + " auto-closing managed positions by " + reason + ". New copied orders are blocked.");
            var result = FlattenRow(row, reason);
            if (result.ShouldRememberAutoCloseRequest && result.HasFailures)
            {
                row.LastAction = reason + " - auto close partial";
                row.SetStatus("Error", "Auto-close partial", "Some auto-close requests were submitted, but at least one cancel or close action failed. Check the account.");
                Log(row.AccountName + " auto-close by " + reason + " had errors after a close request was submitted. Check the account before unlocking.");
            }

            return result;
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

            if (AccountHasCopyRows(row.AccountName, row))
            {
                row.SetStatus("Disabled", "Lead", "Lead account. It can drive copy rows but does not receive copied orders.");
                return;
            }

            if (row.AutoLocked)
            {
                UpdateAutoLockedRowStatus(row);
                return;
            }

            if (!row.Enabled || row.SizingMode == SizingMode.Disabled)
            {
                row.SetStatus("Disabled", GetDisabledRowStatusText(row), GetDisabledRowStatusDetail(row));
                return;
            }

            var rowLead = ResolveLeadAccountForRow(row);
            if (rowLead == null)
            {
                row.SetStatus("Error", "Lead missing");
                return;
            }

            if (AccountNamesEqual(row.AccountName, rowLead.Name) || AccountHasCopyRows(row.AccountName, row))
            {
                row.SetStatus("Error", "Lead conflict", "This account is also assigned as a lead. Lead accounts cannot receive copied entries.");
                return;
            }

            if (row.ManualLock)
            {
                row.SetStatus("Locked", "Locked", "Manual lock: exits only. Unlock this row to allow new entries.");
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
                row.SetStatus("ExitsOnly", "Exits only", isCopying
                    ? "Copy mode blocks new entries and follows exits only."
                    : "Ready to follow exits only when copying starts.");
                return;
            }

            var nearRiskStatus = GetNearRiskLimitStatusText(row);
            if (!string.IsNullOrEmpty(nearRiskStatus))
            {
                row.SetStatus("Warning", nearRiskStatus);
                return;
            }

            if (LastActionShowsSizingZero(row))
            {
                row.SetStatus("Warning", "Sizing 0", row.LastAction);
                return;
            }

            row.SetStatus(isCopying ? "Active" : "Ready", isCopying ? "Copying" : "Ready");
        }

        private string GetRiskLockStatusText(AccountCopyRow row)
        {
            var action = row != null && row.LimitAction == RiskAction.HardFlatten ? "Auto-close row" : "Entries locked";
            var reason = row == null ? string.Empty : FormatRiskReasonForStatus(row.LockReason);
            return string.IsNullOrEmpty(reason) ? action : action + " - " + reason;
        }

        private void UpdateAutoLockedRowStatus(AccountCopyRow row)
        {
            var detail = GetRiskLockStatusText(row);
            if (row != null && row.LimitAction == RiskAction.HardFlatten)
            {
                if (row.AutoCloseNeedsReview)
                {
                    row.SetStatus("Error", "Check close", detail + ". At least one close order was submitted, but a cancel or close request failed. Check the account before unlocking.");
                    return;
                }

                if (row.AutoCloseRetryPending)
                {
                    row.SetStatus("Error", "Retry close", detail + ". No close order has submitted yet; the copier will retry while copying is active.");
                    return;
                }
            }

            row.SetStatus("Locked", "Locked", detail);
        }

        private bool LastActionShowsSizingZero(AccountCopyRow row)
        {
            if (row == null || string.IsNullOrEmpty(row.LastAction))
                return false;

            return string.Equals(row.LastAction, "Sizing produced 0", StringComparison.OrdinalIgnoreCase)
                || row.LastAction.StartsWith("Sizing 0", StringComparison.OrdinalIgnoreCase)
                || row.LastAction.StartsWith("Reconcile sizing 0", StringComparison.OrdinalIgnoreCase);
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
                return "Off";

            if (AccountHasCopyRows(row.AccountName, row))
                return "Lead";

            if (string.IsNullOrWhiteSpace(row.LeadAccountName))
                return "Available";

            var rowLead = ResolveLeadAccountForRow(row);
            if (rowLead == null)
                return "Lead missing";

            if (AccountNamesEqual(row.AccountName, rowLead.Name))
                return "Self-copy";

            if (IsActiveCopyAccount(rowLead.Name, row))
                return "Lead copying";

            if (row.SizingMode == SizingMode.Multiplier && row.Multiplier <= 0)
                return "Check sizing";

            if (row.SizingMode == SizingMode.Fixed && row.FixedQuantity <= 0)
                return "Check sizing";

            return "Off";
        }

        private string GetDisabledRowStatusDetail(AccountCopyRow row)
        {
            if (row.SizingMode == SizingMode.Disabled)
                return "Sizing is off. This row will not copy entries.";

            if (AccountHasCopyRows(row.AccountName, row))
                return "Lead account. It can drive copy rows but does not receive copied orders.";

            if (string.IsNullOrWhiteSpace(row.LeadAccountName))
                return "No lead selected. Choose a Lead to make this a copy row.";

            var rowLead = ResolveLeadAccountForRow(row);
            if (rowLead == null)
                return "The selected lead account is not connected.";

            if (AccountNamesEqual(row.AccountName, rowLead.Name))
                return "An account cannot copy itself.";

            if (IsActiveCopyAccount(rowLead.Name, row))
                return "The selected Lead is already an active copy row. Choose a lead account that is not copying another account.";

            if (row.SizingMode == SizingMode.Multiplier && row.Multiplier <= 0)
                return "Multiplier must be greater than 0.";

            if (row.SizingMode == SizingMode.Fixed && row.FixedQuantity <= 0)
                return "Fixed quantity must be greater than 0.";

            return "Row is off. Turn On to copy from the selected lead.";
        }

        private void UpdateRowRole(AccountCopyRow row)
        {
            var followerCount = CountCopyRows(row.AccountName, row);
            row.FollowerCount = followerCount;

            if (followerCount > 0)
            {
                row.RoleSummary = "Lead";
                return;
            }

            if (row.Account == null || row.Account.ConnectionStatus != ConnectionStatus.Connected)
            {
                row.RoleSummary = "Offline";
                return;
            }

            row.RoleSummary = string.IsNullOrWhiteSpace(row.LeadAccountName) ? "Available" : "Copy";
        }

        private string GetNearRiskLimitStatusText(AccountCopyRow row)
        {
            if (row.DailyLossLimit > 0)
            {
                var lossLimit = Math.Abs(row.DailyLossLimit);
                var lossUsed = Math.Max(0, -row.SessionPnl);
                if (lossUsed >= lossLimit)
                    return "Max loss hit";

                if (lossUsed >= lossLimit * 0.9)
                    return "Near max loss";
            }

            if (row.MaxDrawdown > 0)
            {
                var drawdownLimit = Math.Abs(row.MaxDrawdown);
                if (row.Drawdown >= drawdownLimit)
                    return "Max DD hit";

                if (row.Drawdown >= drawdownLimit * 0.9)
                    return "Near max DD";
            }

            if (row.ProfitTarget > 0)
            {
                var target = Math.Abs(row.ProfitTarget);
                var profitProgress = Math.Max(0, row.SessionPnl);
                if (profitProgress >= target)
                    return "Target hit";

                if (profitProgress >= target * 0.9)
                    return "Near target";
            }

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
                    double copyRowBalance;
                    var rowLead = ResolveLeadAccountForRow(row);
                    if (rowLead == null || !TryGetSizingBalance(rowLead, out leadBalance) || leadBalance <= 0 || !TryGetSizingBalance(row.Account, out copyRowBalance) || copyRowBalance <= 0)
                        return false;

                    desiredQuantity = (int)Math.Floor(baseQuantity * copyRowBalance / leadBalance);
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

            var onCount = accountRows.Count(IsConfiguredCopyRow);
            var entryActiveCount = accountRows.Count(IsEntryActiveRow);
            var exitsOnlyCount = accountRows.Count(IsExitsOnlyCopyRow);
            var leadCount = GetReferencedLeadAccountCount();
            var lockedCount = accountRows.Count(IsLockedCopyRow);
            var attentionCount = accountRows.Count(IsAttentionRow);
            var offlineCount = accountRows.Count(IsOfflineRow);

            var parts = new List<string>
            {
                mode
            };

            parts.Add(onCount > 0 ? "On " + onCount : "No On rows");

            if (leadCount > 0)
                parts.Add("Leads " + leadCount);

            if (entryActiveCount > 0 && entryActiveCount != onCount)
                parts.Add("Entries " + entryActiveCount);

            if (exitsOnlyCount > 0)
                parts.Add("Exits-only " + exitsOnlyCount);

            if (lockedCount > 0)
                parts.Add("Locked " + lockedCount);

            if (attentionCount > 0)
                parts.Add("Review " + attentionCount);

            if (offlineCount > 0)
                parts.Add("Offline " + offlineCount);

            return string.Join(" | ", parts);
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

        private bool IsAttentionRow(AccountCopyRow row)
        {
            if (row == null)
                return false;

            return row.StatusLevel == "Error"
                || row.StatusLevel == "Desynced"
                || row.StatusLevel == "Warning";
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
                var onCount = rows.Count(r => r.Enabled);
                var offCount = rows.Count - onCount;
                var offlineCount = rows.Count(IsOfflineRow);
                var copyRowCount = rows.Count(IsConfiguredCopyRow);
                var lockedCount = rows.Count(r => IsConfiguredCopyRow(r) && r.IsEntryLocked);
                var attentionCount = rows.Count(IsAttentionRow);
                var parts = new List<string> { "Selected " + rows.Count };
                parts.Add(BuildAccountNamePreview(rows, 8));

                if (onCount > 0)
                    parts.Add("On " + onCount);

                if (offCount > 0)
                    parts.Add("Off " + offCount);

                if (offlineCount > 0)
                    parts.Add("Offline " + offlineCount);

                if (copyRowCount > 0)
                    parts.Add("Copy " + copyRowCount);

                if (lockedCount > 0)
                    parts.Add("Locked " + lockedCount);

                if (attentionCount > 0)
                    parts.Add("Review " + attentionCount);

                return string.Join(" | ", parts);
            }

            var row = rows[0];
            if (ShouldUseSimpleSelectionSummary(row))
                return BuildSimpleSelectionSummary(row);

            var relationship = DescribeSelectedCopyRelationship(row);
            var sizing = DescribeSizing(row);
            var risk = DescribeRisk(row);
            var riskNow = DescribeRiskProgressForSelection(row);
            var summary = relationship + " | " + row.Status + " | " + sizing + " | " + risk;
            return string.IsNullOrEmpty(riskNow) ? summary : summary + " | now " + riskNow;
        }

        private string BuildSimpleSelectionSummary(AccountCopyRow row)
        {
            var label = BuildSelectedRowLabel(row);
            if (row == null || string.IsNullOrWhiteSpace(row.Status))
                return label;

            if (SelectionLabelAlreadyShowsState(label, row.Status))
                return label;

            if (string.Equals(row.Status, "Lead missing", StringComparison.OrdinalIgnoreCase)
                && SelectionLabelAlreadyShowsState(label, "Needs lead"))
                return label;

            if (string.Equals(row.Status, "Self-copy", StringComparison.OrdinalIgnoreCase)
                && SelectionLabelAlreadyShowsState(label, "Self-copy"))
                return label;

            return label + " | " + row.Status;
        }

        private bool SelectionLabelAlreadyShowsState(string label, string state)
        {
            return !string.IsNullOrWhiteSpace(label)
                && !string.IsNullOrWhiteSpace(state)
                && label.IndexOf(" | " + state, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string BuildAccountNamePreview(IList<AccountCopyRow> rows, int previewCount)
        {
            if (rows == null || rows.Count == 0)
                return "none";

            var safePreviewCount = Math.Max(1, previewCount);
            var names = rows
                .Where(r => r != null)
                .Select(r => string.IsNullOrWhiteSpace(r.AccountName) ? "Unknown" : r.AccountName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count == 0)
                return "none";

            var preview = names.Take(safePreviewCount).ToList();
            var label = string.Join(", ", preview);
            if (names.Count > preview.Count)
                label += " +" + (names.Count - preview.Count).ToString(CultureInfo.InvariantCulture);

            return label;
        }

        private string BuildSelectedRowLabel(AccountCopyRow row)
        {
            if (row == null)
                return "No selection";

            if (ShouldUseSimpleSelectionSummary(row))
                return DescribeSimpleSelectedRow(row);

            return DescribeSelectedCopyRelationship(row);
        }

        private string DescribeSimpleSelectedRow(AccountCopyRow row)
        {
            if (row == null)
                return "No selection";

            if (string.Equals(row.RoleSummary, "Lead", StringComparison.OrdinalIgnoreCase))
                return row.AccountName + " | Lead";

            if (string.Equals(row.RoleSummary, "Conflict", StringComparison.OrdinalIgnoreCase))
                return row.AccountName + " | Lead conflict";

            if (AccountNamesEqual(row.AccountName, row.LeadAccountName))
                return row.AccountName + " | Self-copy";

            if (string.IsNullOrWhiteSpace(row.LeadAccountName))
                return row.AccountName + " | " + (string.Equals(row.PlanSummary, "Needs lead", StringComparison.OrdinalIgnoreCase) ? "Needs lead" : "Available");

            return row.AccountName + " <- " + row.LeadAccountName;
        }

        private bool ShouldUseSimpleSelectionSummary(AccountCopyRow row)
        {
            return row == null
                || string.Equals(row.RoleSummary, "Lead", StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.RoleSummary, "Available", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(row.LeadAccountName);
        }

        private string DescribeSelectedLead(AccountCopyRow row)
        {
            if (row == null)
                return "unknown";

            if (string.Equals(row.RoleSummary, "Lead", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(row.PlanSummary) ? "Lead" : row.PlanSummary;

            if (string.Equals(row.RoleSummary, "Conflict", StringComparison.OrdinalIgnoreCase))
                return "Lead/copy conflict";

            if (AccountNamesEqual(row.AccountName, row.LeadAccountName))
                return "self-copy";

            if (!string.IsNullOrWhiteSpace(row.LeadAccountName))
                return row.LeadAccountName;

            return "Available";
        }

        private string DescribeSelectedCopyRelationship(AccountCopyRow row)
        {
            if (row == null)
                return "Unknown";

            if (AccountNamesEqual(row.AccountName, row.LeadAccountName))
                return row.AccountName + " | Self-copy";

            return row.AccountName + " <- " + DescribeSelectedLead(row);
        }

        private string DescribeSizing(AccountCopyRow row)
        {
            switch (row.SizingMode)
            {
                case SizingMode.Multiplier:
                    return "floor(fill x " + row.Multiplier.ToString("0.##", CultureInfo.InvariantCulture) + ")";
                case SizingMode.Fixed:
                    return "fixed " + row.FixedQuantity.ToString(CultureInfo.InvariantCulture) + "/order";
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

            var action = row.LimitAction == RiskAction.HardFlatten ? "auto-close row" : "lock entries only";
            if (parts.Count == 0)
                return row.LimitAction == RiskAction.HardFlatten
                    ? "no limits, auto-close row when set"
                    : "no limits";

            return "at " + string.Join(", ", parts) + ": " + action;
        }

        private string DescribeRiskProgressForSelection(AccountCopyRow row)
        {
            if (row == null || string.Equals(row.RiskProgressSummary, "No limits", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            return row.RiskProgressSummary;
        }

        private void SetStatus(string message)
        {
            SetStatus(message, string.Empty);
        }

        private void SetStatus(string message, string detail)
        {
            heldStatusMessage = message ?? string.Empty;
            heldStatusDetail = detail ?? string.Empty;
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
                var detail = string.IsNullOrWhiteSpace(heldStatusDetail) ? heldStatusMessage : heldStatusDetail;
                statusTextBlock.Text = heldStatusMessage;
                statusTextBlock.ToolTip = string.IsNullOrWhiteSpace(detail)
                    ? summary
                    : detail + Environment.NewLine + Environment.NewLine + summary;
                return;
            }

            heldStatusMessage = string.Empty;
            heldStatusDetail = string.Empty;
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

        private class MaxNetCapResult
        {
            public int RequestedQuantity { get; set; }
            public int Quantity { get; set; }
            public int CurrentSigned { get; set; }
            public int RequestedSignedResult { get; set; }
            public int Limit { get; set; }

            public bool WasCapped
            {
                get { return Quantity < RequestedQuantity; }
            }
        }

        private class FlattenRequestResult
        {
            public int CancelOrderFailures { get; set; }
            public int CloseOrdersSubmitted { get; set; }
            public int CloseOrderFailures { get; set; }
            public bool FlattenFailed { get; set; }

            public bool HasFailures
            {
                get { return FlattenFailed || CancelOrderFailures > 0 || CloseOrderFailures > 0; }
            }

            public bool ShouldRememberAutoCloseRequest
            {
                get { return CloseOrdersSubmitted > 0 || !HasFailures; }
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

        private class RowPresetOption
        {
            public RowPresetOption(string label, string description, TradeCopyMode? copyMode, SizingMode? sizingMode, double? multiplier, int? fixedQuantity, RiskAction? limitAction)
            {
                Label = label;
                Description = description;
                CopyMode = copyMode;
                SizingMode = sizingMode;
                Multiplier = multiplier;
                FixedQuantity = fixedQuantity;
                LimitAction = limitAction;
            }

            public string Label { get; private set; }
            public string Description { get; private set; }
            public TradeCopyMode? CopyMode { get; private set; }
            public SizingMode? SizingMode { get; private set; }
            public double? Multiplier { get; private set; }
            public int? FixedQuantity { get; private set; }
            public RiskAction? LimitAction { get; private set; }
        }

        private class AccountCopyRow : INotifyPropertyChanged
        {
            private bool enabled;
            private string leadAccountName = string.Empty;
            private TradeCopyMode copyMode = TradeCopyMode.All;
            private string connectionStatus = "Unknown";
            private string status = "Ready";
            private string statusDetail = "Ready";
            private string statusLevel = "Ready";
            private string roleSummary = "Available";
            private int followerCount;
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
            private bool autoCloseRetryPending;
            private bool autoCloseNeedsReview;
            private string lockReason = string.Empty;
            private string lastAction = "Ready";

            public AccountCopyRow(Account account, double baselinePnl)
            {
                Account = account;
                AccountName = account != null ? account.Name : string.Empty;
                BaselinePnl = baselinePnl;
                PeakPnl = 0;
                LeadAccountOptions = new ObservableCollection<string>();
                LeadAccountOptions.Add(string.Empty);
            }

            public AccountCopyRow(string accountName, double baselinePnl)
            {
                AccountName = accountName ?? string.Empty;
                BaselinePnl = baselinePnl;
                connectionStatus = "Not connected";
                positionSummary = "No account";
                PeakPnl = 0;
                LeadAccountOptions = new ObservableCollection<string>();
                LeadAccountOptions.Add(string.Empty);
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public Account Account { get; private set; }
            public string AccountName { get; private set; }
            public ObservableCollection<string> LeadAccountOptions { get; private set; }
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

            public bool CanToggleEnabled
            {
                get
                {
                    if (Enabled)
                        return true;

                    if (string.Equals(RoleSummary, "Lead", StringComparison.OrdinalIgnoreCase))
                        return false;

                    if (Account == null || Account.ConnectionStatus != NinjaTrader.Cbi.ConnectionStatus.Connected)
                        return false;

                    if (SizingMode == SizingMode.Disabled)
                        return false;

                    if (string.IsNullOrWhiteSpace(LeadAccountName))
                        return false;

                    if (string.Equals(AccountName ?? string.Empty, LeadAccountName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        return false;

                    if (string.Equals(Status, "Lead missing", StringComparison.OrdinalIgnoreCase))
                        return false;

                    if (string.Equals(Status, "Lead copying", StringComparison.OrdinalIgnoreCase))
                        return false;

                    return true;
                }
            }

            public string EnableTooltip
            {
                get
                {
                    if (Enabled)
                        return "Turn this copy row off. The row stays visible and saved.";

                    if (string.Equals(RoleSummary, "Lead", StringComparison.OrdinalIgnoreCase))
                        return "Lead account. It stays off and can drive copy rows.";

                    if (Account == null || Account.ConnectionStatus != NinjaTrader.Cbi.ConnectionStatus.Connected)
                        return "Connect this account in NinjaTrader before turning this row on.";

                    if (SizingMode == SizingMode.Disabled)
                        return "Choose an active Sizing mode before turning this row on.";

                    if (string.IsNullOrWhiteSpace(LeadAccountName))
                        return "Choose a Lead before turning this row on.";

                    if (string.Equals(AccountName ?? string.Empty, LeadAccountName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        return "An account cannot copy itself. Choose a different Lead.";

                    if (string.Equals(Status, "Lead missing", StringComparison.OrdinalIgnoreCase))
                        return "The selected Lead is not connected.";

                    if (string.Equals(Status, "Lead copying", StringComparison.OrdinalIgnoreCase))
                        return "The selected Lead is already an active copy row. Choose a different Lead.";

                    return "Turn this copy row on.";
                }
            }

            public bool CanEditLeadSelection
            {
                get
                {
                    return !string.Equals(RoleSummary, "Lead", StringComparison.OrdinalIgnoreCase);
                }
            }

            public string LeadSelectionTooltip
            {
                get
                {
                    if (string.Equals(RoleSummary, "Lead", StringComparison.OrdinalIgnoreCase))
                        return "This account is a lead because another row follows it. Clear copy-row Lead selections before this account can copy another account.";

                    if (Enabled)
                        return "Changing the Lead on an On row may pause it for review while copying is active.";

                    return "Choose the account this row should copy. The list hides this account and active copy rows; leave blank to keep it available.";
                }
            }

            public bool CanEditCopySetup
            {
                get
                {
                    return !string.Equals(RoleSummary, "Lead", StringComparison.OrdinalIgnoreCase);
                }
            }

            public bool CanToggleManualLock
            {
                get
                {
                    if (ManualLock)
                        return true;

                    if (!Enabled || AutoLocked)
                        return false;

                    if (string.Equals(RoleSummary, "Lead", StringComparison.OrdinalIgnoreCase))
                        return false;

                    if (SizingMode == SizingMode.Disabled || string.IsNullOrWhiteSpace(LeadAccountName))
                        return false;

                    return true;
                }
            }

            public string ManualLockTooltip
            {
                get
                {
                    if (ManualLock)
                        return "Clear manual lock so this row can take new copied entries again.";

                    if (AutoLocked)
                        return "Risk lock is active. Use Unlock or Reset Baselines to clear it.";

                    if (string.Equals(RoleSummary, "Lead", StringComparison.OrdinalIgnoreCase))
                        return "Lead accounts stay off and do not receive copied entries.";

                    if (!Enabled)
                        return "Turn this copy row On before using Manual Lock.";

                    if (SizingMode == SizingMode.Disabled)
                        return "Choose an active Sizing mode before using Manual Lock.";

                    if (string.IsNullOrWhiteSpace(LeadAccountName))
                        return "Choose a Lead before using Manual Lock.";

                    return "Block new entries for this On copy row while still allowing exits.";
                }
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

            public string StatusDetail
            {
                get { return statusDetail; }
                private set { SetField(ref statusDetail, value, "StatusDetail"); }
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

            public int FollowerCount
            {
                get { return followerCount; }
                set { SetField(ref followerCount, Math.Max(0, value), "FollowerCount"); }
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

            public string RiskProgressSummary
            {
                get { return BuildRiskProgressSummary(); }
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

            public bool AutoCloseRetryPending
            {
                get { return autoCloseRetryPending; }
                set { SetField(ref autoCloseRetryPending, value, "AutoCloseRetryPending"); }
            }

            public bool AutoCloseNeedsReview
            {
                get { return autoCloseNeedsReview; }
                set { SetField(ref autoCloseNeedsReview, value, "AutoCloseNeedsReview"); }
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
                    AutoCloseRetryPending = false;
                    AutoCloseNeedsReview = false;
                    LockReason = string.Empty;
                }

                OnPropertyChanged("BaselinePnl");
            }

            public void SetStatus(string level, string text)
            {
                SetStatus(level, text, text);
            }

            public void SetStatus(string level, string text, string detail)
            {
                StatusLevel = level;
                Status = text;
                StatusDetail = string.IsNullOrWhiteSpace(detail) ? text : detail;
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
                var leadSummary = BuildLeadSummary();
                if (ShouldUseLeadOnlyPlan(leadSummary))
                    return leadSummary;

                var parts = new List<string>
                {
                    leadSummary,
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

            private bool ShouldUseLeadOnlyPlan(string leadSummary)
            {
                return string.Equals(leadSummary, "Lead", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(leadSummary, "Lead account", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(leadSummary) && leadSummary.StartsWith("Lead for ", StringComparison.OrdinalIgnoreCase))
                    || string.Equals(leadSummary, "Lead/copy conflict", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(leadSummary, "Self-copy", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(leadSummary, "Needs lead", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(leadSummary, "Available", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(leadSummary) && leadSummary.StartsWith("Missing lead ", StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(leadSummary) && leadSummary.StartsWith("Lead copying ", StringComparison.OrdinalIgnoreCase));
            }

            private string BuildLeadSummary()
            {
                if (string.Equals(RoleSummary, "Lead", StringComparison.OrdinalIgnoreCase))
                    return "Lead";

                if (string.Equals(RoleSummary, "Conflict", StringComparison.OrdinalIgnoreCase))
                    return "Lead/copy conflict";

                if (!string.IsNullOrWhiteSpace(LeadAccountName)
                    && string.Equals(AccountName ?? string.Empty, LeadAccountName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    return "Self-copy";

                if (!string.IsNullOrWhiteSpace(LeadAccountName)
                    && string.Equals(Status, "Lead missing", StringComparison.OrdinalIgnoreCase))
                    return "Missing lead " + LeadAccountName;

                if (!string.IsNullOrWhiteSpace(LeadAccountName)
                    && string.Equals(Status, "Lead copying", StringComparison.OrdinalIgnoreCase))
                    return "Lead copying " + LeadAccountName;

                if (!string.IsNullOrWhiteSpace(LeadAccountName))
                    return "Lead " + LeadAccountName;

                return Enabled && SizingMode != SizingMode.Disabled
                    ? "Needs lead"
                    : "Available";
            }

            private string BuildSizingSummary()
            {
                string sizing;
                switch (SizingMode)
                {
                    case SizingMode.Multiplier:
                        sizing = "floor(fill x " + Multiplier.ToString("0.##", CultureInfo.InvariantCulture) + ")";
                        break;
                    case SizingMode.Fixed:
                        sizing = "fixed " + FixedQuantity.ToString(CultureInfo.InvariantCulture) + "/order";
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
                    caps.Add("net max " + MaxNetPosition.ToString(CultureInfo.InvariantCulture));

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

                return symbols.Count == 0 ? string.Empty : "only " + string.Join(",", symbols);
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

                var action = LimitAction == RiskAction.HardFlatten ? "auto-close row" : "lock entries only";
                if (limits.Count == 0)
                    return LimitAction == RiskAction.HardFlatten
                        ? "no limits, auto-close row when set"
                        : "no limits";

                return "at " + string.Join(", ", limits) + ": " + action;
            }

            private string BuildRiskProgressSummary()
            {
                if (DailyLossLimit <= 0 && MaxDrawdown <= 0 && ProfitTarget <= 0)
                    return "No limits";

                if (AutoLocked)
                {
                    var reason = FormatRiskReasonForProgress(LockReason);
                    if (string.IsNullOrEmpty(reason))
                        reason = "risk limit";

                    if (LimitAction == RiskAction.HardFlatten && AutoCloseNeedsReview)
                        return "Check close: " + reason;

                    if (LimitAction == RiskAction.HardFlatten && AutoCloseRetryPending)
                        return "Retry close: " + reason;

                    return (LimitAction == RiskAction.HardFlatten ? "Auto-close: " : "Locked: ") + reason;
                }

                var parts = new List<string>();
                if (DailyLossLimit > 0)
                {
                    var lossUsed = Math.Max(0, -SessionPnl);
                    parts.Add("Loss " + FormatCurrency(lossUsed) + "/" + FormatCurrency(DailyLossLimit));
                }

                if (MaxDrawdown > 0)
                    parts.Add("DD " + FormatCurrency(Drawdown) + "/" + FormatCurrency(MaxDrawdown));

                if (ProfitTarget > 0)
                {
                    var profitProgress = Math.Max(0, SessionPnl);
                    parts.Add("Target " + FormatCurrency(profitProgress) + "/" + FormatCurrency(ProfitTarget));
                }

                return string.Join(" | ", parts);
            }

            private string FormatRiskReasonForProgress(string reason)
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
                        return string.IsNullOrWhiteSpace(reason) ? "risk limit" : reason.ToLowerInvariant();
                }
            }

            private string FormatCurrency(double value)
            {
                return value.ToString("C0", CultureInfo.CurrentCulture);
            }

            private void NotifyDerivedProperties(string propertyName)
            {
                switch (propertyName)
                {
                    case "AccountName":
                    case "ConnectionStatus":
                    case "Enabled":
                    case "LeadAccountName":
                    case "SizingMode":
                    case "RoleSummary":
                    case "Status":
                    case "ManualLock":
                    case "AutoLocked":
                        OnPropertyChanged("CanToggleEnabled");
                        OnPropertyChanged("EnableTooltip");
                        OnPropertyChanged("CanEditLeadSelection");
                        OnPropertyChanged("LeadSelectionTooltip");
                        OnPropertyChanged("CanEditCopySetup");
                        OnPropertyChanged("CanToggleManualLock");
                        OnPropertyChanged("ManualLockTooltip");
                        break;
                }

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
                    case "AutoCloseRetryPending":
                    case "AutoCloseNeedsReview":
                    case "LockReason":
                    case "RoleSummary":
                    case "FollowerCount":
                    case "Status":
                        OnPropertyChanged("PlanSummary");
                        break;
                }

                switch (propertyName)
                {
                    case "SessionPnl":
                    case "Drawdown":
                    case "DailyLossLimit":
                    case "MaxDrawdown":
                    case "ProfitTarget":
                    case "AutoLocked":
                    case "AutoCloseRetryPending":
                    case "AutoCloseNeedsReview":
                    case "LockReason":
                        OnPropertyChanged("RiskProgressSummary");
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
