"""
生成「投屏Window」应用图标：
  深色圆角底 + 白色显示器符号（镜像语义），无渐变、无细碎细节，16px 下依然清晰。
输出：src/CastReceiver.App/app.ico（多尺寸）+ docs/icon.png（预览）
"""
from PIL import Image, ImageDraw
import os

OUT_ICO = os.path.join("src", "CastReceiver.App", "app.ico")
OUT_PNG = os.path.join("docs", "icon.png")

BG = (22, 24, 28, 255)        # #16181C
BORDER = (42, 47, 54, 255)    # #2A2F36
FG = (236, 237, 239, 255)     # #ECEDEF


def render(size: int) -> Image.Image:
    s = size * 8  # 8 倍超采样后缩放，边缘更干净
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    radius = int(s * 0.22)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=radius, fill=BG,
                        outline=BORDER, width=max(1, int(s * 0.012)))

    # 显示器主体（白色实心圆角矩形）
    mw, mh = int(s * 0.60), int(s * 0.40)
    mx, my = (s - mw) // 2, int(s * 0.27)
    d.rounded_rectangle([mx, my, mx + mw, my + mh], radius=int(s * 0.055), fill=FG)

    # 底座
    bw, bh = int(s * 0.34), int(s * 0.055)
    bx, by = (s - bw) // 2, my + mh + int(s * 0.055)
    d.rounded_rectangle([bx, by, bx + bw, by + bh], radius=int(bh / 2), fill=FG)

    # 屏幕里挖出深色“播放三角”，增加识别度（小尺寸下也不糊）
    tri = int(s * 0.115)
    cx, cy = s // 2, my + mh // 2
    d.polygon([(cx - tri // 2, cy - tri), (cx - tri // 2, cy + tri), (cx + tri, cy)], fill=BG)

    return img.resize((size, size), Image.LANCZOS)


sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16]
images = [render(n) for n in sizes]
images[0].save(OUT_ICO, format="ICO", sizes=[(n, n) for n in sizes], append_images=images[1:])
os.makedirs(os.path.dirname(OUT_PNG), exist_ok=True)
render(256).save(OUT_PNG)
print("已生成：", OUT_ICO, "尺寸:", sizes)
print("预览：", OUT_PNG)