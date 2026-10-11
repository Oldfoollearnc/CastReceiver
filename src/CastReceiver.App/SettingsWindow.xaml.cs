using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CastReceiver.App.Services;
using CastReceiver.Core.Audio;
using CastReceiver.Core.Diagnostics;
using CastReceiver.Core.Infrastructure;
using CastReceiver.Core.Security;

namespace CastReceiver.App;

/// <summary>
/// 设置窗口（左侧导航 + 分组行）。
/// 页面：通用 / 画面 / 网络 / 诊断 / 关于；主题在这里切换并立即生效。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly CastAppHost _host;
    private readonly Func<CancellationToken, Task<SelfTestItem>>? _renderProbe;
    private readonly ObservableCollection<SelfTestItem> _items = new();
    private bool _running;
    private bool _loaded;

    public SettingsWindow(CastAppHost host, Func<CancellationToken, Task<SelfTestItem>>? renderProbe)
    {
        InitializeComponent();
        _host = host;
        _renderProbe = renderProbe;
        LstResults.ItemsSource = _items;

        var s = host.Settings;
        TxtName.Text = s.DeviceName;
        SwAutoStart.IsChecked = s.AutoStartMiracast;
        SwConfirm.IsChecked = s.MiracastConfirmConnection;
        SwMute.IsChecked = s.MuteAudio;
        SwAirPlay.IsChecked = s.AutoStartAirPlay;
        SwAudioSwitch.IsChecked = s.AudioSwitchEnabled;
        SwAudioRestore.IsChecked = s.AudioSwitchRestore;
        _ = LoadAudioEndpointsAsync(s);   // 枚举播放设备是异步的，先让窗口显示出来
        SwStats.IsChecked = s.ShowStats;
        SwStatsAuto.IsChecked = s.StatsOnlyWhenLagging;
        SwIntro.IsChecked = s.ShowIntro;
        InitIntroVideoSection(s);
        SldZoom.Value = Math.Clamp(s.MirrorZoom * 100, 50, 200);
        UpdateFitButtons(s.MirrorFit);

        var theme = string.IsNullOrWhiteSpace(s.Theme) ? "Dark" : s.Theme;
        SegDark.IsChecked = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        SegLight.IsChecked = theme.Equals("Light", StringComparison.OrdinalIgnoreCase);
        SegSystem.IsChecked = theme.Equals("System", StringComparison.OrdinalIgnoreCase);

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        TxtVersion.Text = "版本 " + version;
        TxtVersion2.Text = version;
        TxtRuntime.Text = AppContext.BaseDirectory.Contains("Programs", StringComparison.OrdinalIgnoreCase)
            ? "已安装（自带运行环境）"
            : "绿色版 / 开发版";

        RefreshNetworkText();
        host.Log.EntryWritten += OnLogEntry;
        host.Notice += OnNotice;
        Closed += (_, _) =>
        {
            host.Log.EntryWritten -= OnLogEntry;
            host.Notice -= OnNotice;
        };

        var cfg = host.Settings;
        SwTray.IsChecked = cfg.MinimizeToTray;
        SwStartup.IsChecked = StartupRegistration.IsEnabled();
        SwNotify.IsChecked = cfg.NotifyOnConnect;
        SwHotkeys.IsChecked = cfg.GlobalHotkeys;
        SwMica.IsChecked = cfg.UseMica;
        SwAutoFullscreen.IsChecked = cfg.AutoFullscreen;
        SwHideCursor.IsChecked = cfg.HideCursorInFullscreen;
        SwPreventSleep.IsChecked = cfg.PreventSleep;
        SegComfortable.IsChecked = !cfg.Density.Equals("Compact", StringComparison.OrdinalIgnoreCase);
        SegCompact.IsChecked = cfg.Density.Equals("Compact", StringComparison.OrdinalIgnoreCase);
        UpdateQualityButtons(cfg.QualityPreset);
        BuildAccentSwatches(cfg.Accent);
        TxtUpdateUrl.Text = cfg.UpdateUrl;
        SwClearOnExit.IsChecked = cfg.ClearCapturesOnExit;
        InitVaultSection(cfg);
        InitSecuritySection(cfg);

        _loaded = true;
        ShowPage("general");
    }

    // ------------------------------------------------------------------ 加密库区块

    private void InitVaultSection(CastSettings cfg)
    {
        TxtVaultPath.Text = VaultPaths.Root;

        CmbRecordFps.Items.Clear();
        foreach (var value in new[] { "5", "10", "15", "20" }) CmbRecordFps.Items.Add(value + " fps");
        CmbRecordFps.SelectedIndex = cfg.RecordFps switch { "5" => 0, "15" => 2, "20" => 3, _ => 1 };

        CmbRecordQuality.Items.Clear();
        foreach (var value in new[] { "省空间（60）", "标准（80）", "清晰（92）" }) CmbRecordQuality.Items.Add(value);
        CmbRecordQuality.SelectedIndex = cfg.RecordJpegQuality switch { <= 65 => 0, >= 88 => 2, _ => 1 };

        CmbRecordMinutes.Items.Clear();
        foreach (var value in new[] { 5, 10, 30, 60, 120 }) CmbRecordMinutes.Items.Add(value + " 分钟");
        CmbRecordMinutes.SelectedIndex = cfg.RecordMaxMinutes switch { <= 5 => 0, <= 10 => 1, <= 30 => 2, <= 60 => 3, _ => 4 };

        CmbVaultQuota.Items.Clear();
        foreach (var value in new[] { 512, 1024, 2048, 5120, 10240 }) CmbVaultQuota.Items.Add(value >= 1024 ? $"{value / 1024} GB" : $"{value} MB");
        CmbVaultQuota.SelectedIndex = cfg.VaultQuotaMB switch { <= 512 => 0, <= 1024 => 1, <= 2048 => 2, <= 5120 => 3, _ => 4 };

        CmbVaultRetention.Items.Clear();
        foreach (var value in new[] { "不自动清理", "保留 30 天", "保留 90 天" }) CmbVaultRetention.Items.Add(value);
        CmbVaultRetention.SelectedIndex = cfg.VaultRetention switch { "Days30" => 1, "Days90" => 2, _ => 0 };

        CmbIdleLock.Items.Clear();
        foreach (var value in new[] { "不自动上锁", "1 分钟", "5 分钟", "15 分钟", "30 分钟" }) CmbIdleLock.Items.Add(value);
        CmbIdleLock.SelectedIndex = cfg.LibraryIdleLockMinutes switch { <= 0 => 0, <= 1 => 1, <= 5 => 2, <= 15 => 3, _ => 4 };

        RefreshVaultState();
        UpdateRecordEstimate();
    }

    private void RefreshVaultState()
    {
        var vault = (Application.Current.MainWindow as MainWindow)?.Vault;
        if (vault is null) { TxtVaultState.Text = ""; return; }

        string accounts = vault.Accounts.Count == 0 ? "尚未创建账号" : $"{vault.Accounts.Count} 个账号";
        string owner = vault.ActiveAccount?.DisplayName ?? "未绑定投屏设备";
        string state = vault.IsUnlocked ? "已解锁" : "已上锁";
        long bytes = vault.Accounts.Sum(a => VaultPaths.MeasureAccountBytes(a.Id));
        string size = bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024 / 1024:F2} GB" : $"{bytes / 1024.0 / 1024:F1} MB";
        TxtVaultState.Text = $"{accounts}　当前内容归属：{owner}　状态：{state}　占用：{size}" +
                             (vault.LoadError is null ? "" : $"　⚠ {vault.LoadError}");
    }

    private void RecordOption_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        var cfg = _host.Settings;
        cfg.RecordFps = new[] { "5", "10", "15", "20" }[Math.Max(0, CmbRecordFps.SelectedIndex)];
        cfg.RecordJpegQuality = new[] { 60, 80, 92 }[Math.Max(0, CmbRecordQuality.SelectedIndex)];
        cfg.RecordMaxMinutes = new[] { 5, 10, 30, 60, 120 }[Math.Max(0, CmbRecordMinutes.SelectedIndex)];
        cfg.VaultQuotaMB = new long[] { 512, 1024, 2048, 5120, 10240 }[Math.Max(0, CmbVaultQuota.SelectedIndex)];
        cfg.VaultRetention = new[] { "Off", "Days30", "Days90" }[Math.Max(0, CmbVaultRetention.SelectedIndex)];
        cfg.LibraryIdleLockMinutes = new[] { 0, 1, 5, 15, 30 }[Math.Max(0, CmbIdleLock.SelectedIndex)];
        cfg.Save();
        TxtSaved.Text = "已自动保存";
        UpdateRecordEstimate();
    }

    private void UpdateRecordEstimate()
    {
        int fps = _host.Settings.RecordFps switch { "5" => 5, "15" => 15, "20" => 20, _ => 10 };
        int quality = _host.Settings.RecordJpegQuality;
        double perFrameKb = quality switch { <= 65 => 70, >= 88 => 190, _ => 120 };   // 1280×720 经验值
        double perMinuteMb = perFrameKb * fps * 60 / 1024.0;
        TxtRecordEstimate.Text = $"估算：约 {perMinuteMb:F0} MB/分钟（720p 镜像）；" +
                                 $"上限 {_host.Settings.RecordMaxMinutes} 分钟约 {perMinuteMb * _host.Settings.RecordMaxMinutes / 1024:F1} GB。";
    }

    private void BtnOpenVault_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw) mw.OpenVault();
        RefreshVaultState();
    }

    private void BtnLockVault_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw) mw.LockVault();
        RefreshVaultState();
        TxtSaved.Text = "已上锁";
    }

    private void BtnCleanupTemp_Click(object sender, RoutedEventArgs e)
    {
        int n = LegacyMigration.CleanupTempFrameDirectories();
        TxtSaved.Text = n > 0 ? $"已清理 {n} 个旧明文临时帧目录" : "没有发现临时帧残留";
    }

    private async void BtnImportLegacy_Click(object sender, RoutedEventArgs e)
    {
        var vault = (Application.Current.MainWindow as MainWindow)?.Vault;
        if (vault is null) return;

        var files = LegacyMigration.Find();
        if (files.Count == 0) { TxtSaved.Text = "没有发现旧版本的明文文件"; return; }

        var account = vault.ActiveAccount ?? vault.Accounts.FirstOrDefault();
        if (account is null) { TxtSaved.Text = "请先创建账号"; return; }

        var answer = MessageBox.Show(this,
            $"发现旧明文文件 {files.Count} 个（共 {files.Sum(f => f.Bytes) / 1024.0 / 1024:F1} MB）。\n\n" +
            $"导入到账号「{account.DisplayName}」的加密库，并删除原明文文件？",
            "导入旧明文文件", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        TxtSaved.Text = "正在导入…";
        var progress = new Progress<string>(message => TxtSaved.Text = message);
        var (imported, failed, error) = await LegacyMigration.ImportAsync(account, progress, deleteSource: true, CancellationToken.None);
        TxtSaved.Text = $"导入完成：成功 {imported}，失败 {failed}" + (error is null ? "" : $"（{error}）");
        RefreshVaultState();
    }

    private void OnLogEntry(LogEntry entry) => Dispatcher.BeginInvoke(() =>
    {
        TxtLog.Text += entry + Environment.NewLine;
        if (TxtLog.Text.Length > 60_000) TxtLog.Text = TxtLog.Text[^30_000..];
        if (ViewLog.Visibility == Visibility.Visible) ViewLog.ScrollToEnd();
    });

    private void OnNotice(string message) => Dispatcher.BeginInvoke(() =>
    {
        TxtEvents.Text = message + Environment.NewLine + TxtEvents.Text;
        if (TxtEvents.Text.Length > 40_000) TxtEvents.Text = TxtEvents.Text[..20_000];
    });

    private void RefreshNetworkText()
    {
        var lan = _host.Lan;
        TxtNetwork.Text =
            $"网卡　　{(lan is null ? "未找到" : lan.Description)}\n" +
            $"本机地址　{(lan is null ? "-" : lan.Address.ToString())}\n" +
            $"网络位置　{_host.NetworkCategory}\n" +
            $"防火墙　　{(_host.FirewallRulePresent ? "已放行" : "未放行")}\n" +
            $"镜像流　　{_host.Miracast.StreamInfo}";
    }

    // ------------------------------------------------------------------ 导航

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || sender is not RadioButton rb) return;
        ShowPage(rb.Name switch
        {
            "NavVisual" => "visual",
            "NavNetwork" => "network",
            "NavSecurity" => "security",
            "NavDiag" => "diag",
            "NavAbout" => "about",
            _ => "general",
        });
    }

    private void ShowPage(string page)
    {
        PageGeneral.Visibility = page == "general" ? Visibility.Visible : Visibility.Collapsed;
        PageVisual.Visibility = page == "visual" ? Visibility.Visible : Visibility.Collapsed;
        PageNetwork.Visibility = page == "network" ? Visibility.Visible : Visibility.Collapsed;
        PageSecurity.Visibility = page == "security" ? Visibility.Visible : Visibility.Collapsed;
        PageDiag.Visibility = page == "diag" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "network") RefreshNetworkText();
        if (page == "security") { RefreshVaultState(); RefreshCheckup(); }
    }

    // ------------------------------------------------------------------ 安全页

    private void InitSecuritySection(CastSettings cfg)
    {
        SwPrivacyLog.IsChecked = cfg.PrivacyLog;
        SwShowDeviceName.IsChecked = cfg.ShowTransmitterName;
        SwTakeover.IsChecked = cfg.AllowConnectionTakeover;
        SwKeyboardBackchannel.IsChecked = cfg.KeyboardBackchannel;
        SwForgetDevices.IsChecked = cfg.ForgetDeviceHistory;
        SwAntiCapture.IsChecked = cfg.AntiCapture;
        RefreshAntiCaptureState();

        CmbPairing.Items.Clear();
        CmbPairing.Items.Add("本机确认（推荐）");
        CmbPairing.Items.Add("要求 PIN（更严格）");
        CmbPairing.SelectedIndex = string.Equals(cfg.PairingMode, "Pin", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        CmbAntiCaptureFallback.Items.Clear();
        CmbAntiCaptureFallback.Items.Add("录制期间暂停保护（推荐，能录上）");
        CmbAntiCaptureFallback.Items.Add("保护优先：开启保护时不允许录制");
        CmbAntiCaptureFallback.SelectedIndex =
            string.Equals(cfg.AntiCaptureFallback, "BlockRecording", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        SwLockOnSessionLock.IsChecked = cfg.LockOnSessionLock;

        CmbIdleRefresh.Items.Clear();
        foreach (var value in new[] { "不自动刷新", "5 分钟", "10 分钟", "30 分钟" }) CmbIdleRefresh.Items.Add(value);
        CmbIdleRefresh.SelectedIndex = cfg.IdleBroadcastRefreshMinutes switch { <= 0 => 0, <= 5 => 1, <= 10 => 2, _ => 3 };

        TxtAntiCaptureReport.Text = "点「运行反截屏自检」实测本机的取帧路径（需要正在投屏才能测出真实结果）。";
        RefreshCheckup();
    }

    private void SecurityOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        var cfg = _host.Settings;
        cfg.PairingMode = CmbPairing.SelectedIndex == 1 ? "Pin" : "Confirm";
        cfg.AntiCaptureFallback = CmbAntiCaptureFallback.SelectedIndex == 1 ? "BlockRecording" : "PauseWhileRecording";
        cfg.IdleBroadcastRefreshMinutes = new[] { 0, 5, 10, 30 }[Math.Max(0, CmbIdleRefresh.SelectedIndex)];
        _host.Miracast.IdleBroadcastRefreshMinutes = cfg.IdleBroadcastRefreshMinutes;
        cfg.Save();
        TxtSaved.Text = "已自动保存";
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        bool ok = _host.Log.TryClearFile();
        TxtSaved.Text = ok ? "日志文件已删除（新日志会继续写入）" : "日志删除失败（可能正被占用）";
    }

    /// <summary>
    /// 把"现在到底防不防截屏"直接写出来 —— 用户要的就是一眼能确认的隐私开关。
    /// </summary>
    private void RefreshAntiCaptureState()
    {
        try
        {
            bool on = SwAntiCapture.IsChecked == true;
            TxtAntiCaptureState.Text = on
                ? "当前：已开启 —— 其他程序截本窗口只会得到黑屏（本程序自己的截图/录制不受影响）"
                : "当前：已关闭 —— 画面可以被截屏、录制、共享";
            TxtAntiCaptureState.Foreground = (System.Windows.Media.Brush)FindResource(on ? "B.Ok" : "B.TextDim");
        }
        catch { }
    }

    // ------------------------------------------------------------------ 启动入场动画：选视频 + 裁时长

    /// <summary>用户在"选择本地文件…"里挑的那个文件（设置里存成 file:完整路径）。</summary>
    private string _introCustomPath = "";

    /// <summary>"选择本地文件…"那一项在列表里的位置（选中它就弹文件对话框）。</summary>
    private const string IntroBrowseItem = "选择本地文件…";

    private void InitIntroVideoSection(CastSettings s)
    {
        _introCustomPath = s.IntroSource.StartsWith(IntroClips.FilePrefix, StringComparison.OrdinalIgnoreCase)
            ? s.IntroSource[IntroClips.FilePrefix.Length..]
            : "";

        CmbIntroVideo.Items.Clear();
        foreach (IntroClips.Clip clip in IntroClips.BuiltIn) CmbIntroVideo.Items.Add(clip.Title);
        if (_introCustomPath.Length > 0) CmbIntroVideo.Items.Add(CustomItemLabel());
        int browseIndex = CmbIntroVideo.Items.Add(IntroBrowseItem);

        // 选中的是哪一项
        int selected = 0;
        if (_introCustomPath.Length > 0)
        {
            selected = browseIndex - 1;
        }
        else if (!string.IsNullOrWhiteSpace(s.IntroSource))
        {
            int hit = IntroClips.BuiltIn.ToList()
                .FindIndex(c => string.Equals(c.Key, s.IntroSource, StringComparison.OrdinalIgnoreCase));
            if (hit >= 0) selected = hit;
        }

        _suppressIntroVideoChange = true;
        CmbIntroVideo.SelectedIndex = selected;
        _suppressIntroVideoChange = false;

        TxtIntroStart.Text = s.IntroStart > 0.05 ? s.IntroStart.ToString("0.#") : "0";
        TxtIntroEnd.Text = s.IntroEnd > 0.05 ? s.IntroEnd.ToString("0.#") : "0";
        TxtIntroExtend.Text = s.IntroExtend > 0.05 ? s.IntroExtend.ToString("0.#") : "0";
        LoadIntroStyle(s);
        RefreshIntroLength();
    }

    /// <summary>
    /// 读当前所选素材的真实时长，并把"结束秒"默认成它（用户要求：有多少秒就默认多少秒）。
    /// </summary>
    private void RefreshIntroLength(bool resetEndToFullLength = false)
    {
        try
        {
            string source = CurrentIntroSource();
            string path = source.Length > 0 ? IntroClips.ResolvePath(source, AppContext.BaseDirectory) : "";
            double? seconds = path.Length > 0 ? Mp4Probe.TryGetDurationSeconds(path) : null;

            if (seconds is null)
            {
                TxtIntroLength.Text = "素材时长未知";
                TxtIntroHint.Text = "读不出这个文件的时长（可能不是 MP4/MOV），结束秒请自己填";
                return;
            }

            TxtIntroLength.Text = $"素材共 {seconds.Value:0.#} 秒";
            TxtIntroHint.Text = "预览会立刻按当前设置播一遍（不用重启）";

            if (resetEndToFullLength || TxtIntroEnd.Text.Trim() is "" or "0")
            {
                TxtIntroEnd.Text = seconds.Value.ToString("0.#");   // 换素材就按新素材的整段时长来
            }
            else if (double.TryParse(TxtIntroEnd.Text.Trim(), out double end) && end > seconds.Value + 0.2)
            {
                TxtIntroEnd.Text = seconds.Value.ToString("0.#");     // 填超了纠回来
            }
        }
        catch { }
    }

    /// <summary>画面样式改动：立刻写进设置并显示（预览就能看到效果）。</summary>
    private void IntroStyle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        SaveIntroStyle();
    }

    private void IntroStyle_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        SaveIntroStyle();
    }

    /// <summary>把画面相关的可编辑项写进设置。</summary>
    /// <summary>把"跟随窗口"这类文字解析成 0（＝跟随）。</summary>
    private static double ParseSecondsOrFollow(string? text)
        => double.TryParse((text ?? "").Trim().TrimEnd('秒', ' '), out double value) ? value : 0;

    private void SaveIntroStyle()
    {
        try
        {
            var s = _host.Settings;
            s.IntroWidth = (int)ParseSecondsOrFollow(TxtIntroWidth.Text);
            s.IntroHeight = (int)ParseSecondsOrFollow(TxtIntroHeight.Text);
            s.IntroStretch = (CmbIntroStretch.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "fill";
            s.IntroAlignH = ParseTaggedNumber(CmbIntroAlignH, 0.5);
            s.IntroAlignV = ParseTaggedNumber(CmbIntroAlignV, 0.5);
            s.IntroVolume = Math.Clamp(SldIntroVolume.Value / 100.0, 0, 1);
            s.IntroFadeInMs = (int)Math.Clamp(ParseSeconds(TxtIntroFadeIn.Text), 80, 5000);
            s.IntroFadeOutMs = (int)Math.Clamp(ParseSeconds(TxtIntroFadeOut.Text), 100, 5000);
            s.IntroFlash = ChkIntroFlash.IsChecked == true;
            s.IntroPreviewReenter = ChkIntroReenter.IsChecked == true;
            s.Save();

            TxtIntroVolume.Text = $"{s.IntroVolume * 100:0}%";
        }
        catch { }
    }

    private static double ParseTaggedNumber(System.Windows.Controls.ComboBox box, double fallback)
    {
        try
        {
            if (box.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string tag &&
                double.TryParse(tag, out double value))
            {
                return value;
            }
        }
        catch { }
        return fallback;
    }

    private void LoadIntroStyle(CastSettings s)
    {
        // 用"跟随窗口"这样的可读文字，而不是一串 0（用户反馈：看起来像空的、不知道该填什么）
        TxtIntroWidth.Text = s.IntroWidth > 200 ? s.IntroWidth.ToString() : "跟随窗口";
        TxtIntroHeight.Text = s.IntroHeight > 150 ? s.IntroHeight.ToString() : "跟随窗口";
        SelectByTag(CmbIntroStretch, s.IntroStretch);
        SelectByTag(CmbIntroAlignH, s.IntroAlignH.ToString("0.##"));
        SelectByTag(CmbIntroAlignV, s.IntroAlignV.ToString("0.##"));
        SldIntroVolume.Value = Math.Clamp(s.IntroVolume, 0, 1) * 100;
        TxtIntroVolume.Text = $"{Math.Clamp(s.IntroVolume, 0, 1) * 100:0}%";
        TxtIntroFadeIn.Text = s.IntroFadeInMs > 0 ? s.IntroFadeInMs.ToString() : "760";
        TxtIntroFadeOut.Text = s.IntroFadeOutMs > 0 ? s.IntroFadeOutMs.ToString() : "880";
        ChkIntroFlash.IsChecked = s.IntroFlash;
        ChkIntroReenter.IsChecked = s.IntroPreviewReenter;
    }

    private static void SelectByTag(System.Windows.Controls.ComboBox box, string tag)
    {
        foreach (object item in box.Items)
        {
            if (item is System.Windows.Controls.ComboBoxItem candidate && (candidate.Tag as string) == tag)
            {
                box.SelectedItem = candidate;
                return;
            }
        }

        box.SelectedIndex = 0;
    }

    /// <summary>
    /// 预览播完（入场窗真的收场了）：把主窗口淡回来，并重新打开设置窗（方便接着调）。
    /// 全程切到界面线程做：入场窗的 Closed 是在它自己的线程上抛出来的，
    /// 在那里直接 Stop 定时器/改控件是跨线程操作（抛异常的话界面就永远放不回来了）。
    /// </summary>
    private void OnIntroPreviewClosed(int seq)
    {
        if (seq != _previewSeq) return;      // 已经开了新的一轮预览，别拿旧的一轮去还原界面
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (seq != _previewSeq) return;
                ResetPreviewHooks();
                RestoreUiAfterPreview("预览结束（界面已放回）");
            }));
        }
        catch { }
    }

    /// <summary>把界面从"预览独占"状态放回来（幂等）。</summary>
    private void RestoreUiAfterPreview(string hint)
    {
        try
        {
            if (Application.Current.MainWindow is MainWindow main) main.FinishIntroPreview();
            Show();
            BringToFrontAndFocus();
            TxtIntroHint.Text = hint;
        }
        catch { }
    }

    /// <summary>收掉这一轮预览的收尾钩子（看门狗 + Closed 订阅）。</summary>
    private void ResetPreviewHooks()
    {
        StopPreviewGuard();
        if (_previewClosed is not null)
        {
            try { NativeIntroWindow.Closed -= _previewClosed; } catch { }
            _previewClosed = null;
        }
    }

    private void StopPreviewGuard()
    {
        try
        {
            _previewGuard?.Stop();
            _previewGuard = null;
        }
        catch { }
    }

    /// <summary>
    /// 把设置窗带回最前面并交出焦点。
    /// 预览结束后必须做这件事：不然窗口虽然在，键盘焦点却在别处，
    /// 用户就会觉得"输入框锁住了、打不了字"。
    /// </summary>
    private void BringToFrontAndFocus()
    {
        try
        {
            Topmost = true;
            Topmost = false;
            Activate();
            Focus();
            System.Windows.Input.Keyboard.Focus(this);
        }
        catch { }
    }

    private System.Windows.Threading.DispatcherTimer? _previewGuard;

    /// <summary>本轮预览的 Closed 订阅（带代次校验，连点两次时旧的一轮回调不会来还原界面）。</summary>
    private Action? _previewClosed;

    /// <summary>预览轮次：每点一次 +1，旧轮次的回调/看门狗据此自动作废。</summary>
    private int _previewSeq;

    /// <summary>当前下拉选择对应的设置值（内置 key 或 file:路径）。</summary>
    private string CurrentIntroSource()
    {
        string? picked = CmbIntroVideo.SelectedItem as string;
        if (_introCustomPath.Length > 0 && picked is not null && picked.StartsWith("自定义："))
        {
            return IntroClips.FilePrefix + _introCustomPath;
        }

        int index = CmbIntroVideo.SelectedIndex;
        return index >= 0 && index < IntroClips.BuiltIn.Count ? IntroClips.BuiltIn[index].Key : "";
    }

    /// <summary>预览：立刻用当前设置播一遍入场动画（不需要重启程序）。</summary>
    private void BtnIntroPreview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveIntroSelection();

            string path = IntroClips.ResolvePath(_host.Settings.IntroSource, AppContext.BaseDirectory);
            if (path.Length == 0)
            {
                TxtIntroHint.Text = "找不到这个素材，无法预览";
                return;
            }

            var s = _host.Settings;
            (int x, int y, int w, int h) = Program.IntroWindowBounds(s);
            var style = new NativeIntroWindow.Style(s.IntroStretch, s.IntroAlignH, s.IntroAlignV,
                s.IntroVolume, s.IntroFadeInMs, s.IntroFadeOutMs, s.IntroFlash);

            // "等于重进一次"：把自己和主窗口都藏起来，让入场画面独占屏幕，播完再把界面放回来
            var main = Application.Current.MainWindow as MainWindow;

            // 连点两次预览时：先把上一轮的看门狗/订阅收掉（旧窗收场时的回调会因为代次对不上而自动忽略），
            // 再把它那块画面收掉，避免两块置顶窗叠在一起谁也点不到
            ResetPreviewHooks();
            if (NativeIntroWindow.IsPlaying) NativeIntroWindow.ForceClose();

            int seq = ++_previewSeq;
            _previewClosed = () => OnIntroPreviewClosed(seq);
            NativeIntroWindow.Closed += _previewClosed;
            if (s.IntroPreviewReenter)
            {
                main?.PrepareIntroPreview();
                Hide();
            }

            bool ok = NativeIntroWindow.Start(path, s.IntroStart, s.IntroEnd, s.IntroExtend, x, y, w, h, style);
            if (!ok)
            {
                ResetPreviewHooks();
                if (s.IntroPreviewReenter) RestoreUiAfterPreview("预览没能起来（这条渲染路径在本机不可用）");
                else TxtIntroHint.Text = "预览没能起来（这条渲染路径在本机不可用）";
                return;
            }

            try { TxtIntroHint.Text = $"正在预览…（{DateTime.Now:HH:mm:ss}）"; } catch { }
            NativeIntroWindow.Finish();       // 预览不用等程序就绪：按设置播完即淡出

            // 预览看门狗：入场窗**必须**在这个时间内收场，否则强制销毁并把界面放回来。
            // 没有它的话，一旦入场窗卡住（透明却盖在最上层），用户会点不动界面也关不掉程序。
            double guardSeconds = Math.Max(12.0, (s.IntroEnd > 0.05 ? s.IntroEnd - s.IntroStart : 60)
                                                + s.IntroExtend + s.IntroFadeOutMs / 1000.0 + 8.0);
            StopPreviewGuard();
            _previewGuard = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(guardSeconds), System.Windows.Threading.DispatcherPriority.Normal, (_, _) =>
            {
                if (seq != _previewSeq) return;      // 已经换了一轮预览，这条看门狗作废
                ResetPreviewHooks();
                try
                {
                    NativeIntroWindow.ForceClose();
                    RestoreUiAfterPreview("预览超时，已强制收场（界面已放回）");
                }
                catch { }
            }, Dispatcher);
            _previewGuard.Start();
        }
        catch (Exception ex)
        {
            TxtIntroHint.Text = "预览失败：" + ex.Message;
        }
    }

    private string CustomItemLabel() => "自定义：" + System.IO.Path.GetFileName(_introCustomPath);

    private bool _suppressIntroVideoChange;

    private void IntroVideo_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressIntroVideoChange) return;

        // 选中"选择本地文件…"：弹文件对话框；用户取消就退回原来的选择
        if (CmbIntroVideo.SelectedItem as string == IntroBrowseItem)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择启动视频",
                Filter = "视频文件|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv|所有文件|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != true)
            {
                _suppressIntroVideoChange = true;
                CmbIntroVideo.SelectedIndex = _introCustomPath.Length > 0
                    ? CmbIntroVideo.Items.Count - 2      // 原来那个自定义项
                    : 0;
                _suppressIntroVideoChange = false;
                return;
            }

            _introCustomPath = dialog.FileName;
            _suppressIntroVideoChange = true;
            // 把自定义项换成新文件（先删旧的）
            if (CmbIntroVideo.Items.Count > 0 && (CmbIntroVideo.Items[^1] as string) == IntroBrowseItem &&
                CmbIntroVideo.Items.Count >= 2 && (CmbIntroVideo.Items[^2] as string)?.StartsWith("自定义：") == true)
            {
                CmbIntroVideo.Items.RemoveAt(CmbIntroVideo.Items.Count - 2);
            }
            CmbIntroVideo.Items.Insert(CmbIntroVideo.Items.Count - 1, CustomItemLabel());
            CmbIntroVideo.SelectedIndex = CmbIntroVideo.Items.Count - 2;
            _suppressIntroVideoChange = false;
        }

        RefreshIntroLength(resetEndToFullLength: true);
        SaveIntroSelection();
    }

    /// <summary>把当前选择写进设置（视频来源 + 起止秒 + 延长秒）。</summary>
    private void SaveIntroSelection()
    {
        try
        {
            var s = _host.Settings;
            string? picked = CmbIntroVideo.SelectedItem as string;

            if (_introCustomPath.Length > 0 && picked is not null && picked.StartsWith("自定义："))
            {
                s.IntroSource = IntroClips.FilePrefix + _introCustomPath;
            }
            else
            {
                int index = CmbIntroVideo.SelectedIndex;
                s.IntroSource = index >= 0 && index < IntroClips.BuiltIn.Count ? IntroClips.BuiltIn[index].Key : "";
            }

            s.IntroStart = ParseSeconds(TxtIntroStart.Text);
            s.IntroEnd = ParseSeconds(TxtIntroEnd.Text);
            s.IntroExtend = ParseSeconds(TxtIntroExtend.Text);
            SaveIntroStyle();
            s.Save();
        }
        catch { }
    }

    private static double ParseSeconds(string? text)
    {
        if (double.TryParse((text ?? "").Trim(), out double value) && value > 0) return Math.Round(value, 2);
        return 0;
    }

    private void BtnAntiCaptureCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is not MainWindow mw)
        {
            TxtAntiCaptureReport.Text = "主窗口不可用，无法自检。";
            return;
        }
        try
        {
            TxtAntiCaptureReport.Text = mw.RunAntiCaptureCheck();
        }
        catch (Exception ex)
        {
            TxtAntiCaptureReport.Text = "自检失败：" + ex.Message;
        }
    }

    private void BtnChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var vault = (Application.Current.MainWindow as MainWindow)?.Vault;
        if (vault is null || vault.Accounts.Count == 0) { TxtSaved.Text = "还没有账号"; return; }

        var dialog = new VaultChangePasswordWindow(vault) { Owner = this };
        dialog.ShowDialog();
        TxtSaved.Text = dialog.Changed ? "密码已更新" : "未修改密码";
        RefreshVaultState();
    }

    private void RefreshCheckup()
    {
        var vault = (Application.Current.MainWindow as MainWindow)?.Vault;
        var items = PrivacyCheckup.Run(_host.Settings.PrivacyLog, _host.Settings.AntiCapture,
            vault?.IsUnlocked ?? false, _host.Settings.ForgetDeviceHistory,
            _host.Settings.AntiCapture ? "取帧路径见下方自检" : "未开启");

        var text = new System.Text.StringBuilder();
        int bad = items.Count(i => !i.Ok);
        text.AppendLine(bad == 0 ? "体检结果：未发现明显问题 ✓" : $"体检结果：{bad} 项建议处理");
        text.AppendLine();
        foreach (var item in items)
        {
            text.AppendLine($"{(item.Ok ? "✓" : "⚠")} {item.Title}");
            text.AppendLine($"    {item.Detail}");
        }
        TxtCheckup.Text = text.ToString();
    }

    private void BtnCheckup_Click(object sender, RoutedEventArgs e)
    {
        RefreshCheckup();
        TxtSaved.Text = "已重新体检";
    }

    private void BtnFixGameBar_Click(object sender, RoutedEventArgs e)
    {
        bool ok = PrivacyCheckup.DisableGameBarBackgroundRecording();
        TxtSaved.Text = ok ? "已关闭 Game Bar 后台录制（可在系统设置里改回）" : "修改失败（可能被策略限制）";
        RefreshCheckup();
    }

    // ------------------------------------------------------------------ 主题

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || sender is not RadioButton rb || rb.Tag is not string theme) return;
        App.ApplyTheme(theme);
        TxtSaved.Text = $"已切换为{(theme == "System" ? "跟随系统" : theme == "Light" ? "浅色" : "深色")}主题";
    }

    // ------------------------------------------------------------------ 画面

    private void BtnFit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        _host.Settings.MirrorFit = tag;
        _host.Settings.Save();
        UpdateFitButtons(tag);
        if (Application.Current.MainWindow is MainWindow main)
            main.SetFit(tag switch
            {
                "Fill" => Interop.CompositionVideoHost.VideoFit.Fill,
                "Stretch" => Interop.CompositionVideoHost.VideoFit.Stretch,
                "Native" => Interop.CompositionVideoHost.VideoFit.Native,
                _ => Interop.CompositionVideoHost.VideoFit.Fit,
            });
    }

    private void UpdateFitButtons(string current)
    {
        foreach (var (btn, tag) in new[]
                 {
                     (BtnFitFit, "Fit"), (BtnFitFill, "Fill"), (BtnFitStretch, "Stretch"), (BtnFitNative, "Native"),
                 })
        {
            if (btn is null) continue;
            bool active = string.Equals(tag, current, StringComparison.OrdinalIgnoreCase);
            // 未选中：卡片色的实心块 + 细边，落在浅灰的内容面上才分得清是"可点的选项"
            btn.Background = active
                ? (System.Windows.Media.Brush)FindResource("B.Accent")
                : (System.Windows.Media.Brush)FindResource("B.Surface");
            btn.Foreground = active
                ? (System.Windows.Media.Brush)FindResource("B.AccentText")
                : (System.Windows.Media.Brush)FindResource("B.TextDim");
            btn.FontWeight = active ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Normal;
        }
    }

    private void SldZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtZoomValue is not null) TxtZoomValue.Text = $"{e.NewValue:F0}%";
        if (!_loaded) return;
        _host.Settings.MirrorZoom = e.NewValue / 100.0;
        _host.Settings.Save();
        if (Application.Current.MainWindow is MainWindow main) main.SetZoom(e.NewValue / 100.0);
    }

    private void BtnZoomReset_Click(object sender, RoutedEventArgs e) => SldZoom.Value = 100;

    // ------------------------------------------------------------------ 动作

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>任何改动都立即生效并保存（不再需要点"保存设置"）。</summary>
    private void Setting_Changed(object sender, RoutedEventArgs e) => AutoSave();

    private void AudioTarget_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => AutoSave();

    /// <summary>
    /// 填充可选的播放设备。枚举是异步的，所以放在窗口显示之后做；
    /// 找不到设置里记着的那台设备时给出明确提示，而不是静默显示空列表 ——
    /// 设备被拔掉/关掉是很常见的，用户需要知道"为什么没生效"。
    /// </summary>
    private async Task LoadAudioEndpointsAsync(CastSettings s)
    {
        try
        {
            IReadOnlyList<AudioEndpoint> endpoints = await AudioEndpointService.ListRenderEndpointsAsync();
            // 显示名由 AudioEndpoint.ToString() 提供（见 Core/Audio/AudioEndpointService.cs）
            CmbAudioTarget.ItemsSource = endpoints;

            AudioEndpoint? match = endpoints.FirstOrDefault(e => e.Id == s.AudioSwitchTargetId);
            if (match is not null)
            {
                CmbAudioTarget.SelectedItem = match;
                TxtAudioHint.Text = "选择投屏时使用的播放设备";
            }
            else if (endpoints.Count == 0)
            {
                TxtAudioHint.Text = "没有找到可用的播放设备";
            }
            else if (!string.IsNullOrWhiteSpace(s.AudioSwitchTargetId))
            {
                TxtAudioHint.Text = "原来选定的设备已不可用，请重新选择";
            }
            else
            {
                TxtAudioHint.Text = "未选择 —— 自动切换不会生效";
            }
        }
        catch (Exception ex)
        {
            TxtAudioHint.Text = "读取播放设备失败：" + ex.Message;
        }
    }

    private void TxtName_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_loaded) return;
        AutoSave();
    }

    private void AutoSave()
    {
        if (!_loaded) return;
        try
        {
            ApplyAndSave();
            TxtSaved.Text = _host.Settings.LastSaveError.Length == 0
                ? $"已自动保存 · {DateTime.Now:HH:mm:ss}"
                : "保存失败：" + _host.Settings.LastSaveError;
        }
        catch { }
    }

    private void ApplyAndSave()
    {
        var s = _host.Settings;
        string name = TxtName.Text.Trim();
        if (name.Length > 0) s.DeviceName = name;
        s.AutoStartMiracast = SwAutoStart.IsChecked == true;
        s.MiracastConfirmConnection = SwConfirm.IsChecked == true;
        s.MuteAudio = SwMute.IsChecked == true;
        s.AutoStartAirPlay = SwAirPlay.IsChecked == true;
        s.AudioSwitchEnabled = SwAudioSwitch.IsChecked == true;
        s.AudioSwitchRestore = SwAudioRestore.IsChecked == true;
        if (CmbAudioTarget.SelectedItem is AudioEndpoint target)
        {
            s.AudioSwitchTargetId = target.Id;
        }
        s.ShowStats = SwStats.IsChecked == true;
        s.StatsOnlyWhenLagging = SwStatsAuto.IsChecked == true;
        s.ShowIntro = SwIntro.IsChecked == true;
        SaveIntroSelection();
        s.MirrorZoom = SldZoom.Value / 100.0;
        s.ClearCapturesOnExit = SwClearOnExit.IsChecked == true;
        s.MinimizeToTray = SwTray.IsChecked == true;
        s.NotifyOnConnect = SwNotify.IsChecked == true;
        s.GlobalHotkeys = SwHotkeys.IsChecked == true;
        s.UseMica = SwMica.IsChecked == true;
        s.AutoFullscreen = SwAutoFullscreen.IsChecked == true;
        s.HideCursorInFullscreen = SwHideCursor.IsChecked == true;
        s.PreventSleep = SwPreventSleep.IsChecked == true;
        s.ClearCapturesOnExit = SwClearOnExit.IsChecked == true;

        // 安全页
        s.PrivacyLog = SwPrivacyLog.IsChecked == true;
        s.ShowTransmitterName = SwShowDeviceName.IsChecked == true;
        s.AllowConnectionTakeover = SwTakeover.IsChecked == true;
        s.KeyboardBackchannel = SwKeyboardBackchannel.IsChecked == true;
        s.ForgetDeviceHistory = SwForgetDevices.IsChecked == true;
        s.AntiCapture = SwAntiCapture.IsChecked == true;
        s.LockOnSessionLock = SwLockOnSessionLock.IsChecked == true;
        s.Save();

        StartupRegistration.Apply(SwStartup.IsChecked == true);
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.ApplyGlobalHotkeySetting(s.GlobalHotkeys);
            mw.ApplyMica(s.UseMica);
            mw.ApplyAntiCapture();     // 防截屏开关：改完立刻生效，不用重启
        }
        RefreshAntiCaptureState();
        _host.Miracast.MuteAudio = s.MuteAudio;
        _host.Miracast.PrivacyLog = s.PrivacyLog;
        _host.Miracast.AllowConnectionTakeover = s.AllowConnectionTakeover;
        _host.Miracast.TransmitKeyboard = s.KeyboardBackchannel;   // 连接时才应用，改完重连一次生效
        _host.Miracast.ReapplyInputDevicePolicy();                 // 已经连着的就当场生效，不用重连
        _host.Miracast.PairingMode = s.PairingMode;
        _host.Miracast.ForgetDeviceHistory = s.ForgetDeviceHistory;
        _host.Log.PrivacyMode = s.PrivacyLog;

        // AirPlay 开关立即生效：打开就广播，关掉就停（不需要重启）
        if (s.AutoStartAirPlay && !_host.AirPlay.IsRunning) _host.StartAirPlay();
        else if (!s.AutoStartAirPlay && _host.AirPlay.IsRunning) _host.StopAirPlay();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e) => AutoSave();

    private void BtnTutorial_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow main) main.ShowTutorialAgain();
    }

    private async void BtnFixNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow main) await main.PrepareNetworkAsync();
        RefreshNetworkText();
    }

    private void BtnRefreshNetwork_Click(object sender, RoutedEventArgs e)
    {
        RefreshNetworkText();
        TxtSaved.Text = "网络状态已刷新";
    }

    // ------------------------------------------------------------------ 诊断

    private void DiagTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        ViewResults.Visibility = tag == "results" ? Visibility.Visible : Visibility.Collapsed;
        ViewLog.Visibility = tag == "log" ? Visibility.Visible : Visibility.Collapsed;
        ViewEvents.Visibility = tag == "events" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "events" && Application.Current.MainWindow is MainWindow main)
            TxtEvents.Text = main.EventsText;
    }

    private async void BtnSelfTest_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        _items.Clear();
        TxtSummary.Text = "自检进行中…";
        ViewResults.Visibility = Visibility.Visible;
        ViewLog.Visibility = Visibility.Collapsed;
        ViewEvents.Visibility = Visibility.Collapsed;
        try
        {
            string asset = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.mp4");
            var progress = new Progress<SelfTestItem>(item =>
            {
                if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => _items.Add(item)); return; }
                _items.Add(item);
                ViewResults.ScrollToEnd();
            });
            var results = await Task.Run(async () =>
            {
                var runner = new SelfTestRunner(_host.Settings, _host.Log, null, null, _host.Miracast, asset, _renderProbe);
                return await runner.RunAsync(progress);
            });
            int passed = results.Count(r => r.Outcome == SelfTestOutcome.Passed);
            int warned = results.Count(r => r.Outcome == SelfTestOutcome.Warning);
            int failed = results.Count(r => r.Outcome == SelfTestOutcome.Failed);
            TxtSummary.Text = $"通过 {passed}" + (warned > 0 ? $"，警告 {warned}" : "") + (failed > 0 ? $"，失败 {failed}" : "，无失败");
        }
        catch (Exception ex) { TxtSummary.Text = "自检异常：" + ex.Message; }
        finally { _running = false; }
    }

    // ------------------------------------------------------------------ 新增：强调色 / 密度 / 画质 / 诊断

    private void BuildAccentSwatches(string current)
    {
        AccentRow.Children.Clear();
        var presets = new (string Key, string Name, string Color)[]
        {
            ("Mint", "薄荷绿", "#3DDC97"),
            ("Amber", "琥珀", "#F0B45E"),
            ("Violet", "蓝紫", "#A78BFA"),
            ("Rose", "樱粉", "#F472B6"),
            ("Blue", "天蓝", "#60A5FA"),
            ("System", "跟随系统", "#8A9199"),
        };
        foreach (var (key, name, color) in presets)
        {
            bool active = string.Equals(key, current, StringComparison.OrdinalIgnoreCase);
            var dot = new System.Windows.Controls.Border
            {
                Width = active ? 26 : 22, Height = active ? 26 : 22,
                CornerRadius = new CornerRadius(999),
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)),
                BorderBrush = active ? (System.Windows.Media.Brush)FindResource("B.Text") : System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(active ? 2 : 0),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = name,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            dot.MouseLeftButtonUp += (_, _) =>
            {
                _host.Settings.Accent = key;
                _host.Settings.Save();
                App.ApplyAccent(key);
                BuildAccentSwatches(key);
                TxtSaved.Text = $"强调色：{name}";
            };
            AccentRow.Children.Add(dot);
        }
    }

    private void Density_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || sender is not RadioButton rb || rb.Tag is not string density) return;
        _host.Settings.Density = density;
        _host.Settings.Save();
        if (Application.Current.MainWindow is MainWindow mw) mw.ApplyDensity(density);
        TxtSaved.Text = density == "Compact" ? "已切换为紧凑布局" : "已切换为舒适布局";
    }

    private void BtnQuality_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        _host.Settings.QualityPreset = tag;
        _host.Settings.LowLatency = tag != "HighQuality";
        _host.Settings.Save();
        _host.Miracast.QualityPreset = tag;
        _host.Miracast.LowLatency = _host.Settings.LowLatency;
        UpdateQualityButtons(tag);
        if (_host.Miracast.ConnectionCount > 0) _ = _host.Miracast.ReapplyStreamPreferencesAsync();
        TxtSaved.Text = tag switch { "HighQuality" => "已切换为高画质", "Balanced" => "已切换为均衡", _ => "已切换为低延迟" };
    }

    private void UpdateQualityButtons(string current)
    {
        foreach (var (btn, tag) in new[]
                 {
                     (BtnQualityLow, "LowLatency"), (BtnQualityBalanced, "Balanced"), (BtnQualityHigh, "HighQuality"),
                 })
        {
            if (btn is null) continue;
            bool active = string.Equals(tag, current, StringComparison.OrdinalIgnoreCase);
            btn.Background = active ? (System.Windows.Media.Brush)FindResource("B.Accent")
                                    : (System.Windows.Media.Brush)FindResource("B.Surface");
            btn.Foreground = active ? (System.Windows.Media.Brush)FindResource("B.AccentText")
                                    : (System.Windows.Media.Brush)FindResource("B.TextDim");
            btn.FontWeight = active ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Normal;
        }
    }

    private void BtnOpenCaptureFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw) mw.OpenLegacyFolder();
    }

    private void BtnClearCaptures_Click(object sender, RoutedEventArgs e)
    {
        var files = LegacyMigration.Find();
        if (files.Count == 0) { TxtSaved.Text = "没有发现旧版本的明文文件"; return; }

        var answer = MessageBox.Show(this,
            $"将删除旧版本留在图片/视频文件夹里的明文文件 {files.Count} 个。\n\n" +
            "（加密库里的内容不受影响；删除后这些明文文件无法恢复。）",
            "删除旧明文文件", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        int n = CaptureService.ClearLegacy();
        int temp = LegacyMigration.CleanupTempFrameDirectories();
        TxtSaved.Text = $"已删除 {n} 个明文文件" + (temp > 0 ? $"，清理 {temp} 个临时帧目录" : "");
    }

    private async void BtnCleanupFirewall_Click(object sender, RoutedEventArgs e)
    {
        TxtSaved.Text = "正在请求管理员授权…";
        var (ok, message) = await Task.Run(() =>
        {
            bool r = NetworkFixer.RequestElevation(out string m, setPrivate: false, cleanupFirewallOnly: true);
            return (r, m);
        });
        TxtSaved.Text = ok ? "网络规则已清理" : (message.Length > 0 ? message : "已取消");
    }

    private void BtnWizard_Click(object sender, RoutedEventArgs e)
        => new DiagnosticWizard(_host) { Owner = this }.ShowDialog();

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        string summary = $"接收端：{(_host.Miracast.IsListening ? "监听中" : "未监听")}\n" +
                         $"流参数：{_host.Miracast.StreamInfo}\n" +
                         $"网络：{_host.NetworkCategory}，防火墙 {(_host.FirewallRulePresent ? "已放行" : "未放行")}\n" +
                         $"热点占用：{_host.HotspotConflict ?? "无"}";
        string? path = DiagnosticBundle.Export(_host.Settings, _host.Log, summary);
        TxtSaved.Text = path is null ? "导出失败" : "诊断包已导出到桌面";
        if (path is not null) _host.Publish("诊断包已导出：" + path);
    }

    private void BtnSaveUpdate_Click(object sender, RoutedEventArgs e)
    {
        _host.Settings.UpdateUrl = TxtUpdateUrl.Text.Trim();
        _host.Settings.Save();
        TxtUpdateResult.Text = "已保存";
    }

    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        string url = TxtUpdateUrl.Text.Trim();
        if (url.Length == 0)
        {
            TxtUpdateResult.Text = "未配置更新源（留空即不检查）";
            return;
        }
        BtnCheckUpdate.IsEnabled = false;
        TxtUpdateResult.Text = "正在检查…";
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            string json = await http.GetStringAsync(url);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            string latest = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            string page = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            string current = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            if (latest.Length == 0) { TxtUpdateResult.Text = "更新源格式不正确"; return; }
            if (string.CompareOrdinal(latest, current) > 0)
            {
                TxtUpdateResult.Text = $"发现新版本 {latest}";
                if (page.Length > 0)
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true }); } catch { }
                }
            }
            else TxtUpdateResult.Text = $"已是最新版本（{current}）";
        }
        catch (Exception ex) { TxtUpdateResult.Text = "检查失败：" + ex.Message; }
        finally { BtnCheckUpdate.IsEnabled = true; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}