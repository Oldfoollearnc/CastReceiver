namespace CastReceiver.Core.Infrastructure;

/// <summary>
/// 启动入场动画的素材解析规则。
///
/// 沿革：早期一度"不内置任何画面"（素材完全由用户添加）；作者后来要求把
/// 他自己配的那段开场做成**内置素材**随安装包发布，于是恢复了内置清单。
/// 导入/内置的文件都**直接播放、不做任何转码**，所以"裁切"只裁时长、不掉画质。
/// </summary>
public static class IntroClips
{
    /// <summary>一个片段：key 存进设置，Title 给界面看，FileName 是随程序发布的那份。</summary>
    public sealed record Clip(string Key, string Title, string FileName);

    /// <summary>
    /// 内置片段清单：发布时由 build.ps1 把 <c>assets/intro/&lt;FileName&gt;</c> 复制到
    /// 程序目录的 <c>Assets/intro/</c> 下（Inno 的 [Files] 会连整个目录一起打进安装包）。
    /// 文件名用纯 ASCII，避免安装器/压缩环节的编码麻烦。
    /// </summary>
    public static IReadOnlyList<Clip> BuiltIn { get; } = new[]
    {
        new Clip("builtin", "内置开场", "builtin-intro.mp4"),
    };

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
        if (hit is null)
        {
            return "未选择素材";
        }

        // 安装包默认**不附带**开场视频（191MB，打包很慢）：这时候要如实说"没有"，
        // 不能让界面显示"内置开场"却什么都放不出来。
        string path = Path.Combine(baseDirectory, "Assets", "intro", hit.FileName);
        return File.Exists(path) ? hit.Title : $"{hit.Title}（当前安装包未附带）";
    }
}
