#!/usr/bin/env python3
"""Extract a real H.264 Annex-B stream (plus per-frame boundaries) from an MP4.

There is no ffmpeg on this machine, so this parses the MP4 box tree directly:
moov/trak/mdia/minf/stbl -> stsd(avc1/avcC for SPS+PPS), stsz, stsc, stco/co64, stts.
Each sample is then rewritten from AVCC (4-byte big-endian NAL length prefixes) to
Annex-B, with SPS/PPS injected in front of the first frame and of every IDR — the same
shape the AirPlay receiver produces.

Output (for tools/airplay-port/render-test):
    magic   "APH264\\0\\0"  (8 bytes)
    u32     frameCount
    per frame: u32 payloadLen, u64 timestampQ32_32, payload bytes

Usage:
    python mp4_extract_h264.py <input.mp4> <out.bin>
"""
import struct
import sys


def read_boxes(data, start, end):
    """Yield (type, payload_start, payload_end) for boxes in [start, end)."""
    pos = start
    while pos + 8 <= end:
        size = int.from_bytes(data[pos:pos + 4], "big")
        typ = data[pos + 4:pos + 8].decode("latin1")
        header = 8
        if size == 1:
            if pos + 16 > end:
                break
            size = int.from_bytes(data[pos + 8:pos + 16], "big")
            header = 16
        elif size == 0:
            size = end - pos
        if size < header or pos + size > end:
            break
        yield typ, pos + header, pos + size
        pos += size


def find_box(data, start, end, path):
    """Descend through a chain of box types; returns (payload_start, payload_end) or None."""
    cur_start, cur_end = start, end
    for want in path:
        found = None
        for typ, ps, pe in read_boxes(data, cur_start, cur_end):
            if typ == want:
                found = (ps, pe)
                break
        if found is None:
            return None
        cur_start, cur_end = found
    return cur_start, cur_end


def parse_stsd_video(data, start, end):
    """From an stsd payload, return (avcC_payload_slice, width, height) for the first avc1."""
    for typ, ps, pe in read_boxes(data, start + 8, end):   # skip version/flags + entry_count
        if typ != "avc1":
            continue
        width = int.from_bytes(data[ps + 24:ps + 26], "big")
        height = int.from_bytes(data[ps + 26:ps + 28], "big")
        # 视觉采样条目固定 78 字节头部，其后是子 box（avcC 就在里面）
        for ctyp, cps, cpe in read_boxes(data, ps + 78, pe):
            if ctyp == "avcC":
                return (cps, cpe), width, height
    return None, 0, 0


def parse_avcc(data, start, end):
    """Return (sps_list, pps_list, nal_length_size)."""
    pos = start
    pos += 1                       # configurationVersion
    pos += 3                       # profile, compatibility, level
    length_size = (data[pos] & 0x03) + 1
    pos += 1
    num_sps = data[pos] & 0x1F
    pos += 1
    sps = []
    for _ in range(num_sps):
        ln = int.from_bytes(data[pos:pos + 2], "big")
        pos += 2
        sps.append(data[pos:pos + ln])
        pos += ln
    num_pps = data[pos]
    pos += 1
    pps = []
    for _ in range(num_pps):
        ln = int.from_bytes(data[pos:pos + 2], "big")
        pos += 2
        pps.append(data[pos:pos + ln])
        pos += ln
    return sps, pps, length_size


def read_entries(data, start, widths):
    """Read entry_count entries of the given per-field widths (bytes)."""
    count = int.from_bytes(data[start + 4:start + 8], "big")
    pos = start + 8
    out = []
    for _ in range(count):
        row = []
        for w in widths:
            row.append(int.from_bytes(data[pos:pos + w], "big"))
            pos += w
        out.append(tuple(row))
    return out


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    src, dst = sys.argv[1], sys.argv[2]

    with open(src, "rb") as f:
        data = f.read()

    moov = find_box(data, 0, len(data), ["moov"])
    if moov is None:
        raise SystemExit("no moov box")

    video = None
    for typ, ts, te in read_boxes(data, moov[0], moov[1]):
        if typ != "trak":
            continue
        stbl = find_box(data, ts, te, ["mdia", "minf", "stbl"])
        if stbl is None:
            continue
        mdhd = find_box(data, ts, te, ["mdia", "mdhd"])
        timescale = 0
        if mdhd:
            version = data[mdhd[0]]
            if version == 1:
                timescale = int.from_bytes(data[mdhd[0] + 20:mdhd[0] + 24], "big")
            else:
                timescale = int.from_bytes(data[mdhd[0] + 12:mdhd[0] + 16], "big")
        stsd = find_box(data, stbl[0], stbl[1], ["stsd"])
        if stsd is None:
            continue
        avcc, width, height = parse_stsd_video(data, stsd[0], stsd[1])
        if avcc is None:
            continue
        video = {"stbl": stbl, "avcc": avcc, "timescale": timescale or 90000,
                 "width": width, "height": height}
        break

    if video is None:
        raise SystemExit("no avc1 video track found")

    stbl = video["stbl"]
    stsz = find_box(data, stbl[0], stbl[1], ["stsz"])
    stsc = find_box(data, stbl[0], stbl[1], ["stsc"])
    stco = find_box(data, stbl[0], stbl[1], ["stco"])
    co64 = find_box(data, stbl[0], stbl[1], ["co64"])
    stts = find_box(data, stbl[0], stbl[1], ["stts"])

    # stsz：sample_size==0 时，每个样本的大小逐个列在表里
    sample_size = int.from_bytes(data[stsz[0] + 4:stsz[0] + 8], "big")
    sample_count = int.from_bytes(data[stsz[0] + 8:stsz[0] + 12], "big")
    if sample_size == 0:
        sizes = [int.from_bytes(data[stsz[0] + 12 + i * 4:stsz[0] + 16 + i * 4], "big")
                 for i in range(sample_count)]
    else:
        sizes = [sample_size] * sample_count

    stsc_entries = read_entries(data, stsc[0], [4, 4, 4])
    if co64:
        chunk_offsets = [r[0] for r in read_entries(data, co64[0], [8])]
    else:
        chunk_offsets = [r[0] for r in read_entries(data, stco[0], [4])]

    # stts：把每个样本的解码时间累出来
    times = []
    tick = 0
    for count, delta in read_entries(data, stts[0], [4, 4]):
        for _ in range(count):
            times.append(tick)
            tick += delta
    while len(times) < sample_count:
        times.append(tick)
        tick += times[-2] - times[-3] if len(times) > 2 else 1

    # 展开 stsc：得到每个 chunk 的样本数
    per_chunk = []
    for i, (first_chunk, samples_per_chunk, _) in enumerate(stsc_entries):
        last = stsc_entries[i + 1][0] - 1 if i + 1 < len(stsc_entries) else len(chunk_offsets)
        for _ in range(first_chunk, last + 1):
            per_chunk.append(samples_per_chunk)

    sps_list, pps_list, nal_len = parse_avcc(data, video["avcc"][0], video["avcc"][1])
    start_code = b"\x00\x00\x00\x01"
    config = b"".join(start_code + s for s in sps_list) + b"".join(start_code + p for p in pps_list)

    frames = []
    sample_index = 0
    timescale = video["timescale"]
    for chunk_index, chunk_offset in enumerate(chunk_offsets):
        offset = chunk_offset
        n = per_chunk[chunk_index] if chunk_index < len(per_chunk) else 0
        for _ in range(n):
            if sample_index >= sample_count:
                break
            size = sizes[sample_index]
            raw = data[offset:offset + size]
            offset += size

            # AVCC -> Annex-B
            out = bytearray()
            pos = 0
            has_idr = False
            while pos + nal_len <= len(raw):
                ln = int.from_bytes(raw[pos:pos + nal_len], "big")
                pos += nal_len
                if ln <= 0 or pos + ln > len(raw):
                    break
                nal = raw[pos:pos + ln]
                pos += ln
                if nal and (nal[0] & 0x1F) == 5:
                    has_idr = True
                out += start_code + nal

            if sample_index == 0 or has_idr:
                out = bytearray(config) + out

            ticks = times[sample_index] if sample_index < len(times) else 0
            q32 = (ticks << 32) // timescale
            frames.append((q32, bytes(out)))
            sample_index += 1

    with open(dst, "wb") as f:
        f.write(b"APH264\x00\x00")
        f.write(struct.pack("<I", len(frames)))
        for q32, payload in frames:
            f.write(struct.pack("<I", len(payload)))
            f.write(struct.pack("<Q", q32))
            f.write(payload)

    total = sum(len(p) for _, p in frames)
    print("video: %dx%d timescale=%d nal_len=%d" % (video["width"], video["height"], timescale, nal_len))
    print("SPS %d, PPS %d" % (len(sps_list), len(pps_list)))
    print("frames=%d bytes=%d -> %s" % (len(frames), total, dst))
    if frames:
        print("frame0 head=%s" % frames[0][1][:24].hex())


if __name__ == "__main__":
    main()
