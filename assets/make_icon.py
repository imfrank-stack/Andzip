"""Andzip 程序图标：由 assets/andzip.png 生成 assets/andzip.ico。

用法：python assets/make_icon.py
输出：assets/andzip.ico（16 / 24 / 32 / 48 / 64 / 128 / 256 七档）

图源是 assets/andzip.png —— 一张 512×512、四角 alpha=0 的透明底图。
这里不再自己画几何（1.6.0 之前是代码画的拉链方块），只做三件事：

1. 按 alpha 外接框裁掉四周空白，再按每一档的边距放回格子里。
   不裁的话 16px 上有效画面只剩 11px，图标会在任务栏里显得又小又虚。
2. 小尺寸（≤32）用"贴边"档：边距比大图小一档，让画面尽量占满。
   大图自带的软投影在 16px 上只会糊成一块脏边，但它就在 alpha 里，
   裁不掉 —— 所以小尺寸只靠"占满 + 不额外加锐"来保可读性，别假装做了别的。
3. 透明通道原样带进 ICO：16/24/32 存 32 位 DIB（BGRA + 全 0 的 AND 掩码），
   48 以上存 PNG 条目。桌面快捷方式读的就是这个透明底，
   存成白底的话图标会带一个方方的白框。

ICO 里 16 / 24 / 32 用位图而不是 PNG：Office / Outlook 这类老组件读小尺寸时
只认位图，用 PNG 条目会出现"在某个程序里图标是空白"的报告。
"""

import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw

SOURCE = Path(__file__).with_name("andzip.png")
SIZES = (16, 24, 32, 48, 64, 128, 256)
DIB_SIZES = (16, 24, 32)

# 画面在格子里占的比例：贴边档给任务栏和标题栏用，留白档给桌面与资源管理器用。
EDGE_FILL = 0.94
PADDED_FILL = 0.86


def load_source():
    """读图源并确认它真的带透明通道 —— 白底图混进来时这里就停，别等到桌面上看见白框。"""
    if not SOURCE.exists():
        raise SystemExit(f"缺少图源 {SOURCE.name}：图标由它生成，没有它就出不来 ICO。")

    image = Image.open(SOURCE).convert("RGBA")
    extremes = image.getchannel("A").getextrema()
    if extremes[0] > 8:
        raise SystemExit(f"{SOURCE.name} 没有透明通道（最小 alpha={extremes[0]}），装出来的图标会带方框。")
    return image


def render(source, size, fill):
    """按 alpha 外接框裁切，再等比放进 size×size 的透明格子。"""
    box = source.getbbox()
    art = source.crop(box) if box else source

    side = int(round(size * fill))
    scale = side / max(art.size)
    resized = art.resize((max(1, round(art.size[0] * scale)), max(1, round(art.size[1] * scale))),
                         Image.LANCZOS)

    tile = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    tile.paste(resized, ((size - resized.size[0]) // 2, (size - resized.size[1]) // 2), resized)
    return tile


# ---------------------------------------------------------------- ICO 封装

def _png_entry(image):
    buf = io.BytesIO()
    image.save(buf, "PNG", optimize=True)
    return buf.getvalue()


def _dib_entry(image):
    """BITMAPINFOHEADER + 自下而上的 BGRA + 全 0 的 AND 掩码（ICO 内的位图不带文件头）。"""
    w, h = image.size
    rgba = image.tobytes("raw", "RGBA")
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            i = (y * w + x) * 4
            row += bytes((rgba[i + 2], rgba[i + 1], rgba[i], rgba[i + 3]))
        rows.append(bytes(row))
    pixels = b"".join(rows)
    and_mask = b"\x00" * (((w + 31) // 32) * 4 * h)
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0,
                         len(pixels) + len(and_mask), 0, 0, 0, 0)
    return header + pixels + and_mask


def write_ico(path, images):
    header_size = 6 + 16 * len(images)
    entries, blobs, offset = [], [], header_size
    for image in images:
        blob = _dib_entry(image) if image.size[0] in DIB_SIZES else _png_entry(image)
        w = 0 if image.size[0] == 256 else image.size[0]
        h = 0 if image.size[1] == 256 else image.size[1]
        entries.append(struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(blob), offset))
        blobs.append(blob)
        offset += len(blob)
    path.write_bytes(struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(blobs))


def main():
    source = load_source()
    images = [render(source, size, EDGE_FILL if size <= 32 else PADDED_FILL) for size in SIZES]
    images.sort(key=lambda im: -im.size[0] * im.size[1])  # 大的排前面，shell 挑第一个能用的
    out_path = Path(__file__).with_name("andzip.ico")
    write_ico(out_path, images)
    print(f"ok {out_path.name} {out_path.stat().st_size} bytes "
          f"sizes={[im.size[0] for im in images]}")


def contact_sheet(path, images, scale=4):
    """把每一档画在浅底和深底各排一行，供人眼核对（16px 好不好使只能看）。"""
    cell = max(im.size[0] for im in images) * scale
    sheet = Image.new("RGBA", (cell * len(images) + 40, cell * 2 + 70), (255, 255, 255, 255))
    d = ImageDraw.Draw(sheet)
    for col, row_bg in enumerate(((0xF6, 0xF8, 0xFA), (0x1F, 0x23, 0x28))):
        d.rectangle((20, 30 + col * (cell + 20), cell * len(images) + 20, 30 + col * (cell + 20) + cell),
                    fill=(*row_bg, 255))
        for i, im in enumerate(images):
            pad = (cell - im.size[0] * scale) // 2
            big = im.resize((im.size[0] * scale, im.size[0] * scale), Image.NEAREST)
            sheet.alpha_composite(big, (20 + i * cell + pad, 30 + col * (cell + 20) + pad))
    sheet.save(path)
    print(f"ok {path}")


if __name__ == "__main__":
    sys.exit(main())
