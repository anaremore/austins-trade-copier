#region Using declarations
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

        private const int DefaultFixedQuantity = 1;
        private const double DefaultMultiplier = 1.0;
        private const string DefaultGroupName = "Default";
        private const string ProfileFolderName = "AustinTradeCopier";
        private const string ProfileFileExtension = ".xml";
        private const int MaxEventLogLines = 500;

        private readonly ObservableCollection<AccountCopyRow> accountRows = new ObservableCollection<AccountCopyRow>();
        private readonly Dictionary<string, int> mirroredTargetQuantities = new Dictionary<string, int>();
        private readonly Dictionary<string, int> lockedVirtualPositions = new Dictionary<string, int>();
        private readonly Queue<string> eventLogLines = new Queue<string>();
        private readonly DispatcherTimer telemetryTimer;

        private Account leadAccount;
        private List<Account> connectedAccounts = new List<Account>();
        private bool isCopying;
        private bool dryRunMode;
        private string lastGroupListSignature = string.Empty;

        private ComboBox leadAccountComboBox;
        private ComboBox addAccountComboBox;
        private TextBox addGroupTextBox;
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
            Width = 1180;
            Height = 720;
            MinWidth = 980;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(36, 36, 38));

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
            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });

            var leadPanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };

            leadPanel.Children.Add(CreateLabel("Lead Account"));
            leadAccountComboBox = new ComboBox
            {
                DisplayMemberPath = "Name",
                Width = 220,
                Margin = new Thickness(8, 0, 18, 0),
                Padding = new Thickness(4)
            };
            leadAccountComboBox.SelectionChanged += LeadAccountComboBox_SelectionChanged;
            leadPanel.Children.Add(leadAccountComboBox);

            leadPanel.Children.Add(CreateLabel("Add Follower"));
            addAccountComboBox = new ComboBox
            {
                DisplayMemberPath = "Name",
                Width = 220,
                Margin = new Thickness(8, 0, 10, 0),
                Padding = new Thickness(4)
            };
            leadPanel.Children.Add(addAccountComboBox);

            leadPanel.Children.Add(CreateLabel("Group"));
            addGroupTextBox = new TextBox
            {
                Text = DefaultGroupName,
                Width = 120,
                Margin = new Thickness(8, 0, 10, 0),
                Padding = new Thickness(4)
            };
            leadPanel.Children.Add(addGroupTextBox);

            var addAccountButton = CreateButton("Add Account", Brushes.DimGray);
            addAccountButton.Click += AddAccountButton_Click;
            leadPanel.Children.Add(addAccountButton);

            Grid.SetRow(leadPanel, 0);
            root.Children.Add(leadPanel);

            var profilePanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };

            profilePanel.Children.Add(CreateLabel("Profile"));
            profileComboBox = new ComboBox
            {
                Width = 180,
                Margin = new Thickness(8, 0, 8, 0),
                Padding = new Thickness(4)
            };
            profileComboBox.SelectionChanged += ProfileComboBox_SelectionChanged;
            profilePanel.Children.Add(profileComboBox);

            profileNameTextBox = new TextBox
            {
                Text = "Default",
                Width = 160,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(4)
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

            Grid.SetRow(profilePanel, 1);
            root.Children.Add(profilePanel);

            var actionPanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };

            startPauseButton = CreateButton("Start Copying", Brushes.SeaGreen);
            startPauseButton.Width = 130;
            startPauseButton.Click += StartPauseButton_Click;
            actionPanel.Children.Add(startPauseButton);

            dryRunCheckBox = new CheckBox
            {
                Content = "Dry Run",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            actionPanel.Children.Add(dryRunCheckBox);

            var flattenFollowersButton = CreateButton("Flatten Followers", Brushes.Firebrick);
            flattenFollowersButton.Click += FlattenFollowersButton_Click;
            actionPanel.Children.Add(flattenFollowersButton);

            var flattenSelectedButton = CreateButton("Flatten Selected", Brushes.Firebrick);
            flattenSelectedButton.Click += FlattenSelectedButton_Click;
            actionPanel.Children.Add(flattenSelectedButton);

            var flattenAllButton = CreateButton("Flatten All", Brushes.DarkRed);
            flattenAllButton.Click += FlattenAllButton_Click;
            actionPanel.Children.Add(flattenAllButton);

            var reconcileSelectedButton = CreateButton("Reconcile Selected", Brushes.DimGray);
            reconcileSelectedButton.Click += ReconcileSelectedButton_Click;
            actionPanel.Children.Add(reconcileSelectedButton);

            var removeSelectedButton = CreateButton("Remove Selected", Brushes.DimGray);
            removeSelectedButton.Click += RemoveSelectedButton_Click;
            actionPanel.Children.Add(removeSelectedButton);

            var unlockSelectedButton = CreateButton("Unlock Selected", Brushes.DimGray);
            unlockSelectedButton.Click += UnlockSelectedButton_Click;
            actionPanel.Children.Add(unlockSelectedButton);

            var resetBaselineButton = CreateButton("Reset Baselines", Brushes.DimGray);
            resetBaselineButton.Click += ResetBaselinesButton_Click;
            actionPanel.Children.Add(resetBaselineButton);

            actionPanel.Children.Add(CreateLabel("Group"));
            groupComboBox = new ComboBox
            {
                Width = 140,
                Margin = new Thickness(8, 0, 8, 0),
                Padding = new Thickness(4)
            };
            actionPanel.Children.Add(groupComboBox);

            var enableGroupButton = CreateButton("Enable Group", Brushes.DimGray);
            enableGroupButton.Click += EnableGroupButton_Click;
            actionPanel.Children.Add(enableGroupButton);

            var pauseGroupButton = CreateButton("Pause Group", Brushes.DimGray);
            pauseGroupButton.Click += PauseGroupButton_Click;
            actionPanel.Children.Add(pauseGroupButton);

            var flattenGroupButton = CreateButton("Flatten Group", Brushes.Firebrick);
            flattenGroupButton.Click += FlattenGroupButton_Click;
            actionPanel.Children.Add(flattenGroupButton);

            var applyGroupSettingsButton = CreateButton("Apply Row Settings To Group", Brushes.DimGray);
            applyGroupSettingsButton.Click += ApplyGroupSettingsButton_Click;
            actionPanel.Children.Add(applyGroupSettingsButton);

            Grid.SetRow(actionPanel, 2);
            root.Children.Add(actionPanel);

            accountsGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Extended,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                ItemsSource = accountRows,
                RowStyle = CreateRowStyle(),
                Background = new SolidColorBrush(Color.FromRgb(43, 43, 46)),
                Foreground = Brushes.White,
                RowBackground = new SolidColorBrush(Color.FromRgb(50, 50, 54)),
                AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(44, 44, 48)),
                HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(72, 72, 76)),
                VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(72, 72, 76)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            AddGridColumns(accountsGrid);

            Grid.SetRow(accountsGrid, 3);
            root.Children.Add(accountsGrid);

            statusTextBlock = new TextBlock
            {
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
                Text = "Ready. Add follower accounts, set sizing and risk rules, then start copying."
            };
            Grid.SetRow(statusTextBlock, 4);
            root.Children.Add(statusTextBlock);

            var logPanel = new Grid();
            logPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            logPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var logButtonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var exportLogButton = CreateButton("Export Log", Brushes.DimGray);
            exportLogButton.Click += ExportLogButton_Click;
            logButtonPanel.Children.Add(exportLogButton);

            var clearLogButton = CreateButton("Clear Log", Brushes.DimGray);
            clearLogButton.Click += ClearLogButton_Click;
            logButtonPanel.Children.Add(clearLogButton);

            Grid.SetRow(logButtonPanel, 0);
            logPanel.Children.Add(logButtonPanel);

            eventLogTextBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 26)),
                Foreground = Brushes.Gainsboro,
                BorderBrush = new SolidColorBrush(Color.FromRgb(76, 76, 80)),
                TextWrapping = TextWrapping.NoWrap
            };
            Grid.SetRow(eventLogTextBox, 1);
            logPanel.Children.Add(eventLogTextBox);

            Grid.SetRow(logPanel, 5);
            root.Children.Add(logPanel);

            Content = root;
        }

        private Label CreateLabel(string text)
        {
            return new Label
            {
                Content = text,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0)
            };
        }

        private Button CreateButton(string text, Brush background)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Background = background,
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(92, 92, 96)),
                MinWidth = 92
            };
        }

        private void AddGridColumns(DataGrid grid)
        {
            grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "On",
                Binding = new Binding("Enabled") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(42)
            });

            grid.Columns.Add(CreateTextColumn("Account", "AccountName", 130, null, true));
            grid.Columns.Add(CreateTextColumn("Group", "GroupName", 110, null, false));
            grid.Columns.Add(CreateTextColumn("Conn", "ConnectionStatus", 90, null, true));
            grid.Columns.Add(CreateTextColumn("Status", "Status", 150, null, true));
            grid.Columns.Add(CreateTextColumn("Pos", "PositionSummary", 125, null, true));
            grid.Columns.Add(CreateTextColumn("Pnl", "SessionPnl", 80, "{0:C0}", true));
            grid.Columns.Add(CreateTextColumn("DD", "Drawdown", 80, "{0:C0}", true));
            grid.Columns.Add(CreateTextColumn("Symbols", "InstrumentFilter", 100, null, false));

            grid.Columns.Add(new DataGridComboBoxColumn
            {
                Header = "Sizing",
                ItemsSource = Enum.GetValues(typeof(SizingMode)),
                SelectedItemBinding = new Binding("SizingMode") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(105)
            });

            grid.Columns.Add(CreateTextColumn("Mult", "Multiplier", 70, "{0:0.##}", false));
            grid.Columns.Add(CreateTextColumn("Fixed", "FixedQuantity", 60, null, false));
            grid.Columns.Add(CreateTextColumn("Max", "MaxQuantity", 60, null, false));
            grid.Columns.Add(CreateTextColumn("Max Net", "MaxNetPosition", 70, null, false));
            grid.Columns.Add(CreateTextColumn("Loss", "DailyLossLimit", 75, "{0:0}", false));
            grid.Columns.Add(CreateTextColumn("DD Lim", "MaxDrawdown", 75, "{0:0}", false));
            grid.Columns.Add(CreateTextColumn("Target", "ProfitTarget", 75, "{0:0}", false));

            grid.Columns.Add(new DataGridComboBoxColumn
            {
                Header = "Limit Action",
                ItemsSource = Enum.GetValues(typeof(RiskAction)),
                SelectedItemBinding = new Binding("LimitAction") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(105)
            });

            grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "Manual Lock",
                Binding = new Binding("ManualLock") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(95)
            });

            grid.Columns.Add(CreateTextColumn("Last Action", "LastAction", 220, null, true));
        }

        private DataGridTextColumn CreateTextColumn(string header, string propertyName, double width, string stringFormat, bool readOnly)
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
                Header = header,
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
                var selectedLeadName = leadAccount != null ? leadAccount.Name : null;
                var selectedAddName = addAccountComboBox.SelectedItem is Account ? ((Account)addAccountComboBox.SelectedItem).Name : null;

                leadAccountComboBox.ItemsSource = connectedAccounts;
                addAccountComboBox.ItemsSource = connectedAccounts;

                if (!string.IsNullOrEmpty(selectedLeadName))
                    leadAccountComboBox.SelectedItem = connectedAccounts.FirstOrDefault(a => a.Name == selectedLeadName);

                if (!string.IsNullOrEmpty(selectedAddName))
                    addAccountComboBox.SelectedItem = connectedAccounts.FirstOrDefault(a => a.Name == selectedAddName);
            });
        }

        private void OnAccountStatusUpdate(object sender, AccountStatusEventArgs e)
        {
            RefreshAccountList();
        }

        private void LeadAccountComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (leadAccount != null)
                leadAccount.OrderUpdate -= OnOrderUpdate;

            leadAccount = leadAccountComboBox.SelectedItem as Account;
            mirroredTargetQuantities.Clear();

            if (leadAccount != null)
            {
                leadAccount.OrderUpdate += OnOrderUpdate;
                Log("Lead account set to " + leadAccount.Name + ".");
            }
        }

        private void AddAccountButton_Click(object sender, RoutedEventArgs e)
        {
            var account = addAccountComboBox.SelectedItem as Account;
            if (account == null)
            {
                SetStatus("Select a follower account before adding.");
                return;
            }

            if (leadAccount != null && account == leadAccount)
            {
                SetStatus("The lead account cannot also be a follower.");
                return;
            }

            if (accountRows.Any(r => r.Account == account))
            {
                SetStatus(account.Name + " is already in the follower dashboard.");
                return;
            }

            var groupName = NormalizeGroupName(addGroupTextBox.Text);
            var row = new AccountCopyRow(account, groupName, ReadAccountPnl(account));
            accountRows.Add(row);
            RefreshGroupList();
            Log("Added follower " + account.Name + " to group " + groupName + ".");
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
            root.SetAttribute("version", "1");
            root.SetAttribute("savedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            root.SetAttribute("leadAccount", leadAccount != null ? leadAccount.Name : string.Empty);
            document.AppendChild(root);

            foreach (var row in accountRows)
            {
                var rowElement = document.CreateElement("Follower");
                SetAttribute(rowElement, "account", row.AccountName);
                SetAttribute(rowElement, "group", row.GroupName);
                SetAttribute(rowElement, "enabled", row.Enabled);
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
            leadAccountComboBox.ItemsSource = connectedAccounts;
            addAccountComboBox.ItemsSource = connectedAccounts;
            leadAccountComboBox.SelectedItem = null;
            addAccountComboBox.SelectedItem = null;

            var document = new XmlDocument();
            document.Load(path);

            var root = document.DocumentElement;
            if (root == null || root.Name != "TradeCopierProfile")
                throw new InvalidOperationException("Invalid profile file.");

            var leadAccountName = root.GetAttribute("leadAccount");
            if (!string.IsNullOrEmpty(leadAccountName))
            {
                var lead = accounts.FirstOrDefault(a => string.Equals(a.Name, leadAccountName, StringComparison.OrdinalIgnoreCase));
                if (lead != null)
                    leadAccountComboBox.SelectedItem = lead;
                else
                    Log("Profile lead account " + leadAccountName + " is not connected.");
            }

            accountRows.Clear();
            mirroredTargetQuantities.Clear();
            lockedVirtualPositions.Clear();

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
                    Log("Profile skipped duplicate follower " + accountName + ".");
                    continue;
                }

                var account = accounts.FirstOrDefault(a => string.Equals(a.Name, accountName, StringComparison.OrdinalIgnoreCase));
                if (account == null)
                {
                    Log("Profile follower " + accountName + " is not connected.");
                    continue;
                }

                if (leadAccount != null && account == leadAccount)
                {
                    Log("Profile skipped follower " + accountName + " because it is the lead account.");
                    continue;
                }

                var row = new AccountCopyRow(account, GetStringAttribute(element, "group", DefaultGroupName), ReadAccountPnl(account));
                row.Enabled = GetBoolAttribute(element, "enabled", true);
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
                row.LastAction = "Loaded profile";

                accountRows.Add(row);
                seenAccounts.Add(accountName);
            }

            lastGroupListSignature = string.Empty;
            RefreshGroupList();
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
            if (leadAccount == null)
            {
                SetStatus("Select a lead account before starting.");
                return;
            }

            if (!accountRows.Any(r => r.Enabled && r.SizingMode != SizingMode.Disabled))
            {
                SetStatus("Add and enable at least one follower before starting.");
                return;
            }

            if (accountRows.Any(r => r.Account == leadAccount))
            {
                SetStatus("The lead account cannot also be a follower.");
                return;
            }

            var validationMessage = ValidateReadyToStart();
            if (!string.IsNullOrEmpty(validationMessage))
            {
                SetStatus(validationMessage);
                Log("Start blocked: " + validationMessage);
                return;
            }

            mirroredTargetQuantities.Clear();
            lockedVirtualPositions.Clear();
            dryRunMode = dryRunCheckBox != null && dryRunCheckBox.IsChecked == true;
            isCopying = true;
            startPauseButton.Content = "Pause Copying";
            startPauseButton.Background = Brushes.DarkOrange;
            if (dryRunCheckBox != null)
                dryRunCheckBox.IsEnabled = false;

            SetStatus(dryRunMode ? "Dry run active. Orders are simulated only." : "Copying active. Locked rows allow exits only.");
            Log(dryRunMode ? "Dry run started. No copied orders will be submitted." : "Copying started.");
            RefreshAllRows();
        }

        private void PauseCopyingTrades()
        {
            isCopying = false;
            dryRunMode = false;
            startPauseButton.Content = "Start Copying";
            startPauseButton.Background = Brushes.SeaGreen;
            if (dryRunCheckBox != null)
                dryRunCheckBox.IsEnabled = true;

            SetStatus("Copying paused. Positions were left untouched.");
            Log("Copying paused.");
            RefreshAllRows();
        }

        private void OnOrderUpdate(object sender, OrderEventArgs args)
        {
            if (!isCopying || args.Order == null || args.Order.Account != leadAccount)
                return;

            if (args.Order.OrderState != OrderState.PartFilled && args.Order.OrderState != OrderState.Filled)
                return;

            Dispatcher.InvokeAsync(() => CopyOrderToFollowerAccounts(args.Order));
        }

        private void CopyOrderToFollowerAccounts(Order sourceOrder)
        {
            if (sourceOrder == null || sourceOrder.Filled <= 0)
                return;

            foreach (var row in accountRows.ToList())
            {
                if (row.Account == null || !row.Enabled || row.SizingMode == SizingMode.Disabled)
                    continue;

                if (row.Account.ConnectionStatus != ConnectionStatus.Connected)
                {
                    row.SetStatus("Error", "Disconnected");
                    row.LastAction = "Skipped disconnected";
                    Log(row.AccountName + " skipped because account is disconnected.");
                    continue;
                }

                if (row.Account == leadAccount)
                {
                    row.SetStatus("Error", "Lead selected as follower");
                    row.LastAction = "Skipped lead account";
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
                var wasEntryLocked = row.IsEntryLocked;
                if (row.IsEntryLocked)
                    quantityToSubmit = CapLockedQuantityToReducingOnly(row, sourceOrder, quantityToSubmit);

                if (quantityToSubmit <= 0)
                {
                    mirroredTargetQuantities[targetKey] = desiredQuantity;
                    row.LastAction = "Blocked entry while locked";
                    Log(row.AccountName + " blocked copied entry while locked.");
                    continue;
                }

                if (quantityToSubmit < originalQuantity)
                    Log(row.AccountName + " capped locked exit from " + originalQuantity + " to " + quantityToSubmit + ".");

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
                        if (wasEntryLocked)
                            ApplyLockedVirtualFill(row, sourceOrder.Instrument, sourceOrder.OrderAction, quantityToSubmit);

                        mirroredTargetQuantities[targetKey] = (wasEntryLocked && quantityToSubmit < originalQuantity) || maxNetCapped
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
                    if (wasEntryLocked)
                        ApplyLockedVirtualFill(row, sourceOrder.Instrument, sourceOrder.OrderAction, quantityToSubmit);

                    mirroredTargetQuantities[targetKey] = (wasEntryLocked && quantityToSubmit < originalQuantity) || maxNetCapped
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
                    return "Follower " + row.AccountName + " has no account.";

                if (row.Account.ConnectionStatus != ConnectionStatus.Connected)
                    return "Follower " + row.AccountName + " is disconnected.";

                if (row.SizingMode == SizingMode.Multiplier && row.Multiplier <= 0)
                    return "Follower " + row.AccountName + " needs a multiplier greater than 0.";

                if (row.SizingMode == SizingMode.Fixed && row.FixedQuantity <= 0)
                    return "Follower " + row.AccountName + " needs a fixed quantity greater than 0.";
            }

            if (activeRows.Any(r => r.SizingMode == SizingMode.BalanceRatio))
            {
                double leadBalance;
                if (!TryGetSizingBalance(leadAccount, out leadBalance) || leadBalance <= 0)
                    return "Balance-ratio sizing needs usable lead account value data.";

                foreach (var row in activeRows.Where(r => r.SizingMode == SizingMode.BalanceRatio))
                {
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

            if (leadAccount == null || !TryGetSizingBalance(leadAccount, out leadBalance) || !TryGetSizingBalance(row.Account, out followerBalance))
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

        private int CapQuantityToMaxNetPosition(AccountCopyRow row, Instrument instrument, OrderAction action, int requestedQuantity)
        {
            if (row.MaxNetPosition <= 0 || requestedQuantity <= 0)
                return requestedQuantity;

            var currentSigned = GetSignedPosition(row.Account, instrument);
            var signedDelta = IsBuyAction(action) ? requestedQuantity : -requestedQuantity;
            var requestedResult = currentSigned + signedDelta;

            if (Math.Abs(requestedResult) <= row.MaxNetPosition)
                return requestedQuantity;

            if (signedDelta > 0)
                return Math.Min(requestedQuantity, Math.Max(0, row.MaxNetPosition - currentSigned));

            return Math.Min(requestedQuantity, Math.Max(0, currentSigned + row.MaxNetPosition));
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
            var instrumentName = instrument != null ? instrument.FullName : string.Empty;
            return row.AccountName + "|" + instrumentName;
        }

        private void ClearLockedVirtualPositions(AccountCopyRow row)
        {
            var prefix = row.AccountName + "|";
            foreach (var key in lockedVirtualPositions.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                lockedVirtualPositions.Remove(key);
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
            if (MessageBox.Show("Flatten all follower accounts?", "Confirm Flatten Followers", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            foreach (var row in accountRows.ToList())
                FlattenAccount(row.Account, "Manual follower flatten");
        }

        private void FlattenSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRows();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to flatten.");
                return;
            }

            if (MessageBox.Show("Flatten selected follower accounts?", "Confirm Flatten Selected", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            foreach (var row in rows)
                FlattenAccount(row.Account, "Manual selected flatten");
        }

        private void FlattenAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Flatten the lead and every follower account?", "Confirm Flatten All", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            PauseCopyingTrades();

            if (leadAccount != null)
                FlattenAccount(leadAccount, "Manual flatten all");

            foreach (var row in accountRows.ToList())
                FlattenAccount(row.Account, "Manual flatten all");
        }

        private void FlattenGroupButton_Click(object sender, RoutedEventArgs e)
        {
            var groupName = GetSelectedGroupName();
            if (string.IsNullOrEmpty(groupName))
            {
                SetStatus("Select a group before flattening.");
                return;
            }

            if (MessageBox.Show("Flatten all accounts in group " + groupName + "?", "Confirm Flatten Group", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            foreach (var row in accountRows.Where(r => GroupEquals(r.GroupName, groupName)).ToList())
            {
                row.ManualLock = true;
                FlattenAccount(row.Account, "Manual group flatten");
            }
        }

        private void ReconcileSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRows();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to reconcile.");
                return;
            }

            if (leadAccount == null)
            {
                SetStatus("Select a lead account before reconciling.");
                return;
            }

            if (MessageBox.Show("Reconcile selected followers to the lead account using each row's sizing rules?", "Confirm Reconcile Selected", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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

                if (row.IsEntryLocked)
                    desiredSigned = CalculateLockedReconcileTarget(currentSigned, desiredSigned);

                desiredSigned = CapDesiredSignedPositionToMaxNet(row, desiredSigned);

                var delta = desiredSigned - currentSigned;
                if (delta == 0)
                    continue;

                SubmitReconcileAdjustment(row, instrument, delta);
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

            if (row.SizingMode == SizingMode.BalanceRatio)
            {
                double leadBalance;
                double followerBalance;
                if (!TryGetSizingBalance(leadAccount, out leadBalance) || leadBalance <= 0)
                    return "lead balance data is unavailable";

                if (!TryGetSizingBalance(row.Account, out followerBalance) || followerBalance <= 0)
                    return "follower balance data is unavailable";
            }

            return string.Empty;
        }

        private Dictionary<string, PositionSnapshot> BuildDesiredPositions(AccountCopyRow row)
        {
            var desiredPositions = new Dictionary<string, PositionSnapshot>(StringComparer.OrdinalIgnoreCase);

            foreach (var leadPosition in GetOpenPositionSnapshots(leadAccount))
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

        private void SubmitReconcileAdjustment(AccountCopyRow row, Instrument instrument, int signedDelta)
        {
            var action = signedDelta > 0 ? OrderAction.Buy : OrderAction.Sell;
            var quantity = Math.Abs(signedDelta);

            try
            {
                if (IsDryRunSelected())
                {
                    Log("DRY RUN " + row.AccountName + " would reconcile with " + DescribeOrder(action, quantity, instrument) + ".");
                    return;
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
                Log(row.AccountName + " reconcile sent " + DescribeOrder(action, quantity, instrument) + ".");
            }
            catch (Exception ex)
            {
                row.SetStatus("Error", "Reconcile failed");
                row.LastAction = "Reconcile failed";
                Log("ERROR " + row.AccountName + " reconcile failed: " + ex.Message);
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
                    Log(account.Name + " closing " + DescribeOrder(closeAction, quantity, position.Instrument) + ".");
                }
                catch (Exception ex)
                {
                    Log("ERROR " + account.Name + " close failed: " + ex.Message);
                }
            }
        }

        private Order CreateAccountOrder(Account account, Instrument instrument, OrderAction action, OrderType orderType, TimeInForce timeInForce, int quantity, double limitPrice, double stopPrice, string name)
        {
            return account.CreateOrder(instrument, action, orderType, OrderEntry.Automated, timeInForce, quantity, limitPrice, stopPrice, string.Empty, name, NinjaTrader.Core.Globals.MaxDate, null);
        }

        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = accountsGrid.SelectedItems.OfType<AccountCopyRow>().ToList();
            if (rows.Count == 0)
            {
                SetStatus("Select one or more rows to remove.");
                return;
            }

            foreach (var row in rows)
            {
                accountRows.Remove(row);
                Log("Removed follower " + row.AccountName + ".");
            }

            RefreshGroupList();
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

            foreach (var row in accountRows.Where(r => GroupEquals(r.GroupName, groupName)))
            {
                row.Enabled = true;
                row.ManualLock = false;
                row.AutoLocked = false;
                row.LockReason = string.Empty;
                row.LastAction = "Group enabled";
                ClearLockedVirtualPositions(row);
            }

            Log("Enabled group " + groupName + ".");
            RefreshAllRows();
        }

        private void PauseGroupButton_Click(object sender, RoutedEventArgs e)
        {
            var groupName = GetSelectedGroupName();
            if (string.IsNullOrEmpty(groupName))
            {
                SetStatus("Select a group before pausing.");
                return;
            }

            foreach (var row in accountRows.Where(r => GroupEquals(r.GroupName, groupName)))
            {
                row.ManualLock = true;
                row.LastAction = "Group paused";
            }

            Log("Paused group " + groupName + ". Entries are blocked; exits remain allowed.");
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

            var rows = accountRows.Where(r => r != source && GroupEquals(r.GroupName, source.GroupName)).ToList();
            foreach (var row in rows)
            {
                row.SizingMode = source.SizingMode;
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
            }

            Log("Applied " + source.AccountName + " settings to " + rows.Count + " row(s) in group " + source.GroupName + ".");
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
            RefreshAllRows();
            RefreshGroupList();
        }

        private void RefreshAllRows()
        {
            foreach (var row in accountRows.ToList())
            {
                if (!row.IsEntryLocked)
                    ClearLockedVirtualPositions(row);

                RefreshRowMetrics(row);
                EvaluateRisk(row);
                UpdateRowStatus(row);
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
            if (row.AutoLocked || row.Account == null)
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

            if (!row.Enabled || row.SizingMode == SizingMode.Disabled)
            {
                row.SetStatus("Disabled", "Disabled");
                return;
            }

            if (IsPotentiallyDesynced(row))
            {
                row.SetStatus("Desynced", "Check position sync");
                return;
            }

            if (IsNearRiskLimit(row))
            {
                row.SetStatus("Warning", "Near risk limit");
                return;
            }

            row.SetStatus(isCopying ? "Active" : "Ready", isCopying ? "Copying" : "Ready");
        }

        private bool IsNearRiskLimit(AccountCopyRow row)
        {
            return (row.DailyLossLimit > 0 && row.SessionPnl <= -Math.Abs(row.DailyLossLimit) * 0.9)
                || (row.MaxDrawdown > 0 && row.Drawdown >= Math.Abs(row.MaxDrawdown) * 0.9)
                || (row.ProfitTarget > 0 && row.SessionPnl >= Math.Abs(row.ProfitTarget) * 0.9);
        }

        private bool IsPotentiallyDesynced(AccountCopyRow row)
        {
            if (!isCopying || leadAccount == null || row.Account == null || row.IsEntryLocked || !row.Enabled || row.SizingMode == SizingMode.Disabled)
                return false;

            var leadPositions = GetOpenPositionSnapshots(leadAccount);
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
            var orderId = string.IsNullOrEmpty(order.OrderId) ? "no-order-id" : order.OrderId;
            var orderName = string.IsNullOrEmpty(order.Name) ? "no-name" : order.Name;
            return accountName + "|" + instrumentName + "|" + orderName + "|" + orderId + "|" + order.Time.Ticks;
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
            var activeCount = accountRows.Count(r => r.Enabled && r.SizingMode != SizingMode.Disabled && !r.IsEntryLocked && r.Account != null && r.Account.ConnectionStatus == ConnectionStatus.Connected);
            var lockedCount = accountRows.Count(r => r.IsEntryLocked);
            var errorCount = accountRows.Count(r => r.StatusLevel == "Error" || r.StatusLevel == "Desynced");
            var mode = isCopying ? dryRunMode ? "Dry Run" : "Copying" : "Paused";
            return mode + " | Active followers: " + activeCount + " | Locked: " + lockedCount + " | Attention: " + errorCount;
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
            PauseCopyingTrades();

            if (leadAccount != null)
                leadAccount.OrderUpdate -= OnOrderUpdate;

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
            private bool enabled = true;
            private string groupName;
            private string connectionStatus = "Unknown";
            private string status = "Ready";
            private string statusLevel = "Ready";
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

            public bool Enabled
            {
                get { return enabled; }
                set { SetField(ref enabled, value, "Enabled"); }
            }

            public string GroupName
            {
                get { return groupName; }
                set { SetField(ref groupName, string.IsNullOrWhiteSpace(value) ? DefaultGroupName : value.Trim(), "GroupName"); }
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
