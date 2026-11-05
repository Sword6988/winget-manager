"""Winget 软件管家 · 公共层：常量、颜色令牌、绘制工具。

只提供一套浅色主题：令牌是模块级常量，自绘控件在 paintEvent 里直接读。

视觉规范（对齐 static/index.html）：
  · 靛蓝 → 紫罗兰渐变 #6366F1 → #8B5CF6
  · 淡紫底色 #F4F5FA + 双径向光晕
  · 卡片 16px 圆角 + 柔和投影，玻璃拟态工具条/底栏
  · 统计卡（顶部渐变条 + 渐变数字）、分组胶囊（选中渐变）
  · 无边框窗口 + 自绘标题栏 + 圆角窗口
业务逻辑复用 core/ services/，未改动后端。
"""
from __future__ import annotations

import html
import sys
from pathlib import Path

from PySide6.QtCore import QPointF, QRectF, QSettings, Qt
from PySide6.QtGui import (QBrush, QColor, QFontDatabase, QIcon, QLinearGradient,
                           QPainter, QPainterPath, QPen, QPixmap)
from PySide6.QtWidgets import QApplication, QGraphicsDropShadowEffect, QWidget

if getattr(sys, "frozen", False):
    BASE_DIR = Path(sys.executable).resolve().parent
else:
    BASE_DIR = Path(__file__).resolve().parent
# 只在源码运行时把项目根塞进 sys.path。冻结版不能这么做：此时 BASE_DIR 是 exe
# 所在目录，用户往同目录丢一个 core/ 就能顶掉内置模块（DLL/模块劫持）。
if not getattr(sys, "frozen", False):
    sys.path.insert(0, str(BASE_DIR))

from core.config import Config, ConfigError, load_config  # noqa: E402
from core.errors import AppError  # noqa: E402
from core.version import APP_VERSION  # noqa: E402,F401  （对外转出）
from services.catalog import CatalogService  # noqa: E402
from services.profiles import (build_restore_bat, build_winget_manifest,  # noqa: E402
                              source_breakdown)
from services.winget_cli import WingetRepository  # noqa: E402

APP_TITLE = "Winget 软件管家"
APP_TAG = "Qt 桌面版"
PERSIST_DIR = Path.home() / "AppData/Local/WingetManager"
LOG_DIR = PERSIST_DIR / "logs"
LOG_FILE = LOG_DIR / "app.log"
SELECTION_FILE = PERSIST_DIR / "selection.json"
SETTINGS_ORG = "Shibeng"
SETTINGS_APP = "WingetManager"
SINGLE_KEY = "WingetManagerQt.SingleInstance"
EXPORT_DEFAULT = Path("D:/apps.json")


def settings() -> QSettings:
    """全局设置（窗口几何、导出路径、筛选状态等）。"""
    return QSettings(SETTINGS_ORG, SETTINGS_APP)


def default_export_path() -> Path:
    """导出路径：优先上次使用的位置，其次 D:\\apps.json，最后回退文档目录。

    早先版本硬编码 D 盘，在没有 D 盘的机器上会直接导出失败。
    """
    saved = settings().value("export/path", "", type=str)
    if saved:
        p = Path(saved)
        if p.parent.is_dir():
            return p
    if EXPORT_DEFAULT.parent.exists():
        return EXPORT_DEFAULT
    docs = Path.home() / "Documents"
    return (docs if docs.is_dir() else Path.home()) / "apps.json"

# ── 设计令牌（与网页版一致）──
BG = "#F4F5FA"
CARD = "#FFFFFF"
LINE = "#E8EAF3"
LINE_SOFT = "#F1F2F8"
TEXT = "#181C28"
MUTED = "#6A7183"
MUTED2 = "#9AA1B5"
MUTED3 = "#C3C8D8"
ACCENT = "#6366F1"
ACCENT_2 = "#8B5CF6"
ACCENT_DEEP = "#4F46E5"
ACCENT_SOFT = "#EEF0FF"
ACCENT_LINE = "#D9DCFF"
WARN = "#B45309"
WARN_BG = "#FFF8E8"
WARN_LINE = "#F5E2BB"
ERR = "#C53030"

# ── 日志面板（浅色控制台：浅底 + 深灰等宽字 + 级别色标）──
LOG_BG = "#FBFCFE"
LOG_FG = "#39404F"
LOG_TS = "#A6ADBE"
LOG_HEAD = "#6A7183"
LOG_ACT = "#8A93A6"
LOG_SEL = "#E4E8FF"
LOG_OK = "#0B7A55"
LOG_SKIP = "#6D4FD0"
LOG_FAIL = "#C53030"
LOG_DONE = "#4F46E5"
LOG_SCROLL = "#DCE0EC"

# ── 派生令牌（单一浅色主题，不做深浅切换）──
SURFACE = "#FFFFFF"          # 输入框 / 菜单 / 按钮底色
SURFACE_2 = "#FAFBFD"        # 次级面板底
SURFACE_3 = "#FAFBFE"        # hover 底
BORDER_HOVER = "#CDD2E2"
ROW_HOVER = "#F7F8FE"
ROW_ALT = "#E9EBF4"          # 未选中复选框 / 禁用按钮底
NAME_OFF = "#A9B1C0"         # 不可重装行：名称
ID_OFF = "#C3C9D6"           # 不可重装行：包 ID
CHK_RING_HOVER = "#DFE2F0"
TAG_DIM_FG = "#98A0B1"
TAG_DIM_BG = "#F2F4F9"
TAG_DIM_LINE = "#E7EAF3"
TAG_SRC_FG = "#6D4FD0"
TAG_SRC_BG = "#F3F0FE"
TAG_SRC_LINE = "#E5DFFB"
PILL_RING = "#F1F3F9"
PILL_RING_FULL = "#C9CDFB"
GRAD_H1 = "#F8F9FE"
GRAD_H2 = "#EFF1FA"
HEAD_TOP = "#FBFDFF"
HEAD_BOT = "#F8F9FD"
CHIP_BG = "#F1F2F8"
LINK_FG = "#535B6E"
LINK_FG_OFF = "#9AA3B5"
LINK_FG_HOVER = "#AEB5C4"
CHEV = "#8A93A6"
SCROLL = "#D2D6E4"
SCROLL_HI = "#B8BED2"
SKEL_1 = "#E8EBF3"
SKEL_2 = "#EFF1F7"
GLASS = "rgba(255,255,255,0.78)"
GLASS_2 = "rgba(255,255,255,0.82)"
GLASS_BORDER = "rgba(232,234,243,0.95)"
GLASS_INPUT = "rgba(250,251,253,0.92)"


def _broadcast(values: dict) -> None:
    """把刷新后的令牌写进其它 UI 模块的全局命名空间。"""
    for mod_name in ("qt_ui", "qt_app"):
        mod = sys.modules.get(mod_name)
        if mod is None:
            continue
        for k, v in values.items():
            setattr(mod, k, v)


def set_font_family(family: str) -> None:
    """统一换字体：本模块 + 各 UI 模块的 FONT_FAMILY 一起改。"""
    global FONT_FAMILY
    FONT_FAMILY = family
    _broadcast({"FONT_FAMILY": family})

FONT_FAMILY = "HarmonyOS Sans SC"
MONO_FAMILY = "Consolas"

# 软件列表固定两组，判定只看 reinstallable，不做任何关键词分类
REINSTALLABLE_GROUP = "winget 可装"      # 分组一：winget 有安装源，可勾选导出
UNSUPPORTED_GROUP = "winget 不支持"      # 分组二：winget 装不上，灰显仅作核对，固定沉底


HL_BG = "#FDE68A"
HL_FG = "#7C2D12"


def _hl_html(text: str, keywords, color: str) -> str:
    """把命中关键词的片段包上高亮底色，其余部分保持原色。

    支持多个关键词（空格分词）：每次取「最靠前、同位置取最长」的命中，
    避免短词把长词切碎。
    QLabel 只有走富文本才能局部着色，所以整段都要显式带 color，
    否则 QSS 的 color 对内联 span 不生效。
    """
    if isinstance(keywords, str):
        keywords = [keywords]
    kws = [k.lower() for k in keywords if k]
    if not kws:
        return f'<span style="color:{color}">{html.escape(text)}</span>'
    low = text.lower()
    parts: list[str] = []
    i = 0
    while i < len(text):
        best_j, best_k = -1, ""
        for k in kws:
            j = low.find(k, i)
            if j < 0:
                continue
            if best_j < 0 or j < best_j or (j == best_j and len(k) > len(best_k)):
                best_j, best_k = j, k
        if best_j < 0:
            parts.append(html.escape(text[i:]))
            break
        if best_j > i:
            parts.append(html.escape(text[i:best_j]))
        parts.append(f'<span style="background-color:{HL_BG}; color:{HL_FG};'
                     f' font-weight:600;">{html.escape(text[best_j:best_j + len(best_k)])}</span>')
        i = best_j + len(best_k)
    return f'<span style="color:{color}">{"".join(parts)}</span>'


def _pick_family(app: QApplication) -> str:
    fams = set(QFontDatabase.families())
    for name in (FONT_FAMILY, "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI"):
        if name in fams:
            return name
    return app.font().family()


def _grad(rect: QRectF, c1: str = ACCENT, c2: str = ACCENT_2) -> QLinearGradient:
    g = QLinearGradient(rect.topLeft(), rect.bottomRight())
    g.setColorAt(0.0, QColor(c1))
    g.setColorAt(1.0, QColor(c2))
    return g


def _shadow(widget: QWidget, blur=22, dy=6, alpha=26, color=(24, 28, 40)) -> None:
    eff = QGraphicsDropShadowEffect(widget)
    eff.setBlurRadius(blur)
    eff.setOffset(0, dy)
    eff.setColor(QColor(*color, alpha))
    widget.setGraphicsEffect(eff)


def _glow(widget: QWidget, blur=20, dy=5, alpha=110) -> None:
    eff = QGraphicsDropShadowEffect(widget)
    eff.setBlurRadius(blur)
    eff.setOffset(0, dy)
    eff.setColor(QColor(99, 102, 241, alpha))
    widget.setGraphicsEffect(eff)


def _brand_arrow_path(size: float) -> tuple[QPainterPath, QPainterPath]:
    """品牌符号（方案 B）：实心下箭头 + 圆角托盘。

    箭头 = 下载/安装，托盘 = 收纳清单；几何绘制保证 16px 下仍清晰。
    """
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
    arrow = shaft.united(head)
    w, h = s * 0.46, s * 0.078
    tray = QPainterPath()
    tray.addRoundedRect(QRectF((s - w) / 2, s * 0.735, w, h), h / 2, h / 2)
    return arrow, tray


def app_icon() -> QIcon:
    s = 256.0
    pm = QPixmap(int(s), int(s))
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    m = s * 0.055
    r = QRectF(m, m, s - 2 * m, s - 2 * m)
    path = QPainterPath()
    path.addRoundedRect(r, s * 0.205, s * 0.205)
    p.fillPath(path, QBrush(_grad(r)))
    # 左上斜向高光，给平面渐变一点体积感
    p.save()
    p.setClipPath(path)
    g = QLinearGradient(r.topLeft(), QPointF(r.left(), r.bottom()))
    g.setColorAt(0.0, QColor(255, 255, 255, 54))
    g.setColorAt(0.62, QColor(255, 255, 255, 0))
    p.fillRect(r, QBrush(g))
    p.restore()
    arrow, tray = _brand_arrow_path(s)
    p.fillPath(arrow, QColor("#FFFFFF"))
    p.fillPath(tray, QColor(255, 255, 255, 235))
    p.end()
    return QIcon(pm)


def search_icon(color: str = MUTED2, size: int = 15) -> QIcon:
    pm = QPixmap(size, size)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    pen = QPen(QColor(color), 1.7)
    pen.setCapStyle(Qt.RoundCap)
    p.setPen(pen)
    rr = size * 0.27
    p.drawEllipse(QRectF(size * 0.13, size * 0.13, rr * 2, rr * 2))
    p.drawLine(int(size * 0.62), int(size * 0.62), int(size * 0.85), int(size * 0.85))
    p.end()
    return QIcon(pm)


# ══════════════════════════ 自绘基础组件 ══════════════════════════
