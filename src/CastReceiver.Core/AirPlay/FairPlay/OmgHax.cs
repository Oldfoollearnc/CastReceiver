using static CastReceiver.Core.AirPlay.FairPlay.FairPlayTables;

namespace CastReceiver.Core.AirPlay.FairPlay;

/// <summary>
/// UxPlay <c>lib/playfair/omg_hax.c</c> + <c>lib/playfair/playfair.c</c> 的逐句移植。
///
/// 覆盖 FairPlay「PlayFair」需要的完整调用链：
/// <c>generate_session_key</c> → <c>generate_key_schedule</c> → <c>cycle</c>，
/// 以及 <c>playfair_decrypt</c> 本身。
/// （<c>decrypt_sap</c>/<c>decrypt_key</c> 在 AirPlay 镜像路径上用不到，故未移植。）
///
/// 注意 C 里大量使用 <c>((uint32_t*)block)[i]</c> 这种"把字节数组当 32 位字用"的写法，
/// 其字节序为小端；这里用 <see cref="ReadU32"/>/<see cref="WriteU32"/> 精确复现。
/// </summary>
internal static class OmgHax
{
    // ---------------------------------------------------------------- 字节序工具

    private static uint ReadU32(byte[] b, int off)
        => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v;
        b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16);
        b[off + 3] = (byte)(v >> 24);
    }

    /// <summary>C 里 <c>((unsigned char*)&word)[i]</c>：小端取第 i 个字节。</summary>
    private static byte KeyByte(uint word, int i) => (byte)(word >> (8 * i));

    // ---------------------------------------------------------------- 表索引

    /// <summary>C <c>table_index(i)</c>：返回 table_s1 中的字节偏移。</summary>
    private static int TableIndex(int i) => ((31 * i) % 0x28) << 8;

    /// <summary>C <c>message_table_index(i)</c>：返回 table_s2 中的字节偏移。</summary>
    private static int MessageTableIndex(int i) => (97 * i % 144) << 8;

    /// <summary>C <c>permute_table_2(i)</c>：返回 table_s4 中的字节偏移。</summary>
    private static int PermuteTable2(int i) => ((71 * i) % 144) << 8;

    // ---------------------------------------------------------------- 异或

    /// <summary>C <c>z_xor</c>。</summary>
    internal static void ZXor(byte[] input, int inOffset, byte[] output, int outOffset, int blocks)
    {
        for (int j = 0; j < blocks; j++)
        {
            for (int i = 0; i < 16; i++)
            {
                output[outOffset + (j * 16) + i] = (byte)(input[inOffset + (j * 16) + i] ^ ZKey[i]);
            }
        }
    }

    /// <summary>C <c>z_xor(in, out, blocks)</c>（无偏移版本）。</summary>
    internal static void ZXor(byte[] input, byte[] output, int blocks) => ZXor(input, 0, output, 0, blocks);

    /// <summary>C <c>x_xor</c>。</summary>
    internal static void XXor(byte[] input, int inOffset, byte[] output, int outOffset, int blocks)
    {
        for (int j = 0; j < blocks; j++)
        {
            for (int i = 0; i < 16; i++)
            {
                output[outOffset + (j * 16) + i] = (byte)(input[inOffset + (j * 16) + i] ^ XKey[i]);
            }
        }
    }

    /// <summary>C <c>x_xor(in, out, blocks)</c>（无偏移版本）。</summary>
    internal static void XXor(byte[] input, byte[] output, int blocks) => XXor(input, 0, output, 0, blocks);

    /// <summary>C <c>t_xor</c>。</summary>
    private static void TXor(byte[] input, byte[] output)
    {
        for (int i = 0; i < 16; i++)
        {
            output[i] = (byte)(input[i] ^ TKey[i]);
        }
    }

    private static void XorBlocks(byte[] a, int aOff, byte[] b, int bOff, byte[] output, int outOff)
    {
        for (int i = 0; i < 16; i++)
        {
            output[outOff + i] = (byte)(a[aOff + i] ^ b[bOff + i]);
        }
    }

    // ---------------------------------------------------------------- 置换

    /// <summary>C <c>permute_block_1</c>。</summary>
    private static void PermuteBlock1(byte[] block)
    {
        block[0] = TableS3[block[0]];
        block[4] = TableS3[0x400 + block[4]];
        block[8] = TableS3[0x800 + block[8]];
        block[12] = TableS3[0xc00 + block[12]];

        byte tmp = block[13];
        block[13] = TableS3[0x100 + block[9]];
        block[9] = TableS3[0xd00 + block[5]];
        block[5] = TableS3[0x900 + block[1]];
        block[1] = TableS3[0x500 + tmp];

        tmp = block[2];
        block[2] = TableS3[0xa00 + block[10]];
        block[10] = TableS3[0x200 + tmp];
        tmp = block[6];
        block[6] = TableS3[0xe00 + block[14]];
        block[14] = TableS3[0x600 + tmp];

        tmp = block[3];
        block[3] = TableS3[0xf00 + block[7]];
        block[7] = TableS3[0x300 + block[11]];
        block[11] = TableS3[0x700 + block[15]];
        block[15] = TableS3[0xb00 + tmp];
    }

    /// <summary>C <c>permute_block_2</c>（round 0..8）。</summary>
    private static void PermuteBlock2(byte[] block, int round)
    {
        block[0] = TableS4[PermuteTable2((round * 16) + 0) + block[0]];
        block[4] = TableS4[PermuteTable2((round * 16) + 4) + block[4]];
        block[8] = TableS4[PermuteTable2((round * 16) + 8) + block[8]];
        block[12] = TableS4[PermuteTable2((round * 16) + 12) + block[12]];

        byte tmp = block[13];
        block[13] = TableS4[PermuteTable2((round * 16) + 13) + block[9]];
        block[9] = TableS4[PermuteTable2((round * 16) + 9) + block[5]];
        block[5] = TableS4[PermuteTable2((round * 16) + 5) + block[1]];
        block[1] = TableS4[PermuteTable2((round * 16) + 1) + tmp];

        tmp = block[2];
        block[2] = TableS4[PermuteTable2((round * 16) + 2) + block[10]];
        block[10] = TableS4[PermuteTable2((round * 16) + 10) + tmp];
        tmp = block[6];
        block[6] = TableS4[PermuteTable2((round * 16) + 6) + block[14]];
        block[14] = TableS4[PermuteTable2((round * 16) + 14) + tmp];

        tmp = block[3];
        block[3] = TableS4[PermuteTable2((round * 16) + 3) + block[7]];
        block[7] = TableS4[PermuteTable2((round * 16) + 7) + block[11]];
        block[11] = TableS4[PermuteTable2((round * 16) + 11) + block[15]];
        block[15] = TableS4[PermuteTable2((round * 16) + 15) + tmp];
    }

    // ---------------------------------------------------------------- 密钥扩展

    /// <summary>
    /// C <c>generate_key_schedule(key_material, key_schedule)</c>。
    /// 看起来像 Rijndael 密钥扩展，但 S-box 换成了 table_s1 的不同分组。
    /// </summary>
    internal static void GenerateKeySchedule(byte[] keyMaterial, uint[,] keySchedule)
    {
        var kd = new byte[16];   // C 里是 uint32_t key_data[4]，被 buffer 按字节改写

        for (int i = 0; i < 11; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                keySchedule[i, j] = 0xdeadbeef;
            }
        }

        int ti = 0;
        TXor(keyMaterial, kd);

        for (int round = 0; round < 11; round++)
        {
            keySchedule[round, 0] = ReadU32(kd, 0);

            int t1 = TableIndex(ti);
            int t2 = TableIndex(ti + 1);
            int t3 = TableIndex(ti + 2);
            int t4 = TableIndex(ti + 3);
            ti += 4;

            kd[0] = (byte)(kd[0] ^ TableS1[t1 + kd[0x0d]] ^ IndexMangle[round]);
            kd[1] = (byte)(kd[1] ^ TableS1[t2 + kd[0x0e]]);
            kd[2] = (byte)(kd[2] ^ TableS1[t3 + kd[0x0f]]);
            kd[3] = (byte)(kd[3] ^ TableS1[t4 + kd[0x0c]]);

            keySchedule[round, 1] = ReadU32(kd, 4);
            WriteU32(kd, 4, ReadU32(kd, 4) ^ ReadU32(kd, 0));

            keySchedule[round, 2] = ReadU32(kd, 8);
            WriteU32(kd, 8, ReadU32(kd, 8) ^ ReadU32(kd, 4));

            keySchedule[round, 3] = ReadU32(kd, 12);
            WriteU32(kd, 12, ReadU32(kd, 12) ^ ReadU32(kd, 8));
        }
    }

    // ---------------------------------------------------------------- 轮函数

    /// <summary>C <c>cycle</c>：AES 变体的轮函数，用 table_s5..s8 的 T 表实现。</summary>
    internal static void Cycle(byte[] block, uint[,] keySchedule)
    {
        WriteU32(block, 0, ReadU32(block, 0) ^ keySchedule[10, 0]);
        WriteU32(block, 4, ReadU32(block, 4) ^ keySchedule[10, 1]);
        WriteU32(block, 8, ReadU32(block, 8) ^ keySchedule[10, 2]);
        WriteU32(block, 12, ReadU32(block, 12) ^ keySchedule[10, 3]);

        PermuteBlock1(block);

        for (int round = 0; round < 9; round++)
        {
            uint k0 = keySchedule[9 - round, 0];
            uint k1 = keySchedule[9 - round, 1];
            uint k2 = keySchedule[9 - round, 2];
            uint k3 = keySchedule[9 - round, 3];

            uint ptr1 = TableS5[block[3] ^ KeyByte(k0, 3)];
            uint ptr2 = TableS6[block[2] ^ KeyByte(k0, 2)];
            uint ptr3 = TableS8[block[0] ^ KeyByte(k0, 0)];
            uint ptr4 = TableS7[block[1] ^ KeyByte(k0, 1)];
            uint ab = ptr1 ^ ptr2 ^ ptr3 ^ ptr4;
            WriteU32(block, 0, ab);

            ptr2 = TableS5[block[7] ^ KeyByte(k1, 3)];
            ptr1 = TableS6[block[6] ^ KeyByte(k1, 2)];
            ptr4 = TableS7[block[5] ^ KeyByte(k1, 1)];
            ptr3 = TableS8[block[4] ^ KeyByte(k1, 0)];
            ab = ptr1 ^ ptr2 ^ ptr3 ^ ptr4;

            WriteU32(block, 4, ab);
            WriteU32(block, 8, TableS5[block[11] ^ KeyByte(k2, 3)]
                             ^ TableS6[block[10] ^ KeyByte(k2, 2)]
                             ^ TableS7[block[9] ^ KeyByte(k2, 1)]
                             ^ TableS8[block[8] ^ KeyByte(k2, 0)]);
            WriteU32(block, 12, TableS5[block[15] ^ KeyByte(k3, 3)]
                              ^ TableS6[block[14] ^ KeyByte(k3, 2)]
                              ^ TableS7[block[13] ^ KeyByte(k3, 1)]
                              ^ TableS8[block[12] ^ KeyByte(k3, 0)]);

            PermuteBlock2(block, 8 - round);
        }

        WriteU32(block, 0, ReadU32(block, 0) ^ keySchedule[0, 0]);
        WriteU32(block, 4, ReadU32(block, 4) ^ keySchedule[0, 1]);
        WriteU32(block, 8, ReadU32(block, 8) ^ keySchedule[0, 2]);
        WriteU32(block, 12, ReadU32(block, 12) ^ keySchedule[0, 3]);
    }

    // ---------------------------------------------------------------- 消息解密

    /// <summary>C <c>decryptMessage</c>：把 164 字节 fp-setup 消息解出 128 字节（4 个 mode 分支）。</summary>
    private static void DecryptMessage(byte[] messageIn, byte[] decryptedMessage)
    {
        var buffer = new byte[16];
        var keySchedule = new uint[11, 4];
        int mode = messageIn[12];

        // C 版不校验 mode，越界即 UB（会读到 message_key 之外的内存）。真实客户端只发 0..3，
        // 这里显式拒绝非法值，避免把未定义行为带进托管代码。
        if (mode is < 0 or > 3)
        {
            throw new InvalidDataException($"FairPlay 消息 mode 非法：{mode}（应为 0..3）");
        }

        GenerateKeySchedule(InitialSessionKey, keySchedule);

        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                buffer[j] = mode == 3
                    ? messageIn[(0x80 - (0x10 * i)) + j]
                    : messageIn[(0x10 * (i + 1)) + j];
            }

            for (int j = 0; j < 9; j++)
            {
                int bas = 0x80 - (0x10 * j);
                int mk = (mode * 144) + bas;

                buffer[0x0] = (byte)(TableS2[MessageTableIndex(bas + 0x0) + buffer[0x0]] ^ MessageKey[mk + 0x0]);
                buffer[0x4] = (byte)(TableS2[MessageTableIndex(bas + 0x4) + buffer[0x4]] ^ MessageKey[mk + 0x4]);
                buffer[0x8] = (byte)(TableS2[MessageTableIndex(bas + 0x8) + buffer[0x8]] ^ MessageKey[mk + 0x8]);
                buffer[0xc] = (byte)(TableS2[MessageTableIndex(bas + 0xc) + buffer[0xc]] ^ MessageKey[mk + 0xc]);

                byte tmp = buffer[0x0d];
                buffer[0xd] = (byte)(TableS2[MessageTableIndex(bas + 0xd) + buffer[0x9]] ^ MessageKey[mk + 0xd]);
                buffer[0x9] = (byte)(TableS2[MessageTableIndex(bas + 0x9) + buffer[0x5]] ^ MessageKey[mk + 0x9]);
                buffer[0x5] = (byte)(TableS2[MessageTableIndex(bas + 0x5) + buffer[0x1]] ^ MessageKey[mk + 0x5]);
                buffer[0x1] = (byte)(TableS2[MessageTableIndex(bas + 0x1) + tmp] ^ MessageKey[mk + 0x1]);

                tmp = buffer[0x02];
                buffer[0x2] = (byte)(TableS2[MessageTableIndex(bas + 0x2) + buffer[0xa]] ^ MessageKey[mk + 0x2]);
                buffer[0xa] = (byte)(TableS2[MessageTableIndex(bas + 0xa) + tmp] ^ MessageKey[mk + 0xa]);
                tmp = buffer[0x06];
                buffer[0x6] = (byte)(TableS2[MessageTableIndex(bas + 0x6) + buffer[0xe]] ^ MessageKey[mk + 0x6]);
                buffer[0xe] = (byte)(TableS2[MessageTableIndex(bas + 0xe) + tmp] ^ MessageKey[mk + 0xe]);

                tmp = buffer[0x3];
                buffer[0x3] = (byte)(TableS2[MessageTableIndex(bas + 0x3) + buffer[0x7]] ^ MessageKey[mk + 0x3]);
                buffer[0x7] = (byte)(TableS2[MessageTableIndex(bas + 0x7) + buffer[0xb]] ^ MessageKey[mk + 0x7]);
                buffer[0xb] = (byte)(TableS2[MessageTableIndex(bas + 0xb) + buffer[0xf]] ^ MessageKey[mk + 0xb]);
                buffer[0xf] = (byte)(TableS2[MessageTableIndex(bas + 0xf) + tmp] ^ MessageKey[mk + 0xf]);

                // C 里 block[] 与 buffer[] 是同一块内存；四段读写互不重叠，先算完再写回等价
                uint w0 = TableS9[0x000 + buffer[0x0]] ^ TableS9[0x100 + buffer[0x1]] ^ TableS9[0x200 + buffer[0x2]] ^ TableS9[0x300 + buffer[0x3]];
                uint w1 = TableS9[0x000 + buffer[0x4]] ^ TableS9[0x100 + buffer[0x5]] ^ TableS9[0x200 + buffer[0x6]] ^ TableS9[0x300 + buffer[0x7]];
                uint w2 = TableS9[0x000 + buffer[0x8]] ^ TableS9[0x100 + buffer[0x9]] ^ TableS9[0x200 + buffer[0xa]] ^ TableS9[0x300 + buffer[0xb]];
                uint w3 = TableS9[0x000 + buffer[0xc]] ^ TableS9[0x100 + buffer[0xd]] ^ TableS9[0x200 + buffer[0xe]] ^ TableS9[0x300 + buffer[0xf]];
                WriteU32(buffer, 0, w0);
                WriteU32(buffer, 4, w1);
                WriteU32(buffer, 8, w2);
                WriteU32(buffer, 12, w3);
            }

            buffer[0x0] = TableS10[(0x0 << 8) + buffer[0x0]];
            buffer[0x4] = TableS10[(0x4 << 8) + buffer[0x4]];
            buffer[0x8] = TableS10[(0x8 << 8) + buffer[0x8]];
            buffer[0xc] = TableS10[(0xc << 8) + buffer[0xc]];

            byte t = buffer[0x0d];
            buffer[0xd] = TableS10[(0xd << 8) + buffer[0x9]];
            buffer[0x9] = TableS10[(0x9 << 8) + buffer[0x5]];
            buffer[0x5] = TableS10[(0x5 << 8) + buffer[0x1]];
            buffer[0x1] = TableS10[(0x1 << 8) + t];

            t = buffer[0x02];
            buffer[0x2] = TableS10[(0x2 << 8) + buffer[0xa]];
            buffer[0xa] = TableS10[(0xa << 8) + t];
            t = buffer[0x06];
            buffer[0x6] = TableS10[(0x6 << 8) + buffer[0xe]];
            buffer[0xe] = TableS10[(0xe << 8) + t];

            t = buffer[0x3];
            buffer[0x3] = TableS10[(0x3 << 8) + buffer[0x7]];
            buffer[0x7] = TableS10[(0x7 << 8) + buffer[0xb]];
            buffer[0xb] = TableS10[(0xb << 8) + buffer[0xf]];
            buffer[0xf] = TableS10[(0xf << 8) + t];

            if (mode == 2 || mode == 1 || mode == 0)
            {
                if (i > 0)
                {
                    XorBlocks(buffer, 0, messageIn, 0x10 * i, decryptedMessage, 0x10 * i);
                }
                else
                {
                    XorBlocks(buffer, 0, MessageIv, mode * 16, decryptedMessage, 0);
                }
            }
            else
            {
                if (i < 7)
                {
                    XorBlocks(buffer, 0, messageIn, 0x70 - (0x10 * i), decryptedMessage, 0x70 - (0x10 * i));
                }
                else
                {
                    XorBlocks(buffer, 0, MessageIv, mode * 16, decryptedMessage, 0x70 - (0x10 * i));
                }
            }
        }
    }

    // ---------------------------------------------------------------- 会话密钥

    /// <summary>C <c>generate_session_key(oldSap, messageIn, sessionKey)</c>。</summary>
    internal static void GenerateSessionKey(byte[] oldSap, byte[] messageIn, byte[] sessionKey)
    {
        var decryptedMessage = new byte[128];
        var newSap = new byte[320];
        var md5 = new byte[16];

        DecryptMessage(messageIn, decryptedMessage);

        Array.Copy(StaticSource1, 0, newSap, 0x000, 0x11);
        Array.Copy(decryptedMessage, 0, newSap, 0x011, 0x80);
        Array.Copy(oldSap, 0x80, newSap, 0x091, 0x80);
        Array.Copy(StaticSource2, 0, newSap, 0x111, 0x2f);
        Array.Copy(InitialSessionKey, 0, sessionKey, 0, 16);

        for (int round = 0; round < 5; round++)
        {
            var baseBlock = new byte[64];
            Array.Copy(newSap, round * 64, baseBlock, 0, 64);

            ModifiedMd5.Compute(baseBlock, sessionKey, md5);
            SapHash.Compute(baseBlock, sessionKey);

            for (int i = 0; i < 4; i++)
            {
                WriteU32(sessionKey, i * 4, ReadU32(sessionKey, i * 4) + ReadU32(md5, i * 4));
            }
        }

        for (int i = 0; i < 16; i += 4)
        {
            (sessionKey[i], sessionKey[i + 3]) = (sessionKey[i + 3], sessionKey[i]);
            (sessionKey[i + 1], sessionKey[i + 2]) = (sessionKey[i + 2], sessionKey[i + 1]);
        }

        for (int i = 0; i < 16; i++)
        {
            sessionKey[i] ^= 121;
        }
    }

    // ---------------------------------------------------------------- 入口

    /// <summary>
    /// C <c>playfair_decrypt(message3, cipherText, keyOut)</c>：
    /// 由 164 字节 fp-setup 消息与 72 字节 <c>ekey</c> 推出 16 字节 AES 密钥。
    /// </summary>
    internal static void PlayFairDecrypt(byte[] message3, byte[] cipherText, byte[] keyOut)
    {
        var sapKey = new byte[16];
        var keySchedule = new uint[11, 4];
        var blockIn = new byte[16];

        GenerateSessionKey(DefaultSap, message3, sapKey);
        GenerateKeySchedule(sapKey, keySchedule);

        ZXor(cipherText, 56, blockIn, 0, 1);
        Cycle(blockIn, keySchedule);

        for (int i = 0; i < 16; i++)
        {
            keyOut[i] = (byte)(blockIn[i] ^ cipherText[16 + i]);
        }

        XXor(keyOut, keyOut, 1);
        ZXor(keyOut, keyOut, 1);
    }
}
