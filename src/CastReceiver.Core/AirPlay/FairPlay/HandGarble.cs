using System.Buffers.Binary;

namespace CastReceiver.Core.AirPlay.FairPlay;

// CS0675（"在经符号扩展的操作数上使用了按位或"）：这里是**刻意**保留 C 的语义 ——
// C 里 `~buffer1[105]` 会把 unsigned char 提升成 int 再取反，于是带上高位的符号扩展，
// 随后才与掩码相与。改成"先截断成小无符号类型"会改变结果。
// 这些表达式的位型与 C 版逐字节一致，已由 tools/airplay-port/vector-verify 的 43 项参考向量验证。
#pragma warning disable CS0675

/// <summary>
/// UxPlay <c>lib/playfair/hand_garble.c</c> 的逐句移植（<c>garble</c>）。
///
/// 这是 FairPlay「PlayFair」逆向出来的核心混淆函数：443 行、约 150 条语句，
/// 宽度与符号极其敏感（<c>unsigned char</c> 存回时截断、<c>unsigned int</c> 按 2^32 回绕、
/// <c>~</c> 提升成 int 后再与掩码相与）。因此这里刻意与 C 版**逐句对应**，
/// 不做任何"化简"，并用参考向量做逐字节比对（见 AirPlayFairPlayTests）。
///
/// 移植约定：
/// <list type="bullet">
/// <item><c>byte[]</c> 对应 C 的 <c>unsigned char*</c>，每次存回都截断到 8 位（<c>(byte)</c>）。</item>
/// <item>中间量用 <c>uint</c>/<c>int</c> 与 C 保持一致；<c>+ - * &amp; | ^ ~</c> 的补码位型与符号无关，
/// 只有 <c>/</c>、<c>%</c>、<c>&gt;&gt;</c> 需要区分，凡是这类位置都显式标注。</item>
/// <item>C 里 <c>weird_*</c>/<c>rol8*</c> 的首参是 <c>uint8_t</c>，传入更宽的值会被截断，故调用处补 <c>(byte)</c>。</item>
/// </list>
/// </summary>
internal static class HandGarble
{
    /// <summary>C <c>rol8(unsigned char, int)</c>：8 位循环左移，结果存回 unsigned char。</summary>
    internal static byte Rol8(byte input, int count)
        => (byte)(((input << count) & 0xff) | ((input & 0xff) >> (8 - count)));

    /// <summary>C <c>rol8x</c>：与 <see cref="Rol8"/> 类似但返回 uint32，且 count==0 时返回原值（不是 0）。</summary>
    internal static uint Rol8x(byte input, int count)
        => (uint)((input << count) | (input >> (8 - count)));

    /// <summary>C <c>weird_ror8</c>：count==0 时返回 0（这是它的"怪"之处，必须保留）。</summary>
    internal static uint WeirdRor8(byte input, int count)
        => count == 0 ? 0u : (uint)(((input >> count) & 0xff) | ((input & 0xff) << (8 - count)));

    /// <summary>C <c>weird_rol8</c>：count==0 时返回 0。</summary>
    internal static uint WeirdRol8(byte input, int count)
        => count == 0 ? 0u : (uint)(((input << count) & 0xff) | ((input & 0xff) >> (8 - count)));

    /// <summary>C <c>weird_rol32</c>：count==0 时返回 0；注意用的是 8 位掩码与 8-count 位移。</summary>
    internal static uint WeirdRol32(byte input, int count)
        => count == 0 ? 0u : (uint)((input << count) ^ (input >> (8 - count)));

    /// <summary>
    /// C <c>garble(buffer0, buffer1, buffer2, buffer3, buffer4)</c>。
    /// 参数即 C 里那 5 个缓冲区的别名，函数只就地修改它们。
    /// </summary>
    internal static void Garble(byte[] b0, byte[] b1, byte[] b2, byte[] b3, byte[] b4)
    {
        uint tmp, tmp2, tmp3;
        uint A, B, C, D, E, M, J, G, F, H, K, R, S, T, U, V, W, X, Y, Z;

        // buffer2[12] = 0x14 + (((buffer1[64] & 92) | ((buffer1[99] / 3) & 35)) & buffer4[rol8x(buffer4[(buffer1[206] % 21)],4) % 21]);
        b2[12] = (byte)(0x14 + (((b1[64] & 92) | ((b1[99] / 3) & 35)) & b4[(int)(Rol8x(b4[b1[206] % 21], 4) % 21u)]));

        // buffer1[4] = (buffer1[99] / 5) * (buffer1[99] / 5) * 2;
        b1[4] = (byte)((b1[99] / 5) * (b1[99] / 5) * 2);

        // buffer2[34] = 0xb8;
        b2[34] = 0xb8;

        // buffer1[153] ^= (buffer2[buffer1[203] % 35] * buffer2[buffer1[203] % 35] * buffer1[190]);
        b1[153] = (byte)(b1[153] ^ (b2[b1[203] % 35] * b2[b1[203] % 35] * b1[190]));

        // buffer0[3] -= (((buffer4[buffer1[205] % 21]>>1) & 80) | 0xe6440);
        b0[3] = (byte)(b0[3] - (((b4[b1[205] % 21] >> 1) & 80) | 0xe6440));

        // buffer0[16] = 0x93;
        b0[16] = 0x93;

        // buffer0[13] = 0x62;
        b0[13] = 0x62;

        // buffer1[33] -= (buffer4[buffer1[36] % 21] & 0xf6);
        b1[33] = (byte)(b1[33] - (b4[b1[36] % 21] & 0xf6));

        // tmp2 = buffer2[buffer1[67] % 35];
        tmp2 = b2[b1[67] % 35];

        // buffer2[12] = 0x07;
        b2[12] = 0x07;

        // tmp = buffer0[buffer1[181] % 20];
        tmp = b0[b1[181] % 20];

        // buffer1[2] -= 3136;
        b1[2] = (byte)(b1[2] - 3136);

        // buffer0[19] = buffer4[buffer1[58] % 21];
        b0[19] = b4[b1[58] % 21];

        // buffer3[0] = 92 - buffer2[buffer1[32] % 35];
        b3[0] = (byte)(92 - b2[b1[32] % 35]);

        // buffer3[4] = buffer2[buffer1[15] % 35] + 0x9e;
        b3[4] = (byte)(b2[b1[15] % 35] + 0x9e);

        // buffer1[34] += (buffer4[((buffer2[buffer1[15] % 35] + 0x9e) & 0xff) % 21] / 5);
        b1[34] = (byte)(b1[34] + (b4[((b2[b1[15] % 35] + 0x9e) & 0xff) % 21] / 5));

        // buffer0[19] += 0xfffffee6 - ((buffer0[buffer3[4] % 20]>>1) & 102);
        b0[19] = (byte)(b0[19] + (0xfffffee6u - (uint)((b0[b3[4] % 20] >> 1) & 102)));

        // buffer1[15] = (3*(((buffer1[72] >> (buffer4[buffer1[190] % 21] & 7)) ^ (buffer1[72] << ((7 - (buffer4[buffer1[190] % 21]-1)&7)))) - (3*buffer4[buffer1[126] % 21]))) ^ buffer1[15];
        // buffer1[15] = (3*(((buffer1[72] >> (B & 7)) ^ (buffer1[72] << ((7 - (B-1)) & 7))) - (3*buffer4[buffer1[126] % 21]))) ^ buffer1[15];
        // 注意括号层级：C 是 3 * ( (A ^ C) - D )，减法在乘法**里面**。
        int b98 = b4[b1[190] % 21];
        int a98 = b1[72] >> (b98 & 7);
        int c98 = b1[72] << ((7 - (b98 - 1)) & 7);
        int d98 = 3 * b4[b1[126] % 21];
        b1[15] = (byte)((3 * ((a98 ^ c98) - d98)) ^ b1[15]);

        // buffer0[15] ^= buffer2[buffer1[181] % 35] * buffer2[buffer1[181] % 35] * buffer2[buffer1[181] % 35];
        b0[15] = (byte)(b0[15] ^ (b2[b1[181] % 35] * b2[b1[181] % 35] * b2[b1[181] % 35]));

        // buffer2[4] ^= buffer1[202]/3;
        b2[4] = (byte)(b2[4] ^ (b1[202] / 3));

        // A = 92 - buffer0[buffer3[0] % 20];
        A = (uint)(92 - b0[b3[0] % 20]);

        // E = (A & 0xc6) | (~buffer1[105] & 0xc6) | (A & (~buffer1[105]));
        E = (A & 0xc6u) | (uint)((~b1[105]) & 0xc6) | (A & (uint)(~b1[105]));

        // buffer2[1] += (E*E*E);
        b2[1] = (byte)(b2[1] + (E * E * E));

        // buffer0[19] ^= ((224 | (buffer4[buffer1[92] % 21] & 27)) * buffer2[buffer1[41] % 35]) / 3;
        b0[19] = (byte)(b0[19] ^ (uint)(((224 | (b4[b1[92] % 21] & 27)) * b2[b1[41] % 35]) / 3));

        // buffer1[140] += weird_ror8(92, buffer1[5] & 7);
        b1[140] = (byte)(b1[140] + WeirdRor8(92, b1[5] & 7));

        // buffer2[12] += ((((~buffer1[4]) ^ buffer2[buffer1[12] % 35]) | buffer1[182]) & 192) | (((~buffer1[4]) ^ buffer2[buffer1[12] % 35]) & buffer1[182]);
        b2[12] = (byte)(b2[12] + (((((~b1[4]) ^ b2[b1[12] % 35]) | b1[182]) & 192) | (((~b1[4]) ^ b2[b1[12] % 35]) & b1[182])));

        // buffer1[36] += 125;
        b1[36] = (byte)(b1[36] + 125);

        // buffer1[124] = rol8x((((74 & buffer1[138]) | ((74 | buffer1[138]) & buffer0[15])) & buffer0[buffer1[43] % 20]) | (((74 & buffer1[138]) | ((74 | buffer1[138]) & buffer0[15]) | buffer0[buffer1[43] % 20]) & 95), 4);
        b1[124] = (byte)Rol8x((byte)((((74 & b1[138]) | ((74 | b1[138]) & b0[15])) & b0[b1[43] % 20]) | (((74 & b1[138]) | ((74 | b1[138]) & b0[15]) | b0[b1[43] % 20]) & 95)), 4);

        // buffer3[8] = ((((buffer0[buffer3[4] % 20] & 95)) & ((buffer4[buffer1[68] % 21] & 46) << 1)) | 16) ^ 92;
        b3[8] = (byte)(((((b0[b3[4] % 20] & 95)) & ((b4[b1[68] % 21] & 46) << 1)) | 16) ^ 92);

        // A = buffer1[177] + buffer4[buffer1[79] % 21];
        A = (uint)(b1[177] + b4[b1[79] % 21]);

        // D = (((A >> 1) | ((3 * buffer1[148]) / 5)) & buffer2[1]) | ((A >> 1) & ((3 * buffer1[148])/5));
        D = (((A >> 1) | (uint)((3 * b1[148]) / 5)) & b2[1]) | ((A >> 1) & (uint)((3 * b1[148]) / 5));

        // buffer3[12] = ((-34 - D));      // D 是 unsigned，-34 先转成 unsigned 再减
        b3[12] = (byte)(unchecked((uint)(-34)) - D);

        // A = 8 - ((buffer2[22] & 7));
        A = (uint)(8 - (b2[22] & 7));

        // B = (buffer1[33] >> (A & 7));
        B = (uint)(b1[33] >> (int)(A & 7));

        // C = buffer1[33] << (buffer2[22] & 7);
        C = (uint)(b1[33] << (int)(b2[22] & 7));

        // buffer2[16] += ((buffer2[buffer3[0] % 35] & 159) | buffer0[buffer3[4] % 20] | 8) - ((B^C) | 128);
        b2[16] = (byte)(b2[16] + ((uint)((b2[b3[0] % 35] & 159) | b0[b3[4] % 20] | 8) - ((B ^ C) | 128u)));

        // buffer0[14] ^= buffer2[buffer3[12] % 35];
        b0[14] = (byte)(b0[14] ^ b2[b3[12] % 35]);

        // A = weird_rol8(buffer4[buffer0[buffer1[201] % 20] % 21], ((buffer2[buffer1[112] % 35] << 1) & 7));
        A = WeirdRol8(b4[b0[b1[201] % 20] % 21], (b2[b1[112] % 35] << 1) & 7);

        // D = (buffer0[buffer1[208] % 20] & 131) | (buffer0[buffer1[164] % 20] & 124);
        D = (uint)((b0[b1[208] % 20] & 131) | (b0[b1[164] % 20] & 124));

        // buffer1[19] += (A & (D/5)) | ((A | (D/5)) & 37);
        b1[19] = (byte)(b1[19] + ((A & (D / 5)) | ((A | (D / 5)) & 37u)));

        // buffer2[8] = weird_ror8(140, ((buffer4[buffer1[45] % 21] + 92) * (buffer4[buffer1[45] % 21] + 92)) & 7);
        b2[8] = (byte)WeirdRor8(140, ((b4[b1[45] % 21] + 92) * (b4[b1[45] % 21] + 92)) & 7);

        // buffer1[190] = 56;
        b1[190] = 56;

        // buffer2[8] ^= buffer3[0];
        b2[8] = (byte)(b2[8] ^ b3[0]);

        // buffer1[53] = ~((buffer0[buffer1[83] % 20] | 204)/5);
        b1[53] = (byte)(~((b0[b1[83] % 20] | 204) / 5));

        // buffer0[13] += buffer0[buffer1[41] % 20];
        b0[13] = (byte)(b0[13] + b0[b1[41] % 20]);

        // buffer0[10] = ((buffer2[buffer3[0] % 35] & buffer1[2]) | ((buffer2[buffer3[0] % 35] | buffer1[2]) & buffer3[12])) / 15;
        b0[10] = (byte)(((b2[b3[0] % 35] & b1[2]) | ((b2[b3[0] % 35] | b1[2]) & b3[12])) / 15);

        // A = (((56 | (buffer4[buffer1[2] % 21] & 68)) | buffer2[buffer3[8] % 35]) & 42) | (((buffer4[buffer1[2] % 21] & 68) | 56) & buffer2[buffer3[8] % 35]);
        A = (uint)((((56 | (b4[b1[2] % 21] & 68)) | b2[b3[8] % 35]) & 42) | (((b4[b1[2] % 21] & 68) | 56) & b2[b3[8] % 35]));

        // buffer3[16] = (A*A) + 110;
        b3[16] = (byte)((A * A) + 110);

        // buffer3[20] = 202 - buffer3[16];
        b3[20] = (byte)(202 - b3[16]);

        // buffer3[24] = buffer1[151];
        b3[24] = b1[151];

        // buffer2[13] ^= buffer4[buffer3[0] % 21];
        b2[13] = (byte)(b2[13] ^ b4[b3[0] % 21]);

        // B = ((buffer2[buffer1[179] % 35] - 38) & 177) | (buffer3[12] & 177);
        B = (uint)(((b2[b1[179] % 35] - 38) & 177) | (b3[12] & 177));

        // C = ((buffer2[buffer1[179] % 35] - 38)) & buffer3[12];
        C = (uint)(((b2[b1[179] % 35] - 38)) & b3[12]);

        // buffer3[28] = 30 + ((B | C) * (B | C));
        b3[28] = (byte)(30 + ((B | C) * (B | C)));

        // buffer3[32] = buffer3[28] + 62;
        b3[32] = (byte)(b3[28] + 62);

        // A = ((buffer3[20] + (buffer3[0] & 74)) | ~buffer4[buffer3[0] % 21]) & 121;
        A = (uint)(((b3[20] + (b3[0] & 74)) | ~b4[b3[0] % 21]) & 121);

        // B = ((buffer3[20] + (buffer3[0] & 74)) & ~buffer4[buffer3[0] % 21]);
        B = (uint)((b3[20] + (b3[0] & 74)) & ~b4[b3[0] % 21]);

        // tmp3 = (A|B);
        tmp3 = A | B;

        // C = ((((A|B) ^ 0xffffffa6) | buffer3[0]) & 4) | (((A|B) ^ 0xffffffa6) & buffer3[0]);
        C = ((((A | B) ^ 0xffffffa6u) | b3[0]) & 4u) | (((A | B) ^ 0xffffffa6u) & b3[0]);

        // buffer1[47] = (buffer2[buffer1[89] % 35] + C) ^ buffer1[47];
        b1[47] = (byte)((b2[b1[89] % 35] + C) ^ b1[47]);

        // buffer3[36] = ((rol8((tmp & 179) + 68, 2) & buffer0[3]) | (tmp2 & ~buffer0[3])) - 15;
        b3[36] = (byte)((((uint)Rol8((byte)((tmp & 179) + 68), 2) & b0[3]) | (tmp2 & (uint)(~b0[3]))) - 15u);

        // buffer1[123] ^= 221;
        b1[123] = (byte)(b1[123] ^ 221);

        // A = ((buffer4[buffer3[0] % 21]) / 3) - buffer2[buffer3[4] % 35];
        A = (uint)((b4[b3[0] % 21] / 3) - b2[b3[4] % 35]);

        // C = (((buffer3[0] & 163) + 92) & 246) | (buffer3[0] & 92);
        C = (uint)((((b3[0] & 163) + 92) & 246) | (b3[0] & 92));

        // E = ((C | buffer3[24]) & 54) | (C & buffer3[24]);
        E = ((C | b3[24]) & 54u) | (C & b3[24]);

        // buffer3[40] = A - E;
        b3[40] = (byte)(A - E);

        // buffer3[44] = tmp3 ^ 81 ^ (((buffer3[0] >> 1) & 101) + 26);
        b3[44] = (byte)(tmp3 ^ 81u ^ (uint)(((b3[0] >> 1) & 101) + 26));

        // buffer3[48] = buffer2[buffer3[4] % 35] & 27;
        b3[48] = (byte)(b2[b3[4] % 35] & 27);

        // buffer3[52] = 27;
        b3[52] = 27;

        // buffer3[56] = 199;
        b3[56] = 199;

        // buffer3[64] = buffer3[4] + ( ... ) & (~buffer3[52]) | buffer3[48];
        b3[64] = (byte)(b3[4] + (((((((b3[40] | b3[24]) & 177) | (b3[40] & b3[24])) & ((((b4[b3[0] % 20] & 177) | 176)) | ((b4[b3[0] % 21]) & ~3))) | ((((b3[40] & b3[24]) | ((b3[40] | b3[24]) & 177)) & 199) | ((((b4[b3[0] % 21] & 1) + 176) | (b4[b3[0] % 21] & ~3)) & b3[56]))) & (~b3[52])) | b3[48]));

        // buffer2[33] ^= buffer1[26];
        b2[33] = (byte)(b2[33] ^ b1[26]);

        // buffer1[106] ^= buffer3[20] ^ 133;
        b1[106] = (byte)(b1[106] ^ b3[20] ^ 133);

        // buffer2[30] = ((buffer3[64] / 3) - (275 | (buffer3[0] & 247))) ^ buffer0[buffer1[122] % 20];
        b2[30] = (byte)(((b3[64] / 3) - (275 | (b3[0] & 247))) ^ b0[b1[122] % 20]);

        // buffer1[22] = (buffer2[buffer1[90] % 35] & 95) | 68;
        b1[22] = (byte)((b2[b1[90] % 35] & 95) | 68);

        // A = (buffer4[buffer3[36] % 21] & 184) | (buffer2[buffer3[44] % 35] & ~184);
        A = (uint)((b4[b3[36] % 21] & 184) | (b2[b3[44] % 35] & ~184));

        // buffer2[18] += ((A*A*A) >> 1);
        b2[18] = (byte)(b2[18] + ((A * A * A) >> 1));

        // buffer2[5] -= buffer4[buffer1[92] % 21];
        b2[5] = (byte)(b2[5] - b4[b1[92] % 21]);

        // A = (((buffer1[41] & ~24)|(buffer2[buffer1[183] % 35] & 24)) & (buffer3[16] + 53)) | (buffer3[20] & buffer2[buffer3[20] % 35]);
        A = (uint)((((b1[41] & ~24) | (b2[b1[183] % 35] & 24)) & (b3[16] + 53)) | (b3[20] & b2[b3[20] % 35]));

        // B = (buffer1[17] & (~buffer3[44])) | (buffer0[buffer1[59] % 20] & buffer3[44]);
        B = (uint)((b1[17] & (~b3[44])) | (b0[b1[59] % 20] & b3[44]));

        // buffer2[18] ^= (A*B);
        b2[18] = (byte)(b2[18] ^ (A * B));

        // A = weird_ror8(buffer1[11], buffer2[buffer1[28] % 35] & 7) & 7;
        A = WeirdRor8(b1[11], b2[b1[28] % 35] & 7) & 7u;

        // B = (((buffer0[buffer1[93] % 20] & ~buffer0[14]) | (buffer0[14] & 150)) & ~28) | (buffer1[7] & 28);
        B = (uint)((((b0[b1[93] % 20] & ~b0[14]) | (b0[14] & 150)) & ~28) | (b1[7] & 28));

        // buffer2[22] = (((((B | weird_rol8(buffer2[buffer3[0] % 35], A)) & buffer2[33]) | (B & weird_rol8(buffer2[buffer3[0] % 35], A))) + 74) & 0xff);
        b2[22] = (byte)(((((B | WeirdRol8(b2[b3[0] % 35], (int)A)) & b2[33]) | (B & WeirdRol8(b2[b3[0] % 35], (int)A))) + 74u) & 0xffu);

        // A = buffer4[(buffer0[buffer1[39] % 20] ^ 217) % 21]; // X5
        A = b4[(b0[b1[39] % 20] ^ 217) % 21];

        // buffer0[15] -= ((((buffer3[20] | buffer3[0]) & 214) | (buffer3[20] & buffer3[0])) & A) | ((((buffer3[20] | buffer3[0]) & 214) | (buffer3[20] & buffer3[0]) | A) & buffer3[32]);
        // X = ((((buffer3[20] | buffer3[0]) & 214) | (buffer3[20] & buffer3[0]))
        uint x15 = (uint)((((b3[20] | b3[0]) & 214) | (b3[20] & b3[0])));
        b0[15] = (byte)(b0[15] - ((x15 & A) | ((x15 | A) & b3[32])));

        // B = (((buffer2[buffer1[57] % 35] & buffer0[buffer3[64] % 20]) | ((buffer0[buffer3[64] % 20] | buffer2[buffer1[57] % 35]) & 95) | (buffer3[64] & 45) | 82) & 32);
        B = (uint)((((b2[b1[57] % 35] & b0[b3[64] % 20]) | ((b0[b3[64] % 20] | b2[b1[57] % 35]) & 95) | (b3[64] & 45) | 82) & 32));

        // C = ((buffer2[buffer1[57] % 35] & buffer0[buffer3[64] % 20]) | ((buffer2[buffer1[57] % 35] | buffer0[buffer3[64] % 20]) & 95)) & ((buffer3[64] & 45) | 82);
        C = (uint)(((b2[b1[57] % 35] & b0[b3[64] % 20]) | ((b2[b1[57] % 35] | b0[b3[64] % 20]) & 95)) & ((b3[64] & 45) | 82));

        // D = ((((buffer3[0]/3) - (buffer3[64]|buffer1[22]))) ^ (buffer3[28] + 62) ^ ((B|C)));
        D = (uint)((((b3[0] / 3) - (b3[64] | b1[22]))) ^ (b3[28] + 62) ^ ((B | C)));

        // T = buffer0[(D & 0xff) % 20];
        T = b0[(int)((D & 0xffu) % 20u)];

        // buffer3[68] = (buffer0[buffer1[99] % 20] * buffer0[buffer1[99] % 20] * buffer0[buffer1[99] % 20] * buffer0[buffer1[99] % 20]) | buffer2[buffer3[64] % 35];
        b3[68] = (byte)((b0[b1[99] % 20] * b0[b1[99] % 20] * b0[b1[99] % 20] * b0[b1[99] % 20]) | b2[b3[64] % 35]);

        // U = buffer0[buffer1[50] % 20];
        U = b0[b1[50] % 20];

        // W = buffer2[buffer1[138] % 35];
        W = b2[b1[138] % 35];

        // X = buffer4[buffer1[39] % 21];
        X = b4[b1[39] % 21];

        // Y = buffer0[buffer1[4] % 20];
        Y = b0[b1[4] % 20];

        // Z = buffer4[buffer1[202] % 21];
        Z = b4[b1[202] % 21];

        // V = buffer0[buffer1[151] % 20];
        V = b0[b1[151] % 20];

        // S = buffer2[buffer1[14] % 35];
        S = b2[b1[14] % 35];

        // R = buffer0[buffer1[145] % 20];
        R = b0[b1[145] % 20];

        // A = (buffer2[buffer3[68] % 35] & buffer0[buffer1[209] % 20]) | ((buffer2[buffer3[68] % 35] | buffer0[buffer1[209] % 20]) & 24);
        A = (uint)((b2[b3[68] % 35] & b0[b1[209] % 20]) | ((b2[b3[68] % 35] | b0[b1[209] % 20]) & 24));

        // B = weird_rol8(buffer4[buffer1[127] % 21], buffer2[buffer3[68] % 35] & 7);
        B = WeirdRol8(b4[b1[127] % 21], b2[b3[68] % 35] & 7);

        // C = (A & buffer0[10]) | (B & ~buffer0[10]);
        C = (A & b0[10]) | (B & (uint)(~b0[10]));

        // D = 7 ^ (buffer4[buffer2[buffer3[36] % 35] % 21] << 1);
        D = (uint)(7 ^ (b4[b2[b3[36] % 35] % 21] << 1));

        // buffer3[72] = (C & 71) | (D & ~71);
        b3[72] = (byte)((C & 71u) | (D & ~71u));

        // buffer2[2] += (((buffer0[buffer3[20] % 20] << 1) & 159) | (buffer4[buffer1[190] % 21] & ~159)) & ((((buffer4[buffer3[64] % 21] & 110) | (buffer0[buffer1[25] % 20] & ~110)) & ~150) | (buffer1[25] & 150));
        b2[2] = (byte)(b2[2] + (uint)((((b0[b3[20] % 20] << 1) & 159) | (b4[b1[190] % 21] & ~159)) & ((((b4[b3[64] % 21] & 110) | (b0[b1[25] % 20] & ~110)) & ~150) | (b1[25] & 150))));

        // buffer2[14] -= ((buffer2[buffer3[20] % 35] & (buffer3[72] ^ buffer2[buffer1[100] % 35])) & ~34) | (buffer1[97] & 34);
        b2[14] = (byte)(b2[14] - (uint)(((b2[b3[20] % 35] & (b3[72] ^ b2[b1[100] % 35])) & ~34) | (b1[97] & 34)));

        // buffer0[17] = 115;
        b0[17] = 115;

        // buffer1[23] ^= ( ... ) << 1;
        // Y = ((buffer4[buffer1[17] % 21] | buffer0[buffer3[20] % 20]) & buffer3[72]) | (buffer4[...] & buffer0[...])
        int y23 = ((b4[b1[17] % 21] | b0[b3[20] % 20]) & b3[72]) | (b4[b1[17] % 21] & b0[b3[20] % 20]);
        int q23 = b1[50] / 3;
        b1[23] = (byte)(b1[23] ^ (((y23 & q23) | ((y23 | q23) & 246)) << 1));

        // buffer0[13] = ( ... ) >> 1;
        b0[13] = (byte)((((((b0[b3[40] % 20] | b1[10]) & 82) | (b0[b3[40] % 20] & b1[10])) & 209) |
                          ((b0[b1[39] % 20] << 1) & 46)) >> 1);

        // buffer2[33] -= buffer1[113] & 9;
        b2[33] = (byte)(b2[33] - (b1[113] & 9));

        // buffer2[28] -= ((((2 | (buffer1[110] & 222)) >> 1) & ~223) | (buffer3[20] & 223));
        b2[28] = (byte)(b2[28] - (uint)(((((2 | (b1[110] & 222)) >> 1) & ~223) | (b3[20] & 223))));

        // J = weird_rol8((V | Z), (U & 7));
        J = WeirdRol8((byte)(V | Z), (int)(U & 7u));

        // A = (buffer2[16] & T) | (W & (~buffer2[16]));
        A = (uint)((b2[16] & T) | (W & (~b2[16])));

        // B = (buffer1[33] & 17) | (X & ~17);
        B = (uint)((b1[33] & 17) | (X & ~17));

        // E = ((Y | ((A+B) / 5)) & 147) | (Y & ((A+B) / 5));
        E = ((Y | ((A + B) / 5)) & 147u) | (Y & ((A + B) / 5));

        // M = (buffer3[40] & buffer4[((buffer3[8] + J + E) & 0xff) % 21]) | ((buffer3[40] | buffer4[...]) & buffer2[23]);
        M = (uint)((b3[40] & b4[(int)((b3[8] + J + E) & 0xffu) % 21]) |
                   ((b3[40] | b4[(int)((b3[8] + J + E) & 0xffu) % 21]) & b2[23]));

        // buffer0[15] = (((buffer4[buffer3[20] % 21] - 48) & (~buffer1[184])) | ((buffer4[buffer3[20] % 21] - 48) & 189) | (189 & ~buffer1[184])) & (M*M*M);
        b0[15] = (byte)((uint)(((b4[b3[20] % 21] - 48) & (~b1[184])) | ((b4[b3[20] % 21] - 48) & 189) | (189 & ~b1[184])) & (M * M * M));

        // buffer2[22] += buffer1[183];
        b2[22] = (byte)(b2[22] + b1[183]);

        // buffer3[76] = (3 * buffer4[buffer1[1] % 21]) ^ buffer3[0];
        b3[76] = (byte)((3 * b4[b1[1] % 21]) ^ b3[0]);

        // A = buffer2[((buffer3[8] + (J + E)) & 0xff) % 35];
        A = b2[(int)((b3[8] + (J + E)) & 0xffu) % 35];

        // F = (((buffer4[buffer1[178] % 21] & A) | ((buffer4[buffer1[178] % 21] | A) & 209)) * buffer0[buffer1[13] % 20]) * (buffer4[buffer1[26] % 21] >> 1);
        F = (uint)((((b4[b1[178] % 21] & A) | ((b4[b1[178] % 21] | A) & 209)) * b0[b1[13] % 20]) * (b4[b1[26] % 21] >> 1));

        // G = (F + 0x733ffff9) * 198 - (((F + 0x733ffff9) * 396 + 212) & 212) + 85;
        G = (F + 0x733ffff9u) * 198u - (((F + 0x733ffff9u) * 396u + 212u) & 212u) + 85u;

        // buffer3[80] = buffer3[36] + (G ^ 148) + ((G ^ 107) << 1) - 127;
        b3[80] = (byte)(b3[36] + (G ^ 148u) + ((G ^ 107u) << 1) - 127u);

        // buffer3[84] = ((buffer2[buffer3[64] % 35]) & 245) | (buffer2[buffer3[20] % 35] & 10);
        b3[84] = (byte)(((b2[b3[64] % 35]) & 245) | (b2[b3[20] % 35] & 10));

        // A = buffer0[buffer3[68] % 20] | 81;
        A = (uint)(b0[b3[68] % 20] | 81);

        // buffer2[18] -= ((A*A*A) & ~buffer0[15]) | ((buffer3[80] / 15) & buffer0[15]);
        b2[18] = (byte)(b2[18] - (uint)(((A * A * A) & (uint)(~b0[15])) | ((b3[80] / 15) & b0[15])));

        // buffer3[88] = buffer3[8] + J + E - buffer0[buffer1[160] % 20] + (buffer4[buffer0[((buffer3[8] + J + E) & 255) % 20] % 21] / 3);
        b3[88] = (byte)(b3[8] + J + E - b0[b1[160] % 20] + (b4[(int)(b0[(int)((b3[8] + J + E) & 255u) % 20] % 21)] / 3));

        // B = ((R ^ buffer3[72]) & ~198) | ((S * S) & 198);
        B = (uint)(((R ^ b3[72]) & ~198) | ((S * S) & 198));

        // F = (buffer4[buffer1[69] % 21] & buffer1[172]) | ((buffer4[buffer1[69] % 21] | buffer1[172]) & ((buffer3[12] - B) + 77));
        F = (uint)((b4[b1[69] % 21] & b1[172]) | ((b4[b1[69] % 21] | b1[172]) & ((b3[12] - B) + 77u)));

        // buffer0[16] = 147 - ((buffer3[72] & ((F & 251) | 1)) | (((F & 250) | buffer3[72]) & 198));
        b0[16] = (byte)(147 - ((b3[72] & ((F & 251u) | 1u)) | (((F & 250u) | b3[72]) & 198u)));

        // C = (buffer4[buffer1[168] % 21] & buffer0[buffer1[29] % 20] & 7) | ((buffer4[buffer1[168] % 21] | buffer0[buffer1[29] % 20]) & 6);
        C = (uint)((b4[b1[168] % 21] & b0[b1[29] % 20] & 7) | ((b4[b1[168] % 21] | b0[b1[29] % 20]) & 6));

        // F = (buffer4[buffer1[155] % 21] & buffer1[105]) | ((buffer4[buffer1[155] % 21] | buffer1[105]) & 141);
        F = (uint)((b4[b1[155] % 21] & b1[105]) | ((b4[b1[155] % 21] | b1[105]) & 141));

        // buffer0[3] -= buffer4[weird_rol32(F, C) % 21];
        b0[3] = (byte)(b0[3] - b4[(int)(WeirdRol32((byte)F, (int)C) % 21u)]);

        // buffer1[5] = weird_ror8(buffer0[12], ((buffer0[buffer1[61] % 20] / 5) & 7)) ^ (((~buffer2[buffer3[84] % 35]) & 0xffffffff) / 5);
        b1[5] = (byte)(WeirdRor8(b0[12], (b0[b1[61] % 20] / 5) & 7) ^ (((uint)(~b2[b3[84] % 35])) / 5u));

        // buffer1[198] += buffer1[3];
        b1[198] = (byte)(b1[198] + b1[3]);

        // A = (162 | buffer2[buffer3[64] % 35]);
        A = (uint)(162 | b2[b3[64] % 35]);

        // buffer1[164] += ((A*A)/5);
        b1[164] = (byte)(b1[164] + ((A * A) / 5));

        // G = weird_ror8(139, (buffer3[80] & 7));
        G = WeirdRor8(139, b3[80] & 7);

        // C = ((buffer4[buffer3[64] % 21] * buffer4[...] * buffer4[...]) & 95) | (buffer0[buffer3[40] % 20] & ~95);
        C = (uint)(((b4[b3[64] % 21] * b4[b3[64] % 21] * b4[b3[64] % 21]) & 95) | (b0[b3[40] % 20] & ~95));

        // buffer3[92] = (G & 12) | (buffer0[buffer3[20] % 20] & 12) | (G & buffer0[buffer3[20] % 20]) | C;
        b3[92] = (byte)((G & 12u) | ((uint)b0[b3[20] % 20] & 12u) | (G & b0[b3[20] % 20]) | C);

        // buffer2[12] += ((buffer1[103] & 32) | (buffer3[92] & ((buffer1[103] | 60))) | 16)/3;
        b2[12] = (byte)(b2[12] + (((b1[103] & 32) | (b3[92] & ((b1[103] | 60))) | 16) / 3));

        // buffer3[96] = buffer1[143];
        b3[96] = b1[143];

        // buffer3[100] = 27;
        b3[100] = 27;

        // buffer3[104] = (((buffer3[40] & ~buffer2[8]) | (buffer1[35] & buffer2[8])) & buffer3[64]) ^ 119;
        b3[104] = (byte)((((b3[40] & ~b2[8]) | (b1[35] & b2[8])) & b3[64]) ^ 119);

        // buffer3[108] = 238 & ((((buffer3[40] & ~buffer2[8]) | (buffer1[35] & buffer2[8])) & buffer3[64]) << 1);
        b3[108] = (byte)(238 & ((((b3[40] & ~b2[8]) | (b1[35] & b2[8])) & b3[64]) << 1));

        // buffer3[112] = (~buffer3[64] & (buffer3[84] / 3)) ^ 49;
        b3[112] = (byte)((~b3[64] & (b3[84] / 3)) ^ 49);

        // buffer3[116] = 98 & ((~buffer3[64] & (buffer3[84] / 3)) << 1);
        b3[116] = (byte)(98 & ((~b3[64] & (b3[84] / 3)) << 1));

        // A = (buffer1[35] & buffer2[8]) | (buffer3[40] & ~buffer2[8]);
        A = (uint)((b1[35] & b2[8]) | (b3[40] & ~b2[8]));

        // B = (A & buffer3[64]) | (((buffer3[84] / 3) & ~buffer3[64]));
        B = (uint)((A & b3[64]) | (((b3[84] / 3) & ~b3[64])));

        // buffer1[143] = buffer3[96] - ((B & (86 + ((buffer1[172] & 64) >> 1))) | (((((buffer1[172] & 65) >> 1) ^ 86) | ((~buffer3[64] & (buffer3[84] / 3)) | (((buffer3[40] & ~buffer2[8]) | (buffer1[35] & buffer2[8])) & buffer3[64]))) & buffer3[100]));
        b1[143] = (byte)(b3[96] - (uint)((B & (86 + ((b1[172] & 64) >> 1))) | (((((b1[172] & 65) >> 1) ^ 86) | ((~b3[64] & (b3[84] / 3)) | (((b3[40] & ~b2[8]) | (b1[35] & b2[8])) & b3[64]))) & b3[100])));

        // buffer2[29] = 162;
        b2[29] = 162;

        // A = ((((buffer4[buffer3[88] % 21]) & 160) | (buffer0[buffer1[125] % 20] & 95)) >> 1);
        A = (uint)((((b4[b3[88] % 21]) & 160) | (b0[b1[125] % 20] & 95)) >> 1);

        // B = buffer2[buffer1[149] % 35] ^ (buffer1[43] * buffer1[43]);
        B = (uint)(b2[b1[149] % 35] ^ (b1[43] * b1[43]));

        // buffer0[15] += (B&A) | ((A|B) & 115);
        b0[15] = (byte)(b0[15] + ((B & A) | ((A | B) & 115u)));

        // buffer3[120] = buffer3[64] - buffer0[buffer3[40] % 20];
        b3[120] = (byte)(b3[64] - b0[b3[40] % 20]);

        // buffer1[95] = buffer4[buffer3[20] % 21];
        b1[95] = b4[b3[20] % 21];

        // A = weird_ror8(buffer2[buffer3[80] % 35], (buffer2[buffer1[17] % 35] * buffer2[buffer1[17] % 35] * buffer2[buffer1[17] % 35]) & 7);
        A = WeirdRor8(b2[b3[80] % 35], (b2[b1[17] % 35] * b2[b1[17] % 35] * b2[b1[17] % 35]) & 7);

        // buffer0[7] -= (A*A);
        b0[7] = (byte)(b0[7] - (A * A));

        // buffer2[8] = buffer2[8] - buffer1[184] + (buffer4[...] * buffer4[...] * buffer4[...]);
        b2[8] = (byte)(b2[8] - b1[184] + (b4[b1[202] % 21] * b4[b1[202] % 21] * b4[b1[202] % 21]));

        // buffer0[16] = (buffer2[buffer1[102] % 35] << 1) & 132;
        b0[16] = (byte)((b2[b1[102] % 35] << 1) & 132);

        // buffer3[124] = (buffer4[buffer3[40] % 21] >> 1) ^ buffer3[68];
        b3[124] = (byte)((b4[b3[40] % 21] >> 1) ^ b3[68]);

        // buffer0[7] -= (buffer0[buffer1[191] % 20] - (((buffer4[buffer1[80] % 21] << 1) & ~177) | (buffer4[buffer4[buffer3[88] % 21] % 21] & 177)));
        b0[7] = (byte)(b0[7] - (b0[b1[191] % 20] - (((b4[b1[80] % 21] << 1) & ~177) | (b4[b4[b3[88] % 21] % 21] & 177))));

        // buffer0[6] = buffer0[buffer1[119] % 20];
        b0[6] = b0[b1[119] % 20];

        // A = (buffer4[buffer1[190] % 21] & ~209) | (buffer1[118] & 209);
        A = (uint)((b4[b1[190] % 21] & ~209) | (b1[118] & 209));

        // B = buffer0[buffer3[120] % 20] * buffer0[buffer3[120] % 20];
        B = (uint)(b0[b3[120] % 20] * b0[b3[120] % 20]);

        // buffer0[12] = (buffer0[buffer3[84] % 20] ^ (buffer2[buffer1[71] % 35] + buffer2[buffer1[15] % 35])) & ((A & B) | ((A | B) & 27));
        b0[12] = (byte)((b0[b3[84] % 20] ^ (b2[b1[71] % 35] + b2[b1[15] % 35])) & ((A & B) | ((A | B) & 27u)));

        // B = (buffer1[32] & buffer2[buffer3[88] % 35]) | ((buffer1[32] | buffer2[buffer3[88] % 35]) & 23);
        B = (uint)((b1[32] & b2[b3[88] % 35]) | ((b1[32] | b2[b3[88] % 35]) & 23));

        // D = (((buffer4[buffer1[57] % 21] * 231) & 169) | (B & 86));
        D = (uint)(((b4[b1[57] % 21] * 231) & 169) | (B & 86));

        // F = (((buffer0[buffer1[82] % 20] & ~29) | (buffer4[buffer3[124] % 21] & 29)) & 190) | (buffer4[(D/5) % 21] & ~190);
        F = (uint)((((b0[b1[82] % 20] & ~29) | (b4[b3[124] % 21] & 29)) & 190) | (b4[(int)((D / 5) % 21u)] & ~190));

        // H = buffer0[buffer3[40] % 20] * buffer0[buffer3[40] % 20] * buffer0[buffer3[40] % 20];
        H = (uint)(b0[b3[40] % 20] * b0[b3[40] % 20] * b0[b3[40] % 20]);

        // K = (H & buffer1[82]) | (H & 92) | (buffer1[82] & 92);
        K = (H & b1[82]) | (H & 92u) | ((uint)b1[82] & 92u);

        // buffer3[128] = ((F & K) | ((F | K) & 192)) ^ (D/5);
        b3[128] = (byte)(((F & K) | ((F | K) & 192u)) ^ (D / 5));

        // buffer2[25] ^= ((buffer0[buffer3[120] % 20] << 1) * buffer1[5]) - (weird_rol8(buffer3[76], (buffer4[buffer3[124] % 21] & 7)) & (buffer3[20] + 110));
        b2[25] = (byte)(b2[25] ^ (uint)(((b0[b3[120] % 20] << 1) * b1[5]) - (WeirdRol8(b3[76], b4[b3[124] % 21] & 7) & (uint)(b3[20] + 110))));
    }
}
