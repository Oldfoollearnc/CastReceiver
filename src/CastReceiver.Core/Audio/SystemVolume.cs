using System.Runtime.InteropServices;

namespace CastReceiver.Core.Audio;

/// <summary>
/// 本机（电脑）扬声器音量：主音量 + 静音，走 Core Audio 的 <c>IAudioEndpointVolume</c>。
///
/// 特意说清楚它调的是**这台电脑**的音量，不是手机音量 —— 投屏的声音是电脑在放，
/// 调手机音量对听到的声音没有任何影响（用户要的就是电脑这一侧）。
///
/// 另一条路是直接发 VK_VOLUME_UP/DOWN 按键，但那读不回当前音量，滑杆就没法显示位置，
/// 所以这里老老实实走 Core Audio：能读能写，还能和系统音量键保持一致。
/// </summary>
public static class SystemVolume
{
    private const int ERender = 0;          // EDataFlow.eRender
    private const int ERoleMultimedia = 1;  // ERole.eMultimedia（与系统音量键同一路）
    private const int ClsCtxAll = 23;       // CLSCTX_ALL

    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    /// <summary>上一次失败的原因（成功时为 null）：界面可以据此提示"这台机器的音量读不到"。</summary>
    public static string? LastError { get; private set; }

    /// <summary>当前主音量（0..1）；读不到返回 null。</summary>
    public static float? GetScalar() => Read(volume =>
        volume.GetMasterVolumeLevelScalar(out float level) >= 0 ? level : (float?)null);

    /// <summary>当前是否静音；读不到返回 null。</summary>
    public static bool? GetMute() => Read(volume =>
        volume.GetMute(out int mute) >= 0 ? mute != 0 : (bool?)null);

    /// <summary>设置主音量（0..1，越界会被夹住）。</summary>
    public static bool SetScalar(float level)
    {
        float clamped = Math.Clamp(level, 0f, 1f);
        return Write(volume => volume.SetMasterVolumeLevelScalar(clamped, IntPtr.Zero) >= 0);
    }

    /// <summary>设置静音。</summary>
    public static bool SetMute(bool mute)
        => Write(volume => volume.SetMute(mute ? 1 : 0, IntPtr.Zero) >= 0);

    /// <summary>在当前音量上加减一点（滚轮 / 上下键用）。</summary>
    public static bool Step(float delta)
    {
        float? now = GetScalar();
        if (now is null) return false;
        return SetScalar(now.Value + delta);
    }

    // ---------------------------------------------------------------- 内部

    // 设备会被切换（本程序自己就能换默认播放设备），所以缓存要能失效：
    // 先试缓存，调用返回失败就重新取一次端点，再失败才算真失败。
    [ThreadStatic] private static IAudioEndpointVolume? _cached;

    private static T? Read<T>(Func<IAudioEndpointVolume, T?> read) where T : struct
    {
        try
        {
            IAudioEndpointVolume? volume = Acquire(refresh: false);
            T? value = volume is null ? null : read(volume);
            if (value is not null) { LastError = null; return value; }

            volume = Acquire(refresh: true);
            if (volume is null) return null;
            value = read(volume);
            LastError = value is null ? "读取音量失败" : null;
            return value;
        }
        catch (Exception ex)
        {
            LastError = Describe(ex);
            return null;
        }
    }

    private static bool Write(Func<IAudioEndpointVolume, bool> write)
    {
        try
        {
            IAudioEndpointVolume? volume = Acquire(refresh: false);
            if (volume is not null && write(volume)) { LastError = null; return true; }

            volume = Acquire(refresh: true);
            if (volume is null) return false;
            bool ok = write(volume);
            LastError = ok ? null : "写入音量失败";
            return ok;
        }
        catch (Exception ex)
        {
            LastError = Describe(ex);
            return false;
        }
    }

    private static string Describe(Exception ex)
        => $"{ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})";

    /// <summary>取当前默认播放设备的音量接口。<paramref name="refresh"/> = true 时丢弃缓存重取。</summary>
    private static IAudioEndpointVolume? Acquire(bool refresh)
    {
        if (!refresh && _cached is not null) return _cached;
        _cached = null;

        object? enumeratorObject = null;
        IMMDevice? device = null;
        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            if (enumerator.GetDefaultAudioEndpoint(ERender, ERoleMultimedia, out device) < 0 || device is null)
            {
                LastError = "找不到默认播放设备";
                return null;
            }

            Guid iid = IID_IAudioEndpointVolume;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object raw) < 0 || raw is null)
            {
                LastError = "无法激活音量接口";
                return null;
            }

            _cached = (IAudioEndpointVolume)raw;
            return _cached;
        }
        finally
        {
            if (device is not null) { try { Marshal.ReleaseComObject(device); } catch { } }
            if (enumeratorObject is not null) { try { Marshal.ReleaseComObject(enumeratorObject); } catch { } }
        }
    }

    // ---------------------------------------------------------------- COM 声明
    // 注意：vtable 顺序不能改 —— [PreserveSig] 的方法顺序就是接口里方法的顺序。

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? interfacePointer);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    /// <summary>
    /// IAudioEndpointVolume：只需要读到 GetMute，所以声明到第 13 个方法为止
    /// （后面的 Step/QueryHardwareSupport 用不到，省略不影响前面的槽位）。
    /// </summary>
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, IntPtr eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int channel, float levelDb, IntPtr eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, IntPtr eventContext);
        [PreserveSig] int GetChannelVolumeLevel(int channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
        [PreserveSig] int SetMute(int mute, IntPtr eventContext);
        [PreserveSig] int GetMute(out int mute);
    }
}