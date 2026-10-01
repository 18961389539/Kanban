# Kanban User Manual (English)

> For operators, shift leaders, and on-site administrators using Kanban for the first time.
>
> This manual uses a step-by-step approach. Click blue buttons when you see them; handle red alarms first. You do not need to understand the code to perform daily work.
>
> Last updated: 2026-10 · Applies to: Kanban (`MainAPP.exe`). If the UI changes, the on-screen behavior takes precedence; ask an administrator to update this manual.
>
> **中文版:** [Kanban 用户使用手册](./MainAPP用户使用手册.md) · **Screenshots:** [README](./README.md)

## 1. What This System Does

Kanban centralizes shop-floor production information:

- View device status: Running, Standby, Alarm, Offline, or Unknown. With no duration data, the ring shows “No status data”.
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
- A login account is ready (the app auto-logs-in as the default administrator; usually nothing to do).
- Licensing is active or still within the trial period.

> Do not enter addresses, or click Reset All on Production Line, without confirming the address table. PLC write commands can affect live production.

### 2.2 Starting the Application

1. Double-click `MainAPP.exe`, or use the desktop shortcut provided by your administrator.
2. Wait for the main window to appear.
3. If a window titled 软件激活 appears, send the machine code to the person who issues keys, paste the key, and click 激活. 取消 closes the program. This window does not appear during the trial or after the license is already active.
4. On first launch, open **System Settings** to confirm PLC and shift configuration before entering Device Manager.

The app starts maximized. If the display is not 16:9, you may see letterboxing. This keeps content from being cropped.

The app has two run modes: **Full mode** (default, all pages available) and **Viewer mode** (usually for shop-floor displays; Home, Production Line, Alarm Center, Device Detail, and Data Source Monitor remain, and management pages are hidden). Switching modes is done by an administrator under **System Settings → General**. See [System Settings](#12-system-settings-data-source-plc-shifts-and-display).

### 2.3 Opening the User Manual

When you need step-by-step help:

1. At the very **bottom of the left sidebar**, click **User Manual (F1)**.
2. Or press **F1** on the keyboard.
3. The manual opens inside the app with screenshots (screenshot files must be shipped with the app).
4. When the UI language is English, the English manual opens automatically; other UI languages may show Chinese or English.

On first launch, a **quick guide** may appear; the last step also offers **Open User Manual**.

> Other shortcuts: `Ctrl+1`–`Ctrl+9` open the first nine sidebar pages the signed-in account can see. For an administrator those are Home, Production Line, Alarm Center, Work Orders, Review, History Query, Device Manager, Recipes, and System Settings. For an engineer the ninth page is Data Monitoring. An operator sees seven pages, ending at Data Monitoring, so `Ctrl+8` and `Ctrl+9` do nothing. Viewer mode keeps only Home, Production Line, Alarm Center, and Data Monitoring. `Ctrl+L` switches the current user; `F11` toggles fullscreen.

## 3. Main Interface Overview

The left side is the navigation bar; the right side shows the current page. Click icons to switch pages; hover for tooltips. For help, use **User Manual (F1)** at the bottom of the sidebar, or press **F1**.

![Home example](screenshots/01_home.png)

The screenshot above is an example. Device counts, output, alarms, and times change with live PLC data.

### 3.1 Common Navigation Pages

The left sidebar lists pages from top to bottom in the order below (some entries appear depending on the logged-in role; see Chapter 17):

| Icon | Page | Purpose |
| --- | --- | --- |
| Dashboard | Home | Quick view of line KPIs |
| Factory | Production Line | Every device’s current shift at once |
| Bell | Alarm Center | Handle alarms and view statistics |
| Clipboard | Work Orders | Add, start, complete, and abort work orders |
| Chart | Review | Trends, output, and OEE summary |
| Clock | History Query | Query production, alarm, and status history |
| Device | Device Manager | Configure devices and addresses (Engineer+) |
| Recipe | Recipes | Recipes written to the PLC (Engineer+) |
| Gear | System Settings | Data source, PLC, shifts, refresh, licensing (Admin) |
| Monitor | Runtime Monitor | Acquisition service and system health (Admin) |
| Users | User Management | Accounts and roles (Admin) |
| Shield | Audit Log | Configuration and operation audit trail (Admin) |
| Chart | Data Monitoring | Live data source samples |

![Production Line example](screenshots/02_production_line.png)

### 3.2 Checking Connection Health

A global connection banner appears at the **top** of the page (no banner when connected normally):

- **Red banner**: PLC disconnected or the last connection failed; check network and settings.
- **Yellow banner (connecting)**: the system is trying to connect; wait a moment.
- **Yellow banner (data stale)**: connected, but data has not updated for a while—the acquisition end may be stuck.

If the PLC is disconnected, home page status and output stop updating. Check **System Settings** and **Runtime Monitor** first; do not repeatedly click Reset All.

> When the data source is **Collector service (Remote)**, the banner reflects the connection to the **collector service**, not the PLC directly. Check the actual PLC status in **Runtime Monitor**.

## 4. Daily Startup Procedure

Recommended order each day:

1. Open the app and wait for pages to load.
2. Confirm there is no red or yellow connection banner at the top of the page.
3. Open **Runtime Monitor**. In local acquisition, confirm the PLC is connected and the acquisition loop is still running. In remote acquisition, also confirm the collector can be reached.
4. Open **Alarm Center** and confirm no high-priority unhandled alarms.
5. Return to **Home** and confirm device count and KPIs match the shop floor.

## 5. Home: Reading Production Status

![Home dashboard](screenshots/01_home.png)

<!-- page-help:home -->
The title is Production Dashboard. The page follows the one device selected in the top bar. The small chip beside a number names its window: Live is right now, Shift is this shift’s running total, Order is the work-order window. Do not read numbers from different chips as the same count.

### Top bar

A green dot means the PLC is connected; red means it is not. The status word next to the dot hides itself when data is live. When it is visible it is one of these:

- No Device Selected: nothing is chosen yet, so output and OEE on the cards show “—”.
- PLC Disconnected: local acquisition cannot reach the PLC. On a computer that watches a remote service, this word is Service Disconnected.
- No Data: the link is up, but there is no usable runtime snapshot yet. Output and OEE still show “—”.

The device list only changes which machine you are looking at. Work Order Management opens orders. Device Detail opens this device. Addresses and alarm points are edited in Device Manager, not here.

### Device Status (top left)

The word beside the title is the live state, not a summary of the shift. Hovering it says Current Status.

- Running: the device is producing.
- Standby: it has paused. That is not automatically a fault.
- Alarm: an alarm needs attention right now.
- Offline: the label may add a cause, such as PLC, communication lost, acquisition stopped, or a gap in acquisition.
- Unknown: the status word does not match the states above.

To the right of the title are the shift name and how far the shift has run, as a percent of shift time, then the date and clock. That percent is elapsed time, not output achievement.

Above the ring is Current Speed, in pcs/h, with a Live chip. It is this shift’s (good + defect) ÷ running hours. Until running time reaches 5 seconds the speed stays 0, so a just-started shift is not blown up. The percent and the bar beside it are achievement against rated output: 90% and above is green, 70% and above is yellow, and below that is red.

The legend on the right, top to bottom, is Running (green), Alarm, Standby, and Offline. Each row is a duration and its share of the four added together. The ring center reads State Duration Share, with that total under it and a Shift chip. With no duration data the ring area shows “No status data”.

Under the legend is cycle time. Actual cycle time = 3600 ÷ current speed, in seconds per piece. A speed of 0 shows “—”. Target cycle time sits beside it. A gap under 0.01 seconds shows “● Met”. A faster piece shows “▲ …s faster”. A slower piece shows “▼ …s slower”, in the warning color.

### Current Production Progress (top center)

Numbers appear only while a work order is running. Otherwise the card says “No active work order” and points you to Work Order Management. A blank card is not an output of 0.

With an order, the top line is the order number and the product code. The four cells carry scope chips:

- Order Target: how many pieces this order asks for.
- Shift OK: good pieces in the current shift only.
- Order cumulative OK: follows the order across shifts, so it can be larger than Shift OK.
- Order achievement: cumulative OK ÷ order target. 90% and above is green, 70% and above is yellow, and below that is red.

When a schedule exists, a bar appears under the four cells. The fill is actual achievement. The tick in the middle is where the plan says you should be by now. “Behind plan” is yellow, “ahead of plan” is green, and a passed end time reads as overdue. Shift total output (good plus defect) is on the Current Shift Quality card below. Current speed is on the Device Status card, not here.

### Live Alarms (top right)

Alarms that are on right now. The count beside the title is the active total, and it turns red when any of them is High. A count of 0 means none are active at this moment. Alarms that occurred earlier today and already recovered are not listed.

- H, M, and L toggle High, Medium, and Low. They only hide rows. Turning one off does not reduce the count beside the title.
- Mute stops the local sound and the flashing border together. The list still updates. Sound for the whole app can also be turned off under Settings → Display.
- A new row gets a border in its level color. A count-type alarm uses a counter icon, not the ordinary alarm icon.
- Within the same level, earlier starts come first. Home shows at most 5 rows. Past that, a note says only the first 5 are shown. Open Alarm Center from the sidebar for the rest, or to read the full story.

### OEE (bottom left)

Scoped to the current shift. The three factors and the overall value all carry a Shift chip. Until data is live, the four percentages are “—”, rounded to a whole percent. With no usable run data the card shows “No OEE data”. The large ring is overall OEE. The three items on the right are A: Time Availability, B: Performance Rate, and C: Quality Rate. Overall OEE is their product, not their average. Availability, performance, and overall OEE use one color scale: 85% and above is green, 60% and above is yellow, and below that is red. Hover a number to see the formula with this shift’s values filled in.

- Availability = running time ÷ (running time + alarm time). Standby and offline are left out of the denominator, so a long standby with no alarm can still show 100%.
- Performance = actual output (good + defect) ÷ (rated output per hour × running hours). Faster than the rating displays as 100% at most.
- Quality = good count ÷ (good count + defect count). This one uses a different scale: 95% and above is green, 90% and above is yellow, and below that is red. Yellow means it has not reached 95%, but it is close.

Device health, at the right of the title, is a 0–100 score: 30% availability, 20% performance, 25% quality, 25% stability. Stability = 1 − alarm time ÷ (run + alarm + standby), and offline is excluded. 85 and above is Healthy, 70 and above is Good, 60 and above is Needs attention, and below that is Abnormal. With no run, alarm, or standby time the score is “—”.

| Number | Color |
| --- | --- |
| Availability, performance, overall OEE | 85% and above green, 60% and above yellow, below 60% red |
| Quality (the OEE factor, and shift quality) | 95% and above green, 90% and above yellow, below 90% red |
| Speed achievement, order achievement | 90% and above green, 70% and above yellow, below 70% red |
| Device health | 85 and above Healthy, 70 and above Good, 60 and above Needs attention, below 60 Abnormal |
| Schedule bar | Ahead of plan green, behind plan yellow, past the end time reads overdue |

### Current Shift Quality (bottom center)

On the left is shift total output, good plus defect, in pieces. On the right is cumulative quality: good so far this shift ÷ (good + defect). It is not the quality of one hour by itself. Colors match the OEE quality number: 95% and above green, 90% and above yellow, below that red. The gap beside it is versus 95%: positive means above the target, negative means still short. With no output both numbers are “—” and the chart area says “No output data”.

The left axis is green bars. One bar is that clock hour’s OK count. An hour with zero OK draws no bar. The right axis is the line. Each clock hour keeps one point, and the point is the shift’s cumulative quality at the end of that hour, drawn in the middle of the hour. The 8:00–9:00 hour is labeled at 8:30. The current hour’s bar and point refresh about every 15 seconds. The chart does not wait for the next hour to draw. Neighboring hours stay one line. A skipped hour breaks the line and paints a gray band that says “No data” and how many hours were empty. Hover a bar for the hour and the OK count. Hover a point for the time and the percent.

### Defect Pareto (bottom right)

The title says Top 8 and carries a Shift chip. The subtitle is “Current device · shift increment”. Bars are defect counts. The line is the cumulative share from largest to smallest. When there is data, the title row also shows the defect total and what share of NG those defects are. Kinds past the eighth are folded into “Others (N)”.

The three empty states are different:

- No defect items configured: names and PLC addresses are still missing in Device Manager.
- No defect counts right now: the addresses exist, and every reading is 0.
- No defect data: no device is selected.

### When numbers stay still

The page refreshes on its own. Check the PLC dot, then whether the top bar is stuck on No Data or PLC Disconnected. Open Runtime Monitor for whether acquisition is running and when it last succeeded. Leave the page and come back. If it is still empty, ask an administrator. Do not delete the database.
<!-- /page-help -->

## 5.6 Device Detail

<!-- page-help:device-detail -->
Open it from Home with Device Detail, or from a Production Line card with Details. It is not in the sidebar, so the sidebar does not highlight a page while you are here. Back, or Esc, returns to the page you came from. With no device selected the page says No Device Selected and tells you to pick one from Home or Production Line. This page is one device. Addresses and alarm points are edited in Device Manager. F5 refreshes now.

### Beside the title

- The chip next to the device name is the live state. Running is green, Alarm is red, Standby is yellow, and anything else uses the offline gray. The words are still the state that was collected. The device id sits further right.

### The cards on top use different windows

- Good Output, Defective Output, and Quality Rate are this device’s current session totals. Quality = good ÷ (good + defective).
- Today's Alarms counts historical triggers for this device since midnight, including ones that already recovered. It is not the “still on” count on the Home alarm card.
- Run Time is how long this device has spent running.
- Device Config shows target output in pieces per hour, and the recipe. PLC addresses sit in an expander and are read-only.
- OK Count and NG Count under PLC Live Values are the raw PLC counters. They jump after a reset. They are not the session totals shown on Home.
- Current Work Order shows the order number, the product, and progress. With no order in progress it says No Active Work Order.
- Output comparison: actual output in pieces per hour = (good + defective) ÷ running hours, and it is 0 when running time is still under 5 seconds. Gap = (actual rate − target) ÷ target. A negative gap is below target. Run-window due = rated output per hour × running hours, and the actual count is good plus defect. Alarm, standby, and offline are not multiplied in.

### Hour-by-hour plan

Each cell is one hour of the current shift. The plan is rated output prorated by how many minutes of that hour fall inside the shift. It includes stops, and it counts OK only. Run-window due above excludes stops and includes defectives, so the two due figures are meant to differ.

A past hour that met the plan is marked On target, with a green edge. A past hour that missed it is marked Below plan, with a red edge. The current hour is highlighted. Hours that have not started are faded. Above the cells is cumulative OK, due, and a percent. Off shift, the board says Currently off shift, and shifts need to be configured in Settings.

### Colors

| Mark | Color |
| --- | --- |
| Running beside the title | Green |
| Alarm beside the title | Red |
| Standby beside the title | Yellow |
| Any other state beside the title | Gray. The text is still the collected state |
| A past hour that met the plan | Green “On target”, green edge |
| A past hour that missed the plan | Red “Below plan”, red edge, and a red-tinted background |
| The current hour | The theme color, with a thicker edge |
| An hour that has not started | Faded |
| OEE breakdown percentages | Not colored. The formulas match Home. The color bands are on Home and on History OEE |

### Further down

- Data sources: when any are configured, each source shows its live value first. Open one to see its trend, then return to the live cards. When none are configured this block is empty. Add them on the data-source tab in Device Manager.
- OEE Breakdown is availability, performance, and quality, using the same formulas as Home. The overall figure is the product of the three.
- Status duration (incl. offline) puts running, alarm, standby, and offline into the same distribution. Availability’s denominator is still only running plus alarm. Standby and offline stay out of that denominator.
- Active Alarms (Live) are the ones still on. Recent Alarm Events cover the last 24 hours, including recovered ones. Defect Ratio is this device’s defect mix.
<!-- /page-help -->

## 6. Production Line: Line-wide View

![Production Line](screenshots/02_production_line.png)

<!-- page-help:production-line -->
This page shows every device at once, for walking the line. The count beside the title is how many devices there are. The top right is the current shift name and its time range. Output and OEE on a card belong to that device’s current shift. The four status counts above cover every device. The filters below do not change those four numbers.

### The top row

- Type a target output in pieces per hour, then Apply to all devices. After you confirm, that rated output is written to every device. When the devices do not already share one rated output, the box shows 0.
- Running, Alarm, Standby, and Offline are how many devices are in each state right now.
- Reset All writes an OEE reset to every device and clears the software-side totals. It asks first, and it cannot be undone. The button stays disabled when the PLC is disconnected, or when the signed-in account is below engineer.

| Mark | Color |
| --- | --- |
| Running count | Green |
| Alarm count | Red |
| Standby count | Yellow |
| Offline count | Gray |
| OEE, availability, performance, and quality on a card | Not colored. The formulas match Home. Home’s 85% / 60% and 95% / 90% bands are not applied to these percentages |

### Filters and cards

- All Statuses, Running, Alarm, Standby, and Offline only decide which cards are shown. Search matches the device name. Sort is Default, Alarm First, By OEE, or Output from high to low.
- One card is one device: state, recipe name, OK, NG, total output, cycle time, and the durations for run, alarm, standby, and offline, plus overall OEE, availability, performance, and quality. Those rates use the same formulas as Home, and the overall figure is the product of the three. Availability’s denominator is running plus alarm. Standby and offline stay out of it. When a shift target exists, the card also shows good-count pace (OK against due).
- Details opens Device Detail for that device.
- With no devices at all the page says No Devices and points you to Device Manager. When the filter matches nothing it says No matching devices, and you can clear the filters.

Home follows the one device in its top bar. Use this page to find the device, then open its detail. Use History Query for a span of stored records.
<!-- /page-help -->

## 7. Alarm Center: Handling Alarms

![Alarm Center](screenshots/03_alarm_center.png)

<!-- page-help:alarm-center -->
The left side is what still needs handling. The right side is what already happened. After an alarm recovers it leaves the left list, but it stays available on the right and in History Query. The small text under the title is the last refresh time. Under the range control a fixed line says the event stream and the Top list follow the selected range, while today’s trigger and recover counts always start at midnight.

### What each toolbar control changes

- Device: one device, or all devices.
- Search: device name or alarm name. It filters the left list, today’s two counts, the earliest alarm, and the affected-device count.
- Range: last 1 hour, last 4 hours, last 24 hours, or this shift. The page opens on last 4 hours. Range changes only the event stream, the Top ranking, and the “most frequent” cell. It does not change Triggered Today or Recovered Today.
- High, Medium, Low: beside the left-list title. Turning a level off removes it from the left list, and also from today’s counts, the earliest alarm, and the affected-device count.
- Speaker: mutes the local sound for new alarms. The list, the colors, and the history stay. Sound for the whole app can also be turned off under Settings → Display.
- Refresh: load now. Otherwise the left list updates about every 3 seconds and the statistics about every 60 seconds.

### The six numbers do not share one window

- Active Alarms: still unrecovered right now, after the device, search, and level filters.
- Triggered Today and Recovered Today: event counts from midnight until now. The small line compares them with yesterday up to the same time of day. ▲ means higher than yesterday, ▼ means lower. Fewer triggers turns the line green. More recoveries turns the line green. If yesterday’s count was 0, the line is “—”, because a percent cannot be formed. Hover it for “Versus yesterday at the same time”. The range control does not affect these two. Device, search, and level do.
- Earliest Alarm · Ongoing: among alarms that are still on, how long the one that started first has run. The line under the duration is its name. It is not “the longest alarm in history”.
- Affected Device: how many devices those active alarms sit on. With one device selected this is usually 1 or 0.
- Most frequent: the label itself includes the range, for example “Most Frequent Alarm (Last 4h)”. The value is the name with the most triggers in that window, not since midnight. With no triggers in the window it is “—”.

If the history database behind the statistics fails, a red banner appears under the six numbers. Do not treat today’s comparison or the most-frequent name as settled until Runtime Monitor shows acquisition and the database are healthy again.

### Colors

| Mark | Color |
| --- | --- |
| High | Red |
| Medium | Orange |
| Low | Yellow |
| Fewer triggers than yesterday, or more recoveries than yesterday | The comparison text is green |
| A recovery on the event stream | A green check |

### Each row on the left

The list title is Active Alarms (Live), with a red edge. A row shows the alarm name, a High / Medium / Low pill, the start time, the device, and how long it has already lasted. Within the same level, earlier starts come first.

- Copy: copy the text of this alarm.
- View alarm history: open History Query on the Alarm Records tab, already aimed at this device and this alarm name.
- At most 200 rows are shown. Past that, a note says only the first 200 are shown. A count of 0 means nothing is waiting now. It does not mean nothing happened today.

Handle high severity first. Confirm the name and the device on site, then wait for the PLC signal to clear. The row leaves the list only after that. If it stays, confirm on site that the signal has actually dropped, then check the PLC connection and whether the address and the name in Device Manager match the program. Alarm Management has no separate enable switch. A saved address is collected. Do not delete records to empty the list.

### The two blocks on the right

- Recent Event Stream: triggers and recoveries inside the selected range. A trigger uses a bell. A recovery uses a green check. The title shows how many events are in the window. The same alarm rising again within two minutes is folded into one row, with a note of how many were merged, so a burst does not fill the list. At most 500 rows are shown. Past that, a note says only the first 500 are shown.
- Trigger Count Top 10: the ranking for that same range, at most 10 names. Switch it to “By triggers” or “By duration”. Duration is the sum of every episode in the window: a recovered episode uses the paired length, and one that is still open counts until now. It is not “the single longest episode”.

For anything older than the range, or for a full duration and pending table, use the Alarm Records tab in History Query.
<!-- /page-help -->

## 8. Device Manager: Adding and Configuring Devices

![Device Manager](screenshots/04_device_manager.png)

<!-- page-help:device-manager -->
This page edits device configuration. It needs an engineer or administrator account. Pick a device on the left and edit it on the right. Click Save, or press Ctrl+S. While the title shows “● Unsaved”, the change is not in effect yet.

### The left side

- Search and the status list only change which devices you see. They do not change configuration. With no devices the list says No Devices. When a filter matches nothing, clear the filters.
- Add creates a device. Delete removes that device’s configuration. It does not remove production or alarm history that was already stored. It asks before it deletes.
- Validate checks every device and does not write. An address conflict shows a warning icon beside the title.
- Import and Export are the device configuration file, for backup or for moving to another computer. They are not production or alarm history. The columns for a data-source CSV are in [Appendix B](#appendix-b-engineers-and-administrators).

### The six tabs on the right

With no device selected, the right side tells you to select one or click Add.

- Device Parameters: device name, target output in pieces per hour, OK count address, NG count address, status address, OEE reset address, recipe name, and recipe address. Use the addresses from the PLC program, for example D100. Read loads the current PLC values, and it stays disabled while the PLC is disconnected. Apply to all devices on Production Line writes this same target output.
- Alarm Management: each row needs an alarm name, a PLC address such as M100, and a level. English, Japanese, and Portuguese names can be filled in too. There is no separate enable switch. After you save, Home and Alarm Center report that point. When an alarm will not clear, check the address and the name here first.
- Defect Management: defect name and PLC address. When the Home Pareto says no defect items are configured, this tab is still empty.
- Counter Alarm: alarm name, PLC address such as D300, upper threshold, and unit. It fires when the count reaches the threshold. It is not the same kind of point as a bit address in Alarm Management. An empty tab says Not configured.
- Data sources: extra values collected for this device. The data-source cards on Device Detail come from here.
- Work Order: orders linked to this device. Start and complete them in Work Orders.

### After you save

Wait one acquisition cycle. If an address is wrong, Recent Successful Reads on Runtime Monitor drops toward 0, or the page reports an invalid address or an address conflict. After a rename, Home, Alarm Center, and History Query show the new name. Names already stored on old records stay as they were written.
<!-- /page-help -->

## 9. History Query

![History Query](screenshots/05_history_query.png)

<!-- page-help:history -->
This page reads records that have already been stored. It is not the live home screen. Changing the device, quick time, start, end, shift, or alarm type waits about 0.8 seconds and then queries by itself. Query runs immediately and starts again at page 1. If the start is after the end, the page says the start cannot be after the end, and this attempt returns nothing.

Output, status, and alarm tables show 50 rows per page, newest first. The summaries, the note, and the chart use every matching row, so turning the page only swaps those 50 rows. OEE Analysis is not paged. With no device selected, these tabs stay empty.

### How the filters stack

- Device: one device. Output, status, alarms, and OEE all follow it. The alarm-type list appears only on Alarm Records.
- Quick time: Custom, today, yesterday, last 7 days, last 30 days, then this shift, previous shift, this week, this month, and the last 1 hour, 4 hours, and 24 hours. Last 7 days is seven calendar dates including today. Last 30 days is the same kind of count. After you pick today or yesterday and then edit a clock time, the quick choice returns to Custom, so the label cannot keep saying Today while the clocks no longer match. Both ends are included. If the end is still in the future, open-alarm duration and the state time used by OEE stop at now. Time that has not arrived is not counted.
- Shift: leave it on all shifts to keep every shift in the range. After you pick one, only rows that stored that shift name when they were written remain. Changing the shift setup later does not rewrite the name on old rows.
- Alarm type: every alarm name that appeared in this time window, in name order, with All Alarms first. After you pick one, the table, the four summaries, and the chart keep only that name. The dropdown itself still lists the other names, so you can switch without going back to All Alarms.
- Reset clears the shift and the alarm type, sets the range from yesterday 00:00 through today 23:59:59, and labels the quick choice Custom. A device that is already selected stays selected. If none is selected and the list has devices, the first one is selected. The table is cleared, and reset does not follow that with an empty automatic query.
- If nothing comes back, check whether the times are reversed, whether the device is the other one, and whether the shift name is the name that was current then. Then check Runtime Monitor for acquisition in that period. An empty result does not by itself mean the line produced nothing.

### Export

Export asks for the scope first. Yes writes every filtered row. No writes only the current page of 50. Cancel writes nothing. The file is a CSV with a header, saved under the Exports folder in this computer’s data directory. A current-page file name includes the page number, and the file ends with a note that it is one page. Export stays disabled until a query has returned rows. SN Traceability does not use this export. The title line “Totals use all N rows” is the basis of the summary numbers, not the sum of the 50 rows on this page.

### Output Query

Five numbers: total output, total OK, total NG, quality rate, defect rate. The two rates keep one decimal. Quality = good count ÷ (good count + defect count). Colors match Home and the OEE quality number on this page: 95% and above is green, 90% and above is yellow, and below that is red. Defect rate is the rest, colored as the complement: 5% and below is green, 10% and below is yellow, and above that is red. A 96% quality rate and a 4% defect rate are both green.

| Number | Color |
| --- | --- |
| Quality | 95% and above green, 90% and above yellow, below 90% red |
| Defect rate | 5% and below green, 10% and below yellow, above 10% red |

When there is data, a note sits under the five numbers, then an output chart, then the table. Columns: time, device, shift, OK output, NG output, and the device state at that time. A row is one stored output record, not the live counter.

### Status Duration

Four durations: running, alarm, standby, offline. The note under them names the longest running stretch, the longest standby stretch, and any single alarm longer than 30 minutes. Standby above 20% of the query window is called out. Alarm time above 20% is called out in the same note, even when standby is also above 20%.

Three charts sit in the middle, left to right: state share, duration bars, and the status-transition timeline. The table under them is each change, 50 rows per page: time, device, previous state, next state. Offline may include a cause, such as communication lost or a gap in acquisition.

### Alarm Records: one row is one edge

Columns: time, device, alarm name, PLC address, event type, duration. There are three event types.

- Triggered: the alarm rose. The row is tinted red.
- Recovered: the alarm fell.
- Shift change: the alarm was still on when the shift changed, and the new shift starts the timer again. It is not a recovery, and it is not counted in the recovered total.

Duration is written on trigger rows. Pairing works like this: each recovery takes the nearest earlier trigger of the same device and the same alarm that no other recovery has already taken, and the duration is trigger to recovery. The latest trigger that still has no recovery is measured through the end of the query, or through now when the end is in the future. An earlier unpaired trigger that already has a recovery after it, plus recovery rows and shift-change rows, still show “—”.

The four numbers use every row after the filters. Turning the page does not change them.

- Triggered and recovered counts: how many of each event. One rise and one fall are usually two rows, so the counts are often close and not always equal.
- Pending: groups of device plus alarm whose latest trigger has no recovery after it. A shift change in between does not clear the group.
- Average recovery time: the mean of the pairs above. An hour or more is shown in hours; otherwise whole minutes. The card is hidden when nothing paired.
- The chart ranks alarm names by trigger count. When there is data, the note above it lists three things: the most frequent names and their share; a chain that shows up at least twice inside a 5-minute window; and the alarms still open at the end of the query that have lasted the longest. “Still open” uses the same rule as Pending: no recovery after the last trigger. A shift change in between does not close it.

### SN Traceability

Enter a serial number to see the records for that piece. Columns: SN, device, work order, shift, time, result. OK is green, NG is red. If SN recording is off, or the remote connection has no SN channel, the tab only says SN traceability is not available in the current mode. Changing the time range will not find rows in that case.
<!-- /page-help -->

<!-- page-help:history-oee -->
On this tab the device, the time range, and the shift still come from the bar at the top of the page. Changing them recalculates. This tab is not paged, and it has no alarm-type filter. Overall OEE is the product of the three rates, not their average, so one low factor pulls the result down further. All four numbers keep one decimal. Hover a number to see the formula with this query’s values filled in.

### The four numbers, and their colors

Left to right: quality, performance, availability, overall OEE.

- Quality = good count ÷ (good count + defect count). Colors match the home quality number: 95% and above is green, 90% and above is yellow, and below that is red. Yellow means it has not reached 95%, but it is close. No output in the range shows 0. It does not mean “not calculated yet”.
- Performance = actual output (good + defect) ÷ (rated output per hour × running hours). Rated output is pieces per hour, and only running time is multiplied in. Standby and offline are not in that denominator. Faster than the rating displays as 100% at most. No running time, or no rated output, shows 0. Colors use the OEE scale: 85% and above is green, 60% and above is yellow, and below that is red.
- Availability = running time ÷ (running time + alarm time). Standby and offline stay out of the denominator. A shift that was mostly standby, with zero alarm time, can still show 100%. If there was neither running nor alarm time, the rate is 0. Colors use the same 85% / 60% scale.
- Overall OEE = availability × performance × quality. Colors use the same 85% / 60% scale.

| Number | Color |
| --- | --- |
| Quality | 95% and above green, 90% and above yellow, below 90% red |
| Performance, availability, overall OEE | 85% and above green, 60% and above yellow, below 60% red |
| A shift-table row whose performance is under 60% | The whole row is yellow |

### Charts, the note, then the inputs

Left to right: OEE Metrics (the three rates and the overall value together), Trend Change (one point per shift, time on the horizontal axis, that shift’s overall OEE on the vertical axis), and Shift Comparison (one bar per shift, also overall OEE).

Under the charts a note appears, unless all three rates are 0. If the lowest factor is under 60%, the note names it as the bottleneck. When the query contains at least two shifts and the best and worst differ by 10 percentage points or more, it also names the lagging shift and which of the three factors opened the gap. A smaller gap is not reported.

Under that note: good count, defect count, running time in hours, and rated output in pieces per hour. These are the inputs to the formulas above. When a percentage looks wrong, read these four before doubting the overall value.

### The shift table

One row per shift: shift start, shift, quality, performance, availability, OEE. A row whose performance is under 60% is highlighted yellow. The highlight follows performance, not overall OEE, so a slow shift stays visible next to a shift whose problem is quality.

When one rate is clearly low: quality sends you to Output Query for OK and NG; availability sends you to Status Duration for run time versus alarm time; performance is checked against rated output and running hours. Do not stop at the overall percentage.
<!-- /page-help -->

## 9.1 AI Q&A

<!-- page-help:assistant -->
AI Q&A on the sidebar is for questions about facts this board has already calculated. Opening the page carries the previous page, the selected device, the list of device names, and the time range already set in History Query. The previous page’s manual note and any notes History Query has already written are included. The device list is names only. The raw output table, addresses, and connection parameters are not sent.

Type one sentence and press Enter or Send. The first send prepares the model on this computer. The weights download into this user's folder and are not part of the installer. If the download drops, the next send continues from the bytes already saved. Until that finishes, the page says it is preparing.

An answer only explains the facts it was given. It does not acknowledge alarms, start or finish work orders, or release a recipe. When a number was not given, it should say the page does not have that number yet.
<!-- /page-help -->

## 10. Production Review: Trends and Summaries

![Review](screenshots/06_overview.png)

<!-- page-help:overview -->
The title is Production Review. The Range chip beside it means every number on this page is aggregated from history for the selected time range. That is not the same window as Home’s Shift, Order, or Live chips. The device list is the same device as Home. Changing it here changes Home as well. The whole page calculates that one device.

### Time range

- The page opens on the last 24 hours, not the current shift.
- The three shortcuts are Today, Last 24h, and Last 7 Days. The dropdown also has Current Shift, Previous Shift, Last 1h, and Last 8 Hours.
- After you change the range, click Refresh, or press F5. The clock at the right is the last update time. Export Report writes a CSV. Export PDF is for filing or printing. The PDF uses a Chinese font installed on this computer. If that font is missing, export fails with a message and does not leave a partial file.

### What each block shows

- Device Health Score, the current work order, and Current Recipe sit at the top. The recipe value comes from this device’s configuration now. It is not a recipe name stored on an old record.
- Review Conclusion is built from production, the device, and quality in this range. Under that, Cycle Comparison shows Current Total Output, Current Quality Rate, Current OEE, and the change against the previous period. The output trend marks the peak period and the valley period.
- Top 5 Alarms are the five names triggered most often in this range. With none, the block says No alarm records.
- On the Device Status Timeline, green is Running, red is Alarm, yellow is Standby, and gray is offline. Rest the pointer on a cell and the tip shows that cell’s state and its start and end. Device Health Score is written as “score / 100”. The number itself is not colored.

| Mark | Color |
| --- | --- |
| Timeline running | Green |
| Timeline alarm | Red |
| Timeline standby | Yellow |
| Timeline offline | Gray |
| Device health score | Not colored |

The output trend, the heatmap, the defect Pareto, and the OEE loss breakdown show time and value when you rest the pointer on them. Clicking them does not open a popup.

- Current Device Details stays collapsed until you open it. It shows this device’s output, run time, and OEE. Clicking the row opens Device Detail.
- Shift comparison lists output composition, alarm density, and target achievement for each shift. With no shift data it says No shift data and points you to Settings.
- OEE Loss Breakdown shows whether the loss is in performance, availability, or quality.
- Current Device Defect Pareto keeps at most 3 rows, ranked by the increase inside this time window. When a count exists before the window, the increase is the last value minus that baseline. Without a baseline, it is the last value in the window minus the first. A window that holds only one sample and has no baseline counts as 0, so that defect does not appear. An empty chart says No cumulative defects. Home’s Pareto uses defects added during the current shift. This chart uses the increase inside the selected window.
- Downtime Analysis shows Total Downtime and Average Alarm Duration. The output heatmap is this device’s output by hour. With no output it says No heatmap data, and the hint says the device was not running or no output was acquired.
<!-- /page-help -->

## 11. Work Orders

![Work Orders](screenshots/07_work_order.png)

<!-- page-help:work-orders -->
The left side is the order list. The right side is the output and the plan of the order you selected. The Current Production Progress card on Home only recognizes the one order that is in progress on that device. With no in-progress order, that Home card stays empty.

### How one order moves

- Add fills in the order number, product, target quantity, device, planned start, and planned end. After you save, the order is Pending.
- Start is available only for a Pending order. A device that already has an In Progress order cannot start a second one. The page says that device already has an order in progress.
- Complete is available only for an In Progress order. Completing stores the good count and the defect count as they were at that moment, then opens Work Order Completed. From there you can start a Pending order, create a blank order and start it, copy this one and start it, or choose Not now.
- Abort is available for Pending and In Progress, and it asks first. Aborting an In Progress order also stores the output so far. A Completed or Aborted order cannot be aborted again, and an aborted order cannot be put back In Progress.
- After Completed or Aborted, the list keeps the counts stored at that moment. Later acquisition does not change them.

### What a row means

- Search matches the order number, the product, or the device. The device list beside it keeps one device. The status chips are All, Pending, In Progress, Completed, and Aborted. The number on a chip is how many orders are in that state.
- A row shows the order number, the product, the achievement rate, and the status. Achievement = good count ÷ target quantity. The good count does not keep refreshing by itself. Click Refresh Output, or press F5, and the achievement rate is recalculated from the latest collection.

| Mark | Color |
| --- | --- |
| In Progress count | Theme color |
| Completed count | Green |
| Aborted count | Red |
| Achievement rate | Not colored. Met Only means the good count has reached the target quantity, which is 100%. It is not Home’s order-achievement rule, where 90% turns green |
- Conflict: Pending or In Progress orders on the same device have overlapping planned times. Adjust the plan before starting.
- Overdue: the planned end has passed and the order is not complete.
- Order Schedule Sort starts on Planned Start. It can also be Planned End, Status + Time, or Created. The same row can keep only Overdue Only, Met Only, or Has NG. Import adds many Pending orders at once. Generate Samples asks for a password and is only for a preview.
- With no orders, the list tells you to click Add. When a filter matches nothing it says No matching work order.
- With nothing selected, the right side says Select a work order. After you select one it shows mold / model, progress, OK / NG, estimated remaining, estimated completion, the device, created at, and the last status change.

### Do not mix this with shift output

An order’s good count adds up the production records linked to that order, and it can cross shifts. Shift OK on Home counts only the current shift, so the two numbers can differ. Use one spelling for the order number and the product. Delete removes the order. It does not remove production history. It asks before it deletes.
<!-- /page-help -->

## 12. System Settings: Data Source, PLC, Shifts, and Display

![System Settings](screenshots/08_settings.png)

<!-- page-help:settings -->
This page sets how the program connects, how it looks, and which shift the numbers use. It needs an administrator. After an edit, the bottom line changes from Saved to Unsaved Changes. Click Save Settings, or press Ctrl+S. Discard Changes throws away the draft that has not been saved.

### What save asks about

- Changing the PLC, the data source, or the run mode asks for confirmation. A new PLC IP or port disconnects the current link, and the next acquisition cycle reconnects with the new address. Test Connection tries the current IP and port once. It does not stop acquisition that is already running.
- Switching between Local Acquisition and Remote Acquisition takes effect only after a restart. After you save, the program asks whether to restart now. Remote mode needs the collector address. The default is http://127.0.0.1:5129/hubs/kanban . Test Collector Connection checks it first. Once remote is selected, the PLC Connection and Acquisition Strategy tabs are hidden, because the collector owns the link.
- Saving a shift change warns that the new configuration applies immediately. A shift that is already in progress can be treated as a changeover and the current totals can be cleared. At least one shift must remain.
- A language change waits for a restart. The three type sizes are on Display Settings, and they apply to this screen after you save.

### The five tabs

- General: Local Acquisition means this computer talks to the PLC. Remote Acquisition watches Kanban.Collector, so several screens share one set of numbers. Full Mode keeps the management pages. Viewer Mode keeps only Home, Production Line, Alarm Center, Device Detail, and Data Source Monitor, and leaving the program asks again. After you save, the sidebar opens or hides pages to match the new mode.
- Display Settings: dashboard title, language, type size, new-alarm sound, TV carousel, and automatic per-device daily report PDF. The languages are 简体中文, English, 日本語, and Português, and a language change waits for a restart. The three type sizes are Standard 100%, Large 115%, and Extra Large 130%. The carousel is on by default: Home 10 minutes, Production Line 100 seconds, Alarm Center 100 seconds, and it skips Alarm Center when there is no alarm. A click pauses it for 80 seconds. A High alarm holds the alarm page. Full mode can turn the carousel off. The daily report runs at the chosen time and writes the previous calendar day’s PDF for each device that has data, under Reports. On several screens, check Daily report master node on only one computer, so each computer does not write its own copy.
- PLC Connection: brand, IP, port, timeout, and that brand’s protocol parameters. While the port is still the previous brand’s default, changing the brand fills in the new default: Mitsubishi 4999, Siemens 102, Modbus TCP 502, Omron 9600, Keyence 5000. A port you already changed is kept.
- Acquisition Strategy: poll interval in milliseconds, history write interval in scan counts, home refresh interval in milliseconds, and batch reads. A shorter interval loads the PLC and this computer more. Batch reads usually stay as they are.
- Shift Config: name, start, and end. An overnight shift has its end on the next day. Home, alarms, and OEE “current shift” all follow these times. At least one shift must remain. A shift name already written on a history row stays as it was written if you rename the shift later.

### The license card

Machine code, license type, activation time, and expiry. During the trial the card shows the days remaining. An active license shows the type and the expiry. After expiry a new key is required. Copy Machine Code is what you send to the person who issues a key. Reactivate opens the window titled 软件激活; paste the key and click 激活. A key belongs to this computer. Do not use a key from another computer. When the program starts outside the trial and without an active license, the same window appears first. 取消 closes the program.

Restore Defaults only resets the draft to factory values. The bottom line becomes Unsaved Changes, and Save Settings is still required.
<!-- /page-help -->

## 14. Runtime Monitor

![Runtime Monitor](screenshots/09_runtime_monitoring.png)

<!-- page-help:runtime-monitor -->
This page shows whether acquisition is still running. It does not show output. Come here when the numbers on Home stay still. The page refreshes about once a second. To hold a number still, click Pause refresh, then Resume refresh when you have read it. F5, or Refresh at the top right, loads once immediately. Copy diagnostics and Export diagnostics take the current numbers with you. A screenshot drops the precision.

### The four numbers first

- PLC Connection: in local acquisition, Retry connection appears only while the PLC is disconnected. When this computer watches a remote collector, that button is hidden and the service reconnects on its own. If the service cannot be reached, this cell says Collector unreachable.
- System Health: Communication Lost when disconnected, Acquisition Stopped when the loop is not running, Running Normally when the last cycle succeeded, and “N consecutive failures” when the last cycle failed.
- Recent Acquisition Cycle: how long the latest cycle took.
- Cumulative Disconnects: how many times the link has dropped. It is not how long the current gap has lasted.

### The red acquisition banner

When Acquisition Abnormal appears, read the failure line under it. While still connected, the right side is the consecutive-failure count. While disconnected, the right side switches to how long the disconnect has lasted. A fresh disconnect often still shows 0 consecutive failures, so do not read that 0 as “nothing has failed”. If the refresh itself failed, an extra line says Refresh failed, and the numbers on the page have stopped.

### Further down, block by block

- Acquisition Loop: completed polls, average cycle, max cycle, and the configured poll interval. The actual cycle includes PLC read time and the wait. When the max stays above the configured interval, check the PLC response, the network, or whether there are too many addresses.
- Device Reads: if Last Success Time is still moving and Recent Successful Reads is above 0, this round still reached a device. A long stay at 0 means checking the PLC connection and whether addresses are filled in under Device Manager. Zero devices also counts toward the disconnect decision. Beside those numbers are the configured device count, points read, and the device that succeeded most recently.
- Acquisition quality: poll success rate shows N/A until one cycle has finished. That is not 0%. P95 cycle and P99 cycle are the slower end of the recent cycles.
- History Write Health: last successful write, write failures, and database storage. If Home still shows numbers while write failures keep rising, those numbers are not reaching History Query.
- System Resources and Data Consistency: CPU, memory, free disk, address conflicts, and invalid addresses.
- Per-Device Acquisition Status is the read result of each device. Poll Cycle Trend is the last 60 refreshes, in milliseconds.

When numbers stay still, do not delete the database. Check the PLC connection, the last success time, and history writes first.

### Colors

| Mark | Color |
| --- | --- |
| PLC connected | Green |
| PLC disconnected, and Collector unreachable | Red |
| System Health “Running Normally” | Green |
| System Health “Communication Lost” | Red |
| System Health “Acquisition Stopped”, and “N consecutive failures” | Yellow |
| The Acquisition Abnormal title, and Refresh failed | Red text on a light red background |
| Address conflicts and invalid addresses | Yellow when the count is not 0, ordinary text when it is 0 |
<!-- /page-help -->

## 15. Data Monitoring

![Data Monitoring](screenshots/10_data_monitoring.png)

<!-- page-help:data-source -->
This page only shows live samples from each device’s data sources. It does not edit them. Addresses, limits, and triggers are changed under Device Manager, on the data-source tab. The page refreshes about once a second. Refresh at the top right loads once immediately.

### The six numbers

Total values, Alarm, Read failed, Stale, Not sampled, and Normal. Those six numbers cover every data source. The device filter, the status filter, and search only change which rows you see.

### How to read a status

- Not sampled: no read has been attempted yet.
- Read failed: a read was attempted, and this value is invalid.
- Alarm: the current value has met this source’s alarm condition.
- Stale: a periodic point whose last success is more than 5 seconds old. A triggered point is not judged stale by those 5 seconds. While the point is still current, Freshness in the detail pane says Fresh.
- Normal: a value was read, it is not in alarm, and it is not stale.

| Status | Color |
| --- | --- |
| Normal | Green |
| Alarm | Red |
| Read failed | Red |
| Stale | Yellow |
| Not sampled | Gray |

Exceptions keeps only the rows that are not normal. Clear filters returns to everything. Search matches the device, source, value name, current value, and address.

### Three ways to look

- Table: one value per row. Columns are device, source, value, type, current value, unit, address, status, and last update.
- Compact cards: the same numbers as cards.
- Trend: numbers, on/off values, and enumerations can draw the recent change. A string has no curve. Read it in the table.

Select a row and the detail pane shows the device, source, current value, last valid value, status, freshness, last update, criteria, address, acquisition mode, trigger address, trigger value, and ack value. Last update is shown to the second. A point that has never been sampled says Not sampled. When a value stays still, see whether the row is Read failed or Not sampled, then check on Runtime Monitor whether the PLC is still connected.
<!-- /page-help -->

## 16. Recipes

![Recipes](screenshots/11_recipe_manager.png)

<!-- page-help:recipes -->
This page keeps the recipes that get written to a PLC. It needs an engineer or an administrator. The list is on the left and the editor is on the right. Apply writes the saved recipe. A draft still sitting in the editor is not written to the PLC.

### Save first, then apply

- On the right, fill in the recipe name, machine type, and remark, then each row’s parameter name, PLC address, type, value, minimum, maximum, and unit. Save needs at least one parameter row. Recipe names cannot repeat inside the same machine type. The validation column marks a problem on that row, and a failed check is not stored.
- Click Save Recipe. After a save, the search box and the machine-type filter are cleared so the recipe you just stored stays visible.
- Choose Target Device at the top, then click Apply to device. It asks again. In local acquisition the program writes the PLC itself and reads the values back. Cancel Apply is available while that write is running. When this computer watches a remote collector, the service does the write, this page cannot cancel it, and the wait is about 90 seconds.
- A blank machine type means the recipe is general and can be applied to any device. If the recipe names a machine type that does not match the target device, a warning appears first. Confirming still allows the apply.

### The list on the left

- The search box filters by recipe name or parameter name. The machine-type chips only change which recipes you see.
- New Recipe starts empty. Copy Recipe makes a new copy of the current one. Delete Recipe asks first. It removes the recipe. It does not clear the values already written in the PLC.
- Export Recipes and Import Recipes are the recipe file, for backup or for moving to another computer. They are not production history. Keep the recipe name aligned with the process documents on site.
<!-- /page-help -->

## 17. User Management

![User Management](screenshots/12_user_manager.png)

<!-- page-help:users -->
This page manages accounts. It needs an administrator. Pick a person on the left and edit them on the right. The columns are User, Role, Status, Last login, Security, and Actions. A username cannot be changed after it is created. The display name can. While the right side shows Unsaved changes, click Save Settings to store them. Ctrl+L opens the login window. That window only offers usernames already created here. It does not accept a typed name. Viewer Mode cannot switch users.

### The three roles

- Operator: Home, Production Line, Alarm Center, Work Orders, Review, History Query, Data Source Monitor, and Device Detail opened from Home.
- Engineer: those pages, plus Device Manager and Recipes.
- Administrator: those pages, plus Settings, Runtime Monitor, User Management, and Audit Log.

When the role is unclear, ask the shift leader or an administrator first.

### Limits when you edit an account

- You cannot change your own role, and you cannot disable yourself.
- You cannot disable or demote the last administrator who is still active. Deleting yourself is also refused. Deleting someone else asks first. It removes the login. It does not remove the production or alarm records that person was present for.
- Reset Password sets a new password for the selected person. Must change password on first login makes that person change it at the next login.
- Security reminders at the top of the list names only three cases: still using a default password, a passwordless account, or an account created more than 30 days ago that has never logged in. The reminder does not lock the account.

The role filter and search only change the list. Turning Active off means that person cannot log in.
<!-- /page-help -->

<!-- page-help:login -->
This window switches the current account. In full mode the program already logs in as the administrator when it starts, so this window is not required first. Press Ctrl+L to change person. Viewer mode does not open this window, and it cannot switch users.

### What to fill in

- The username can only be picked from the list. Those are accounts still marked Active in User Management. A new name cannot be typed. If someone is missing, the account is disabled or has not been created.
- The password belongs to that account. An account marked passwordless in User Management can leave the password empty.
- Click Log In, or press Enter in the password box. Cancel leaves the person who is logged in now unchanged.

### What happens next

- A wrong password shows “Invalid username or password, or account is disabled” underneath. A wrong password does not lock the account.
- If the account has Must change password on first login checked, a change-password window opens right after login. Closing that window cancels this login, and the current user in the title bar becomes empty. Press Ctrl+L again to log in.
- After a successful login, the sidebar hides or shows pages for the new role. If the new role cannot open the current page, the program returns to Home.
<!-- /page-help -->

## 18. Audit Log

![Audit Log](screenshots/13_audit.png)

<!-- page-help:audit -->
This page shows who did what, and when. It needs an administrator. It only reads the record. It does not change devices or production. Opening the page runs one query for the default range.

### Range and filters

- The default is the last 7 days. Today, Last 30 days, and This month are the other shortcuts. After you edit the start or end yourself, the query uses that custom range.
- Operator and Action narrow the list. Result can be All, Success, or Failed.
- Query goes back to page 1 and searches. Reset clears the operator and the action, sets the result back to All, and returns the time range to the last 7 days.
- Each page holds 100 rows. Previous and Next only turn pages inside the current query. The success count, the failure count, and the success rate cover every match, not only this page. With no rows, the success rate shows “—”. That is not 0%.

### Colors

| Mark | Color |
| --- | --- |
| The success count above, and Success in the result column | Green |
| The failure count above, and Failed in the result column | Red |

### What one row contains

Time, operator, action, target type, target, result, and detail. Select a row and Audit details on the right shows the value before the change and the value after it. Settings changes, logins, recipe applies, and user changes are kept here.

### Export

Export CSV and Export JSON export every match under the current filters, not only this page. The limit is 10,000 rows, and a larger result warns that it was cut off. JSON keeps the full before-and-after content for an archive.
<!-- /page-help -->

## 19. FAQ

### Q1: PLC always disconnected

Check cable/switch, ping PLC IP from Windows, **System Settings** IP/port, PLC allow list, Runtime Monitor and logs.

### Q2: Home output not increasing

Confirm production is running, PLC connected, OK/NG addresses correct, and recent successful device count. Saving a shift change while a shift is in progress can clear the current totals. There is no separate reset window.

### Q3: Home shows “No status data” or the device is offline, but PLC is green

PLC green means connection only—not per-device address correctness. Check that device’s addresses and confirm acquisition in Runtime Monitor.

### Q4: No alarm in app but device alarms on site

Verify the alarm point is added and saved, and that its address matches the PLC signal. Alarm Management has no separate enable switch.

### Q5: History query empty

Check time range, device, shift, and history write status. Recent data may not have been written yet.

### Q6: Settings don’t seem to apply

Confirm Save was clicked; reopen the page. Check write permissions and `.corrupt` backup files.

### Q7: App won’t start or exits immediately

Contact admin with: full error text, time, recent config changes, network/PLC changes, and logs under `%APPDATA%\Kanban` (or the custom data folder). Back up before deleting data.

### 19.1 Quick Diagnostic Table

| Symptom | Possible cause | Action |
| --- | --- | --- |
| Red banner “disconnected” at top | Network or PLC configuration | Check cable, IP/port, then Runtime Monitor |
| Yellow banner “data stale” | Acquisition stuck | Open Runtime Monitor and check the PLC connection and the acquisition loop. In remote mode, also check whether the collector can be reached |
| Home output not increasing | Wrong addresses, not producing, or a shift change saved during a shift cleared the totals | Verify OK/NG addresses. If a shift was just saved, check the current-shift totals |
| No alarm in app, alarm at site | Alarm point not added, or the address and name do not match the site | Check the address and name in Device Manager. After you save, that point is acquired |
| History query empty | No acquisition in range / wrong filter | Widen the range; check history write status |
| PDF export fails | Missing CJK fonts | Install a system CJK font and retry |
| Settings not applied after save | Save not clicked / no permission | Save again; check config folder write permissions |

> Detailed steps are in Q1–Q7 above.

## 20. Daily Shutdown

1. Confirm Reset All or a settings save is not in progress.
2. Update work order status per site procedure.
3. Close Kanban and wait for clean exit.
4. Back up `%APPDATA%\Kanban` (or the custom data folder) if required.

## 21. Three Rules for New Users

1. **Check PLC connection before judging data anomalies.**
2. **Confirm the address table before editing devices or sending writes.**
3. **Back up the data directory before deleting devices, changing shifts, or fixing corrupt config.**

---

## Appendix A: Data Backup (Administrators)

- Default data folder: `%APPDATA%\Kanban`; if the `KANBAN_DATA_DIR` environment variable is configured, that folder takes precedence.
- Back up the entire folder (Config, Logs, databases, etc.).
- Never delete `.db` or `.json` files without a backup.

## Appendix B: Engineers and Administrators

Content below is for engineers and IT—operators can skip it.

### B.1 Data Source CSV Import/Export

In Device Manager, select a device and open the **Data sources** tab to export or import that device’s CSV. Each row is a value item. Device Detail only shows the live values. It does not import there.

- **Replace** clears existing sources for that device; **Append** merges by name.
- Click **Save** after import.
- Always use an exported CSV as your template.

### B.2 Manual Languages

The app UI supports 简体中文, English, 日本語, and Português. The user manual is available in **Chinese** and **English**; F1 opens the English manual when the UI language is English, otherwise the Chinese manual (including for 日本語 and Português).

## Appendix C: Keyboard Shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+1`–`Ctrl+9` | Open the first nine sidebar pages this account can see. Keys past the visible list do nothing |
| `Ctrl+L` | Switch user (open the login dialog) |
| `F1` | Open the user manual |
| `Ctrl+Shift+K` | Open the shortcut list |
| `F11` | Toggle fullscreen |
| `F5` | Refresh Alarm Center, work-order output, Review, Device Detail, Data Monitoring, or Runtime Monitor |
| `Esc` | Go back (Device Detail page) |
| `Ctrl+Enter` | Run the query (History Query) |
| `Ctrl+R` | Reset filters (History Query) |
| `Ctrl+E` | Export results (History Query) |
| `Ctrl+S` | Save. Available in System Settings and Device Manager |
| `Ctrl+F` | Move focus to this page’s search box. Available in Alarm Center, Production Line, Device Manager, and Work Orders |

> This table covers the main window and common pages; some pages have additional local shortcuts described in their chapters. Whether a shortcut works depends on the current page.
