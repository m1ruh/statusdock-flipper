using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;

namespace StatusDockPreview
{
    public partial class SettingsWindow : Window
    {
        private const int DwmCaptionColor = 35;
        private const int DwmTextColor = 36;

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
        private readonly Stopwatch exampleClock = Stopwatch.StartNew();
        private readonly DispatcherTimer previewTimer = new DispatcherTimer();
        private long? drawnSampleUtcMs;
        private int drawnClockSeconds = -1;
        private bool ready;
        private StatsLayout layout;

        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--tray")
            {
                int senderId;
                if (args.Length == 2 && int.TryParse(args[1], out senderId) && senderId > 0)
                    TrayApplication.Run(senderId);
                return;
            }
            new Application().Run(new SettingsWindow());
        }

        public SettingsWindow()
        {
            InitializeComponent();
            MaxHeight = SystemParameters.WorkArea.Height;
            MaxWidth = SystemParameters.WorkArea.Width;

            var settings = LoadSettings(true) ?? new PreviewSettings();
            ClockChoice.IsChecked = settings.Display == "clock";
            StatsChoice.IsChecked = !ClockChoice.IsChecked;
            SecondsChoice.IsChecked = settings.ShowSeconds ?? true;
            InvertChoice.IsChecked = settings.InvertDisplay ?? false;
            LightThemeChoice.IsChecked = settings.Theme == "light";
            DarkThemeChoice.IsChecked = !LightThemeChoice.IsChecked;
            layout = new StatsLayout(settings.PcStatsLayout);
            UpdateLayoutControls();
            ApplyTheme();
            SourceInitialized += (sender, e) => ApplyCaptionTheme();
            RefreshPreview(true);
            ready = true;

            previewTimer.Interval = TimeSpan.FromMilliseconds(250);
            previewTimer.Tick += (sender, e) => RefreshPreview(RefreshSavedView());
            Closed += (sender, e) => previewTimer.Stop();
            previewTimer.Start();
        }

        private void OnChoiceChanged(object sender, RoutedEventArgs e)
        {
            if (!ready) return;
            if (sender == SecondsChoice)
                SaveCurrentSettings(settings => settings.ShowSeconds = SecondsChoice.IsChecked == true);
            else if (sender == InvertChoice)
                SaveCurrentSettings(settings => settings.InvertDisplay = InvertChoice.IsChecked == true);
            else
                SaveCurrentSettings(settings => settings.Display = sender == ClockChoice ? "clock" : "stats");
            RefreshPreview(true);
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
            bool clockOnly = ClockChoice.IsChecked == true;
            LayoutSection.Visibility = clockOnly ? Visibility.Collapsed : Visibility.Visible;
            bool showSeconds = SecondsChoice.IsChecked == true;
            bool invertDisplay = InvertChoice.IsChecked == true;
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
                displayedSample, clockSeconds, clockOnly, showSeconds, invertDisplay, layout);
            AutomationProperties.SetHelpText(PreviewImage,
                (clockOnly ? "Large Clock" : "PC Stats") +
                (showSeconds ? " with seconds. " : " without seconds. ") +
                (invertDisplay ? "Inverted display. " : "") +
                (isLive ? "Live." : "No live data. Showing sample data."));
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

        [DataMember(Name = "pcStatsLayout", EmitDefaultValue = false)]
        public int PcStatsLayout;
    }
}
