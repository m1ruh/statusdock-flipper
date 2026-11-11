using System.Runtime.Serialization;

namespace StatusDockPreview
{
    [DataContract]
    internal sealed class ConnectionStatus
    {
        public const string FileName = "StatusDock Connection.json";

        [DataMember(Name = "state")]
        public string State { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }

        [DataMember(Name = "firmware")]
        public FirmwareInfo Firmware { get; set; }

        [DataMember(Name = "appIssue")]
        public AppIssue Issue { get; set; }
    }

    [DataContract]
    internal sealed class FirmwareInfo
    {
        public const string ReleaseNotes = "https://github.com/m1ruh/statusdock-flipper/releases/tag/v1.2";
        private const string Downloads = "https://github.com/m1ruh/statusdock-flipper/releases/download/v1.2/";

        [DataMember(Name = "version")]
        public string Version { get; set; }

        [DataMember(Name = "fork")]
        public string Fork { get; set; }

        [DataMember(Name = "family")]
        public string Family { get; set; }

        [DataMember(Name = "api")]
        public string Api { get; set; }

        public string DisplayName
        {
            get
            {
                var name = Family == "Official" ? "Official" : Family == "Momentum" ? "Momentum" : Fork;
                return (name + " " + Version).Trim();
            }
        }

        public string DownloadUrl
        {
            get
            {
                if (Family == "Momentum" && Version == "mntm-012")
                    return Downloads + "StatusDock-1.2-mntm-012-29f7b23.zip";
                if (Family == "Official" && Version == "1.5.1")
                    return Downloads + "StatusDock-1.2-official-1.5.1.zip";
                return null;
            }
        }
    }

    [DataContract]
    internal sealed class AppIssue
    {
        [DataMember(Name = "kind")]
        public string Kind { get; set; }

        [DataMember(Name = "detail")]
        public string Detail { get; set; }
    }
}
