# MainAPP User Manual (English)

> For operators, shift leaders, and on-site administrators using Kanban for the first time.
>
> This manual uses a step-by-step approach. Click blue buttons when you see them; handle red alarms first. You do not need to understand the code to perform daily work.
>
> **中文版:** [MainAPP 用户使用手册](./MainAPP用户使用手册.md) · **Screenshots:** [README](./README.md)

## 1. What This System Does

MainAPP centralizes shop-floor production information:

- View device status: Running, Standby, Alarm, Initial (when no data yet).
- View total output, good count, defect count, production rate, and OEE.
- View live alarms, alarm activation history, and recovery history.
- Query production, status transition, and alarm history.
- Manage devices, PLC addresses, alarm points, defect points, and counter alarms.
- Create and track work orders.
- Monitor PLC connection, scan cycle, history storage, and system resources.
- Manage licensing, shifts, refresh intervals, and display settings.

## 2. Before First Use

### 2.1 Pre-launch Checklist

Ask your administrator to confirm:

- The PC is connected to the PLC network.
- PLC IP address and port are known.
- Device names and PLC addresses are prepared.
- Current shift start and end times are confirmed.
- You have permission to edit devices and PLC configuration.
- Licensing is active or still within the trial period.

> Do not enter addresses or click “Reset Output” without confirming the address table. PLC write commands can affect live production.

### 2.2 Starting the Application

1. Double-click `MainAPP.exe`, or use the desktop shortcut provided by your administrator.
2. Wait for the main window to appear.
3. If a license dialog appears, follow the **License Management** section.
4. On first launch, open **System Settings** to confirm PLC and shift configuration before entering Device Manager.

The app opens in large-screen mode by default. If the window is not 16:9, you may see letterboxing. This keeps content from being cropped.

### 2.3 Opening the User Manual

When you need step-by-step help:

1. At the **bottom of the left sidebar** (above license info), click **User Manual (F1)**.
2. Or press **F1** on the keyboard.
3. The manual opens inside the app with screenshots (screenshot files must be shipped with the app).
4. When the UI language is English, the English manual opens automatically; other UI languages may show Chinese or English.

On first launch, a **quick guide** may appear; the last step also offers **Open User Manual**.

## 3. Main Interface Overview

The left side is the navigation bar; the right side shows the current page. Click icons to switch pages; hover for tooltips. For help, use **User Manual (F1)** at the bottom of the sidebar, or press **F1**.

![Home example](screenshots/01_home.png)

The screenshot above is an example. Device counts, output, alarms, and times change with live PLC data.

### 3.1 Common Navigation Pages

| Icon | Page | Purpose |
| --- | --- | --- |
| Dashboard | Home | Quick view of line KPIs |
| Factory | Production Line | Layout view of production status |
| Bell | Alarm Center | Handle alarms and view statistics |
| Device | Device Manager | Configure devices and addresses (Engineer+) |
| Clock | History Query | Query production, alarm, and status history |
| Chart | Review | Trends, output, and OEE summary |
| Gear | System Settings | PLC, shifts, refresh, licensing (Admin) |
| Clipboard | Work Orders | Create and track work orders |
| Monitor | Runtime Monitor | Acquisition service and system health (Admin) |
| Chart | Data Monitoring | Live data source samples |
| Recipe | Recipes | Device recipes and apply history (Engineer+) |
| Users | User Management | Accounts and roles (Admin) |
| Shield | Audit Log | Configuration and operation audit trail (Admin) |

![Production Line example](screenshots/02_production_line.png)

### 3.2 PLC Connection Status

At the bottom of the navigation bar:

- **Green dot**: PLC connected; acquisition can continue.
- **Red dot**: PLC disconnected or last connection failed; check network and settings.
- **Disconnect count**: Cumulative disconnects this session—not alarm count.

If the PLC is disconnected, home page status and output stop updating. Check **System Settings** and **Runtime Monitor** first; do not repeatedly click reset commands.

## 4. Daily Startup Procedure

Recommended order each day:

1. Open the app and wait for pages to load.
2. Confirm the PLC indicator is green (bottom-left).
3. Open **Runtime Monitor** and confirm the acquisition service is running.
4. Open **Alarm Center** and confirm no high-priority unhandled alarms.
5. Return to **Home** and confirm device count and KPIs match the shop floor.

## 5. Home: Reading Production Status

![Home dashboard](screenshots/01_home.png)

Home has **three cards** for quick overview—do not edit device configuration here.

### 5.1 Device Status (left card)

- Top: current shift and clock.
- **Center of the ring chart** (most important): small label “Current Status”, large text **Running / Standby / Alarm / Initial**, total duration below.
- Right legend: standby, run, and alarm time ratios, device health score, and total duration.

| Status | Meaning |
| --- | --- |
| **Running** | Device is producing |
| **Standby** | Temporarily stopped—not necessarily a fault |
| **Alarm** | Needs attention |
| **Initial** | No valid status yet, or not yet acquired |

### 5.2 Current Production (center card)

- **Current speed** and achievement bar vs target cycle.
- KPIs: total output, defects, quality rate, OEE, target vs actual cycle.

Confirm shift and date before comparing numbers across shifts or days.

### 5.3 Live Alarms (right card)

- Active alarms; high-severity items highlighted in red.
- Filter by level; mute alert sound without losing records.
- **View all** opens Alarm Center.

### 5.4 Home Refresh

The home page refreshes automatically. If data appears frozen:

1. Check PLC connection (green).
2. Open Runtime Monitor—confirm acquisition is running.
3. Check “Last successful acquisition time”.
4. Return to Home or use the page refresh control if available.
5. If still no data, contact your administrator—do not delete database files.

## 6. Production Line: Line-wide View

![Production Line](screenshots/02_production_line.png)

Open **Production Line** to view multiple devices in layout.

Suggested workflow:

1. Scan the line for red alarms or devices with no data (gray).
2. Locate the abnormal device by name.
3. Click a device for details or jump to Device Manager.
4. After fixing the issue on site, wait for the next scan cycle to confirm recovery.

Use **History Query** when you need detailed historical data.

## 7. Alarm Center: Handling Alarms

![Alarm Center](screenshots/03_alarm_center.png)

The Alarm Center shows active alarms, today’s trigger count, recovery count, and recent events.

### 7.1 Handling an Alarm

1. Open **Alarm Center** (bell icon).
2. Review the active alarm list.
3. Handle high-severity alarms first.
4. Go to the device on site using alarm and device names.
5. After fixing the fault, wait for the PLC signal to recover.
6. Confirm the alarm leaves the active list or shows recovered.
7. Use **History Query** to trace activation and recovery times if needed.

### 7.2 Colors and States

- **Red**: Priority or severe condition.
- **Yellow**: Needs attention or pending action.
- **Green / Recovered**: Condition cleared; history is retained.
- **Zero active alarms**: Does not mean no alarms occurred today.

### 7.3 Alarm Won’t Clear

Check in order:

1. Is the device actually recovered on site?
2. Is the PLC connected?
3. Is the alarm address correct?
4. Is the alarm point enabled in Device Manager?
5. Is manual confirmation required on site?

Visual alerts are always shown. If sound is enabled, new alarms play a local alert. Disable sound under **System Settings → Display**; this does not disable alarm records or visual alerts.

Do not delete alarm records—they affect traceability and statistics.

## 8. Device Manager: Adding and Configuring Devices

![Device Manager](screenshots/04_device_manager.png)

Device Manager has a device list (left) and detail editor (right).

### 8.1 Information to Prepare

| Field | Example | Notes |
| --- | --- | --- |
| Device name | Injection Molder 1 | Display name |
| OK counter address | D100 | Good count |
| NG counter address | D110 | Defect count |
| Status address | D120 | Status or status counter |
| Reset address | M100 | Used at shift change |
| Alarm address | M200 | One alarm point per bit |
| Defect address | D300 | Defect type counter |

Use addresses from your PLC program and site documentation.

### 8.2 Adding a Device

1. Open **Device Manager**.
2. Click **Add Device**.
3. Enter device name and OK/NG/status/reset addresses.
4. Add alarms, defects, or counter alarms on the corresponding tabs.
5. Validate addresses (no duplicates, not empty, correct format).
6. Save and confirm the device appears in the list.
7. Wait one scan cycle and confirm status and data update.

### 8.3 Editing and Deleting

Edit: select device → change fields → save → verify one acquisition cycle if addresses changed.

Before delete: confirm the device is unused, historical data policy is understood, and configuration is backed up. Deleting config does not automatically delete history records.

> **For engineers:** Data source CSV import/export is in [Appendix B](#appendix-b-engineers-and-administrators).

## 9. History Query

![History Query](screenshots/05_history_query.png)

### 9.1 Query Steps

1. Select type: Production, Alarm, or Status transition.
2. Set start and end time.
3. Select device(s)—empty often means all devices.
4. Select shift if needed.
5. Click **Query** and review result count and time range.
6. Export for archival if needed.

### 9.2 No Results

- Time range reversed?
- Wrong device selected?
- Wrong shift?
- Was the app running and acquiring in that period?
- Check Runtime Monitor for history write diagnostics.

### 9.3 Production Review and Reports

![Review](screenshots/06_overview.png)

Open **Review** for summaries by current shift, previous shift, today, last 24 hours, or last 7 days. Includes output, yield, OEE, alarms, device details, shift comparison, and top alarms.

**Reading the charts:**

| Area | Hover | Click |
| --- | --- | --- |
| Status timeline (color bars) | Status name and time range | No popup |
| Output/speed trend | Time point and value | No |
| Production heatmap | Time bucket and value | No |
| Defect Pareto | Category and count | No |
| OEE waterfall | Labels on bars | No |
| Device table mini status bar | No | Row click changes focused device |

1. Select time range.
2. Click **Refresh** and wait for data.
3. Check data coverage hints.
4. **Export Report** (CSV) or **Export PDF** for archival.

PDF requires a system font with CJK support; export fails with a clear message if fonts are missing.

## 10. Work Orders

![Work Orders](screenshots/07_work_order.png)

1. Click **Add Work Order**.
2. Enter order number, product, target quantity, device, and planned times.
3. Save and set status: Pending, In Progress, Completed, or Cancelled.
4. Compare actual output on Home and History Query during production.
5. Review target vs actual when complete.

Use consistent naming for products and orders. The list sorts by planned start time by default; **Schedule conflict** means overlapping active orders on the same device—adjust plans before starting.

## 11. System Settings: PLC, Shifts, and Display

![System Settings](screenshots/08_settings.png)

### 11.1 PLC Connection

- **IP address**: e.g. `192.168.1.10`
- **Port**: e.g. `4999` (site-specific)
- **Poll interval** (ms)
- **History write interval**: writes per N scans
- **Home refresh interval**

After changes: validate numbers → save → wait for connection status → verify in Runtime Monitor.

### 11.2 Shifts

Configure name, start, and end times. For overnight shifts, confirm end time crosses midnight correctly. Avoid renaming shifts used in historical statistics.

### 11.3 Display

Dark theme, font scaling, home refresh, alarm sound, and other preferences. Increase font scale for large displays.

### 11.4 UI Language

Under **System Settings → Display**, select the UI language (简体中文 / English / 日本語 / Português). Changes take effect after **restarting the application**.

## 12. License Management

The top of **System Settings** shows license status: trial days remaining, activated, or expired.

1. Copy the machine ID.
2. Send it to your license administrator.
3. Enter the activation code.
4. Click **Activate** and verify functionality.

Activation codes are typically machine-bound.

## 13. Runtime Monitor

![Runtime Monitor](screenshots/09_runtime_monitoring.png)

| Item | Normal | Abnormal |
| --- | --- | --- |
| PLC connection | Connected | Disconnected / reconnecting |
| Acquisition service | Running | Stopped |
| Recent successful devices | > 0 (or none configured) | Stuck at 0 |
| Last acquisition time | Updating | Stale |
| Consecutive failed cycles | 0 or occasional | Increasing |
| History write | OK | Errors / disk full |
| System resources | Sufficient | High memory / low disk |

Check PLC status, last successful acquisition, and consecutive failures together when diagnosing faults.

## 14. Data Monitoring

![Data Monitoring](screenshots/10_data_monitoring.png)

Shows live samples for configured data sources—useful for verifying addresses, limits, and triggers.

1. Select a device.
2. Review sample values, quality, and timestamps.
3. Compare with the PLC.
4. If stale, check PLC connection and Runtime Monitor.

Read-only; edit configuration in Device Manager.

## 15. Recipes

![Recipes](screenshots/11_recipe_manager.png)

Maintain device recipe parameters (Engineer+).

1. Select device.
2. Add or edit recipe entries.
3. Save and select recipes during production changes.
4. Review apply history and PLC acknowledgements.

Keep recipe names aligned with process documentation.

## 16. User Management

![User Management](screenshots/12_user_manager.png)

Create accounts, assign roles, reset passwords (Admin).

| Role | Typical access |
| --- | --- |
| Operator | Home, Production Line, Alarm Center, Work Orders, History, Review, Data Monitoring |
| Engineer | Above + Device Manager, Recipes |
| Admin | All pages including System Settings, Runtime Monitor, User Management, Audit Log |

The last administrator account cannot be deleted or demoted.

## 17. Audit Log

![Audit Log](screenshots/13_audit.png)

Traces configuration changes, logins, recipe applies, and other critical actions.

1. Set time range.
2. Filter by action type or keyword if needed.
3. Query and review results.
4. Export for records.

## 18. FAQ

### Q1: PLC always disconnected

Check cable/switch, ping PLC IP from Windows, **System Settings** IP/port, PLC allow list, Runtime Monitor and logs.

### Q2: Home output not increasing

Confirm production is running, PLC connected, OK/NG addresses correct, not in shift reset window, and recent successful device count.

### Q3: Home shows “Initial” or line cards have no data, but PLC is green

PLC green means connection only—not per-device address correctness. Check that device’s addresses and confirm acquisition in Runtime Monitor.

### Q4: No alarm in app but device alarms on site

Verify alarm points exist, are enabled, and addresses match PLC signals.

### Q5: History query empty

Check time range, device, shift, and history write status. Recent data may not have been written yet.

### Q6: Settings don’t seem to apply

Confirm Save was clicked; reopen the page. Check write permissions and `.corrupt` backup files.

### Q7: App won’t start or exits immediately

Contact admin with: full error text, time, recent config changes, network/PLC changes, and logs under `%APPDATA%\Kanban`. Back up before deleting data.

## 19. Daily Shutdown

1. Confirm no reset or save in progress.
2. Update work order status per site procedure.
3. Close MainAPP and wait for clean exit.
4. Back up `%APPDATA%\Kanban` if required.

## 20. Three Rules for New Users

1. **Check PLC connection before judging data anomalies.**
2. **Confirm the address table before editing devices or sending writes.**
3. **Back up the data directory before deleting devices, changing shifts, or fixing corrupt config.**

---

## Appendix A: Data Backup (Administrators)

- Default data folder: `%APPDATA%\Kanban`
- Back up the entire folder (Config, Logs, databases, etc.).
- Never delete `.db` or `.json` files without a backup.

## Appendix B: Engineers and Administrators

Content below is for engineers and IT—operators can skip it.

### B.1 Data Source CSV Import/Export

On the device **Data Sources** tab, export/import CSV per device. Each row is a value item.

- **Replace** clears existing sources for that device; **Append** merges by name.
- Click **Save** after import.
- Always use an exported CSV as your template.

### B.2 Manual Languages

The app UI supports 简体中文, English, 日本語, and Português. The user manual is available in **Chinese** and **English**; F1 opens the matching manual when the UI is set to English, otherwise Chinese or English per settings.
