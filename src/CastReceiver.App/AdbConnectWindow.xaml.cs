using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CastReceiver.Core.Android;
using CastReceiver.Core.Infrastructure;

namespace CastReceiver.App;

/// <summary>
/// 「电脑控制手机」的连接窗口：
/// 两条路 —— **数据线**（最省事：不用配对、不用填地址）与**无线调试**（mDNS 自动发现，首次要一次配对码）。
/// 已连上就显示是哪台手机、什么系统、以及**中文能不能发**。
///
/// 刻意把 **adb 的原始输出**摆在窗口里：配对失败的原因（端口写错、配对码过期、手机端弹窗没点）
/// 只有原始输出说得清，用一句"失败"糊过去等于让用户没法自救。
/// </summary>
public partial class AdbConnectWindow : Window
{
    private readonly AdbWirelessSession _session;
    private readonly CastSettings _settings;

    public AdbConnectWindow(AdbWirelessSession session, CastSettings settings)
    {
        InitializeComponent();
        _session = session;
        _settings = settings;

        TxtHost.Text = settings.AdbHost;
        TxtConnectPort.Text = settings.AdbConnectPort;
        TxtPairPort.Text = settings.AdbPairPort;
        // 刚才自动发现过的那次结果直接拿来用：打开窗口时端口就是填好的，用户只需要去手机上点一下
        if (session.LastPairingService is { } pairing)
        {
            if (TxtHost.Text.Length == 0) TxtHost.Text = pairing.Host;
            TxtPairPort.Text = pairing.Port.ToString(CultureInfo.InvariantCulture);
            TxtInputHint.Text = "首次配对（端口已自动填好，只差手机上的 6 位配对码）";
        }

        _session.Changed += OnSessionChanged;
        Loaded += (_, _) =>
        {
            RefreshUi();
            _ = FillFromDiscoveryAsync();      // 打开窗口就先自动发现一次，省得用户找端口
        };
        Closed += (_, _) => _session.Changed -= OnSessionChanged;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>
    /// 主界面在"发现手机在等配对"时会把本窗口直接打开：这时把焦点放到配对码输入框里，
    /// 用户从手机上抄 6 位数字过来就能直接敲，不用再点一次输入框。
    /// </summary>
    public void FocusPairingCode()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                TxtPairCode.Focus();
                TxtPairCode.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch { }
    }

    /// <summary>自动发现同网段的手机，把 IP 与两个端口填好（找不到就如实说）。</summary>
    private async Task FillFromDiscoveryAsync(bool connectIfPossible = false)
    {
        try
        {
            IReadOnlyList<AdbMdnsService> found = await _session.DiscoverAsync();
            ApplyDiscovery(found);
            if (connectIfPossible && found.Count > 0)
            {
                await _session.AutoConnectAsync();
            }
        }
        catch (Exception ex)
        {
            TxtState.Text = "自动发现失败：" + ex.Message;
        }
        finally
        {
            RefreshUi();
        }
    }

    private void ApplyDiscovery(IReadOnlyList<AdbMdnsService> found)
    {
        if (found.Count == 0)
        {
            TxtState.Text = "没发现开了无线调试的手机。请确认：手机已打开「无线调试」、与本机在同一网络（也可以手动填 IP 与端口）";
            return;
        }

        AdbMdnsService? pair = found.FirstOrDefault(s => s.IsPairing);
        AdbMdnsService? connect = found.FirstOrDefault(s => s.IsConnect) ?? found.FirstOrDefault();
        if (connect is not null)
        {
            TxtHost.Text = connect.Host;
            TxtConnectPort.Text = connect.Port.ToString(CultureInfo.InvariantCulture);
        }
        if (pair is not null)
        {
            TxtPairPort.Text = pair.Port.ToString(CultureInfo.InvariantCulture);
        }

        TxtState.Text = pair is not null
            ? $"发现 {found.Count} 个服务：IP 与端口已自动填好，把手机上的 6 位配对码填进来即可（只需配对这一次）"
            : $"发现 {found.Count} 个服务：已自动填好 IP 与端口";
    }

    private void OnSessionChanged() => Dispatcher.BeginInvoke(RefreshUi);

    private void RefreshUi()
    {
        string dot = _session.State switch
        {
            AdbLinkState.Connected => "B.Ok",
            AdbLinkState.Unsupported => "B.Bad",
            AdbLinkState.Failed => "B.Bad",
            AdbLinkState.Pairing or AdbLinkState.Connecting => "B.Warn",
            _ => "B.TextFaint",
        };
        DotState.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);
        TxtState.Text = _session.StatusText;

        if (_session.Device is { } device)
        {
            TxtDevice.Text = $"{device.Serial} · {device.VersionText}" +
                             (device.Screen is { } screen2
                                 ? $" · 屏幕 {screen2.CurrentWidth}×{screen2.CurrentHeight}（旋转 {screen2.Rotation * 90}°" +
                                   (screen2.HasMeasuredLogicalSize ? "，实测" : "，按旋转推算") + "）"
                                 : " · 屏幕尺寸未知");
            TxtDevice.Text += _session.IsUsbConnection ? "\n连接方式：adb 设备列表里的有线设备" : "\n连接方式：无线调试（mDNS 自动发现）";
            // 中文能不能发是用户最先会问的：连上后就把结论写清楚
            TxtDevice.Text += _session.AdbKeyboardInstalled
                ? "\n中文输入：可用（手机装了 ADBKeyboard）"
                : "\n中文输入：只能发英文数字（手机没装 ADBKeyboard；中文可用手机自己的输入法打）";
            // 老系统没有「无线调试」（Android 11 才有）：这条路走不通就说清楚，别让人反复重试
            if (!device.SupportsWirelessPairing)
            {
                TxtDevice.Text += "\n该机型是 Android 11 以下，没有「无线调试」：需要在电脑上先用数据线执行一次 " +
                                  "adb tcpip 5555 打开网络调试口，之后才走无线。";
            }
        }
        else
        {
            TxtDevice.Text = _session.PairingSupported == false
                ? "这台手机不支持无线调试配对（需要 Android 11 及以上）"
                : "还没有连接手机";
        }

        TxtAdbPath.Text = _session.Client is null
            ? "adb：未找到"
            : "adb：" + _session.Client.ExecutablePath + (_session.Client.IsBundled ? "（程序自带）" : "（来自系统）");

        TxtOutput.Text = _session.LastOutput.Length > 0 ? _session.LastOutput : "（还没有执行过命令）";
        BtnDisconnect.IsEnabled = _session.Serial is not null;
    }

    private (string Host, int ConnectPort, int PairPort) ReadInputs()
    {
        string host = TxtHost.Text.Trim();
        int.TryParse(TxtConnectPort.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int connectPort);
        int.TryParse(TxtPairPort.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pairPort);
        return (host, connectPort, pairPort);
    }

    private void RememberInputs()
    {
        (string host, int connectPort, int pairPort) = ReadInputs();
        _settings.AdbHost = host;
        if (connectPort > 0) _settings.AdbConnectPort = connectPort.ToString(CultureInfo.InvariantCulture);
        if (pairPort > 0) _settings.AdbPairPort = pairPort.ToString(CultureInfo.InvariantCulture);
        _settings.Save();
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        (string host, int connectPort, _) = ReadInputs();
        if (host.Length == 0 || connectPort <= 0)
        {
            TxtState.Text = "请先填手机 IP 与连接端口（手机无线调试界面里的「IP 地址和端口」）";
            return;
        }

        BtnConnect.IsEnabled = false;
        try
        {
            RememberInputs();
            await _session.ConnectAsync(host, connectPort);
        }
        finally
        {
            BtnConnect.IsEnabled = true;
            RefreshUi();
        }
    }

    private async void BtnPair_Click(object sender, RoutedEventArgs e)
    {
        (string host, int connectPort, int pairPort) = ReadInputs();
        string code = TxtPairCode.Text.Trim();
        if (host.Length == 0 || pairPort <= 0 || code.Length == 0)
        {
            TxtState.Text = "配对需要：手机 IP + 配对端口 + 6 位配对码（都在手机「使用配对码配对设备」那一页）";
            return;
        }

        BtnPair.IsEnabled = false;
        try
        {
            RememberInputs();
            await _session.PairAsync(host, pairPort, code);
            if (_session.PairingSupported == true)
            {
                // 配对成功要记住：以后就是"打开开关即连上"，不会再被引导回来配对
                _session.EverPaired = true;
                _settings.AdbPairedOnce = true;
                _settings.Save();
            }
            // 配对成功后如果连接端口也填了，顺手连上，省一步
            if (_session.State == AdbLinkState.Idle && connectPort > 0)
            {
                await _session.ConnectAsync(host, connectPort);
            }
        }
        finally
        {
            BtnPair.IsEnabled = true;
            RefreshUi();
        }
    }

    private async void BtnAuto_Click(object sender, RoutedEventArgs e)
    {
        BtnAuto.IsEnabled = false;
        try
        {
            TxtState.Text = "正在自动发现并连接…";
            await FillFromDiscoveryAsync(connectIfPossible: true);
            RememberInputs();
        }
        finally
        {
            BtnAuto.IsEnabled = true;
            RefreshUi();
        }
    }

    private async void BtnDiscover_Click(object sender, RoutedEventArgs e)
    {
        BtnDiscover.IsEnabled = false;
        try
        {
            TxtState.Text = "正在用 mDNS 找同网段上开了无线调试的手机…";
            await FillFromDiscoveryAsync();
        }
        finally
        {
            BtnDiscover.IsEnabled = true;
            RefreshUi();
        }
    }

    private async void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        BtnDisconnect.IsEnabled = false;
        try
        {
            await _session.DisconnectAsync();
        }
        finally
        {
            BtnDisconnect.IsEnabled = true;
            RefreshUi();
        }
    }

    private async void BtnDiagnose_Click(object sender, RoutedEventArgs e)
    {
        BtnDiagnose.IsEnabled = false;
        try
        {
            TxtState.Text = "正在收集诊断信息…";
            string report = await _session.BuildDiagnosticAsync();
            TxtOutput.Text = report;

            // 存一份到桌面：用户可以直接把这个文件发出来
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "投屏Window-安卓鼠标诊断.txt");
            try
            {
                System.IO.File.WriteAllText(path, report);
                TxtState.Text = "诊断信息已复制到剪贴板，并存到桌面：" + path;
            }
            catch
            {
                TxtState.Text = "诊断信息已复制到剪贴板（桌面文件没写成功）";
            }

            try { System.Windows.Clipboard.SetText(report); } catch { }
        }
        catch (Exception ex)
        {
            TxtState.Text = "收集诊断信息失败：" + ex.Message;
        }
        finally
        {
            BtnDiagnose.IsEnabled = true;
            RefreshUi();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
