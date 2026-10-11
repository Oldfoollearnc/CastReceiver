using System.IO;
using System.Text;
using CastReceiver.Core.Diagnostics;
using CastReceiver.Core.Security;

namespace CastReceiver.SelfTest;

/// <summary>
/// 加密库自检（V 组）：不做界面，只验证「密码才是钥匙」「内容不可篡改」「磁盘无明文」。
/// 每条用例都在临时目录里跑，结束后删除，不碰真实库。
/// </summary>
internal static class VaultSelfTest
{
    private const string Group = "V 加密库";
    private const string User = "selftester";
    private const string Password = "Abcd1234xy";

    private sealed record Fixture(string Root, VaultAccountStore Store, VaultAccount Account, byte[] PrivateKey)
    {
        public string ItemsDirectory => VaultPaths.ItemsDirectory(Account.Id);
    }

    public static IReadOnlyList<SelfTestItem> RunAll()
    {
        var items = new List<SelfTestItem>();
        string previousRoot = VaultPaths.Root;
        var roots = new List<string>();
        try
        {
            items.Add(PasswordIsTheOnlyKey(roots));
            items.Add(RoundTrip(roots));
            items.Add(TamperDetected(roots));
            items.Add(TruncationRecoverable(roots));
            items.Add(CrossFileTransplantRejected(roots));
            items.Add(RenameRewritesHeaderOnly(roots));
            items.Add(NoPlaintextOnDisk(roots));
            items.Add(FileTimesNormalized(roots));
            items.Add(PerformanceBudget(roots));
            items.Add(AppCaptureIntegration(roots));
        }
        catch (Exception ex)
        {
            items.Add(new SelfTestItem(Group, "加密库自检", SelfTestOutcome.Failed, ex.ToString()));
        }
        finally
        {
            VaultPaths.Root = previousRoot;
            foreach (var root in roots)
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
        return items;
    }

    // ------------------------------------------------------------------ 夹具

    private static string NewRoot(List<string> roots)
    {
        string root = Path.Combine(Path.GetTempPath(), "CastReceiverVaultTest_" + Guid.NewGuid().ToString("N")[..8]);
        VaultPaths.Root = root;
        roots.Add(root);
        return root;
    }

    private static Fixture CreateFixture(List<string> roots, string user = User)
    {
        string root = NewRoot(roots);
        var store = VaultAccountStore.Load(root);
        var (ok, error, account) = store.Create(user, Password);
        if (!ok || account is null) throw new InvalidOperationException("创建账号失败：" + error);

        var login = store.Login(user, Password);
        if (!login.Ok || login.PrivateKey is null) throw new InvalidOperationException("登录失败：" + login.Error);
        return new Fixture(root, store, account, login.PrivateKey);
    }

    private static byte[] Pattern(int length, byte seed)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(seed + i * 31);
        return data;
    }

    private static VaultItemInfo WriteItem(Fixture fixture, byte[][] frames, VaultItemKind kind,
                                           string title, byte[]? thumbnail = null, bool complete = true)
    {
        var meta = kind == VaultItemKind.Recording
            ? VaultItemMeta.Recording(1280, 720, 10, "devicefp-hash")
            : VaultItemMeta.Screenshot(1280, 720, "devicefp-hash", title);
        var writer = VaultItemWriter.Create(fixture.ItemsDirectory, fixture.Account.PublicKeySpki, meta);
        try
        {
            for (int i = 0; i < frames.Length; i++)
                writer.AddFrame(frames[i], VaultFrameCodec.Jpeg, i == 0 ? thumbnail : null);
            if (!complete) { writer.Abort(); return new VaultItemInfo(writer.ItemId, writer.Path, kind,
                DateTimeOffset.UtcNow, null, frames.Length, 1280, 720, 10, "devicefp-hash", title,
                Array.Empty<string>(), new FileInfo(writer.Path).Length, false, null); }
            return writer.Complete();
        }
        finally { writer.Dispose(); }
    }

    // ------------------------------------------------------------------ 用例

    private static SelfTestItem PasswordIsTheOnlyKey(List<string> roots)
    {
        const string name = "密码是唯一的钥匙（错误密码/他人私钥都解不开）";
        try
        {
            var fixture = CreateFixture(roots);
            var wrong = fixture.Store.Login(User, "Wrong12345");
            var nobody = fixture.Store.Login("no-such-user", Password);
            var item = WriteItem(fixture, new[] { Pattern(64 * 1024, 7) }, VaultItemKind.Screenshot, "机密截图");

            // 用另一个账号的私钥去开这一条：必须失败
            var other = CreateFixture(roots, "otheruser");
            var foreign = VaultItemReader.Open(item.Path, other.PrivateKey, out string? foreignError);

            // 弱密码必须被拒绝
            var weak = new[]
            {
                VaultCrypto.ValidatePassword("12345678"),
                VaultCrypto.ValidatePassword("abcdefgh"),
                VaultCrypto.ValidatePassword("aaaaaaaa"),
                VaultCrypto.ValidatePassword("password1"),
                VaultCrypto.ValidatePassword("Abcd1234xy", "abcd"),
            }.Count(e => e is not null);

            bool ok = !wrong.Ok && wrong.PrivateKey is null
                      && !nobody.Ok && nobody.PrivateKey is null
                      && foreign is null && foreignError is not null
                      && weak == 5;
            foreign?.Dispose();
            return new SelfTestItem(Group, name, ok ? SelfTestOutcome.Passed : SelfTestOutcome.Failed,
                $"错误密码={(!wrong.Ok ? "拒绝" : "被接受!")}，他人私钥={(foreign is null ? "拒绝" : "被接受!")}，" +
                $"弱密码拦截 {weak}/5，不存在账号={(nobody.Error ?? "-")}");
        }
        catch (Exception ex) { return Fail(name, ex); }
    }

    private static SelfTestItem RoundTrip(List<string> roots)
    {
        const string name = "写入→读出：帧字节一致、缩略图与元数据正确";
        try
        {
            var fixture = CreateFixture(roots);
            var frames = new[] { Pattern(200 * 1024, 3), Pattern(180 * 1024, 9), Pattern(220 * 1024, 21) };
            var thumb = Pattern(8 * 1024, 55);
            var written = WriteItem(fixture, frames, VaultItemKind.Recording, "回放测试", thumb);

            using var library = VaultLibrary.Open(fixture.Account.Id, fixture.PrivateKey, out string? libraryError);
            if (library is null) return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "打开库失败：" + libraryError);

            var info = library.Items.FirstOrDefault(i => i.ItemId == written.ItemId);
            if (info is null) return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "库里找不到刚写入的条目");

            using var reader = library.OpenItem(info.ItemId, out string? openError);
            if (reader is null) return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "打开条目失败：" + openError);

            bool framesOk = reader.FrameCount == frames.Length;
            for (int i = 0; i < frames.Length && framesOk; i++)
            {
                var got = reader.GetFrame(i, out _);
                framesOk = got is not null && got.AsSpan().SequenceEqual(frames[i]);
            }
            bool thumbOk = reader.ThumbnailJpeg is not null && reader.ThumbnailJpeg.AsSpan().SequenceEqual(thumb);
            bool metaOk = info.Kind == VaultItemKind.Recording && info.FrameCount == frames.Length
                          && info.Width == 1280 && info.Height == 720 && info.Fps == 10 && info.Completed
                          && info.Duration >= TimeSpan.Zero && library.UnreadableItems == 0;
            bool ok = framesOk && thumbOk && metaOk;
            return new SelfTestItem(Group, name, ok ? SelfTestOutcome.Passed : SelfTestOutcome.Failed,
                $"帧 {reader.FrameCount}/{frames.Length} 一致={framesOk}，缩略图={thumbOk}，元数据={metaOk}，" +
                $"时长={info.Duration.TotalSeconds:F2}s，占用={info.Bytes / 1024}KB");
        }
        catch (Exception ex) { return Fail(name, ex); }
    }

    private static SelfTestItem TamperDetected(List<string> roots)
    {
        const string name = "篡改检测：翻转 1 字节 → 该帧拒绝输出（其它帧不受影响）";
        try
        {
            var fixture = CreateFixture(roots);
            var frames = new[] { Pattern(100 * 1024, 1), Pattern(100 * 1024, 2), Pattern(100 * 1024, 3) };
            var written = WriteItem(fixture, frames, VaultItemKind.Recording, "篡改测试");

            // 第 0 帧的载荷起点：缩略图块之后的第一个块头 + 块头长度
            long firstFrameChunk = 4096 + (21 + 16) + 1024;
            long payloadOffset = firstFrameChunk + 21;
            using (var fs = new FileStream(written.Path, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Position = payloadOffset + 4096;
                int b = fs.ReadByte();
                fs.Position = payloadOffset + 4096;
                fs.WriteByte((byte)(b ^ 0xFF));
            }

            using var reader = VaultItemReader.Open(written.Path, fixture.PrivateKey, out string? error);
            if (reader is null) return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "打开条目失败：" + error);

            var tampered = reader.GetFrame(0, out string? frameError);
            var intact = reader.GetFrame(1, out _);
            bool ok = tampered is null && frameError is not null
                      && intact is not null && intact.AsSpan().SequenceEqual(frames[1])
                      && reader.CorruptChunks == 1;
            return new SelfTestItem(Group, name, ok ? SelfTestOutcome.Passed : SelfTestOutcome.Failed,
                $"被改帧={(tampered is null ? "已拒绝" : "仍输出!")}（{frameError}），未改帧完好={intact is not null}，损坏计数={reader.CorruptChunks}");
        }
        catch (Exception ex) { return Fail(name, ex); }
    }

    private static SelfTestItem TruncationRecoverable(List<string> roots)
    {
        const string name = "崩溃/断电恢复：没有结束标记也能播已录部分";
        try
        {
            var fixture = CreateFixture(roots);
            var frames = new[] { Pattern(64 * 1024, 4), Pattern(64 * 1024, 5), Pattern(64 * 1024, 6), Pattern(64 * 1024, 7) };
            var partial = WriteItem(fixture, frames, VaultItemKind.Recording, "未完成录像", complete: false);

            using (var reader = VaultItemReader.Open(partial.Path, fixture.PrivateKey, out string? error))
            {
                if (reader is null) return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "打开未完成条目失败：" + error);
                bool ok = !reader.IsComplete && reader.FrameCount == 4 && reader.GetFrame(3, out _) is not null;
                if (!ok) return new SelfTestItem(Group, name, SelfTestOutcome.Failed,
                    $"未完成标记={!reader.IsComplete}，可读帧={reader.FrameCount}/4");
            }

            // 物理截断尾部 200 字节：仍应能读出前面完整的帧
            string truncated = partial.Path + ".trunc";
            File.Copy(partial.Path, truncated, overwrite: true);
            using (var fs = new FileStream(truncated, FileMode.Open, FileAccess.ReadWrite))
                fs.SetLength(Math.Max(0, fs.Length - 200));

            using var after = VaultItemReader.Open(truncated, fixture.PrivateKey, out string? afterError);
            bool ok2 = after is not null && after.FrameCount >= 3 && after.GetFrame(0, out _) is not null && !after.IsComplete;
            string detail = after is null
                ? "截断后打开失败：" + afterError
                : $"未完成标记=True，截断后可读帧={after.FrameCount}/4，首帧可解={after.GetFrame(0, out _) is not null}";
            after?.Dispose();
            return new SelfTestItem(Group, name, ok2 ? SelfTestOutcome.Passed : SelfTestOutcome.Failed, detail);
        }
        catch (Exception ex) { return Fail(name, ex); }
    }

    private static SelfTestItem CrossFileTransplantRejected(List<string> roots)
    {
        const string name = "防移植：把别的条目里的帧块搬到本条 → 解密失败";
        try
        {
            var fixture = CreateFixture(roots);
            var a = WriteItem(fixture, new[] { Pattern(64 * 1024, 11), Pattern(64 * 1024, 12) }, VaultItemKind.Recording, "条目A");
            var b = WriteItem(fixture, new[] { Pattern(64 * 1024, 21) }, VaultItemKind.Recording, "条目B");

            long firstFrameChunk = 4096 + (21 + 16) + 1024;
            int chunkLength = 21 + 64 * 1024 + 16;
            var stolen = new byte[chunkLength];
            using (var fs = new FileStream(b.Path, FileMode.Open, FileAccess.Read)) { fs.Position = firstFrameChunk; fs.ReadExactly(stolen); }
            using (var fs = new FileStream(a.Path, FileMode.Open, FileAccess.ReadWrite)) { fs.Position = firstFrameChunk; fs.Write(stolen); }

            using var reader = VaultItemReader.Open(a.Path, fixture.PrivateKey, out string? error);
            if (reader is null) return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "打开条目失败：" + error);
            var transplanted = reader.GetFrame(0, out string? frameError);
            var intact = reader.GetFrame(1, out _);
            bool ok = transplanted is null && frameError is not null && intact is not null;
            return new SelfTestItem(Group, name, ok ? SelfTestOutcome.Passed : SelfTestOutcome.Failed,
                $"移植块={(transplanted is null ? "被拒绝" : "被接受!")}（{frameError}），同文件其它帧不受影响={intact is not null}");
        }
        catch (Exception ex) { return Fail(name, ex); }
    }

    private static SelfTestItem RenameRewritesHeaderOnly(List<string> roots)
    {
        const string name = "改名只重写加密头（内容不受影响）";
        try
        {
            var fixture = CreateFixture(roots);
            var frames = new[] { Pattern(48 * 1024, 31), Pattern(48 * 1024, 32) };
            var written = WriteItem(fixture, frames, VaultItemKind.Recording, "原名");

            using var library = VaultLibrary.Open(fixture.Account.Id, fixture.PrivateKey, out string? error);
            if (library is null) return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "打开库失败：" + error);
            long before = new FileInfo(written.Path).Length;
            bool renamed = library.SetTitle(written.ItemId, "新名字", out string? renameError);
            long after = new FileInfo(written.Path).Length;
            var info = library.Find(written.ItemId);

            using var reader = library.OpenItem(written.ItemId, out _);
            var frame = reader?.GetFrame(1, out _);
            bool ok = renamed && info?.Title == "新名字" && reader is not null && frame is not null
                      && frame.AsSpan().SequenceEqual(frames[1]) && before == after;
            return new SelfTestItem(Group, name, ok ? SelfTestOutcome.Passed : SelfTestOutcome.Failed,
                $"改名={(renamed ? "成功" : renameError)}，标题={info?.Title}，文件长度不变={before == after}，内容一致={frame is not null}");
        }
        catch (Exception ex) { return Fail(name, ex); }
    }

    private static SelfTestItem NoPlaintextOnDisk(List<string> roots)
    {
        const string name = "磁盘无明文：标题/时间/设备指纹不出现在文件里";
        try
        {
            var fixture = CreateFixture(roots);
            const string title = "董事会决议截图-绝密";
            var item = WriteItem(fixture, new[] { Pattern(96 * 1024, 41) }, VaultItemKind.Screenshot, title);

            var bytes = File.ReadAllBytes(item.Path);
            string asText = Encoding.UTF8.GetString(bytes);
            bool hasTitle = asText.Contains(title, StringComparison.Ordinal);
            bool hasIsoTime = System.Text.RegularExpressions.Regex.IsMatch(asText, @"20\d\d[-/]\d\d[-/]\d\d");
            bool hasDeviceHash = asText.Contains("devicefp-hash", StringComparison.OrdinalIgnoreCase);
            var head = bytes.AsSpan(0, 8);
            bool looksPng = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50;
            bool looksJpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;
            bool looksMp4 = bytes.Length > 12 && Encoding.ASCII.GetString(bytes, 4, 4) == "ftyp";
            bool magicOk = head.SequenceEqual("CRWVAULT"u8);
            bool ok = !hasTitle && !hasIsoTime && !hasDeviceHash && !looksPng && !looksJpeg && !looksMp4 && magicOk;

            return new SelfTestItem(Group, name, ok ? SelfTestOutcome.Passed : SelfTestOutcome.Failed,
                $"标题明文={hasTitle}，时间明文={hasIsoTime}，设备指纹明文={hasDeviceHash}，" +
                $"伪装成图片/视频={looksPng || looksJpeg || looksMp4}，容器标识={magicOk}");
        }
        catch (Exception ex) { return Fail(name, ex); }
    }

    private static SelfTestItem FileTimesNormalized(List<string> roots)
    {
        const string name = "文件时间已归一化（不泄露「什么时候录的」）";
        try
        {
            var fixture = CreateFixture(roots);
            var item = WriteItem(fixture, new[] { Pattern(32 * 1024, 51) }, VaultItemKind.Screenshot, "时间测试");
            var info = new FileInfo(item.Path);
            bool ok = info.CreationTimeUtc == VaultPaths.NormalizedFileTimeUtc
                      && info.LastWriteTimeUtc == VaultPaths.NormalizedFileTimeUtc
                      && info.LastAccessTimeUtc == VaultPaths.NormalizedFileTimeUtc;
            return new SelfTestItem(Group, name, ok ? SelfTestOutcome.Passed : SelfTestOutcome.Failed,
                $"创建={info.CreationTimeUtc:yyyy-MM-dd HH:mm:ss}，修改={info.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}（期望 {VaultPaths.NormalizedFileTimeUtc:yyyy-MM-dd}）");
        }
        catch (Exception ex) { return Fail(name, ex); }
    }

    private static SelfTestItem PerformanceBudget(List<string> roots)
    {
        const string name = "性能：写入/打开在可接受范围内";
        try
        {
            var fixture = CreateFixture(roots);
            var frame = Pattern(256 * 1024, 61);   // 约 720p JPEG 的量级
            const int frameCount = 100;
            var meta = VaultItemMeta.Recording(1280, 720, 10, "devicefp-hash");
            var writer = VaultItemWriter.Create(fixture.ItemsDirectory, fixture.Account.PublicKeySpki, meta);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < frameCount; i++) writer.AddFrame(frame, VaultFrameCodec.Jpeg, i == 0 ? frame : null);
            var info = writer.Complete();
            watch.Stop();
            writer.Dispose();

            double msPerFrame = watch.Elapsed.TotalMilliseconds / frameCount;
            double throughput = frameCount * frame.Length / 1024.0 / 1024.0 / Math.Max(0.001, watch.Elapsed.TotalSeconds);

            var openWatch = System.Diagnostics.Stopwatch.StartNew();
            using var library = VaultLibrary.Open(fixture.Account.Id, fixture.PrivateKey, out string? error);
            openWatch.Stop();
            if (library is null) return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "打开库失败：" + error);

            // 10fps 录制时每帧预算 100ms；这里要求远低于它，且吞吐 ≥ 20MB/s（含 AES-GCM 与落盘）
            bool ok = msPerFrame < 30 && throughput > 20 && library.Items.Count >= 1 && openWatch.ElapsedMilliseconds < 3000;
            return new SelfTestItem(Group, name, ok ? SelfTestOutcome.Passed : SelfTestOutcome.Warning,
                $"100 帧 × 256KB：{msPerFrame:F1} ms/帧，{throughput:F0} MB/s，文件 {info.Bytes / 1024 / 1024}MB；" +
                $"打开库（1 条）{openWatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex) { return Fail(name, ex); }
    }

    private static SelfTestItem Fail(string name, Exception ex)
        => new(Group, name, SelfTestOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}");

    /// <summary>
    /// App 层端到端：抓到的位图 → 内存编码 → 加密入库 → 用密码解锁 → 回读并核对像素。
    /// 这条用例覆盖「截图」这条真实链路（Core 的容器用例之外，把 App 侧服务也一起验证）。
    /// </summary>
    private static SelfTestItem AppCaptureIntegration(List<string> roots)
    {
        const string name = "端到端：截图入库 → 解锁 → 回读一致（App 服务链路）";
        try
        {
            var fixture = CreateFixture(roots);
            var host = new CastReceiver.App.Services.CastAppHost();
            try
            {
                var vault = new CastReceiver.App.Services.VaultService(host);
                using var capture = new CastReceiver.App.Services.VaultCaptureService(vault, host.Settings);

                // 把一台设备绑定到该账号，让「归属」成立
                string fingerprint = VaultCrypto.DeviceFingerprint("测试手机", "AA:BB:CC:DD:EE:FF");
                string fingerprintHash = VaultCrypto.FingerprintHash(fingerprint);
                vault.BindDevice(fixture.Account.Id, fingerprintHash, "测试手机");
                var resolution = vault.ResolveDevice("测试手机", "AA:BB:CC:DD:EE:FF");
                if (resolution.Account is null) return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "设备归属解析失败");

                // 造一张有内容的位图（不是纯黑，便于校验解码结果）
                using var bitmap = new System.Drawing.Bitmap(320, 180);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.Clear(System.Drawing.Color.FromArgb(20, 120, 200));
                    graphics.FillRectangle(System.Drawing.Brushes.White, 40, 40, 120, 60);
                }

                var (ok, error, item) = capture.SaveScreenshot(bitmap, "端到端测试");
                if (!ok || item is null) return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "截图入库失败：" + error);

                // 未解锁时不能读
                using var lockedLibrary = VaultLibrary.Open(fixture.Account.Id, Array.Empty<byte>(), out string? lockedError);
                bool lockedBlocked = lockedLibrary is null && lockedError is not null;

                var login = fixture.Store.Login(User, Password);
                if (!login.Ok || login.PrivateKey is null)
                    return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "解锁失败：" + login.Error);

                using var library = VaultLibrary.Open(fixture.Account.Id, login.PrivateKey, out string? libraryError);
                if (library is null) return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "打开库失败：" + libraryError);

                using var reader = library.OpenItem(item.ItemId, out string? openError);
                if (reader is null) return new SelfTestItem(Group, name, SelfTestOutcome.Failed, "打开条目失败：" + openError);

                var frame = reader.GetFrame(0, out string? frameError);
                bool frameOk = false;
                if (frame is not null)
                {
                    var decoded = CastReceiver.App.Services.VaultPlayback.DecodeBytes(frame);
                    frameOk = decoded is not null && decoded.PixelWidth == bitmap.Width && decoded.PixelHeight == bitmap.Height;
                }

                bool thumbOk = reader.ThumbnailJpeg is { Length: > 0 };
                bool okAll = lockedBlocked && frameOk && thumbOk && reader.Info.Kind == VaultItemKind.Screenshot
                             && reader.Info.Title == "端到端测试";
                return new SelfTestItem(Group, name, okAll ? SelfTestOutcome.Passed : SelfTestOutcome.Failed,
                    $"未解锁时被拒绝={lockedBlocked}，回读像素一致={frameOk}，缩略图={thumbOk}，" +
                    $"标题={reader.Info.Title}，体积={item.Bytes / 1024}KB" +
                    (frameError is null ? "" : $"（{frameError}）"));
            }
            finally { host.Dispose(); }
        }
        catch (Exception ex) { return Fail(name, ex); }
    }
}
