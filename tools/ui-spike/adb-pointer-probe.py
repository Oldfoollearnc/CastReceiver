#!/usr/bin/env python3
"""Locate the Android "pointer location" mark by diffing two screencaps.

Why this exists: we need to know *where* a tap actually landed on the phone,
without OCR and without trusting the tool that sent it. Android's developer
option "Pointer location" (Settings.System pointer_location) marks the touch
point; on Android 14 it draws a small cross/dot (a few pixels wide) at the touch
point plus a header bar with counters at the top of the screen.

So: capture before, send the tap, capture after, and every changed pixel blob
*below the header* is a touch mark. The blob's centre is where the touch landed
— measured from the device's own framebuffer, in the device's current display
coordinates (which is exactly the space `input tap` is documented to use).

usage: adb-pointer-probe.py <before.png> <after.png> [header_px]
prints one line per blob, biggest first:  "BLOB <x> <y> size=<n> bbox=<x0,y0,x1,y1>"
exit 0 when at least one blob was found.
"""
import sys

import numpy as np
from PIL import Image


def blobs(mask):
    """Flood-fill the changed-pixel mask into blobs (no scipy needed)."""
    height, width = mask.shape
    seen = np.zeros_like(mask, dtype=bool)
    found = []
    for y in range(height):
        xs = np.where(mask[y] & ~seen[y])[0]
        for x in xs:
            if seen[y, x]:
                continue
            stack = [(int(x), y)]
            seen[y, x] = True
            pixels = []
            while stack:
                cx, cy = stack.pop()
                pixels.append((cx, cy))
                for nx, ny in ((cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1)):
                    if 0 <= nx < width and 0 <= ny < height and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        stack.append((nx, ny))
            if len(pixels) >= 3:
                px = [p[0] for p in pixels]
                py = [p[1] for p in pixels]
                found.append({
                    "size": len(pixels),
                    "x": sum(px) / len(px),
                    "y": sum(py) / len(py),
                    "bbox": (min(px), min(py), max(px), max(py)),
                })
    found.sort(key=lambda b: -b["size"])
    return found


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    before, after = sys.argv[1], sys.argv[2]
    header_px = int(sys.argv[3]) if len(sys.argv) > 3 else 90

    with Image.open(before) as img:
        a = np.asarray(img.convert("RGB"), dtype=np.int16)
    with Image.open(after) as img:
        b = np.asarray(img.convert("RGB"), dtype=np.int16)
    if a.shape != b.shape:
        print("SHAPE-MISMATCH %s vs %s" % (a.shape, b.shape))
        return 1

    diff = np.abs(a - b).sum(axis=2) > 24
    diff[:header_px, :] = False          # 顶部是计数器表头，不算触摸标记

    found = blobs(diff)
    if not found:
        print("NO-BLOB changed=%d" % int(diff.sum()))
        return 1

    # 触摸标记长什么样：一个小方块（Android 14 的 PointerLocationView 画的是 4x4）。
    # 手机上别的东西也在动（点一下可能打开某个界面），那些是大块/长条 —— 必须区分开，
    # 否则"点没点中"会被误判成"点在别处"。
    def is_mark(b):
        x0, y0, x1, y1 = b["bbox"]
        return b["size"] <= 64 and (x1 - x0) <= 10 and (y1 - y0) <= 10

    marks = [b for b in found if is_mark(b)]
    others = [b for b in found if not is_mark(b)]
    for blob in marks[:8]:
        print("MARK %.1f %.1f size=%d bbox=%d,%d,%d,%d"
              % (blob["x"], blob["y"], blob["size"], *blob["bbox"]))
    for blob in others[:8]:
        print("BLOB %.1f %.1f size=%d bbox=%d,%d,%d,%d"
              % (blob["x"], blob["y"], blob["size"], *blob["bbox"]))
    return 0 if marks else 1


if __name__ == "__main__":
    sys.exit(main())
