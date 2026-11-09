using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Threading;
using System.Xml;

namespace StatusDockPreview
{
    public partial class SettingsWindow : Window
    {
        private const int LiveTimeoutMs = 10000;
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
            AppDomain.CurrentDomain.BaseDirectory, "StatusDock Live.json");
        private readonly Stopwatch exampleClock = Stopwatch.StartNew();
        private readonly DispatcherTimer previewTimer = new DispatcherTimer();
        private long? drawnSampleUtcMs;
        private int drawnClockSeconds = -1;
        private bool ready;

        [STAThread]
        public static void Main()
        {
            new Application().Run(new SettingsWindow());
        }

        public SettingsWindow()
        {
            InitializeComponent();
            MaxHeight = SystemParameters.WorkArea.Height;
            MaxWidth = SystemParameters.WorkArea.Width;

            var settings = LoadSettings();
            ClockChoice.IsChecked = settings.Display == "clock";
            StatsChoice.IsChecked = !ClockChoice.IsChecked;
            SecondsChoice.IsChecked = settings.ShowSeconds ?? true;
            RefreshPreview(true);
            ready = true;

            previewTimer.Interval = TimeSpan.FromMilliseconds(250);
            previewTimer.Tick += (sender, e) => RefreshPreview();
            Closed += (sender, e) => previewTimer.Stop();
            previewTimer.Start();
        }

        private void OnChoiceChanged(object sender, RoutedEventArgs e)
        {
            if (!ready) return;
            RefreshPreview(true);

            var settings = new PreviewSettings
            {
                Display = ClockChoice.IsChecked == true ? "clock" : "stats",
                ShowSeconds = SecondsChoice.IsChecked == true
            };
            try
            {
                SaveSettings(settings);
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

        private void RefreshPreview(bool force = false)
        {
            var sample = LiveSample.Read(liveSamplePath);
            long ageMs = sample == null ? 0 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sample.SampledAtUtcMs;
            if (ageMs < 0 || ageMs >= LiveTimeoutMs) sample = null;

            bool isLive = sample != null;
            bool clockOnly = ClockChoice.IsChecked == true;
            bool showSeconds = SecondsChoice.IsChecked == true;
            var displayedSample = sample ?? Example;
            long elapsedMs = isLive ? (showSeconds ? ageMs : 0) : exampleClock.ElapsedMilliseconds;
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
            PreviewImage.Source = FlipperPreview.Render(displayedSample, clockSeconds,
                clockOnly, showSeconds, isLive);
            System.Windows.Automation.AutomationProperties.SetHelpText(PreviewImage,
                (clockOnly ? "Large Clock" : "PC Stats") +
                (showSeconds ? " with seconds. " : " without seconds. ") +
                (isLive ? "Live." : "No live data. Showing sample data."));
        }

        private PreviewSettings LoadSettings()
        {
            try
            {
                using (var file = File.OpenRead(settingsPath))
                    return (PreviewSettings)new DataContractJsonSerializer(typeof(PreviewSettings))
                        .ReadObject(file) ?? new PreviewSettings();
            }
            catch (FileNotFoundException) { return new PreviewSettings(); }
            catch (IOException) { return UseDefaultSettings(); }
            catch (UnauthorizedAccessException) { return UseDefaultSettings(); }
            catch (SerializationException) { return UseDefaultSettings(); }
            catch (XmlException) { return UseDefaultSettings(); }
        }

        private PreviewSettings UseDefaultSettings()
        {
            SaveStatus.Text = "Could not read saved settings. Using defaults.";
            return new PreviewSettings();
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
    }
}
