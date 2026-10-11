// 合并可行性探针：一次运行验证多项关键前提，避免逐功能小脚本。
//   A. SSDP 组播 1900 端口绑定（系统 SSDPSRV 已占用 1900 的前提下）
//   B. 组播加入 + M-SEARCH 发送 + 局域网内 DLNA 设备响应统计
//   C. TCP 监听 + 本机 HTTP 回环自测（验证内嵌 HTTP 服务器可用）
//   D. Windows.Media.Miracast 接收端 API 在"未打包(unpackaged) Win32 进程"中是否可用
//   E. 网络位置(Public/Private)与防火墙入站对可见性的影响评估
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

var report = new List<(string Name, bool Ok, string Detail)>();
void Add(string name, bool ok, string detail) => report.Add((name, ok, detail));
void Log(string s) => Console.WriteLine(s);

Log("================ 投屏接收端可行性合并探针 ================");
Log($"[时间] {DateTime.Now:yyyy-MM-dd HH:mm:ss}   [主机] {Environment.MachineName}   [OS] {Environment.OSVersion.Version}");

// ---------------------------------------------------------------- 0. 选定局域网 IPv4
var wlan = NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.OperationalStatus == OperationalStatus.Up)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => (Nic: n, Addr: u.Address)))
    .FirstOrDefault(x => x.Addr.AddressFamily == AddressFamily.InterNetwork);

if (wlan.Addr is null)
{
    Add("0. 找到 WLAN IPv4", false, "未找到已连接无线的 IPv4 地址，后续测试无意义");
    Print();
    return 1;
}
var localIp = wlan.Addr;
Log($"\n[0] WLAN 网卡={wlan.Nic.Name}  本机 IP={localIp}");

// ---------------------------------------------------------------- A. SSDP 1900 绑定
const string SsdpMulticast = "239.255.255.250";
const int SsdpPort = 1900;
Socket? ssdp = null;
try
{
    ssdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    ssdp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    ssdp.ExclusiveAddressUse = false;
    ssdp.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
    try
    {
        ssdp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
            new MulticastOption(IPAddress.Parse(SsdpMulticast), localIp));
    }
    catch (Exception ex) { Log($"    (组播加入异常，忽略继续: {ex.Message})"); }
    ssdp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
    Add("A. SSDP 绑定 UDP:1900(共享)", true, "成功：系统 SSDPSRV 占用下仍可共享绑定并加入组播");
}
catch (Exception ex)
{
    Add("A. SSDP 绑定 UDP:1900(共享)", false, $"{ex.GetType().Name}: {ex.Message}  → 需停用 SSDPSRV 或走高权限方案");
}

// ---------------------------------------------------------------- B. M-SEARCH 收发
int responses = 0;
var found = new ConcurrentDictionary<string, string>();
if (ssdp is not null)
{
    var msearch =
        "M-SEARCH * HTTP/1.1\r\n" +
        $"HOST: {SsdpMulticast}:{SsdpPort}\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 2\r\n" +
        "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n";
    var payload = Encoding.ASCII.GetBytes(msearch);
    try
    {
        ssdp.SendTo(payload, new IPEndPoint(IPAddress.Parse(SsdpMulticast), SsdpPort));
        var buf = new byte[8192];
        var deadline = DateTime.UtcNow.AddSeconds(4);
        ssdp.ReceiveTimeout = 1000;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n = ssdp.ReceiveFrom(buf, ref from);
                string text = Encoding.ASCII.GetString(buf, 0, n);
                if (text.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref responses);
                    string ip = ((IPEndPoint)from).Address.ToString();
                    string loc = text.Split("\r\n").FirstOrDefault(l => l.StartsWith("LOCATION", StringComparison.OrdinalIgnoreCase)) ?? "?";
                    found[ip] = loc;
                }
            }
            catch (SocketException) { }
        }
        Add("B. M-SEARCH 可发送", true, $"已向 {SsdpMulticast}:1900 发送 MediaRenderer 搜索");
        Add("B. 局域网存在其它 DLNA 设备", responses > 0,
            responses > 0 ? $"4 秒内收到 {responses} 个响应，来自: {string.Join(", ", found.Keys)}" : "无响应（可能网内没有 DLNA 设备，或网络为公用导致隔离）");
    }
    catch (Exception ex) { Add("B. M-SEARCH 发送", false, ex.Message); }
}

// ---------------------------------------------------------------- C. TCP 监听 + 本机 HTTP 回环
const int HttpPort = 2869;
TcpListener? listener = null;
try
{
    listener = new TcpListener(IPAddress.Any, HttpPort);
    listener.Start();
    using var client = new TcpClient();
    var connect = client.ConnectAsync(localIp, HttpPort);
    if (connect.Wait(2000))
    {
        using var s = client.GetStream();
        var req = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: probe\r\nConnection: close\r\n\r\n");
        s.Write(req);
        using var srvSide = listener.AcceptTcpClient();
        var one = new byte[1];
        srvSide.GetStream().ReadTimeout = 1500;
        try { srvSide.GetStream().Read(one, 0, 1); } catch { }
        Add("C. TCP 监听 + 回环自测", true, $"成功在 0.0.0.0:{HttpPort} 监听并经 {localIp} 自连成功");
    }
    else { Add("C. TCP 监听 + 回环自测", false, "自连接超时"); }
}
catch (Exception ex)
{
    Add("C. TCP 监听 + 回环自测", false, $"{ex.GetType().Name}: {ex.Message}（端口可能被占用）");
}
finally { try { listener?.Stop(); } catch { } }

// ---------------------------------------------------------------- E. 网络位置
try
{
    var psi = new ProcessStartInfo("powershell", "-NoProfile -Command \"(Get-NetConnectionProfile | Where-Object {$_.InterfaceAlias -eq 'WLAN'}).NetworkCategory\"")
    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    var p = Process.Start(psi)!;
    string cat = p.StandardOutput.ReadToEnd().Trim();
    p.WaitForExit(3000);
    Add("E. WLAN 网络位置", cat.Equals("Private", StringComparison.OrdinalIgnoreCase),
        $"当前 = {cat}；Public 时防火墙入站默认拦截，手机可能发现不了本机（应用内需提供一键放行）");
}
catch (Exception ex) { Add("E. WLAN 网络位置", false, ex.Message); }

// ---------------------------------------------------------------- D. Miracast 接收端 API
try
{
    bool typePresent = Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Media.Miracast.MiracastReceiver");
    Add("D1. MiracastReceiver 类型存在", typePresent, typePresent ? "Windows.Media.Miracast 已在本机注册" : "本机不含该 API");

    var t = Type.GetType("Windows.Media.Miracast.MiracastReceiver, Microsoft.Windows.SDK.NET");
    if (t is not null)
    {
        Log("\n[D] Windows.Media.Miracast 命名空间 API 表面（反射导出）：");
        var asm = t.Assembly;
        foreach (var nt in asm.GetTypes().Where(x => x.Namespace == "Windows.Media.Miracast" && x.IsPublic).OrderBy(x => x.Name))
        {
            Log($"  · {nt.Name}");
            foreach (var m in nt.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly).Take(40))
            {
                if (m is System.Reflection.MethodInfo mi && (mi.IsSpecialName && !mi.Name.StartsWith("get_") && !mi.Name.StartsWith("put_"))) continue;
                if (m is System.Reflection.MethodInfo mi2 && mi2.Name.StartsWith("add_")) continue;
                Log($"      - {m.MemberType}: {m}");
            }
        }

        // 尝试在未打包进程中激活接收端
        try
        {
            object? receiver = null;
            var ctor = t.GetConstructor(Type.EmptyTypes);
            if (ctor is not null)
            {
                receiver = ctor.Invoke(null);
                Add("D2. 未打包进程创建接收端(new MiracastReceiver())", true, "构造成功：" + receiver!.GetType().FullName);
            }
            else
            {
                Add("D2. 未打包进程创建接收端", false, "该类型无公共无参构造函数，需查找静态工厂");
            }

            if (receiver is not null)
            {
                DumpObject("D2b. 接收端当前状态", receiver, "GetStatus");
                DumpObject("D2c. 当前设置", receiver, "GetCurrentSettings");
                DumpObject("D2d. 默认设置", receiver, "GetDefaultSettings");

                // D5: 应用设置（写权限）
                try
                {
                    var def = t.GetMethod("GetDefaultSettings")!.Invoke(receiver, null);
                    var apply = t.GetMethod("DisconnectAllAndApplySettingsAsync")!;
                    var taskObj = await AwaitMaybe(apply.Invoke(receiver, new[] { def }));
                    DumpObject("D5. 应用接收端设置(不合包)", taskObj, null);
                }
                catch (Exception ex) { Add("D5. 应用接收端设置", false, Describe(ex)); }

                // D6: 创建并启动会话 —— Miracast 接收可行性的决定性判据
                object? session = null;
                try
                {
                    var create = t.GetMethod("CreateSessionAsync")!;
                    session = await AwaitMaybe(create.Invoke(receiver, new object?[] { null }));
                    Add("D6. 未打包创建接收会话", session is not null,
                        session is null ? "CreateSessionAsync 返回 null" : "成功：" + session.GetType().Name);
                }
                catch (Exception ex) { Add("D6. 未打包创建接收会话", false, Describe(ex)); }

                if (session is not null)
                {
                    var st = session.GetType();
                    try { st.GetProperty("MaxSimultaneousConnections")?.SetValue(session, 1); } catch { }
                    try { st.GetProperty("AllowConnectionTakeover")?.SetValue(session, true); } catch { }
                    try
                    {
                        var startRes = await AwaitMaybe(st.GetMethod("StartAsync")!.Invoke(session, null));
                        DumpObject("D7. 启动监听会话", startRes, null);
                        DumpObject("D7b. 启动后状态", receiver, "GetStatus");
                    }
                    catch (Exception ex) { Add("D7. 启动监听会话", false, Describe(ex)); }
                    finally { try { (session as IDisposable)?.Dispose(); } catch { } DumpObject("D7c. 释放会话后状态", receiver, "GetStatus"); }
                }
            }
        }
        catch (Exception ex) { Add("D2. 未打包进程创建接收端", false, Describe(ex)); }
    }
    else
    {
        Add("D1b. 反射定位投影类型", false, "未在 Microsoft.Windows.SDK.NET 中找到 MiracastReceiver");
    }
}
catch (Exception ex) { Add("D. Miracast API 探测", false, Describe(ex)); }

Print();
return report.All(r => r.Ok) ? 0 : 2;

static string SafeStatus(object receiver)
{
    try { return receiver.GetType().GetMethod("GetStatus")?.Invoke(receiver, null)?.ToString() ?? "?"; }
    catch { return "?"; }
}

// 反射读取对象属性并作为一条检查项记录（用于 Miracast 状态/设置的结构化输出）
void DumpObject(string title, object? obj, string? methodName)
{
    if (obj is null) { Add(title, false, "对象为 null"); return; }
    try
    {
        if (methodName is not null)
        {
            var m = obj.GetType().GetMethod(methodName);
            if (m is null) { Add(title, false, $"无方法 {methodName}"); return; }
            obj = m.Invoke(obj, null);
            if (obj is null) { Add(title, false, $"{methodName} 返回 null"); return; }
        }
        var parts = obj.GetType().GetProperties()
            .Where(p => p.GetIndexParameters().Length == 0)
            .Select(p =>
            {
                try { var v = p.GetValue(obj); return $"{p.Name}={v ?? "null"}"; }
                catch (Exception ex) { return $"{p.Name}=<{ex.GetType().Name}>"; }
            });
        Add(title, true, string.Join("  ", parts));
    }
    catch (Exception ex) { Add(title, false, Describe(ex)); }
}

// 把 WinRT IAsyncOperation<T> 反射结果转成可 await 的 Task
static async Task<object?> AwaitMaybe(object? r)
{
    if (r is null) return null;
    var iface = r.GetType().GetInterfaces().FirstOrDefault(i =>
        i.IsGenericType && i.GetGenericTypeDefinition().FullName == "Windows.Foundation.IAsyncOperation`1");
    if (iface is null) return r;
    var ext = typeof(System.WindowsRuntimeSystemExtensions).GetMethods()
        .First(m => m.Name == "AsTask" && m.IsGenericMethod && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.IsGenericType);
    var task = (Task)ext.MakeGenericMethod(iface.GetGenericArguments()[0]).Invoke(null, new[] { r })!;
    await task;
    return task.GetType().GetProperty("Result")?.GetValue(task);
}

static string Describe(Exception ex)
{
    string extra = ex.HResult != 0 ? $" HRESULT=0x{ex.HResult:X8}" : "";
    string inner = ex.InnerException is null ? "" : $" | inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
    return $"{ex.GetType().Name}: {ex.Message}{extra}{inner}";
}

void Print()
{
    Log("\n================ 探针结果汇总 ================");
    int i = 0;
    foreach (var (name, ok, detail) in report)
    {
        i++;
        Log($"{i,2}. [{(ok ? "通过" : "失败")}] {name}\n      {detail}");
    }
    int pass = report.Count(r => r.Ok);
    Log($"\n结论：{pass}/{report.Count} 项通过。");
}