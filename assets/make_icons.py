"""生成应用图标候选方案：矢量绘制 + 按尺寸分级简化 + 多尺寸 ICO + 预览图。

设计约束（对齐现有品牌 DNA 与 Fluent 图标规范）：
  · 主色靛蓝 #6366F1 → 紫罗兰 #8B5CF6 对角渐变
  · 圆角方形底，圆角半径 ≈ 内容区 20%，外边距 ≈ 5.5%
  · 几何符号居中，笔画粗壮；小尺寸自动去掉装饰层并加粗笔画
"""
from __future__ import annotations

import struct
from pathlib import Path

from PySide6.QtCore import QPointF, QRectF, Qt
from PySide6.QtGui import (QBrush, QColor, QImage, QLinearGradient, QPainter,
                           QPainterPath, QPainterPathStroker)

ACCENT = "#6366F1"
ACCENT_2 = "#8B5CF6"
OUT = Path(__file__).resolve().parent / "icons"
OUT.mkdir(parents=True, exist_ok=True)



# ── 基础绘制 ──
def canvas(size: int) -> QImage:
    img = QImage(size, size, QImage.Format_ARGB32_Premultiplied)
    img.fill(Qt.transparent)
    return img


def begin(img: QImage) -> QPainter:
    p = QPainter(img)
    p.setRenderHint(QPainter.Antialiasing, True)
    p.setRenderHint(QPainter.SmoothPixmapTransform, True)
    return p


def grad(rect: QRectF) -> QLinearGradient:
    g = QLinearGradient(rect.topLeft(), rect.bottomRight())
    g.setColorAt(0.0, QColor(ACCENT))
    g.setColorAt(1.0, QColor(ACCENT_2))
    return g


def tile(p: QPainter, size: int) -> tuple[QPainterPath, QRectF]:
    """渐变圆角方底。"""
    m = size * 0.055
    r = QRectF(m, m, size - 2 * m, size - 2 * m)
    path = QPainterPath()
    path.addRoundedRect(r, size * 0.205, size * 0.205)
    p.fillPath(path, QBrush(grad(r)))
    return path, r


def sheen(p: QPainter, path: QPainterPath, rect: QRectF) -> None:
    """左上斜向高光，给平面渐变一点体积感（小尺寸省略）。"""
    p.save()
    p.setClipPath(path)
    g = QLinearGradient(rect.topLeft(), QPointF(rect.left(), rect.bottom()))
    g.setColorAt(0.0, QColor(255, 255, 255, 54))
    g.setColorAt(0.62, QColor(255, 255, 255, 0))
    p.fillRect(rect, QBrush(g))
    p.restore()


def w_outline(size: int, width_ratio: float = 0.125) -> QPainterPath:
    """几何 W：五点四段（两谷一峰），转成填充路径。"""
    cx, cy = size / 2, size / 2
    w, h = size * 0.52, size * 0.40
    pts = [
        QPointF(cx - w / 2, cy - h / 2),   # 左上
        QPointF(cx - w / 4, cy + h / 2),   # 左谷
        QPointF(cx, cy - h / 2),           # 中峰
        QPointF(cx + w / 4, cy + h / 2),   # 右谷
        QPointF(cx + w / 2, cy - h / 2),   # 右上
    ]
    p = QPainterPath(pts[0])
    for pt in pts[1:]:
        p.lineTo(pt)
    st = QPainterPathStroker()
    st.setWidth(size * width_ratio)
    st.setCapStyle(Qt.RoundCap)
    st.setJoinStyle(Qt.RoundJoin)
    return st.createStroke(p)


def arrow_path(size: int) -> QPainterPath:
    """实心向下箭头：圆角方杆 + 三角头。"""
    s = size
    cx = s / 2
    shaft_w = s * 0.15
    shaft = QPainterPath()
    shaft.addRoundedRect(QRectF(cx - shaft_w / 2, s * 0.245, shaft_w, s * 0.285),
                         shaft_w / 2, shaft_w / 2)
    head = QPainterPath()
    head.moveTo(cx - s * 0.215, s * 0.475)
    head.lineTo(cx + s * 0.215, s * 0.475)
    head.lineTo(cx, s * 0.665)
    head.closeSubpath()
    return shaft.united(head)


def tray_path(size: int) -> QPainterPath:
    """底部圆角托盘：暗示“已收纳到清单”。"""
    s = size
    w, h = s * 0.46, s * 0.078
    p = QPainterPath()
    p.addRoundedRect(QRectF((s - w) / 2, s * 0.735, w, h), h / 2, h / 2)
    return p


# ── 四个候选方案 ──
def draw_a(size: int) -> QImage:            # 流光 W
    img = canvas(size)
    p = begin(img)
    path, rect = tile(p, size)
    if size >= 48:
        sheen(p, path, rect)
    p.fillPath(w_outline(size, 0.145 if size <= 24 else 0.122), QColor("#FFFFFF"))
    p.end()
    return img


def draw_b(size: int) -> QImage:            # 安装箭头 + 托盘
    img = canvas(size)
    p = begin(img)
    path, rect = tile(p, size)
    if size >= 48:
        sheen(p, path, rect)
    p.fillPath(arrow_path(size), QColor("#FFFFFF"))
    p.fillPath(tray_path(size), QColor(255, 255, 255, 235))
    p.end()
    return img


def draw_c(size: int) -> QImage:            # 负空间 W（挖空）
    img = canvas(size)
    p = begin(img)
    path, rect = tile(p, size)
    if size >= 48:
        sheen(p, path, rect)
    p.setCompositionMode(QPainter.CompositionMode_DestinationOut)
    p.fillPath(w_outline(size, 0.165 if size <= 24 else 0.142), QColor("#000000"))
    p.setCompositionMode(QPainter.CompositionMode_SourceOver)
    p.end()
    return img


def draw_d(size: int) -> QImage:            # 纯渐变 W（无底）
    img = canvas(size)
    p = begin(img)
    r = QRectF(size * 0.09, size * 0.09, size * 0.82, size * 0.82)
    p.fillPath(w_outline(size, 0.155 if size <= 24 else 0.132), QBrush(grad(r)))
    p.end()
    return img


PLANS = {
    "a": ("方案 A · 流光 W", draw_a),
    "b": ("方案 B · 安装箭头", draw_b),
    "c": ("方案 C · 负空间 W", draw_c),
    "d": ("方案 D · 无底流光 W", draw_d),
}


# ── 多尺寸 ICO（每帧独立渲染，避免缩放假边） ──
def write_ico(frames: list[tuple[int, bytes]], dest: Path) -> None:
    n = len(frames)
    header = struct.pack("<HHH", 0, 1, n)
    entries, blobs, offset = b"", b"", 6 + 16 * n
    for size, png in frames:
        entries += struct.pack("<BBBBHHII", size if size < 256 else 0,
                               size if size < 256 else 0, 0, 0, 1, 32,
                               len(png), offset)
        blobs += png
        offset += len(png)
    dest.write_bytes(header + entries + blobs)


def render(fn, size: int, dest: Path) -> None:
    fn(size).save(str(dest), "PNG")


def main() -> None:
    for key, (_, fn) in PLANS.items():
        # 大图（预览/商店素材）
        render(fn, 512, OUT / f"{key}_512.png")
        render(fn, 256, OUT / f"{key}_256.png")
        # 多尺寸 ICO
        frames = []
        for s in (16, 20, 24, 32, 40, 48, 64, 96, 128, 256):
            fn(s).save(str(OUT / f"_tmp_{s}.png"), "PNG")
            frames.append((s, (OUT / f"_tmp_{s}.png").read_bytes()))
            (OUT / f"_tmp_{s}.png").unlink()
        write_ico(frames, OUT / f"{key}.ico")
        print("built", key)


if __name__ == "__main__":
    main()
