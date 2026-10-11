#!/usr/bin/env python3
"""Cross-validate the C# binary-plist implementation against Python's plistlib.

Python's plistlib is a completely independent bplist implementation, so it makes
a good oracle: it also lays objects out differently (it deduplicates strings and
orders the object table its own way), which catches readers that wrongly assume a
particular layout.

Two directions:
  read  : plistlib writes case_N.bplist -> the C# reader must reproduce the tagged JSON
  write : the C# writer emits case_N.out.bplist -> plistlib must read back the same values

Tagged JSON keeps int/real/bool/bytes distinguishable (plain JSON cannot):
  null | true/false | {"i":n} | {"r":x} | {"s":str} | {"d":hex} | {"a":[...]} | {"o":{k:v}}

Usage:
    python plist_roundtrip.py <cases.json> <bindir>          # generate inputs + expectations
    python plist_roundtrip.py --verify <cases.json> <bindir> # validate C#-written outputs
"""
import json
import math
import plistlib
import sys
from pathlib import Path


def tag(v):
    if v is None:
        return None
    if isinstance(v, bool):
        return v
    if isinstance(v, int):
        return {"i": v}
    if isinstance(v, float):
        return {"r": v}
    if isinstance(v, str):
        return {"s": v}
    if isinstance(v, (bytes, bytearray)):
        return {"d": bytes(v).hex()}
    if isinstance(v, list):
        return {"a": [tag(x) for x in v]}
    if isinstance(v, dict):
        return {"o": {k: tag(x) for k, x in v.items()}}
    raise TypeError("unsupported: %r" % type(v))


def untag(t):
    if t is None or isinstance(t, bool):
        return t
    if isinstance(t, dict):
        if "i" in t:
            return t["i"]
        if "r" in t:
            return t["r"]
        if "s" in t:
            return t["s"]
        if "d" in t:
            return bytes.fromhex(t["d"])
        if "a" in t:
            return [untag(x) for x in t["a"]]
        if "o" in t:
            return {k: untag(v) for k, v in t["o"].items()}
    raise ValueError("bad tag: %r" % (t,))


def same(a, b, path="", tol=1e-12):
    """Deep compare with float tolerance; returns None if equal else a description."""
    if isinstance(a, bool) or isinstance(b, bool):
        return None if a is b else "%s: bool %r != %r" % (path, a, b)
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        if isinstance(a, int) and isinstance(b, int):
            return None if a == b else "%s: int %r != %r" % (path, a, b)
        if math.isclose(float(a), float(b), rel_tol=tol, abs_tol=tol):
            return None
        return "%s: real %r != %r" % (path, a, b)
    if isinstance(a, bytes) and isinstance(b, bytes):
        return None if a == b else "%s: bytes differ (len %d vs %d)" % (path, len(a), len(b))
    if isinstance(a, str) and isinstance(b, str):
        return None if a == b else "%s: str %r != %r" % (path, a[:40], b[:40])
    if isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            return "%s: list len %d != %d" % (path, len(a), len(b))
        for i, (x, y) in enumerate(zip(a, b)):
            d = same(x, y, "%s[%d]" % (path, i), tol)
            if d:
                return d
        return None
    if isinstance(a, dict) and isinstance(b, dict):
        if set(a) != set(b):
            return "%s: keys differ: %r vs %r" % (path, sorted(a), sorted(b))
        for k in a:
            d = same(a[k], b[k], "%s.%s" % (path, k), tol)
            if d:
                return d
        return None
    if a is None and b is None:
        return None
    return "%s: type/值不同: %r vs %r" % (path, a, b)


def build_cases():
    long_name = "投屏Window 桌面版 AirPlay Receiver"          # 非 ASCII -> UTF-16
    many_keys = {"k%02d" % i: i for i in range(20)}            # 触发 0xF 计数路径
    return [
        {},
        {"a": 1, "b": "text", "c": True, "d": False},
        {"ekey": bytes(range(72)), "eiv": bytes(16)},
        {"streams": [{"type": 110, "streamConnectionID": 12345678901234}]},
        {"empty_data": b"", "one": b"\x00", "big": bytes(range(256))},
        list(range(20)),
        many_keys,
        {"name": long_name, "short": "pc"},
        {"big_u32": 4294967295, "big_u64": 18446744073709551615, "neg": -12345},
        {"reals": [0.0, 1.5, -30.0, 60.0, 0.016666666666666666]},
        {"nested": {"deep": {"deeper": [1, 2, {"x": b"\xde\xad\xbe\xef"}]}}},
        # 接近真实的 GET /info 响应结构
        {
            "deviceID": "aa:bb:cc:dd:ee:ff",
            "macAddress": "aa:bb:cc:dd:ee:ff",
            "pk": bytes.fromhex("b0" * 32),
            "features": 0x527FFEE6,
            "name": "PC@DESKTOP",
            "pi": "2e388006-13ba-4041-9a67-25dd4a43d536",
            "vv": 2,
            "statusFlags": 68,
            "keepAliveLowPower": 1,
            "sourceVersion": "220.68",
            "keepAliveSendStatsAsBody": True,
            "model": "AppleTV3,2",
            "initialVolume": -30.0,
            "audioLatencies": [
                {"type": 100, "inputLatencyMicros": 0, "audioType": "default",
                 "outputLatencyMicros": False},
                {"type": 101, "inputLatencyMicros": 0, "audioType": "default",
                 "outputLatencyMicros": False},
            ],
            "audioFormats": [
                {"type": 100, "audioInputFormats": 0x3FFFFFC, "audioOutputFormats": 0x3FFFFFC},
                {"type": 101, "audioInputFormats": 0x3FFFFFC, "audioOutputFormats": 0x3FFFFFC},
            ],
            "displays": [{
                "uuid": "e0ff8a27-6738-3d56-8a16-cc53aacee925",
                "widthPhysical": 0, "heightPhysical": 0,
                "width": 1920, "height": 1080,
                "widthPixels": 1920, "heightPixels": 1080,
                "rotation": False, "refreshRate": 0.016666666666666666,
                "maxFPS": 30, "overscanned": False, "features": 14,
            }],
        },
        # 长字符串（>15 字符）与长字节串，触发“长度用整数对象”的分支
        {"s20": "0123456789abcdefghij", "d20": bytes(range(20))},
    ]


def main():
    if len(sys.argv) >= 2 and sys.argv[1] == "--verify":
        cases_json, bindir = Path(sys.argv[2]), Path(sys.argv[3])
        expected = [untag(t) for t in json.loads(cases_json.read_text(encoding="utf-8"))]
        bad = 0
        for i, exp in enumerate(expected):
            p = bindir / ("case_%d.out.bplist" % i)
            if not p.exists():
                print("MISSING %s" % p.name)
                bad += 1
                continue
            got = plistlib.loads(p.read_bytes())
            d = same(exp, got, "case_%d" % i)
            if d:
                print("FAIL %s" % d)
                bad += 1
            else:
                print("ok   case_%d (%d bytes)" % (i, p.stat().st_size))
        print()
        print("==== write-direction: %d cases, %d bad ====" % (len(expected), bad))
        return 1 if bad else 0

    cases_json, bindir = Path(sys.argv[1]), Path(sys.argv[2])
    bindir.mkdir(parents=True, exist_ok=True)
    cases = build_cases()
    for i, c in enumerate(cases):
        (bindir / ("case_%d.bplist" % i)).write_bytes(
            plistlib.dumps(c, fmt=plistlib.FMT_BINARY, sort_keys=False))
    cases_json.write_text(
        json.dumps([tag(c) for c in cases], ensure_ascii=False, indent=1), encoding="utf-8")
    print("wrote %d cases -> %s / %s" % (len(cases), cases_json, bindir))
    return 0


if __name__ == "__main__":
    sys.exit(main())
