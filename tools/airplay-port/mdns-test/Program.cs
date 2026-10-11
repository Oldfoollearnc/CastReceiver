using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CastReceiver.Core.AirPlay.Discovery;

// 开发期验证宿主：启动 mDNS 应答器并保持运行，供 mdns_query.py 查询。
//   mdns-test [localIp] [seconds] [serverName] [rtspPort]
IPAddress ip = args.Length > 0 ? IPAddress.Parse(args[0]) : FindLanAddress();
int seconds = args.Length > 1 ? int.Parse(args[1]) : 30;
string serverName = args.Length > 2 ? args[2] : "投屏Window";
int port = args.Length > 3 ? int.Parse(args[3]) : 7000;

string hostName = AirPlayAdvertisement.LocalHostName();
string mac = "aa:bb:cc:dd:ee:ff";
string pk = new string('b', 64);

var services = AirPlayAdvertisement.Build(serverName, mac, pk, port);
using var responder = new MdnsResponder(ip, hostName, services, msg => Console.WriteLine($"[mdns] {msg}"));
responder.Start();

Console.WriteLine($"READY ip={ip} host={hostName} server={serverName} port={port} boundPort={responder.BoundPort}");
Console.Out.Flush();

await Task.Delay(TimeSpan.FromSeconds(seconds));
Console.WriteLine("DONE");

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
