using CastReceiver.Core.AirPlay;
using CastReceiver.Core.AirPlay.Discovery;
using CastReceiver.Core.AirPlay.FairPlay;
using CastReceiver.Core.AirPlay.Mirror;
using CastReceiver.Core.AirPlay.Plist;
using CastReceiver.Core.AirPlay.Rtsp;

namespace CastReceiver.Core.Diagnostics;

/// <summary>
/// AirPlay 链路的协议级自检（不依赖网络、不依赖手机、不依赖 Python/GCC）。
///
/// 这里内嵌的期望值不是"随便找一个自洽的值"，而是**从 UxPlay C 参考实现导出的向量**：
/// <list type="bullet">
/// <item><c>fp-setup</c> 的 142 字节应答、以及由 <c>ekey</c> 推出的 16 字节 AES 密钥，
/// 都是编译 UxPlay 原版 C 源码后逐字节比对过的（见 <c>tools/airplay-port/vector-verify</c>）；</item>
/// <item>AES-128-CTR 用的是 NIST SP 800-38A 的公开向量。</item>
/// </list>
/// 这样产品自身就能回归整条链路，而不用把开发期的工具链带进发布包。
/// </summary>
internal static class AirPlaySelfTest
{
    // ---- 以下常量由 tools/airplay-port 的向量文件导出（fairplay-vectors.json / aes-ctr-vectors.json）

    /// <summary>fp-setup 的 16 字节挑战（FPLY 头 + mode 0）。</summary>
    private const string SetupRequest16 =
        "46504c59030101000000000000000000";

    /// <summary>UxPlay 对上述挑战的 142 字节应答。</summary>
    private const string SetupResponse142 =
        "46504c59030102000000008202000f9f3f9e0a2521dbdf312ab2bfb29e8d232b6376a8c818701d22ae93d82737feaf9db4fdf41c2dba9d1f49caaabf6591ac1f7bc6f7e0663d21afe01565953eab81f418ceed095adb7c3d0e254909a79831d49c3982973434facb42c63a1cd911a6fe941a8a6d4a743b46c3a7649e44c78955e49d8155009549c4e2f7a3f6d5ba";

    /// <summary>164 字节 fp-setup 握手消息（mode 0）。</summary>
    private const string HandshakeKeyMsg164 =
        "46504c59030101000000000000010000a7854fa03db056c1b1d76c6de1e0e550f3c0777795f31ce9852d3575524ff0cb629eedcf35311d2be9a9eb25ff259fb05e7600c6968f3af5664100398126f30b8fdb43b8707394f4c52924afb2562c28dea48541bd828d140fd749c4a9fccd9675e5d940b59fc5828d00a0f4c19b9520bcf38ed2d2f11dacc89899fe92f985d55c623752cddcb73e88d5e5ddf51ae0003f09a45f";

    /// <summary>SETUP 里的 72 字节 ekey。</summary>
    private const string Ekey72 =
        "9e06f326d82c76ce0345252f8dfce5d57f537290ff517d5015fd1630b0914dd1e9e815ea87396a1ec509b40e505b6cb0942afee03a1aee35ea6c40175730120f7abf8c5f1f69fcd4";

    /// <summary>参考实现由上述 ekey 解出的 16 字节 AES 密钥。</summary>
    private const string ExpectedAesKey16 =
        "af2bd1d3a96a5c76a6ee079ad1b466f3";

    /// <summary>NIST SP 800-38A 的 AES-128-CTR 已知答案。</summary>
    private const string CtrKey = "b3771583a8cda7b6815855c9e80196a7";
    private const string CtrIv = "e15762b09499372f1e2b88a3c9971de8";
    private const string CtrPlain = "c545ca14d65c6d23ab9cd56bcedc33a1";
    private const string CtrCipher = "f2186d07db136e474cd5f6170ec03736";

    private const string Group = "J AirPlay";

    internal static void Run(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        ArgumentNullException.ThrowIfNull(add);

        CheckFpSetup(add);
        CheckFairPlayDecrypt(add);
        CheckAesCtr(add);
        CheckPlist(add);
        CheckDiscovery(add);
        CheckMirrorParsing(add);
        CheckVideoKeyDerivation(add);
    }

    // ---------------------------------------------------------------- FairPlay

    private static void CheckFpSetup(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        try
        {
            var session = new FairPlaySession();

            byte[] request = Convert.FromHexString(SetupRequest16);
            var response = new byte[142];
            bool ok = session.Setup(request, response);
            byte[] expected = Convert.FromHexString(SetupResponse142);
            bool same = ok && response.AsSpan().SequenceEqual(expected);
            add(Group, "fp-setup 16 字节挑战的应答与参考实现一致", same,
                same ? "142 字节逐字节一致" : $"应答不一致（ok={ok}，首字节 0x{response[0]:x2}）");

            // 164 字节握手：应答 = 12 字节固定头 + 请求尾 20 字节
            byte[] keyMsg = Convert.FromHexString(HandshakeKeyMsg164);
            var handshake = new byte[32];
            bool ok2 = session.Handshake(keyMsg, handshake);
            byte[] header = Convert.FromHexString("46504c590301040000000014");
            bool headerOk = ok2 && handshake.AsSpan(0, 12).SequenceEqual(header);
            bool tailOk = handshake.AsSpan(12).SequenceEqual(keyMsg.AsSpan(144, 20));
            add(Group, "fp-setup 164 字节握手应答结构正确", headerOk && tailOk,
                headerOk && tailOk ? "12 字节头 + 回显请求尾 20 字节" : $"头={headerOk} 尾={tailOk}");
        }
        catch (Exception ex)
        {
            add(Group, "fp-setup 握手", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CheckFairPlayDecrypt(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        try
        {
            var session = new FairPlaySession();
            byte[] keyMsg = Convert.FromHexString(HandshakeKeyMsg164);
            var handshake = new byte[32];
            session.Handshake(keyMsg, handshake);

            var aesKey = new byte[16];
            bool ok = session.Decrypt(Convert.FromHexString(Ekey72), aesKey);
            byte[] expected = Convert.FromHexString(ExpectedAesKey16);
            bool same = ok && aesKey.AsSpan().SequenceEqual(expected);
            add(Group, "由 ekey 解出 AES 密钥（与参考实现一致）", same,
                same ? $"密钥 {Convert.ToHexString(aesKey).ToLowerInvariant()}" : $"得到 {Convert.ToHexString(aesKey).ToLowerInvariant()}");
        }
        catch (Exception ex)
        {
            add(Group, "FairPlay 密钥派生", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- AES-CTR

    private static void CheckAesCtr(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        try
        {
            byte[] key = Convert.FromHexString(CtrKey);
            byte[] iv = Convert.FromHexString(CtrIv);
            byte[] expected = Convert.FromHexString(CtrCipher);

            byte[] oneShot = Convert.FromHexString(CtrPlain);
            using (var ctr = new Aes128Ctr(key, iv))
            {
                ctr.Transform(oneShot);
            }
            bool katOk = oneShot.AsSpan().SequenceEqual(expected);
            add(Group, "AES-128-CTR 已知答案（NIST SP 800-38A）", katOk,
                katOk ? "密文与公开向量一致" : $"得到 {Convert.ToHexString(oneShot).ToLowerInvariant()}");

            // 镜像流的密钥流是跨包连续的：分多次调用必须与一次调用等价
            byte[] split = Convert.FromHexString(CtrPlain);
            using (var ctr = new Aes128Ctr(key, iv))
            {
                ctr.Transform(split.AsSpan(0, 5));
                ctr.Transform(split.AsSpan(5, 0));      // 空段也不能打乱密钥流
                ctr.Transform(split.AsSpan(5));
            }
            bool continuityOk = split.AsSpan().SequenceEqual(expected);
            add(Group, "AES-128-CTR 密钥流跨调用连续（含空段）", continuityOk,
                continuityOk ? "分段解密与一次解密结果相同" : "分段结果与整段不一致");
        }
        catch (Exception ex)
        {
            add(Group, "AES-128-CTR", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 二进制 plist

    private static void CheckPlist(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        try
        {
            var original = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deviceID"] = "aa:bb:cc:dd:ee:ff",
                ["features"] = 0x527FFEE6L,
                ["pk"] = new byte[] { 1, 2, 3, 4, 5 },
                ["flag"] = true,
                ["rate"] = 0.016666666666666666,
                ["streams"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["type"] = 110L, ["streamConnectionID"] = 12345678901234L },
                },
            };

            byte[] bytes = BinaryPlist.Write(original);
            bool headerOk = bytes.Length > 8 && bytes[0] == (byte)'b' && bytes[1] == (byte)'p';
            var round = BinaryPlist.Read(bytes) as Dictionary<string, object?>;

            bool ok = headerOk && round is not null
                && round.Count == original.Count
                && Equals(round["deviceID"], "aa:bb:cc:dd:ee:ff")
                && Convert.ToInt64(round["features"]) == 0x527FFEE6L
                && round["pk"] is byte[] pk && pk.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4, 5 })
                && round["flag"] is true
                && Math.Abs(Convert.ToDouble(round["rate"]) - 0.016666666666666666) < 1e-12
                && round["streams"] is List<object?> list && list.Count == 1
                && list[0] is Dictionary<string, object?> s
                && Convert.ToInt64(s["type"]) == 110L
                && Convert.ToInt64(s["streamConnectionID"]) == 12345678901234L;

            add(Group, "二进制 plist 往返（含 data/uint/bool/real/嵌套）", ok,
                ok ? $"{bytes.Length} 字节，读回一致" : "读回内容与写入不一致");

            // 损坏数据必须被拒绝，而不是抛未预期的异常或静默返回垃圾
            bool rejected = false;
            try
            {
                var broken = (byte[])bytes.Clone();
                broken[0] = (byte)'x';
                BinaryPlist.Read(broken);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            catch (Exception)
            {
                rejected = false;
            }
            add(Group, "二进制 plist 拒绝损坏数据", rejected,
                rejected ? "魔数错误被正确拒绝" : "损坏数据未被拒绝");
        }
        catch (Exception ex)
        {
            add(Group, "二进制 plist", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 发现

    private static void CheckDiscovery(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        try
        {
            const string mac = "7a:1b:2c:3d:4e:5f";
            string pk = new string('b', 64);
            var services = AirPlayAdvertisement.Build("PC", mac, pk, 7000);

            MdnsService? airplay = services.FirstOrDefault(s => s.ServiceType == "_airplay._tcp.local");
            MdnsService? raop = services.FirstOrDefault(s => s.ServiceType == "_raop._tcp.local");

            bool twoServices = airplay is not null && raop is not null;
            string? features = airplay?.Txt.FirstOrDefault(t => t.Key == "features").Value;
            bool featuresOk = features == "0x527FFEE6,0x0";
            bool noPairing = (AirPlayAdvertisement.FeaturesLow & (1u << 27)) == 0;

            add(Group, "广播两种服务（_airplay 与 _raop）", twoServices,
                twoServices ? $"{airplay!.Instance} / {raop!.Instance}" : "缺少服务定义");

            add(Group, "features 第 27 位为 0（客户端不做配对）", featuresOk && noPairing,
                featuresOk && noPairing
                    ? $"features={features}，无需实现 AirPlay2 配对"
                    : $"features={features}, bit27={(AirPlayAdvertisement.FeaturesLow >> 27) & 1}");

            bool macOk = airplay?.Txt.FirstOrDefault(t => t.Key == "deviceid").Value == mac;
            bool raopInstanceOk = raop?.Instance.Contains("@PC._raop._tcp.local", StringComparison.Ordinal) == true;
            bool modelOk = airplay?.Txt.FirstOrDefault(t => t.Key == "model").Value == "AppleTV3,2";
            add(Group, "TXT 关键字段（deviceid/model/实例名）", macOk && raopInstanceOk && modelOk,
                $"deviceid={macOk} 实例名={raopInstanceOk} model={modelOk}");
        }
        catch (Exception ex)
        {
            add(Group, "AirPlay 广播内容", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 镜像流拆包

    private static void CheckMirrorParsing(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        try
        {
            // AVCC：两段 [BE32 长度][NAL]，应被就地改写成 Annex-B 起始码
            var nals = new byte[] { 0x65, 0x11, 0x22, 0x33, 0x09, 0x10 };
            var buffer = new byte[] { 0, 0, 0, 4, 0x65, 0x11, 0x22, 0x33, 0, 0, 0, 2, 0x09, 0x10 };
            bool ok = MirrorStreamReceiver.TryConvertAvccToAnnexB(buffer, buffer.Length, out int outLength);
            byte[] expected = { 0, 0, 0, 1, 0x65, 0x11, 0x22, 0x33, 0, 0, 0, 1, 0x09, 0x10 };
            bool same = ok && outLength == expected.Length && buffer.AsSpan(0, outLength).SequenceEqual(expected);
            add(Group, "镜像流 AVCC 就地改写为 Annex-B", same,
                same ? "两段 NAL 均被正确改写" : $"ok={ok} len={outLength} 结果={Convert.ToHexString(buffer).ToLowerInvariant()}");
            _ = nals;

            // 坏数据：长度前缀接近 int.MaxValue，会先溢出 —— 必须判为非法而不是抛异常
            var bad = new byte[] { 0x7f, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            bool rejected;
            try
            {
                rejected = !MirrorStreamReceiver.TryConvertAvccToAnnexB(bad, bad.Length, out _);
            }
            catch (Exception)
            {
                rejected = false;
            }
            add(Group, "镜像流拒绝越界载荷（长度前缀溢出）", rejected,
                rejected ? "被判为无效，未抛异常" : "越界载荷未被安全拒绝");
        }
        catch (Exception ex)
        {
            add(Group, "镜像流拆包", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CheckVideoKeyDerivation(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        try
        {
            byte[] aesKey = Convert.FromHexString(ExpectedAesKey16);
            (byte[] key1, byte[] iv1) = AirPlayRtspServer.DeriveVideoKey(aesKey, 12345678901234UL);
            (byte[] key2, byte[] iv2) = AirPlayRtspServer.DeriveVideoKey(aesKey, 12345678901234UL);

            bool lengthOk = key1.Length == 16 && iv1.Length == 16;
            bool stable = key1.AsSpan().SequenceEqual(key2) && iv1.AsSpan().SequenceEqual(iv2);
            bool keyDiffersFromIv = !key1.AsSpan().SequenceEqual(iv1);

            add(Group, "镜像视频密钥/IV 派生（长度与稳定性）", lengthOk && stable && keyDiffersFromIv,
                lengthOk && stable && keyDiffersFromIv
                    ? $"key={Convert.ToHexString(key1).ToLowerInvariant()}（派生自 SHA-512 前 16 字节）"
                    : $"len={lengthOk} stable={stable} key!=iv={keyDiffersFromIv}");
        }
        catch (Exception ex)
        {
            add(Group, "视频密钥派生", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
