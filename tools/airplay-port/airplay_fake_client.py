#!/usr/bin/env python3
"""Play an iPhone: discover the receiver over mDNS, then walk the whole RTSP handshake.

This is the protocol-level acceptance test. Everything is cross-checked against an
independent implementation where possible:
  * mDNS replies are decoded by our own decoder (tools/airplay-port/mdns_query.py)
  * /info and SETUP replies are parsed with Python's plistlib, NOT our own reader
  * fp-setup replies are compared against the vectors generated from the UxPlay C reference

Usage:
    python airplay_fake_client.py <localIp> [vectors.json] [serverName]
"""
import json
import os
import plistlib
import socket
import struct
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mdns_query as mdns   # noqa: E402
from aes_ctr_oracle import ctr_keystream   # noqa: E402
import hashlib   # noqa: E402

failures = []
checks = 0


def check(cond, label, detail=""):
    global checks
    checks += 1
    if cond:
        print("ok   %s" % label)
    else:
        print("FAIL %s %s" % (label, detail))
        failures.append(label)


# ---------------------------------------------------------------- RTSP framing

def send_rtsp(sock, method, url, cseq=None, body=b"", headers=None):
    head = "%s %s RTSP/1.0\r\n" % (method, url)
    if cseq is not None:
        head += "CSeq: %d\r\n" % cseq
    for k, v in (headers or {}).items():
        head += "%s: %s\r\n" % (k, v)
    if body:
        head += "Content-Length: %d\r\n" % len(body)
    head += "\r\n"
    sock.sendall(head.encode("ascii") + body)


def recv_rtsp(f):
    """Read one RTSP response; returns (status_line, headers_dict, body_bytes)."""
    raw = b""
    while b"\r\n\r\n" not in raw:
        ch = f.read(1)
        if not ch:
            return None
        raw += ch
    head, body = raw.split(b"\r\n\r\n", 1)
    lines = head.decode("latin1").split("\r\n")
    headers = {}
    for line in lines[1:]:
        if ":" in line:
            k, v = line.split(":", 1)
            headers[k.strip().lower()] = v.strip()
    length = int(headers.get("content-length", "0"))
    while len(body) < length:
        ch = f.read(length - len(body))
        if not ch:
            break
        body += ch
    return lines[0], headers, body[:length]


def read_frames(path):
    """读 mp4_extract_h264.py 产出的 APH264 帧文件。"""
    with open(path, "rb") as handle:
        data = handle.read()
    count = struct.unpack_from("<I", data, 8)[0]
    frames = []
    pos = 12
    for _ in range(count):
        length = struct.unpack_from("<I", data, pos)[0]
        timestamp = struct.unpack_from("<Q", data, pos + 4)[0]
        pos += 12
        frames.append((timestamp, data[pos:pos + length]))
        pos += length
    return frames


def annexb_to_avcc(payload):
    """Annex-B（起始码）→ AVCC（4 字节长度前缀）。

    接收端按 AVCC 解析（TryConvertAvccToAnnexB），所以发之前必须转过来。
    找不到起始码就当作已经是 AVCC，原样返回。
    """
    starts = []
    i = 0
    while i < len(payload) - 3:
        if payload[i] == 0 and payload[i + 1] == 0:
            if payload[i + 2] == 1:
                starts.append((i, 3))
                i += 3
                continue
            if i + 3 < len(payload) and payload[i + 2] == 0 and payload[i + 3] == 1:
                starts.append((i, 4))
                i += 4
                continue
        i += 1

    if not starts:
        return payload

    out = bytearray()
    for index, (offset, sc) in enumerate(starts):
        end = starts[index + 1][0] if index + 1 < len(starts) else len(payload)
        nalu = payload[offset + sc:end]
        out += struct.pack(">I", len(nalu)) + nalu
    return bytes(out)


def send_mirror_video(conn, aes_key, stream_id, frames_path, seconds, pace=0.033):
    """发送**真实加密**的镜像流：接收端据此得到实测码率，并真的解码出画面。

    视频密钥派生与接收端一致（见 AirPlayRtspServer.DeriveVideoKey）：
        key = SHA512("AirPlayStreamKey" + 十进制 streamConnectionID ‖ aesKey)[0:16]
        iv  = SHA512("AirPlayStreamIV"  + 十进制 streamConnectionID ‖ aesKey)[0:16]
    密钥流在所有 0x00 载荷之间是**连续**的，所以这里一次性生成。
    """
    key = hashlib.sha512(b"AirPlayStreamKey" + str(stream_id).encode() + aes_key).digest()[:16]
    iv = hashlib.sha512(b"AirPlayStreamIV" + str(stream_id).encode() + aes_key).digest()[:16]

    frames = [(ts, annexb_to_avcc(payload)) for ts, payload in read_frames(frames_path)]
    if not frames:
        return 0

    # 密钥流按块增量生成（而不是一次性算好整段）：
    # 纯 Python 的 AES 很慢，一次性算几十秒的密钥流会先卡住很久才开始发数据，
    # 那样码率曲线会变成"一段空白 + 一个尖峰"，看不出真实吞吐。
    chunk_bytes = 128 * 1024
    keystream = b""
    blocks_used = 0

    # 循环发送时时间戳必须**单调递增**：直接重放同一批时间戳会让接收端的
    # 播放管道以为时间在往回走，滞后统计会一路虚高（真实手机不会这样）。
    raw = [ts for ts, _ in frames]
    span = max(raw) - min(raw)
    step = span // max(1, len(frames) - 1) if len(frames) > 1 else 0
    loop_span = span + step
    pass_index = 0

    deadline = time.time() + seconds
    offset = 0          # 已消费的密钥流字节数（全局，跨块连续）
    sent = 0
    frames_sent = 0

    while time.time() < deadline:
        progressed = False
        for timestamp, payload in frames:
            if time.time() >= deadline:
                break

            local = offset
            if local + len(payload) > len(keystream):
                # 新块的计数器接着已生成的密钥流走（CTR 的 128 位计数器按块递增）
                start_blocks = len(keystream) // 16
                counter = (int.from_bytes(iv, "big") + start_blocks) % (1 << 128)
                keystream += ctr_keystream(key, counter.to_bytes(16, "big"), chunk_bytes)
                blocks_used = len(keystream) // 16

            enc = bytes(a ^ b for a, b in zip(payload, keystream[local:local + len(payload)]))
            offset += len(payload)

            header = bytearray(128)
            struct.pack_into("<I", header, 0, len(enc))
            header[4] = 0x00                      # 0x00 = 加密 H.264
            struct.pack_into("<Q", header, 8, (timestamp + pass_index * loop_span) & 0xFFFFFFFFFFFFFFFF)
            conn.sendall(bytes(header) + enc)
            sent += 128 + len(enc)
            frames_sent += 1
            progressed = True
            time.sleep(pace)

        if not progressed:
            break
        pass_index += 1

    print("MIRROR 已发送 %d 字节 / %d 帧" % (sent, frames_sent), flush=True)
    return sent


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    local_ip = sys.argv[1]
    vectors = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)),
        "..", "..", "tests", "CastReceiver.SelfTest", "AirPlay", "fairplay-vectors.json")
    server = sys.argv[3] if len(sys.argv) > 3 else "投屏Window"
    # 接收端的 deviceID 是可配置的（应用的 AirPlayMac），所以期望值要能传进来
    expected_mac = sys.argv[4] if len(sys.argv) > 4 else "aa:bb:cc:dd:ee:ff"
    # 可选：握手完成后保持连接多少秒（默认 0 = 立刻 TEARDOWN）
    hold = int(sys.argv[5]) if len(sys.argv) > 5 else 0
    # 可选：要发送的 H.264 帧文件（mp4_extract_h264.py 的产物），给了就会发真实加密镜像流
    frames_path = sys.argv[6] if len(sys.argv) > 6 else None
    with open(vectors, encoding="utf-8") as f:
        fp_vectors = json.load(f)

    # ---- 1. discovery (what an iPhone does first)
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 255)
    sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(local_ip))
    sock.bind((local_ip, 0))
    try:
        recs = mdns.query(sock, "_airplay._tcp.local")
    finally:
        sock.close()

    instance = "%s._airplay._tcp.local" % server
    srvs = [r for r in recs if r["type"] == mdns.TYPE_SRV and r["name"] == instance]
    check(len(srvs) == 1, "mDNS 解析出 _airplay SRV", [r["name"] for r in recs if r["type"] == mdns.TYPE_SRV])
    if not srvs:
        print("\n==== cannot continue without discovery ====")
        return 1
    port = srvs[0]["port"]
    print("info 发现服务：%s -> %s:%d" % (instance, srvs[0]["target"], port))

    txt = [r for r in recs if r["type"] == mdns.TYPE_TXT and r["name"] == instance]
    txt_items = {}
    for item in (txt[0]["txt"] if txt else []):
        if "=" in item:
            k, v = item.split("=", 1)
            txt_items[k] = v
    check(txt_items.get("features") == "0x527FFEE6,0x0",
          "发现的 features 第27位为0（跳过配对）", txt_items.get("features"))

    # ---- 2. control channel
    conn = socket.create_connection((local_ip, port), timeout=8)
    f = conn.makefile("rb")
    cseq = 1

    try:
        # OPTIONS
        send_rtsp(conn, "OPTIONS", "*", cseq)
        status, headers, _ = recv_rtsp(f)
        check(status.startswith("RTSP/1.0 200"), "OPTIONS 返回 200", status)
        check("setup" in headers.get("public", "").lower(), "OPTIONS 带 Public 头", headers.get("public"))
        check(headers.get("cseq") == str(cseq), "CSeq 被原样回显", headers.get("cseq"))
        check(headers.get("server", "").startswith("AirTunes/"), "Server 头", headers.get("server"))
        check("audio-jack-status" in headers, "带 Audio-Jack-Status", headers.get("audio-jack-status"))

        # GET /info（用 plistlib 解析，不是我们自己的 reader）
        cseq += 1
        send_rtsp(conn, "GET", "/info", cseq)
        status, headers, body = recv_rtsp(f)
        check(status.startswith("RTSP/1.0 200"), "GET /info 返回 200", status)
        check(headers.get("content-type") == "application/x-apple-binary-plist",
              "/info 是二进制 plist", headers.get("content-type"))
        try:
            info = plistlib.loads(body)
            ok = True
        except Exception as exc:      # noqa: BLE001
            info, ok = {}, False
            check(False, "/info 能被 plistlib 解析", str(exc))
        if ok:
            check(True, "/info 能被 plistlib 解析（%d 字节）" % len(body))
            check(info.get("features") == 0x527FFEE6, "features = 0x527FFEE6", hex(info.get("features", 0)))
            check(info.get("model") == "AppleTV3,2", "model", info.get("model"))
            check(info.get("deviceID") == expected_mac, "deviceID", info.get("deviceID"))
            check(len(info.get("pk", b"")) == 32, "pk 是 32 字节", len(info.get("pk", b"")))
            check(info.get("vv") == 2, "vv = 2", info.get("vv"))
            check(info.get("sourceVersion") == "220.68", "sourceVersion", info.get("sourceVersion"))
            check(info.get("statusFlags") == 68, "statusFlags = 68", info.get("statusFlags"))
            check(info.get("keepAliveSendStatsAsBody") is True, "keepAliveSendStatsAsBody = true")
            check(info.get("pi") == "2e388006-13ba-4041-9a67-25dd4a43d536", "pi", info.get("pi"))
            displays = info.get("displays") or []
            check(len(displays) == 1 and displays[0].get("width") == 1920
                  and displays[0].get("height") == 1080, "displays 尺寸 1920x1080", displays)
            check(displays and displays[0].get("rotation") is False
                  and displays[0].get("overscanned") is False,
                  "displays 的 rotation/overscanned 是布尔", displays)
            check(len(info.get("audioLatencies") or []) == 2, "audioLatencies 两项")
            check(len(info.get("audioFormats") or []) == 2, "audioFormats 两项")

        # POST /fp-setup：16 字节挑战 → 142 字节（与参考向量逐字节一致）
        vec = fp_vectors["fpSetup"][0]
        req16 = bytes.fromhex(vec["request"])
        cseq += 1
        send_rtsp(conn, "POST", "/fp-setup", cseq, req16,
                  {"Content-Type": "application/octet-stream"})
        status, headers, body = recv_rtsp(f)
        check(status.startswith("RTSP/1.0 200"), "fp-setup(16) 返回 200", status)
        check(len(body) == 142, "fp-setup(16) 返回 142 字节", len(body))
        check(body == bytes.fromhex(vec["response"]), "fp-setup(16) 与 UxPlay 参考应答逐字节一致")

        # POST /fp-setup：164 字节握手 → 32 字节 = 12 字节头 + 请求尾 20 字节
        # 这里刻意用**向量里的 keymsg**，而不是随便造一段：这样接收端解出的 AES 密钥
        # 正好就是向量里的 key —— 客户端知道它，于是才能真的发一段接收端解得开的加密视频流。
        # （自己造握手的话密钥只有接收端知道，客户端永远发不出能被解开的流。）
        decrypt_vec = fp_vectors["fpDecrypt"][0]
        handshake = bytearray(bytes.fromhex(decrypt_vec["keymsg"]))
        cseq += 1
        send_rtsp(conn, "POST", "/fp-setup", cseq, bytes(handshake),
                  {"Content-Type": "application/octet-stream"})
        status, headers, body = recv_rtsp(f)
        check(status.startswith("RTSP/1.0 200"), "fp-setup(164) 返回 200", status)
        check(len(body) == 32, "fp-setup(164) 返回 32 字节", len(body))
        check(body[:12] == bytes.fromhex("46504c590301040000000014"),
              "fp-setup(164) 的 12 字节头正确", body[:12].hex())
        check(body[12:] == bytes(handshake[144:164]), "fp-setup(164) 回显请求尾 20 字节")

        # SETUP 第一步：ekey/eiv
        # 同时带上真实 iOS 会带的设备识别字段：model("iPhone15,2") / name / osName / osVersion，
        # 以及请求头 User-Agent —— 接收端就是靠这几个认出设备型号的。
        # ekey 也用向量里的那一份 —— 与上面的 keymsg 配对，
        # 接收端 fairplay_decrypt 出来的就是 decrypt_vec["key"]。
        ekey = bytes.fromhex(decrypt_vec["input"])
        eiv = bytes(((i * 29 + 11) & 0xFF) for i in range(16))
        setup1 = {"ekey": ekey, "eiv": eiv, "deviceID": "aa:bb:cc:dd:ee:ff",
                  "model": "iPhone15,2", "name": "Fake iPhone",
                  "osName": "iOS", "osVersion": "18.1",
                  "timingProtocol": "NTP", "timingPort": 7011}
        cseq += 1
        send_rtsp(conn, "SETUP", "rtsp://%s/%d" % (local_ip, port), cseq,
                  plistlib.dumps(setup1, fmt=plistlib.FMT_BINARY),
                  {"Content-Type": "application/x-apple-binary-plist",
                   "User-Agent": "AirPlay/770.8.1"})
        status, headers, body = recv_rtsp(f)
        check(status.startswith("RTSP/1.0 200"), "SETUP(ekey/eiv) 返回 200", status)
        try:
            setup_res = plistlib.loads(body)
        except Exception as exc:      # noqa: BLE001
            setup_res = {}
            check(False, "SETUP 响应能被 plistlib 解析", str(exc))
        check("timingPort" in setup_res, "SETUP 响应含 timingPort", setup_res)
        check(setup_res.get("eventPort") == 0, "SETUP 响应 eventPort=0", setup_res.get("eventPort"))

        # SETUP 第二步：streams（type 110 = 镜像视频）
        stream_id = 12345678901234
        setup2 = {"streams": [{"type": 110, "streamConnectionID": stream_id}],
                  "deviceID": "aa:bb:cc:dd:ee:ff"}
        cseq += 1
        send_rtsp(conn, "SETUP", "rtsp://%s/%d" % (local_ip, port), cseq,
                  plistlib.dumps(setup2, fmt=plistlib.FMT_BINARY),
                  {"Content-Type": "application/x-apple-binary-plist"})
        status, headers, body = recv_rtsp(f)
        check(status.startswith("RTSP/1.0 200"), "SETUP(streams) 返回 200", status)
        try:
            setup_res = plistlib.loads(body)
        except Exception as exc:      # noqa: BLE001
            setup_res = {}
            check(False, "SETUP(streams) 响应能被 plistlib 解析", str(exc))
        streams = setup_res.get("streams") or []
        check(len(streams) == 1 and streams[0].get("type") == 110,
              "SETUP 响应含 type=110 的流", streams)
        data_port = streams[0].get("dataPort") if streams else None
        check(isinstance(data_port, int) and data_port > 0, "dataPort 是有效端口", data_port)

        # 镜像流端口应当真的在监听（连上即证明）
        if data_port:
            try:
                mirror = socket.create_connection((local_ip, data_port), timeout=5)
                check(True, "镜像流端口 %d 可连接" % data_port)
                mirror.close()
            except Exception as exc:      # noqa: BLE001
                check(False, "镜像流端口可连接", str(exc))

        # RECORD
        cseq += 1
        send_rtsp(conn, "RECORD", "rtsp://%s/%d" % (local_ip, port), cseq,
                  headers={"RTP-Info": "seq=0"})
        status, headers, _ = recv_rtsp(f)
        check(status.startswith("RTSP/1.0 200"), "RECORD 返回 200", status)
        check(headers.get("audio-latency") == "11025", "RECORD 的 Audio-Latency=11025",
              headers.get("audio-latency"))
        check(headers.get("audio-jack-status") == "connected; type=analog",
              "RECORD 的 Audio-Jack-Status 是 analog", headers.get("audio-jack-status"))

        # SET_PARAMETER：音量
        cseq += 1
        send_rtsp(conn, "SET_PARAMETER", "rtsp://%s/%d" % (local_ip, port), cseq,
                  b"volume: -12.000000\r\n", {"Content-Type": "text/parameters"})
        status, _, _ = recv_rtsp(f)
        check(status.startswith("RTSP/1.0 200"), "SET_PARAMETER(volume) 返回 200", status)

        # GET_PARAMETER：保活 + 查音量
        cseq += 1
        send_rtsp(conn, "GET_PARAMETER", "rtsp://%s/%d" % (local_ip, port), cseq,
                  b"volume\r\n", {"Content-Type": "text/parameters"})
        status, headers, body = recv_rtsp(f)
        check(status.startswith("RTSP/1.0 200"), "GET_PARAMETER 返回 200", status)
        check(b"volume:" in body, "GET_PARAMETER 回了音量", body[:40])

        # POST /feedback：心跳
        cseq += 1
        send_rtsp(conn, "POST", "/feedback", cseq)
        status, _, _ = recv_rtsp(f)
        check(status.startswith("RTSP/1.0 200"), "POST /feedback 返回 200", status)

        # 发一段真实加密的镜像流（可选）。这让接收端产生实测码率，并真的解码出画面。
        # 关键：视频必须走**镜像流端口**，不能走 RTSP 控制连接
        # （控制连接上发二进制包会被 RTSP 解析器当成垃圾，接收端会直接断开）。
        if frames_path and os.path.exists(frames_path) and data_port:
            aes_key = bytes.fromhex(decrypt_vec["key"])
            video_seconds = max(3, hold - 2) if hold > 0 else 6
            try:
                mirror_sock = socket.create_connection((local_ip, data_port), timeout=8)
                send_mirror_video(mirror_sock, aes_key, stream_id, frames_path, video_seconds)
                mirror_sock.close()
                check(True, "发送真实加密镜像流", "%d 秒" % video_seconds)
            except Exception as exc:      # noqa: BLE001
                check(False, "发送真实加密镜像流", str(exc))

        # 保持会话：用于在"投屏进行中"检查界面。
        # 设备识别结果会在会话结束时被清掉，所以断言必须在还连着的时候做。
        if hold > 0:
            print("HOLDING %d" % hold, flush=True)
            time.sleep(hold)

        # TEARDOWN
        cseq += 1
        send_rtsp(conn, "TEARDOWN", "rtsp://%s/%d" % (local_ip, port), cseq,
                  plistlib.dumps({"streams": [{"type": 110}]}, fmt=plistlib.FMT_BINARY),
                  {"Content-Type": "application/x-apple-binary-plist"})
        status, headers, _ = recv_rtsp(f)
        check(status.startswith("RTSP/1.0 200"), "TEARDOWN 返回 200", status)
        check(headers.get("connection", "").lower() == "close", "TEARDOWN 要求关闭连接",
              headers.get("connection"))
    finally:
        try:
            f.close()
            conn.close()
        except Exception:      # noqa: BLE001
            pass

    print()
    print("==== fake iPhone: %d checks, %d failed ====" % (checks, len(failures)))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
