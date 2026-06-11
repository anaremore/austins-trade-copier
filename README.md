# Austin's Trade Copier

**Austin's Trade Copier** is a NinjaTrader 8 add-on that lets you mirror trades from one lead account to multiple target accounts in real time. Built for traders managing multiple accounts, it provides a simple UI and robust functionality to streamline account management.

## 🚀 Features

- 📈 **Trade Mirroring**: Automatically copies market, limit, and stop orders from a lead account to any number of target accounts.
- 🔁 **Live Sync**: Reacts to account connection status updates in real time.
- 🧭 **Table-First Account Setup**: Every connected account appears in one grid. Choose each copy row's lead directly in the table; referenced accounts are marked as leads automatically.
- 🔀 **Multiple Lead Accounts**: Different rows can copy different leads at the same time, so one copier window can manage several lead/copy-row sets.
- 🎯 **Instrument Filters**: Restrict any row to selected symbols such as `MNQ, MES`.
- 🚪 **Exits-Only Mode**: Let a row stop taking new entries while still following reducing/closing orders.
- ⚖️ **Per-Account Sizing**: Choose 1:1, multiplier, fixed quantity, balance-ratio, or disabled sizing for each row.
- 🧱 **Max Net Position Guard**: Cap each row's absolute resulting position per instrument.
- 🧯 **Risk Lockouts**: Set max loss, max drawdown, and profit-target thresholds per row with entry-lock or auto-close behavior.
- ✅ **Pause Without Flattening**: Pause copying without touching open positions; flatten actions are separate and confirmed.
- 🧹 **Flatten Controls**: Flatten On rows, selected rows, or all connected table/lead accounts without changing the copier's running state.
- 🧮 **Reconcile Selected**: Deliberately align selected rows to their leads using each row's sizing rules.
- 🧪 **Dry Run Mode**: Simulate copy and reconcile decisions without submitting copied/reconcile orders.
- 💾 **Profiles**: Save, load, and delete copier profiles so enabled rows, lead assignments, sizing, filters, and risk rules survive restarts.
- 📝 **Event Log**: Non-blocking panel log for copied orders, skipped orders, lockouts, and errors, with export and clear controls.

---

## 🛠️ Installation

1. Copy `austins-trade-copier.cs` into your NinjaTrader 8 `AddOns` directory.
2. Open NinjaTrader, go to **NinjaScript Editor**, and press `F5` to compile.
3. Open the copier from **Control Center > Tools > Austin's Trade Copier**.

> ℹ️ The UI will appear automatically when the add-on is activated.

For a local syntax/build check outside NinjaTrader, run:

```powershell
.\scripts\verify-ninjascript.ps1
```

The verifier compiles `austins-trade-copier.cs` against the installed NinjaTrader 8 assemblies and .NET Framework WPF references.

---

## 📋 How to Use

1. **Review the Account Table** – Connected NinjaTrader accounts are listed automatically. Rows with no lead can stay available as lead-only or unused accounts.
2. **Choose Leads Per Row** – In the **Lead** column, pick the account each copy row should follow. As soon as another row points at an account, that account is marked **Lead**. If it was on, the copier turns it off because lead accounts drive followers instead of receiving copied orders.
3. **Enable Copy Rows** – Check **On** for rows that should receive copied orders. A row needs a connected account, a different connected lead, and active sizing. The selected-row **Enable / Disable** button enables ready off rows first; if none are ready, it disables selected on rows.
4. **Configure Copy Mode, Symbols, and Sizing** – Use **Row Preset** on selected rows for common setups like `1:1 copy`, `Multiplier x2`, `Fixed 1`, `Exits only`, or limit-action presets. Presets preserve Leads, Symbols, enabled state, and risk amounts. Use **Copy** to choose normal `All` copying or `ExitsOnly`. Leave **Symbols** blank to copy all instruments, or enter roots/full names such as `MNQ, MES`. Then pick a sizing mode per row:
   - **1:1**: copies the lead filled quantity.
   - **Multiplier**: copies `floor(lead filled quantity * multiplier)`.
   - **Fixed qty**: sends a fixed quantity once per lead order.
   - **Balance ratio**: scales by follower equity versus lead equity.
   - **Off**: keeps the row visible but does not copy entries.
5. **Set Risk Rules** – Optional max loss, max drawdown, and profit-target values lock a row when hit. **Risk Now** shows current progress toward those limits. Use **Limit Action** to choose whether the row only locks new entries or auto-closes matching managed positions.
6. **Save or Load a Profile** – Store the current dashboard as a profile if you want to reuse the setup. Saving over an existing profile and loading a profile both require confirmation because they replace saved or current table setup; neither action touches open positions or working orders.
7. **Start Copying** – The dashboard validates active rows before arming and shows active, ready, locked, warning, desynced, and error states. Enable **Dry Run** first if you want to test the copy decisions without submitting copied orders.
8. **Pause Copying** – Pausing stops new copy processing and leaves positions untouched.
9. **Flatten or Reconcile Deliberately** – Use enabled, selected, or all-account flatten buttons when you intend to close positions. Use **Reconcile Selected** only when you want selected rows adjusted back toward their configured leads.

---

## 🧠 Sizing and Risk Model

Sizing is calculated from the cumulative filled quantity of the lead order. This avoids over-copying partial fills: if the lead order fills in pieces, each copy row receives only the remaining quantity needed for its configured target size.

Balance-ratio sizing uses `NetLiquidation` first and falls back to `CashValue`. If either account has missing or zero balance data, the copier skips that row's order instead of falling back to the full lead size.

`Max Qty` caps every sizing mode when greater than zero. A value of `0` means no max cap.

`Max Net` caps the absolute resulting row position per instrument. A value of `0` means no net-position cap. The guard blocks or caps exposure increases but still allows position-reducing orders.

Before copying starts, enabled rows are validated. Disconnected rows, zero multipliers, zero fixed quantities, or unavailable balance-ratio account values block startup instead of silently creating an unsafe copier state.

Profiles are stored under your NinjaTrader documents templates folder:

`Documents\NinjaTrader 8\templates\AustinTradeCopier`

Risk thresholds use the row baseline captured when the account row is created, loaded, or reset with **Reset Baselines**:

The **Plan** column shows the selected limit action even before risk amounts are entered, for example `no limits, auto-close row when set`.

- **Max Loss** locks when session PnL is less than or equal to the negative loss limit.
- **Max DD** locks when drawdown from the row's session peak reaches the limit.
- **Profit Target** locks when session PnL reaches the profit target.

Risk actions:

- **Lock entries only** blocks new or increasing entries, but allows position-reducing exits. Exit quantity is capped so a locked account cannot reverse.
- **Auto-close row** immediately requests a flatten for matching managed row positions, then blocks new or increasing entries.

Clearing a risk lock through **Unlock Selected** or **Reset Baselines** requires confirmation. Connected risk-locked rows reset their session PnL baselines before they can become eligible to copy again.

`ExitsOnly` copy mode behaves like a planned reduce-only state: it blocks entries and exposure increases, but follows lead orders that reduce or close the copy row's current position.

Reconciliation is selected-row only and requires confirmation. Unlocked rows are adjusted toward the lead account using their configured sizing rules. Locked rows reconcile by reducing exposure only; they will not open or increase a position.

Dry run mode is selected before starting a copy session and stays locked for that session. In dry run, copied orders and reconcile adjustments are logged as simulated actions instead of being submitted. Manual flatten buttons remain real emergency controls.

---

## ⚠️ Warnings & Best Practices

- Only **connected accounts** are available for copying.
- A row cannot copy itself. Lead accounts are kept off automatically; copy rows point to them from the **Lead** column.
- Filled and partially filled lead orders are tracked by cumulative filled quantity to avoid duplicate target orders.
- Soft-locked and manually locked accounts still allow exits that reduce an existing position.
- Balance-ratio sizing can skip a row if NinjaTrader does not expose usable account value data.
- Always test new sizing and lockout rules in simulation before using live accounts.
- **Trade responsibly**—copied trades carry the same risk as manual entries.

---

## 📁 File Overview

- `austins-trade-copier.cs` – The complete NinjaScript AddOn source code.

---

## 📄 License

MIT License

Copyright (c) 2025 Austin Naremore

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

---

## 🙋 Feedback & Contributions

This is a personal tool built by Austin with lots of vibes. Feel free to submit new features, fixes, or report a bug in [Issues](https://github.com/anaremore/austins-trade-copier/issues).

> "Because one account isn't degenerate enough."
