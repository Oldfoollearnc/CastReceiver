using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CastReceiver.Core.AirPlay;
using CastReceiver.Core.AirPlay.Discovery;
using CastReceiver.Core.AirPlay.Mirror;
using CastReceiver.Core.AirPlay.Rtsp;

// 开发期验证宿主。
//   完整握手：  airplay-test [localIp] [seconds] [serverName]
//   只验镜像流：airplay-test mirror <hexKey> <hexIv> [seconds]
if (args.Length >= 3 && args[0] == "mirror")
{
    byte[] key = Convert.FromHexString(args[1]);
    byte[] iv = Convert.FromHexString(args[2]);
    int secs = args.Length > 3 ? int.Parse(args[3]) : 20;

    using var receiver = new MirrorStreamReceiver(key, iv, m => Console.WriteLine($"[mirror] {m}"));
    receiver.FrameReady += (frame, ts) =>
        Console.WriteLine($"FRAME ts={ts} hex={Convert.ToHexString(frame).ToLowerInvariant()}");
    receiver.Start();
    Console.WriteLine($"PORT={receiver.Port}");
    Console.Out.Flush();

    await Task.Delay(TimeSpan.FromSeconds(secs));
    Console.WriteLine($"DONE frames={receiver.FrameCount} invalid={receiver.InvalidPayloadCount}");
    return;
}

// 开发期验证宿主：起 mDNS + RTSP，等 airplay_fake_client.py 扮演 iPhone 走完整握手。
//   airplay-test [localIp] [seconds] [serverName]
IPAddress ip = args.Length > 0 ? IPAddress.Parse(args[0]) : FindLanAddress();
int seconds = args.Length > 1 ? int.Parse(args[1]) : 60;
string serverName = args.Length > 2 ? args[2] : "投屏Window";

void Log(string message) => Console.WriteLine($"[airplay] {message}");

var options = new AirPlayOptions
{
    ServerName = serverName,
    MacAddress = "aa:bb:cc:dd:ee:ff",
    PkHex = "b07727d6f6cd6e08b58ede525ec3cdeaa252ad9f683feb212ef8a205246554e7",
};

// 先起控制通道（端口是动态分配的），再用实际端口去广播
using var server = new AirPlayRtspServer(options, Log);
int frameCount = 0;
server.FrameReady += (frame, timestamp) =>
{
    frameCount++;
    Console.WriteLine($"FRAME #{frameCount} bytes={frame.Length} ts={timestamp} head={Convert.ToHexString(frame.AsSpan(0, Math.Min(8, frame.Length)))}");
};
server.Start();

var services = AirPlayAdvertisement.Build(options.ServerName, options.MacAddress, options.PkHex, server.Port);
server.Services = services;

string hostName = AirPlayAdvertisement.LocalHostName();
using var responder = new MdnsResponder(ip, hostName, services, Log);
responder.Start();

Console.WriteLine($"READY ip={ip} rtsp={server.Port} host={hostName} server={options.ServerName}");
Console.Out.Flush();

await Task.Delay(TimeSpan.FromSeconds(seconds));
Console.WriteLine($"DONE frames={frameCount}");

static IPAddress FindLanAddress()
{
    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            continue;
        }

        IPInterfaceProperties props = ni.GetIPProperties();
        if (props.GatewayAddresses.Count == 0)
        {
            continue;
        }

        foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
        {
            if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                return ua.Address;
            }
        }
    }

    throw new InvalidOperationException("找不到可用的局域网 IPv4 地址");
}
