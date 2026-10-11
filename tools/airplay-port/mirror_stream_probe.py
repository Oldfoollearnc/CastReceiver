#!/usr/bin/env python3
"""Feed the C# mirroring receiver a synthetic, independently-encrypted stream.

The ciphertext is produced by the AES-CTR oracle in aes_ctr_oracle.py (itself validated
against the FIPS-197 known-answer vector and the published NIST SP 800-38A CTR vector),
so the C# side has to get several things right at once:

  * 128-byte header framing (little-endian payload_size, type at [4], LE u64 timestamp at [8])
  * one CONTINUOUS AES-128-CTR keystream across successive type-0x00 payloads
    (the plaintext 0x01 codec packet must NOT advance it)
  * AVCC -> Annex-B rewriting of the 4-byte length prefixes
  * SPS/PPS injection in front of the next video packet with a matching timestamp

Usage:
    python mirror_stream_probe.py <localIp> <hexKey> <hexIv>
"""
import socket
import struct
import sys
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from aes_ctr_oracle import ctr_keystream   # noqa: E402

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


def header(payload_size, ptype, subtype, option_low, option_high, timestamp):
    h = bytearray(128)
    struct.pack_into("<I", h, 0, payload_size)
    h[4] = ptype
    h[5] = subtype
    h[6] = option_low
    h[7] = option_high
    struct.pack_into("<Q", h, 8, timestamp)
    return bytes(h)


def build_codec_config(sps, pps, timestamp):
    """0x01 包：6 字节配置头 + BE16 sps 长度 + SPS + pps 个数 + BE16 pps 长度 + PPS。"""
    p = bytearray(6)
    p[0] = 0x01
    p[1] = 0x64          # profile high
    p[2] = 0x00          # compatibility
    p[3] = 0x1F          # level
    p[4] = 0x00
    p[5] = 0x00
    p += struct.pack(">H", len(sps)) + sps
    p += bytes([1])                      # number_of_pps
    p += struct.pack(">H", len(pps)) + pps
    return header(len(p), 0x01, 0x00, 0x16, 0x01, timestamp) + bytes(p)


def build_video(nalus, timestamp, subtype):
    """AVCC 形式的 0x00 包：每段 [BE32 长度][NAL]，整体加密。"""
    plain = b""
    for n in nalus:
        plain += struct.pack(">I", len(n)) + n
    return plain


def main():
    if len(sys.argv) < 4:
        raise SystemExit(__doc__)
    local_ip, key_hex, iv_hex = sys.argv[1], sys.argv[2], sys.argv[3]
    key, iv = bytes.fromhex(key_hex), bytes.fromhex(iv_hex)

    port = int(os.environ["MIRROR_PORT"])
    ts_config = 0x1122334455667788
    ts_video = 0x1122334455667788

    sps = bytes([0x67, 0x64, 0x00, 0x1F, 0xAC, 0xD9, 0x40])
    pps = bytes([0x68, 0xEB, 0xE3, 0xCB, 0x22, 0xC0])
    idr = bytes([0x65, 0x88, 0x84, 0x00, 0x33, 0xFF, 0x11, 0x22])
    nonidr = bytes([0x41, 0x9A, 0x02, 0x34, 0x56, 0x78])
    aud = bytes([0x09, 0x10])

    # 第二个视频包（非 IDR，两个 NAL）—— 密钥流必须从这里接着往下走
    p1 = build_video([idr], ts_video, 0x10)
    p2 = build_video([aud, nonidr], ts_video + 1, 0x00)

    keystream = ctr_keystream(key, iv, len(p1) + len(p2))
    c1 = bytes(a ^ b for a, b in zip(p1, keystream[:len(p1)]))
    c2 = bytes(a ^ b for a, b in zip(p2, keystream[len(p1):len(p1) + len(p2)]))

    sock = socket.create_connection((local_ip, port), timeout=8)
    try:
        # 配置包是明文，不推进密钥流
        sock.sendall(build_codec_config(sps, pps, ts_config))
        sock.sendall(header(len(c1), 0x00, 0x10, 0x00, 0x00, ts_video) + c1)
        # 中间插一个明文报表包，验证它同样不推进密钥流
        sock.sendall(header(0, 0x05, 0x00, 0x00, 0x00, 0))
        sock.sendall(header(len(c2), 0x00, 0x00, 0x00, 0x00, ts_video + 1) + c2)
        # 故意发一个解密后不像 H.264 的载荷，验证被计入无效而不是崩掉
        bad = b"\x7f\xff\xff\xff" + bytes(8)
        bad_ks = ctr_keystream(key, iv, len(p1) + len(p2) + len(bad))[len(p1) + len(p2):]
        bad_c = bytes(a ^ b for a, b in zip(bad, bad_ks))
        sock.sendall(header(len(bad_c), 0x00, 0x00, 0x00, 0x00, ts_video + 2) + bad_c)
        import time
        time.sleep(1.5)
    finally:
        sock.close()

    # 期望的 Annex-B 帧
    start = b"\x00\x00\x00\x01"
    expected_1 = start + sps + start + pps + start + idr
    expected_2 = start + aud + start + nonidr

    print("info 已发送；期望帧 1 = %s" % expected_1.hex())
    print("info 期望帧 2 = %s" % expected_2.hex())
    print("EXPECT1=%s" % expected_1.hex())
    print("EXPECT2=%s" % expected_2.hex())

    print()
    print("==== probe sent: %d checks, %d failed ====" % (checks, len(failures)))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
