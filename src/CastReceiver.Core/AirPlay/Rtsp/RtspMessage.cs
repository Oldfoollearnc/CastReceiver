using System.Net.Sockets;
using System.Text;

namespace CastReceiver.Core.AirPlay.Rtsp;

/// <summary>一条 RTSP 请求。解析时严格按 <c>Content-Length</c> 取体，因为体可能是二进制 plist。</summary>
internal sealed class RtspRequest
{
    private const int MaxMessageBytes = 1 << 20;

    public required string Method { get; init; }

    public required string Url { get; init; }

    /// <summary>请求行里的协议串（<c>RTSP/1.0</c> 或 <c>HTTP/1.1</c>）；响应必须原样回。</summary>
    public required string Protocol { get; init; }

    public required Dictionary<string, string> Headers { get; init; }

    public required byte[] Body { get; init; }

    public bool HasCSeq { get; init; }

    public int CSeq { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;

    /// <summary>读一条请求；连接关闭返回 null。</summary>
    public static async Task<RtspRequest?> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        var data = new List<byte>(1024);
        var chunk = new byte[4096];
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (n <= 0)
            {
                return null;
            }
            for (int i = 0; i < n; i++)
            {
                data.Add(chunk[i]);
            }
            headerEnd = FindHeaderEnd(data);
            if (data.Count > MaxMessageBytes)
            {
                return null;
            }
        }

        string headerText = Encoding.ASCII.GetString(data.ToArray(), 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return null;
        }

        string[] parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        int bodyStart = headerEnd + 4;
        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? lengthText))
        {
            int.TryParse(lengthText, out contentLength);
        }

        // 体可能还没读全（头与体在同一次读取里到达时只有一部分）
        while (data.Count - bodyStart < contentLength)
        {
            int n = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }
            for (int i = 0; i < n; i++)
            {
                data.Add(chunk[i]);
            }
            if (data.Count > MaxMessageBytes)
            {
                return null;
            }
        }

        int available = Math.Max(0, Math.Min(contentLength, data.Count - bodyStart));
        var body = new byte[available];
        data.CopyTo(bodyStart, body, 0, available);

        bool hasCSeq = false;
        int cseq = 0;
        if (headers.TryGetValue("CSeq", out string? cseqText))
        {
            hasCSeq = int.TryParse(cseqText, out cseq) && cseq >= 0;
        }

        return new RtspRequest
        {
            Method = parts[0].ToUpperInvariant(),
            Url = parts[1],
            Protocol = parts[2],
            Headers = headers,
            Body = body,
            HasCSeq = hasCSeq,
            CSeq = cseq,
        };
    }

    private static int FindHeaderEnd(List<byte> data)
    {
        for (int i = 0; i + 3 < data.Count; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' && data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
            {
                return i;
            }
        }
        return -1;
    }
}

/// <summary>一条 RTSP 响应。</summary>
internal sealed class RtspResponse
{
    public int StatusCode { get; init; } = 200;

    public string Reason { get; init; } = "OK";

    public List<KeyValuePair<string, string>> Headers { get; init; } = new();

    public byte[]? Body { get; init; }

    /// <summary>置位后在响应末尾追加 <c>Connection: close</c>。</summary>
    public bool CloseConnection { get; init; }

    /// <summary>
    /// 序列化。头部顺序照参考实现：状态行 → <c>Audio-Jack-Status</c> → 处理函数的头 →
    /// <c>Server</c> → <c>CSeq</c> → <c>Content-Length</c> → 空行 → 体。
    /// </summary>
    public byte[] Serialize(string protocol, int cseq, string audioJackStatus, string serverVersion)
    {
        var sb = new StringBuilder(256);
        sb.Append(protocol).Append(' ').Append(StatusCode).Append(' ').Append(Reason).Append("\r\n");
        sb.Append("Audio-Jack-Status: ").Append(audioJackStatus).Append("\r\n");
        foreach (var header in Headers)
        {
            sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }
        sb.Append("Server: AirTunes/").Append(serverVersion).Append("\r\n");
        sb.Append("CSeq: ").Append(cseq).Append("\r\n");
        if (CloseConnection)
        {
            sb.Append("Connection: close\r\n");
        }

        byte[] body = Body ?? Array.Empty<byte>();
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("\r\n");

        byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
        if (body.Length == 0)
        {
            return head;
        }

        var result = new byte[head.Length + body.Length];
        Array.Copy(head, result, head.Length);
        Array.Copy(body, 0, result, head.Length, body.Length);
        return result;
    }
}
