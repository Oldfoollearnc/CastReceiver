using System.Buffers.Binary;
using System.Text.Json;

namespace CastReceiver.Core.Security;

/// <summary>
/// 写入一条加密条目（截图或录像）。
///
/// 关键性质：
///   1. 只需要账号「公钥」就能写 —— 所以录制全程不需要密码、不需要解锁；
///   2. 帧在内存里编码后立刻加密再落盘 —— 磁盘上从头到尾没有明文帧（旧实现会把明文 JPEG 写进 %TEMP%）；
///   3. 未调用 <see cref="Complete"/> 就结束（崩溃/断电/强杀）时，文件仍是「未完成的录像」，
///      已写入的帧照常可播 —— 不会出现半截文件把整库带崩的情况。
/// </summary>
public sealed class VaultItemWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly Guid _itemId;
    private readonly byte[] _contentKey;
    private readonly string _publicKeySpki;
    private readonly VaultItemMeta _meta;
    private readonly string _path;

    private int _frameCount;
    private int _nextChunkIndex = VaultFormat.FirstFrameChunkIndex;
    private bool _thumbnailWritten;
    private int _thumbnailPayloadLength;
    private byte[] _thumbnailNonce = new byte[VaultCrypto.NonceSize];
    private byte[] _headerNonce = new byte[VaultCrypto.NonceSize];
    private int _headerJsonLength;
    private bool _completed;
    private bool _disposed;
    private DateTimeOffset? _endUtc;

    public Guid ItemId => _itemId;
    public string Path => _path;
    public int FrameCount => _frameCount;
    public VaultItemMeta Meta => _meta;
    public long BytesWritten => _stream.Length;

    private VaultItemWriter(FileStream stream, Guid itemId, byte[] contentKey, string publicKeySpki,
                            VaultItemMeta meta, string path)
    {
        _stream = stream;
        _itemId = itemId;
        _contentKey = contentKey;
        _publicKeySpki = publicKeySpki;
        _meta = meta;
        _path = path;
    }

    public static VaultItemWriter Create(string itemsDirectory, string publicKeySpki, VaultItemMeta meta)
    {
        if (string.IsNullOrWhiteSpace(publicKeySpki)) throw new ArgumentException("缺少账号公钥", nameof(publicKeySpki));
        Directory.CreateDirectory(itemsDirectory);
        var itemId = Guid.NewGuid();
        string path = System.IO.Path.Combine(itemsDirectory, itemId.ToString("N") + VaultPaths.ItemExtension);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite, 1 << 16);
        var contentKey = VaultCrypto.RandomBytes(VaultCrypto.KeySize);
        var writer = new VaultItemWriter(stream, itemId, contentKey, publicKeySpki, meta, path);
        try
        {
            writer.WriteHeaderChunk(completed: false);
            writer.WriteFixedArea(completed: false);
            stream.Flush(true);
            return writer;
        }
        catch
        {
            VaultCrypto.Zero(contentKey);
            try { stream.Dispose(); } catch { }
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    // ------------------------------------------------------------------ 固定区与头部

    private void WriteFixedArea(bool completed)
    {
        var area = new byte[VaultFormat.FixedAreaSize];
        VaultFormat.Magic.CopyTo(area);
        BinaryPrimitives.WriteInt16LittleEndian(area.AsSpan(VaultFormat.OffVersion), VaultFormat.Version);

        byte flags = 0;
        if (completed) flags |= VaultFormat.FlagCompleted;
        if (_thumbnailWritten) flags |= VaultFormat.FlagHasThumbnail;
        area[VaultFormat.OffFlags] = flags;

        _itemId.TryWriteBytes(area.AsSpan(VaultFormat.OffItemId, 16));
        var wrapped = VaultCrypto.WrapItemKey(_publicKeySpki, _contentKey);
        wrapped.CopyTo(area.AsSpan(VaultFormat.OffWrappedKey, VaultCrypto.WrappedKeySize));
        _headerNonce.CopyTo(area.AsSpan(VaultFormat.OffHeaderNonce, VaultCrypto.NonceSize));
        BinaryPrimitives.WriteInt32LittleEndian(area.AsSpan(VaultFormat.OffHeaderLength), _headerJsonLength);
        _thumbnailNonce.CopyTo(area.AsSpan(VaultFormat.OffThumbNonce, VaultCrypto.NonceSize));
        BinaryPrimitives.WriteInt32LittleEndian(area.AsSpan(VaultFormat.OffThumbLength), _thumbnailPayloadLength);

        _stream.Position = 0;
        _stream.Write(area, 0, area.Length);
        _stream.Flush(true);
    }

    /// <summary>在固定偏移处写入（或重写）加密头。载荷长度恒定 1024，所以可以原地重写。</summary>
    private void WriteHeaderChunk(bool completed)
    {
        var dto = VaultHeaderDto.From(_meta);
        dto.frames = _frameCount;
        dto.end = completed ? (_endUtc ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() : null;

        var json = JsonSerializer.SerializeToUtf8Bytes(dto);
        if (json.Length > VaultFormat.HeaderPayloadSize)
        {
            // 极端情况：标题过长 —— 丢掉可选字段保证头部不超过固定载荷
            dto.title = null;
            dto.tags = null;
            json = JsonSerializer.SerializeToUtf8Bytes(dto);
        }
        _headerJsonLength = Math.Min(json.Length, VaultFormat.HeaderPayloadSize);
        _headerNonce = VaultFormat.NewNonce();

        _stream.Position = VaultFormat.HeaderChunkOffset;
        VaultFormat.WriteChunk(_stream, _itemId, VaultFormat.HeaderChunkIndex, VaultFormat.TypeHeader,
                               _contentKey, _headerNonce, VaultFormat.PadHeader(json));
    }

    // ------------------------------------------------------------------ 写帧

    /// <summary>追加一帧（已编码的 JPEG/PNG 字节）。缩略图只在第一次调用时写入一次。</summary>
    public void AddFrame(byte[] encodedImage, VaultFrameCodec codec, byte[]? thumbnailJpeg = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (encodedImage is null || encodedImage.Length == 0) return;
        if (_completed) throw new InvalidOperationException("条目已完成，不能再写帧");
        if (encodedImage.Length > VaultFormat.MaxChunkTotal) throw new ArgumentException("单帧过大", nameof(encodedImage));

        if (!_thumbnailWritten && thumbnailJpeg is { Length: > 0 })
        {
            _thumbnailNonce = VaultFormat.NewNonce();
            _thumbnailPayloadLength = thumbnailJpeg.Length;
            _stream.Position = VaultFormat.ThumbnailChunkOffset;
            VaultFormat.WriteChunk(_stream, _itemId, VaultFormat.ThumbnailChunkIndex, VaultFormat.TypeThumbnail,
                                   _contentKey, _thumbnailNonce, thumbnailJpeg);
            _thumbnailWritten = true;
            WriteFixedArea(completed: false);
        }

        _stream.Position = _stream.Length;
        byte type = codec == VaultFrameCodec.Png ? VaultFormat.TypePngImage : VaultFormat.TypeJpegFrame;
        VaultFormat.WriteChunk(_stream, _itemId, _nextChunkIndex, type, _contentKey, VaultFormat.NewNonce(), encodedImage);
        _nextChunkIndex++;
        _frameCount++;
    }

    /// <summary>完成条目：写结束标记 + 回写加密头（帧数、结束时间、完成标志）+ 归一化文件时间。</summary>
    public VaultItemInfo Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("条目已经完成");
        _endUtc = DateTimeOffset.UtcNow;

        var endPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            frames = _frameCount,
            end = _endUtc.Value.ToUnixTimeMilliseconds(),
        });
        _stream.Position = _stream.Length;
        VaultFormat.WriteChunk(_stream, _itemId, VaultFormat.EndChunkIndex, VaultFormat.TypeEnd,
                               _contentKey, VaultFormat.NewNonce(), endPayload);
        _stream.Flush(true);

        WriteHeaderChunk(completed: true);
        WriteFixedArea(completed: true);
        _completed = true;
        _stream.Flush(true);

        long bytes = _stream.Length;
        _stream.Dispose();
        _disposed = true;
        VaultPaths.NormalizeFileTimes(_path);
        VaultCrypto.Zero(_contentKey);

        return new VaultItemInfo(_itemId, _path, _meta.Kind, _meta.StartUtc, _endUtc, _frameCount,
            _meta.Width, _meta.Height, _meta.Fps, _meta.DeviceFingerprintHash, _meta.Title,
            _meta.Tags ?? Array.Empty<string>(), bytes, true, null);
    }

    /// <summary>放弃完成标记：保留已写入的帧（与崩溃语义一致，库里显示为「未完成」）。</summary>
    public void Abort()
    {
        if (_disposed) return;
        try { _stream.Flush(true); } catch { }
        try { _stream.Dispose(); } catch { }
        VaultPaths.NormalizeFileTimes(_path);
        _disposed = true;
        VaultCrypto.Zero(_contentKey);
    }

    /// <summary>整条丢弃：关闭并删除文件（用于「一帧都没录到」的空条目，不在库里留垃圾）。</summary>
    public void Discard()
    {
        if (_disposed) return;
        try { _stream.Dispose(); } catch { }
        _disposed = true;
        VaultCrypto.Zero(_contentKey);
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_completed) Abort();
        _disposed = true;
    }

    /// <summary>只改标题：用私钥解出内容密钥后原地重写加密头（文件其余部分不动）。</summary>
    public static bool TryUpdateTitle(string path, byte[] privateKeyPkcs8, string? title, out string? error)
    {
        error = null;
        try
        {
            using var reader = VaultItemReader.Open(path, privateKeyPkcs8, out error);
            if (reader is null) return false;
            reader.RewriteHeaderTitle(title, out error);
            return error is null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
