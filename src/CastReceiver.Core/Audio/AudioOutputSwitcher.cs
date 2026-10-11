using CastReceiver.Core.Infrastructure;

namespace CastReceiver.Core.Audio;

/// <summary>
/// 投屏期间自动把音频输出切到指定设备，并在投屏结束时（可选）还原。
///
/// 设计上的两个要点：
/// <list type="number">
/// <item><b>只在"原本不是目标设备"时才记录并切换。</b>否则还原时会把用户自己选的设备改回去，
/// 或者把"本来就是目标设备"记成"上一个设备"，还原逻辑就错了。</item>
/// <item><b>还原是尽力而为。</b>设备可能已经被拔掉/停用，这时切换会失败；
/// 失败也要把状态清干净，不能卡在"以为自己还占着"的状态里。</item>
/// </list>
/// </summary>
public sealed class AudioOutputSwitcher
{
    private readonly AppLog _log;
    private readonly object _gate = new();

    private string? _previousId;

    public AudioOutputSwitcher(AppLog log) => _log = log;

    /// <summary>当前是否处于"我们改过默认设备"的状态。</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _previousId is not null;
            }
        }
    }

    /// <summary>当前切到的目标设备 id（没有则为 null）。</summary>
    public string? ActiveTargetId { get; private set; }

    /// <summary>切换前的设备 id（用于还原与界面提示）。</summary>
    public string? PreviousId
    {
        get
        {
            lock (_gate)
            {
                return _previousId;
            }
        }
    }

    /// <summary>
    /// 开始投屏时调用。返回是否真的发生了切换。
    /// <paramref name="restoreOnStop"/> = false 表示只切过去、结束时不还原（用户可能就想让它留着）。
    /// </summary>
    public bool Apply(string? targetId, bool restoreOnStop)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return false;
        }

        lock (_gate)
        {
            if (_previousId is not null)
            {
                // 已经切过一次了：不重复记录，否则还原目标会被覆盖成"上一次切完的设备"
                return false;
            }

            string? current = AudioEndpointService.GetDefaultRenderId();
            if (string.Equals(current, targetId, StringComparison.OrdinalIgnoreCase))
            {
                _log.Info("audio", "默认音频设备已经是投屏目标设备，无需切换");
                ActiveTargetId = targetId;
                return false;
            }

            if (!AudioEndpointService.TrySetDefaultRender(targetId))
            {
                _log.Warn("audio", "切换默认音频设备失败：" + (AudioEndpointService.LastError ?? "原因未知"));
                return false;
            }

            ActiveTargetId = targetId;
            if (restoreOnStop)
            {
                _previousId = current;
            }

            _log.Info("audio", $"投屏开始：默认音频设备已切换（{Describe(current)} → {Describe(targetId)}）" +
                               (restoreOnStop ? "，结束时还原" : "，结束时保留"));
            return true;
        }
    }

    /// <summary>投屏结束时调用：还原到切换前的设备。返回是否成功还原。</summary>
    public bool Restore()
    {
        lock (_gate)
        {
            string? previous = _previousId;
            _previousId = null;
            ActiveTargetId = null;

            if (previous is null)
            {
                return false;
            }

            if (AudioEndpointService.TrySetDefaultRender(previous))
            {
                _log.Info("audio", $"投屏结束：默认音频设备已还原到 {Describe(previous)}");
                return true;
            }

            // 设备可能已被拔掉/停用：如实记录，并把状态清掉，不留在"以为占着"的状态
            _log.Warn("audio", $"投屏结束：还原默认音频设备失败（设备可能已移除）：" +
                               (AudioEndpointService.LastError ?? "原因未知"));
            return false;
        }
    }

    private static string Describe(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "(未知)";
        }

        // 设备 id 很长，日志里只保留端点 GUID 那段，够定位就行
        const string prefix = @"\\?\SWD#MMDEVAPI#";
        string text = id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? id[prefix.Length..] : id;
        int hash = text.IndexOf('#');
        return hash > 0 ? text[..hash] : text;
    }
}
