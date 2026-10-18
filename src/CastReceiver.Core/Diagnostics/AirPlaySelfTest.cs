using CastReceiver.Core.AirPlay;
using CastReceiver.Core.AirPlay.Discovery;
using CastReceiver.Core.AirPlay.FairPlay;
using CastReceiver.Core.AirPlay.Mirror;
using CastReceiver.Core.AirPlay.Plist;
using CastReceiver.Core.AirPlay.Rtsp;
using CastReceiver.Core.Infrastructure;

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

    /// <summary>非 IDR 的一帧（AUD 9 + 非 IDR slice 1）。</summary>
    private static readonly byte[] NonIdrFrame = { 0, 0, 0, 1, 0x09, 0x10, 0, 0, 0, 1, 0x41, 0x9a };

    /// <summary>IDR 的一帧（SPS 7 + PPS 8 + IDR slice 5）——注入过配置的帧必须也算关键帧。</summary>
    private static readonly byte[] IdrFrame =
        { 0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68, 0xce, 0, 0, 0, 1, 0x65, 0x88 };

    internal static void Run(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        ArgumentNullException.ThrowIfNull(add);

        CheckFpSetup(add);
        CheckFairPlayDecrypt(add);
        CheckAesCtr(add);
        CheckPlist(add);
        CheckDiscovery(add);
        CheckMirrorParsing(add);
        CheckKeyFrameDetection(add);
        CheckFrameGate(add);
        CheckTimestampNormalizer(add);
        CheckQualityProfile(add);
        CheckVideoKeyDerivation(add);
    }

    // ---------------------------------------------------------------- 时间戳规整

    private static void CheckTimestampNormalizer(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        try
        {
            var clock = new MirrorTimestampNormalizer(fps: 30);
            const ulong t0 = 100UL << 32;
            ulong step = (ulong)(4294967296.0 / 30.0);

            TimeSpan first = clock.Map(t0);
            TimeSpan second = clock.Map(t0 + step);
            TimeSpan third = clock.Map(t0 + step * 2);

            bool firstOk = first == TimeSpan.Zero;
            bool cadenceOk = Math.Abs(second.TotalMilliseconds - 33.33) < 0.5
                             && Math.Abs(third.TotalMilliseconds - 66.67) < 0.8;

            // 时间戳倒退：必须继续前进 1ms，并标成不连续
            TimeSpan backward = clock.Map(t0 + step - 1000);
            bool backwardOk = backward > third && clock.LastWasDiscontinuity;

            // 向前大跳 3 秒：重新锚定到"上一帧 + 一个标称帧长"，不能把 3 秒后的时间交给播放器；
            // 但**不能**标不连续 —— 静止画面停顿是正常现象，标了会让解码器白白重置参考链（花屏）。
            TimeSpan jumped = clock.Map(t0 + step * 2 + (3UL << 32));
            bool jumpOk = !clock.LastWasDiscontinuity
                          && jumped > backward
                          && jumped - backward <= TimeSpan.FromMilliseconds(40);

            clock.Reset();
            bool resetOk = clock.Map(t0 + (5UL << 32)) == TimeSpan.Zero;

            bool ok = firstOk && cadenceOk && backwardOk && jumpOk && resetOk;
            add(Group, "时间戳规整：首帧归零 / 单调 / 倒退标不连续 / 前跳只压缩不重置", ok,
                $"首帧 {first.TotalMilliseconds:F0}ms，第二帧 {second.TotalMilliseconds:F1}ms，"
                + $"倒退后 {backward.TotalMilliseconds:F1}ms（不连续={backwardOk}），"
                + $"大跳后 {jumped.TotalMilliseconds:F1}ms（不连续={clock.LastWasDiscontinuity}）");
        }
        catch (Exception ex)
        {
            add(Group, "时间戳规整", SelfTestOutcome.Failed, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 自动画质

    private static void CheckQualityProfile(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        try
        {
            var wifi5 = new NetInfo.WlanLink("test-5g", 5.0, 36, 866, 866, 80, "802.11ax");
            var wifi24 = new NetInfo.WlanLink("test-24g", 2.4, 6, 72, 72, 60, "802.11n");

            AirPlayQualityProfile low = AirPlayQualityProfile.ForSettings("LowLatency", wifi5, false);
            AirPlayQualityProfile balanced = AirPlayQualityProfile.ForSettings("Balanced", wifi5, false);
            AirPlayQualityProfile high5 = AirPlayQualityProfile.ForSettings("HighQuality", wifi5, false);
            AirPlayQualityProfile high24 = AirPlayQualityProfile.ForSettings("HighQuality", wifi24, false);
            AirPlayQualityProfile smooth = AirPlayQualityProfile.ForSettings("HighQuality", wifi5, true);

            bool lowOk = low is { Width: 1280, Height: 720, MaxFPS: 30 };
            bool balancedOk = balanced is { Width: 1920, Height: 1080, MaxFPS: 30 };
            bool highOk = high5 is { Width: 1920, Height: 1080, MaxFPS: 30 };
            bool weakOk = high24 is { Width: 1280, Height: 720, MaxFPS: 30 };
            bool smoothOk = smooth is { Width: 1280, Height: 720, MaxFPS: 30 };

            var bad = new AirPlaySessionHealth(30, 900, 60, 1, 1.2);
            var good = new AirPlaySessionHealth(30, 900, 0, 0, 0.4);
            bool badOk = bad.IsBad && AirPlayQualityProfile.Evaluate(bad, false) == AirPlayQualityDecision.Downgrade;
            bool goodOk = good.IsGood && AirPlayQualityProfile.Evaluate(good, true) == AirPlayQualityDecision.Upgrade;
            bool keepOk = AirPlayQualityProfile.Evaluate(good, false) == AirPlayQualityDecision.Keep;

            // /info 的 maxFPS 必须真的写进 plist（手机读的是这个字段）
            var options = new AirPlayOptions { Width = 1280, Height = 720, MaxFPS = 24 };
            Dictionary<string, object?> info = AirPlayInfoPlist.Build(options, options.PkHex, 7000);
            bool maxFpsOk = info.TryGetValue("displays", out object? displaysObj)
                            && displaysObj is List<object?> { Count: > 0 } displays
                            && displays[0] is Dictionary<string, object?> display
                            && display.TryGetValue("maxFPS", out object? fps)
                            && fps is 24L
                            && info.TryGetValue("maxFPS", out object? rootFps) && rootFps is 24L;

            bool ok = lowOk && balancedOk && highOk && weakOk && smoothOk && badOk && goodOk && keepOk && maxFpsOk;
            add(Group, "自动画质：档位 × 2.4G/5G × 上次健康度（含 /info maxFPS）", ok,
                $"低延迟 {low.Spec}，均衡 {balanced.Spec}，高画质 {high5.Spec}，2.4G 高画质 {high24.Spec}，"
                + $"强制流畅 {smooth.Spec}，卡顿={bad.IsBad}，健康={good.IsGood}，maxFPS 写入={maxFpsOk}");
        }
        catch (Exception ex)
        {
            add(Group, "自动画质", SelfTestOutcome.Failed, ex.Message);
        }
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

            // 长载荷（10000 字节，跨多个密钥流块）：与"每 16 字节一个计数器块"的朴素参照实现
            // 逐字节比对。这一条专门守住"RefillKeystream 改成一次算 4096 字节"之后语义没变。
            byte[] plain = new byte[10000];
            for (int i = 0; i < plain.Length; i++)
            {
                plain[i] = (byte)(i * 31 + 7);
            }

            byte[] reference = NaiveCtr(key, iv, plain);
            byte[] fast = (byte[])plain.Clone();
            using (var ctr = new Aes128Ctr(key, iv))
            {
                ctr.Transform(fast);
            }
            bool longOk = fast.AsSpan().SequenceEqual(reference);

            // 再按"每段长度都跨块边界"的方式分段调用一次，确认分段与整段等价
            byte[] splitLong = (byte[])plain.Clone();
            using (var ctr = new Aes128Ctr(key, iv))
            {
                int[] cuts = { 0, 4095, 4096, 4097, 8000, splitLong.Length };
                for (int i = 0; i + 1 < cuts.Length; i++)
                {
                    ctr.Transform(splitLong.AsSpan(cuts[i], cuts[i + 1] - cuts[i]));
                }
            }
            bool splitLongOk = splitLong.AsSpan().SequenceEqual(reference);

            add(Group, "AES-128-CTR 长载荷跨块一致（10000 字节，含跨块分段）", longOk && splitLongOk,
                longOk && splitLongOk
                    ? "整段与分段均与朴素 CTR 参照实现逐字节一致"
                    : $"整段一致={longOk}，跨块分段一致={splitLongOk}");
        }
        catch (Exception ex)
        {
            add(Group, "AES-128-CTR", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 朴素的 CTR 参照实现：每 16 字节一个计数器块、大端 +1（与 OpenSSL 的
    /// <c>EVP_aes_128_ctr</c> 语义一致）。它自己由上面的 NIST 已知答案守着，
    /// 这里用它当"独立实现"来验产品里那份"一次算 4096 字节"的快版本。
    /// </summary>
    private static byte[] NaiveCtr(byte[] key, byte[] iv, byte[] plain)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Mode = System.Security.Cryptography.CipherMode.ECB;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        aes.Key = key;
        using var encryptor = aes.CreateEncryptor();

        var counter = (byte[])iv.Clone();
        var block = new byte[16];
        var output = new byte[plain.Length];
        for (int offset = 0; offset < plain.Length; offset += 16)
        {
            encryptor.TransformBlock(counter, 0, 16, block, 0);
            for (int i = 0; i < 16 && offset + i < plain.Length; i++)
            {
                output[offset + i] = (byte)(plain[offset + i] ^ block[i]);
            }
            for (int i = 15; i >= 0; i--)
            {
                if (++counter[i] != 0)
                {
                    break;
                }
            }
        }
        return output;
    }

    // ---------------------------------------------------------------- 关键帧与直播丢帧策略

    private static void CheckKeyFrameDetection(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        try
        {
            bool nonIdrOk = !MirrorStreamReceiver.IsKeyFrame(NonIdrFrame);
            bool idrOk = MirrorStreamReceiver.IsKeyFrame(IdrFrame);

            // 坏数据不能抛异常（长度不足 4、没有起始码）
            bool shortOk, junkOk;
            try { shortOk = !MirrorStreamReceiver.IsKeyFrame(new byte[] { 0, 0, 0 }); }
            catch (Exception) { shortOk = false; }
            try { junkOk = !MirrorStreamReceiver.IsKeyFrame(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }); }
            catch (Exception) { junkOk = false; }

            bool ok = nonIdrOk && idrOk && shortOk && junkOk;
            add(Group, "关键帧识别（含注入 SPS/PPS 的帧，坏数据不崩）", ok,
                ok ? "非 IDR 判否、含 SPS+PPS 的 IDR 判是、坏数据安全返回 false"
                   : $"非IDR={nonIdrOk} IDR={idrOk} 短数据={shortOk} 无起始码={junkOk}");
        }
        catch (Exception ex)
        {
            add(Group, "关键帧识别", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CheckFrameGate(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var gate = new MirrorFrameGate(capacity: 4, maxWaitForKeyFrameSeconds: 2.0);

            // ① 还有余量：正常收下
            FrameDisposition d1 = gate.Offer(NonIdrFrame, 0, start, out bool c1);
            // ② 积压到上限且这一帧不是关键帧：丢弃、清空积压、转入"等关键帧"
            FrameDisposition d2 = gate.Offer(NonIdrFrame, 4, start, out bool c2);
            bool waiting = gate.WaitingForKeyFrame;
            // ③ 等待期间的非关键帧继续丢（不清积压，积压已经空了）
            FrameDisposition d3 = gate.Offer(NonIdrFrame, 1, start.AddMilliseconds(200), out bool c3);
            // ④ 关键帧到达：清空积压后收下，退出等待
            FrameDisposition d4 = gate.Offer(IdrFrame, 2, start.AddMilliseconds(400), out bool c4);
            bool resumed = !gate.WaitingForKeyFrame;

            // ⑤ 超时兜底：进入等待后 2 秒仍没有关键帧 → 丢这一帧、退出等待
            var timeoutGate = new MirrorFrameGate(capacity: 4, maxWaitForKeyFrameSeconds: 2.0);
            timeoutGate.Offer(NonIdrFrame, 4, start, out _);                 // 进入等待
            FrameDisposition d5 = timeoutGate.Offer(NonIdrFrame, 1, start.AddSeconds(2.1), out bool c5);
            bool timeoutOk = d5 == FrameDisposition.DropTimeoutProbe && !timeoutGate.WaitingForKeyFrame;
            // ⑥ 超时之后的下一帧：正常收下（画面恢复推进，不再一直冻着）
            FrameDisposition d6 = timeoutGate.Offer(NonIdrFrame, 0, start.AddSeconds(2.2), out _);

            bool ok = d1 == FrameDisposition.Accept
                      && d2 == FrameDisposition.DropOverflow && c2 && waiting
                      && d3 == FrameDisposition.DropWaitingKey && !c3
                      && d4 == FrameDisposition.AcceptAfterResync && c4 && resumed
                      && timeoutOk && c5 && d6 == FrameDisposition.Accept;

            add(Group, "直播丢帧策略：积压→等关键帧→恢复（含超时兜底）", ok,
                ok ? "超容量转等关键帧、等待期丢非关键帧、IDR 到达即恢复、2 秒超时兜底"
                   : $"d1={d1} d2={d2}(清积压={c2},等待={waiting}) d3={d3}(清积压={c3}) "
                     + $"d4={d4}(清积压={c4},已恢复={resumed}) 超时={timeoutOk}(清积压={c5}) d6={d6}");
        }
        catch (Exception ex)
        {
            add(Group, "直播丢帧策略", false, $"{ex.GetType().Name}: {ex.Message}");
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
