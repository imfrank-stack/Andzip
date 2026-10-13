"""Andzip 程序图标生成器。

用法：python assets/make_icon.py
输出：assets/andzip.ico（16 / 24 / 32 / 48 / 64 / 128 / 256 七档）

为什么用代码画而不是一张现成的图：

1. 图标要在 16px 的标题栏里站得住。一张 1024 的精美大图缩到 16 会糊成一团，
   所以小尺寸必须有单独的简化画法（去掉拉链齿、去掉软投影、把滑块放大）。
2. 颜色要跟界面上的品牌色同源。色值取自 Design.cs 的 Brand / Folder 一族，
   改一处两边一起改，不会出现"图标是另一个蓝"。
3. 可复现。规范文档写的是几何与色值，这个脚本就是那份规范的机器版本。

ICO 里 16 / 24 / 32 存成无压缩的 32 位 DIB，48 以上存 PNG：PNG 条目在
Vista+ 的 shell 里没问题，但 Office / Outlook 这类老组件读 16px 时只认位图，
小尺寸用 DIB 免掉"在某个程序里图标是空白"这类报告。
"""

import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

# ---------------------------------------------------------------- 色板（与 Design.cs 同源）

BRAND_TOP = (0x3D, 0x94, 0xE0)       # 比 BrandBright 再亮一档：方块顶部要吃到光
BRAND_MID = (0x0F, 0x6C, 0xBD)       # Design.BrandBase（浅色板）
BRAND_BOTTOM = (0x0B, 0x4E, 0x8C)    # 比 BrandDeep 再深一点：底部压住重量

PAPER_EDGE = (0xA8, 0xC4, 0xE0)
PAPER_STOPS = ((0.0, (255, 255, 255)), (0.62, (0xF6, 0xFA, 0xFF)), (1.0, (0xDD, 0xE9, 0xF6)))
TOOTH = (0xEF, 0xF6, 0xFC)
AMBER = (0xF2, 0xC0, 0x3C)           # 介于 FolderFace 与 FolderEdge 之间
AMBER_DARK = (0xD9, 0xA6, 0x1F)      # Design.FolderEdge
AMBER_EDGE = (0x8A, 0x61, 0x00)
SHADOW_RGB = (6, 24, 46)

STOPS = ((0.0, BRAND_TOP), (0.42, BRAND_MID), (1.0, BRAND_BOTTOM))
SIZES = (16, 24, 32, 48, 64, 128, 256)
DIB_SIZES = (16, 24, 32)


# ---------------------------------------------------------------- 画布与底

def _blend(stops, t):
    for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
        if t <= t1:
            k = 0.0 if t1 == t0 else (t - t0) / (t1 - t0)
            return tuple(int(c0[i] + (c1[i] - c0[i]) * k) for i in range(3))
    return stops[-1][1]


def _ramp(stops, n=256):
    ramp = Image.new("RGB", (1, n))
    for i in range(n):
        ramp.putpixel((0, i), _blend(stops, i / (n - 1)))
    return ramp


def _rounded_mask(ss, rect, radius):
    mask = Image.new("L", (ss, ss), 0)
    ImageDraw.Draw(mask).rounded_rectangle(rect, radius=radius, fill=255)
    return mask


def _tile_layer(ss, rect, radius, tier):
    """圆角方块本体：竖向渐变，full 档再加顶边那道受光高光。"""
    tile = Image.new("RGBA", (ss, ss))
    tile.paste(_ramp(STOPS).resize((ss, ss)), (0, 0))
    tile.putalpha(_rounded_mask(ss, rect, radius))

    if tier == "small":
        return tile

    inset = tuple(v + ss * 0.008 for v in rect[:2]) + tuple(v - ss * 0.008 for v in rect[2:])
    hl = Image.new("L", (ss, ss), 0)
    ImageDraw.Draw(hl).rounded_rectangle(
        inset, radius=radius * 0.86, outline=255, width=max(1, int(ss * 0.0045)))
    hl = hl.filter(ImageFilter.GaussianBlur(ss * 0.0045))
    light = Image.new("RGBA", (ss, ss), (255, 255, 255, 0))
    light.putalpha(hl.point(lambda v: int(v * 0.34)))
    tile = Image.alpha_composite(tile, light)

    # 内圈那道暗边：白色背景下没有它，方块的边缘会"化"掉
    ring = Image.new("L", (ss, ss), 0)
    ImageDraw.Draw(ring).rounded_rectangle(rect, radius=radius, outline=255,
                                           width=max(1, int(ss * 0.006)))
    edge = Image.new("RGBA", (ss, ss), (4, 26, 48, 0))
    edge.putalpha(ring.point(lambda v: int(v * 0.30)))
    tile = Image.alpha_composite(tile, edge)

    tile.putalpha(_rounded_mask(ss, rect, radius))
    return tile


# ---------------------------------------------------------------- 内容

def _panel_shadow(ss, panels, radius, strength):
    layer = Image.new("L", (ss, ss), 0)
    d = ImageDraw.Draw(layer)
    drop = ss * 0.016
    for x0, y0, x1, y1 in panels:
        d.rounded_rectangle((x0 * ss, y0 * ss + drop, x1 * ss, y1 * ss + drop),
                            radius=ss * radius, fill=255)
    layer = layer.filter(ImageFilter.GaussianBlur(ss * 0.011))
    shadow = Image.new("RGBA", (ss, ss), (*SHADOW_RGB, 0))
    shadow.putalpha(layer.point(lambda v: int(v * strength)))
    return shadow


def _panels(canvas, ss, panels, radius, tier):
    """两片纸不是纯白：从上到下带一点冷色，才不像两张剪纸贴在上面。"""
    ramp = _ramp(PAPER_STOPS).resize((ss, ss))
    edge_w = max(1, int(ss * 0.0018))
    for box in panels:
        mask = Image.new("L", (ss, ss), 0)
        ImageDraw.Draw(mask).rounded_rectangle(tuple(v * ss for v in box),
                                               radius=ss * radius, fill=255)
        layer = Image.new("RGBA", (ss, ss), (0, 0, 0, 0))
        layer.paste(ramp, (0, 0), mask)
        canvas.alpha_composite(layer)
        if tier == "full":
            ImageDraw.Draw(canvas).rounded_rectangle(
                tuple(v * ss for v in box), radius=ss * radius,
                outline=(*PAPER_EDGE, 255), width=edge_w)


def _teeth(canvas, ss, top, bottom, gap):
    """缝里交错的拉链齿：左齿搭左半边、右齿搭右半边，错开半个步距。"""
    d = ImageDraw.Draw(canvas)
    step, height = 0.0525, 0.031
    y, i = top, 0
    while y + height < bottom:
        if i % 2 == 0:
            box = (0.50 - gap * 1.9, y, 0.50 + gap * 0.25, y + height)
        else:
            box = (0.50 - gap * 0.25, y, 0.50 + gap * 1.9, y + height)
        d.rounded_rectangle(tuple(v * ss for v in box), radius=ss * height * 0.45,
                            fill=(*TOOTH, 255), outline=(*PAPER_EDGE, 255),
                            width=max(1, int(ss * 0.0016)))
        y, i = y + step, i + 1


def _slider(canvas, ss, box, tab_box, radius, tier):
    """拉头 + 下面那片拉环。琥珀色是画面里唯一的暖色，也是视线的落点。"""
    d = ImageDraw.Draw(canvas)
    edge = max(1, int(ss * (0.006 if tier == "full" else 0.010)))
    if tier == "full":
        d.rounded_rectangle(tuple(v * ss for v in tab_box), radius=ss * radius * 0.7,
                            fill=(*AMBER_DARK, 255), outline=(*AMBER_EDGE, 255), width=edge)
        d.rounded_rectangle(tuple(v * ss for v in box), radius=ss * radius,
                            fill=(*AMBER, 255), outline=(*AMBER_EDGE, 255), width=edge)
        # 滑块中间那道凹槽，是让读者认出"这是拉头"的关键一笔 —— 但 16px 上它只会
        # 把拉头啃成一个暗点，所以小尺寸不画
        slot = (box[0] + 0.024, box[1] + 0.022, box[2] - 0.024, box[3] - 0.034)
        d.rounded_rectangle(tuple(v * ss for v in slot), radius=ss * radius * 0.55,
                            fill=(*AMBER_DARK, 255))
    else:
        d.rounded_rectangle(tuple(v * ss for v in box), radius=ss * radius, fill=(*AMBER, 255))


def draw_icon(size, tier):
    ss = size * (8 if size <= 64 else 4)
    margin = 0.030 if tier == "small" else 0.022
    radius = ss * (0.235 if tier == "small" else 0.225)
    rect = (ss * margin, ss * margin, ss * (1 - margin), ss * (1 - margin))

    canvas = Image.new("RGBA", (ss, ss), (0, 0, 0, 0))
    if tier == "small":
        # 16px 上没有能容纳四五种形状的地方。三样：左纸、右纸、拉头 —— 
        # 中间那道缝直接让方块的蓝透出来，比再画一条"拉链带"更清楚。
        gap = 0.062
        panels = [(0.235, 0.250, 0.50 - gap / 2, 0.720), (0.50 + gap / 2, 0.250, 0.765, 0.720)]
        canvas = Image.alpha_composite(canvas, _panel_shadow(ss, panels, 0.052, 0.20))
        _panels(canvas, ss, panels, 0.052, tier)
        _slider(canvas, ss, (0.408, 0.560, 0.592, 0.730), (0.408, 0.560, 0.592, 0.730),
                0.070, tier)
    else:
        gap = 0.046
        panels = [(0.230, 0.255, 0.50 - gap / 2, 0.745), (0.50 + gap / 2, 0.255, 0.770, 0.745)]
        canvas = Image.alpha_composite(canvas, _panel_shadow(ss, panels, 0.040, 0.34))
        _panels(canvas, ss, panels, 0.040, tier)
        _teeth(canvas, ss, 0.300, 0.560, gap)
        _slider(canvas, ss, (0.437, 0.560, 0.563, 0.668), (0.462, 0.658, 0.538, 0.752),
                0.045, tier)

    composed = Image.alpha_composite(_tile_layer(ss, rect, radius, tier), canvas)
    return composed.resize((size, size), Image.LANCZOS)


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
    images = [draw_icon(size, "small" if size <= 32 else "full") for size in SIZES]
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
