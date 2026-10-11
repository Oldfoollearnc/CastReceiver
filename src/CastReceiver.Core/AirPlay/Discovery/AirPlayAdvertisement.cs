using System.Text;

namespace CastReceiver.Core.AirPlay.Discovery;

/// <summary>
/// 按参考实现（UxPlay 1.74）给出的值构造 AirPlay 的 mDNS 广播。
///
/// 关键决定：广播的 <c>features</c> 用 <b>0x527FFEE6</b>（第 27 位 = 0，"不支持 legacy pairing"）。
/// 依据 <c>uxplay.cpp:2484</c>：运行时用 <c>setup_legacy_pairing</c>（默认 false）覆盖第 27 位，
/// 因此客户端**不会**发起 pair-setup / pair-verify —— 这正是我们不需要实现 AirPlay 2 配对
/// （SRP / Curve25519 / Ed25519）的原因。若把这一位设成 1 却又不实现配对，
/// 双方派生出的 AES 密钥会不一致，视频能连上但解出来是花屏。
///
/// 注意 <c>GET /info</c> 的 <c>features</c> 必须与这里一致（参考实现读的是同一个运行时值）。
/// </summary>
public static class AirPlayAdvertisement
{
    /// <summary>低 32 位 features（第 27 位清零）。</summary>
    public const uint FeaturesLow = 0x527FFEE6;

    /// <summary>高 32 位 features。</summary>
    public const uint FeaturesHigh = 0x0;

    /// <summary>mDNS TXT 里的 features 写法（"低,高"，十六进制）。</summary>
    public const string FeaturesText = "0x527FFEE6,0x0";

    public const string Model = "AppleTV3,2";
    public const string SourceVersion = "220.68";
    public const string ProtocolVersion = "2";
    public const string Pi = "2e388006-13ba-4041-9a67-25dd4a43d536";

    /// <summary>mDNS 里的 flags（参考实现恒为 0x4）。</summary>
    private const string Flags = "0x4";

    /// <summary>把机器名规整成合法的主机名标签并加 <c>.local</c>（参考实现的做法）。</summary>
    public static string LocalHostName(string? computerName = null)
    {
        string name = computerName ?? Environment.MachineName;
        int dot = name.IndexOf('.');
        if (dot >= 0)
        {
            name = name[..dot];
        }

        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-');
        }

        string label = sb.Length == 0 ? "airplay" : sb.ToString();
        return label + ".local";
    }

    /// <summary>把 <c>aa:bb:cc:dd:ee:ff</c> 变成 <c>AABBCCDDEEFF</c>（_raop 实例名前缀）。</summary>
    private static string MacUpperNoSeparators(string mac)
        => new(mac.Where(char.IsAsciiHexDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>构造 <c>_airplay._tcp</c> 与 <c>_raop._tcp</c> 两个服务（端口都指向 RTSP 端口）。</summary>
    /// <param name="serverName">设备名（手机投屏列表里显示的名字）。</param>
    /// <param name="macAddress">形如 <c>aa:bb:cc:dd:ee:ff</c> 的 AirPlay 设备地址。</param>
    /// <param name="pkHex">64 个小写十六进制字符（32 字节公钥）。</param>
    /// <param name="port">RTSP/HTTP 控制通道的 TCP 端口。</param>
    public static IReadOnlyList<MdnsService> Build(string serverName, string macAddress, string pkHex, int port)
    {
        string macLower = macAddress.ToLowerInvariant();
        string raopPrefix = MacUpperNoSeparators(macAddress);

        var airplayTxt = new List<KeyValuePair<string, string>>
        {
            new("deviceid", macLower),
            new("features", FeaturesText),
            new("pw", "false"),
            new("flags", Flags),
            new("model", Model),
            new("pk", pkHex),
            new("pi", Pi),
            new("srcvers", SourceVersion),
            new("vv", ProtocolVersion),
        };

        var raopTxt = new List<KeyValuePair<string, string>>
        {
            new("ch", "2"),
            new("cn", "0,1,2,3"),
            new("da", "true"),
            new("et", "0,3,5"),
            new("vv", ProtocolVersion),
            new("ft", FeaturesText),
            new("am", Model),
            new("md", "0,1,2"),
            new("rhd", "5.6.0.0"),
            new("pw", "false"),
            new("sr", "44100"),
            new("ss", "16"),
            new("sv", "false"),
            new("tp", "UDP"),
            new("txtvers", "1"),
            new("sf", Flags),
            new("vs", SourceVersion),
            new("vn", "65537"),
            new("pk", pkHex),
        };

        return new[]
        {
            new MdnsService
            {
                ServiceType = "_airplay._tcp.local",
                Instance = $"{serverName}._airplay._tcp.local",
                Port = port,
                Txt = airplayTxt,
            },
            new MdnsService
            {
                ServiceType = "_raop._tcp.local",
                Instance = $"{raopPrefix}@{serverName}._raop._tcp.local",
                Port = port,
                Txt = raopTxt,
            },
        };
    }
}
