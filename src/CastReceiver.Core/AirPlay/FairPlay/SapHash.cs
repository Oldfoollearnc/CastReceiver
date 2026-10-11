namespace CastReceiver.Core.AirPlay.FairPlay;

/// <summary>
/// UxPlay <c>lib/playfair/sap_hash.c</c> 的逐句移植（<c>sap_hash</c>）。
///
/// 前半段把 64 字节输入按大端解释展开成 210 字节，再做 840 轮滚动混淆，
/// 交给 <see cref="HandGarble.Garble"/> 生成各缓冲区，最后把
/// buffer3/buffer0/buffer2/buffer1 依次折进 16 字节输出并做一次反向混淆。
/// </summary>
internal static class SapHash
{
    private static readonly byte[] Buffer0 =
    {
        0x96, 0x5F, 0xC6, 0x53, 0xF8, 0x46, 0xCC, 0x18, 0xDF, 0xBE,
        0xB2, 0xF8, 0x38, 0xD7, 0xEC, 0x22, 0x03, 0xD1, 0x20, 0x8F,
    };

    private static readonly byte[] Buffer2 =
    {
        0x43, 0x54, 0x62, 0x7A, 0x18, 0xC3, 0xD6, 0xB3, 0x9A, 0x56, 0xF6, 0x1C,
        0x14, 0x3F, 0x0C, 0x1D, 0x3B, 0x36, 0x83, 0xB1, 0x39, 0x51, 0x4A, 0xAA,
        0x09, 0x3E, 0xFE, 0x44, 0xAF, 0xDE, 0xC3, 0x20, 0x9D, 0x42, 0x3A,
    };

    private static readonly byte[] Buffer4 =
    {
        0xED, 0x25, 0xD1, 0xBB, 0xBC, 0x27, 0x9F, 0x02, 0xA2, 0xA9, 0x11,
        0x00, 0x0C, 0xB3, 0x52, 0xC0, 0xBD, 0xE3, 0x1B, 0x49, 0xC7,
    };

    private static readonly int[] I0Index = { 18, 22, 23, 0, 5, 19, 32, 31, 10, 21, 30 };

    private static uint ReadU32(byte[] b, int off)
        => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    /// <summary>
    /// C <c>sap_hash(blockIn, keyOut)</c>：<paramref name="blockIn"/> 只读（64 字节），
    /// <paramref name="keyOut"/> 就地写入 16 字节。
    /// </summary>
    internal static void Compute(byte[] blockIn, byte[] keyOut)
    {
        var buffer1 = new byte[210];
        var buffer3 = new byte[132];   // C 里未初始化；下文所有读取都在写入之后，故清零等价

        // 输入按大端逐字节展开到 210 字节
        for (int i = 0; i < 210; i++)
        {
            uint inWord = ReadU32(blockIn, ((i % 64) >> 2) * 4);
            uint inByte = (inWord >> ((3 - (i % 4)) << 3)) & 0xff;
            buffer1[i] = (byte)inByte;
        }

        // 840 轮滚动混淆。C 里下标写成 ((i-155) & 0xffffffff) % 210：
        // 0xffffffff 是 unsigned，负数先转成无符号再取模，故这里用 (uint) 转换保持等价。
        for (int i = 0; i < 840; i++)
        {
            byte x = buffer1[(int)((uint)(i - 155) % 210u)];
            byte y = buffer1[(int)((uint)(i - 57) % 210u)];
            byte z = buffer1[(int)((uint)(i - 13) % 210u)];
            byte w = buffer1[(int)((uint)i % 210u)];
            buffer1[i % 210] = (byte)((HandGarble.Rol8(y, 5) + (HandGarble.Rol8(z, 3) ^ w) - HandGarble.Rol8(x, 7)) & 0xff);
        }

        // buffer2 / buffer3 / buffer4 由 garble 就地生成
        var buffer2 = (byte[])Buffer2.Clone();
        var buffer4 = (byte[])Buffer4.Clone();
        var buffer0 = (byte[])Buffer0.Clone();
        HandGarble.Garble(buffer0, buffer1, buffer2, buffer3, buffer4);

        for (int i = 0; i < 16; i++)
        {
            keyOut[i] = 0xE1;
        }

        for (int i = 0; i < 11; i++)
        {
            if (i == 3)
            {
                keyOut[i] = 0x3d;
            }
            else
            {
                keyOut[i] = (byte)((keyOut[i] + buffer3[I0Index[i] * 4]) & 0xff);
            }
        }

        for (int i = 0; i < 20; i++)
        {
            keyOut[i % 16] ^= buffer0[i];
        }

        for (int i = 0; i < 35; i++)
        {
            keyOut[i % 16] ^= buffer2[i];
        }

        for (int i = 0; i < 210; i++)
        {
            keyOut[i % 16] ^= buffer1[i];
        }

        // 反向混淆
        for (int j = 0; j < 16; j++)
        {
            for (int i = 0; i < 16; i++)
            {
                byte x = keyOut[(int)((uint)(i - 7) % 16u)];
                byte y = keyOut[i % 16];
                byte z = keyOut[(int)((uint)(i - 37) % 16u)];
                byte w = keyOut[(int)((uint)(i - 177) % 16u)];
                keyOut[i] = (byte)(HandGarble.Rol8(x, 1) ^ y ^ HandGarble.Rol8(z, 6) ^ HandGarble.Rol8(w, 5));
            }
        }
    }
}
