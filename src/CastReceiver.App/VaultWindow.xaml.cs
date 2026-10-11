using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CastReceiver.App.Services;
using CastReceiver.Core.Security;

namespace CastReceiver.App;

/// <summary>
/// 「我的库」：左边加密条目列表（只解密缩略图），右边内嵌查看器（图片 / 录像）。
/// 窗口关闭或点「锁定」时立即清零私钥 —— 之后要看内容必须重新输密码。
/// </summary>
public partial class VaultWindow : Window
{
    private sealed class VaultRow : INotifyPropertyChanged
    {
        private BitmapSource? _thumb;

        public VaultRow(VaultItemInfo info) => Info = info;

        public VaultItemInfo Info { get; }

        public BitmapSource? Thumb
        {
            get => _thumb;
            set { _thumb = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb))); }
        }

        public string Title => Info.DisplayName;

        public string Subtitle
        {
            get
            {
                var parts = new List<string>();
                if (Info.Kind == VaultItemKind.Recording)
                {
                    parts.Add(Info.Duration.TotalSeconds >= 1
                        ? $"{Info.Duration:mm\\:ss}"
                        : $"{Info.FrameCount} 帧");
                    parts.Add($"{Info.Fps}fps");
                }
                else parts.Add("截图");
                parts.Add(Info.Bytes >= 1024 * 1024
                    ? $"{Info.Bytes / 1024.0 / 1024.0:F1} MB"
                    : $"{Info.Bytes / 1024} KB");
                if (!Info.Completed) parts.Add("未完成");
                return string.Join(" · ", parts);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly VaultService _vault;
    private readonly VaultLibrary _library;
    private readonly ObservableCollection<VaultRow> _rows = new();
    private readonly DispatcherTimer _idleTimer;

    private VaultPlayback? _playback;
    private VaultRow? _selected;
    private bool _suppressSeek;
    private bool _fullscreen;
    private bool _locked;
    private WindowState _prevState = WindowState.Normal;
    private Rect _prevBounds;
    private DateTime _lastActivity = DateTime.UtcNow;
    private double _speed = 1.0;

    public VaultWindow(VaultService vault)
    {
        InitializeComponent();
        _vault = vault;
        _library = vault.Library ?? throw new InvalidOperationException("库尚未解锁");

        var account = vault.Accounts.FirstOrDefault(a => a.Id == _library.AccountId);
        TxtAccount.Text = account?.DisplayName ?? "账号";
        LstItems.ItemsSource = _rows;

        ApplyRetentionOnOpen();
        ReloadRows();

        // 缩略图后台解密（打开大库时界面不卡）
        _ = LoadThumbnailsAsync();

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _idleTimer.Tick += (_, _) => CheckIdleLock();
        _idleTimer.Start();

        PreviewKeyDown += OnPreviewKeyDown;
        MouseMove += (_, _) => _lastActivity = DateTime.UtcNow;
        MouseDown += (_, _) => _lastActivity = DateTime.UtcNow;
        Closed += (_, _) => Cleanup();
        Loaded += (_, _) =>
        {
            // 库里显示的是解密后的画面：同样加上防捕获保护
            if (_vault.Host.Settings.AntiCapture) WindowProtection.SetProtection(this, true);
            TxtStatus.Text = BuildStatus();
            LstItems.Focus();
        };
    }

    // ------------------------------------------------------------------ 列表

    private void ReloadRows()
    {
        var previous = _selected?.Info.ItemId;
        _rows.Clear();
        foreach (var info in _library.Items)
            _rows.Add(new VaultRow(info));

        TxtEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtStatus.Text = BuildStatus();

        if (previous is { } id)
        {
            var row = _rows.FirstOrDefault(r => r.Info.ItemId == id);
            if (row is not null) LstItems.SelectedItem = row;
        }
        else if (_rows.Count > 0) LstItems.SelectedIndex = 0;
        else ClearPreview();
    }

    private async Task LoadThumbnailsAsync()
    {
        var rows = _rows.ToArray();
        foreach (var row in rows)
        {
            if (row.Thumb is not null) continue;
            var itemId = row.Info.ItemId;
            byte[]? thumb = null;
            await Task.Run(() => thumb = _library.LoadThumbnail(itemId, out _));
            if (thumb is null) continue;
            var source = VaultPlayback.DecodeBytes(thumb);
            if (source is null) continue;
            await Dispatcher.BeginInvoke(new Action(() => row.Thumb = source));
        }
    }

    private string BuildStatus()
    {
        string size = _library.TotalBytes >= 1024L * 1024 * 1024
            ? $"{_library.TotalBytes / 1024.0 / 1024 / 1024:F2} GB"
            : $"{_library.TotalBytes / 1024.0 / 1024:F1} MB";
        string extra = _library.UnreadableItems > 0 ? $"　无法解密 {_library.UnreadableItems} 条" : "";
        string incomplete = _library.Items.Count(i => !i.Completed) is var n && n > 0 ? $"　未完成 {n} 条" : "";
        return $"共 {_library.Items.Count} 条（截图 {_library.ScreenshotCount} / 录像 {_library.RecordingCount}）　占用 {size}" +
               $"　库位置：{_library.ItemsDirectory}{extra}{incomplete}";
    }

    private void LstItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _lastActivity = DateTime.UtcNow;
        if (LstItems.SelectedItem is VaultRow row) ShowItem(row);
    }

    // ------------------------------------------------------------------ 预览

    private void ClearPreview()
    {
        _playback?.Dispose();
        _playback = null;
        _selected = null;
        ImgPreview.Source = null;
        TxtPreviewHint.Visibility = Visibility.Visible;
        TxtPreviewHint.Text = "从左侧选择一条内容";
        Transport.Visibility = Visibility.Collapsed;
        TxtInfo.Text = "";
        TxtTitle.Text = "";
    }

    private void ShowItem(VaultRow row)
    {
        _lastActivity = DateTime.UtcNow;
        _playback?.Dispose();
        _playback = null;
        _selected = row;
        var info = row.Info;

        TxtTitle.Text = info.Title ?? "";
        TxtPreviewHint.Visibility = Visibility.Collapsed;
        TxtInfo.Text =
            $"类型：{(info.Kind == VaultItemKind.Recording ? "录像" : "截图")}　" +
            $"开始：{info.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}　" +
            (info.Kind == VaultItemKind.Recording
                ? $"时长：{info.Duration.TotalSeconds:F1}s　帧数：{info.FrameCount}　帧率：{info.Fps}fps　"
                : "") +
            $"分辨率：{info.Width}×{info.Height}　大小：{info.Bytes / 1024}KB" +
            (info.Completed ? "" : "　⚠ 未完成（录制被中断，可播放已录部分）");

        var reader = _library.OpenItem(info.ItemId, out string? error);
        if (reader is null)
        {
            ImgPreview.Source = null;
            TxtPreviewHint.Visibility = Visibility.Visible;
            TxtPreviewHint.Text = "无法打开这条内容：" + error;
            Transport.Visibility = Visibility.Collapsed;
            return;
        }

        _playback = new VaultPlayback(reader, Dispatcher);
        _playback.FrameReady += (image, _) => ImgPreview.Source = image;
        _playback.Progress += (frame, total) =>
        {
            _suppressSeek = true;
            SldProgress.Maximum = Math.Max(1, total - 1);
            SldProgress.Value = frame;
            _suppressSeek = false;
            TxtFrame.Text = $"{frame + 1}/{total}";
        };
        _playback.Error += message => TxtStatus.Text = message;

        bool isRecording = info.Kind == VaultItemKind.Recording && info.FrameCount > 1;
        Transport.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
        BtnPlay.Content = "\uE768";
        _playback.RenderOnce(0);
    }

    // ------------------------------------------------------------------ 播放控制

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_playback is null || _playback.FrameCount <= 1) return;
        _lastActivity = DateTime.UtcNow;
        _playback.TogglePlay();
        BtnPlay.Content = _playback.IsPlaying ? "\uE769" : "\uE768";
    }

    private void SldProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSeek || _playback is null) return;
        TxtFrame.Text = $"{(int)e.NewValue + 1}/{_playback.FrameCount}";
    }

    private void SldProgress_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_playback is null) return;
        _lastActivity = DateTime.UtcNow;
        _playback.Seek((int)SldProgress.Value);
    }

    private void BtnSpeed_Click(object sender, RoutedEventArgs e)
    {
        _speed = _speed switch { 1.0 => 1.5, 1.5 => 2.0, 2.0 => 0.5, _ => 1.0 };
        if (_playback is not null) _playback.Speed = _speed;
        BtnSpeed.Content = $"{_speed:0.0}×";
    }

    private void BtnFullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool on)
    {
        if (_fullscreen == on) return;
        _fullscreen = on;
        if (on)
        {
            _prevState = WindowState;
            _prevBounds = new Rect(Left, Top, Width, Height);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            Header.Visibility = Visibility.Collapsed;
            Footer.Visibility = Visibility.Collapsed;
            ListCard.Visibility = Visibility.Collapsed;
            ColList.Width = new GridLength(0);
            RowHeader.Height = new GridLength(0);
            RowFooter.Height = new GridLength(0);
            Body.Margin = new Thickness(0);
        }
        else
        {
            WindowStyle = WindowStyle.None;   // 本程序一律无边框
            ResizeMode = ResizeMode.CanResize;
            Header.Visibility = Visibility.Visible;
            Footer.Visibility = Visibility.Visible;
            ListCard.Visibility = Visibility.Visible;
            ColList.Width = new GridLength(304);
            RowHeader.Height = new GridLength(46);
            RowFooter.Height = new GridLength(30);
            Body.Margin = new Thickness(16, 4, 16, 8);
            Left = _prevBounds.Left;
            Top = _prevBounds.Top;
            Width = _prevBounds.Width;
            Height = _prevBounds.Height;
            WindowState = _prevState;
        }
    }

    // ------------------------------------------------------------------ 条目操作

    private void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (!_library.SetTitle(_selected.Info.ItemId, TxtTitle.Text, out string? error))
        {
            TxtStatus.Text = "改名失败：" + error;
            return;
        }
        _lastActivity = DateTime.UtcNow;
        ReloadRows();
        TxtStatus.Text = "标题已保存（只重写了加密头，内容未变）";
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_selected is null) return;
        var info = _selected.Info;
        var answer = MessageBox.Show(this,
            $"删除这条{VaultKindText(info.Kind)}？\n\n{info.DisplayName}\n\n删除后无法恢复。",
            "删除内容", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        // 关键：先释放播放器（它会一直开着这个文件），否则文件被占用会删除失败
        _playback?.Dispose();
        _playback = null;

        if (!_library.Delete(info.ItemId, out string? error))
        {
            TxtStatus.Text = error ?? "删除失败";
            ShowItem(_selected);      // 恢复预览
            return;
        }
        _lastActivity = DateTime.UtcNow;
        ClearPreview();
        ReloadRows();
        TxtStatus.Text = "已删除 1 条　·　" + BuildStatus();
    }

    private static string VaultKindText(VaultItemKind kind) => kind == VaultItemKind.Recording ? "录像" : "截图";

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (_library.Items.Count == 0) { TxtStatus.Text = "库里没有内容"; return; }
        var first = MessageBox.Show(this,
            $"将清空当前账号（{TxtAccount.Text}）的全部 {_library.Items.Count} 条加密内容，且无法恢复。\n\n继续吗？",
            "清空库", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (first != MessageBoxResult.Yes) return;
        var second = MessageBox.Show(this, "最后确认：确定要清空吗？", "清空库",
            MessageBoxButton.YesNo, MessageBoxImage.Stop);
        if (second != MessageBoxResult.Yes) return;

        // 同样先释放播放器，避免当前条目被占用
        _playback?.Dispose();
        _playback = null;

        int removed = _library.DeleteAll();
        ClearPreview();
        ReloadRows();
        TxtStatus.Text = (removed == 0 ? "没有删除任何内容（可能被占用，请重试）" : $"已清空 {removed} 条") + "　·　" + BuildStatus();
    }

    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var account = _vault.Accounts.FirstOrDefault(a => a.Id == _library.AccountId);
        if (account is null) return;

        int cleaned = LegacyMigration.CleanupTempFrameDirectories();
        var files = LegacyMigration.Find();
        if (files.Count == 0)
        {
            TxtStatus.Text = cleaned > 0
                ? $"没有发现旧明文文件；已清理 {cleaned} 个旧的明文临时帧目录"
                : "没有发现旧版本的明文截图/录像";
            return;
        }

        long total = files.Sum(f => f.Bytes);
        var answer = MessageBox.Show(this,
            $"发现旧版本留下的明文文件 {files.Count} 个（共 {total / 1024.0 / 1024:F1} MB），保存在图片/视频文件夹里。\n\n" +
            "导入后它们会变成加密条目，并删除原来的明文文件。\n\n现在导入吗？",
            "导入旧明文文件", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        BtnImport.IsEnabled = false;
        var progress = new Progress<string>(message => TxtStatus.Text = message);
        try
        {
            var (imported, failed, error) = await LegacyMigration.ImportAsync(account, progress, deleteSource: true, CancellationToken.None);
            ReloadRows();
            TxtStatus.Text = $"导入完成：成功 {imported}，失败 {failed}" +
                             (error is null ? "" : $"（{error}）") +
                             (cleaned > 0 ? $"；已清理 {cleaned} 个旧明文临时目录" : "");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "导入失败：" + ex.Message;
        }
        finally { BtnImport.IsEnabled = true; }
    }

    private void BtnLock_Click(object sender, RoutedEventArgs e) => LockAndClose();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void LockAndClose()
    {
        _locked = true;
        _vault.Lock();
        Close();
    }

    // ------------------------------------------------------------------ 保留策略与空闲上锁

    private void ApplyRetentionOnOpen()
    {
        try
        {
            string policy = _vault.Host.Settings.VaultRetention;
            TimeSpan? age = policy switch
            {
                "Days30" => TimeSpan.FromDays(30),
                "Days90" => TimeSpan.FromDays(90),
                _ => null,
            };
            long? quota = _vault.Host.Settings.VaultQuotaMB > 0
                ? _vault.Host.Settings.VaultQuotaMB * 1024L * 1024L
                : null;
            int removed = _library.ApplyRetention(age, quota);
            if (removed > 0) _vault.Host.Log.Info("vault", $"按保留策略清理了 {removed} 条");
        }
        catch { }
    }

    private void CheckIdleLock()
    {
        int minutes = _vault.Host.Settings.LibraryIdleLockMinutes;
        if (minutes <= 0) return;
        if (_playback?.IsPlaying == true) { _lastActivity = DateTime.UtcNow; return; }
        if ((DateTime.UtcNow - _lastActivity).TotalMinutes < minutes) return;
        _vault.Host.Log.Info("vault", $"库空闲超过 {minutes} 分钟，自动上锁");
        LockAndClose();
    }

    // ------------------------------------------------------------------ 键盘与生命周期

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _lastActivity = DateTime.UtcNow;
        switch (e.Key)
        {
            case Key.Escape:
                if (_fullscreen) SetFullscreen(false);
                else Close();
                e.Handled = true;
                break;
            case Key.Space:
                TogglePlay();
                e.Handled = true;
                break;
            case Key.Left:
                _playback?.Step(-1);
                e.Handled = true;
                break;
            case Key.Right:
                _playback?.Step(1);
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteSelected();
                e.Handled = true;
                break;
        }
    }

    private void Cleanup()
    {
        _idleTimer.Stop();
        try { _playback?.Dispose(); } catch { }
        _playback = null;
        if (!_locked)
        {
            _locked = true;
            _vault.Lock();      // 关闭窗口 = 上锁
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_fullscreen) SetFullscreen(false);
        base.OnClosing(e);
    }
}
