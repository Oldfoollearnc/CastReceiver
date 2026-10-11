namespace CastReceiver.Core.AirPlay.FairPlay;

/// <summary>
/// UxPlay <c>lib/playfair/modified_md5.c</c> 的逐句移植。
///
/// 注意这**不是**标准 MD5：没有长度填充、没有末尾的初值累加，
/// 而且第 31 轮会用 A/B/C/D 的低位去交换 64 字节输入里的 32 位字
/// （<c>swap(&amp;block_words[A &amp; 15], ...)</c>），后续轮次继续读被改过的输入。
/// 因此必须逐句对应，不能按"标准 MD5"改写。
/// </summary>
internal static class ModifiedMd5
{
    /// <summary>
    /// 标准 MD5 加法常量 K[i] = floor(2^32 * |sin(i+1)|)，
    /// 与 C 版 <c>(int)(long long)((1LL &lt;&lt; 32) * fabs(sin(i + 1)))</c> 等值
    /// （参考向量 md5K 会逐项验证这一点，避免 sin 的末位差异被带进来）。
    /// </summary>
    private static readonly uint[] K =
    {
        0xd76aa478u, 0xe8c7b756u, 0x242070dbu, 0xc1bdceeeu, 0xf57c0fafu, 0x4787c62au, 0xa8304613u, 0xfd469501u,
        0x698098d8u, 0x8b44f7afu, 0xffff5bb1u, 0x895cd7beu, 0x6b901122u, 0xfd987193u, 0xa679438eu, 0x49b40821u,
        0xf61e2562u, 0xc040b340u, 0x265e5a51u, 0xe9b6c7aau, 0xd62f105du, 0x02441453u, 0xd8a1e681u, 0xe7d3fbc8u,
        0x21e1cde6u, 0xc33707d6u, 0xf4d50d87u, 0x455a14edu, 0xa9e3e905u, 0xfcefa3f8u, 0x676f02d9u, 0x8d2a4c8au,
        0xfffa3942u, 0x8771f681u, 0x6d9d6122u, 0xfde5380cu, 0xa4beea44u, 0x4bdecfa9u, 0xf6bb4b60u, 0xbebfbc70u,
        0x289b7ec6u, 0xeaa127fau, 0xd4ef3085u, 0x04881d05u, 0xd9d4d039u, 0xe6db99e5u, 0x1fa27cf8u, 0xc4ac5665u,
        0xf4292244u, 0x432aff97u, 0xab9423a7u, 0xfc93a039u, 0x655b59c3u, 0x8f0ccc92u, 0xffeff47du, 0x85845dd1u,
        0x6fa87e4fu, 0xfe2ce6e0u, 0xa3014314u, 0x4e0811a1u, 0xf7537e82u, 0xbd3af235u, 0x2ad7d2bbu, 0xeb86d391u,
    };

    private static readonly int[] Shift =
    {
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    };

    private static uint F(uint b, uint c, uint d) => (b & c) | (~b & d);
    private static uint G(uint b, uint c, uint d) => (b & d) | (c & ~d);
    private static uint H(uint b, uint c, uint d) => b ^ c ^ d;
    private static uint I(uint b, uint c, uint d) => c ^ (b | ~d);

    /// <summary>C <c>rol</c>：<c>((input &lt;&lt; count) &amp; 0xffffffff) | (input &amp; 0xffffffff) &gt;&gt; (32-count)</c>。</summary>
    private static uint Rol(uint input, int count)
        => (input << count) | (input >> (32 - count));

    private static uint ReadU32(byte[] b, int off)
        => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v;
        b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16);
        b[off + 3] = (byte)(v >> 24);
    }

    /// <summary>C <c>swap(&amp;block_words[a], &amp;block_words[b])</c>：交换两个 32 位字。</summary>
    private static void SwapWords(byte[] block, int wordA, int wordB)
    {
        int a = wordA * 4, b = wordB * 4;
        for (int k = 0; k < 4; k++)
        {
            (block[a + k], block[b + k]) = (block[b + k], block[a + k]);
        }
    }

    /// <summary>
    /// C <c>modified_md5(originalblockIn, keyIn, keyOut)</c>。
    /// <paramref name="keyIn"/> 只读（C 里通过 <c>key_words</c> 读初值），
    /// 结果写入 <paramref name="keyOut"/> 的前 16 字节。
    /// </summary>
    internal static void Compute(byte[] originalBlockIn, byte[] keyIn, byte[] keyOut)
    {
        var blockIn = new byte[64];
        Array.Copy(originalBlockIn, blockIn, 64);

        uint A = ReadU32(keyIn, 0);
        uint B = ReadU32(keyIn, 4);
        uint C = ReadU32(keyIn, 8);
        uint D = ReadU32(keyIn, 12);

        for (int i = 0; i < 64; i++)
        {
            int j;
            if (i < 16)
            {
                j = i;
            }
            else if (i < 32)
            {
                j = (5 * i + 1) % 16;
            }
            else if (i < 48)
            {
                j = (3 * i + 5) % 16;
            }
            else
            {
                j = 7 * i % 16;
            }

            uint input = (uint)((blockIn[4 * j] << 24) | (blockIn[4 * j + 1] << 16) | (blockIn[4 * j + 2] << 8) | blockIn[4 * j + 3]);
            uint Z = A + input + K[i];
            if (i < 16)
            {
                Z = Rol(Z + F(B, C, D), Shift[i]);
            }
            else if (i < 32)
            {
                Z = Rol(Z + G(B, C, D), Shift[i]);
            }
            else if (i < 48)
            {
                Z = Rol(Z + H(B, C, D), Shift[i]);
            }
            else
            {
                Z = Rol(Z + I(B, C, D), Shift[i]);
            }

            Z += B;
            uint tmp = D;
            D = C;
            C = B;
            B = Z;
            A = tmp;

            if (i == 31)
            {
                SwapWords(blockIn, (int)(A & 15), (int)(B & 15));
                SwapWords(blockIn, (int)(C & 15), (int)(D & 15));
                SwapWords(blockIn, (int)((A & (15u << 4)) >> 4), (int)((B & (15u << 4)) >> 4));
                SwapWords(blockIn, (int)((A & (15u << 8)) >> 8), (int)((B & (15u << 8)) >> 8));
                SwapWords(blockIn, (int)((A & (15u << 12)) >> 12), (int)((B & (15u << 12)) >> 12));
            }
        }

        WriteU32(keyOut, 0, ReadU32(keyIn, 0) + A);
        WriteU32(keyOut, 4, ReadU32(keyIn, 4) + B);
        WriteU32(keyOut, 8, ReadU32(keyIn, 8) + C);
        WriteU32(keyOut, 12, ReadU32(keyIn, 12) + D);
    }
}
