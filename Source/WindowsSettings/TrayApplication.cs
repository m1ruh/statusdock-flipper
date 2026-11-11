using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Forms = System.Windows.Forms;

namespace StatusDockPreview
{
    internal sealed class TrayApplication : Forms.ApplicationContext
    {
        internal const string QuitSettingsEvent = @"Local\StatusDock-QuitSettings";
        internal const string StopSenderEvent = @"Local\StatusDock-Stop";

        private readonly Process sender;
        private readonly EventWaitHandle pauseRequested;
        private readonly EventWaitHandle paused;
        private readonly string liveSamplePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, LiveSample.FileName);
        private readonly Forms.NotifyIcon tray = new Forms.NotifyIcon();
        private readonly Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
        private readonly Forms.ToolStripMenuItem status = new Forms.ToolStripMenuItem { Enabled = false };
        private readonly Forms.ToolStripMenuItem pause = new Forms.ToolStripMenuItem();
        private readonly Forms.Timer timer = new Forms.Timer { Interval = 250 };
        private readonly Icon liveIcon = CreateIcon(Color.FromArgb(44, 187, 114));
        private readonly Icon disconnectedIcon = CreateIcon(Color.FromArgb(166, 175, 187));
        private readonly Icon pausedIcon = CreateIcon(Color.FromArgb(247, 172, 62));

        public static void Run(int senderId)
        {
            try
            {
                using (var sender = Process.GetProcessById(senderId))
                using (var request = EventWaitHandle.OpenExisting(@"Local\StatusDock-Pause-" + senderId))
                using (var paused = EventWaitHandle.OpenExisting(@"Local\StatusDock-Paused-" + senderId))
                using (var stop = EventWaitHandle.OpenExisting(StopSenderEvent))
                using (var quit = new EventWaitHandle(false, EventResetMode.ManualReset, QuitSettingsEvent))
                using (var context = new TrayApplication(sender, request, paused, stop, quit))
                    Forms.Application.Run(context);
            }
            catch (ArgumentException) { } // The sender exited before the tray started.
            catch (WaitHandleCannotBeOpenedException) { }
        }

        private TrayApplication(Process sender, EventWaitHandle pauseRequested, EventWaitHandle paused,
            EventWaitHandle stopRequested, EventWaitHandle quitSettings)
        {
            this.sender = sender;
            this.pauseRequested = pauseRequested;
            this.paused = paused;

            menu.Items.Add(status);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Open Settings", null, (s, e) => OpenSettings());
            menu.Items.Add(pause);
            pause.Click += (s, e) =>
            {
                if (pauseRequested.WaitOne(0)) pauseRequested.Reset();
                else pauseRequested.Set();
                RefreshStatus();
            };
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit StatusDock", null, (s, e) =>
            {
                menu.Enabled = false;
                quitSettings.Set();
                stopRequested.Set();
            });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => OpenSettings();
            timer.Tick += (s, e) =>
            {
                if (sender.HasExited) ExitThread();
                else RefreshStatus();
            };
            RefreshStatus();
            tray.Visible = true;
            timer.Start();
        }

        private void RefreshStatus()
        {
            pause.Text = pauseRequested.WaitOne(0) ? "Resume" : "Pause";
            bool portReleased = paused.WaitOne(0);
            var sample = portReleased ? null : LiveSample.Read(liveSamplePath);
            bool live = sample != null && sample.IsFreshAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            string state = portReleased ? "Paused" : live ? "Live" : "Disconnected";
            if (status.Text == state) return;
            status.Text = state;
            tray.Text = "StatusDock · " + state;
            tray.Icon = portReleased ? pausedIcon : live ? liveIcon : disconnectedIcon;
        }

        private static void OpenSettings()
        {
            Process.Start(Forms.Application.ExecutablePath);
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private static Icon CreateIcon(Color color)
        {
            using (var bitmap = new Bitmap(16, 16))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var gauge = Icon.ExtractAssociatedIcon(Forms.Application.ExecutablePath))
            using (var indicator = new SolidBrush(color))
            {
                graphics.DrawIcon(gauge, new Rectangle(0, 0, 16, 16));
                graphics.FillEllipse(indicator, 9, 8, 7, 7);
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                tray.Dispose();
                menu.Dispose();
                liveIcon.Dispose();
                disconnectedIcon.Dispose();
                pausedIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
