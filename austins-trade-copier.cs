#region Using declarations
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
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

        private readonly ObservableCollection<AccountCopyRow> accountRows = new ObservableCollection<AccountCopyRow>();
        private readonly Dictionary<string, int> mirroredTargetQuantities = new Dictionary<string, int>();
        private readonly Dictionary<string, int> lockedVirtualPositions = new Dictionary<string, int>();
        private readonly DispatcherTimer telemetryTimer;

        private Account leadAccount;
        private List<Account> connectedAccounts = new List<Account>();
        private bool isCopying;
        private string lastGroupListSignature = string.Empty;

        private ComboBox leadAccountComboBox;
        private ComboBox addAccountComboBox;
        private TextBox addGroupTextBox;
        private ComboBox groupComboBox;
        private DataGrid accountsGrid;
        private Button startPauseButton;
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
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });

            var leadPanel = new StackPanel
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

            var actionPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };

            startPauseButton = CreateButton("Start Copying", Brushes.SeaGreen);
            startPauseButton.Width = 130;
            startPauseButton.Click += StartPauseButton_Click;
            actionPanel.Children.Add(startPauseButton);

            var flattenFollowersButton = CreateButton("Flatten Followers", Brushes.Firebrick);
            flattenFollowersButton.Click += FlattenFollowersButton_Click;
            actionPanel.Children.Add(flattenFollowersButton);

            var flattenAllButton = CreateButton("Flatten All", Brushes.DarkRed);
            flattenAllButton.Click += FlattenAllButton_Click;
            actionPanel.Children.Add(flattenAllButton);

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

            Grid.SetRow(actionPanel, 1);
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

            Grid.SetRow(accountsGrid, 2);
            root.Children.Add(accountsGrid);

            statusTextBlock = new TextBlock
            {
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
                Text = "Ready. Add follower accounts, set sizing and risk rules, then start copying."
            };
            Grid.SetRow(statusTextBlock, 3);
            root.Children.Add(statusTextBlock);

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
            Grid.SetRow(eventLogTextBox, 4);
            root.Children.Add(eventLogTextBox);

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

            mirroredTargetQuantities.Clear();
            lockedVirtualPositions.Clear();
            isCopying = true;
            startPauseButton.Content = "Pause Copying";
            startPauseButton.Background = Brushes.DarkOrange;
            SetStatus("Copying active. Locked rows allow exits only.");
            Log("Copying started.");
            RefreshAllRows();
        }

        private void PauseCopyingTrades()
        {
            isCopying = false;
            startPauseButton.Content = "Start Copying";
            startPauseButton.Background = Brushes.SeaGreen;
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

                if (row.Account == leadAccount)
                {
                    row.SetStatus("Error", "Lead selected as follower");
                    row.LastAction = "Skipped lead account";
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

                try
                {
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

                    mirroredTargetQuantities[targetKey] = wasEntryLocked && quantityToSubmit < originalQuantity
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
            int desiredQuantity = 0;

            switch (row.SizingMode)
            {
                case SizingMode.OneToOne:
                    desiredQuantity = sourceOrder.Filled;
                    break;
                case SizingMode.Multiplier:
                    desiredQuantity = row.Multiplier > 0
                        ? (int)Math.Floor(sourceOrder.Filled * row.Multiplier)
                        : 0;
                    break;
                case SizingMode.Fixed:
                    desiredQuantity = sourceOrder.Filled > 0 ? Math.Max(0, row.FixedQuantity) : 0;
                    break;
                case SizingMode.BalanceRatio:
                    desiredQuantity = CalculateBalanceRatioQuantity(row, sourceOrder.Filled);
                    break;
                case SizingMode.Disabled:
                    desiredQuantity = 0;
                    break;
            }

            if (row.MaxQuantity > 0)
                desiredQuantity = Math.Min(desiredQuantity, row.MaxQuantity);

            return Math.Max(0, desiredQuantity);
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
                row.DailyLossLimit = source.DailyLossLimit;
                row.MaxDrawdown = source.MaxDrawdown;
                row.ProfitTarget = source.ProfitTarget;
                row.LimitAction = source.LimitAction;
                row.LastAction = "Group settings applied";
            }

            Log("Applied " + source.AccountName + " settings to " + rows.Count + " row(s) in group " + source.GroupName + ".");
            RefreshAllRows();
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
            var activeCount = accountRows.Count(r => r.Enabled && r.SizingMode != SizingMode.Disabled && !r.IsEntryLocked);
            var lockedCount = accountRows.Count(r => r.IsEntryLocked);
            var errorCount = accountRows.Count(r => r.StatusLevel == "Error" || r.StatusLevel == "Desynced");
            return (isCopying ? "Copying" : "Paused") + " | Active followers: " + activeCount + " | Locked: " + lockedCount + " | Attention: " + errorCount;
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

            eventLogTextBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            eventLogTextBox.ScrollToEnd();
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
            private double sessionPnl;
            private double drawdown;
            private double peakPnl;
            private int netPosition;
            private SizingMode sizingMode = SizingMode.OneToOne;
            private double multiplier = DefaultMultiplier;
            private int fixedQuantity = DefaultFixedQuantity;
            private int maxQuantity;
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
