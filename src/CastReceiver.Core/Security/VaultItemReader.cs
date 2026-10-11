using System.Buffers.Binary;
using System.Text.Json;

namespace CastReceiver.Core.Security;

/// <summary>
/// 读取一条加密条目。必须提供账号私钥（= 必须输入过密码）；
/// 私钥不匹配、文件被篡改、被截断都会在这里被识别出来，而不是吐出错误的画面。
/// </summary>
public sealed class VaultItemReader : IDisposable
{
    private readonly record struct ChunkRef(int Index, byte Type, long Offset, int TotalLength, byte[] Nonce);

    private readonly FileStream _stream;
    private readonly Guid _itemId;
    private readonly byte[] _contentKey;
    private readonly List<ChunkRef> _frameChunks = new();
    private bool _disposed;

    public VaultItemInfo Info { get; private set; } = null!;
    public byte[]? ThumbnailJpeg { get; private set; }
    public int FrameCount => _frameChunks.Count;
    public bool IsComplete => Info.Completed;
    public int CorruptChunks { get; private set; }
    public string Path { get; }

    private VaultItemReader(FileStream stream, Guid itemId, byte[] contentKey, string path)
    {
        _stream = stream;
        _itemId = itemId;
        _contentKey = contentKey;
        Path = path;
    }

    public static VaultItemReader? Open(string path, byte[] privateKeyPkcs8, out string? error)
    {
        error = null;
        FileStream? stream = null;
        byte[]? contentKey = null;
        try
        {
            if (privateKeyPkcs8 is null || privateKeyPkcs8.Length == 0) { error = "尚未解锁账号"; return null; }
            // FileShare.Delete：库里删除条目时不会因为「正被播放器打开」而失败
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            if (stream.Length < VaultFormat.FixedAreaSize) { error = "文件不完整"; return null; }

            var area = new byte[VaultFormat.FixedAreaSize];
            if (!TryReadExact(stream, area, 0, area.Length)) { error = "文件读取失败"; return null; }
            if (!area.AsSpan(0, 8).SequenceEqual(VaultFormat.Magic)) { error = "不是本程序的加密条目"; return null; }
            if (BinaryPrimitives.ReadInt16LittleEndian(area.AsSpan(VaultFormat.OffVersion)) != VaultFormat.Version)
            {
                error = "条目格式版本不受支持";
                return null;
            }

            var itemId = new Guid(area.AsSpan(VaultFormat.OffItemId, 16));
            byte flags = area[VaultFormat.OffFlags];
            bool completed = (flags & VaultFormat.FlagCompleted) != 0;
            bool hasThumbnail = (flags & VaultFormat.FlagHasThumbnail) != 0;
            var headerNonce = area.AsSpan(VaultFormat.OffHeaderNonce, VaultCrypto.NonceSize).ToArray();
            int headerJsonLength = BinaryPrimitives.ReadInt32LittleEndian(area.AsSpan(VaultFormat.OffHeaderLength));
            var thumbNonce = area.AsSpan(VaultFormat.OffThumbNonce, VaultCrypto.NonceSize).ToArray();
            int thumbPayloadLength = BinaryPrimitives.ReadInt32LittleEndian(area.AsSpan(VaultFormat.OffThumbLength));

            var wrappedKey = area.AsSpan(VaultFormat.OffWrappedKey, VaultCrypto.WrappedKeySize).ToArray();
            contentKey = VaultCrypto.TryUnwrapItemKey(privateKeyPkcs8, wrappedKey);
            if (contentKey is null) { error = "无法解密（账号私钥不匹配）"; return null; }

            // 加密头
            var headerPayload = ReadChunkPayload(stream, VaultFormat.HeaderChunkOffset, itemId,
                VaultFormat.HeaderChunkIndex, VaultFormat.TypeHeader, headerNonce, contentKey);
            if (headerPayload is null) { error = "加密头校验失败（文件被篡改或损坏）"; return null; }

            VaultHeaderDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<VaultHeaderDto>(VaultFormat.UnpadHeader(headerPayload, headerJsonLength));
            }
            catch { dto = null; }
            if (dto is null) { error = "加密头内容无法解析"; return null; }

            var reader = new VaultItemReader(stream, itemId, contentKey, path);
            contentKey = null;   // 所有权已转交

            // 缩略图（可选）
            long scanPosition = VaultFormat.ThumbnailChunkOffset;
            if (hasThumbnail && thumbPayloadLength > 0)
            {
                var thumb = ReadChunkPayload(stream, VaultFormat.ThumbnailChunkOffset, itemId,
                    VaultFormat.ThumbnailChunkIndex, VaultFormat.TypeThumbnail, thumbNonce, reader._contentKey);
                if (thumb is not null) reader.ThumbnailJpeg = thumb;
                scanPosition = VaultFormat.ThumbnailChunkOffset + VaultFormat.ChunkOverhead + thumbPayloadLength;
            }

            // 扫描帧块（只读块头，跳过载荷 —— 打开大文件也很快）
            DateTimeOffset? endUtc = null;
            stream.Position = scanPosition;
            while (true)
            {
                long chunkStart = stream.Position;
                var head = new byte[VaultFormat.ChunkHeadSize];
                if (!TryReadExact(stream, head, 0, head.Length)) break;
                int total = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0, 4));
                int index = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4, 4));
                byte type = head[8];
                if (total < VaultFormat.ChunkOverhead || total > VaultFormat.MaxChunkTotal) break;
                if (stream.Length - chunkStart < total) break;      // 尾部半截块（写入中被中断）

                if (index == VaultFormat.EndChunkIndex && type == VaultFormat.TypeEnd)
                {
                    var nonce = head.AsSpan(9, VaultCrypto.NonceSize).ToArray();
                    var payload = ReadChunkPayload(stream, chunkStart, itemId, index, type, nonce, reader._contentKey);
                    if (payload is not null)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(payload);
                            if (doc.RootElement.TryGetProperty("end", out var endElement) && endElement.TryGetInt64(out long endMs))
                                endUtc = DateTimeOffset.FromUnixTimeMilliseconds(endMs);
                        }
                        catch { }
                    }
                    break;
                }

                reader._frameChunks.Add(new ChunkRef(index, type, chunkStart, total,
                    head.AsSpan(9, VaultCrypto.NonceSize).ToArray()));
                stream.Position = chunkStart + total;
            }

            reader._frameChunks.Sort((a, b) => a.Index.CompareTo(b.Index));

            var meta = dto.ToMeta();
            reader.Info = new VaultItemInfo(itemId, path, meta.Kind, meta.StartUtc, endUtc,
                reader._frameChunks.Count, meta.Width, meta.Height, meta.Fps, meta.DeviceFingerprintHash,
                meta.Title, meta.Tags ?? Array.Empty<string>(), stream.Length, completed, reader.ThumbnailJpeg);
            return reader;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            stream?.Dispose();
            VaultCrypto.Zero(contentKey);
            return null;
        }
    }

    private static byte[]? ReadChunkPayload(FileStream stream, long chunkStart, Guid itemId,
                                            int index, byte type, byte[] nonce, byte[] contentKey)
    {
        try
        {
            stream.Position = chunkStart;
            var head = new byte[VaultFormat.ChunkHeadSize];
            if (!TryReadExact(stream, head, 0, head.Length)) return null;
            int total = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0, 4));
            if (total < VaultFormat.ChunkOverhead || total > VaultFormat.MaxChunkTotal) return null;
            if (stream.Length - chunkStart < total) return null;

            var body = new byte[total - VaultFormat.ChunkHeadSize];
            if (!TryReadExact(stream, body, 0, body.Length)) return null;
            return VaultCrypto.TryOpen(contentKey, nonce, body, VaultFormat.BuildAad(itemId, index, type));
        }
        catch { return null; }
    }

    /// <summary>取第 ordinal 帧（0 起）的原始编码字节（JPEG 或 PNG）。</summary>
    public byte[]? GetFrame(int ordinal, out string? error)
    {
        error = null;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ordinal < 0 || ordinal >= _frameChunks.Count) { error = "帧号超出范围"; return null; }
        var chunk = _frameChunks[ordinal];
        var payload = ReadChunkPayload(_stream, chunk.Offset, _itemId, chunk.Index, chunk.Type, chunk.Nonce, _contentKey);
        if (payload is null)
        {
            CorruptChunks++;
            error = "该帧校验失败（已损坏或被篡改）";
        }
        return payload;
    }

    public ReadOnlySpan<byte> FrameCodecOf(int ordinal)
        => ordinal >= 0 && ordinal < _frameChunks.Count && _frameChunks[ordinal].Type == VaultFormat.TypePngImage
            ? "png"u8
            : "jpg"u8;

    /// <summary>只改标题：原地重写加密头（载荷长度固定，文件其余字节不动），需要写权限。</summary>
    public bool RewriteHeaderTitle(string? title, out string? error)
    {
        error = null;
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var dto = new VaultHeaderDto
            {
                kind = Info.Kind == VaultItemKind.Recording ? "recording" : "screenshot",
                start = Info.StartUtc.ToUnixTimeMilliseconds(),
                end = Info.EndUtc?.ToUnixTimeMilliseconds(),
                frames = Info.FrameCount,
                w = Info.Width,
                h = Info.Height,
                fps = Info.Fps,
                dev = Info.DeviceFingerprintHash,
                title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                tags = Info.Tags.Length == 0 ? null : Info.Tags,
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(dto);
            if (json.Length > VaultFormat.HeaderPayloadSize) { error = "标题过长"; return false; }

            var payload = VaultFormat.PadHeader(json);
            var nonce = VaultFormat.NewNonce();
            var sealedPayload = VaultCrypto.Seal(_contentKey, nonce, payload,
                VaultFormat.BuildAad(_itemId, VaultFormat.HeaderChunkIndex, VaultFormat.TypeHeader));

            using var write = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            write.Position = VaultFormat.HeaderChunkOffset + 9;
            write.Write(nonce, 0, nonce.Length);
            write.Position = VaultFormat.HeaderChunkOffset + VaultFormat.ChunkHeadSize;
            write.Write(sealedPayload, 0, sealedPayload.Length);
            write.Position = VaultFormat.OffHeaderNonce;
            write.Write(nonce, 0, nonce.Length);
            write.Position = VaultFormat.OffHeaderLength;
            write.Write(BitConverter.GetBytes(json.Length), 0, 4);
            write.Flush(true);

            Info = Info with { Title = dto.title };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>不需要密钥的浅层信息（用于配额统计/锁定时判断文件是否可用）。</summary>
    public static bool TryPeek(string path, out Guid itemId, out bool completed, out long bytes)
    {
        itemId = Guid.Empty;
        completed = false;
        bytes = 0;
        try
        {
            var info = new FileInfo(path);
            bytes = info.Length;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < VaultFormat.OffThumbTotal) return false;
            var area = new byte[VaultFormat.OffThumbTotal];
            if (!TryReadExact(stream, area, 0, area.Length)) return false;
            if (!area.AsSpan(0, 8).SequenceEqual(VaultFormat.Magic)) return false;
            itemId = new Guid(area.AsSpan(VaultFormat.OffItemId, 16));
            completed = (area[VaultFormat.OffFlags] & VaultFormat.FlagCompleted) != 0;
            return true;
        }
        catch { return false; }
    }

    private static bool TryReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, offset + read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); } catch { }
        VaultCrypto.Zero(_contentKey);
    }
}
