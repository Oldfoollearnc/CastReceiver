namespace CastReceiver.Core.AirPlay.FairPlay;

/// <summary>
/// UxPlay <c>lib/fairplay_playfair.c</c> 的移植：FairPlay 3 的握手与会话状态。
///
/// 每个连接持有一个实例（对应 C 里的 <c>fairplay_t</c>），因为 <c>fp-setup</c> 的
/// 164 字节挑战需要在后续 <c>SETUP</c> 解 <c>ekey</c> 时复用。
///
/// 三步的输入输出尺寸（UxPlay 的判据就是请求体长度）：
/// <list type="bullet">
/// <item>16 字节挑战 → 142 字节应答（<see cref="Setup"/>）</item>
/// <item>164 字节握手 → 32 字节应答（<see cref="Handshake"/>）</item>
/// <item>72 字节 <c>ekey</c> → 16 字节 AES 密钥（<see cref="Decrypt"/>）</item>
/// </list>
/// </summary>
internal sealed class FairPlaySession
{
    /// <summary>fp-setup 的 142 字节应答表（4 种 mode）。</summary>
    private const int ReplySize = 142;

    private readonly byte[] _keyMsg = new byte[164];
    private int _keyMsgLen;

    /// <summary>
    /// C <c>fairplay_setup</c>：按 <c>req[14]</c> 选一条固定应答。
    /// 原版不校验 mode 范围（越界即 UB），这里显式拒绝非法 mode。
    /// </summary>
    public bool Setup(byte[] request, byte[] response)
    {
        if (request[4] != 0x03)
        {
            return false;   // 不支持的 FairPlay 版本
        }

        int mode = request[14];
        if (mode is < 0 or > 3)
        {
            return false;
        }

        Array.Copy(FairPlayConstants.ReplyMessage, mode * ReplySize, response, 0, ReplySize);
        _keyMsgLen = 0;
        return true;
    }

    /// <summary>C <c>fairplay_handshake</c>：记录 164 字节 keymsg，应答 = 12 字节头 + 请求尾 20 字节。</summary>
    public bool Handshake(byte[] request, byte[] response)
    {
        if (request[4] != 0x03)
        {
            return false;
        }

        Array.Copy(request, _keyMsg, 164);
        _keyMsgLen = 164;

        Array.Copy(FairPlayConstants.FpHeader, 0, response, 0, 12);
        Array.Copy(request, 144, response, 12, 20);
        return true;
    }

    /// <summary>C <c>fairplay_decrypt</c>：把 72 字节 <c>ekey</c> 解成 16 字节 AES 密钥。</summary>
    public bool Decrypt(byte[] input, byte[] output)
    {
        if (_keyMsgLen != 164)
        {
            return false;
        }

        OmgHax.PlayFairDecrypt(_keyMsg, input, output);
        return true;
    }

    /// <summary>是否已经完成 164 字节握手（<c>fairplay_decrypt</c> 的前提）。</summary>
    public bool IsHandshakeComplete => _keyMsgLen == 164;
}
