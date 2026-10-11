namespace CastReceiver.Core.Infrastructure;

/// <summary>
/// 读 MP4/MOV 的时长（只读文件头的 <c>moov/mvhd</c>，不解码、不加载媒体引擎）。
///
/// 用途：设置里编辑启动视频时要显示"素材共多少秒"，并把结束秒默认成这个值。
/// 读不出来（比如不是 MP4 家族）就返回 null，界面显示"未知"，不报错。
/// </summary>
public static class Mp4Probe
{
    /// <summary>时长（秒）；读不出来返回 null。</summary>
    public static double? TryGetDurationSeconds(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using FileStream stream = File.OpenRead(path);
            return ReadDuration(stream);
        }
        catch { return null; }
    }

    private static double? ReadDuration(Stream stream)
    {
        // 顶层 box 里找 moov；moov 里找 mvhd。
        // 大文件（几百 MB）也不会整份读进来：只按 box 头跳。
        long length = stream.Length;
        long position = 0;

        while (position + 8 <= length)
        {
            stream.Position = position;
            byte[] header = new byte[8];
            if (stream.Read(header, 0, 8) != 8) return null;

            long size = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            int headerSize = 8;

            if (size == 1)
            {
                // 64 位长度
                byte[] big = new byte[8];
                if (stream.Read(big, 0, 8) != 8) return null;
                size = 0;
                for (int i = 0; i < 8; i++) size = (size << 8) | big[i];
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = length - position;      // 一直到文件末尾
            }

            if (size < headerSize || position + size > length) return null;

            if (type == "moov")
            {
                return ReadMvhd(stream, position + headerSize, position + size);
            }

            position += size;
        }

        return null;
    }

    private static double? ReadMvhd(Stream stream, long start, long end)
    {
        long position = start;
        while (position + 8 <= end)
        {
            stream.Position = position;
            byte[] header = new byte[8];
            if (stream.Read(header, 0, 8) != 8) return null;

            long size = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            if (size < 8 || position + size > end) return null;

            if (type == "mvhd")
            {
                stream.Position = position + 8;
                byte[] body = new byte[Math.Min(32, size - 8)];
                if (stream.Read(body, 0, body.Length) < 20) return null;

                int version = body[0];
                if (version == 1)
                {
                    // version 1: creation(8) modification(8) timescale(4) duration(8)
                    uint timescale = ReadUInt32(body, 20);
                    long duration = ReadInt64(body, 24);
                    if (timescale == 0) return null;
                    return duration / (double)timescale;
                }
                else
                {
                    // version 0: creation(4) modification(4) timescale(4) duration(4)
                    uint timescale = ReadUInt32(body, 12);
                    uint duration = ReadUInt32(body, 16);
                    if (timescale == 0) return null;
                    return duration / (double)timescale;
                }
            }

            position += size;
        }

        return null;
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
        => (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);

    private static long ReadInt64(byte[] buffer, int offset)
    {
        long value = 0;
        for (int i = 0; i < 8; i++) value = (value << 8) | buffer[offset + i];
        return value;
    }
}
