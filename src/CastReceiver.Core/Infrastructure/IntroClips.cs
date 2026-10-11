namespace CastReceiver.Core.Infrastructure;

/// <summary>
/// 启动入场动画的素材解析规则。
///
/// 用户要求（本轮起）：**程序不再内置任何画面**，素材完全由用户自己添加；
/// 导入的原文件**直接播放、不做任何转码**，所以"裁切"只裁时长、不掉画质。
/// </summary>
public static class IntroClips
{
    /// <summary>一个片段：key 存进设置，Title 给界面看，FileName 是随程序发布的那份。</summary>
    public sealed record Clip(string Key, string Title, string FileName);

    /// <summary>内置片段清单：现在是空的（用户不要内置画面）。以后要加就往这里塞。</summary>
    public static IReadOnlyList<Clip> BuiltIn { get; } = Array.Empty<Clip>();

    /// <summary>设置里"自定义"用的前缀：自定义项存成 <c>file:完整路径</c>。</summary>
    public const string FilePrefix = "file:";

    /// <summary>
    /// 把设置里的值解析成实际要播的文件路径。
    /// <c>file:xxx</c> → 直接用那个路径（不拷贝、不转码）；没有可用素材就返回空串，
    /// 调用方据此跳过入场动画（而不是去播一个不存在的东西）。
    /// </summary>
    public static string ResolvePath(string? setting, string baseDirectory)
    {
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
                    string path = Path.Combine(baseDirectory, "Assets", "intro", hit.FileName);
                    if (File.Exists(path)) return path;
                }
            }
        }

        return "";
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
        return hit?.Title ?? "未选择素材";
    }
}
