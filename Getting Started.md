# Getting started with StatusDock

## Start and stop

1. Connect the Flipper over USB and return to its home screen. Close qFlipper and Flipper Lab to release the serial port.
2. Open **Windows → StatusDock.cmd** and choose **1** to start. The Windows sender opens StatusDock on the Flipper and sends the current time and measurements.
3. Choose **2** to start the sender at Windows sign-in. If you move the StatusDock folder, run option **2** again from the new location.
4. Open **Windows → StatusDock Settings.exe**. Select **PC Stats** or **Large Clock**, and use **Show seconds** to control the clock. The Flipper applies the change on the next normal update.

StatusDock saves your choices in `Windows/StatusDock Settings.json`.

At your next Windows sign-in, connect the Flipper and open **StatusDock Settings**. Check that the Flipper shows your saved display, the preview says **Live**, and the clock or measurements update without starting the sender manually.

Press **Back** on the Flipper to leave the display. Open **StatusDock** from **Apps → Tools** to resume. Choose **3** in the Windows launcher to stop the sender and release the port for qFlipper, or **4** to remove automatic startup.

## When the PC shuts down

If the USB port cuts power, the Flipper shuts down after about eight seconds and starts again when USB power returns. If the port keeps supplying power, the display goes blank after 15 seconds without data and wakes when Windows sends another sample.

## Install the Flipper app

Use a build for **Momentum mntm-012**. Build `Source/dist/statusdock.fap` from source or use `Flipper/statusdock.fap` from a packaged copy. Stop the Windows sender with **3**, exit StatusDock on the Flipper, and back up the installed `statusdock.fap` if present. In qFlipper, copy the new build to **SD Card → apps → Tools** as `statusdock.fap`. Close qFlipper and start the Windows sender with **1**.

See the [README](README.md) for build instructions.
