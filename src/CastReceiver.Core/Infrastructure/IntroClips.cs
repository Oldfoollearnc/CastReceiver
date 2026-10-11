namespace CastReceiver.Core.Infrastructure;

/// <summary>
/// 启动入场动画的**内置片段清单**与解析规则。
///
/// 用户要求：可以在设置里切换、也可以导入自己的视频；导入的原文件**直接播放、不做任何转码**
/// （所以他说的"裁切"只裁时长，不掉画质），裁切就是"从第几秒播到第几秒"。
/// </summary>
public static class IntroClips
{
    /// <summary>一个内置片段：key 存进设置，Title 给界面看，FileName 是随程序发布的那份。</summary>
    public sealed record Clip(string Key, string Title, string FileName);

    /// <summary>内置片段（随程序发布，放在 Assets\intro\ 下）。</summary>
    public static IReadOnlyList<Clip> BuiltIn { get; } = new[]
    {
        new Clip("fleet", "舰队环绕行星（内置 · 默认）", "intro-fleet.mp4"),
        new Clip("swordsman", "不良人 · 天暗星出场（内置）", "intro-swordsman.mp4"),
        new Clip("ninja", "火影 · 秽土斑（内置）", "intro-ninja.mp4"),
    };

    /// <summary>设置里"自定义"用的前缀：自定义项存成 <c>file:完整路径</c>。</summary>
    public const string FilePrefix = "file:";

    public static Clip Default => BuiltIn[0];

    /// <summary>
    /// 把设置里的值解析成实际要播的文件路径。
    /// 空值 / 认不出的 key → 默认内置；<c>file:xxx</c> → 直接用那个路径（不拷贝、不转码）。
    /// </summary>
    public static string ResolvePath(string? setting, string baseDirectory)
    {
        string introDir = Path.Combine(baseDirectory, "Assets", "intro");

        if (!string.IsNullOrWhiteSpace(setting))
        {
            if (setting.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string custom = setting[FilePrefix.Length..].Trim();
                if (custom.Length > 0 && File.Exists(custom)) return custom;
            }
            else
            {
                Clip? hit = BuiltIn.FirstOrDefault(c => string.Equals(c.Key, setting, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                {
                    string path = Path.Combine(introDir, hit.FileName);
                    if (File.Exists(path)) return path;
                }
            }
        }

        // 默认：先找新的内置目录，再退回老位置（Assets\intro.mp4），都没有就返回空
        string fallback = Path.Combine(introDir, Default.FileName);
        if (File.Exists(fallback)) return fallback;
        string legacy = Path.Combine(baseDirectory, "Assets", "intro.mp4");
        return File.Exists(legacy) ? legacy : "";
    }

    /// <summary>界面上显示的当前选择（找不到文件时如实说明）。</summary>
    public static string Describe(string? setting, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(setting) && setting.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string custom = setting[FilePrefix.Length..].Trim();
            return File.Exists(custom) ? "自定义：" + Path.GetFileName(custom) : "自定义（文件已不存在）";
        }

        Clip? hit = BuiltIn.FirstOrDefault(c => string.Equals(c.Key, setting, StringComparison.OrdinalIgnoreCase));
        return (hit ?? Default).Title;
    }
}
