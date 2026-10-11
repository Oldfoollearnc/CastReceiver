using CastReceiver.Core.Audio;

namespace CastReceiver.SelfTest;

/// <summary>
/// 音频输出设备诊断（开发工具）。
///
/// 这里最要紧的一条纪律：<b>不能用"没抛异常"来证明切换成功</b>。
/// IPolicyConfig 是未公开接口，vtable 对不上时也可能返回成功码。
/// 所以每一步都用 WinRT 公开 API 读回当前默认设备来校验。
///
/// 默认只做空操作切换（把默认设备"切换"成它自己）—— 足以验证 COM 通路，
/// 而且不会打扰正在播放的声音。真正换设备需要显式加 --switch。
/// </summary>
internal static class AudioEndpointDiagnostic
{
    /// <summary>把默认播放设备设成指定 id（救急/手工还原用）。</summary>
    public static int SetDefault(string deviceId)
    {
        string? before = AudioEndpointService.GetDefaultRenderId();
        bool ok = AudioEndpointService.TrySetDefaultRender(deviceId);
        string? after = AudioEndpointService.GetDefaultRenderId();
        bool effective = ok && string.Equals(after, deviceId, StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"设置默认播放设备：调用成功={ok} 读回一致={effective}");
        Console.WriteLine($"  之前 = {before}");
        Console.WriteLine($"  之后 = {after}");
        if (!ok)
        {
            Console.WriteLine($"  失败原因：{AudioEndpointService.LastError ?? "(未提供)"}");
        }
        return effective ? 0 : 1;
    }

    public static int Run(bool realSwitch)
    {
        IReadOnlyList<AudioEndpoint> endpoints = AudioEndpointService.ListRenderEndpointsAsync().GetAwaiter().GetResult();
        string? before = AudioEndpointService.GetDefaultRenderId();

        Console.WriteLine($"播放设备 {endpoints.Count} 个；当前默认 = {before ?? "(读不到)"}");
        foreach (AudioEndpoint endpoint in endpoints)
        {
            Console.WriteLine($"  [{(endpoint.IsDefault ? "*" : " ")}] {endpoint.Name}  enabled={endpoint.IsEnabled}");
            Console.WriteLine($"        {endpoint.Id}");
        }
        Console.WriteLine();

        int fail = 0;

        // ---- 1) 空操作切换：证明 COM 通路与 vtable 正确
        bool attempt = before is not null && AudioEndpointService.TrySetDefaultRender(before);
        string? afterNoop = AudioEndpointService.GetDefaultRenderId();
        bool unchanged = string.Equals(afterNoop, before, StringComparison.OrdinalIgnoreCase);
        bool noopOk = attempt && unchanged;
        Console.WriteLine($"[{(noopOk ? "PASS" : "FAIL")}] 空操作切换（设成当前设备）:: 调用成功={attempt} 读回未变={unchanged}");
        if (!noopOk)
        {
            Console.WriteLine($"        失败原因：{AudioEndpointService.LastError ?? "(未提供)"}");
            fail++;
        }

        // ---- 2) 真切换 + 还原：只有显式要求时才做，且一定切回去
        AudioEndpoint? other = endpoints.FirstOrDefault(e => !e.IsDefault && e.IsEnabled);
        if (!realSwitch)
        {
            Console.WriteLine("[SKIP] 真切换未启用（加 --switch 才会真的换设备）");
        }
        else if (other is null)
        {
            Console.WriteLine("[SKIP] 没有第二个可用播放设备，无法验证真切换");
        }
        else
        {
            bool set1 = AudioEndpointService.TrySetDefaultRender(other.Id);
            string? read1 = AudioEndpointService.GetDefaultRenderId();
            bool switched = set1 && string.Equals(read1, other.Id, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"[{(switched ? "PASS" : "FAIL")}] 切到「{other.Name}」:: 调用成功={set1} 读回={read1}");
            if (!switched)
            {
                fail++;
            }

            bool restore = before is not null && AudioEndpointService.TrySetDefaultRender(before);
            string? read2 = AudioEndpointService.GetDefaultRenderId();
            bool restored = restore && string.Equals(read2, before, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"[{(restored ? "PASS" : "FAIL")}] 切回原设备:: 调用成功={restore} 读回={read2}");
            if (!restored)
            {
                fail++;
            }
        }

        Console.WriteLine($"\n音频设备诊断：失败 {fail} 项");
        return fail;
    }
}
