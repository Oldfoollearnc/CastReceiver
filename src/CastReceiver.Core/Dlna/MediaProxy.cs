using System.Net.Http.Headers;
using System.Text;
using CastReceiver.Core.Http;
using CastReceiver.Core.Infrastructure;

namespace CastReceiver.Core.Dlna;

/// <summary>
/// 媒体代理：把上游视频地址改写成 http://本机:端口/media?u=... ，由本机带 UA/Referer/Range 回源。
/// 国内视频 App 的 CDN 常有防盗链（校验 Referer/UA/IP 一致性），直接让播放器拉流可能 403，
/// 经本机代理转发可显著提高成功率；同时支持 Range 以支持拖动进度。
/// </summary>
public sealed class MediaProxy
{
    private readonly AppLog _log;
    private readonly HttpClient _client;

    public MediaProxy(AppLog log)
    {
        _log = log;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static string Encode(string url)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string? Decode(string token)
    {
        try
        {
            string s = token.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch { return null; }
    }

    public static string BuildProxyUrl(string baseUrl, string originalUrl)
        => $"{baseUrl.TrimEnd('/')}/media?u={Encode(originalUrl)}";

    public bool IsProxyUrl(string url) => url.Contains("/media?u=", StringComparison.OrdinalIgnoreCase);

    public async Task<HttpResponseEx> HandleAsync(HttpRequestEx request, CancellationToken ct)
    {
        string? token = request.QueryParam("u");
        string? target = token is null ? null : Decode(token);
        if (string.IsNullOrWhiteSpace(target) || !Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            _log.Warn("proxy", "代理请求缺少合法的 u 参数");
            return HttpResponseEx.Status(400, "bad url");
        }

        var upstream = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in new[] { "Range", "If-Range", "Accept", "Accept-Language" })
        {
            if (request.Header(header) is { Length: > 0 } value)
                upstream.Headers.TryAddWithoutValidation(header, value);
        }
        ApplyIdentityHeaders(upstream, uri);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("proxy", $"回源失败 {uri.Host}", ex);
            return HttpResponseEx.Status(502, "upstream error: " + ex.Message);
        }

        _log.Info("proxy", $"回源 {(int)response.StatusCode} {uri.Host}{uri.AbsolutePath} ({(response.Content.Headers.ContentLength?.ToString() ?? "?")} bytes)");

        var result = new HttpResponseEx
        {
            StatusCode = (int)response.StatusCode,
            ContentLength = response.Content.Headers.ContentLength,
        };
        string? contentType = response.Content.Headers.ContentType?.ToString();
        result.Headers["Content-Type"] = contentType ?? GuessContentType(uri);
        if (response.Content.Headers.ContentRange is { } range)
            result.Headers["Content-Range"] = range.ToString();
        result.Headers["Accept-Ranges"] = "bytes";
        result.Headers["transferMode.dlna.org"] = "Streaming";
        result.Headers["contentFeatures.dlna.org"] = "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        result.BodyWriter = async (output, token2) =>
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, token2).ConfigureAwait(false)) > 0)
                await output.WriteAsync(buffer.AsMemory(0, read), token2).ConfigureAwait(false);
        };
        result.CloseConnection = false;
        return result;
    }

    private void ApplyIdentityHeaders(HttpRequestMessage request, Uri uri)
    {
        // 防盗链常见校验：UA 必须像手机端播放器/浏览器，Referer 需为对应站点
        string ua = "Mozilla/5.0 (Linux; Android 13; SM-S9180) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
        string referer = $"https://{uri.Host}/";
        try
        {
            request.Headers.TryAddWithoutValidation("User-Agent", ua);
            request.Headers.TryAddWithoutValidation("Referer", referer);
            request.Headers.TryAddWithoutValidation("Origin", $"https://{uri.Host}");
        }
        catch { }
    }

    private static string GuessContentType(Uri uri)
    {
        string path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".m3u8")) return "application/vnd.apple.mpegurl";
        if (path.EndsWith(".ts")) return "video/mp2t";
        if (path.EndsWith(".mp4") || path.EndsWith(".m4v")) return "video/mp4";
        if (path.EndsWith(".flv")) return "video/x-flv";
        if (path.EndsWith(".mp3")) return "audio/mpeg";
        if (path.EndsWith(".m4a")) return "audio/mp4";
        return "application/octet-stream";
    }
}