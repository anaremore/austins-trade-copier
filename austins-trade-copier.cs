#region Using declarations
using System;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
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
	    private Account leadAccount;
	    private List<Account> targetAccounts = new List<Account>();
	    private bool isCopying = false;
	    private ComboBox leadAccountComboBox;
	    private StackPanel targetAccountsPanel;
	    private Button addAccountButton;
	    private Button startStopButton;
	    private Button flattenAllButton; // New button for flattening all accounts

	    public TradeCopierWindow()
	    {
	        Caption = "Austin's Trade Copier";
	        Width = 400;
	        Height = 450; // Increased height to accommodate the new button
	        
	        CreateUI();
	        RefreshAccountList();

	        Account.AccountStatusUpdate += OnAccountStatusUpdate;

	        Closing += TradeCopierWindow_Closing;
	    }

	    private void CreateUI()
	    {
	        var grid = new Grid();
	        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Lead Account
	        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Target Accounts Label
	        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Target Accounts List
	        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Add Target Account Button
	        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Start/Stop Button
	        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Flatten All Accounts Button


		    // Lead Account Section
		    var leadAccountPanel = new StackPanel { Margin = new Thickness(10) };
		    var leadAccountLabel = new Label { 
		        Content = "Lead Account:", 
		        FontWeight = FontWeights.Bold,
		        Foreground = Brushes.White,
		    };
		    leadAccountPanel.Children.Add(leadAccountLabel);

		    leadAccountComboBox = new ComboBox { 
		        Margin = new Thickness(0, 5, 0, 10),
		        Padding = new Thickness(5),
		        MinWidth = 200
		    };
		    leadAccountComboBox.SelectionChanged += LeadAccountComboBox_SelectionChanged;
		    leadAccountPanel.Children.Add(leadAccountComboBox);

		    Grid.SetRow(leadAccountPanel, 0);
		    grid.Children.Add(leadAccountPanel);

		    // Target Accounts Label
		    var targetAccountsLabel = new Label { 
		        Content = "Target Accounts:", 
		        FontWeight = FontWeights.Bold,
		        Foreground = Brushes.White,
		        Margin = new Thickness(10, 0, 10, 5)
		    };
		    Grid.SetRow(targetAccountsLabel, 1);
		    grid.Children.Add(targetAccountsLabel);

		    // Target Accounts Panel
		    targetAccountsPanel = new StackPanel { Margin = new Thickness(10, 0, 10, 10) };
		    var scrollViewer = new ScrollViewer { 
		        Content = targetAccountsPanel,
		        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
		        Margin = new Thickness(10, 0, 10, 10)
		    };
		    Grid.SetRow(scrollViewer, 2);
		    grid.Children.Add(scrollViewer);

		    // Add Target Account Button
		    addAccountButton = new Button
		    {
		        Content = "Add Target Account",
		        Padding = new Thickness(10, 5, 10, 5),
		        Margin = new Thickness(10),
		        HorizontalAlignment = HorizontalAlignment.Left
		    };
		    addAccountButton.Click += AddAccountButton_Click;
		    Grid.SetRow(addAccountButton, 3);
		    grid.Children.Add(addAccountButton);

		    // Start/Stop Button
		    startStopButton = new Button
		    {
		        Content = "Start Copying",
		        Padding = new Thickness(20, 10, 20, 10),
		        Margin = new Thickness(10),
		        HorizontalAlignment = HorizontalAlignment.Stretch
		    };
		    startStopButton.Click += StartStopButton_Click;
		    Grid.SetRow(startStopButton, 4);
		    grid.Children.Add(startStopButton);
			
	        // Flatten All Accounts Button
	        flattenAllButton = new Button
	        {
	            Content = "Flatten All Accounts",
	            Padding = new Thickness(20, 10, 20, 10),
	            Margin = new Thickness(10),
	            HorizontalAlignment = HorizontalAlignment.Stretch
	        };
	        flattenAllButton.Click += FlattenAllButton_Click;
	        Grid.SetRow(flattenAllButton, 5);
	        grid.Children.Add(flattenAllButton);

		    Content = grid;

		    // Set window properties
		    Width = 400;
		    Height = 500;
		    WindowStartupLocation = WindowStartupLocation.CenterScreen;
		    Background = Brushes.DarkGray;
		}
		
	    private void AddAccountButton_Click(object sender, RoutedEventArgs e)
	    {
	        if (targetAccountsPanel == null)
	        {
	            MessageBox.Show("Error: Target accounts panel not initialized. Please restart the application.");
	            return;
	        }

	        var horizontalStackPanel = new StackPanel { 
	            Orientation = Orientation.Horizontal,
	            Margin = new Thickness(0, 0, 0, 5)
	        };

	        var newComboBox = new ComboBox
	        {
	            DisplayMemberPath = "Name",
	            ItemsSource = leadAccountComboBox.ItemsSource,
	            MinWidth = 200,
	            Margin = new Thickness(0, 0, 5, 0)
	        };
	        newComboBox.SelectionChanged += TargetAccountComboBox_SelectionChanged;

	        var removeButton = new Button
	        {
	            Content = "Remove",
	            Width = 25,
	            Height = 25,
	            Background = Brushes.DarkGray,
                Foreground = Brushes.Black
	        };
	        removeButton.Click += (s, args) =>
	        {
	            targetAccountsPanel.Children.Remove(horizontalStackPanel);
	            TargetAccountComboBox_SelectionChanged(newComboBox, null);
	        };

	        horizontalStackPanel.Children.Add(newComboBox);
	        horizontalStackPanel.Children.Add(removeButton);

	        targetAccountsPanel.Children.Add(horizontalStackPanel);
	    }		

        private void RefreshAccountList()
        {
            var accounts = Account.All.ToList();
            
            NinjaTrader.Code.Output.Process("Total accounts found: " + accounts.Count, PrintTo.OutputTab1);

            foreach (var account in accounts)
            {
                NinjaTrader.Code.Output.Process("Account: " + account.Name + ", Status: " + account.ConnectionStatus, PrintTo.OutputTab1);
            }

            try
            {
                accounts = accounts.Where(a => a.ConnectionStatus == ConnectionStatus.Connected).ToList();
            }
            catch (Exception ex)
            {
                NinjaTrader.Code.Output.Process("Error filtering accounts: " + ex.Message, PrintTo.OutputTab1);
            }

            NinjaTrader.Code.Output.Process("Connected accounts: " + accounts.Count, PrintTo.OutputTab1);

            Dispatcher.InvokeAsync(() =>
            {
                leadAccountComboBox.ItemsSource = accounts;
                leadAccountComboBox.DisplayMemberPath = "Name";

                foreach (StackPanel sp in targetAccountsPanel.Children)
                {
                    var cb = sp.Children.OfType<ComboBox>().FirstOrDefault();
                    if (cb != null)
                    {
                        var selectedAccount = cb.SelectedItem as Account;
                        cb.ItemsSource = accounts;
                        cb.SelectedItem = selectedAccount;
                    }
                }
            });
        }

        private void OnAccountStatusUpdate(object sender, AccountStatusEventArgs e)
        {
            RefreshAccountList();
        }

        private void LeadAccountComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (leadAccount != null)
            {
                leadAccount.OrderUpdate -= OnOrderUpdate;
            }
            leadAccount = leadAccountComboBox.SelectedItem as Account;
            if (leadAccount != null)
            {
                leadAccount.OrderUpdate += OnOrderUpdate;
            }
        }

		private void TargetAccountComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
		    targetAccounts.Clear();
		    foreach (StackPanel sp in targetAccountsPanel.Children)
		    {
		        var cb = sp.Children.OfType<ComboBox>().FirstOrDefault();
		        if (cb != null && cb.SelectedItem != null)
		        {
		            Account account = cb.SelectedItem as Account;
		            if (account != null)
		            {
		                targetAccounts.Add(account);
		            }
		        }
		    }
		}

        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isCopying)
                StartCopyingTrades();
            else
                StopCopyingTrades();
        }

	    private void StartCopyingTrades()
	    {
	        if (leadAccount == null || targetAccounts.Count == 0)
	        {
	            MessageBox.Show("Please select a lead account and at least one target account.");
	            return;
	        }

	        if (targetAccounts.Contains(leadAccount))
	        {
	            MessageBox.Show("Lead account cannot be in the target accounts list.");
	            return;
	        }

	        isCopying = true;
	        startStopButton.Content = "Stop Copying";
	    }

	    private void StopCopyingTrades()
	    {
	        isCopying = false;
	        startStopButton.Content = "Start Copying";
	        FlattenAllPositions();
	    }

        private void OnOrderUpdate(object sender, OrderEventArgs args)
        {
            if (!isCopying || args.Order.Account != leadAccount || args.Order.OrderState != OrderState.Filled)
            {
                return;
            }

            Dispatcher.InvokeAsync(() => CopyOrderToTargetAccounts(args.Order));
        }

        private void CopyOrderToTargetAccounts(Order sourceOrder)
        {
            foreach (var targetAccount in targetAccounts)
            {
                var newOrder = new Order
                {
                    Account = targetAccount,
                    OrderType = sourceOrder.OrderType,
                    Instrument = sourceOrder.Instrument,
                    Quantity = sourceOrder.Filled,
                    LimitPrice = sourceOrder.LimitPrice,
                    StopPrice = sourceOrder.StopPrice,
                    OrderAction = sourceOrder.OrderAction
                };

                try
                {
                    targetAccount.Submit(new[] { newOrder });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error submitting order to " + targetAccount.Name + ": " + ex.Message);
                }
            }
        }
		
	    private void FlattenAllButton_Click(object sender, RoutedEventArgs e)
	    {
	        FlattenAllPositions();
	    }

	    private void FlattenAllPositions()
	    {
	        List<Account> accountsToFlatten = new List<Account>(targetAccounts);
	        if (leadAccount != null)
	        {
	            accountsToFlatten.Add(leadAccount);
	        }

	        foreach (var account in accountsToFlatten)
	        {
	            try
	            {
	                // Step 1: Cancel all active orders
	                CancelAllOrders(account);

	                // Step 2: Close all open positions
	                CloseAllPositions(account);

	                // Step 3: Verify and force-close any remaining positions
	                VerifyAndForceClosePositions(account);

	                NinjaTrader.Code.Output.Process("Flattened all positions and canceled all orders for account: " + account.Name, PrintTo.OutputTab1);
	            }
	            catch (Exception ex)
	            {
	                NinjaTrader.Code.Output.Process("Error flattening positions for account " + account.Name + ": " + ex.Message, PrintTo.OutputTab1);
	            }
	        }
	        MessageBox.Show("All positions have been flattened and active orders canceled.");
	    }

	    private void CancelAllOrders(Account account)
	    {
	        foreach (Order order in account.Orders)
	        {
	            if (order.OrderState == OrderState.Working)
	            {
	                try
	                {
	                    account.Cancel(new[] { order });
	                    NinjaTrader.Code.Output.Process("Canceled order for " + account.Name + ": " + order.Instrument.FullName + ", OrderAction: " + order.OrderAction + ", Quantity: " + order.Quantity, PrintTo.OutputTab1);
	                }
	                catch (Exception ex)
	                {
	                    NinjaTrader.Code.Output.Process("Error canceling order for account " + account.Name + ": " + ex.Message, PrintTo.OutputTab1);
	                }
	            }
	        }
	    }

	    private void CloseAllPositions(Account account)
	    {
	        foreach (Position position in account.Positions)
	        {
	            if (position.Quantity != 0)
	            {
	                OrderAction closeAction = position.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
	                Order closeOrder = account.CreateOrder(position.Instrument, closeAction, OrderType.Market, TimeInForce.Day, Math.Abs(position.Quantity), 0, 0, string.Empty, "Close position", null);
	                account.Submit(new[] { closeOrder });
	                NinjaTrader.Code.Output.Process("Closing position for " + account.Name + ": " + position.Instrument.FullName + ", Quantity: " + position.Quantity, PrintTo.OutputTab1);
	            }
	        }
	    }

	    private void VerifyAndForceClosePositions(Account account)
	    {
	        // Wait briefly for previous orders to process
	        System.Threading.Thread.Sleep(1000);

	        foreach (Position position in account.Positions)
	        {
	            if (position.Quantity != 0)
	            {
	                OrderAction closeAction = position.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
	                Order forceCloseOrder = account.CreateOrder(position.Instrument, closeAction, OrderType.Market, TimeInForce.Day, Math.Abs(position.Quantity), 0, 0, string.Empty, "Force close position", null);
	                account.Submit(new[] { forceCloseOrder });
	                NinjaTrader.Code.Output.Process("Force closing remaining position for " + account.Name + ": " + position.Instrument.FullName + ", Quantity: " + position.Quantity, PrintTo.OutputTab1);
	            }
	        }
	    }

	    private void TradeCopierWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
	    {
	        StopCopyingTrades(); // This will call FlattenAllPositions
	        if (leadAccount != null)
	        {
	            leadAccount.OrderUpdate -= OnOrderUpdate;
	        }
	        Account.AccountStatusUpdate -= OnAccountStatusUpdate;
	    }
    }
}