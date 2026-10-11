using System.Text;
using CastReceiver.Core.AirPlay.Discovery;

namespace CastReceiver.Core.AirPlay;

/// <summary>AirPlay 接收端的基本参数。</summary>
public sealed class AirPlayOptions
{
    /// <summary>手机投屏列表里显示的名字。</summary>
    public string ServerName { get; set; } = "投屏Window";

    /// <summary>AirPlay 设备地址（形如 <c>aa:bb:cc:dd:ee:ff</c>）。</summary>
    public string MacAddress { get; set; } = "aa:bb:cc:dd:ee:ff";

    /// <summary>mDNS 与 <c>/info</c> 共用的 32 字节公钥（64 个十六进制字符）。跳过配对时它不参与密码学运算。</summary>
    public string PkHex { get; set; } = "b07727d6f6cd6e08b58ede525ec3cdeaa252ad9f683feb212ef8a205246554e7";

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public double RefreshRate { get; set; } = 60.0;

    /// <summary>初始音量（AirPlay 用 dB，范围 0 … -30）。镜像不用音频，取 0。</summary>
    public double InitialVolume { get; set; }
}

/// <summary>
/// 构造 <c>GET /info</c> 的响应体（二进制 plist）。
///
/// 两条分支（与参考实现一致）：
/// <list type="bullet">
/// <item>请求带任何 <c>Content-Type</c>：只回 <c>txtAirPlay</c>/<c>txtRAOP</c>（mDNS TXT 原始字节）。</item>
/// <item>请求没有 <c>Content-Type</c>（iOS 探测投屏能力走这条）：回完整的设备能力字典。</item>
/// </list>
///
/// <c>features</c> 必须与 mDNS 广播的值一致（都是 0x527FFEE6）。若干键的类型有讲究：
/// <c>outputLatencyMicros</c>/<c>rotation</c>/<c>overscanned</c> 是布尔而不是整数。
/// </summary>
internal static class AirPlayInfoPlist
{
    public static Dictionary<string, object?> Build(AirPlayOptions options, string pkHex, int port)
    {
        var displays = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["uuid"] = "e0ff8a27-6738-3d56-8a16-cc53aacee925",
                ["widthPhysical"] = 0L,
                ["heightPhysical"] = 0L,
                ["width"] = (long)options.Width,
                ["height"] = (long)options.Height,
                ["widthPixels"] = (long)options.Width,
                ["heightPixels"] = (long)options.Height,
                ["rotation"] = false,
                // 参考实现写的是 1.0/refreshRate（60 → 0.0166666…）
                ["refreshRate"] = options.RefreshRate > 0 ? 1.0 / options.RefreshRate : 0.0,
                ["maxFPS"] = 30L,
                ["overscanned"] = false,
                ["features"] = 14L,
            },
        };

        var audioLatencies = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["type"] = 100L,
                ["inputLatencyMicros"] = 0L,
                ["audioType"] = "default",
                ["outputLatencyMicros"] = false,
            },
            new Dictionary<string, object?>
            {
                ["type"] = 101L,
                ["inputLatencyMicros"] = 0L,
                ["audioType"] = "default",
                ["outputLatencyMicros"] = false,
            },
        };

        var audioFormats = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["type"] = 100L,
                ["audioInputFormats"] = 0x3FFFFFCL,
                ["audioOutputFormats"] = 0x3FFFFFCL,
            },
            new Dictionary<string, object?>
            {
                ["type"] = 101L,
                ["audioInputFormats"] = 0x3FFFFFCL,
                ["audioOutputFormats"] = 0x3FFFFFCL,
            },
        };

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["deviceID"] = options.MacAddress,
            ["macAddress"] = options.MacAddress,
            ["pk"] = HexToBytes(pkHex),
            ["features"] = ((ulong)AirPlayAdvertisement.FeaturesHigh << 32) | AirPlayAdvertisement.FeaturesLow,
            ["name"] = $"{options.ServerName}@{AirPlayAdvertisement.LocalHostName()}",
            ["pi"] = AirPlayAdvertisement.Pi,
            ["vv"] = 2L,
            ["statusFlags"] = 68L,
            ["keepAliveLowPower"] = 1L,
            ["sourceVersion"] = AirPlayAdvertisement.SourceVersion,
            ["keepAliveSendStatsAsBody"] = true,
            ["model"] = AirPlayAdvertisement.Model,
            ["initialVolume"] = options.InitialVolume,
            ["audioLatencies"] = audioLatencies,
            ["audioFormats"] = audioFormats,
            ["displays"] = displays,
        };
    }

    /// <summary>把 mDNS 的 TXT 键值序列化成 DNS-SD 的原始形态（长度前缀 + <c>k=v</c>）。</summary>
    public static byte[] RawTxt(MdnsService service)
    {
        var bytes = new List<byte>();
        foreach (var pair in service.Txt)
        {
            byte[] item = Encoding.UTF8.GetBytes($"{pair.Key}={pair.Value}");
            if (item.Length > 255)
            {
                continue;
            }
            bytes.Add((byte)item.Length);
            bytes.AddRange(item);
        }
        return bytes.ToArray();
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            return Array.Empty<byte>();
        }
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
}
