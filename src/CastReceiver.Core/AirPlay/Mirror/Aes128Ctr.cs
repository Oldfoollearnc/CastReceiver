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
    private readonly Aes _aes;
    private readonly ICryptoTransform _encryptor;
    private readonly byte[] _counterBlock = new byte[16];
    private readonly byte[] _keystream = new byte[16];
    private int _keystreamPos = 16;   // 16 表示"需要重新生成密钥流块"

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
        for (int i = 0; i < data.Length; i++)
        {
            if (_keystreamPos == 16)
            {
                RefillKeystream();
            }
            data[i] ^= _keystream[_keystreamPos++];
        }
    }

    /// <summary>当前密钥流已消耗的字节数（自检用：验证跨包连续）。</summary>
    public long BytesProcessed { get; private set; }

    private void RefillKeystream()
    {
        _encryptor.TransformBlock(_counterBlock, 0, 16, _keystream, 0);
        _keystreamPos = 0;
        IncrementCounter();
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
