using System.Text;

namespace CastReceiver.Core.Dlna;

/// <summary>
/// 生成 DLNA 设备描述与三个服务的 SCPD（Service Control Protocol Description）。
/// 说明：国内视频 App（腾讯视频/爱奇艺等）在做 DLNA 投屏时会先抓取 description.xml 与 SCPD 校验能力，
/// 因此这里把 AVTransport / RenderingControl / ConnectionManager 的动作与状态变量写全，
/// 并声明 dlna:X_DLNADOC=DMR-1.50（标准 DLNA 渲染器标识）。
/// </summary>
public static class DlnaXml
{
    public const string DeviceNs = "urn:schemas-upnp-org:device-1-0";
    public const string ServiceNsAvt = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string ServiceNsRcs = "urn:schemas-upnp-org:service:RenderingControl:1";
    public const string ServiceNsCms = "urn:schemas-upnp-org:service:ConnectionManager:1";

    public const string ControlUrlAvt = "/AVTransport/control";
    public const string EventUrlAvt = "/AVTransport/event";
    public const string ScpdUrlAvt = "/AVTransport/scpd.xml";
    public const string ControlUrlRcs = "/RenderingControl/control";
    public const string EventUrlRcs = "/RenderingControl/event";
    public const string ScpdUrlRcs = "/RenderingControl/scpd.xml";
    public const string ControlUrlCms = "/ConnectionManager/control";
    public const string EventUrlCms = "/ConnectionManager/event";
    public const string ScpdUrlCms = "/ConnectionManager/scpd.xml";
    public const string DescriptionUrl = "/description.xml";

    public static string DeviceDescription(string uuid, string friendlyName, string baseUrl)
    {
        string modelName = Escape(friendlyName);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append($"<root xmlns=\"{DeviceNs}\" xmlns:dlna=\"urn:schemas-dlna-org:device-1-0\">\n");
        sb.Append("  <specVersion><major>1</major><minor>0</minor></specVersion>\n");
        sb.Append("  <device>\n");
        sb.Append("    <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>\n");
        sb.Append($"    <friendlyName>{modelName}</friendlyName>\n");
        sb.Append("    <manufacturer>投屏Window</manufacturer>\n");
        sb.Append("    <manufacturerURL>https://localhost/cast-receiver</manufacturerURL>\n");
        sb.Append("    <modelDescription>Windows 桌面投屏接收端（DLNA MediaRenderer）</modelDescription>\n");
        sb.Append($"    <modelName>{modelName}</modelName>\n");
        sb.Append("    <modelNumber>1.0</modelNumber>\n");
        sb.Append($"    <serialNumber>{uuid.Replace("-", "")[..12].ToUpperInvariant()}</serialNumber>\n");
        sb.Append($"    <UDN>uuid:{uuid}</UDN>\n");
        sb.Append($"    <dlna:X_DLNADOC>DMR-1.50</dlna:X_DLNADOC>\n");
        sb.Append("    <serviceList>\n");
        AppendService(sb, ServiceNsAvt, "urn:upnp-org:serviceId:AVTransport", ScpdUrlAvt, ControlUrlAvt, EventUrlAvt);
        AppendService(sb, ServiceNsRcs, "urn:upnp-org:serviceId:RenderingControl", ScpdUrlRcs, ControlUrlRcs, EventUrlRcs);
        AppendService(sb, ServiceNsCms, "urn:upnp-org:serviceId:ConnectionManager", ScpdUrlCms, ControlUrlCms, EventUrlCms);
        sb.Append("    </serviceList>\n");
        sb.Append("  </device>\n");
        sb.Append("</root>\n");
        return sb.ToString();
    }

    private static void AppendService(StringBuilder sb, string type, string id, string scpd, string control, string eventUrl)
    {
        sb.Append("      <service>\n");
        sb.Append($"        <serviceType>{type}</serviceType>\n");
        sb.Append($"        <serviceId>{id}</serviceId>\n");
        sb.Append($"        <SCPDURL>{scpd}</SCPDURL>\n");
        sb.Append($"        <controlURL>{control}</controlURL>\n");
        sb.Append($"        <eventSubURL>{eventUrl}</eventSubURL>\n");
        sb.Append("      </service>\n");
    }

    // ------------------------------------------------------------------ SCPD

    public static string AvTransportScpd() => Scpd(ServiceNsAvt, "AVTransport",
        new[]
        {
            "GetCurrentTransportActions|InstanceID:in:A_ARG_TYPE_InstanceID;Actions:out:CurrentTransportActions",
            "GetDeviceCapabilities|InstanceID:in:A_ARG_TYPE_InstanceID;PlayMedia:out:PossiblePlaybackStorageMedia;RecMedia:out:PossibleRecordStorageMedia;RecQualityModes:out:PossibleRecordQualityModes",
            "GetMediaInfo|InstanceID:in:A_ARG_TYPE_InstanceID;NrTracks:out:NumberOfTracks;MediaDuration:out:CurrentMediaDuration;CurrentURI:out:AVTransportURI;CurrentURIMetaData:out:AVTransportURIMetaData;NextURI:out:NextAVTransportURI;NextURIMetaData:out:NextAVTransportURIMetaData;PlayMedium:out:PlaybackStorageMedium;RecordMedium:out:RecordStorageMedium;WriteStatus:out:RecordMediumWriteStatus",
            "GetPositionInfo|InstanceID:in:A_ARG_TYPE_InstanceID;Track:out:CurrentTrack;TrackDuration:out:CurrentTrackDuration;TrackMetaData:out:CurrentTrackMetaData;TrackURI:out:CurrentTrackURI;RelTime:out:RelativeTimePosition;AbsTime:out:AbsoluteTimePosition;RelCount:out:RelativeCounterPosition;AbsCount:out:AbsoluteCounterPosition",
            "GetTransportInfo|InstanceID:in:A_ARG_TYPE_InstanceID;CurrentTransportState:out:TransportState;CurrentTransportStatus:out:TransportStatus;CurrentSpeed:out:TransportPlaySpeed",
            "GetTransportSettings|InstanceID:in:A_ARG_TYPE_InstanceID;PlayMode:out:CurrentPlayMode;RecQualityMode:out:CurrentRecordQualityMode",
            "Next|InstanceID:in:A_ARG_TYPE_InstanceID",
            "Pause|InstanceID:in:A_ARG_TYPE_InstanceID",
            "Play|InstanceID:in:A_ARG_TYPE_InstanceID;Speed:in:TransportPlaySpeed",
            "Previous|InstanceID:in:A_ARG_TYPE_InstanceID",
            "Seek|InstanceID:in:A_ARG_TYPE_InstanceID;Unit:in:A_ARG_TYPE_SeekMode;Target:in:A_ARG_TYPE_SeekTarget",
            "SetAVTransportURI|InstanceID:in:A_ARG_TYPE_InstanceID;CurrentURI:in:AVTransportURI;CurrentURIMetaData:in:AVTransportURIMetaData",
            "SetNextAVTransportURI|InstanceID:in:A_ARG_TYPE_InstanceID;NextURI:in:NextAVTransportURI;NextURIMetaData:in:NextAVTransportURIMetaData",
            "Stop|InstanceID:in:A_ARG_TYPE_InstanceID",
        },
        new[]
        {
            "LastChange|string|yes|null",
            "TransportState|string|no|STOPPED,PLAYING,PAUSED_PLAYBACK,TRANSITIONING,NO_MEDIA_PRESENT",
            "TransportStatus|string|no|OK,ERROR_OCCURRED",
            "PlaybackStorageMedium|string|no|NONE,NETWORK,UNKNOWN",
            "RecordStorageMedium|string|no|NOT_IMPLEMENTED",
            "PossiblePlaybackStorageMedia|string|no|null",
            "PossibleRecordStorageMedia|string|no|null",
            "CurrentPlayMode|string|no|NORMAL",
            "TransportPlaySpeed|string|no|1",
            "RecordMediumWriteStatus|string|no|NOT_IMPLEMENTED",
            "CurrentRecordQualityMode|string|no|NOT_IMPLEMENTED",
            "PossibleRecordQualityModes|string|no|null",
            "NumberOfTracks|ui4|no|0",
            "CurrentTrack|ui4|no|0",
            "CurrentTrackDuration|string|no|null",
            "CurrentMediaDuration|string|no|null",
            "CurrentTrackMetaData|string|no|null",
            "CurrentTrackURI|string|no|null",
            "AVTransportURI|string|no|null",
            "AVTransportURIMetaData|string|no|null",
            "NextAVTransportURI|string|no|null",
            "NextAVTransportURIMetaData|string|no|null",
            "RelativeTimePosition|string|no|null",
            "AbsoluteTimePosition|string|no|null",
            "RelativeCounterPosition|i4|no|null",
            "AbsoluteCounterPosition|i4|no|null",
            "CurrentTransportActions|string|no|null",
        },
        new[] { "InstanceID|ui4", "SeekMode|string|TRACK_NUM,REL_TIME,ABS_TIME", "SeekTarget|string", "Speed|string", "Track|ui4" },
        "A_ARG_TYPE_");

    public static string RenderingControlScpd() => Scpd(ServiceNsRcs, "RenderingControl",
        new[]
        {
            "GetBlueVideoBlackLevel|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentBlueVideoBlackLevel:out:BlueVideoBlackLevel",
            "GetBlueVideoGain|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentBlueVideoGain:out:BlueVideoGain",
            "GetBrightness|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentBrightness:out:Brightness",
            "GetColorTemperature|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentColorTemperature:out:ColorTemperature",
            "GetContrast|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentContrast:out:Contrast",
            "GetGreenVideoBlackLevel|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentGreenVideoBlackLevel:out:GreenVideoBlackLevel",
            "GetGreenVideoGain|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentGreenVideoGain:out:GreenVideoGain",
            "GetHorizontalKeystone|InstanceID:in:A_ARG_TYPE_InstanceID;CurrentHorizontalKeystone:out:HorizontalKeystone",
            "GetMute|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentMute:out:Mute",
            "GetRedVideoBlackLevel|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentRedVideoBlackLevel:out:RedVideoBlackLevel",
            "GetRedVideoGain|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentRedVideoGain:out:RedVideoGain",
            "GetSharpness|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentSharpness:out:Sharpness",
            "GetVolume|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentVolume:out:Volume",
            "GetVolumeDB|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;CurrentVolume:out:VolumeDB",
            "GetVolumeDBRange|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;MinValue:out:VolumeDB;MaxValue:out:VolumeDB",
            "GetVerticalKeystone|InstanceID:in:A_ARG_TYPE_InstanceID;CurrentVerticalKeystone:out:VerticalKeystone",
            "ListPresets|InstanceID:in:A_ARG_TYPE_InstanceID;CurrentPresetNameList:out:PresetNameList",
            "SelectPreset|InstanceID:in:A_ARG_TYPE_InstanceID;PresetName:in:A_ARG_TYPE_PresetName",
            "SetBlueVideoBlackLevel|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredBlueVideoBlackLevel:in:BlueVideoBlackLevel",
            "SetBlueVideoGain|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredBlueVideoGain:in:BlueVideoGain",
            "SetBrightness|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredBrightness:in:Brightness",
            "SetColorTemperature|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredColorTemperature:in:ColorTemperature",
            "SetContrast|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredContrast:in:Contrast",
            "SetGreenVideoBlackLevel|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredGreenVideoBlackLevel:in:GreenVideoBlackLevel",
            "SetGreenVideoGain|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredGreenVideoGain:in:GreenVideoGain",
            "SetHorizontalKeystone|InstanceID:in:A_ARG_TYPE_InstanceID;DesiredHorizontalKeystone:in:HorizontalKeystone",
            "SetMute|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredMute:in:Mute",
            "SetRedVideoBlackLevel|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredRedVideoBlackLevel:in:RedVideoBlackLevel",
            "SetRedVideoGain|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredRedVideoGain:in:RedVideoGain",
            "SetSharpness|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredSharpness:in:Sharpness",
            "SetVerticalKeystone|InstanceID:in:A_ARG_TYPE_InstanceID;DesiredVerticalKeystone:in:VerticalKeystone",
            "SetVolume|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredVolume:in:Volume",
            "SetVolumeDB|InstanceID:in:A_ARG_TYPE_InstanceID;Channel:in:A_ARG_TYPE_Channel;DesiredVolume:in:VolumeDB",
        },
        new[]
        {
            "LastChange|string|yes|null",
            "PresetNameList|string|no|FactoryDefaults",
            "Brightness|ui2|no|0-65535",
            "Contrast|ui2|no|0-65535",
            "Sharpness|ui2|no|0-65535",
            "RedVideoGain|ui2|no|0-65535",
            "GreenVideoGain|ui2|no|0-65535",
            "BlueVideoGain|ui2|no|0-65535",
            "RedVideoBlackLevel|ui2|no|0-65535",
            "GreenVideoBlackLevel|ui2|no|0-65535",
            "BlueVideoBlackLevel|ui2|no|0-65535",
            "ColorTemperature|ui2|no|0-65535",
            "HorizontalKeystone|i2|no|-32768-32767",
            "VerticalKeystone|i2|no|-32768-32767",
            "Mute|boolean|no|0,1",
            "Volume|ui2|no|0-100",
            "VolumeDB|i2|no|-32768-32767",
        },
        new[]
        {
            "InstanceID|ui4", "Channel|string|Master,LF,RF", "PresetName|string|FactoryDefaults",
            "DesiredVolume|ui2", "DesiredMute|boolean",
        },
        "A_ARG_TYPE_");

    public static string ConnectionManagerScpd() => Scpd(ServiceNsCms, "ConnectionManager",
        new[]
        {
            "GetCurrentConnectionIDs|ConnectionIDs:out:CurrentConnectionIDs",
            "GetCurrentConnectionInfo|ConnectionID:in:A_ARG_TYPE_ConnectionID;RcsID:out:A_ARG_TYPE_RcsID;AVTransportID:out:A_ARG_TYPE_AVTransportID;ProtocolInfo:out:A_ARG_TYPE_ProtocolInfo;PeerConnectionManager:out:A_ARG_TYPE_ConnectionManager;PeerConnectionID:out:A_ARG_TYPE_ConnectionID;Direction:out:A_ARG_TYPE_Direction;Status:out:A_ARG_TYPE_ConnectionStatus",
            "GetProtocolInfo|Source:out:SourceProtocolInfo;Sink:out:SinkProtocolInfo",
        },
        new[]
        {
            "SourceProtocolInfo|string|yes|null",
            "SinkProtocolInfo|string|yes|null",
            "CurrentConnectionIDs|string|yes|null",
            "A_ARG_TYPE_ConnectionStatus|string|no|OK,ContentFormatMismatch,InsufficientBandwidth,UnreliableChannel,Unknown",
            "A_ARG_TYPE_ConnectionManager|string|no|null",
            "A_ARG_TYPE_Direction|string|no|Input,Output",
            "A_ARG_TYPE_ProtocolInfo|string|no|null",
            "A_ARG_TYPE_ConnectionID|i4|no|null",
            "A_ARG_TYPE_AVTransportID|i4|no|null",
            "A_ARG_TYPE_RcsID|i4|no|null",
        },
        Array.Empty<string>());

    private static string Scpd(string serviceNs, string serviceName,
                               IEnumerable<string> actions,
                               IEnumerable<string> variables,
                               IEnumerable<string> argTypes,
                               string argTypePrefix = "A_ARG_TYPE_")
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append($"<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\">\n");
        sb.Append("  <specVersion><major>1</major><minor>0</minor></specVersion>\n");
        sb.Append("  <actionList>\n");
        foreach (var spec in actions)
        {
            var parts = spec.Split('|');
            sb.Append("    <action>\n");
            sb.Append($"      <name>{parts[0]}</name>\n");
            if (parts.Length > 1 && parts[1].Length > 0)
            {
                sb.Append("      <argumentList>\n");
                foreach (var argSpec in parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var a = argSpec.Split(':');
                    sb.Append("        <argument>\n");
                    sb.Append($"          <name>{a[0]}</name>\n");
                    sb.Append($"          <direction>{a[1]}</direction>\n");
                    sb.Append($"          <relatedStateVariable>{a[2]}</relatedStateVariable>\n");
                    sb.Append("        </argument>\n");
                }
                sb.Append("      </argumentList>\n");
            }
            sb.Append("    </action>\n");
        }
        sb.Append("  </actionList>\n");
        sb.Append("  <serviceStateTable>\n");

        void Emit(string name, string type, bool sendEvents, string? allowed)
        {
            sb.Append($"    <stateVariable sendEvents=\"{(sendEvents ? "yes" : "no")}\">\n");
            sb.Append($"      <name>{name}</name>\n");
            sb.Append($"      <dataType>{type}</dataType>\n");
            if (allowed is not null and not "null")
            {
                sb.Append("      <allowedValueList>\n");
                foreach (var v in allowed.Split(','))
                    sb.Append($"        <allowedValue>{v}</allowedValue>\n");
                sb.Append("      </allowedValueList>\n");
            }
            sb.Append("    </stateVariable>\n");
        }

        foreach (var spec in variables)
        {
            var parts = spec.Split('|');
            string allowed = parts.Length > 3 ? parts[3] : "null";
            Emit(parts[0], parts[1], parts.Length > 2 && parts[2] == "yes", allowed);
        }

        foreach (var spec in argTypes)
        {
            var parts = spec.Split('|');
            string name = argTypePrefix + parts[0];
            // 已显式声明的（ConnectionManager 的 A_ARG_TYPE_*）直接使用原名
            if (variables.Any(v => v.Split('|')[0].Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            Emit(name, parts[1], false, parts.Length > 2 ? parts[2] : null);
        }

        sb.Append("  </serviceStateTable>\n");
        sb.Append("</scpd>\n");
        return sb.ToString();
    }

    public static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    /// <summary>协议能力表：尽量宽松，覆盖国内视频 App 常见的 mp4/m3u8/flv/ts 直链。</summary>
    public static string SinkProtocolInfo() => string.Join(",", new[]
    {
        "http-get:*:video/mp4:DLNA.ORG_PN=MP4_LRG;DLNA.ORG_OP=01;DLNA.ORG_CI=0",
        "http-get:*:video/mp4:DLNA.ORG_OP=01;DLNA.ORG_CI=0",
        "http-get:*:video/mpeg:DLNA.ORG_OP=01;DLNA.ORG_CI=0",
        "http-get:*:video/x-msvideo:*",
        "http-get:*:video/x-matroska:*",
        "http-get:*:video/x-flv:*",
        "http-get:*:video/webm:*",
        "http-get:*:video/vnd.dlna.mpeg-tts:*",
        "http-get:*:video/quicktime:*",
        "http-get:*:video/3gpp:*",
        "http-get:*:application/vnd.apple.mpegurl:*",
        "http-get:*:application/x-mpegURL:*",
        "http-get:*:audio/mpeg:*",
        "http-get:*:audio/mp4:*",
        "http-get:*:audio/x-ms-wma:*",
        "http-get:*:audio/flac:*",
        "http-get:*:audio/wav:*",
        "http-get:*:image/jpeg:*",
        "http-get:*:image/png:*",
        "http-get:*:*:*",
    });
}