# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

LCPSAutomate is a WPF (.NET 8, Windows-only) desktop tool that bridges a barcode-scanner log directory and a third-party Windows application called **HandyClient**. It tails `*.txt` log files produced by the scanner, extracts QR payloads from log lines, and submits them to HandyClient's UI via UI Automation (FlaUI / UIA3). Successfully submitted QRs are persisted to a local SQLite DB so the tool is resumable across restarts.

The target framework is `net8.0-windows10.0.22621.0` with WPF enabled — Linux/macOS builds are not supported.

## Common commands

```bash
# Restore + build
dotnet build LCPSAutomate.csproj

# Run the WPF app (must run on Windows with HandyClient installed)
dotnet run --project LCPSAutomate.csproj

# Release publish (self-contained single-file is not configured; default is framework-dependent)
dotnet publish LCPSAutomate.csproj -c Release
```

There is no test project in the solution. `LogWriterTest.cs` is **not** a unit test — it is a runtime helper invoked from `MainWindow.StartButton_Click` that fabricates sample scanner logs into the watched folder for manual smoke-testing.

## Architecture

The flow is a single producer–consumer pipeline orchestrated by `Automate`:

```
FileSystemWatcher (*.txt in user-chosen folder)
   └─ OnFileChanged → HandleFileChangeAsync
        └─ reads from last-known offset (FileShare.ReadWrite, persisted in SQLite)
             └─ enqueues raw new content into ConcurrentQueue<string> _queue

ProcessNewContent loop (background Task)
   └─ ExtractQR(content)   // regex: <QR読込>:\s(.{150})\[CR\]
        └─ Submit(qrList)
             └─ UIA3Automation → finds HandyClient window → finds TextBox by AutomationId
                  └─ sets text, presses ENTER, scans for 错误/警告 modal dialogs, clicks 确定 to dismiss
                       └─ AddOrIgnoreRecord + MarkRecordProcessedByQr in SQLite
```

Key cross-file invariants:

- **HandyClient UI binding** lives entirely in `FlaUIUitls.cs` as `static readonly` constants (`TARGET_WINDOW_TITLE = "HandyClient"`, `TARGET_PANEL_AUTOMATION_ID = "Form_Main_New_3_5"`, `TARGET_TEXT_BOX_AUTOMATION_ID = "txb_prodBatch"`). If the target app changes its window title or automation IDs, edit these constants — they are the only place those strings are defined and are consumed by both `FlaUIUitls.DetectWindow()` and `Automate.Submit()`.
- **Readiness gating**: `MainWindow.MonitorWindowLoopAsync` polls `FlaUIUitls.DetectWindow()` every second. If HandyClient disappears mid-run it calls `_automate?.Stop()`. Any new feature that touches HandyClient must remain robust to the window vanishing.
- **Resume semantics**: `Automate.Start()` rehydrates `_readRecors` from the `FileReadRecord` table before starting the watcher. After every successful enqueue, the new file offset is upserted via `SqliteDataAccess.UpsertFileReadRecordAsync`. A file whose length shrinks below the recorded offset is treated as rewritten and restarts from 0. Do not change offset-tracking semantics without also handling existing rows in the DB.
- **QR de-dup**: the `Records` table uses `Qr TEXT PRIMARY KEY` and inserts go through `INSERT OR IGNORE`, so the same QR scanned twice is never re-submitted to HandyClient logic-wise — but it *is* re-set in the textbox by `Submit`. Dedup happens at persistence, not at the UI step.
- **DB lifecycle**: `SqliteDataAccess` constructor synchronously runs `EnsureDatabaseAsync` (CREATE TABLE IF NOT EXISTS for `Records` and `FileReadRecord`). `Automate` holds one long-lived `_db` instance; `Submit` falls back to a short-lived one if `_db` is null. The default DB path is the relative file `lcpsautomate.db` next to the running executable.

## Things to watch out for

- The regex `<QR読込>:\s(.{150})\[CR\]` is locale-specific (Japanese `読込`) and hard-codes a 150-character payload. Changing scanner log format requires updating `Automate.ExtractQR`.
- `Submit` interacts with real UI: `tb.Text = qr.Trim()` then `Keyboard.Press(ENTER)`. Delays (`Task.Delay(200)`) are tuned for the live app — shortening them tends to cause dropped submissions.
- Error/warning modal handling only matches Chinese titles (`"错误"`, `"警告"`) and a `"确定"` button. If localization changes, update `Automate.Submit` accordingly.
- `MainWindow.StartButton_Click` currently kicks off `LogWriterTest` to write 10 synthetic log files into the watched folder. This is debug scaffolding — it will pollute a real production folder. Remove or guard it before shipping.
- Logging goes through NLog with config in `nlog.config`; logs are written to `${basedir}/logs/${shortdate}.log` (and `…_error.log`). `${basedir}` is the app's working directory, not the watched folder.
