using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace CastReceiver.Core.Audio;

/// <summary>一个音频输出（播放）设备。</summary>
public sealed record AudioEndpoint(string Id, string Name, bool IsDefault, bool IsEnabled)
{
    public string Display => IsDefault ? Name + "（当前默认）" : Name;

    /// <summary>
    /// 下拉框显示用。故意重写 ToString：WPF 的 ComboBoxItem 自动化名称取的是项的 ToString()，
    /// 只设 DisplayMemberPath 会导致"界面显示的名字"和"自动化读到的名字"不一致 ——
    /// 那样我就没法用自动化验证界面究竟显示了什么（只能靠猜）。重写之后两者一致。
    /// </summary>
    public override string ToString() => Display;
}

/// <summary>
/// 音频输出设备的枚举与切换。
///
/// 为什么需要两套 API：
/// <list type="bullet">
/// <item><b>读</b>用 WinRT：<c>MediaDevice.GetDefaultAudioRenderId()</c> 是公开 API，读当前默认设备没问题；</item>
/// <item><b>写</b>没有公开 API —— WinRT 只提供 getter，所以切换默认设备只能用
/// Windows 未公开的 <c>IPolicyConfig</c> COM 接口（各类音频切换工具走的都是这条路）。</item>
/// </list>
///
/// 未公开接口的风险是 vtable 顺序必须完全对上，所以这里的自检**不靠"没抛异常"下结论**：
/// 一律用 WinRT 读回当前默认设备来验证切换是否真的生效。
/// </summary>
public static class AudioEndpointService
{
    private const string PolicyConfigClientClsid = "870AF99C-171D-4F9E-AF0D-E63DF40C2BC9";
    private const string PolicyConfigIid = "F8679F50-850A-41CF-9C72-430F290290C8";

    /// <summary>ERole：与 Win32 的 EDataFlow 角色一一对应。</summary>
    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2,
    }

    [ComImport, Guid(PolicyConfigClientClsid)]
    private class PolicyConfigClient
    {
    }

    /// <summary>
    /// IPolicyConfig：方法顺序即 vtable 顺序，**不能删改**，
    /// 因为前面的方法只是为了让 SetDefaultEndpoint 落到正确的槽位。
    /// </summary>
    [ComImport, Guid(PolicyConfigIid), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    /// <summary>当前默认播放设备的 id（WinRT 公开 API，可放心用于校验）。</summary>
    public static string? GetDefaultRenderId()
    {
        try
        {
            string id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>枚举所有播放设备（含已停用的，便于解释"为什么设备列表里少了一个"）。</summary>
    public static async Task<IReadOnlyList<AudioEndpoint>> ListRenderEndpointsAsync()
    {
        var result = new List<AudioEndpoint>();
        string? current = GetDefaultRenderId();

        try
        {
            string selector = MediaDevice.GetAudioRenderSelector();
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
            foreach (DeviceInformation device in devices)
            {
                result.Add(new AudioEndpoint(
                    device.Id,
                    string.IsNullOrWhiteSpace(device.Name) ? "(未命名设备)" : device.Name,
                    string.Equals(device.Id, current, StringComparison.OrdinalIgnoreCase),
                    device.IsEnabled));
            }
        }
        catch
        {
            // 枚举失败就返回空表：界面显示"没有可用设备"比抛异常好
        }

        return result;
    }

    /// <summary>
    /// 把默认播放设备切到指定 id。三个角色都设，否则"默认设备"和"多媒体设备"会不一致
    /// （换完之后有些程序仍然往旧设备出声）。
    /// </summary>
    public static bool TrySetDefaultRender(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            LastError = "设备 id 为空";
            return false;
        }

        LastError = null;
        object? client = null;
        try
        {
            client = new PolicyConfigClient();
            var policy = (IPolicyConfig)client;

            foreach ((string candidate, string form) in Candidates(deviceId))
            {
                int console = policy.SetDefaultEndpoint(candidate, ERole.eConsole);
                int multimedia = policy.SetDefaultEndpoint(candidate, ERole.eMultimedia);
                // 通信设备失败不算致命（有些系统策略会拒绝），只要求前两个成功
                int communications = policy.SetDefaultEndpoint(candidate, ERole.eCommunications);

                if (console >= 0 && multimedia >= 0)
                {
                    UsedForm = form;
                    return true;
                }

                LastError = $"SetDefaultEndpoint（{form}）返回 0x{console:X8} / 0x{multimedia:X8} / 0x{communications:X8}";
            }

            return false;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})";
            return false;
        }
        finally
        {
            try { if (client is not null) Marshal.ReleaseComObject(client); } catch { }
        }
    }

    /// <summary>上一次切换失败的原因（成功时为 null）。用来解释"为什么没切成"。</summary>
    public static string? LastError { get; private set; }

    /// <summary>上一次成功切换用的是哪种 id 形式（诊断用）。</summary>
    public static string? UsedForm { get; private set; }

    /// <summary>
    /// WinRT 给的是**设备接口路径**（<c>\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{接口 GUID}</c>），
    /// 而 IPolicyConfig 要的是 **IMMDevice 端点 id**（<c>{0.0.0.00000000}.{guid}</c>）。
    /// 实测直接传接口路径会得到 E_INVALIDARG（0x80070057）。
    /// 两种形式都试，谁能成功用谁 —— 不靠猜哪种对。
    /// </summary>
    private static IEnumerable<(string Id, string Form)> Candidates(string deviceId)
    {
        yield return (deviceId, "设备接口路径");

        const string prefix = @"\\?\SWD#MMDEVAPI#";
        if (!deviceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        string rest = deviceId[prefix.Length..];
        int hash = rest.IndexOf('#');
        string endpointId = hash > 0 ? rest[..hash] : rest;
        if (endpointId.Length > 0 && !string.Equals(endpointId, deviceId, StringComparison.Ordinal))
        {
            yield return (endpointId, "端点 id");
        }
    }
}
