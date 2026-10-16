using System.Drawing;
using CastReceiver.Core.Infrastructure;
using CastReceiver.Core.Security;

namespace CastReceiver.App.Services;

/// <summary>
/// 把截屏与录屏写进加密库（App 层：负责抓屏后的编码，Core 层负责加密与容器）。
///
/// 与旧实现的区别（这是本次隐私加固的核心）：
///   旧：截图存成明文 PNG（文件名带时间）、录屏把明文 JPEG 帧写进 %TEMP% 再合成明文 MP4；
///   新：帧在内存里编码后立刻加密写库，磁盘上不存在任何明文帧，也不再有明文中间目录。
/// </summary>
public sealed class VaultCaptureService : IDisposable
{
    private readonly VaultService _vault;
    private readonly CastSettings _settings;
    private readonly object _gate = new();

    private VaultItemWriter? _writer;
    private int _fps = 10;
    private int _maxFrames;
    private DateTime _startedUtc;
    private int _droppedFrames;
    private string? _lastFrameError;

    public VaultCaptureService(VaultService vault, CastSettings settings)
    {
        _vault = vault;
        _settings = settings;
    }

    public bool IsRecording { get { lock (_gate) return _writer is not null; } }

    public int FrameCount { get { lock (_gate) return _writer?.FrameCount ?? 0; } }

    public int DroppedFrames { get { lock (_gate) return _droppedFrames; } }

    /// <summary>最近一次取帧失败的原因（界面用来给出可操作的提示）。</summary>
    public string? LastFrameError { get { lock (_gate) return _lastFrameError; } }

    public DateTime StartedUtc { get { lock (_gate) return _startedUtc; } }

    public TimeSpan MaxDuration => TimeSpan.FromMinutes(Math.Clamp(_settings.RecordMaxMinutes, 1, 240));

    public int Fps => Math.Clamp(_settings.RecordFps switch { "5" => 5, "15" => 15, "20" => 20, _ => 10 }, 1, 30);

    public bool ReachedLimit { get { lock (_gate) return _writer is not null && _writer.FrameCount >= _maxFrames; } }

    // ------------------------------------------------------------------ 截图

    /// <summary>截屏：内存 PNG + 缩略图 → 加密条目（原图不落明文）。</summary>
    public (bool Ok, string? Error, VaultItemInfo? Item) SaveScreenshot(Bitmap bitmap, string? title = null)
    {
        var account = _vault.ActiveAccount;
        if (account is null) return (false, "这台设备还没有归属账号", null);
        try
        {
            string fingerprintHash = _vault.ActiveBinding?.FingerprintHash ?? "";
            var meta = VaultItemMeta.Screenshot(bitmap.Width, bitmap.Height, fingerprintHash, title);
            using var writer = VaultItemWriter.Create(VaultPaths.ItemsDirectory(account.Id), account.PublicKeySpki, meta);
            writer.AddFrame(ImageCodec.EncodePng(bitmap), VaultFrameCodec.Png, ImageCodec.MakeThumbnailJpeg(bitmap));
            var info = writer.Complete();
            _vault.Host.Log.Info("vault", "已加密保存一条截图");
            return (true, null, info);
        }
        catch (Exception ex)
        {
            _vault.Host.Log.Warn("vault", $"截图入库失败：{ex.Message}");
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 把**手机自己截的屏**（adb 取回来的 PNG 原始字节）写进加密库。
    ///
    /// 为什么不落成明文 PNG 文件：本程序从 v1.1 起就不再把任何画面写成明文（见类注释）。
    /// 手机截屏走同一套：原字节直接加密入库，**不重新编码**（保真），查看同样要账号密码。
    /// </summary>
    public (bool Ok, string? Error, VaultItemInfo? Item) SavePhoneScreenshot(byte[] png, int width, int height, string? title = null)
    {
        var account = _vault.ActiveAccount;
        if (account is null) return (false, "这台设备还没有归属账号", null);
        if (png is null || png.Length == 0) return (false, "手机没有返回图片数据", null);
        try
        {
            string fingerprintHash = _vault.ActiveBinding?.FingerprintHash ?? "";
            var meta = VaultItemMeta.Screenshot(width, height, fingerprintHash, title);
            using var writer = VaultItemWriter.Create(VaultPaths.ItemsDirectory(account.Id), account.PublicKeySpki, meta);
            writer.AddFrame(png, VaultFrameCodec.Png);
            var info = writer.Complete();
            _vault.Host.Log.Info("vault", $"已加密保存一条手机截屏（{width}×{height}）");
            return (true, null, info);
        }
        catch (Exception ex)
        {
            _vault.Host.Log.Warn("vault", $"手机截屏入库失败：{ex.Message}");
            return (false, ex.Message, null);
        }
    }

    // ------------------------------------------------------------------ 录制

    public (bool Ok, string? Error) StartRecording(int width, int height)
    {
        lock (_gate)
        {
            if (_writer is not null) return (true, null);
            var account = _vault.ActiveAccount;
            if (account is null) return (false, "这台设备还没有归属账号");

            try
            {
                int fps = Fps;
                var meta = VaultItemMeta.Recording(width, height, fps,
                    _vault.ActiveBinding?.FingerprintHash ?? "");
                _writer = VaultItemWriter.Create(VaultPaths.ItemsDirectory(account.Id), account.PublicKeySpki, meta);
                _fps = fps;
                _maxFrames = (int)(MaxDuration.TotalSeconds * fps);
                _startedUtc = DateTime.UtcNow;
                _droppedFrames = 0;
                _vault.Host.Log.Info("vault", $"开始录制（加密写入，{fps}fps，上限 {MaxDuration.TotalMinutes:F0} 分钟）");
                return (true, null);
            }
            catch (Exception ex)
            {
                _vault.Host.Log.Warn("vault", $"开始录制失败：{ex.Message}");
                return (false, ex.Message);
            }
        }
    }

    /// <summary>追加一帧（应在后台线程调用：编码 + 加密 + 落盘）。</summary>
    public void AppendFrame(Bitmap bitmap)
    {
        lock (_gate)
        {
            var writer = _writer;
            if (writer is null) return;
            if (writer.FrameCount >= _maxFrames) { _droppedFrames++; return; }

            try
            {
                byte[] encoded = ImageCodec.EncodeJpeg(bitmap, _settings.RecordJpegQuality);
                byte[]? thumbnail = null;
                if (writer.FrameCount == 0) thumbnail = ImageCodec.MakeThumbnailJpeg(bitmap);
                writer.AddFrame(encoded, VaultFrameCodec.Jpeg, thumbnail);
                _lastFrameError = null;
            }
            catch (Exception ex)
            {
                _droppedFrames++;
                NoteFrameFailureLocked(ex.Message);
            }
        }
    }

    /// <summary>记录一次取帧/写帧失败（只在第一条与每 50 条时写日志，避免刷屏）。</summary>
    public void NoteFrameFailure(string reason)
    {
        lock (_gate) NoteFrameFailureLocked(reason);
    }

    private void NoteFrameFailureLocked(string reason)
    {
        _lastFrameError = reason;
        if (_droppedFrames == 1 || _droppedFrames % 50 == 0)
            _vault.Host.Log.Warn("vault", $"录制取帧失败（第 {_droppedFrames} 次）：{reason}");
    }

    public (bool Ok, string? Error, VaultItemInfo? Item) StopRecording()
    {
        VaultItemWriter? writer;
        lock (_gate)
        {
            writer = _writer;
            _writer = null;
        }
        if (writer is null) return (false, "当前没有在录制", null);

        try
        {
            if (writer.FrameCount == 0)
            {
                // 一帧都没录到：不留空条目（旧实现会留下一个 5KB 的「未完成」垃圾条目）
                writer.Discard();
                string reason = _lastFrameError is null ? "没有采集到任何画面" : "没有采集到任何画面（" + _lastFrameError + "）";
                _vault.Host.Log.Warn("vault", "录制未产生任何帧，已丢弃空条目：" + (_lastFrameError ?? "抓屏返回空"));
                return (false, reason, null);
            }
            var info = writer.Complete();
            writer.Dispose();
            _vault.Host.Log.Info("vault", $"已加密保存一条录像（{info.FrameCount} 帧）");
            return (true, null, info);
        }
        catch (Exception ex)
        {
            try { writer.Dispose(); } catch { }
            _vault.Host.Log.Warn("vault", $"结束录制失败：{ex.Message}");
            return (false, ex.Message, null);
        }
    }

    /// <summary>放弃录制：保留已写入的帧（库里显示为「未完成」，可播已录部分）。</summary>
    public void AbortRecording()
    {
        VaultItemWriter? writer;
        lock (_gate) { writer = _writer; _writer = null; }
        try { writer?.Abort(); } catch { }
    }

    public void Dispose() => AbortRecording();
}
