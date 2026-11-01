# -*- coding: utf-8 -*-
"""
Suzu app.ico 生成脚本（铃波 / Suzu）
用法：py tools/make_icon.py

档位（见 计划书/11-吉祥物图标制作方案书.md）：
  256/48 = Q 版半身裁剪（圆角方底，品牌蓝，露出全脸）  源图 suzu-halfbody.png
  32/16  = 珐琅徽章原图直接缩放                        源图 suzu-badge.png

输出：Assets/app.ico（16/32/48/256 四档；256 用 PNG 封装，其余用 BMP DIB，
Windows 对 <=48 的 PNG-in-ICO 支持不可靠，故手写 BMP）。
"""
import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ART = Path(r"D:\Users\Desktop\Suzu-立绘设计图")
OUT_ICO = ROOT / "Assets" / "app.ico"
PREVIEW = ART / "_icon-preview.png"

BRAND_BLUE = (0, 122, 204, 255)      # 与旧版图标同源的品牌蓝


def rounded_square(size, color, radius_ratio=0.12):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = int(size * radius_ratio)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=r, fill=color)
    return img


def make_chibi(size):
    """Q 版半身：头为主裁剪、露出全脸（下巴+微笑），内容整体偏上。"""
    src = Image.open(ART / "suzu-halfbody.png").convert("RGBA")
    w, h = src.size
    # 裁剪：下发际到 84% 露出下巴与微笑；粘贴时贴向上方，脸不压底边
    box = (int(w * 0.08), 0, int(w * 0.92), int(h * 0.84))
    head = src.crop(box)

    canvas = rounded_square(size, BRAND_BLUE)
    margin = int(size * 0.05)
    inner = size - margin * 2
    fit = head.copy()
    fit.thumbnail((inner, inner), Image.LANCZOS)
    x = (size - fit.width) // 2
    y = margin  # 顶对齐：脸的重心偏上，底部由裁剪框保底
    canvas.alpha_composite(fit, (x, y))
    return canvas


def make_badge(size):
    """珐琅徽章原图直接缩放（用户定稿：不重绘，忠实原图）。"""
    src = Image.open(ART / "suzu-badge.png").convert("RGBA")
    return src.resize((size, size), Image.LANCZOS)


# ---------- ICO 容器手工合成（256=PNG，16/32/48=BMP DIB） ----------

def png_bytes(img):
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


def bmp_dib_bytes(img):
    """BGRA 自底向上 + AND 掩码与 alpha 互补。

    ⚠️ 掩码不能全 1：任务栏/标题栏的合成路径在掩码全置位时会忽略 alpha 通道，
    把透明像素直接渲染成黑色（实测铃波徽章变黑圆）。正确做法：alpha>0 的像素
    掩码位 0（交给 alpha 混合），alpha==0 的像素掩码位 1（完全透明）。
    """
    w, h = img.size
    px = img.load()
    data = bytearray()
    and_stride = (w + 31) // 32 * 4
    and_mask = bytearray(and_stride * h)
    for y in range(h - 1, -1, -1):
        row_index = h - 1 - y          # AND 掩码同样自底向上
        for x in range(w):
            r, g, b, a = px[x, y]
            data += struct.pack("BBBB", b, g, r, a)
            if a == 0:
                and_mask[row_index * and_stride + x // 8] |= 0x80 >> (x % 8)
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(data) + len(and_mask),
                         0, 0, 0, 0)
    return header + bytes(data) + bytes(and_mask)


def build_ico(images, path):
    """images: {size: PIL.Image}。按 ICO 规范打包。"""
    entries = sorted(images.items(), key=lambda kv: kv[0])
    blobs = []
    for size, img in entries:
        raw = png_bytes(img) if size == 256 else bmp_dib_bytes(img)
        blobs.append((size, raw))

    header = struct.pack("<HHH", 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    directory = b""
    payload = b""
    for size, raw in blobs:
        directory += struct.pack("<BBBBHHII",
                                 size % 256, size % 256, 0, 0,
                                 1, 32, len(raw), offset)
        payload += raw
        offset += len(raw)
    Path(path).write_bytes(header + directory + payload)
    print(f"ICO 写入 {path}（{len(header + directory + payload)} 字节，档位 {list(images)}）")


def main():
    img_256 = make_chibi(256)
    img_48 = make_chibi(48)
    img_32 = make_badge(32)
    img_16 = make_badge(16)

    OUT_ICO.parent.mkdir(parents=True, exist_ok=True)
    build_ico({16: img_16, 32: img_32, 48: img_48, 256: img_256}, OUT_ICO)

    # 预览图：白/深两种底上排开，供目验
    bg1 = Image.new("RGBA", (280 * 2 + 40, 300), (255, 255, 255, 255))
    bg2 = Image.new("RGBA", (280 * 2 + 40, 300), (32, 32, 36, 255))
    for bg in (bg1, bg2):
        bg.alpha_composite(img_256.resize((256, 256), Image.LANCZOS), (12, 12))
        bg.alpha_composite(img_48.resize((96, 96), Image.LANCZOS), (280, 12))
        bg.alpha_composite(img_32.resize((64, 64), Image.LANCZOS), (388, 12))
        bg.alpha_composite(img_16.resize((32, 32), Image.LANCZOS), (464, 12))
    sheet = Image.new("RGBA", (bg1.width, 300 * 2 + 30), (128, 128, 128, 255))
    sheet.alpha_composite(bg1, (0, 0))
    sheet.alpha_composite(bg2, (0, 330))
    sheet.save(PREVIEW)
    print("预览写入", PREVIEW)


if __name__ == "__main__":
    sys.exit(main())
