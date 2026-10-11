# AirPlay 镜像接收端 · 实现进度

> 目标：让 iPhone/iPad 也能把屏幕投到本机（现在只有安卓 Miracast 能投）。
> 本文件记录**已完成并验证过**的部分、关键设计决定、以及还剩什么。

## 一、为什么 iPhone 现在投不了

本程序是纯 Miracast 接收端（`Windows.Media.Miracast`）。iOS 从始至终不支持 Miracast，
只用 AirPlay，所以 iPhone 的投屏列表里**根本不会出现这台电脑** —— 不是连不上，是搜不到。
Windows 本身也不提供 AirPlay 接收能力，只能自己实现协议。

## 二、已完成并**已验证**

### FairPlay（最高风险的一块，已完成）

AirPlay 握手必须过 FairPlay 3：接收端要应答 `fp-setup` 挑战，并用它解出后续
AES 密钥。这部分是逆向出来的算法（UxPlay 的 `lib/playfair/`，约 40 KB 常量表 +
443 行宽度极敏感的混淆代码），无法凭记忆写对。

已完成 C# 移植：

| 文件 | 对应 UxPlay 源 |
| --- | --- |
| `src/CastReceiver.Core/AirPlay/FairPlay/HandGarble.cs` | `lib/playfair/hand_garble.c`（443 行的 `garble`） |
| `src/CastReceiver.Core/AirPlay/FairPlay/SapHash.cs` | `lib/playfair/sap_hash.c` |
| `src/CastReceiver.Core/AirPlay/FairPlay/ModifiedMd5.cs` | `lib/playfair/modified_md5.c` |
| `src/CastReceiver.Core/AirPlay/FairPlay/OmgHax.cs` | `lib/playfair/omg_hax.c` + `playfair.c` |
| `src/CastReceiver.Core/AirPlay/FairPlay/FairPlaySession.cs` | `lib/fairplay_playfair.c` |
| `src/CastReceiver.Core/AirPlay/FairPlay/FairPlayTables.g.cs` | `omg_hax.h` + `omg_hax.c` 的常量（**自动生成**） |
| `src/CastReceiver.Core/AirPlay/FairPlay/FairPlayConstants.g.cs` | 4 条 142 字节应答 + 12 字节头（**自动生成**） |

### 验证方式（不需要 iPhone）

用 w64devkit 里的 GCC 编译 **UxPlay 原版 C 源码**，逐层导出参考向量，再让 C# 移植
逐字节比对。向量与工具都在仓库里：

```powershell
# 重新生成向量（需要 GCC；harness 会编译 UxPlay 原始 C 源码）
#   源文件见 tools/airplay-port/fairplay-harness.c
# 比对（无需任何依赖）
cd tools\airplay-port\vector-verify
dotnet run -c Release -- ..\..\..\tests\CastReceiver.SelfTest\AirPlay\fairplay-vectors.json
```

当前结果：**pass=43 fail=0**，覆盖
`modified_md5` / `sap_hash` / `garble`（5 个缓冲区全量）/ `generate_session_key`（mode 0–3）/
`generate_key_schedule` / `cycle` / `fairplay_setup`（4 种 mode）/ `fairplay_handshake` /
`fairplay_decrypt`（8 组端到端，覆盖 4 个 mode，含 72 字节 `ekey` → 16 字节 AES 密钥的完整链路）。

> 关键点：`garble` 的向量比对的是**所有 5 个缓冲区的最终状态**，不是只看最终密钥 ——
> 这样任何一处的宽度/符号错误都会立刻暴露。第一次比对时只差 2 个字节，
> 定位到 `b1[15]` 一行的括号层级错误（C 是 `3*((A^C) - D)`，最初写成了 `3*(A^C) - D`）。

### 二进制 plist（已完成并已验证）

`src/CastReceiver.Core/AirPlay/Plist/BinaryPlist.cs`：`bplist00` 的读写。
`GET /info` 的响应、`SETUP` 的请求与响应体都必须是二进制 plist，XML plist 在 RTSP 通道上不被接受。

验证方式是**与 Python 的 `plistlib` 交叉比对**（完全独立的实现，而且它的对象排布、
去重策略都不同，正好能验证"不假设某种布局"）：

```powershell
$py = <python>
& $py tools\airplay-port\plist_roundtrip.py $env:TEMP\plist\cases.json $env:TEMP\plist\bin   # 生成用例
cd tools\airplay-port\plist-verify
dotnet run -c Release -- read  $env:TEMP\plist\cases.json $env:TEMP\plist\bin   # 13/13
dotnet run -c Release -- write $env:TEMP\plist\cases.json .\out                  # 13/13
& $py ..\plist_roundtrip.py --verify $env:TEMP\plist\cases.json .\out            # 13/13
```

当前结果：**读 13/13、写 13/13**。这一轮抓到 4 个真实缺陷：

| 缺陷 | 现象 | 根因 |
| --- | --- | --- |
| 循环引用误报 | 真实 `/info` 结构直接报"循环引用" | `plistlib` 会**复用同一个对象**（去重），"访问过就永久标记"是错的，必须只在递归栈上标记 |
| u64 变负数 | `2^64-1` 读成 `-1` | 整数宽度不同语义不同：**1/2/4 字节无符号、8/16 字节有符号**；超过 `long.MaxValue` 必须用 16 字节形式 |
| 负整数写错宽度 | `-12345` 被读成 `53191` | 1/2/4 字节按无符号解读，所以负数只能写 8 字节有符号 |
| 标签页比较器 | 字典里恰好有名为 `a`/`i` 的键时误判 | 测试代码把"用户键"当成"类型标签"解释 |

### mDNS 发现（已完成并已验证）

`src/CastReceiver.Core/AirPlay/Discovery/MdnsResponder.cs` + `AirPlayAdvertisement.cs`：
广播 `_airplay._tcp` 与 `_raop._tcp`（端口都指向 RTSP 端口），TXT 键值严格照参考实现。

验证方式是**真的当一次 mDNS 客户端**：`tools/airplay-port/mdns_query.py` 从临时端口发
DNS-SD 查询（正是 iPhone 的做法），再由独立的 DNS 解析器核对应答：

```powershell
# 起应答器（默认设备名「投屏Window」，顺带验证中文实例名的 UTF-8 编码）
tools\airplay-port\mdns-test\bin\Release\net10.0\mdns-test.exe <本机IP> 40
# 另一个窗口查询
& $py tools\airplay-port\mdns_query.py <本机IP> 7000
```

当前结果：**29 项检查全部通过**，覆盖 `_services._dns-sd._udp.local` 列出两种服务类型、
`_airplay` 的 PTR/SRV/TXT/A、端口、TTL（PTR/TXT=4500、SRV/A=120）、cache-flush 位
（SRV/TXT/A 有、PTR 无），以及 `_raop` 专属 TXT（`ft`/`am`/`rhd`/`sr`/`tp`/`txtvers`）。

抓到 2 个真实缺陷：`_services` 分支的 `continue` 跳过了"传统查询单播应答"，
而客户端绑在临时端口上根本收不到发往 5353 的组播——**这种 bug 只有真的发查询才会暴露**。

### AES-128-CTR（已完成并已验证）

`src/CastReceiver.Core/AirPlay/Mirror/Aes128Ctr.cs`：语义与 OpenSSL 的 `EVP_aes_128_ctr` 一致
（IV 直接当 128 位**大端**计数器，每块 +1），并且**跨调用保持同一条密钥流**。

这台机器上没有 OpenSSL、也没有任何 Python 密码库，所以先用 Python 从零写了一个 AES-128，
并**先用已知答案向量确认它自己是对的**，再拿它做 CTR 的对照实现：

```
FIPS-197 KAT ok: AES-128(00112233445566778899aabbccddeeff) = 69c4e0d86a7b0430d8cdb78070b4c55a
NIST SP800-38A CTR block 1 = 874d6191b620e3261bef6864990db6ce   ← 与公开值一致
```

然后比对 C#：**5/5 通过**，其中特意包含"分多次调用、含空段、跨 16 字节边界、计数器进位"
这几种形态，用来锁死"密钥流连续"这个关键语义。

```powershell
& $py tools\airplay-port\aes_ctr_oracle.py tests\CastReceiver.SelfTest\AirPlay\aes-ctr-vectors.json
cd tools\airplay-port\vector-verify
dotnet run -c Release -- aes ..\..\..\tests\CastReceiver.SelfTest\AirPlay\aes-ctr-vectors.json
```

### RTSP 控制通道（已完成并已验证）

`src/CastReceiver.Core/AirPlay/Rtsp/`（请求/响应框架 + 服务与端点）与
`src/CastReceiver.Core/AirPlay/AirPlayInfoPlist.cs`（`GET /info` 的响应体）。

验证方式是**扮演一次 iPhone**：`tools/airplay-port/airplay_fake_client.py` 先用 mDNS 发现服务、
从 SRV 拿到端口，再走完整握手。其中 `/info` 与 `SETUP` 的响应是**用 Python 的 plistlib 解析**的
（不是我们自己的 reader），`fp-setup` 的应答则**与 UxPlay C 参考向量逐字节比对**。

```powershell
tools\airplay-port\airplay-test\bin\Release\net10.0\airplay-test.exe <本机IP> 70
& $py tools\airplay-port\airplay_fake_client.py <本机IP>
```

当前结果：**46 项检查全部通过** —— 覆盖 OPTIONS（Public 头）、`GET /info`（features=0x527FFEE6、
model、deviceID、pk 32 字节、vv、statusFlags=68、displays 尺寸与布尔类型、audioLatencies/Formats）、
`fp-setup` 16 字节→142 字节（与参考应答一致）与 164 字节→32 字节（12 字节头 + 回显请求尾 20 字节）、
`SETUP` 两次（`ekey`/`eiv` 解出 AES 密钥 → `timingPort`/`eventPort`；`streams:[110]` → 真实 `dataPort`）、
`RECORD`（`Audio-Latency: 11025`、`Audio-Jack-Status: analog`）、`SET_PARAMETER`/`GET_PARAMETER` 音量、
`POST /feedback`、`TEARDOWN`（`Connection: close`），以及 **`dataPort` 真的在监听**。

### 镜像流数据通路（已完成并已验证）

`src/CastReceiver.Core/AirPlay/Mirror/MirrorStreamReceiver.cs`：128 字节头解析、
连续密钥流解密、`AVCC`→`Annex-B` 就地改写、SPS/PPS 按时间戳前缀注入、坏载荷计为无效而不崩溃。

验证方式是**用 Python 的 AES-CTR oracle 当加密端**发一串合成报文
（`tools/airplay-port/mirror_stream_probe.py`），C# 侧必须同时做对四件事：分帧、连续密钥流、
长度改写、配置注入。中间还故意插入明文包与一个坏包：

| 期望 | 实测 |
| --- | --- |
| 帧1 = `00000001 SPS 00000001 PPS 00000001 IDR` | 一致 ✓ |
| 帧2 = `00000001 AUD 00000001 非IDR` | 一致 ✓（证明中间的明文包**没有**推进密钥流） |
| 坏包计为"无效载荷"且连接不断 | `无效载荷 1 个`，无异常 ✓ |

这一轮抓到一个**只有发真数据才会暴露的安全缺陷**：`AVCC` 长度前缀可以是接近 `int.MaxValue` 的值，
`pos + 4 + naluLength` **溢出成负数**，于是越界检查失效、下标变负 —— 坏数据会把接收端打成异常
而不是"判为无效"。已改用 64 位中间量。

### 接入渲染管线（已完成并已验证）

`src/CastReceiver.App/Interop/AirPlayVideoSource.cs`：把镜像帧（Annex-B 的 H.264 访问单元）
包成一个 `MediaStreamSource`，直接交给现有的
`CompositionVideoHost.AttachSource(MediaSource, …)`。

选 `MediaStreamSource` 的理由：它是**推流式**的 MediaSource，由系统在需要解码时回调
`SampleRequested` 向我们要数据，正好匹配"手机持续推、我们边收边喂"；
而且它与现有渲染链路完全兼容——截屏/录制/反截屏/画质档位都建立在 MediaPlayer 之上，
**换掉 MediaSource 即可全部复用**，不需要改渲染代码。

时间戳按"首帧为零点"重排：手机发的是它自己的 Q32.32 秒时钟（近似开机纳秒数），
直接当 `TimeSpan` 用会得到离谱的值。队列满时**丢最旧的帧**（直播以"跟得上"为先）。

验证不是只测管线通不通，而是**真的解码出画面**：本机没有 ffmpeg，所以先用一个小 MP4 解析器
（`tools/airplay-port/mp4_extract_h264.py`，自己走 box 树：`stsd/avcC` 取 SPS/PPS、
`stsz/stsc/stco/stts` 取样本）从 `assets/sample.mp4` 里抽出**真实的 H.264 Annex-B 流**，
再推给 `AirPlayVideoSource`，由 MediaPlayer（帧服务器模式）确认：

```
loaded 300 frames
MediaOpened: natural=640x360 duration=00:00:00     ← 解码器从 SPS 读出了真实分辨率
VideoFrameAvailable: 第一帧已解码
RESULT opened=1 decodedFrames=1290 failed=0 pushed=300 delivered=277 dropped=23 queued=0
```

即：**我们产出的那种字节，Media Foundation 确实能解出画面**（`failed=0`），
而不是"接上了但黑屏"。`dropped=23` 是设计内的背压行为（8ms 的推送节奏快过解码器）。

```powershell
& $py tools\airplay-port\mp4_extract_h264.py assets\sample.mp4 $env:TEMP\sample_frames.bin
tools\airplay-port\render-test\bin\Release\net10.0-windows10.0.26100.0\render-test.exe $env:TEMP\sample_frames.bin 8
```

### 接入主程序（已完成并已验证）

- `src/CastReceiver.App/Services/AirPlayService.cs`：把三层串起来——mDNS 广播 → RTSP 控制通道 →
  视频源；只在局域网就绪时启动，**失败不影响安卓那条 Miracast 链路**（两条链路并行、互不干扰）。
- `CastAppHost` 增加 `AirPlay` 与 `StartAirPlay()`/`StopAirPlay()`；
  `CastSettings` 增加 `AutoStartAirPlay`（默认开）、`AirPlayMac`、`AirPlayPk`。
- `MainWindow` 复用现有渲染宿主：`AirPlay.MediaSourceCreated → MiracastHost.AttachSource(...)`，
  `AirPlay.Disconnected → Detach + EndSession()`。因为截屏/录制/反截屏/画质档位都建立在
  这个宿主之上，**AirPlay 不需要另做一套**。

验证方式是**把伪 iPhone 指向真正运行的程序**（用 `--settings-root` 起隔离配置，不动你的真实设置）：

```powershell
src\CastReceiver.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\投屏Window.exe --settings-root <临时目录>
& $py tools\airplay-port\airplay_fake_client.py <本机IP> <向量文件> PC 7A:1B:2C:3D:4E:5F
```

结果：**46/46 全部通过** —— 真实程序以 `PC._airplay._tcp.local` 广播、被 mDNS 解析出端口、
走完整 RTSP 握手（含与 UxPlay 参考应答逐字节一致的 `fp-setup`）、拿到真实 `dataPort`。
过程中唯一的"失败"是测试脚本把 deviceID 写死成了 `aa:bb:cc:dd:ee:ff`，而应用用的是配置里的
`AirPlayMac` —— 这是脚本的问题，已参数化。

### 产品内协议级自检（已完成并已验证）

`src/CastReceiver.Core/Diagnostics/AirPlaySelfTest.cs` 把整条链路做成了产品自检的第 **J** 组，
**不依赖网络、不依赖手机、也不依赖开发期的 Python/GCC 工具链**——期望值是硬编码的参考向量：

```powershell
tests\CastReceiver.SelfTest\bin\Release\net10.0-windows10.0.26100.0\win-x64\CastReceiver.SelfTest.exe --airplay-only
```

结果：**13 项全部通过**。

| 用例 | 依据 |
| --- | --- |
| fp-setup 16 字节挑战应答 | 与 UxPlay 参考实现 142 字节逐字节一致 |
| fp-setup 164 字节握手结构 | 12 字节头 + 回显请求尾 20 字节 |
| 由 ekey 解出 AES 密钥 | 与参考实现导出的向量一致（`af2bd1d3…b466f3`） |
| AES-128-CTR 已知答案 | NIST SP 800-38A 公开向量 |
| AES-128-CTR 密钥流跨调用连续 | 分段（含空段）与整段等价 |
| 二进制 plist 往返 / 拒绝损坏数据 | 自洽 + 魔数校验 |
| 广播两种服务 / features 第 27 位为 0 / TXT 关键字段 | 防止配对开关被误改 |
| 镜像流 AVCC→Annex-B / 拒绝越界载荷 | 含长度前缀溢出那条安全用例 |
| 视频密钥/IV 派生 | 长度、稳定性、key ≠ iv |

> 顺带说明：`--airplay-only` 是这次新加的开关（与既有的 `--vault-only` 同类），
> 故意做成不碰文件系统——因为本机 `%TEMP%` 对被测进程有写入限制，
> 加密库那组会因此报 "Access denied"（既有现象，与 AirPlay 无关）。

### 设置界面（已完成）

设置 → 通用 增加「AirPlay 接收（iPhone / iPad）」开关，**改完立即生效**（打开就广播、关掉就停，
不需要重启）。默认开启，所以不装任何东西也能直接被 iPhone 搜到。

## 三、关键设计决定（已从源码核实）

| 项 | 结论 | 依据 |
| --- | --- | --- |
| 是否必须实现 AirPlay 2 配对（SRP/Curve25519/Ed25519） | **不需要** | `uxplay.cpp:2484` 用 `setup_legacy_pairing`（默认 `false`，`:164`）**覆盖** features 第 27 位 → iOS 不做 pair-setup |
| 因此广播的 features | **`0x527FFEE6,0x0`**（第 27 位 = 0） | `uxplay.cpp:2389-2484` 运行时逐位设置后覆盖 `dnssdint.h` 的字面量 |
| mDNS TXT 与 `GET /info` 的 features 是否要一致 | **要**，二者都读同一个运行时值 | `raop_handlers.h:133` 调 `dnssd_get_airplay_features` |
| 镜像视频流的加密方式 | **AES-128-CTR**（不是 CBC） | `mirror_buffer.c` 只用 `aes_ctr_*`；CBC 那条是**音频**路径 `raop_buffer.c:130-137` |
| 视频密钥派生 | `SHA512("AirPlayStreamKey"+dec(streamConnectionID) ‖ aeskey16)[0:16]`，IV 同理换 `"AirPlayStreamIV"` | `mirror_buffer.c:41-92`（是 SHA-512，源码注释里的 "sha-256" 是错的） |
| 镜像包格式 | `[128 字节头][payload]`，头里 `[0:4]` 小端 payload_size、`[4]` 类型 | `raop_rtp_mirror.c:297-363` |
| 包类型 | `0x00` 加密 H.264、`0x01` 未加密 SPS/PPS、`0x02` 旧协议心跳、`0x05` 每秒报表；**没有 0x06** | `raop_rtp_mirror.c:389-824` |
| 控制端口 | **不是固定 7000**，必须用 mDNS SRV 里广播的那个 | `uxplay.cpp:3155-3164` |
| 镜像视频连接方向 | **手机连电脑**（接收端 listen，`dataPort` 在 SETUP 响应里给出） | `raop_rtp_mirror.c:863-920` |

## 四、状态

目标范围内的每一项都已完成并验证：广播 → 握手 → FairPlay → 解密 → 拆包 → 解码出画面 →
接入程序 → 产品内自检 → 发布版。

**仍然无法由我确认的一件事**：真实 iPhone 是否接受我们广播的这套 TXT/features 组合。
只能等有设备时验证。降低风险的事实是：`0x527FFEE6`（第 27 位 = 0）是 UxPlay 的**默认值**，
而 UxPlay 是当前仍在维护、被大量真机使用的实现——所以这个组合是经过实战的，不是我拼出来的。

另外记录一个**既有**（与 AirPlay 无关）的环境问题：本机跑完整自检会卡在
DLNA/媒体代理阶段（本机对公网 TLS 不可用），且加密库组会因 `%TEMP%` 写入受限而报
Access denied。所以 AirPlay 的验证走 `--airplay-only`；完整自检的
「45 项」结论在这台机器上无法复现。

## 五、限制（必须如实说明）

- **没有 iPhone，端到端"手机真能连上"无法证明。** 能做到的是：协议级自检全绿 +
  与参考实现逐字节一致。首次接真机大概率还要按日志微调（TXT 记录、`/info` 的若干
  键值、以及各 iOS 版本对第 27 位的处理）。
- 参考实现 UxPlay 是 **GPLv3**，RPiPlay 同为 GPLv3。本项目移植的是其 FairPlay 算法与常量表，
  若对外分发需按 GPL 处理（**这一条需要你确认**）。

## 六、开发期工具（都在 temp，不进仓库/发布包）

参考源码在 `%TEMP%\airplay-ref\`：UxPlay 源码、w64devkit（GCC）、C 向量生成器构建目录。
这些只用于生成测试向量，产品本身不依赖它们。
