using System.Security.Cryptography;

namespace CastReceiver.Core.AirPlay.Mirror;

/// <summary>
/// AES-128-CTR，语义与 OpenSSL 的 <c>EVP_aes_128_ctr</c>（参考实现所用）一致：
/// 把 IV 当作 128 位**大端**计数器，每产生一个 16 字节密钥流块就 +1。
///
/// 关键点：这是一个**连续的密钥流**。镜像流里所有 <c>0x00</c> 类型的载荷按到达顺序
/// 共用一条密钥流，不按包重置、也不按包推导 IV；非 0x00 的包（SPS/PPS、心跳、报表）
/// 是明文的，**不推进**密钥流。因此这里的状态必须跨多次 <see cref="Transform"/> 调用保留。
/// </summary>
internal sealed class Aes128Ctr : IDisposable
{
    /// <summary>一次生成多少字节密钥流（必须是 16 的倍数）。</summary>
    private const int KeystreamChunk = 4096;

    private readonly Aes _aes;
    private readonly ICryptoTransform _encryptor;
    private readonly byte[] _counterBlock = new byte[16];
    private readonly byte[] _keystream = new byte[KeystreamChunk];
    private readonly byte[] _counterBlocks = new byte[KeystreamChunk];
    private int _keystreamPos = KeystreamChunk;   // == 长度 表示"需要重新生成密钥流"

    public Aes128Ctr(byte[] key, byte[] iv)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);
        if (key.Length != 16)
        {
            throw new ArgumentException("AES-128 需要 16 字节密钥", nameof(key));
        }
        if (iv.Length != 16)
        {
            throw new ArgumentException("CTR 需要 16 字节计数器初值", nameof(iv));
        }

        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = key;
        _encryptor = _aes.CreateEncryptor();

        Array.Copy(iv, _counterBlock, 16);
    }

    /// <summary>就地加/解密（CTR 对称）。可多次调用，密钥流连续推进。</summary>
    public void Transform(Span<byte> data)
    {
        int index = 0;
        while (index < data.Length)
        {
            if (_keystreamPos == _keystream.Length)
            {
                RefillKeystream();
            }

            int take = Math.Min(_keystream.Length - _keystreamPos, data.Length - index);
            for (int k = 0; k < take; k++)
            {
                data[index + k] ^= _keystream[_keystreamPos + k];
            }

            _keystreamPos += take;
            index += take;
        }

        BytesProcessed += data.Length;
    }

    /// <summary>当前密钥流已消耗的字节数（自检用：验证跨包连续）。</summary>
    public long BytesProcessed { get; private set; }

    private void RefillKeystream()
    {
        // 一次算一大块。早先是"每 16 字节调一次 TransformBlock"，1080p 下每秒约 19 万次
        // 调用，光是调用开销就吃掉可观的一核时间（镜像卡顿的一个来源）。
        // 这里先把 256 个**连续**计数器块摆好，再用一次 ECB 加密整块得到密钥流 ——
        // 语义与 OpenSSL 的 EVP_aes_128_ctr 完全一致：每生成一个 16 字节块，计数器 +1。
        for (int offset = 0; offset < _counterBlocks.Length; offset += 16)
        {
            Array.Copy(_counterBlock, 0, _counterBlocks, offset, 16);
            IncrementCounter();
        }

        _encryptor.TransformBlock(_counterBlocks, 0, _counterBlocks.Length, _keystream, 0);
        _keystreamPos = 0;
    }

    /// <summary>128 位大端计数器 +1（与 OpenSSL 的 CTR 约定一致）。</summary>
    private void IncrementCounter()
    {
        for (int i = 15; i >= 0; i--)
        {
            if (++_counterBlock[i] != 0)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _encryptor.Dispose();
        _aes.Dispose();
    }
}
