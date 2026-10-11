using System.Buffers.Binary;

namespace CastReceiver.Core.Security;

/// <summary>
/// .crv 条目容器格式（所有常量与组帧规则集中在这里）。
///
/// 布局：
///   [0 .. 4096)                    固定区（明文，但不含任何时间/数量/名字）
///   [4096 .. 5157)                 加密头 chunk（index 0，载荷固定 1024 字节，便于原地重写）
///   [5157 .. )                     可选缩略图 chunk（index 1，长度可变）
///   之后                           帧 chunk（index 2,3,4…，每帧一块）
///   最后                           结束标记 chunk（index 0xFFFFFFF0）
///
/// 每个 chunk = [4B 总长][4B index][1B 类型][12B nonce][密文][16B tag]
/// AAD = itemId(16) || index(4, 大端) || 类型(1)
/// —— 把块的顺序、位置、类型都纳入认证，挪动/替换/重排都会解密失败。
/// </summary>
internal static class VaultFormat
{
    public static ReadOnlySpan<byte> Magic => "CRWVAULT"u8;
    public const int Version = 1;

    public const int FixedAreaSize = 4096;
    public const int HeaderPayloadSize = 1024;
    public const int ChunkHeadSize = 4 + 4 + 1 + VaultCrypto.NonceSize;      // 21
    public const int ChunkOverhead = ChunkHeadSize + VaultCrypto.TagSize;    // 37
    public const int MaxChunkTotal = 64 * 1024 * 1024;

    public const int OffMagic = 0;
    public const int OffVersion = 8;
    public const int OffFlags = 10;
    public const int OffItemId = 12;
    public const int OffWrappedKey = 28;
    public const int OffHeaderNonce = OffWrappedKey + VaultCrypto.WrappedKeySize;   // 284
    public const int OffHeaderLength = OffHeaderNonce + VaultCrypto.NonceSize;      // 296
    public const int OffThumbNonce = OffHeaderLength + 4;                           // 300
    public const int OffThumbLength = OffThumbNonce + VaultCrypto.NonceSize;        // 312
    public const int OffThumbTotal = OffThumbLength + 4;                            // 316

    public const byte FlagCompleted = 1;
    public const byte FlagHasThumbnail = 2;

    public const int HeaderChunkIndex = 0;
    public const int ThumbnailChunkIndex = 1;
    public const int FirstFrameChunkIndex = 2;
    public const int EndChunkIndex = unchecked((int)0xFFFFFFF0);

    public const byte TypeHeader = 1;
    public const byte TypeThumbnail = 2;
    public const byte TypeJpegFrame = 3;
    public const byte TypePngImage = 4;
    public const byte TypeEnd = 5;

    public const long HeaderChunkOffset = FixedAreaSize;
    public const long ThumbnailChunkOffset = FixedAreaSize + ChunkOverhead + HeaderPayloadSize;   // 5157

    public static byte[] BuildAad(Guid itemId, int chunkIndex, byte chunkType)
    {
        var aad = new byte[16 + 4 + 1];
        itemId.TryWriteBytes(aad.AsSpan(0, 16));
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(16, 4), chunkIndex);
        aad[20] = chunkType;
        return aad;
    }

    public static byte[] NewNonce() => VaultCrypto.RandomBytes(VaultCrypto.NonceSize);

    /// <summary>写入一个完整 chunk；返回写入的总字节数。</summary>
    public static int WriteChunk(Stream stream, Guid itemId, int index, byte type,
                                 byte[] contentKey, byte[] nonce, ReadOnlySpan<byte> payload)
    {
        int total = ChunkHeadSize + payload.Length + VaultCrypto.TagSize;
        var head = new byte[ChunkHeadSize];
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(0, 4), total);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(4, 4), index);
        head[8] = type;
        nonce.CopyTo(head.AsSpan(9));
        stream.Write(head, 0, head.Length);
        var sealedPayload = VaultCrypto.Seal(contentKey, nonce, payload, BuildAad(itemId, index, type));
        stream.Write(sealedPayload, 0, sealedPayload.Length);
        return total;
    }

    /// <summary>把 JSON 头填充到固定长度（原地重写要求载荷长度恒定）。</summary>
    public static byte[] PadHeader(byte[] json)
    {
        var buffer = new byte[HeaderPayloadSize];
        int copy = Math.Min(json.Length, HeaderPayloadSize);
        json.AsSpan(0, copy).CopyTo(buffer);
        for (int i = copy; i < buffer.Length; i++) buffer[i] = 0x20;
        return buffer;
    }

    public static byte[] UnpadHeader(ReadOnlySpan<byte> payload, int declaredLength)
    {
        int length = declaredLength > 0 && declaredLength <= payload.Length ? declaredLength : payload.Length;
        return payload[..length].ToArray();
    }
}
