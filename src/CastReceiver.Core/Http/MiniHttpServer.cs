using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CastReceiver.Core.Http;

/// <summary>极简 HTTP/1.1 请求模型。</summary>
public sealed class HttpRequestEx
{
    public string Method { get; init; } = "GET";
    public string RawTarget { get; init; } = "/";
    public string Path { get; init; } = "/";
    public string Query { get; init; } = "";
    public string Version { get; init; } = "HTTP/1.1";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public IPEndPoint? Remote { get; init; }
    public bool KeepAlive { get; init; } = true;

    public string BodyText => Encoding.UTF8.GetString(Body);

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public string? QueryParam(string name)
    {
        foreach (var pair in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = pair.IndexOf('=');
            string k = i < 0 ? pair : pair[..i];
            if (!k.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            string v = i < 0 ? "" : pair[(i + 1)..];
            return Uri.UnescapeDataString(v.Replace('+', ' '));
        }
        return null;
    }

    public override string ToString() => $"{Method} {RawTarget} from {Remote}";
}

/// <summary>极简 HTTP/1.1 响应模型。Body 或 BodyWriter 二选一。</summary>
public sealed class HttpResponseEx
{
    public int StatusCode { get; set; } = 200;
    public string? ReasonPhrase { get; set; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[]? Body { get; set; }

    /// <summary>流式响应体（用于大文件/媒体转发）；设置后 Body 被忽略。</summary>
    public Func<Stream, CancellationToken, Task>? BodyWriter { get; set; }

    /// <summary>流式响应时若已知长度请设置（支持 Range/断点）。未知则用 chunked。</summary>
    public long? ContentLength { get; set; }

    public bool CloseConnection { get; set; }

    public static HttpResponseEx Text(string text, int status = 200, string contentType = "text/plain; charset=utf-8")
        => new() { StatusCode = status, Body = Encoding.UTF8.GetBytes(text), Headers = { ["Content-Type"] = contentType } };

    public static HttpResponseEx Xml(string xml, int status = 200)
        => Text(xml, status, "text/xml; charset=\"utf-8\"");

    public static HttpResponseEx Status(int code, string? text = null)
        => Text(text ?? $"{(code == 404 ? "Not Found" : "Error")}", code);

    public HttpResponseEx With(string name, string value)
    {
        Headers[name] = value;
        return this;
    }
}

/// <summary>
/// 自研 HTTP/1.1 服务器（TcpListener 实现）。
/// 之所以不用 HttpListener：非管理员进程绑定 http://+:port/ 需要 netsh urlacl 提权，
/// 而投屏接收端必须以普通用户权限即可运行。
/// </summary>
public sealed class MiniHttpServer : IDisposable
{
    private readonly Func<HttpRequestEx, CancellationToken, Task<HttpResponseEx>> _handler;
    private readonly Infrastructure.AppLog _log;
    private readonly string _category;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public MiniHttpServer(int port, Func<HttpRequestEx, CancellationToken, Task<HttpResponseEx>> handler,
                          Infrastructure.AppLog log, string? category = null)
    {
        Port = port;
        _handler = handler;
        _log = log;
        _category = category ?? "http";
    }

    public int Port { get; private set; }
    public bool IsRunning => _listener is not null;

    public void Start(int? portOverride = null)
    {
        if (_listener is not null) return;
        int port = portOverride ?? Port;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        try { listener.Start(64); }
        catch (SocketException) when (port != 0)
        {
            // 端口被占用则退化为系统分配端口
            listener = new TcpListener(IPAddress.Any, 0);
            listener.Start(64);
            _log.Warn(_category, $"端口 {port} 被占用，改用系统分配端口");
        }
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listener = listener;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.Info(_category, $"内嵌 HTTP 服务已启动：0.0.0.0:{Port}");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _log.Info(_category, "内嵌 HTTP 服务已停止");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 15000;
            try
            {
                var stream = client.GetStream();
                var reader = new LineReader(stream);
                while (!ct.IsCancellationRequested)
                {
                    var request = await ReadRequestAsync(reader, stream, client, ct).ConfigureAwait(false);
                    if (request is null) break;

                    _log.Debug(_category, $"← {request.Method} {request.RawTarget} Range={request.Header("Range") ?? "-"} from {request.Remote}");

                    HttpResponseEx response;
                    try
                    {
                        response = await _handler(request, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Error(_category, $"处理 {request.Method} {request.Path} 异常", ex);
                        response = HttpResponseEx.Text("Internal Server Error", 500);
                    }

                    bool head = request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
                    bool keepAlive = request.KeepAlive && !response.CloseConnection;
                    try
                    {
                        await WriteResponseAsync(stream, response, head, keepAlive, ct).ConfigureAwait(false);
                        _log.Debug(_category, $"→ {response.StatusCode} {request.Path} len={response.ContentLength?.ToString() ?? "?"} keepAlive={keepAlive}");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(_category, $"响应写入失败（{request.Method} {request.Path}）：{ex.GetType().Name} {ex.Message}");
                        break;
                    }
                    if (!keepAlive) break;
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { _log.Debug(_category, $"连接处理结束：{ex.GetType().Name} {ex.Message}"); }
        }
    }

    private static async Task<HttpRequestEx?> ReadRequestAsync(LineReader reader, NetworkStream stream,
                                                               TcpClient client, CancellationToken ct)
    {
        string? requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (requestLine is null) return null;
        // 容忍请求之间的空行
        int guard = 0;
        while (string.IsNullOrWhiteSpace(requestLine) && guard++ < 8)
            requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine)) return null;

        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return null;
        string method = parts[0];
        string target = parts[1];
        string version = parts.Length > 2 ? parts[2] : "HTTP/1.1";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) break;
            int idx = line.IndexOf(':');
            if (idx <= 0) continue;
            headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        byte[] body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var clText) && long.TryParse(clText, out long cl) && cl > 0)
        {
            body = await reader.ReadBytesAsync((int)Math.Min(cl, 32 * 1024 * 1024), ct).ConfigureAwait(false);
        }
        else if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await reader.ReadChunkedAsync(ct).ConfigureAwait(false);
        }

        string path = target, query = "";
        int q = target.IndexOf('?');
        if (q >= 0) { path = target[..q]; query = target[(q + 1)..]; }

        bool keepAlive = version.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase)
            ? !headers.TryGetValue("Connection", out var cn) || !cn.Contains("close", StringComparison.OrdinalIgnoreCase)
            : headers.TryGetValue("Connection", out var cn2) && cn2.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);

        return new HttpRequestEx
        {
            Method = method, RawTarget = target, Path = Uri.UnescapeDataString(path), Query = query, Version = version,
            Headers = headers, Body = body, Remote = client.Client.RemoteEndPoint as IPEndPoint, KeepAlive = keepAlive,
        };
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpResponseEx response, bool headOnly,
                                                 bool keepAlive, CancellationToken ct)
    {
        string reason = response.ReasonPhrase ?? ReasonFor(response.StatusCode);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(reason).Append("\r\n");

        long? length = response.ContentLength;
        bool chunked = false;
        if (response.BodyWriter is not null)
        {
            if (length is null) { chunked = true; sb.Append("Transfer-Encoding: chunked\r\n"); }
        }
        else
        {
            length = response.Body?.LongLength ?? 0;
        }

        if (length is not null && !chunked) sb.Append("Content-Length: ").Append(length.Value).Append("\r\n");
        sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
        sb.Append("Date: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
        foreach (var (k, v) in response.Headers)
        {
            if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (k.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(k).Append(": ").Append(v).Append("\r\n");
        }
        sb.Append("\r\n");

        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head, ct).ConfigureAwait(false);

        if (headOnly) return;

        if (response.BodyWriter is not null)
        {
            if (chunked)
            {
                var chunkedStream = new ChunkedWriteStream(stream);
                await response.BodyWriter(chunkedStream, ct).ConfigureAwait(false);
                await chunkedStream.FinishAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await response.BodyWriter(stream, ct).ConfigureAwait(false);
            }
        }
        else if (response.Body is { Length: > 0 })
        {
            await stream.WriteAsync(response.Body, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string ReasonFor(int code) => code switch
    {
        200 => "OK", 204 => "No Content", 206 => "Partial Content", 301 => "Moved Permanently",
        302 => "Found", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 412 => "Precondition Failed", 500 => "Internal Server Error",
        501 => "Not Implemented", 503 => "Service Unavailable", _ => "OK",
    };

    public void Dispose() => Stop();
}

/// <summary>把写入的数据按 HTTP chunked 编码打包。</summary>
internal sealed class ChunkedWriteStream : Stream
{
    private readonly Stream _inner;
    public ChunkedWriteStream(Stream inner) => _inner = inner;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;
        var header = Encoding.ASCII.GetBytes($"{count:X}\r\n");
        _inner.Write(header, 0, header.Length);
        _inner.Write(buffer, offset, count);
        _inner.Write("\r\n"u8);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (count <= 0) return;
        var header = Encoding.ASCII.GetBytes($"{count:X}\r\n");
        await _inner.WriteAsync(header, ct).ConfigureAwait(false);
        await _inner.WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
        await _inner.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return;
        var header = Encoding.ASCII.GetBytes($"{buffer.Length:X}\r\n");
        await _inner.WriteAsync(header, ct).ConfigureAwait(false);
        await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        await _inner.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
    }

    public async Task FinishAsync(CancellationToken ct)
    {
        await _inner.WriteAsync("0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        await _inner.FlushAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// HTTP Range 请求头解析（RFC 7233 的三种形式都支持）：
///   bytes=100-199  显式区间
///   bytes=100-     从 100 到文件末尾
///   bytes=-500     末尾 500 字节（播放器读取 MP4 moov 尾块时常用，解析错会导致无法播放）
/// 特别注意：播放器常用 `bytes=<文件长度>-` 探测文件末尾与可寻址性，此时必须回 416 + Content-Range: bytes */len，
/// 若错误地回 200 + 整个文件，播放器会立即断开连接并认为流已结束（实测导致播放进度永远停在 0）。
/// </summary>
public enum RangeParseResult { None, Satisfiable, Unsatisfiable }

public static class HttpRange
{
    public static RangeParseResult Parse(string? header, long totalLength, out long start, out long end)
    {
        start = 0;
        end = Math.Max(0, totalLength - 1);
        if (string.IsNullOrWhiteSpace(header) || totalLength <= 0) return RangeParseResult.None;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return RangeParseResult.None;

        string spec = header[6..].Trim().Split(',')[0].Trim();   // 只处理第一个区间
        int dash = spec.IndexOf('-');
        if (dash < 0) return RangeParseResult.None;

        string left = spec[..dash].Trim();
        string right = spec[(dash + 1)..].Trim();

        if (left.Length == 0)
        {
            // 后缀范围：bytes=-N
            if (!long.TryParse(right, out long suffix) || suffix <= 0) return RangeParseResult.None;
            if (suffix == 0) return RangeParseResult.Unsatisfiable;
            start = Math.Max(0, totalLength - suffix);
            end = totalLength - 1;
            return RangeParseResult.Satisfiable;
        }

        if (!long.TryParse(left, out long s) || s < 0) return RangeParseResult.None;
        if (s >= totalLength) return RangeParseResult.Unsatisfiable;   // 起点超出文件末尾
        start = s;
        if (right.Length == 0) { end = totalLength - 1; return RangeParseResult.Satisfiable; }
        if (!long.TryParse(right, out long e)) return RangeParseResult.None;
        if (e < s) return RangeParseResult.Unsatisfiable;
        end = Math.Min(e, totalLength - 1);
        return RangeParseResult.Satisfiable;
    }
}

/// <summary>按行读取 + 精确读取的字节流辅助器（不预读过多，避免吞掉请求体）。</summary>
internal sealed class LineReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[8192];
    private int _start, _end;

    public LineReader(Stream stream) => _stream = stream;

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start < _end) return true;
        _start = 0;
        _end = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
        return _end > 0;
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var sb = new List<byte>(128);
        while (true)
        {
            if (!await FillAsync(ct).ConfigureAwait(false)) return sb.Count == 0 ? null : Encoding.UTF8.GetString(sb.ToArray());
            while (_start < _end)
            {
                byte b = _buffer[_start++];
                if (b == (byte)'\n')
                {
                    if (sb.Count > 0 && sb[^1] == (byte)'\r') sb.RemoveAt(sb.Count - 1);
                    return Encoding.UTF8.GetString(sb.ToArray());
                }
                sb.Add(b);
                if (sb.Count > 64 * 1024) return Encoding.UTF8.GetString(sb.ToArray());
            }
        }
    }

    public async Task<byte[]> ReadBytesAsync(int count, CancellationToken ct)
    {
        var result = new byte[count];
        int read = 0;
        while (read < count)
        {
            if (_start < _end)
            {
                int take = Math.Min(_end - _start, count - read);
                Buffer.BlockCopy(_buffer, _start, result, read, take);
                _start += take; read += take;
                continue;
            }
            if (!await FillAsync(ct).ConfigureAwait(false)) break;
        }
        if (read == count) return result;
        var trimmed = new byte[read];
        Buffer.BlockCopy(result, 0, trimmed, 0, read);
        return trimmed;
    }

    public async Task<byte[]> ReadChunkedAsync(CancellationToken ct)
    {
        var output = new MemoryStream();
        while (true)
        {
            string? sizeLine = await ReadLineAsync(ct).ConfigureAwait(false);
            if (sizeLine is null) break;
            string hex = sizeLine.Split(';')[0].Trim();
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int size) || size <= 0) break;
            var chunk = await ReadBytesAsync(size, ct).ConfigureAwait(false);
            output.Write(chunk);
            await ReadLineAsync(ct).ConfigureAwait(false); // 末尾 CRLF
        }
        return output.ToArray();
    }
}