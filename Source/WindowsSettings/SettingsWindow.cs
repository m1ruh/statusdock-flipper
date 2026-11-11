using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Navigation;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;

namespace StatusDockPreview
{
    public partial class SettingsWindow : Window
    {
        private const int DwmCaptionColor = 35;
        private const int DwmTextColor = 36;
        private const double SideBySideWidth = 816;
        private const double ColumnGap = 24;
        private const double PreviewPadding = 24;

        private static readonly LiveSample Example = new LiveSample
        {
            ClockQuarters = (10 * 3600 + 42 * 60 + 36) * LiveSample.QuartersPerSecond,
            Cpu = 28,
            Ram = 63,
            Gpu = 41,
            CpuTemp = 57,
            GpuTemp = 64
        };
        private readonly string settingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "StatusDock Settings.json");
        private readonly string liveSamplePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, LiveSample.FileName);
        private readonly string connectionPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, ConnectionStatus.FileName);
        private readonly Stopwatch exampleClock = Stopwatch.StartNew();
        private readonly DispatcherTimer previewTimer = new DispatcherTimer();
        private readonly EventWaitHandle quitRequested =
            new EventWaitHandle(false, EventResetMode.ManualReset, TrayApplication.QuitSettingsEvent);
        private long? drawnSampleUtcMs;
        private int drawnClockSeconds = -1;
        private bool ready;
        private StatsLayout layout;

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--start-sender")
                return StartSender(args);
            if (args.Length > 0 && args[0] == "--tray")
            {
                int senderId;
                if (args.Length == 2 && int.TryParse(args[1], out senderId) && senderId > 0)
                    TrayApplication.Run(senderId);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--open-and-start")
                EnsureSenderStarted();
            return new Application().Run(new SettingsWindow());
        }

        private static void EnsureSenderStarted()
        {
            EventWaitHandle stop;
            if (EventWaitHandle.TryOpenExisting(TrayApplication.StopSenderEvent, out stop))
            {
                using (stop)
                    if (!stop.WaitOne(0)) return;
            }

            try
            {
                using (Process.Start(new ProcessStartInfo(
                    typeof(SettingsWindow).Assembly.Location, "--start-sender")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                })) { }
            }
            catch (Win32Exception) { } // Settings still works when the sender cannot start.
        }

        private static int StartSender(string[] args)
        {
            if (args.Length > 2 || (args.Length == 2 &&
                !Regex.IsMatch(args[1], @"\ACOM[0-9]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                return 2;

            string folder = AppDomain.CurrentDomain.BaseDirectory;
            string script = Path.Combine(folder, "StatusDock.ps1");
            if (!File.Exists(script)) return 2;

            string systemDirectory = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
                ? "Sysnative" : "System32";
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                systemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"";
            if (args.Length == 2) arguments += " -Port " + args[1];

            try
            {
                using (var sender = Process.Start(new ProcessStartInfo(powershell, arguments)
                {
                    WorkingDirectory = folder,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    if (sender == null) return 1;
                    sender.WaitForExit();
                    return sender.ExitCode;
                }
            }
            catch (Win32Exception) { return 1; }
        }

        public SettingsWindow()
        {
            InitializeComponent();
            Height = Math.Min(Height, SystemParameters.WorkArea.Height);
            Width = Math.Min(Width, SystemParameters.WorkArea.Width);

            var settings = LoadSettings(true) ?? new PreviewSettings();
            ClockChoice.IsChecked = settings.Display == "clock";
            StatsChoice.IsChecked = !ClockChoice.IsChecked;
            SecondsChoice.IsChecked = settings.ShowSeconds ?? true;
            InvertChoice.IsChecked = settings.InvertDisplay ?? false;
            FlipChoice.IsChecked = settings.FlipDisplay;
            LightThemeChoice.IsChecked = settings.Theme == "light";
            DarkThemeChoice.IsChecked = !LightThemeChoice.IsChecked;
            layout = new StatsLayout(settings.PcStatsLayout);
            UpdateLayoutControls();
            ApplyTheme();
            SourceInitialized += (sender, e) => ApplyCaptionTheme();
            RefreshPreview(true);
            ready = true;

            previewTimer.Interval = TimeSpan.FromMilliseconds(250);
            previewTimer.Tick += (sender, e) =>
            {
                if (quitRequested.WaitOne(0)) Close();
                else RefreshPreview(RefreshSavedView());
            };
            Closed += (sender, e) =>
            {
                previewTimer.Stop();
                quitRequested.Dispose();
            };
            previewTimer.Start();
        }

        private void OnChoiceChanged(object sender, RoutedEventArgs e)
        {
            if (!ready) return;
            if (sender == SecondsChoice)
                SaveCurrentSettings(settings => settings.ShowSeconds = SecondsChoice.IsChecked == true);
            else if (sender == InvertChoice)
                SaveCurrentSettings(settings => settings.InvertDisplay = InvertChoice.IsChecked == true);
            else if (sender == FlipChoice)
                SaveCurrentSettings(settings => settings.FlipDisplay = FlipChoice.IsChecked == true);
            else
                SaveCurrentSettings(settings => settings.Display = sender == ClockChoice ? "clock" : "stats");
            RefreshPreview(true);
        }

        private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var size = e.NewSize;
            bool sideBySide = size.Width >= SideBySideWidth;
            ControlsColumn.Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            PreviewRow.Height = sideBySide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            ControlsRow.Height = sideBySide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(ControlsScroll, sideBySide ? 1 : 0);
            Grid.SetRow(ControlsScroll, sideBySide ? 0 : 1);
            PreviewPane.Margin = sideBySide ? new Thickness(0, 0, ColumnGap, 0) : new Thickness(0, 0, 0, 17);

            // On a short window, keep room below the preview for the stacked controls.
            double availableWidth = sideBySide ? size.Width / 2 - ColumnGap : size.Width;
            double heightLimit = sideBySide ? size.Height - 44 : (size.Height - 62) * 0.4;
            double width = Math.Min(512, Math.Min(availableWidth - PreviewPadding, heightLimit * 2));
            PreviewImage.Width = Math.Max(1, width);
            PreviewImage.Height = PreviewImage.Width / 2;
        }

        private void OnThemeChanged(object sender, RoutedEventArgs e)
        {
            if (!ready) return;
            ApplyTheme();
            SaveCurrentSettings(settings => settings.Theme = LightThemeChoice.IsChecked == true ? "light" : "dark");
        }

        private void OnLayoutChoice(object sender, RoutedEventArgs e)
        {
            if (!ready) return;
            layout.Alignment = AlignLeft.IsChecked == true ? ClockAlignment.Left :
                AlignRight.IsChecked == true ? ClockAlignment.Right : ClockAlignment.Center;
            layout.NumbersOnly = NumbersOnly.IsChecked == true;
            SaveLayout();
        }

        private void OnRowUp(object sender, RoutedEventArgs e) { MoveRow((Button)sender, -1); }
        private void OnRowDown(object sender, RoutedEventArgs e) { MoveRow((Button)sender, 1); }

        private void MoveRow(Button button, int offset)
        {
            int index = int.Parse((string)button.Tag, CultureInfo.InvariantCulture);
            layout.MoveRow(index, offset);
            UpdateLayoutControls();
            SaveLayout();
        }

        private void OnResetLayout(object sender, RoutedEventArgs e)
        {
            layout = new StatsLayout(0);
            UpdateLayoutControls();
            SaveLayout();
        }

        private void UpdateLayoutControls()
        {
            bool wasReady = ready;
            ready = false;
            try
            {
                AlignLeft.IsChecked = layout.Alignment == ClockAlignment.Left;
                AlignCenter.IsChecked = layout.Alignment == ClockAlignment.Center;
                AlignRight.IsChecked = layout.Alignment == ClockAlignment.Right;
                NumbersOnly.IsChecked = layout.NumbersOnly;
                ShowBars.IsChecked = !layout.NumbersOnly;
                var labels = new[] { Row0Label, Row1Label, Row2Label };
                var up = new[] { Row0Up, Row1Up, Row2Up };
                var down = new[] { Row0Down, Row1Down, Row2Down };
                for (int index = 0; index < labels.Length; index++)
                {
                    string metric = layout.RowAt(index).ToString().ToUpperInvariant();
                    labels[index].Text = metric;
                    AutomationProperties.SetName(up[index], "Move " + metric + " up");
                    AutomationProperties.SetName(down[index], "Move " + metric + " down");
                    up[index].IsEnabled = index > 0;
                    down[index].IsEnabled = index < labels.Length - 1;
                }
            }
            finally { ready = wasReady; }
        }

        private void SaveLayout()
        {
            SaveCurrentSettings(settings => settings.PcStatsLayout = layout.Packed);
            RefreshPreview(true);
        }

        private void ApplyTheme()
        {
            string name = LightThemeChoice.IsChecked == true ? "LightTheme" : "DarkTheme";
            var theme = (ResourceDictionary)Resources[name];
            var merged = Resources.MergedDictionaries;
            if (merged.Count == 0) merged.Add(theme);
            else merged[0] = theme;
            ApplyCaptionTheme();
        }

        private void ApplyCaptionTheme()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            SetCaptionColor(handle, DwmCaptionColor, ((SolidColorBrush)Resources["WindowBackground"]).Color);
            SetCaptionColor(handle, DwmTextColor, ((SolidColorBrush)Resources["MainText"]).Color);
        }

        private static void SetCaptionColor(IntPtr handle, int attribute, Color color)
        {
            // Windows 11 supports custom caption colors. Earlier versions keep the system caption.
            int colorRef = color.R | color.G << 8 | color.B << 16;
            DwmSetWindowAttribute(handle, attribute, ref colorRef, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

        private void SaveCurrentSettings(Action<PreviewSettings> change)
        {
            try
            {
                // The sender may save an OK-button selection while Settings is open.
                using (var mutex = new Mutex(false, @"Local\StatusDock-Preferences"))
                {
                    bool locked;
                    try { locked = mutex.WaitOne(500); }
                    catch (AbandonedMutexException) { locked = true; }
                    if (!locked) throw new IOException("The preferences file is busy.");
                    try
                    {
                        var settings = LoadSettings() ?? new PreviewSettings();
                        settings.Display = settings.Display == "clock" ? "clock" : "stats";
                        settings.ShowSeconds = settings.ShowSeconds ?? true;
                        change(settings);
                        SaveSettings(settings);
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                SaveStatus.Text = "Settings saved.";
            }
            catch (IOException)
            {
                ShowSaveError();
            }
            catch (UnauthorizedAccessException)
            {
                ShowSaveError();
            }
        }

        private bool RefreshSavedView()
        {
            var settings = LoadSettings();
            if (settings == null || (settings.Display != "clock" && settings.Display != "stats"))
                return false;
            bool clock = settings.Display == "clock";
            if (ClockChoice.IsChecked == clock) return false;

            ready = false;
            try
            {
                ClockChoice.IsChecked = clock;
                StatsChoice.IsChecked = !clock;
            }
            finally { ready = true; }
            return true;
        }

        private void RefreshPreview(bool force = false)
        {
            var sample = LiveSample.Read(liveSamplePath);
            long utcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (sample != null && !sample.IsFreshAt(utcMs)) sample = null;

            bool isLive = sample != null;
            RefreshConnection(isLive);
            bool clockOnly = ClockChoice.IsChecked == true;
            LayoutSection.Visibility = clockOnly ? Visibility.Collapsed : Visibility.Visible;
            bool showSeconds = SecondsChoice.IsChecked == true;
            bool invertDisplay = InvertChoice.IsChecked == true;
            bool flipDisplay = FlipChoice.IsChecked == true;
            var displayedSample = sample ?? Example;
            long elapsedMs = isLive
                ? (showSeconds ? utcMs - sample.SampledAtUtcMs : 0)
                : exampleClock.ElapsedMilliseconds;
            long quarters = displayedSample.ClockQuarters + elapsedMs / 250;
            int clockSeconds = (int)(quarters / LiveSample.QuartersPerSecond % LiveSample.SecondsPerDay);
            if (!showSeconds) clockSeconds -= clockSeconds % 60;

            long? sampleUtcMs = isLive ? (long?)sample.SampledAtUtcMs : null;
            if (!force && drawnSampleUtcMs == sampleUtcMs && drawnClockSeconds == clockSeconds)
                return;
            drawnSampleUtcMs = sampleUtcMs;
            drawnClockSeconds = clockSeconds;

            DataLabel.Text = isLive ? "Live" : "Sample data";
            NoLiveLabel.Visibility = isLive ? Visibility.Collapsed : Visibility.Visible;
            PreviewImage.Source = FlipperPreview.Render(
                displayedSample, clockSeconds, clockOnly, showSeconds, invertDisplay, flipDisplay, layout);
            AutomationProperties.SetHelpText(PreviewImage,
                (clockOnly ? "Large Clock" : "PC Stats") +
                (showSeconds ? " with seconds. " : " without seconds. ") +
                (invertDisplay ? "Inverted display colours. " : "") +
                (flipDisplay ? "Display rotated 180 degrees. " : "") +
                (isLive ? "Live." : "No live data. Showing sample data."));
        }

        private void RefreshConnection(bool isLive)
        {
            var connection = SnapshotFile.Read<ConnectionStatus>(connectionPath);
            var firmware = connection == null ? null : connection.Firmware;
            string firmwareName = firmware == null ? "" : firmware.DisplayName;
            FirmwareText.Text = string.IsNullOrEmpty(firmwareName)
                ? "Firmware version unavailable"
                : firmwareName + (string.IsNullOrEmpty(firmware.Api) ? "" : " (API " + firmware.Api + ")");
            ConnectionText.Text = connection == null
                ? (isLive ? "Live readings received." : "Waiting for the Windows sender.")
                : (connection.State == "Live" && !isLive ? "Waiting for fresh readings." : connection.Message);

            var issue = connection == null ? null : connection.Issue;
            bool mismatch = issue != null && issue.Kind == "ApiMismatch";
            string download = firmware == null ? null : firmware.DownloadUrl;
            bool untested = firmware != null && !string.IsNullOrWhiteSpace(firmware.Version) && download == null;
            string issueMessage = null;
            if (mismatch)
                issueMessage = "Wrong StatusDock build" +
                    (string.IsNullOrEmpty(firmwareName) ? "" : " for " + firmwareName) + " (API mismatch).";
            else if (issue != null && issue.Kind == "MissingApp")
                issueMessage = "StatusDock app missing. Copy statusdock.fap to SD Card → apps → Tools.";
            else if (issue != null && issue.Kind == "AppBusy")
                issueMessage = "The Flipper is busy opening or running another app.";
            else if (issue != null)
                issueMessage = "Could not open StatusDock on the Flipper.";
            else if (untested)
                issueMessage = "StatusDock compatibility is untested for this firmware.";
            IssueText.Text = issueMessage;
            IssueText.Visibility = issueMessage == null ? Visibility.Collapsed : Visibility.Visible;
            IssueDetail.Text = issue == null ? null : issue.Detail;
            IssueDetail.Visibility = string.IsNullOrEmpty(IssueDetail.Text) ? Visibility.Collapsed : Visibility.Visible;

            string url = mismatch || untested ? download ?? FirmwareInfo.ReleaseNotes : null;
            ConnectionLink.NavigateUri = url == null ? null : new Uri(url);
            ConnectionLinkText.Text = download != null && mismatch ? "Download the matching StatusDock 1.2 ZIP" : "Read the StatusDock 1.2 release notes";
            ConnectionLinkRow.Visibility = url == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnConnectionLink(object sender, RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Win32Exception)
            {
                ConnectionLinkText.Text = e.Uri.AbsoluteUri;
            }
            e.Handled = true;
        }

        private PreviewSettings LoadSettings(bool reportError = false)
        {
            try
            {
                using (var file = File.Open(settingsPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                    return (PreviewSettings)new DataContractJsonSerializer(typeof(PreviewSettings))
                        .ReadObject(file) ?? new PreviewSettings();
            }
            catch (FileNotFoundException) { return new PreviewSettings(); }
            catch (IOException) { return UnavailableSettings(reportError); }
            catch (UnauthorizedAccessException) { return UnavailableSettings(reportError); }
            catch (SerializationException) { return UnavailableSettings(reportError); }
            catch (XmlException) { return UnavailableSettings(reportError); }
        }

        private PreviewSettings UnavailableSettings(bool reportError)
        {
            if (reportError) SaveStatus.Text = "Could not read saved settings. Using defaults.";
            return null;
        }

        private void SaveSettings(PreviewSettings settings)
        {
            // Write beside the destination so a failed write cannot leave a partial settings file.
            string temporaryPath = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = File.Create(temporaryPath))
                    new DataContractJsonSerializer(typeof(PreviewSettings)).WriteObject(file, settings);

                if (File.Exists(settingsPath))
                    File.Replace(temporaryPath, settingsPath, null);
                else
                    File.Move(temporaryPath, settingsPath);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        private void ShowSaveError()
        {
            SaveStatus.Text = "Could not save settings. Check write access to this folder.";
        }
    }

    [DataContract]
    internal sealed class PreviewSettings
    {
        [DataMember(Name = "display")]
        public string Display;

        [DataMember(Name = "showSeconds")]
        public bool? ShowSeconds;

        [DataMember(Name = "theme")]
        public string Theme;

        [DataMember(Name = "invertDisplay")]
        public bool? InvertDisplay;

        [DataMember(Name = "flipDisplay", EmitDefaultValue = false)]
        public bool FlipDisplay;

        [DataMember(Name = "pcStatsLayout", EmitDefaultValue = false)]
        public int PcStatsLayout;
    }
}
