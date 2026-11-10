# Getting started with StatusDock

## Start and stop

1. Connect the Flipper over USB and return to its home screen. Close qFlipper and Flipper Lab to release the serial port.
2. Open **Windows → StatusDock.cmd** and choose **1** to start. The Windows sender opens StatusDock on the Flipper and sends the current time and measurements.
3. Choose **2** to install the **StatusDock** task in Windows Task Scheduler. It starts the sender when you sign in. If you move the StatusDock folder, run option **2** again from the new location.
4. Open **Windows → StatusDock Settings.exe**. Select **PC Stats** or **Large Clock**, and use **Show seconds** to control the clock. The Flipper applies the change on the next normal update.

StatusDock saves your choices in `Windows/StatusDock Settings.json`.

While live data is showing, briefly press **OK** on the Flipper to switch between **PC Stats** and **Large Clock**. The Windows sender saves that view even when Settings is closed. Changing the view in **StatusDock Settings** also updates the Flipper.

To check the task now, stop the sender with option **3**, return the connected Flipper to its home screen, then select **Run** for **StatusDock** in Task Scheduler. The sender opens StatusDock on the Flipper and the Settings preview shows **Live**. The same task runs at your next Windows sign-in.

Press **Back** on the Flipper to leave the display. Open **StatusDock** from **Apps → Tools** to resume. Choose **3** in the Windows launcher to stop the sender. Choose **4** to remove the sign-in task.

## Use qFlipper alongside StatusDock

The sender starts the StatusDock icon in the Windows notification area. Its menu shows **Live**, **Disconnected** or **Paused** and includes **Open Settings**.

Choose **Pause** and wait for **Paused** before opening qFlipper. StatusDock releases the USB port at that point. Choose **Resume** to restore the feed. If qFlipper still owns USB, StatusDock shows **Disconnected** and keeps trying until qFlipper releases it.

## When the PC shuts down

If the USB port cuts power, the Flipper shuts down after about eight seconds and starts again when USB power returns. If the port keeps supplying power, the display goes blank after 15 seconds without data and wakes when Windows sends another sample.

## Install the Flipper app

Use a build for **Momentum mntm-012**. Build `Source/dist/statusdock.fap` from source or use `Flipper/statusdock.fap` from a packaged copy. Stop the Windows sender with **3**, exit StatusDock on the Flipper, and back up the installed `statusdock.fap` if present. In qFlipper, copy the new build to **SD Card → apps → Tools** as `statusdock.fap`. Close qFlipper and start the Windows sender with **1**.

See the [README](README.md) for build instructions.
