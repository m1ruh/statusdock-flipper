# StatusDock

StatusDock turns a Flipper Zero into a small USB display for Windows. It shows the time, CPU and RAM usage, and NVIDIA GPU usage and temperature. It can also show CPU temperature when Libre Hardware Monitor is running and can read the sensor.

## Project files

- `Source/` contains the Flipper source and uFBT manifest.
- `Source/WindowsSettings/` contains the source for the Windows settings window.
- `Windows/` contains the launcher and the sender that owns the USB connection.
- `Flipper/` holds local builds. In an installed copy, `Flipper/statusdock.fap` is the packaged Flipper app.

Git tracks the source and documentation. Preferences, live snapshots, logs, rollback copies and build outputs are local files.

## StatusDock Settings

Open **Windows → StatusDock Settings.exe** and choose **PC Stats** or **Large Clock**. Use **Show seconds** to control the clock. The 128 × 64 pixel preview reflects your choices, and the sender rereads them on each update. The window and sender share `Windows/StatusDock Settings.json`. If the sender cannot read valid preferences, it uses **PC Stats** with seconds.

The preview says **Live** after the Flipper accepts fresh measurements. The clock keeps ticking between samples. If the feed stops, the preview says **No live data** and shows labeled sample data until the feed resumes.

**Windows theme** switches the settings window between **Dark** and **Light**. Dark is the default. The choice is saved alongside the display settings.

**Invert Flipper display** reverses the preview and the live Flipper display in either view. It is off by default and takes effect on the Flipper with the next live update.

See [Getting Started](Getting%20Started.md) for installation and sign-in checks.

## Build

- With the .NET Framework 4.8 Developer Pack installed, run `MSBuild "Source\WindowsSettings\StatusDockPreview.csproj" /p:Configuration=Release`. The build produces `Windows/StatusDock Settings.exe`.
- Run `ufbt` in `Source` with the Momentum mntm-012 SDK (API 87.1). The build produces `Source/dist/statusdock.fap`.

## Future work

Add a system tray icon, a USB connection indicator and controls for choosing and ordering measurements. A pause control could release USB for qFlipper. Other ideas include switching views with the Flipper's OK button, a focus timer with optional vibration and a temperature alert.
