using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Xml;

namespace StatusDockPreview
{
    [DataContract]
    internal sealed class LiveSample
    {
        public const string FileName = "StatusDock Live.json";
        public const int UnknownMetric = 255;
        public const int UnknownGpu = 127;
        public const int QuartersPerSecond = 4;
        public const int SecondsPerDay = 86400;

        [DataMember(Name = "sampledAtUtcMs", IsRequired = true)]
        public long SampledAtUtcMs { get; set; }

        [DataMember(Name = "clockQuarters", IsRequired = true)]
        public int ClockQuarters;

        [DataMember(Name = "cpu", IsRequired = true)]
        public int Cpu;

        [DataMember(Name = "ram", IsRequired = true)]
        public int Ram;

        [DataMember(Name = "gpu", IsRequired = true)]
        public int Gpu;

        [DataMember(Name = "cpuTemp", IsRequired = true)]
        public int CpuTemp;

        [DataMember(Name = "gpuTemp", IsRequired = true)]
        public int GpuTemp;

        public bool IsFreshAt(long utcMs)
        {
            return utcMs >= SampledAtUtcMs && utcMs - SampledAtUtcMs < 10000;
        }

        public static LiveSample Read(string path)
        {
            try
            {
                // The sender can replace or remove the snapshot while this handle is open.
                using (var file = File.Open(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    var sample = (LiveSample)new DataContractJsonSerializer(typeof(LiveSample))
                        .ReadObject(file);
                    return sample != null && sample.IsValid() ? sample : null;
                }
            }
            catch (IOException) { return null; }
            catch (System.UnauthorizedAccessException) { return null; }
            catch (SerializationException) { return null; }
            catch (XmlException) { return null; }
        }

        private bool IsValid()
        {
            return SampledAtUtcMs > 0 &&
                ClockQuarters >= 0 && ClockQuarters < SecondsPerDay * QuartersPerSecond &&
                (IsPercent(Cpu) || Cpu == UnknownMetric) &&
                IsPercent(Ram) && (IsPercent(Gpu) || Gpu == UnknownGpu) &&
                CpuTemp >= 0 && CpuTemp <= UnknownMetric &&
                GpuTemp >= 0 && GpuTemp <= UnknownMetric;
        }

        private static bool IsPercent(int value)
        {
            return value >= 0 && value <= 100;
        }
    }
}
