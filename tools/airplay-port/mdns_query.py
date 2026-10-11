#!/usr/bin/env python3
"""Act as an mDNS client (what an iPhone does) and validate the C# responder on the wire.

Sends real DNS-SD queries to 224.0.0.251:5353 from an ephemeral port (so the responder
must answer unicast, i.e. the "legacy query" path), then parses the replies with an
independent DNS decoder and asserts the AirPlay-relevant values.

Usage:
    python mdns_query.py <localIp> [expectedPort] [expectedServerName] [expectedMac]
"""
import socket
import struct
import sys

MCAST = "224.0.0.251"
TYPE_A, TYPE_PTR, TYPE_TXT, TYPE_SRV = 1, 12, 16, 33

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


def encode_name(name):
    out = b""
    for label in name.split("."):
        if not label:
            continue
        b = label.encode("utf-8")
        out += bytes([len(b)]) + b
    return out + b"\x00"


def build_query(name, qtype=TYPE_PTR):
    return struct.pack(">HHHHHH", 0, 0, 1, 0, 0, 0) + encode_name(name) + struct.pack(">HH", qtype, 1)


def read_name(data, off):
    labels = []
    jumped = False
    end = off
    hops = 0
    while off < len(data):
        ln = data[off]
        if ln == 0:
            off += 1
            break
        if ln & 0xC0 == 0xC0:
            if off + 1 >= len(data):
                break
            ptr = ((ln & 0x3F) << 8) | data[off + 1]
            if not jumped:
                end = off + 2
                jumped = True
            off = ptr
            hops += 1
            if hops > 32:
                break
            continue
        off += 1
        labels.append(data[off:off + ln].decode("utf-8", "replace"))
        off += ln
    return ".".join(labels), (end if jumped else off)


def parse(data):
    _tid, _flags, qd, an, ns, ar = struct.unpack(">HHHHHH", data[:12])
    off = 12
    for _ in range(qd):
        _, off = read_name(data, off)
        off += 4
    records = []
    for _ in range(an + ns + ar):
        name, off = read_name(data, off)
        rtype, rclass, ttl, rdlen = struct.unpack(">HHIH", data[off:off + 10])
        off += 10
        rd_start = off
        rdata = data[off:off + rdlen]
        off += rdlen
        rec = {"name": name, "type": rtype, "ttl": ttl, "flush": bool(rclass & 0x8000)}
        if rtype == TYPE_PTR:
            rec["target"], _ = read_name(data, rd_start)
        elif rtype == TYPE_SRV:
            prio, weight, port = struct.unpack(">HHH", rdata[:6])
            target, _ = read_name(data, rd_start + 6)
            rec.update(priority=prio, weight=weight, port=port, target=target)
        elif rtype == TYPE_TXT:
            items, i = [], 0
            while i < len(rdata):
                ln = rdata[i]
                i += 1
                items.append(rdata[i:i + ln].decode("utf-8", "replace"))
                i += ln
            rec["txt"] = items
        elif rtype == TYPE_A:
            rec["addr"] = socket.inet_ntoa(rdata)
        records.append(rec)
    return records


def query(sock, name, qtype=TYPE_PTR, timeout=3.0):
    sock.settimeout(timeout)
    sock.sendto(build_query(name, qtype), (MCAST, 5353))
    collected = []
    deadline = timeout
    while deadline > 0:
        try:
            data, _ = sock.recvfrom(9000)
        except socket.timeout:
            break
        collected.extend(parse(data))
        deadline -= 0.4
    return collected


def txt_dict(records, instance):
    for r in records:
        if r["type"] == TYPE_TXT and r["name"].lower() == instance.lower():
            out = {}
            for item in r.get("txt", []):
                if "=" in item:
                    k, v = item.split("=", 1)
                    out[k] = v
                else:
                    out[item] = ""
            return out, r
    return None, None


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    local_ip = sys.argv[1]
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 7000
    server = sys.argv[3] if len(sys.argv) > 3 else "投屏Window"
    mac = sys.argv[4] if len(sys.argv) > 4 else "aa:bb:cc:dd:ee:ff"

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 255)
    sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(local_ip))
    sock.bind((local_ip, 0))
    try:
        # ---- 1. _services._dns-sd._udp.local: 必须列出两种服务类型
        recs = query(sock, "_services._dns-sd._udp.local")
        targets = {r["target"].lower() for r in recs if r["type"] == TYPE_PTR}
        check("_airplay._tcp.local" in targets, "_services 列出 _airplay", sorted(targets))
        check("_raop._tcp.local" in targets, "_services 列出 _raop", sorted(targets))

        # ---- 2. _airplay._tcp.local: PTR + SRV + TXT + A
        recs = query(sock, "_airplay._tcp.local")
        instance = "%s._airplay._tcp.local" % server
        ptrs = [r for r in recs if r["type"] == TYPE_PTR and r["name"].lower() == "_airplay._tcp.local"]
        check(len(ptrs) >= 1, "_airplay PTR 存在", [r.get("target") for r in recs if r["type"] == TYPE_PTR])
        check(any(r["target"] == instance for r in ptrs), "_airplay PTR 指向本机实例",
              [r["target"] for r in ptrs])
        check(all(not r["flush"] for r in ptrs), "_airplay PTR 不带 cache-flush")

        srvs = [r for r in recs if r["type"] == TYPE_SRV and r["name"] == instance]
        check(len(srvs) == 1, "_airplay SRV 存在", [r["name"] for r in recs if r["type"] == TYPE_SRV])
        if srvs:
            check(srvs[0]["port"] == port, "_airplay SRV 端口 = %d" % port, srvs[0]["port"])
            check(srvs[0]["target"].endswith(".local"), "_airplay SRV 目标以 .local 结尾",
                  srvs[0]["target"])
            check(srvs[0]["flush"], "_airplay SRV 带 cache-flush")
            check(srvs[0]["ttl"] == 120, "_airplay SRV TTL=120", srvs[0]["ttl"])

        txt, txtrec = txt_dict(recs, instance)
        check(txt is not None, "_airplay TXT 存在")
        if txt:
            check(txt.get("features") == "0x527FFEE6,0x0",
                  "features = 0x527FFEE6,0x0（第27位=0，跳过配对）", txt.get("features"))
            check(txt.get("model") == "AppleTV3,2", "model = AppleTV3,2", txt.get("model"))
            check(txt.get("deviceid") == mac, "deviceid = %s" % mac, txt.get("deviceid"))
            check(txt.get("vv") == "2", "vv = 2", txt.get("vv"))
            check(txt.get("srcvers") == "220.68", "srcvers = 220.68", txt.get("srcvers"))
            check(txt.get("pi") == "2e388006-13ba-4041-9a67-25dd4a43d536", "pi 固定值", txt.get("pi"))
            check(txt.get("flags") == "0x4", "flags = 0x4", txt.get("flags"))
            check(len(txt.get("pk", "")) == 64, "pk 是 64 个十六进制字符", len(txt.get("pk", "")))
            check(txtrec["ttl"] == 4500, "_airplay TXT TTL=4500", txtrec["ttl"])
            check(txtrec["flush"], "_airplay TXT 带 cache-flush")

        addrs = [r for r in recs if r["type"] == TYPE_A]
        check(any(r["addr"] == local_ip for r in addrs), "A 记录 = 本机地址", [r["addr"] for r in addrs])

        # ---- 3. _raop._tcp.local: 检查 raop 专属 TXT
        recs = query(sock, "_raop._tcp.local")
        raop_instance = "%s@%s._raop._tcp.local" % ("AABBCCDDEEFF", server)
        txt, _ = txt_dict(recs, raop_instance)
        check(txt is not None, "_raop TXT 存在（实例名含大写 MAC）",
              [r["name"] for r in recs if r["type"] == TYPE_TXT])
        if txt:
            check(txt.get("ft") == "0x527FFEE6,0x0", "_raop ft = features", txt.get("ft"))
            check(txt.get("am") == "AppleTV3,2", "_raop am = model", txt.get("am"))
            check(txt.get("rhd") == "5.6.0.0", "_raop rhd", txt.get("rhd"))
            check(txt.get("txtvers") == "1", "_raop txtvers", txt.get("txtvers"))
            check(txt.get("sr") == "44100", "_raop sr", txt.get("sr"))
            check(txt.get("tp") == "UDP", "_raop tp", txt.get("tp"))
    finally:
        sock.close()

    print()
    print("==== mDNS: %d checks, %d failed ====" % (checks, len(failures)))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
