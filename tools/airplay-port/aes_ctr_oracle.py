#!/usr/bin/env python3
"""Independent AES-128-CTR oracle for verifying the C# Aes128Ctr implementation.

There is no crypto library (and no OpenSSL) on this machine, so the oracle is built
from scratch: AES-128 (key expansion + rounds) is implemented here and FIRST validated
against the FIPS-197 known-answer vector. Only then is it used to assemble CTR in the
OpenSSL convention (IV used directly as a big-endian 128-bit counter, +1 per block),
which is what UxPlay's EVP_aes_128_ctr does.

Emits segments whose lengths split a plaintext across several Transform() calls, so the
C# side is checked for keystream CONTINUITY across packets (the mirroring stream shares
one keystream between all type-0x00 payloads).

Usage:
    python aes_ctr_oracle.py <out.json>
"""
import json
import os
import random
import sys


# ---------------------------------------------------------------- GF(2^8) helpers

def gmul(a, b):
    p = 0
    for _ in range(8):
        if b & 1:
            p ^= a
        hi = a & 0x80
        a = (a << 1) & 0xFF
        if hi:
            a ^= 0x1B
        b >>= 1
    return p


def ginv(a):
    if a == 0:
        return 0
    for x in range(1, 256):
        if gmul(a, x) == 1:
            return x
    raise AssertionError("no inverse for %d" % a)


def make_sbox():
    """Compute the AES S-box (inverse + affine transform) instead of transcribing 256 values."""
    out = []
    for a in range(256):
        x = ginv(a)
        s = 0
        for i in range(8):
            bit = ((x >> i) & 1) ^ ((x >> ((i + 4) % 8)) & 1) ^ ((x >> ((i + 5) % 8)) & 1) \
                  ^ ((x >> ((i + 6) % 8)) & 1) ^ ((x >> ((i + 7) % 8)) & 1)
            s |= bit << i
        out.append(s ^ 0x63)
    return out


SBOX = make_sbox()
RCON = [0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36]

# 自检：S-box 的几个公开已知值
assert SBOX[0x00] == 0x63, hex(SBOX[0x00])
assert SBOX[0x01] == 0x7C, hex(SBOX[0x01])
assert SBOX[0x53] == 0xED, hex(SBOX[0x53])
assert SBOX[0xFF] == 0x16, hex(SBOX[0xFF])


# ---------------------------------------------------------------- AES-128

def key_expansion(key):
    w = [list(key[i * 4:i * 4 + 4]) for i in range(4)]
    for i in range(4, 44):
        temp = list(w[i - 1])
        if i % 4 == 0:
            temp = temp[1:] + temp[:1]                       # RotWord
            temp = [SBOX[b] for b in temp]                   # SubWord
            temp[0] ^= RCON[i // 4 - 1]
        w.append([w[i - 4][j] ^ temp[j] for j in range(4)])
    return w


def xtime(a):
    return ((a << 1) ^ 0x1B) & 0xFF if a & 0x80 else (a << 1) & 0xFF


def sub_bytes(s):
    return [SBOX[b] for b in s]


def shift_rows(s):
    out = [0] * 16
    for r in range(4):
        for c in range(4):
            out[4 * c + r] = s[4 * ((c + r) % 4) + r]
    return out


def mix_columns(s):
    out = [0] * 16
    for c in range(4):
        a = s[4 * c:4 * c + 4]
        out[4 * c + 0] = xtime(a[0]) ^ (xtime(a[1]) ^ a[1]) ^ a[2] ^ a[3]
        out[4 * c + 1] = a[0] ^ xtime(a[1]) ^ (xtime(a[2]) ^ a[2]) ^ a[3]
        out[4 * c + 2] = a[0] ^ a[1] ^ xtime(a[2]) ^ (xtime(a[3]) ^ a[3])
        out[4 * c + 3] = (xtime(a[0]) ^ a[0]) ^ a[1] ^ a[2] ^ xtime(a[3])
    return out


def add_round_key(s, rk):
    return [s[i] ^ rk[i] for i in range(16)]


def aes128_encrypt_block(block, w):
    rk = [b for word in w[0:4] for b in word]
    s = add_round_key(list(block), rk)
    for rnd in range(1, 10):
        s = sub_bytes(s)
        s = shift_rows(s)
        s = mix_columns(s)
        rk = [b for word in w[4 * rnd:4 * rnd + 4] for b in word]
        s = add_round_key(s, rk)
    s = sub_bytes(s)
    s = shift_rows(s)
    rk = [b for word in w[40:44] for b in word]
    return bytes(add_round_key(s, rk))


def ctr_keystream(key, iv, length):
    """OpenSSL convention: block k = AES_ECB_Encrypt(IV + k) with a big-endian 128-bit counter."""
    w = key_expansion(key)
    counter = int.from_bytes(iv, "big")
    out = bytearray()
    while len(out) < length:
        out += aes128_encrypt_block((counter % (1 << 128)).to_bytes(16, "big"), w)
        counter = (counter + 1) % (1 << 128)
    return bytes(out[:length])


# ---------------------------------------------------------------- KAT

FIPS197_KEY = bytes.fromhex("000102030405060708090a0b0c0d0e0f")
FIPS197_PT = bytes.fromhex("00112233445566778899aabbccddeeff")
FIPS197_CT = bytes.fromhex("69c4e0d86a7b0430d8cdb78070b4c55a")

got = aes128_encrypt_block(FIPS197_PT, key_expansion(FIPS197_KEY))
if got != FIPS197_CT:
    raise SystemExit("FIPS-197 KAT FAILED: %s != %s" % (got.hex(), FIPS197_CT.hex()))
print("FIPS-197 KAT ok: AES-128(%s) = %s" % (FIPS197_PT.hex(), got.hex()))

# NIST SP 800-38A CTR 向量（打印出来供人工比对）
NIST_KEY = bytes.fromhex("2b7e151628aed2a6abf7158809cf4f3c")
NIST_IV = bytes.fromhex("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff")
NIST_PT = bytes.fromhex(
    "6bc1bee22e409f96e93d7e117393172a"
    "ae2d8a571e03ac9c9eb76fac45af8e51"
    "30c81c46a35ce411e5fbc1191a0a52ef"
    "f69f2445df4f9b17ad2b417be66c3710")
nist_ct = bytes(a ^ b for a, b in zip(NIST_PT, ctr_keystream(NIST_KEY, NIST_IV, len(NIST_PT))))
print("NIST SP800-38A CTR block 1 = %s" % nist_ct[:16].hex())
print("  (published value      = 874d6191b620e3261bef6864990db6ce)")


# ---------------------------------------------------------------- vectors

def make_case(rng, total, segments):
    key = bytes(rng.randrange(256) for _ in range(16))
    iv = bytes(rng.randrange(256) for _ in range(16))
    pt = bytes(rng.randrange(256) for _ in range(total))
    ks = ctr_keystream(key, iv, total)
    ct = bytes(a ^ b for a, b in zip(pt, ks))
    # 分段长度之和必须等于 total
    assert sum(segments) == total, (segments, total)
    return {
        "key": key.hex(),
        "iv": iv.hex(),
        "segments": segments,
        "plaintext": pt.hex(),
        "ciphertext": ct.hex(),
    }


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "aes-vectors.json"
    rng = random.Random(20261010)

    cases = [
        # 单次调用
        make_case(rng, 64, [64]),
        # 逐块调用：验证密钥流跨调用连续
        make_case(rng, 64, [16, 16, 16, 16]),
        # 非对齐分段 + 空段：镜像流里很常见的形态
        make_case(rng, 100, [0, 1, 15, 1, 32, 0, 51]),
        # 跨过 16 字节边界且首尾都不整
        make_case(rng, 37, [3, 29, 5]),
        # 计数器低位进位（构造 IV 末尾为 0xff）
        {
            "key": bytes(rng.randrange(256) for _ in range(16)).hex(),
            "iv": (bytes(15) + b"\xff").hex(),
            "segments": [16, 16, 16],
            "plaintext": bytes(rng.randrange(256) for _ in range(48)).hex(),
            "ciphertext": "",
        },
    ]
    # 补上最后一个用例的密文（IV 末字节 0xff，会触发进位）
    last = cases[-1]
    key = bytes.fromhex(last["key"])
    iv = bytes.fromhex(last["iv"])
    pt = bytes.fromhex(last["plaintext"])
    last["ciphertext"] = bytes(a ^ b for a, b in zip(pt, ctr_keystream(key, iv, len(pt)))).hex()

    doc = {
        "note": "generated by aes_ctr_oracle.py; AES-128 validated against FIPS-197, CTR per OpenSSL convention",
        "fips197": {"key": FIPS197_KEY.hex(), "plaintext": FIPS197_PT.hex(), "ciphertext": FIPS197_CT.hex()},
        "nistCtrBlock1": nist_ct[:16].hex(),
        "ctr": cases,
    }
    with open(out, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1)
    print("wrote %s (%d CTR cases)" % (out, len(cases)))


if __name__ == "__main__":
    main()
