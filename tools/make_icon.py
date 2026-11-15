#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 WingetManagerWpf/Assets/app.ico（纯标准库，不依赖 Pillow）。

图标内容与主窗口标题栏的 BrandMark 一致：圆角方 + 对角渐变（#6366F1 → #8B5CF6）
+ 白色「下载到托盘」符号（下箭头 + 托盘横线）。

实现要点：
  ① 先在 256×256 上用 4×4 超采样算出抗锯齿位图（每个子像素做一次几何覆盖判定）；
  ② 16/32/48/64 由 256 位图做面积平均降采样，避免最近邻产生的锯齿；
  ③ 256 那条用 PNG 载荷（zlib），其余用 32 位 BMP（DIB + AND mask）——
     Vista 之后 PNG 条目在资源管理器里也能显示，但小尺寸用 DIB 在旧 shell 上更稳；
  ④ 写完立刻回读校验：文件头、条目数、每条 offset/length 是否落在文件内、PNG 签名。

用法：python tools/make_icon.py [输出路径]
默认输出 WingetManagerWpf/WingetManagerWpf/Assets/app.ico（相对仓库根）。
"""
from __future__ import annotations

import os
import struct
import sys
import zlib

SIZE = 256
SUPERSAMPLE = 4
SIZES = (256, 64, 48, 32, 16)

# 品牌渐变色（v2「清朗 Azure」：与 Theme/Tokens.xaml 的 Color.Accent / Color.Accent2 一致）
GRAD_START = (0x25, 0x63, 0xEB)
GRAD_END = (0x0E, 0xA5, 0xE9)


# ══════════════════════════ 几何判定 ══════════════════════════

def in_rounded_rect(px: float, py: float, x0: float, y0: float,
                   x1: float, y1: float, radius: float) -> bool:
    """点是否落在圆角矩形内（radius 会被夹到半宽/半高以内）。"""
    if px < x0 or px > x1 or py < y0 or py > y1:
        return False

    radius = max(0.0, min(radius, (x1 - x0) / 2.0, (y1 - y0) / 2.0))
    if radius <= 0.0:
        return True

    # 只在四个角上做圆判定，边与内部直接通过
    cx = x0 + radius if px < x0 + radius else (x1 - radius if px > x1 - radius else px)
    cy = y0 + radius if py < y0 + radius else (y1 - radius if py > y1 - radius else py)
    dx = px - cx
    dy = py - cy
    return dx * dx + dy * dy <= radius * radius


def in_triangle(px: float, py: float, ax: float, ay: float,
                bx: float, by: float, cx: float, cy: float) -> bool:
    """点是否落在三角形内（同向叉积法）。"""
    def cross(x1, y1, x2, y2, x3, y3):
        return (x2 - x1) * (y3 - y1) - (y2 - y1) * (x3 - x1)

    d1 = cross(ax, ay, bx, by, px, py)
    d2 = cross(bx, by, cx, cy, px, py)
    d3 = cross(cx, cy, ax, ay, px, py)
    has_neg = d1 < 0 or d2 < 0 or d3 < 0
    has_pos = d1 > 0 or d2 > 0 or d3 > 0
    return not (has_neg and has_pos)


# ══════════════════════════ 图标像素 ══════════════════════════

def gradient_at(x: float, y: float, size: float):
    """对角渐变：左上 → 右下。"""
    t = (x + y) / (2.0 * max(1.0, size - 1.0))
    t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
    return (
        int(round(GRAD_START[0] + (GRAD_END[0] - GRAD_START[0]) * t)),
        int(round(GRAD_START[1] + (GRAD_END[1] - GRAD_START[1]) * t)),
        int(round(GRAD_START[2] + (GRAD_END[2] - GRAD_START[2]) * t)),
    )


def sample(x: float, y: float, size: int):
    """返回 (r, g, b, a) —— 单独一个采样点。"""
    s = float(size)

    # 底板：圆角方（留 3.5% 边距）
    margin = s * 0.035
    radius = s * 0.22
    if not in_rounded_rect(x, y, margin, margin, s - margin, s - margin, radius):
        return (0, 0, 0, 0)

    r, g, b = gradient_at(x, y, s)

    # 白色符号：箭杆 + 箭头 + 托盘
    center = s / 2.0
    shaft_half = s * 0.046
    shaft_top = s * 0.215
    shaft_bottom = s * 0.500

    head_half = s * 0.165
    head_top = s * 0.492
    apex_y = s * 0.700

    tray_left = s * 0.250
    tray_right = s * 0.750
    tray_top = s * 0.762
    tray_bottom = s * 0.800
    tray_radius = (tray_bottom - tray_top) / 2.0

    on_shaft = in_rounded_rect(x, y, center - shaft_half, shaft_top,
                               center + shaft_half, shaft_bottom, shaft_half)
    on_head = in_triangle(x, y,
                          center - head_half, head_top,
                          center + head_half, head_top,
                          center, apex_y)
    on_tray = in_rounded_rect(x, y, tray_left, tray_top, tray_right, tray_bottom, tray_radius)

    if on_shaft or on_head or on_tray:
        return (255, 255, 255, 255)

    return (r, g, b, 255)


def render_master(size: int = SIZE) -> bytes:
    """渲染 256×256 RGBA（已按 SUPERSAMPLE 做抗锯齿）。"""
    step = 1.0 / SUPERSAMPLE
    weight = SUPERSAMPLE * SUPERSAMPLE
    rows = []

    for py in range(size):
        row = bytearray()
        for px in range(size):
            acc_r = acc_g = acc_b = acc_a = 0
            for sy in range(SUPERSAMPLE):
                y = py + (sy + 0.5) * step
                for sx in range(SUPERSAMPLE):
                    x = px + (sx + 0.5) * step
                    r, g, b, a = sample(x, y, size)
                    # 透明像素的 rgb 参与平均会发黑，先乘上 alpha 做预乘平均
                    acc_r += r * a
                    acc_g += g * a
                    acc_b += b * a
                    acc_a += a

            if acc_a == 0:
                row += bytes((0, 0, 0, 0))
                continue

            row += bytes((
                int(round(acc_r / acc_a)),
                int(round(acc_g / acc_a)),
                int(round(acc_b / acc_a)),
                int(round(acc_a / weight)),
            ))
        rows.append(bytes(row))

    return b"".join(rows)


def downsample(master: bytes, master_size: int, target: int) -> bytes:
    """面积平均降采样（master_size 必须是 target 的整数倍或反之都有覆盖）。"""
    scale = master_size / float(target)
    out = bytearray()

    for ty in range(target):
        y0 = ty * scale
        y1 = (ty + 1) * scale
        sy0 = int(y0)
        sy1 = max(sy0 + 1, int(round(y1)))
        for tx in range(target):
            x0 = tx * scale
            x1 = (tx + 1) * scale
            sx0 = int(x0)
            sx1 = max(sx0 + 1, int(round(x1)))

            acc_r = acc_g = acc_b = acc_a = 0.0
            count = 0
            for sy in range(sy0, min(sy1, master_size)):
                base = sy * master_size * 4
                for sx in range(sx0, min(sx1, master_size)):
                    offset = base + sx * 4
                    a = master[offset + 3]
                    acc_r += master[offset] * a
                    acc_g += master[offset + 1] * a
                    acc_b += master[offset + 2] * a
                    acc_a += a
                    count += 1

            if count == 0 or acc_a == 0:
                out += bytes((0, 0, 0, 0))
                continue

            out += bytes((
                int(round(acc_r / acc_a)),
                int(round(acc_g / acc_a)),
                int(round(acc_b / acc_a)),
                int(round(acc_a / count)),
            ))

    return bytes(out)


# ══════════════════════════ 编码 ══════════════════════════

def png_chunk(tag: bytes, payload: bytes) -> bytes:
    return (struct.pack(">I", len(payload)) + tag + payload
            + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))


def encode_png(rgba: bytes, size: int) -> bytes:
    """最小 PNG 编码：8 位 RGBA、逐行 filter=0。"""
    stride = size * 4
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        raw += rgba[y * stride:(y + 1) * stride]

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n"
            + png_chunk(b"IHDR", header)
            + png_chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + png_chunk(b"IEND", b""))


def encode_dib(rgba: bytes, size: int) -> bytes:
    """32 位 BMP（BITMAPINFOHEADER + 自下而上的 BGRA + 全零 AND mask）。"""
    header = struct.pack("<IiiHHIIiiII",
                         40,          # biSize
                         size,        # biWidth
                         size * 2,    # biHeight（XOR + AND 两张图）
                         1,           # biPlanes
                         32,          # biBitCount
                         0,           # biCompression = BI_RGB
                         size * size * 4 + len(mask_bytes(size)),  # biSizeImage
                         0, 0, 0, 0)

    pixels = bytearray()
    for y in range(size - 1, -1, -1):
        base = y * size * 4
        for x in range(size):
            offset = base + x * 4
            pixels += bytes((rgba[offset + 2], rgba[offset + 1], rgba[offset], rgba[offset + 3]))

    return header + bytes(pixels) + mask_bytes(size)


def mask_bytes(size: int) -> bytes:
    """1bpp AND mask，每行补齐到 4 字节，全 0 表示「不透明由 alpha 决定」。"""
    row_bytes = ((size + 31) // 32) * 4
    return b"\x00" * (row_bytes * size)


def build_ico(entries) -> bytes:
    """entries: [(size, payload)] → ICO 字节。"""
    header = struct.pack("<HHH", 0, 1, len(entries))
    directory = b""
    offset = 6 + 16 * len(entries)
    payloads = b""

    for size, payload in entries:
        width = 0 if size >= 256 else size
        height = 0 if size >= 256 else size
        directory += struct.pack("<BBBBHHII", width, height, 0, 0, 1, 32, len(payload), offset)
        offset += len(payload)
        payloads += payload

    return header + directory + payloads


def verify(ico: bytes, expected_count: int) -> None:
    """回读校验：任何一条不对就抛 AssertionError。"""
    assert ico[0:4] == b"\x00\x00\x01\x00", "ICO 文件头不对"
    count = struct.unpack("<H", ico[4:6])[0]
    assert count == expected_count, "条目数 %d ≠ %d" % (count, expected_count)

    for index in range(count):
        base = 6 + index * 16
        width, height, _, _, planes, bitcount, length, offset = struct.unpack(
            "<BBBBHHII", ico[base:base + 16])
        assert planes == 1, "第 %d 条 planes 不是 1" % index
        assert bitcount == 32, "第 %d 条 bitcount 不是 32" % index
        assert offset + length <= len(ico), "第 %d 条越界" % index
        payload = ico[offset:offset + length]
        assert payload, "第 %d 条载荷为空" % index

        is_png = payload[0:8] == b"\x89PNG\r\n\x1a\n"
        if is_png:
            assert width == 0 and height == 0, "256 条目宽高字段应为 0"
        else:
            size_field = struct.unpack("<I", payload[0:4])[0]
            assert size_field == 40, "DIB 头长度不是 40"

    print("校验通过：%d 条，%d 字节" % (count, len(ico)))


def main() -> int:
    script_dir = os.path.dirname(os.path.abspath(__file__))
    if len(sys.argv) > 1:
        out_path = os.path.abspath(sys.argv[1])
    else:
        out_path = os.path.abspath(os.path.join(
            script_dir, "..", "WingetManagerWpf", "Assets", "app.ico"))

    master = render_master(SIZE)

    entries = []
    for size in SIZES:
        rgba = master if size == SIZE else downsample(master, SIZE, size)
        payload = encode_png(rgba, size) if size == SIZE else encode_dib(rgba, size)
        entries.append((size, payload))
        print("尺寸 %3d：%6d 字节（%s）" % (size, len(payload), "PNG" if size == SIZE else "DIB"))

    ico = build_ico(entries)
    verify(ico, len(entries))

    parent = os.path.dirname(out_path)
    if parent and not os.path.isdir(parent):
        os.makedirs(parent)

    with open(out_path, "wb") as handle:
        handle.write(ico)

    print("已写入：" + out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
