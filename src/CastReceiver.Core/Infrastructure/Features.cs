namespace CastReceiver.Core.Infrastructure;

/// <summary>
/// 功能总开关（**编译期常量**：改一行即可把整个功能从产品里去掉）。
///
/// 与用户偏好的分工：
/// <list type="bullet">
/// <item>**产品决定**（这个功能要不要存在于包里）→ 放这里；</item>
/// <item>**用户偏好**（这个人要不要用它）→ 放 <see cref="CastSettings"/>。</item>
/// </list>
/// 两者是"与"的关系：只有产品里保留了、且用户在设置里打开了，功能才真正生效。
/// </summary>
public static class Features
{
    /// <summary>
    /// 「电脑控制手机」（安卓 · adb 无线）：在投屏画面上直接操作手机。
    ///
    /// 现在是**两级开关**：
    /// <list type="number">
    /// <item>这个常量 = 产品里是否保留该功能（当前保留）；</item>
    /// <item><see cref="CastSettings.AdbControlEnabled"/> = 用户在
    /// 「设置 → 安全 → 电脑控制手机」里的开关，**默认关闭**。</item>
    /// </list>
    ///
    /// 按用户 2026-10-16 的要求，功能**默认下线**（设置里关着就等于不存在）：
    /// 界面整块不出现（侧栏卡片 / 底部控制条 / 配对窗口入口 / 自动连接子项），
    /// 不启动 adb、不做 mDNS 发现、不轮询、不自动连接，鼠标注入 / 系统键 / 文字 /
    /// 手机截屏 / 键盘直通与 Ctrl+Alt+K 一并关闭。
    ///
    /// 用户想用就在设置里打开，**不需要重新编译**；要彻底从产品里移除，
    /// 把这个常量改成 <c>false</c> 即可（两道闸都关）。
    /// </summary>
    public const bool AndroidRemoteControl = true;
}
