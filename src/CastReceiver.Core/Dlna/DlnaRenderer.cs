using System.Net;
using System.Text;
using System.Xml.Linq;
using CastReceiver.Core.Http;
using CastReceiver.Core.Infrastructure;
using CastReceiver.Core.Media;

namespace CastReceiver.Core.Dlna;

/// <summary>
/// DLNA MediaRenderer（DMR）接收端：SSDP 发现 + 设备描述 + AVTransport/RenderingControl/ConnectionManager 控制 + GENA 事件。
/// 腾讯视频、爱奇艺、优酷、B站等 App 的“投屏”按钮走的就是这套标准协议（DLNA 路线）。
/// </summary>
public sealed class DlnaRenderer : IDisposable
{
    private readonly CastSettings _settings;
    private readonly IMediaPlayerEngine _player;
    private readonly AppLog _log;
    private readonly MediaProxy _proxy;
    private readonly GenaSubscriptionManager _gena;

    private MiniHttpServer? _http;
    private SsdpServer? _ssdp;

    // AVTransport 状态
    private string _avtUri = "";
    private string _avtMetadata = "";
    private string _nextUri = "";
    private string _nextMetadata = "";
    private string _transportState = "NO_MEDIA_PRESENT";
    private string _currentTitle = "";
    private bool _proxyFallbackTried;
    private bool _pauseRequested;

    public DlnaRenderer(CastSettings settings, IMediaPlayerEngine player, AppLog log)
    {
        _settings = settings;
        _player = player;
        _log = log;
        _proxy = new MediaProxy(log);
        _gena = new GenaSubscriptionManager(log);
        _player.StateChanged += (_, _) => OnPlayerStateChanged();
    }

    public bool IsRunning => _http?.IsRunning == true;
    public int HttpPort => _http?.Port ?? _settings.HttpPort;
    /// <summary>UPnP AVTransport 的当前传输状态字符串（PLAYING / PAUSED_PLAYBACK / STOPPED …）。</summary>
    public string UpnpTransportState => _transportState;
    public string CurrentTitle => _currentTitle;
    public string CurrentUri => _avtUri;
    public int ActiveSubscriptions => _gena.ActiveCount;
    public SsdpServer? Ssdp => _ssdp;

    public string BaseUrlFor(IPAddress ip) => $"http://{ip}:{HttpPort}";

    public event Action<string>? Notice;

    /// <summary>最近一次收到控制点（手机）活动的时刻：SSDP 搜索 / 抓取设备描述 / 下发投屏指令。
    /// 界面用它来显示“手机正在连接…”的动画反馈。</summary>
    public DateTime LastControlPointActivityUtc { get; private set; } = DateTime.MinValue;

    public bool HasRecentControlPointActivity(TimeSpan window)
        => DateTime.UtcNow - LastControlPointActivityUtc < window;

    // ---------------- 控制点（手机）活动信息：界面上据此确认“手机到底有没有找到我们” ----------------

    /// <summary>最近活动的控制点 IP（手机）。</summary>
    public string LastControlPointIp { get; private set; } = "";

    /// <summary>最近抓取设备描述/SCPD 的客户端 User-Agent（可区分是哪个 App 在识别本机）。</summary>
    public string LastControlPointUserAgent { get; private set; } = "";

    /// <summary>手机侧的搜索累计次数（非本机来源）。</summary>
    public int RemoteSearchCount { get; private set; }

    /// <summary>最近一次手机搜索的时间。</summary>
    public DateTime LastRemoteSearchUtc { get; private set; } = DateTime.MinValue;

    /// <summary>是否已经有控制点抓取过本机的设备描述（= 真正识别到了本机，不只是搜到）。</summary>
    public bool LastControlPointFetchedDescription { get; private set; }

    /// <summary>手动重新广播 SSDP（手机投屏列表里看不到本机时使用）。</summary>
    public void RebroadcastDiscovery() => _ssdp?.Rebroadcast();

    private readonly Dictionary<string, DateTime> _httpNoticeThrottle = new();

    /// <summary>
    /// 记录“控制点抓取设备描述/能力描述”这一关键动作（含 User-Agent）。
    /// 用来判定某个 App 到底有没有发现本机：例如腾讯视频若真的识别到了本机，
    /// 这里就会出现它抓取 description.xml 的记录；没有记录说明它根本没走到这一步。
    /// </summary>
    private void LogControlPointFetch(HttpRequestEx request)
    {
        bool isDescription = request.Path.Equals(DlnaXml.DescriptionUrl, StringComparison.OrdinalIgnoreCase);
        bool isScpd = request.Path.EndsWith("scpd.xml", StringComparison.OrdinalIgnoreCase);
        if (!isDescription && !isScpd) return;

        string ip = request.Remote?.Address.ToString() ?? "?";
        string ua = request.Header("User-Agent") ?? "-";
        string key = $"{ip}|{ua}|{request.Path}";
        lock (_httpNoticeThrottle)
        {
            if (_httpNoticeThrottle.TryGetValue(key, out var last) && (DateTime.UtcNow - last).TotalSeconds < 20) return;
            _httpNoticeThrottle[key] = DateTime.UtcNow;
            if (_httpNoticeThrottle.Count > 100) _httpNoticeThrottle.Clear();
        }
        LastControlPointActivityUtc = DateTime.UtcNow;
        LastControlPointIp = ip;
        LastControlPointUserAgent = ua;
        if (isDescription) LastControlPointFetchedDescription = true;
        string kind = isDescription ? "设备描述" : "服务能力(SCPD)";
        _log.Info("dlna", $"控制点抓取{kind}：{ip} UA=\"{ua}\"");
        Notice?.Invoke($"控制点抓取{kind}：{ip}（{Shorten(ua)}）");
    }

    // ------------------------------------------------------------------ 生命周期

    public void Start()
    {
        if (IsRunning) return;
        _gena.NotifyAsync(string.Empty, string.Empty).GetAwaiter(); // 预热（无订阅时立即返回）

        _http = new MiniHttpServer(_settings.HttpPort, HandleRequestAsync, _log, "http");
        _http.Start();

        _ssdp = new SsdpServer(_settings.DeviceUuid, BaseUrlFor, _log);
        _ssdp.Notice += message => Notice?.Invoke(message);
        // 手机在搜索本机 = 正在准备连接，界面据此显示等待动画
        _ssdp.SearchReceived += (ip, _) =>
        {
            if (!_ssdp.IsLocalAddress(ip))
            {
                LastControlPointActivityUtc = DateTime.UtcNow;
                LastRemoteSearchUtc = DateTime.UtcNow;
                RemoteSearchCount++;
                if (LastControlPointIp.Length == 0) LastControlPointIp = ip.ToString();
            }
        };
        _ssdp.Start();
        _log.Info("dlna", $"DLNA 接收端已启动：设备名“{_settings.DeviceName}”，HTTP 端口 {HttpPort}");
        Notice?.Invoke("App 投屏已开启");
    }

    public void Stop()
    {
        try { _ssdp?.Stop(); } catch { }
        try { _http?.Stop(); } catch { }
        _ssdp = null;
        _http = null;
        _log.Info("dlna", "DLNA 接收端已停止");
        Notice?.Invoke("App 投屏已关闭");
    }

    // ------------------------------------------------------------------ HTTP 路由

    private async Task<HttpResponseEx> HandleRequestAsync(HttpRequestEx request, CancellationToken ct)
    {
        string path = request.Path;
        LogControlPointFetch(request);

        if (request.Method.Equals("SUBSCRIBE", StringComparison.OrdinalIgnoreCase))
        {
            string? service = ServiceOfEventPath(path);
            var subResponse = _gena.HandleSubscribe(service ?? "", request);
            if (subResponse.StatusCode == 200 && service is not null)
            {
                // UPnP 规范要求：订阅成功后必须立刻向该控制点推送一次当前状态（初始事件），
                // 否则控制点会认为设备不响应事件而放弃投屏。
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(150).ConfigureAwait(false);
                        if (service == "AVTransport") await NotifyAvtAsync().ConfigureAwait(false);
                        else if (service == "RenderingControl") await NotifyRcsAsync().ConfigureAwait(false);
                        _log.Info("gena", $"{service} 已推送初始事件");
                    }
                    catch (Exception ex) { _log.Warn("gena", $"初始事件推送失败：{ex.Message}"); }
                });
            }
            return subResponse;
        }
        if (request.Method.Equals("UNSUBSCRIBE", StringComparison.OrdinalIgnoreCase))
            return _gena.HandleUnsubscribe(request);

        switch (path)
        {
            case DlnaXml.DescriptionUrl:
                return HttpResponseEx.Xml(DlnaXml.DeviceDescription(_settings.DeviceUuid, _settings.DeviceName,
                    BaseUrlFor(NetInfo.GetLanInterface()?.Address ?? IPAddress.Loopback)))
                    .With("EXT", "").With("SERVER", SsdpServer.ServerHeader);

            case DlnaXml.ScpdUrlAvt: return HttpResponseEx.Xml(DlnaXml.AvTransportScpd()).With("EXT", "");
            case DlnaXml.ScpdUrlRcs: return HttpResponseEx.Xml(DlnaXml.RenderingControlScpd()).With("EXT", "");
            case DlnaXml.ScpdUrlCms: return HttpResponseEx.Xml(DlnaXml.ConnectionManagerScpd()).With("EXT", "");

            case DlnaXml.ControlUrlAvt: return await HandleControlAsync(request, "AVTransport", ct);
            case DlnaXml.ControlUrlRcs: return await HandleControlAsync(request, "RenderingControl", ct);
            case DlnaXml.ControlUrlCms: return await HandleControlAsync(request, "ConnectionManager", ct);

            case "/media": return await _proxy.HandleAsync(request, ct);
            case "/": case "/status": return HttpResponseEx.Text(StatusPage(), 200, "text/html; charset=utf-8");
            case "/favicon.ico": return new HttpResponseEx { StatusCode = 204, Body = Array.Empty<byte>() };
        }

        _log.Debug("http", $"未匹配路径 {request.Method} {path}");
        return HttpResponseEx.Status(404, "not found");
    }

    private static string? ServiceOfEventPath(string path) => path switch
    {
        DlnaXml.EventUrlAvt => "AVTransport",
        DlnaXml.EventUrlRcs => "RenderingControl",
        DlnaXml.EventUrlCms => "ConnectionManager",
        _ => null,
    };

    private string StatusPage()
    {
        var track = _player.Current;
        const string css = "body{font-family:system-ui,'Microsoft YaHei';margin:24px;background:#111;color:#eee}" +
                           "code{background:#222;padding:2px 6px;border-radius:4px}.k{color:#7fd}";
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(_settings.DeviceName)).Append("</title>");
        sb.Append("<style>").Append(css).Append("</style></head><body>");
        sb.Append("<h2>").Append(WebUtility.HtmlEncode(_settings.DeviceName)).Append(" · DLNA 接收端运行中</h2>");
        sb.Append("<p>设备类型：<code>urn:schemas-upnp-org:device:MediaRenderer:1</code>（DMR-1.50）</p>");
        sb.Append("<p>传输状态：<span class=\"k\">").Append(_transportState).Append("</span>　当前媒体：<span class=\"k\">")
          .Append(WebUtility.HtmlEncode(_currentTitle)).Append("</span></p>");
        sb.Append("<p>媒体地址：<code>").Append(WebUtility.HtmlEncode(_avtUri)).Append("</code></p>");
        sb.Append("<p>播放位置：").Append(_player.Position.ToString(@"hh\:mm\:ss")).Append(" / ")
          .Append(_player.Duration.ToString(@"hh\:mm\:ss")).Append("　音量：").Append(_player.Volume).Append("</p>");
        sb.Append("<p>事件订阅数：").Append(_gena.ActiveCount).Append("</p>");
        sb.Append("<p>在手机上的投屏列表里看到本名称即表示发现成功。</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ SOAP 控制

    private async Task<HttpResponseEx> HandleControlAsync(HttpRequestEx request, string service, CancellationToken ct)
    {
        string body = request.BodyText;
        var parsed = SoapRequest.Parse(body);
        if (parsed is null)
        {
            _log.Warn("soap", $"{service} 收到无法解析的 SOAP 请求");
            return SoapFault(402, "Invalid Args");
        }

        var (action, args) = parsed.Value;
        _log.Info("soap", $"{service}#{action} {Summarize(args)}");

        try
        {
            return service switch
            {
                "AVTransport" => await HandleAvTransportAsync(action, args, ct),
                "RenderingControl" => HandleRenderingControl(action, args),
                "ConnectionManager" => HandleConnectionManager(action, args),
                _ => SoapFault(401, "Invalid Action"),
            };
        }
        catch (Exception ex)
        {
            _log.Error("soap", $"{service}#{action} 处理异常", ex);
            return SoapFault(501, "Action Failed");
        }
    }

    private async Task<HttpResponseEx> HandleAvTransportAsync(string action, Dictionary<string, string> args, CancellationToken ct)
    {
        switch (action)
        {
            case "SetAVTransportURI":
            {
                string uri = args.GetValueOrDefault("CurrentURI", "").Trim();
                string metadata = args.GetValueOrDefault("CurrentURIMetaData", "");
                var didl = DidlLite.Parse(metadata);
                if (string.IsNullOrWhiteSpace(uri) && !string.IsNullOrWhiteSpace(didl.ResourceUrl))
                    uri = didl.ResourceUrl!;
                if (string.IsNullOrWhiteSpace(uri))
                    return SoapFault(402, "Invalid Args");

                LastControlPointActivityUtc = DateTime.UtcNow;
                _avtUri = uri;
                _avtMetadata = metadata;
                _currentTitle = didl.Title ?? uri;
                _proxyFallbackTried = false;
                _log.Info("dlna", $"收到投屏：{_currentTitle}  ← {Shorten(uri)}");
                Notice?.Invoke($"收到投屏：{_currentTitle}");

                await LoadAndStartAsync(uri, ct).ConfigureAwait(false);
                await NotifyAvtAsync().ConfigureAwait(false);
                return SoapResponse(DlnaXml.ServiceNsAvt, action, null);
            }

            case "SetNextAVTransportURI":
                _nextUri = args.GetValueOrDefault("NextURI", "");
                _nextMetadata = args.GetValueOrDefault("NextURIMetaData", "");
                return SoapResponse(DlnaXml.ServiceNsAvt, action, null);

            case "Play":
                if (string.IsNullOrWhiteSpace(_avtUri)) return SoapFault(701, "Transition not available");
                _pauseRequested = false;
                _player.Play();
                SetTransportState(_player.State == TransportState.Playing ? "PLAYING" : "TRANSITIONING");
                await NotifyAvtAsync().ConfigureAwait(false);
                return SoapResponse(DlnaXml.ServiceNsAvt, action, null);

            case "Pause":
                if (string.IsNullOrWhiteSpace(_avtUri)) return SoapFault(701, "Transition not available");
                _pauseRequested = true;
                _player.Pause();
                SetTransportState("PAUSED_PLAYBACK");
                await NotifyAvtAsync().ConfigureAwait(false);
                return SoapResponse(DlnaXml.ServiceNsAvt, action, null);

            case "Stop":
                _pauseRequested = false;
                _player.Stop();
                SetTransportState("STOPPED");
                await NotifyAvtAsync().ConfigureAwait(false);
                return SoapResponse(DlnaXml.ServiceNsAvt, action, null);

            case "Seek":
            {
                if (string.IsNullOrWhiteSpace(_avtUri)) return SoapFault(701, "Transition not available");
                string unit = args.GetValueOrDefault("Unit", "REL_TIME").Trim().ToUpperInvariant();
                string target = args.GetValueOrDefault("Target", "").Trim();
                if (unit is "REL_TIME" or "ABS_TIME")
                {
                    if (!TryParseUpnpTime(target, out var position)) return SoapFault(711, "Illegal seek target");
                    _player.Seek(position);
                    await NotifyAvtAsync().ConfigureAwait(false);
                    return SoapResponse(DlnaXml.ServiceNsAvt, action, null);
                }
                if (unit == "TRACK_NUM")
                    return SoapResponse(DlnaXml.ServiceNsAvt, action, null); // 单曲播放，忽略
                return SoapFault(710, "Seek mode not supported");
            }

            case "Next":
            case "Previous":
                if (string.IsNullOrWhiteSpace(_nextUri)) return SoapFault(701, "Transition not available");
                _avtUri = _nextUri;
                _avtMetadata = _nextMetadata;
                _nextUri = ""; _nextMetadata = "";
                await LoadAndStartAsync(_avtUri, ct).ConfigureAwait(false);
                return SoapResponse(DlnaXml.ServiceNsAvt, action, null);

            case "GetTransportInfo":
                return SoapResponse(DlnaXml.ServiceNsAvt, action, new Dictionary<string, string>
                {
                    ["CurrentTransportState"] = _transportState,
                    ["CurrentTransportStatus"] = "OK",
                    ["CurrentSpeed"] = "1",
                });

            case "GetMediaInfo":
            {
                var duration = FormatUpnpTime(_player.Duration);
                return SoapResponse(DlnaXml.ServiceNsAvt, action, new Dictionary<string, string>
                {
                    ["NrTracks"] = _avtUri.Length > 0 ? "1" : "0",
                    ["MediaDuration"] = duration,
                    ["CurrentURI"] = _avtUri,
                    ["CurrentURIMetaData"] = _avtMetadata,
                    ["NextURI"] = _nextUri,
                    ["NextURIMetaData"] = _nextMetadata,
                    ["PlayMedium"] = "NETWORK",
                    ["RecordMedium"] = "NOT_IMPLEMENTED",
                    ["WriteStatus"] = "NOT_IMPLEMENTED",
                });
            }

            case "GetPositionInfo":
                return SoapResponse(DlnaXml.ServiceNsAvt, action, new Dictionary<string, string>
                {
                    ["Track"] = "1",
                    ["TrackDuration"] = FormatUpnpTime(_player.Duration),
                    ["TrackMetaData"] = _avtMetadata,
                    ["TrackURI"] = _avtUri,
                    ["RelTime"] = FormatUpnpTime(_player.Position),
                    ["AbsTime"] = FormatUpnpTime(_player.Position),
                    ["RelCount"] = "2147483647",
                    ["AbsCount"] = "2147483647",
                });

            case "GetDeviceCapabilities":
                return SoapResponse(DlnaXml.ServiceNsAvt, action, new Dictionary<string, string>
                {
                    ["PlayMedia"] = "NETWORK,NONE",
                    ["RecMedia"] = "NOT_IMPLEMENTED",
                    ["RecQualityModes"] = "NOT_IMPLEMENTED",
                });

            case "GetTransportSettings":
                return SoapResponse(DlnaXml.ServiceNsAvt, action, new Dictionary<string, string>
                {
                    ["PlayMode"] = "NORMAL",
                    ["RecQualityMode"] = "NOT_IMPLEMENTED",
                });

            case "GetCurrentTransportActions":
                return SoapResponse(DlnaXml.ServiceNsAvt, action, new Dictionary<string, string>
                {
                    ["Actions"] = string.IsNullOrWhiteSpace(_avtUri) ? "Stop" : "Play,Stop,Pause,Seek",
                });

            default:
                _log.Warn("soap", $"AVTransport 未实现的动作：{action}");
                return SoapFault(401, "Invalid Action");
        }
    }

    private HttpResponseEx HandleRenderingControl(string action, Dictionary<string, string> args)
    {
        switch (action)
        {
            case "GetVolume":
                return SoapResponse(DlnaXml.ServiceNsRcs, action, new Dictionary<string, string> { ["CurrentVolume"] = _player.Volume.ToString() });

            case "SetVolume":
            {
                if (!int.TryParse(args.GetValueOrDefault("DesiredVolume", ""), out int volume))
                    return SoapFault(402, "Invalid Args");
                _player.SetVolume(volume);
                _ = NotifyRcsAsync();
                return SoapResponse(DlnaXml.ServiceNsRcs, action, null);
            }

            case "GetMute":
                return SoapResponse(DlnaXml.ServiceNsRcs, action, new Dictionary<string, string> { ["CurrentMute"] = _player.Muted ? "1" : "0" });

            case "SetMute":
                _player.SetMuted(args.GetValueOrDefault("DesiredMute", "0") is "1" or "true" or "True");
                _ = NotifyRcsAsync();
                return SoapResponse(DlnaXml.ServiceNsRcs, action, null);

            case "ListPresets":
                return SoapResponse(DlnaXml.ServiceNsRcs, action, new Dictionary<string, string> { ["CurrentPresetNameList"] = "FactoryDefaults" });

            case "SelectPreset":
                return SoapResponse(DlnaXml.ServiceNsRcs, action, null);

            case "GetVolumeDB":
                return SoapResponse(DlnaXml.ServiceNsRcs, action, new Dictionary<string, string> { ["CurrentVolume"] = "0" });

            case "GetVolumeDBRange":
                return SoapResponse(DlnaXml.ServiceNsRcs, action, new Dictionary<string, string> { ["MinValue"] = "-32768", ["MaxValue"] = "32767" });

            case "SetVolumeDB":
                return SoapResponse(DlnaXml.ServiceNsRcs, action, null);

            default:
                if (action.StartsWith("Get", StringComparison.Ordinal))
                {
                    // 亮度/对比度等：统一返回 0，避免控制点因错误而中断投屏
                    var outName = GuessOutName(action);
                    return SoapResponse(DlnaXml.ServiceNsRcs, action, new Dictionary<string, string> { [outName] = "0" });
                }
                if (action.StartsWith("Set", StringComparison.Ordinal))
                    return SoapResponse(DlnaXml.ServiceNsRcs, action, null);
                return SoapFault(401, "Invalid Action");
        }
    }

    private static string GuessOutName(string action) => action switch
    {
        "GetBrightness" => "CurrentBrightness",
        "GetContrast" => "CurrentContrast",
        "GetSharpness" => "CurrentSharpness",
        "GetColorTemperature" => "CurrentColorTemperature",
        "GetRedVideoGain" => "CurrentRedVideoGain",
        "GetGreenVideoGain" => "CurrentGreenVideoGain",
        "GetBlueVideoGain" => "CurrentBlueVideoGain",
        "GetRedVideoBlackLevel" => "CurrentRedVideoBlackLevel",
        "GetGreenVideoBlackLevel" => "CurrentGreenVideoBlackLevel",
        "GetBlueVideoBlackLevel" => "CurrentBlueVideoBlackLevel",
        "GetHorizontalKeystone" => "CurrentHorizontalKeystone",
        "GetVerticalKeystone" => "CurrentVerticalKeystone",
        _ => "CurrentValue",
    };

    private HttpResponseEx HandleConnectionManager(string action, Dictionary<string, string> args)
    {
        switch (action)
        {
            case "GetProtocolInfo":
                return SoapResponse(DlnaXml.ServiceNsCms, action, new Dictionary<string, string>
                {
                    ["Source"] = "",
                    ["Sink"] = DlnaXml.SinkProtocolInfo(),
                });

            case "GetCurrentConnectionIDs":
                return SoapResponse(DlnaXml.ServiceNsCms, action, new Dictionary<string, string> { ["ConnectionIDs"] = "0" });

            case "GetCurrentConnectionInfo":
                return SoapResponse(DlnaXml.ServiceNsCms, action, new Dictionary<string, string>
                {
                    ["RcsID"] = "0",
                    ["AVTransportID"] = "0",
                    ["ProtocolInfo"] = "http-get:*:*:*",
                    ["PeerConnectionManager"] = "",
                    ["PeerConnectionID"] = "-1",
                    ["Direction"] = "Input",
                    ["Status"] = "OK",
                });

            default:
                return SoapFault(401, "Invalid Action");
        }
    }

    // ------------------------------------------------------------------ 播放与事件

    private async Task LoadAndStartAsync(string uri, CancellationToken ct)
    {
        string playUri = uri;
        bool useProxy = _settings.ForceMediaProxy && !_proxy.IsProxyUrl(uri);
        if (useProxy)
        {
            var lan = NetInfo.GetLanInterface();
            if (lan is not null)
            {
                playUri = MediaProxy.BuildProxyUrl(BaseUrlFor(lan.Address), uri);
                _log.Info("dlna", $"按设置经本机代理播放：{Shorten(playUri)}");
            }
        }

        try
        {
            await _player.SetUriAsync(playUri, _avtMetadata, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("dlna", "装载媒体失败", ex);
        }
        _player.Play();
        SetTransportState("PLAYING");
    }

    private void OnPlayerStateChanged()
    {
        string state = _player.State switch
        {
            TransportState.Playing => "PLAYING",
            TransportState.Paused => "PAUSED_PLAYBACK",
            TransportState.Transitioning => _pauseRequested ? "PAUSED_PLAYBACK" : "TRANSITIONING",
            TransportState.Stopped => string.IsNullOrWhiteSpace(_avtUri) ? "NO_MEDIA_PRESENT" : "STOPPED",
            _ => _pauseRequested ? "PAUSED_PLAYBACK" : "NO_MEDIA_PRESENT",
        };
        if (state != _transportState)
        {
            SetTransportState(state);
            _ = NotifyAvtAsync();
        }

        // 播放失败自动降级：改用本机代理回源重试一次（应对 CDN 防盗链）
        if (_player.State == TransportState.Stopped && _player.LastError is not null
            && !_proxyFallbackTried && !string.IsNullOrWhiteSpace(_avtUri) && !_proxy.IsProxyUrl(_avtUri))
        {
            var lan = NetInfo.GetLanInterface();
            if (lan is not null)
            {
                _proxyFallbackTried = true;
                string proxied = MediaProxy.BuildProxyUrl(BaseUrlFor(lan.Address), _avtUri);
                _log.Warn("dlna", $"直连播放失败，改用本机代理重试：{_player.LastError}");
                Notice?.Invoke("直连播放失败，已改用本机代理重试");
                _ = _player.SetUriAsync(proxied, _avtMetadata);
                _player.Play();
            }
        }
    }

    private void SetTransportState(string state) => _transportState = state;

    private Task NotifyAvtAsync() => _gena.NotifyAsync("AVTransport", AvtPropertySet());

    private Task NotifyRcsAsync() => _gena.NotifyAsync("RenderingControl", RcsPropertySet());

    private string AvtPropertySet()
    {
        var evt = new StringBuilder();
        evt.Append("<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/AVT/\"><InstanceID val=\"0\">");
        evt.Append($"<TransportState val=\"{_transportState}\"/>");
        evt.Append("<TransportStatus val=\"OK\"/>");
        evt.Append("<PlaybackStorageMedium val=\"NETWORK\"/>");
        evt.Append("<RecordStorageMedium val=\"NOT_IMPLEMENTED\"/>");
        evt.Append("<PossiblePlaybackStorageMedia val=\"NETWORK,NONE\"/>");
        evt.Append("<PossibleRecordStorageMedia val=\"NOT_IMPLEMENTED\"/>");
        evt.Append("<CurrentPlayMode val=\"NORMAL\"/>");
        evt.Append("<TransportPlaySpeed val=\"1\"/>");
        evt.Append("<RecordMediumWriteStatus val=\"NOT_IMPLEMENTED\"/>");
        evt.Append("<CurrentRecordQualityMode val=\"NOT_IMPLEMENTED\"/>");
        evt.Append("<PossibleRecordQualityModes val=\"NOT_IMPLEMENTED\"/>");
        evt.Append($"<NumberOfTracks val=\"{(string.IsNullOrWhiteSpace(_avtUri) ? 0 : 1)}\"/>");
        evt.Append("<CurrentTrack val=\"1\"/>");
        evt.Append($"<CurrentTrackDuration val=\"{FormatUpnpTime(_player.Duration)}\"/>");
        evt.Append($"<CurrentMediaDuration val=\"{FormatUpnpTime(_player.Duration)}\"/>");
        evt.Append($"<CurrentTrackURI val=\"{DlnaXml.Escape(_avtUri)}\"/>");
        evt.Append($"<AVTransportURI val=\"{DlnaXml.Escape(_avtUri)}\"/>");
        evt.Append("<CurrentTrackMetaData val=\"\"/>");
        evt.Append($"<AVTransportURIMetaData val=\"{DlnaXml.Escape(_avtMetadata)}\"/>");
        evt.Append($"<NextAVTransportURI val=\"{DlnaXml.Escape(_nextUri)}\"/>");
        evt.Append("<NextAVTransportURIMetaData val=\"\"/>");
        evt.Append($"<RelativeTimePosition val=\"{FormatUpnpTime(_player.Position)}\"/>");
        evt.Append($"<AbsoluteTimePosition val=\"{FormatUpnpTime(_player.Position)}\"/>");
        evt.Append("<RelativeCounterPosition val=\"0\"/>");
        evt.Append("<AbsoluteCounterPosition val=\"0\"/>");
        evt.Append($"<CurrentTransportActions val=\"{(string.IsNullOrWhiteSpace(_avtUri) ? "Stop" : "Play,Stop,Pause,Seek")}\"/>");
        evt.Append("</InstanceID></Event>");
        return PropertySet(evt.ToString());
    }

    private string RcsPropertySet()
    {
        var evt = new StringBuilder();
        evt.Append("<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/RCS/\"><InstanceID val=\"0\">");
        evt.Append($"<Volume channel=\"Master\" val=\"{_player.Volume}\"/>");
        evt.Append($"<Mute channel=\"Master\" val=\"{(_player.Muted ? 1 : 0)}\"/>");
        evt.Append("<PresetNameList val=\"FactoryDefaults\"/>");
        evt.Append("</InstanceID></Event>");
        return PropertySet(evt.ToString());
    }

    private static string PropertySet(string lastChangeInner)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
           "<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">\n" +
           $"  <e:property><LastChange>{DlnaXml.Escape(lastChangeInner)}</LastChange></e:property>\n" +
           "</e:propertyset>\n";

    // ------------------------------------------------------------------ 工具

    public static string FormatUpnpTime(TimeSpan time)
    {
        if (time <= TimeSpan.Zero) return "0:00:00";
        int hours = (int)time.TotalHours;
        return $"{hours}:{time.Minutes:00}:{time.Seconds:00}";
    }

    public static bool TryParseUpnpTime(string text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        try
        {
            switch (parts.Length)
            {
                case 3:
                    result = new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), (int)double.Parse(parts[2]));
                    return true;
                case 2:
                    result = new TimeSpan(0, int.Parse(parts[0]), (int)double.Parse(parts[1]));
                    return true;
                case 1:
                    result = TimeSpan.FromSeconds(double.Parse(parts[0]));
                    return true;
                default: return false;
            }
        }
        catch { return false; }
    }

    private static string Shorten(string s) => s.Length <= 120 ? s : s[..120] + "…";

    private static string Summarize(Dictionary<string, string> args)
        => string.Join(", ", args.Where(kv => !kv.Key.Contains("MetaData", StringComparison.OrdinalIgnoreCase))
                                .Select(kv => $"{kv.Key}={Shorten(kv.Value)}"));

    private static HttpResponseEx SoapResponse(string serviceNs, string action, Dictionary<string, string>? outArgs)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
        sb.Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\n");
        sb.Append("  <s:Body>\n");
        sb.Append($"    <u:{action}Response xmlns:u=\"{serviceNs}\">\n");
        if (outArgs is not null)
            foreach (var (k, v) in outArgs)
                sb.Append($"      <{k}>{DlnaXml.Escape(v)}</{k}>\n");
        sb.Append($"    </u:{action}Response>\n");
        sb.Append("  </s:Body>\n");
        sb.Append("</s:Envelope>\n");
        return HttpResponseEx.Xml(sb.ToString()).With("EXT", "");
    }

    private static HttpResponseEx SoapFault(int errorCode, string description)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
        sb.Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\n");
        sb.Append("  <s:Body>\n");
        sb.Append("    <s:Fault>\n");
        sb.Append("      <faultcode>s:Client</faultcode>\n");
        sb.Append("      <faultstring>UPnPError</faultstring>\n");
        sb.Append("      <detail>\n");
        sb.Append("        <UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">\n");
        sb.Append($"          <errorCode>{errorCode}</errorCode>\n");
        sb.Append($"          <errorDescription>{DlnaXml.Escape(description)}</errorDescription>\n");
        sb.Append("        </UPnPError>\n");
        sb.Append("      </detail>\n");
        sb.Append("    </s:Fault>\n");
        sb.Append("  </s:Body>\n");
        sb.Append("</s:Envelope>\n");
        return HttpResponseEx.Xml(sb.ToString(), 500).With("EXT", "");
    }

    public void Dispose()
    {
        Stop();
        _gena.Dispose();
    }
}

/// <summary>SOAP 请求解析。</summary>
public static class SoapRequest
{
    public static (string Action, Dictionary<string, string> Args)? Parse(string body)
    {
        try
        {
            var doc = XDocument.Parse(body);
            var bodyEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Body");
            var actionEl = bodyEl?.Elements().FirstOrDefault();
            if (actionEl is null) return null;
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in actionEl.Elements())
                args[el.Name.LocalName] = el.Value;
            return (actionEl.Name.LocalName, args);
        }
        catch { return null; }
    }
}

/// <summary>DIDL-Lite 元数据解析（标题 / 媒体直链 / 时长 / 封面）。</summary>
public sealed record DidlInfo(string? Title, string? ResourceUrl, TimeSpan? Duration, string? AlbumArtUrl, string? Artist);

public static class DidlLite
{
    public static DidlInfo Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return new DidlInfo(null, null, null, null, null);
        try
        {
            var doc = XDocument.Parse(xml);
            string? title = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
            var res = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "res");
            string? url = res?.Value;
            string? durationAttr = res?.Attributes().FirstOrDefault(a => a.Name.LocalName == "duration")?.Value;
            TimeSpan? duration = null;
            if (durationAttr is not null && DlnaRenderer.TryParseUpnpTime(durationAttr, out var d)) duration = d;
            string? art = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "albumArtURI")?.Value;
            string? artist = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "artist")?.Value;
            return new DidlInfo(Trim(title), Trim(url), duration, Trim(art), Trim(artist));
        }
        catch { return new DidlInfo(null, null, null, null, null); }
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}