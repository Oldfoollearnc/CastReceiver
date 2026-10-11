using System.Net.Http;
using System.Text;
using CastReceiver.Core.Http;
using CastReceiver.Core.Infrastructure;

namespace CastReceiver.Core.Dlna;

/// <summary>GENA 事件订阅管理：处理 SUBSCRIBE/UNSUBSCRIBE 并向控制点回调 NOTIFY。</summary>
public sealed class GenaSubscriptionManager : IDisposable
{
    private sealed class Subscription
    {
        public string Sid = "";
        public string Service = "";
        public List<string> Callbacks = new();
        public DateTime ExpiresUtc = DateTime.UtcNow;
        public int Sequence;
    }

    private readonly AppLog _log;
    private readonly HttpClient _client;
    private readonly Dictionary<string, Subscription> _bySid = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public GenaSubscriptionManager(AppLog log)
    {
        _log = log;
        _client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public int ActiveCount
    {
        get { lock (_gate) { Prune(); return _bySid.Count; } }
    }

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var sid in _bySid.Where(kv => kv.Value.ExpiresUtc <= now).Select(kv => kv.Key).ToList())
            _bySid.Remove(sid);
    }

    /// <summary>处理 SUBSCRIBE：首次订阅返回新 SID，带 SID 视为续订。</summary>
    public HttpResponseEx HandleSubscribe(string service, HttpRequestEx request)
    {
        int requested = ParseTimeout(request.Header("TIMEOUT"));
        string? sid = request.Header("SID");

        if (sid is not null)
        {
            lock (_gate)
            {
                Prune();
                if (!_bySid.TryGetValue(sid, out var existing))
                    return HttpResponseEx.Status(412, "unknown SID").With("SERVER", SsdpServer.ServerHeader);
                existing.ExpiresUtc = DateTime.UtcNow.AddSeconds(requested);
                _log.Debug("gena", $"{service} 续订 SID={sid} ({requested}s)");
                return new HttpResponseEx { StatusCode = 200, Body = Array.Empty<byte>() }
                    .With("SID", sid).With("TIMEOUT", $"Second-{requested}").With("SERVER", SsdpServer.ServerHeader);
            }
        }

        string? callbackHeader = request.Header("CALLBACK");
        if (string.IsNullOrWhiteSpace(callbackHeader))
            return HttpResponseEx.Status(412, "missing CALLBACK").With("SERVER", SsdpServer.ServerHeader);

        var callbacks = ParseCallbacks(callbackHeader);
        if (callbacks.Count == 0)
            return HttpResponseEx.Status(412, "bad CALLBACK").With("SERVER", SsdpServer.ServerHeader);

        var sub = new Subscription
        {
            Sid = "uuid:" + Guid.NewGuid().ToString("D"),
            Service = service,
            Callbacks = callbacks,
            ExpiresUtc = DateTime.UtcNow.AddSeconds(requested),
        };
        lock (_gate)
        {
            Prune();
            _bySid[sub.Sid] = sub;
        }
        _log.Info("gena", $"{service} 新增订阅 SID={sub.Sid} → {string.Join(",", callbacks)} ({requested}s)");

        return new HttpResponseEx { StatusCode = 200, Body = Array.Empty<byte>() }
            .With("SID", sub.Sid).With("TIMEOUT", $"Second-{requested}").With("SERVER", SsdpServer.ServerHeader);
    }

    public HttpResponseEx HandleUnsubscribe(HttpRequestEx request)
    {
        string? sid = request.Header("SID");
        if (sid is null) return HttpResponseEx.Status(412, "missing SID");
        lock (_gate) { Prune(); _bySid.Remove(sid); }
        _log.Info("gena", $"取消订阅 SID={sid}");
        return new HttpResponseEx { StatusCode = 200, Body = Array.Empty<byte>() };
    }

    /// <summary>向所有订阅了该服务的控制点推送事件。</summary>
    public async Task NotifyAsync(string service, string propertysetXml, CancellationToken ct = default)
    {
        List<Subscription> targets;
        lock (_gate)
        {
            Prune();
            targets = _bySid.Values.Where(s => s.Service.Equals(service, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (targets.Count == 0) return;

        byte[] body = Encoding.UTF8.GetBytes(propertysetXml);
        foreach (var sub in targets)
        {
            int seq = ++sub.Sequence;
            foreach (var callback in sub.Callbacks)
            {
                if (await SendNotifyAsync(callback, sub.Sid, seq, body, ct).ConfigureAwait(false))
                {
                    _log.Debug("gena", $"NOTIFY {service} seq={seq} → {callback} 已送达");
                    continue;
                }
                // 失败重试一次：HTTP 长连接可能刚被对端关闭，重发即可（否则控制点会认为设备不响应事件）
                await Task.Delay(400, ct).ConfigureAwait(false);
                if (await SendNotifyAsync(callback, sub.Sid, seq, body, ct).ConfigureAwait(false))
                    _log.Info("gena", $"NOTIFY {service} seq={seq} → {callback} 重试后送达");
                else
                    _log.Warn("gena", $"NOTIFY {service} seq={seq} → {callback} 两次均失败");
            }
        }
    }

    private async Task<bool> SendNotifyAsync(string callback, string sid, int seq, byte[] body, CancellationToken ct)
    {
        try
        {
            using var msg = new HttpRequestMessage(new HttpMethod("NOTIFY"), callback);
            msg.Headers.TryAddWithoutValidation("NT", "upnp:event");
            msg.Headers.TryAddWithoutValidation("NTS", "upnp:propchange");
            msg.Headers.TryAddWithoutValidation("SID", sid);
            msg.Headers.TryAddWithoutValidation("SEQ", seq.ToString());
            msg.Content = new ByteArrayContent(body);
            msg.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");
            using var resp = await _client.SendAsync(msg, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.Debug("gena", $"NOTIFY 发送异常 → {callback}：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    private static int ParseTimeout(string? header)
    {
        if (header is null) return 1800;
        if (header.Contains("Second-", StringComparison.OrdinalIgnoreCase))
        {
            string num = header.Split('-').Last().Trim();
            if (int.TryParse(num, out int seconds)) return Math.Clamp(seconds, 60, 1800);
        }
        return 1800; // infinite 或无法解析时给一个安全值
    }

    private static List<string> ParseCallbacks(string header)
    {
        var list = new List<string>();
        int idx = 0;
        while (idx < header.Length)
        {
            int start = header.IndexOf('<', idx);
            if (start < 0) break;
            int end = header.IndexOf('>', start);
            if (end < 0) break;
            string url = header[(start + 1)..end].Trim();
            if (Uri.TryCreate(url, UriKind.Absolute, out _)) list.Add(url);
            idx = end + 1;
        }
        return list;
    }

    public void Dispose() => _client.Dispose();
}