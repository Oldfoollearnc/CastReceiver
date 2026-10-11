using System.Buffers.Binary;
using System.Text;

namespace CastReceiver.Core.AirPlay.Plist;

/// <summary>
/// Apple 二进制 plist（<c>bplist00</c>）的最小实现，够 AirPlay 用。
///
/// 为什么必须有它：<c>GET /info</c> 的响应、<c>SETUP</c> 的请求与响应体都是二进制 plist
/// （<c>Content-Type: application/x-apple-binary-plist</c>），XML plist 在 RTSP 通道上
/// 不被接受。
///
/// 对象模型用 CLR 原生类型，避免为了几个字段再造一套类型：
/// <list type="bullet">
/// <item><c>null</c> → null，布尔 → <see cref="bool"/>，整数 → <see cref="long"/>（或 <see cref="ulong"/>）</item>
/// <item>实数 → <see cref="double"/>，字符串 → <see cref="string"/>，数据 → <see cref="byte"/>[]</item>
/// <item>数组 → <c>List&lt;object?&gt;</c>，字典 → <c>Dictionary&lt;string, object?&gt;</c></item>
/// </list>
///
/// 格式要点（全部大端）：文件以 <c>bplist00</c> 开头，之后是对象区、偏移表、32 字节 trailer。
/// trailer 里给出偏移表条目宽度、对象引用宽度、对象数、根对象下标与偏移表位置。
/// </summary>
internal static class BinaryPlist
{
    private static readonly byte[] Magic = "bplist00"u8.ToArray();

    // ================================================================ 写

    /// <summary>把对象模型序列化成 <c>bplist00</c> 字节。</summary>
    public static byte[] Write(object? value)
    {
        var nodes = new List<Node>();
        int top = Build(value, nodes);

        int count = nodes.Count;
        int refSize = RefSizeFor(count);

        var body = new List<byte>();
        var offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = Magic.Length + body.Count;
            body.AddRange(Encode(nodes[i], refSize));
        }

        int offsetTableOffset = Magic.Length + body.Count;
        int offsetSize = RefSizeFor(offsetTableOffset);

        var result = new List<byte>(Magic.Length + body.Count + (count * offsetSize) + 32);
        result.AddRange(Magic);
        result.AddRange(body);
        foreach (int off in offsets)
        {
            WriteBigEndian(result, (ulong)off, offsetSize);
        }

        // trailer（32 字节）
        result.AddRange(new byte[6]);           // 未使用
        result.Add((byte)offsetSize);
        result.Add((byte)refSize);
        WriteBigEndian(result, (ulong)count, 8);
        WriteBigEndian(result, (ulong)top, 8);
        WriteBigEndian(result, (ulong)offsetTableOffset, 8);

        return result.ToArray();
    }

    private abstract record Node;

    private sealed record ScalarNode(object? Value) : Node;

    private sealed record ArrayNode(List<int> Refs) : Node;

    private sealed record DictNode(List<int> KeyRefs, List<int> ValueRefs) : Node;

    /// <summary>
    /// 先建节点图（容器先占位、子节点随后编号），这样引用下标与容器自身编码无关，
    /// 等到对象总数确定、算出引用宽度后再统一编码。
    /// </summary>
    private static int Build(object? value, List<Node> nodes)
    {
        switch (value)
        {
            case Dictionary<string, object?> dict:
            {
                int index = nodes.Count;
                nodes.Add(null!);
                var keyRefs = new List<int>(dict.Count);
                var valueRefs = new List<int>(dict.Count);
                foreach (var pair in dict)
                {
                    keyRefs.Add(Build(pair.Key, nodes));
                    valueRefs.Add(Build(pair.Value, nodes));
                }
                nodes[index] = new DictNode(keyRefs, valueRefs);
                return index;
            }

            case List<object?> list:
            {
                int index = nodes.Count;
                nodes.Add(null!);
                var refs = new List<int>(list.Count);
                foreach (object? item in list)
                {
                    refs.Add(Build(item, nodes));
                }
                nodes[index] = new ArrayNode(refs);
                return index;
            }

            default:
            {
                int index = nodes.Count;
                nodes.Add(new ScalarNode(value));
                return index;
            }
        }
    }

    /// <summary>引用宽度：1/2/4/8 字节，取能表示最大下标的最小值。</summary>
    private static int RefSizeFor(int count)
    {
        if (count <= byte.MaxValue)
        {
            return 1;
        }
        if (count <= ushort.MaxValue)
        {
            return 2;
        }
        return count <= int.MaxValue ? 4 : 8;
    }

    private static byte[] Encode(Node node, int refSize)
    {
        switch (node)
        {
            case ScalarNode s:
                return EncodeScalar(s.Value);

            case ArrayNode a:
            {
                var bytes = new List<byte>();
                bytes.AddRange(CountMarker(0xA0, a.Refs.Count));
                foreach (int r in a.Refs)
                {
                    WriteBigEndian(bytes, (ulong)r, refSize);
                }
                return bytes.ToArray();
            }

            case DictNode d:
            {
                var bytes = new List<byte>();
                bytes.AddRange(CountMarker(0xD0, d.KeyRefs.Count));
                foreach (int r in d.KeyRefs)
                {
                    WriteBigEndian(bytes, (ulong)r, refSize);
                }
                foreach (int r in d.ValueRefs)
                {
                    WriteBigEndian(bytes, (ulong)r, refSize);
                }
                return bytes.ToArray();
            }

            default:
                throw new InvalidOperationException("未知的 plist 节点");
        }
    }

    /// <summary>标记字节：低 4 位 &lt; 15 时直接是元素个数，否则为 0xF 后跟一个整数对象。</summary>
    private static byte[] CountMarker(int baseMarker, int count)
    {
        if (count < 15)
        {
            return new[] { (byte)(baseMarker | count) };
        }

        var bytes = new List<byte> { (byte)(baseMarker | 0x0F) };
        bytes.AddRange(EncodeUnsigned((ulong)count));
        return bytes.ToArray();
    }

    private static byte[] EncodeScalar(object? value)
    {
        switch (value)
        {
            case null:
                return new byte[] { 0x00 };

            case bool b:
                return new byte[] { b ? (byte)0x09 : (byte)0x08 };

            case string s:
                return EncodeString(s);

            case byte[] data:
            {
                var bytes = new List<byte>();
                bytes.AddRange(CountMarker(0x40, data.Length));
                bytes.AddRange(data);
                return bytes.ToArray();
            }

            case sbyte or byte or short or ushort or int or uint or long:
                return EncodeSigned(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));

            case ulong u:
                return EncodeUnsigned(u);

            case float f:
                return EncodeReal(f);

            case double d:
                return EncodeReal(d);

            default:
                throw new NotSupportedException($"plist 写入不支持的类型：{value.GetType()}");
        }
    }

    private static byte[] EncodeSigned(long v)
    {
        if (v >= 0)
        {
            return EncodeUnsigned((ulong)v);
        }

        // 负数一律用 8 字节有符号写。
        // 原因：1/2/4 字节在读取端被当作**无符号**（plistlib/Apple 的实际行为），
        // 写成 2 字节的 -12345 会被读成 53191。8 字节是唯一天然有符号的宽度。
        var big = new byte[9];
        big[0] = 0x13;
        BinaryPrimitives.WriteInt64BigEndian(big.AsSpan(1), v);
        return big;
    }

    private static byte[] EncodeUnsigned(ulong v)
    {
        if (v <= byte.MaxValue)
        {
            return new[] { (byte)0x10, (byte)v };
        }
        if (v <= ushort.MaxValue)
        {
            var b = new byte[3];
            b[0] = 0x11;
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), (ushort)v);
            return b;
        }
        if (v <= uint.MaxValue)
        {
            var b = new byte[5];
            b[0] = 0x12;
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(1), (uint)v);
            return b;
        }
        if (v <= long.MaxValue)
        {
            var b = new byte[9];
            b[0] = 0x13;
            BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(1), v);
            return b;
        }

        // 超过 long.MaxValue：必须用 16 字节形式（高 8 字节为 0），
        // 否则按有符号解读会读成负数。
        var wide = new byte[17];
        wide[0] = 0x14;
        BinaryPrimitives.WriteUInt64BigEndian(wide.AsSpan(9), v);
        return wide;
    }

    private static byte[] EncodeReal(double d)
    {
        var b = new byte[9];
        b[0] = 0x23;
        BinaryPrimitives.WriteDoubleBigEndian(b.AsSpan(1), d);
        return b;
    }

    private static byte[] EncodeString(string s)
    {
        // 纯 ASCII 走 0x5n（每字符 1 字节），否则按 UTF-16BE 走 0x6n（长度按 UTF-16 码元数）
        bool ascii = true;
        foreach (char c in s)
        {
            if (c > 0x7F)
            {
                ascii = false;
                break;
            }
        }

        var bytes = new List<byte>();
        if (ascii)
        {
            bytes.AddRange(CountMarker(0x50, s.Length));
            foreach (char c in s)
            {
                bytes.Add((byte)c);
            }
            return bytes.ToArray();
        }

        bytes.AddRange(CountMarker(0x60, s.Length));
        foreach (char c in s)
        {
            bytes.Add((byte)(c >> 8));
            bytes.Add((byte)c);
        }
        return bytes.ToArray();
    }

    private static void WriteBigEndian(List<byte> target, ulong value, int size)
    {
        for (int i = size - 1; i >= 0; i--)
        {
            target.Add((byte)(value >> (8 * i)));
        }
    }

    // ================================================================ 读

    /// <summary>解析 <c>bplist00</c> 字节为对象模型。</summary>
    public static object? Read(byte[] data)
    {
        if (data.Length < Magic.Length + 32 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("不是 bplist00 数据");
        }

        int trailer = data.Length - 32;
        int offsetSize = data[trailer + 6];
        int refSize = data[trailer + 7];
        long numObjects = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(trailer + 8));
        long topObject = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(trailer + 16));
        long offsetTableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(trailer + 24));

        if (offsetTableOffset < Magic.Length || offsetTableOffset + (numObjects * offsetSize) > trailer)
        {
            throw new InvalidDataException("bplist 偏移表越界");
        }

        var offsets = new int[numObjects];
        for (int i = 0; i < numObjects; i++)
        {
            offsets[i] = (int)ReadBigEndian(data, offsetTableOffset + (i * offsetSize), offsetSize);
        }

        var seen = new HashSet<int>();
        return Decode(data, offsets, (int)topObject, refSize, seen);
    }

    private static object? Decode(byte[] data, int[] offsets, int index, int refSize, HashSet<int> seen)
    {
        if ((uint)index >= (uint)offsets.Length)
        {
            throw new InvalidDataException($"bplist 对象下标越界：{index}");
        }
        // 只防"递归环"：同一个对象被多处合法引用（plistlib 会去重、复用同一个对象）必须允许，
        // 因此解码完就从活动集合里移除，而不是"访问过就永久标记"。
        if (!seen.Add(index))
        {
            throw new InvalidDataException("bplist 出现循环引用");
        }

        try
        {
            return DecodeCore(data, offsets, index, refSize, seen);
        }
        finally
        {
            seen.Remove(index);
        }
    }

    private static object? DecodeCore(byte[] data, int[] offsets, int index, int refSize, HashSet<int> seen)
    {
        int pos = offsets[index];
        byte marker = data[pos];
        int kind = marker >> 4;
        int low = marker & 0x0F;

        switch (kind)
        {
            case 0x0:
                return low switch
                {
                    0x0 => null,
                    0x8 => false,
                    0x9 => true,
                    _ => null,
                };

            case 0x1:
            {
                // bplist 整数的符号约定（与 Apple/plistlib 实际行为一致）：
                //   1/2/4 字节 -> 无符号
                //   8 字节     -> 有符号补码（负整数就是这么写的）
                //   16 字节    -> 128 位有符号；高 8 字节为 0 时可承载超过 long.MaxValue 的无符号值
                int size = 1 << low;
                if (size <= 4)
                {
                    return (long)ReadBigEndian(data, pos + 1, size);
                }
                if (size == 8)
                {
                    return (long)ReadBigEndian(data, pos + 1, 8);
                }

                long high = (long)ReadBigEndian(data, pos + 1, 8);
                ulong lowBits = ReadBigEndian(data, pos + 9, 8);
                if (high == 0)
                {
                    return lowBits <= long.MaxValue ? (long)lowBits : lowBits;
                }
                if (high == -1)
                {
                    return (long)lowBits;
                }

                throw new InvalidDataException("bplist 128 位整数超出支持范围");
            }

            case 0x2:
            {
                int size = 1 << low;
                return size == 4
                    ? BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(pos + 1))
                    : BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(pos + 1));
            }

            case 0x3:
            {
                double seconds = BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(pos + 1));
                return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
            }

            case 0x4:
            {
                var (count, start) = ReadCount(data, pos, low, refSize, offsets, seen);
                return data.AsSpan(start, count).ToArray();
            }

            case 0x5:
            {
                var (count, start) = ReadCount(data, pos, low, refSize, offsets, seen);
                return Encoding.ASCII.GetString(data, start, count);
            }

            case 0x6:
            {
                var (count, start) = ReadCount(data, pos, low, refSize, offsets, seen);
                return Encoding.BigEndianUnicode.GetString(data, start, count * 2);
            }

            case 0x8:
                return (long)ReadBigEndian(data, pos + 1, low + 1);

            case 0xA:
            {
                var (count, start) = ReadCount(data, pos, low, refSize, offsets, seen);
                var list = new List<object?>(count);
                for (int i = 0; i < count; i++)
                {
                    int child = (int)ReadBigEndian(data, start + (i * refSize), refSize);
                    list.Add(Decode(data, offsets, child, refSize, seen));
                }
                return list;
            }

            case 0xD:
            {
                var (count, start) = ReadCount(data, pos, low, refSize, offsets, seen);
                var dict = new Dictionary<string, object?>(count, StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    int keyRef = (int)ReadBigEndian(data, start + (i * refSize), refSize);
                    int valRef = (int)ReadBigEndian(data, start + ((count + i) * refSize), refSize);
                    string key = Decode(data, offsets, keyRef, refSize, seen) as string
                                 ?? throw new InvalidDataException("bplist 字典的键不是字符串");
                    dict[key] = Decode(data, offsets, valRef, refSize, seen);
                }
                return dict;
            }

            default:
                throw new InvalidDataException($"不支持的 bplist 标记：0x{marker:x2}");
        }
    }

    /// <summary>读出元素个数；低 4 位为 0xF 时，个数以整数对象紧跟其后。</summary>
    private static (int Count, int Start) ReadCount(byte[] data, int pos, int low, int refSize, int[] offsets, HashSet<int> seen)
    {
        if (low != 0x0F)
        {
            return (low, pos + 1);
        }

        byte marker = data[pos + 1];
        if ((marker >> 4) != 0x1)
        {
            throw new InvalidDataException("bplist 的元素个数不是整数对象");
        }

        // 整数对象内联在标记之后（不是引用），因此这里直接按宽度读取
        int size = 1 << (marker & 0x0F);
        ulong count = ReadBigEndian(data, pos + 2, size);
        return ((int)count, pos + 2 + size);
    }

    private static ulong ReadBigEndian(byte[] data, long offset, int size)
    {
        if (offset < 0 || offset + size > data.Length)
        {
            throw new InvalidDataException("bplist 读取越界");
        }

        ulong v = 0;
        for (int i = 0; i < size; i++)
        {
            v = (v << 8) | data[offset + i];
        }
        return v;
    }
}
