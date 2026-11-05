"""Winget 软件管家 · 桌面版 v2（PySide6 · 与网页版同一套视觉 DNA）。

视觉规范（对齐 static/index.html）：
  · 靛蓝 → 紫罗兰渐变 #6366F1 → #8B5CF6
  · 淡紫底色 #F4F5FA + 双径向光晕
  · 卡片 16px 圆角 + 柔和投影，玻璃拟态工具条/底栏
  · 统计卡（顶部渐变条 + 渐变数字）、分类胶囊（选中渐变）
  · 无边框窗口 + 自绘标题栏 + 圆角窗口
业务逻辑复用 core/ services/，未改动后端。
"""
from __future__ import annotations

import json
import os
import sys
import time
from datetime import datetime
from pathlib import Path

from PySide6.QtCore import (QEasingCurve, QEvent, QPoint, QPointF, QPropertyAnimation,
                            QRect, QRectF, Qt, QThread, QTimer, Property, Signal)
from PySide6.QtGui import (QBrush, QColor, QFont, QFontDatabase, QFontMetrics, QIcon,
                           QKeySequence, QLinearGradient, QPainter, QPainterPath, QPen,
                           QPixmap, QPolygonF, QRadialGradient, QShortcut,
                           QTextCharFormat, QTextCursor)
from PySide6.QtWidgets import (QApplication, QDialog, QFileDialog, QFrame,
                               QGraphicsDropShadowEffect, QGraphicsOpacityEffect,
                               QHBoxLayout, QLabel, QLineEdit, QMainWindow, QMenu,
                               QMessageBox, QPushButton, QScrollArea, QSizePolicy,
                               QStackedWidget, QTextEdit, QVBoxLayout, QWidget)

if getattr(sys, "frozen", False):
    BASE_DIR = Path(sys.executable).resolve().parent
else:
    BASE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BASE_DIR))
os.environ.setdefault("WPM_EXPORT_DIR", str(BASE_DIR / "exports"))

from core.config import Config, ConfigError, load_config  # noqa: E402
from core.errors import AppError  # noqa: E402
from services.catalog import CatalogService  # noqa: E402
from services.profiles import build_winget_manifest  # noqa: E402
from services.winget_cli import WingetRepository  # noqa: E402

APP_TITLE = "Winget 软件管家"
PERSIST_DIR = Path.home() / "AppData/Local/WingetManager"
SELECTION_FILE = PERSIST_DIR / "selection.json"

# ── 设计令牌（与网页版一致）──
BG = "#F4F5FA"
CARD = "#FFFFFF"
LINE = "#E8EAF3"
LINE_SOFT = "#F1F2F8"
TEXT = "#181C28"
MUTED = "#6A7183"
MUTED2 = "#9AA1B5"
ACCENT = "#6366F1"
ACCENT_2 = "#8B5CF6"
ACCENT_DEEP = "#4F46E5"
ACCENT_SOFT = "#EEF0FF"
ACCENT_LINE = "#D9DCFF"
OK = "#0B7A55"
OK_BG = "#E7F8F1"
OK_LINE = "#BFEADD"
WARN = "#B45309"
WARN_BG = "#FFF8E8"
WARN_LINE = "#F5E2BB"
ERR = "#C53030"
LOG_BG = "#0D1017"
LOG_FG = "#C9D1D9"

FONT_FAMILY = "HarmonyOS Sans SC"
MONO_FAMILY = "Consolas"

CAT_TONES = [
    ("#6366F1", "#8B5CF6"), ("#3B82F6", "#06B6D4"), ("#10B981", "#14B8A6"),
    ("#F59E0B", "#F97316"), ("#EC4899", "#F43F5E"), ("#8B5CF6", "#A855F7"),
    ("#0EA5E9", "#6366F1"), ("#64748B", "#94A3B8"),
]


def _tone_for(category: str) -> tuple:
    return CAT_TONES[sum(ord(c) for c in category) % len(CAT_TONES)]


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


def app_icon() -> QIcon:
    pm = QPixmap(128, 128)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    r = QRectF(6, 6, 116, 116)
    path = QPainterPath()
    path.addRoundedRect(r, 30, 30)
    p.fillPath(path, QBrush(_grad(r)))
    p.setPen(QColor("#FFFFFF"))
    f = QFont("Segoe UI", 58)
    f.setBold(True)
    p.setFont(f)
    p.drawText(r, Qt.AlignCenter, "W")
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
class GradientLabel(QWidget):
    """渐变文字（对应网页 h1 的 background-clip:text）。"""

    def __init__(self, text: str, size_pt: float = 20, bold: bool = True,
                 parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._text = text
        self._font = QFont(FONT_FAMILY)
        self._font.setPixelSize(int(size_pt * 96 / 72))
        self._font.setBold(bold)
        fm = QFontMetrics(self._font)
        self.setFixedHeight(fm.height() + 2)
        self.setMinimumWidth(fm.horizontalAdvance(text) + 2)

    def setText(self, text: str) -> None:  # noqa: N802
        self._text = text
        fm = QFontMetrics(self._font)
        self.setMinimumWidth(fm.horizontalAdvance(text) + 2)
        self.update()

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        p.setRenderHint(QPainter.TextAntialiasing)
        p.setFont(self._font)
        w = max(self.width(), QFontMetrics(self._font).horizontalAdvance(self._text) + 2)
        rect = QRectF(0, 0, w, self.height())
        p.setPen(QPen(QBrush(_grad(rect)), 0))
        p.drawText(rect, Qt.AlignLeft | Qt.AlignVCenter, self._text)
        p.end()


class StatCard(QWidget):
    """统计卡：白底圆角 + 顶部渐变条 + 数值（高亮卡渐变字）。"""

    def __init__(self, caption: str, value: str = "0", highlight: bool = False,
                 parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._caption = caption
        self._value = value
        self._hl = highlight
        self._hover = 0.0
        self.setFixedSize(104, 70)
        self.setAttribute(Qt.WA_Hover, True)
        self._anim = QPropertyAnimation(self, b"hover", self)
        self._anim.setDuration(160)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)

    def get_hover(self) -> float:
        return self._hover

    def set_hover(self, v: float) -> None:
        self._hover = v
        self.update()

    hover = Property(float, get_hover, set_hover)

    def set_value(self, value: str) -> None:
        if value != self._value:
            self._value = value
            self.update()

    def enterEvent(self, _e) -> None:  # noqa: N802
        self._anim.stop(); self._anim.setStartValue(self._hover)
        self._anim.setEndValue(1.0); self._anim.start()

    def leaveEvent(self, _e) -> None:  # noqa: N802
        self._anim.stop(); self._anim.setStartValue(self._hover)
        self._anim.setEndValue(0.0); self._anim.start()

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        p.setRenderHint(QPainter.TextAntialiasing)
        dy = -2.0 * self._hover
        r = QRectF(0.5, 0.5 + dy, self.width() - 1, self.height() - 1)
        path = QPainterPath()
        path.addRoundedRect(r, 12, 12)
        p.fillPath(path, QColor(CARD))
        p.save()
        p.setClipPath(path)
        p.fillRect(QRectF(r.left(), r.top(), r.width(), 3),
                   QBrush(_grad(QRectF(r.left(), 0, r.width(), 1))))
        if self._hover > 0.01:
            p.fillRect(r, QColor(99, 102, 241, int(9 * self._hover)))
        p.restore()
        p.setPen(QPen(QColor("#DFE2F0" if self._hover > 0.5 else LINE), 1))
        p.setBrush(Qt.NoBrush)
        p.drawPath(path)
        vf = QFont(FONT_FAMILY)
        vf.setPixelSize(23)
        vf.setBold(True)
        p.setFont(vf)
        if self._hl:
            p.setPen(QPen(QBrush(_grad(QRectF(r.left(), 0, r.width(), 1))), 0))
        else:
            p.setPen(QColor(TEXT))
        p.drawText(QRectF(r.left(), r.top() + 8, r.width(), 30),
                   Qt.AlignHCenter | Qt.AlignVCenter, self._value)
        cf = QFont(FONT_FAMILY)
        cf.setPixelSize(11)
        p.setFont(cf)
        p.setPen(QColor(MUTED))
        p.drawText(QRectF(r.left(), r.top() + 38, r.width(), 20),
                   Qt.AlignHCenter | Qt.AlignVCenter, self._caption)
        p.end()


class CheckBox(QWidget):
    """19px 圆角复选框：勾选渐变填充 + 光晕 + 描线动画。"""

    def __init__(self, checked: bool = False, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.setFixedSize(20, 20)
        self._checked = checked
        self._prog = 1.0 if checked else 0.0
        self._hover = False
        self._anim = QPropertyAnimation(self, b"progress", self)
        self._anim.setDuration(180)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)

    def set_checked(self, on: bool, animate: bool = True) -> None:
        self._checked = on
        target = 1.0 if on else 0.0
        if not animate:
            self._prog = target
            self.update()
            return
        self._anim.stop()
        self._anim.setStartValue(self._prog)
        self._anim.setEndValue(target)
        self._anim.start()

    def is_checked(self) -> bool:
        return self._checked

    def get_progress(self) -> float:
        return self._prog

    def set_progress(self, v: float) -> None:
        self._prog = v
        self.update()

    progress = Property(float, get_progress, set_progress)

    def enterEvent(self, _e) -> None:  # noqa: N802
        self._hover = True
        self.update()

    def leaveEvent(self, _e) -> None:  # noqa: N802
        self._hover = False
        self.update()

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        r = QRectF(1.6, 1.6, 16.8, 16.8)
        path = QPainterPath()
        path.addRoundedRect(r, 6, 6)
        if self._prog > 0.05:
            p.setPen(Qt.NoPen)
            p.setBrush(QColor(99, 102, 241, int(34 * self._prog)))
            p.drawRoundedRect(r.adjusted(-1.8, -0.8, 1.8, 2.8), 7, 7)
            p.fillPath(path, QBrush(_grad(r)))
        if self._prog < 0.98:
            border = QColor(ACCENT) if (self._hover and self._prog < 0.05) else QColor("#C3C8D8")
            p.setPen(QPen(border, 1.5))
            p.setBrush(QColor(255, 255, 255, int(255 * (1 - self._prog))))
            p.drawPath(path)
        if self._prog > 0.05:
            p.setOpacity(self._prog)
            pen = QPen(QColor("#FFFFFF"), 2.0)
            pen.setCapStyle(Qt.RoundCap)
            pen.setJoinStyle(Qt.RoundJoin)
            p.setPen(pen)
            ck = QPainterPath()
            ck.moveTo(5.2, 10.4)
            ck.lineTo(8.3, 13.2)
            ck.lineTo(14.0, 6.8)
            p.drawPath(ck)
        p.end()


class MiniProgress(QWidget):
    """3px 渐变流动进度条（对应网页 shimmer）。"""

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.setFixedHeight(3)
        self._phase = 0.0
        self._timer = QTimer(self)
        self._timer.setInterval(16)
        self._timer.timeout.connect(self._tick)

    def start(self) -> None:
        if not self._timer.isActive():
            self._phase = 0.0
            self._timer.start()
        self.show()

    def stop(self) -> None:
        self._timer.stop()
        self.hide()

    def _tick(self) -> None:
        self._phase = (self._phase + 0.009) % 1.0
        self.update()

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setPen(Qt.NoPen)
        p.fillRect(self.rect(), QColor("#E9EBF4"))
        w = self.width()
        seg = max(180, int(w * 0.32))
        x = int((w + seg) * self._phase) - seg
        g = QLinearGradient(x, 0, x + seg, 0)
        g.setColorAt(0.0, QColor(ACCENT))
        g.setColorAt(0.5, QColor(ACCENT_2))
        g.setColorAt(1.0, QColor(ACCENT))
        p.fillRect(QRect(x, 0, seg, 3), QBrush(g))
        p.end()


# ══════════════════════════ 列表行 / 分组 ══════════════════════════
class PackageRow(QWidget):
    toggled = Signal(object)
    context_requested = Signal(object, QPoint)
    copy_id = Signal(object)
    nav_requested = Signal(object, int)

    def __init__(self, pkg, checked: bool, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.pkg = pkg
        self._checked = checked
        self._hover = 0.0
        self._narrow = False
        self.setFixedHeight(46)
        self.setFocusPolicy(Qt.StrongFocus)
        self.setCursor(Qt.PointingHandCursor)
        self.setAttribute(Qt.WA_Hover, True)

        lay = QHBoxLayout(self)
        lay.setContentsMargins(16, 0, 16, 0)
        lay.setSpacing(0)
        self.box = CheckBox(checked)
        self.box.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        lay.addWidget(self.box, 0, Qt.AlignVCenter)
        lay.addSpacing(13)

        name_box = QVBoxLayout()
        name_box.setContentsMargins(0, 0, 0, 0)
        name_box.setSpacing(1)
        self.lbl_name = QLabel(pkg.name, self)
        self.lbl_name.setToolTip(pkg.name)
        # Ignored：长文本被裁剪而非撑宽行，避免横向错位
        self.lbl_name.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        self.lbl_name.setStyleSheet(
            f"color:{TEXT}; font-size:10pt; font-weight:500; background:transparent;")
        name_box.addWidget(self.lbl_name)
        self.lbl_id = QLabel(pkg.packageIdentifier, self)
        self.lbl_id.setToolTip(pkg.packageIdentifier)
        self.lbl_id.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        self.lbl_id.setStyleSheet(
            f"color:{MUTED2}; font-family:'{MONO_FAMILY}'; font-size:8.5pt; background:transparent;")
        name_box.addWidget(self.lbl_id)
        lay.addLayout(name_box, 1)

        self.tag = QLabel(self)
        self.tag.setAlignment(Qt.AlignCenter)
        dep = pkg.category == "运行库与组件" or getattr(pkg, "isDependency", False)
        if dep:
            self.tag.setText("运行库")
            self.tag.setStyleSheet(
                f"color:{WARN}; background:{WARN_BG}; border:1px solid {WARN_LINE};"
                "border-radius:6px; padding:2px 7px; font-size:8pt;")
        else:
            self.tag.setText(pkg.source or "winget")
            self.tag.setStyleSheet(
                "color:#6D4FD0; background:#F3F0FE; border:1px solid #E5DFFB;"
                "border-radius:6px; padding:2px 7px; font-size:8pt;")
        lay.addWidget(self.tag, 0, Qt.AlignVCenter)
        lay.addSpacing(14)

        self.lbl_ver = QLabel(f"v{pkg.version}" if pkg.version else "—", self)
        self.lbl_ver.setFixedWidth(110)
        self.lbl_ver.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        self.lbl_ver.setStyleSheet(
            f"color:{MUTED}; font-family:'{MONO_FAMILY}'; font-size:9pt; background:transparent;")
        lay.addWidget(self.lbl_ver, 0)

        self._anim = QPropertyAnimation(self, b"hover", self)
        self._anim.setDuration(130)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)

    def get_hover(self) -> float:
        return self._hover

    def set_hover(self, v: float) -> None:
        self._hover = v
        self.update()

    hover = Property(float, get_hover, set_hover)

    def _anim_hover(self, target: float) -> None:
        self._anim.stop()
        self._anim.setStartValue(self._hover)
        self._anim.setEndValue(target)
        self._anim.start()

    def enterEvent(self, _e) -> None:  # noqa: N802
        self._anim_hover(1.0)

    def leaveEvent(self, _e) -> None:  # noqa: N802
        self._anim_hover(0.0)

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        if self._hover > 0.01:
            p.fillRect(self.rect(), QColor(247, 248, 254, int(255 * self._hover)))
        if self.hasFocus():
            p.setPen(QPen(QColor(ACCENT), 1.2))
            p.setBrush(Qt.NoBrush)
            p.drawRoundedRect(QRectF(6, 3, self.width() - 12, self.height() - 6), 8, 8)
        p.setPen(QPen(QColor(LINE_SOFT), 1))
        p.drawLine(16, self.height() - 1, self.width() - 16, self.height() - 1)
        p.end()

    def mouseReleaseEvent(self, e) -> None:  # noqa: N802
        if e.button() == Qt.LeftButton and self.rect().contains(e.position().toPoint()):
            self.setFocus()
            self.toggle()
        super().mouseReleaseEvent(e)

    def contextMenuEvent(self, e) -> None:  # noqa: N802
        self.context_requested.emit(self, e.globalPos())

    def keyPressEvent(self, e) -> None:  # noqa: N802
        if e.key() == Qt.Key_Space:
            self.toggle()
        elif e.key() in (Qt.Key_Return, Qt.Key_Enter):
            self.copy_id.emit(self.pkg)
        elif e.key() in (Qt.Key_Up, Qt.Key_Down):
            self.nav_requested.emit(self, 1 if e.key() == Qt.Key_Down else -1)
        else:
            super().keyPressEvent(e)

    def toggle(self) -> None:
        self._checked = not self._checked
        self.box.set_checked(self._checked)
        self.toggled.emit(self.pkg)

    def set_narrow(self, on: bool) -> None:
        """窄窗口：隐藏来源标签与版本列，为名称让出空间。"""
        if self._narrow == on:
            return
        self._narrow = on
        self.tag.setVisible(not on)
        self.lbl_ver.setVisible(not on)

    def set_checked(self, on: bool) -> None:
        self._checked = on
        self.box.set_checked(on, animate=False)


class CountPill(QWidget):
    """分组计数胶囊：随勾选进度按比例填充，勾满时整颗渐变高亮。"""

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._sel = 0
        self._total = 0
        self._ratio = 0.0
        self.setFixedHeight(20)
        self.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        f = QFont(self.font())
        f.setPointSizeF(7.8)
        f.setWeight(QFont.DemiBold)
        f.setLetterSpacing(QFont.AbsoluteSpacing, 0.15)
        self.setFont(f)
        self._anim = QPropertyAnimation(self, b"ratio", self)
        self._anim.setDuration(280)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)
        self._sync_size()

    # ── 填充比例（动画驱动） ──
    def get_ratio(self) -> float:
        return self._ratio

    def set_ratio(self, v: float) -> None:
        self._ratio = max(0.0, min(1.0, v))
        self.update()

    ratio = Property(float, get_ratio, set_ratio)

    def _sync_size(self) -> None:
        """按最大可能位数定宽，避免计数变化时胶囊抖动。"""
        fm = QFontMetrics(self.font())
        anchor = f"{self._total}/{self._total}"
        self.setFixedWidth(max(40, fm.horizontalAdvance(anchor) + 19))

    def set_count(self, sel: int, total: int, animate: bool = True) -> None:
        self._sel, self._total = sel, total
        self._sync_size()
        target = (sel / total) if total else 0.0
        if animate and self.isVisible():
            self._anim.stop()
            self._anim.setStartValue(self._ratio)
            self._anim.setEndValue(target)
            self._anim.start()
        else:
            self._anim.stop()
            self.set_ratio(target)

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        r = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        rad = r.height() / 2
        path = QPainterPath()
        path.addRoundedRect(r, rad, rad)

        full = self._ratio >= 0.999
        # 底：极浅竖向渐变，给胶囊一点微立体感
        base = QLinearGradient(0, r.top(), 0, r.bottom())
        base.setColorAt(0, QColor("#F8F9FE"))
        base.setColorAt(1, QColor("#EFF1FA"))
        p.fillPath(path, QBrush(base))

        # 进度填充：勾选比例以极淡的靛紫铺满左侧
        if self._ratio > 0.001:
            p.save()
            p.setClipPath(path)
            fill_w = r.width() * self._ratio
            g = QLinearGradient(r.left(), 0, r.left() + max(fill_w, 1.0), 0)
            if full:
                g.setColorAt(0, QColor(ACCENT))
                g.setColorAt(0.55, QColor("#7C67F3"))
                g.setColorAt(1, QColor(ACCENT_2))
            else:
                g.setColorAt(0, QColor(99, 102, 241, 30))
                g.setColorAt(1, QColor(139, 92, 246, 40))
            p.fillRect(QRectF(r.left(), r.top(), fill_w, r.height()), QBrush(g))
            p.restore()

        # 描边：勾满时用浅靛蓝描边，与填充呼应
        p.setPen(QPen(QColor("#C9CDFB") if full else QColor("#E7EAF5"), 1))
        p.setBrush(Qt.NoBrush)
        p.drawPath(path)

        # 文字：选中数（实） + 总数（虚）
        fm = QFontMetrics(self.font())
        a = str(self._sel)
        b = f"/{self._total}"
        wa, wb = fm.horizontalAdvance(a), fm.horizontalAdvance(b)
        x = r.center().x() - (wa + wb) / 2.0
        y = r.center().y() + (fm.ascent() - fm.descent()) / 2.0
        if full:
            p.setPen(QColor(255, 255, 255, 248))
            p.drawText(QPointF(x, y), a)
            p.setPen(QColor(255, 255, 255, 180))
            p.drawText(QPointF(x + wa, y), b)
        else:
            p.setPen(QColor("#535B6E") if self._sel else QColor("#9AA3B5"))
            p.drawText(QPointF(x, y), a)
            p.setPen(QColor("#AEB5C4"))
            p.drawText(QPointF(x + wa, y), b)
        p.end()


class Chevron(QWidget):
    """自绘下拉三角：随展开/收起平滑旋转，悬停加深。"""

    def __init__(self, expanded: bool, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._angle = 90.0 if expanded else 0.0
        self._hover = 0.0
        self.setFixedSize(18, 18)
        self.setAttribute(Qt.WA_Hover, True)
        self._anim = QPropertyAnimation(self, b"hoverT", self)
        self._anim.setDuration(150)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)

    def get_angle(self) -> float:
        return self._angle

    def set_angle(self, v: float) -> None:
        self._angle = v
        self.update()

    angle = Property(float, get_angle, set_angle)

    def get_hover(self) -> float:
        return self._hover

    def set_hover(self, v: float) -> None:
        self._hover = v
        self.update()

    hoverT = Property(float, get_hover, set_hover)

    def _fade(self, to: float) -> None:
        self._anim.stop()
        self._anim.setStartValue(self._hover)
        self._anim.setEndValue(to)
        self._anim.start()

    def enterEvent(self, _e) -> None:  # noqa: N802
        self._fade(1.0)

    def leaveEvent(self, _e) -> None:  # noqa: N802
        self._fade(0.0)

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        p.translate(self.width() / 2, self.height() / 2)
        p.rotate(self._angle)
        p.setPen(Qt.NoPen)
        base = QColor("#8A93A6")
        hot = QColor("#4F46E5")
        t = self._hover
        col = QColor(int(base.red() + (hot.red() - base.red()) * t),
                     int(base.green() + (hot.green() - base.green()) * t),
                     int(base.blue() + (hot.blue() - base.blue()) * t))
        p.setBrush(col)
        s = 4.6
        p.drawPolygon(QPolygonF([QPointF(s, 0),
                                 QPointF(-s * 0.62, -s * 0.86),
                                 QPointF(-s * 0.62, s * 0.86)]))
        p.end()


class LinkLabel(QLabel):
    """分组头右侧文字链接：悬停浮现浅靛蓝底色。"""

    def __init__(self, text: str, parent: QWidget | None = None) -> None:
        super().__init__(text, parent)
        self._hover = 0.0
        self.setCursor(Qt.PointingHandCursor)
        f = QFont(self.font())
        f.setPointSizeF(8.6)
        f.setWeight(QFont.Medium)
        self.setFont(f)
        self._anim = QPropertyAnimation(self, b"hoverT", self)
        self._anim.setDuration(150)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)
        self._fit()

    def get_hover(self) -> float:
        return self._hover

    def set_hover(self, v: float) -> None:
        self._hover = v
        self.update()

    hoverT = Property(float, get_hover, set_hover)

    def _fit(self) -> None:
        fm = QFontMetrics(self.font())
        self.setFixedSize(fm.horizontalAdvance(self.text()) + 18, 24)

    def setText(self, text: str) -> None:  # noqa: N802
        super().setText(text)
        self._fit()

    def _fade(self, to: float) -> None:
        self._anim.stop()
        self._anim.setStartValue(self._hover)
        self._anim.setEndValue(to)
        self._anim.start()

    def enterEvent(self, _e) -> None:  # noqa: N802
        self._fade(1.0)

    def leaveEvent(self, _e) -> None:  # noqa: N802
        self._fade(0.0)

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        r = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        path = QPainterPath()
        path.addRoundedRect(r, 6.5, 6.5)
        if self._hover > 0.001:
            c = QColor(ACCENT_SOFT)
            c.setAlphaF(min(1.0, self._hover))
            p.fillPath(path, c)
            line = QColor(ACCENT_LINE)
            line.setAlphaF(min(1.0, self._hover * 0.9))
            p.setPen(QPen(line, 1))
            p.drawPath(path)
        col = QColor(ACCENT_DEEP)
        p.setPen(col)
        p.setFont(self.font())
        p.drawText(self.rect(), Qt.AlignCenter, self.text())
        p.end()


class GroupHeader(QWidget):
    """分组头：旋转三角 + 名称 + 计数胶囊 + 全选本组。"""

    clicked = Signal()
    select_all = Signal()

    def __init__(self, title: str, expanded: bool, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._title = title
        self._hover = 0.0
        self.setFixedHeight(42)
        self.setCursor(Qt.PointingHandCursor)
        self.setAttribute(Qt.WA_Hover, True)
        lay = QHBoxLayout(self)
        lay.setContentsMargins(22, 0, 14, 0)
        lay.setSpacing(0)
        self.chevron = Chevron(expanded, self)
        lay.addWidget(self.chevron)
        lay.addSpacing(9)
        self.title = QLabel(title, self)
        self.title.setStyleSheet(
            f"color:{TEXT}; font-size:9.5pt; font-weight:600; background:transparent;")
        lay.addWidget(self.title)
        lay.addSpacing(9)
        self.count = CountPill(self)
        lay.addWidget(self.count, 0, Qt.AlignVCenter)
        lay.addStretch(1)
        self.sel_all = LinkLabel("全选本组", self)
        lay.addWidget(self.sel_all, 0, Qt.AlignVCenter)

        self._hover_anim = QPropertyAnimation(self, b"hoverT", self)
        self._hover_anim.setDuration(170)
        self._hover_anim.setEasingCurve(QEasingCurve.OutCubic)

    # ── 悬停渐变（0→1） ──
    def get_hover(self) -> float:
        return self._hover

    def set_hover(self, v: float) -> None:
        self._hover = v
        self.update()

    hoverT = Property(float, get_hover, set_hover)

    def _fade(self, to: float) -> None:
        self._hover_anim.stop()
        self._hover_anim.setStartValue(self._hover)
        self._hover_anim.setEndValue(to)
        self._hover_anim.start()

    def enterEvent(self, _e) -> None:  # noqa: N802
        self._fade(1.0)

    def leaveEvent(self, _e) -> None:  # noqa: N802
        self._fade(0.0)

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        h = self.height()
        top = QColor("#FBFDFF")
        bot = QColor("#F8F9FD")
        if self._hover > 0.001:
            t = self._hover
            top = QColor(int(251 + (246 - 251) * t), int(253 + (248 - 253) * t),
                         int(255 + (254 - 255) * t))
            bot = QColor(int(248 + (240 - 248) * t), int(249 + (243 - 249) * t),
                         int(253 + (251 - 253) * t))
        g = QLinearGradient(0, 0, 0, h)
        g.setColorAt(0, top)
        g.setColorAt(1, bot)
        p.fillRect(self.rect(), QBrush(g))
        p.setPen(QPen(QColor(LINE_SOFT), 1))
        p.drawLine(0, h - 1, self.width(), h - 1)
        p.end()

    def mouseReleaseEvent(self, e) -> None:  # noqa: N802
        if e.button() == Qt.LeftButton:
            if self.sel_all.geometry().contains(e.position().toPoint()):
                self.select_all.emit()
            else:
                self.clicked.emit()
        super().mouseReleaseEvent(e)

    def set_count(self, sel: int, total: int, animate: bool = True) -> None:
        self.count.set_count(sel, total, animate)


class GroupSection(QWidget):
    """分组：头部 + 可折叠内容。

    折叠动画不做「逐帧压缩子项布局」（20 行每帧重排会掉帧），
    而是把内容装进固定高度的 clip 容器，只动画 clip 的裁剪高度，
    子项布局在整个过程中只算一次，帧率稳定。
    """

    def __init__(self, title: str, expanded: bool, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.title = title
        self.expanded = expanded
        lay = QVBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.setSpacing(0)
        self.header = GroupHeader(title, expanded, self)
        self.header.clicked.connect(self.toggle)
        lay.addWidget(self.header)

        self.clip = QWidget(self)
        clip_lay = QVBoxLayout(self.clip)
        clip_lay.setContentsMargins(0, 0, 0, 0)
        clip_lay.setSpacing(0)
        self.body = QWidget(self.clip)
        self.body_lay = QVBoxLayout(self.body)
        self.body_lay.setContentsMargins(0, 0, 0, 2)
        self.body_lay.setSpacing(0)
        clip_lay.addWidget(self.body, 0, Qt.AlignTop)
        lay.addWidget(self.clip)
        self._content_h = 0
        if not expanded:
            self.clip.setMaximumHeight(0)
            self.clip.setVisible(False)

        self._anim = QPropertyAnimation(self.clip, b"maximumHeight", self)
        self._anim.finished.connect(self._after)
        self._rot = QPropertyAnimation(self.header.chevron, b"angle", self)

    def add_row(self, row: QWidget) -> None:
        self.body_lay.addWidget(row)
        self._content_h = 0        # 内容变化后重新测量

    def prepare(self) -> None:
        """行填充完成后预冻结高度：点击时不再临时测量/重排。"""
        h = self._content_height()
        if self.expanded:
            self.clip.setMaximumHeight(h)

    def _content_height(self) -> int:
        """内容真实高度：冻结为定值，避免逐帧重排。"""
        self.body_lay.activate()
        h = max(self.body_lay.sizeHint().height(), 0)
        self.body.setFixedHeight(h)
        self._content_h = h
        return h

    def toggle(self) -> None:
        self.expanded = not self.expanded
        expanding = self.expanded
        self._anim.stop()
        self._rot.stop()

        if expanding:
            target = self._content_h or self._content_height()
            self.clip.setVisible(True)
            self.clip.setMaximumHeight(0)
            start, end = 0, target
            dur, curve = 300, QEasingCurve.OutQuart
        else:
            cur = max(self.clip.height(), 1)
            self.clip.setMaximumHeight(cur)
            start, end = cur, 0
            dur, curve = 210, QEasingCurve.InOutCubic

        self._anim.setDuration(dur)
        self._anim.setEasingCurve(curve)
        self._anim.setStartValue(start)
        self._anim.setEndValue(end)
        self._anim.start()

        # 三角旋转略短于高度动画：先“咬合”到位，收尾更利落
        self._rot.setDuration(int(dur * 0.72))
        self._rot.setEasingCurve(curve)
        self._rot.setStartValue(self.header.chevron.angle)
        self._rot.setEndValue(90.0 if expanding else 0.0)
        self._rot.start()

    def _after(self) -> None:
        # 展开结束时保持动画终值（= 内容高度），不再放大上限，
        # 否则会触发一次整表重排 + 滚动条范围重算，末尾会明显“顿”一下。
        if not self.expanded:
            self.clip.setVisible(False)
            self.clip.setMaximumHeight(0)


# ══════════════════════════ 窗口外壳 / 标题栏 ══════════════════════════
class Shell(QWidget):
    """圆角窗口底：淡紫底 + 双径向光晕。"""

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._radius = 16
        self._glows: list = []

    def set_radius(self, r: int) -> None:
        self._radius = r
        self._recalc()
        self.update()

    def resizeEvent(self, e) -> None:  # noqa: N802
        super().resizeEvent(e)
        self._recalc()

    def _recalc(self) -> None:
        w, h = max(self.width(), 1), max(self.height(), 1)
        self._glows = [
            (QPointF(w * 0.95, -h * 0.08), max(w, h) * 0.52, QColor(139, 92, 246, 32)),
            (QPointF(-w * 0.06, -h * 0.03), max(w, h) * 0.42, QColor(99, 102, 241, 34)),
        ]

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        r = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        path = QPainterPath()
        path.addRoundedRect(r, self._radius, self._radius)
        p.setClipPath(path)
        p.fillPath(path, QColor(BG))
        for center, radius, color in self._glows:
            g = QRadialGradient(center, radius)
            g.setColorAt(0.0, color)
            g.setColorAt(1.0, QColor(color.red(), color.green(), color.blue(), 0))
            p.fillRect(self.rect(), QBrush(g))
        p.end()


class BrandMark(QWidget):
    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.setFixedSize(24, 24)

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        r = QRectF(0, 0, 24, 24)
        path = QPainterPath()
        path.addRoundedRect(r, 7, 7)
        p.fillPath(path, QBrush(_grad(r)))
        p.setPen(QColor("#FFFFFF"))
        f = QFont("Segoe UI", 11)
        f.setBold(True)
        p.setFont(f)
        p.drawText(r, Qt.AlignCenter, "W")
        p.end()


class TitleBar(QWidget):
    def __init__(self, host: "MainWindow") -> None:
        super().__init__(host)
        self._host = host
        self.setFixedHeight(52)
        lay = QHBoxLayout(self)
        lay.setContentsMargins(18, 0, 10, 0)
        lay.setSpacing(0)
        lay.addWidget(BrandMark(self))
        lay.addSpacing(9)
        title = QLabel(APP_TITLE, self)
        title.setStyleSheet(f"color:{TEXT}; font-size:10pt; font-weight:600; background:transparent;")
        lay.addWidget(title)
        lay.addStretch(1)
        self.btns = {}
        for key, glyph, hbg, hfg in (("min", "—", "#F1F2F8", TEXT),
                                     ("max", "□", "#F1F2F8", TEXT),
                                     ("close", "✕", "#E5484D", "#FFFFFF")):
            b = QPushButton(glyph, self)
            b.setFixedSize(30, 28)
            b.setCursor(Qt.PointingHandCursor)
            b.setFocusPolicy(Qt.NoFocus)
            b.setStyleSheet(
                "QPushButton{border:none; border-radius:7px; color:#6A7183;"
                "font-size:9.5pt; background:transparent;}"
                f"QPushButton:hover{{background:{hbg}; color:{hfg};}}")
            lay.addWidget(b)
            self.btns[key] = b
        self.btns["min"].clicked.connect(host.showMinimized)
        self.btns["max"].clicked.connect(host.toggle_max)
        self.btns["close"].clicked.connect(host.close)

    def mousePressEvent(self, e) -> None:  # noqa: N802
        if e.button() == Qt.LeftButton:
            wh = self._host.windowHandle()
            if wh is not None:
                wh.startSystemMove()

    def mouseDoubleClickEvent(self, e) -> None:  # noqa: N802
        if e.button() == Qt.LeftButton:
            self._host.toggle_max()

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setPen(QPen(QColor("#EDEFF7"), 1))
        p.drawLine(0, self.height() - 1, self.width(), self.height() - 1)
        p.end()


# ══════════════════════════ 后台线程 ══════════════════════════
class ScanWorker(QThread):
    ok = Signal(object)
    err = Signal(str)

    def __init__(self, catalog: CatalogService) -> None:
        super().__init__()
        self._catalog = catalog

    def run(self) -> None:
        try:
            self.ok.emit(self._catalog.get_catalog(force=True))
        except Exception as exc:  # noqa: BLE001
            self.err.emit(str(exc))


# ══════════════════════════ 导出对话框 ══════════════════════════
class ExportDialog(QDialog):
    def __init__(self, app: "MainWindow", selected: list) -> None:
        super().__init__(app)
        self.app = app
        self.selected = selected
        self.setWindowTitle("导出配置")
        self.setModal(True)
        self.setFixedWidth(560)
        self.setWindowIcon(app_icon())
        outer = QVBoxLayout(self)
        outer.setContentsMargins(0, 0, 0, 0)
        self.stack = QStackedWidget(self)
        outer.addWidget(self.stack)
        self.stack.addWidget(self._build_form())
        self._files: list = []

    def _build_form(self) -> QWidget:
        page = QWidget(self)
        page.setStyleSheet(f"background:{CARD};")
        lay = QVBoxLayout(page)
        lay.setContentsMargins(28, 26, 28, 22)
        lay.setSpacing(0)
        lay.addWidget(GradientLabel("导出配置", 17))
        lay.addSpacing(4)
        export_path = Path("D:/apps.json")
        exists = export_path.is_file()
        desc = QLabel(f"将已勾选的 {len(self.selected)} 个软件包以 winget 官方格式"
                      '导出到 "D:\\apps.json"', page)
        desc.setWordWrap(True)
        desc.setStyleSheet(f"color:{MUTED}; font-size:9pt;")
        lay.addWidget(desc)
        lay.addSpacing(18)
        self.hint = QLabel("", page)
        self.hint.setFixedHeight(22)
        self.hint.setStyleSheet(f"color:{MUTED2}; font-size:8.5pt;")
        lay.addWidget(self.hint)
        self.hint.setText("目标文件已存在，导出将覆盖" if exists
                          else "winget import 标准格式，可用 winget import -i 直接恢复")
        if exists:
            self.hint.setStyleSheet(f"color:{WARN}; font-size:8.5pt;")
            self.ok_text = "覆盖导出"
        else:
            self.ok_text = "立即导出"
        lay.addSpacing(6)
        btns = QHBoxLayout()
        btns.setContentsMargins(0, 20, 0, 0)
        btns.addStretch(1)
        cancel = QPushButton("取消", page)
        cancel.setCursor(Qt.PointingHandCursor)
        cancel.setFixedHeight(38)
        cancel.setStyleSheet(
            f"QPushButton{{border:1px solid {LINE}; border-radius:10px; padding:0 18px;"
            f"background:#fff; color:{TEXT}; font-size:9.5pt;}}"
            "QPushButton:hover{background:#FAFBFE; border-color:#CDD2E2;}")
        cancel.clicked.connect(self.reject)
        btns.addWidget(cancel)
        btns.addSpacing(9)
        self.ok_btn = QPushButton(self.ok_text, page)
        self.ok_btn.setCursor(Qt.PointingHandCursor)
        self.ok_btn.setFixedHeight(38)
        self.ok_btn.setStyleSheet(
            "QPushButton{border:none; border-radius:10px; padding:0 22px; color:#fff;"
            "font-size:9.5pt; font-weight:600;"
            f"background:qlineargradient(x1:0,y1:0,x2:1,y2:1,stop:0 {ACCENT},stop:1 {ACCENT_2});}}"
            "QPushButton:hover{background:qlineargradient(x1:0,y1:0,x2:1,y2:1,"
            "stop:0 #575AF0,stop:1 #7C4FF2);}"
            f"QPushButton:disabled{{background:#E9EBF4; color:{MUTED2};}}")
        _glow(self.ok_btn, blur=18, dy=4, alpha=90)
        self.ok_btn.clicked.connect(self._do_export)
        btns.addWidget(self.ok_btn)
        lay.addLayout(btns)
        lay.addStretch(1)
        return page

    def _do_export(self) -> None:
        snapshot = self.app.snapshot
        out = Path("D:/apps.json")
        self.ok_btn.setEnabled(False)
        self.hint.setStyleSheet(f"color:{MUTED2}; font-size:8.5pt;")
        self.hint.setText("正在导出…")
        QApplication.processEvents()
        try:
            payload = build_winget_manifest(
                self.selected,
                snapshot.wingetVersion if snapshot else "1.10.0")
            out.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
                           encoding="utf-8")
        except AppError as exc:
            self.ok_btn.setEnabled(True)
            self.hint.setStyleSheet(f"color:{ERR}; font-size:8.5pt;")
            self.hint.setText(exc.user_message)
            return
        except Exception as exc:  # noqa: BLE001
            self.ok_btn.setEnabled(True)
            self.hint.setStyleSheet(f"color:{ERR}; font-size:8.5pt;")
            self.hint.setText(f"导出失败：{exc}")
            return
        self._files = [str(out)]
        self.app.log_append("done", f"已导出 {len(self.selected)} 个软件包：")
        self.app.log_append("info", f"  {out}")
        self.stack.addWidget(self._build_success(len(self.selected)))
        self.stack.setCurrentIndex(1)

    def _build_success(self, count: int) -> QWidget:
        page = QWidget(self)
        page.setStyleSheet(f"background:{CARD};")
        lay = QVBoxLayout(page)
        lay.setContentsMargins(28, 26, 28, 22)
        lay.setSpacing(0)
        badge = QLabel("✓", page)
        badge.setFixedSize(46, 46)
        badge.setAlignment(Qt.AlignCenter)
        badge.setStyleSheet(
            "color:#fff; border-radius:23px; font-size:17pt; font-weight:700;"
            f"background:qlineargradient(x1:0,y1:0,x2:1,y2:1,stop:0 {ACCENT},stop:1 {ACCENT_2});")
        lay.addWidget(badge)
        lay.addSpacing(14)
        lay.addWidget(GradientLabel(f"已导出 {count} 个软件包", 15))
        lay.addSpacing(10)
        for f in self._files:
            item = QLabel(Path(f).name, page)
            item.setStyleSheet(f"color:{MUTED}; font-family:'{MONO_FAMILY}'; font-size:8.5pt;")
            lay.addWidget(item)
        lay.addSpacing(16)
        self._open_flag = {"v": True}
        row = QWidget(page)
        rl = QHBoxLayout(row)
        rl.setContentsMargins(0, 0, 0, 0)
        rl.setSpacing(9)
        self.open_box = CheckBox(True, row)
        self.open_box.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        rl.addWidget(self.open_box)
        lbl = QLabel("导出后打开文件夹", row)
        lbl.setStyleSheet(f"color:{TEXT}; font-size:9pt;")
        rl.addWidget(lbl)
        rl.addStretch(1)

        def _toggle(_e=None):
            self._open_flag["v"] = not self._open_flag["v"]
            self.open_box.set_checked(self._open_flag["v"])

        row.mouseReleaseEvent = _toggle
        lbl.mouseReleaseEvent = _toggle
        lay.addWidget(row)
        btns = QHBoxLayout()
        btns.setContentsMargins(0, 20, 0, 0)
        btns.addStretch(1)
        close = QPushButton("关闭", page)
        close.setCursor(Qt.PointingHandCursor)
        close.setFixedHeight(38)
        close.setFixedWidth(96)
        close.setStyleSheet(
            "QPushButton{border:none; border-radius:10px; color:#fff; font-size:9.5pt;"
            "font-weight:600; background:qlineargradient(x1:0,y1:0,x2:1,y2:1,"
            f"stop:0 {ACCENT},stop:1 {ACCENT_2});}}")
        close.clicked.connect(self._close)
        btns.addWidget(close)
        lay.addLayout(btns)
        lay.addStretch(1)
        return page

    def _close(self) -> None:
        if self._open_flag["v"]:
            try:
                os.startfile("D:/")  # noqa: S606
            except Exception:  # noqa: BLE001
                pass
        self.accept()


# ══════════════════════════ 主窗口 ══════════════════════════
class MainWindow(QMainWindow):
    EDGE = 7

    def __init__(self, cfg: Config) -> None:
        super().__init__()
        self.cfg = cfg
        self.repo = WingetRepository(cfg)
        self.catalog = CatalogService(cfg, self.repo)
        self.snapshot = None
        self.packages: list = []
        self.checked: dict = {}
        self.rows: dict = {}
        self.sections: list = []
        self.expanded: dict = {}
        self.active_cat = None
        self._matched = 0
        self.state = "BOOT"
        self._scan_worker = None
        self._scan_t0 = 0.0
        self._maximized = False
        self._bulk: list = []
        self._log_expanded = True
        self._log_anims: list = []
        self._narrow = False
        self._row_order: list = []

        self._scan_timer = QTimer(self)
        self._scan_timer.setInterval(100)
        self._scan_timer.timeout.connect(self._tick_scan)
        self._save_timer = QTimer(self)
        self._save_timer.setSingleShot(True)
        self._save_timer.setInterval(300)
        self._save_timer.timeout.connect(self._flush_selection)
        self._search_timer = QTimer(self)
        self._search_timer.setSingleShot(True)
        self._search_timer.setInterval(130)
        self._search_timer.timeout.connect(self._rebuild_list)
        self._flash_timer = QTimer(self)
        self._flash_timer.setSingleShot(True)
        self._flash_timer.setInterval(2200)
        self._flash_timer.timeout.connect(self._update_status)

        self.setWindowTitle(APP_TITLE)
        self.setWindowIcon(app_icon())
        self.setWindowFlags(Qt.Window | Qt.FramelessWindowHint)
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.resize(1240, 880)
        self.setMinimumSize(1060, 720)

        self._build_ui()
        self._load_selection()
        self._set_state("BOOT")
        self._fade_in()
        sc = QShortcut(QKeySequence("Ctrl+F"), self)
        sc.activated.connect(self._focus_search)
        self._central.installEventFilter(self)
        QTimer.singleShot(80, self._start_scan)

    # ── UI ──
    def _build_ui(self) -> None:
        central = QWidget(self)
        central.setAttribute(Qt.WA_TranslucentBackground)
        self.setCentralWidget(central)
        self._central = central
        outer = QVBoxLayout(central)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)

        self.shell = Shell(central)
        outer.addWidget(self.shell)

        root = QVBoxLayout(self.shell)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(0)

        self.titlebar = TitleBar(self)
        root.addWidget(self.titlebar)

        # 头部
        head = QWidget(self.shell)
        head.setAttribute(Qt.WA_TranslucentBackground, True)
        hl = QHBoxLayout(head)
        hl.setContentsMargins(26, 16, 26, 12)
        hl.setSpacing(0)
        left = QVBoxLayout()
        left.setContentsMargins(0, 0, 0, 0)
        left.setSpacing(4)
        left.addWidget(GradientLabel("Winget 软件管家", 19))
        self.sub_lbl = QLabel("正在扫描已安装软件…", head)
        self.sub_lbl.setStyleSheet(f"color:{MUTED}; font-size:9pt; background:transparent;")
        left.addWidget(self.sub_lbl)
        hl.addLayout(left)
        hl.addStretch(1)
        self.stat_total = StatCard("已安装", "0")
        self.stat_reinstall = StatCard("可重装", "0", highlight=True)
        self.stat_selected = StatCard("已勾选", "0", highlight=True)
        self.stat_deps = StatCard("运行库", "0")
        for s in (self.stat_total, self.stat_reinstall, self.stat_selected, self.stat_deps):
            hl.addWidget(s)
            hl.addSpacing(8)
        root.addWidget(head)

        # 玻璃工具条
        bar_wrap = QWidget(self.shell)
        bar_wrap.setAttribute(Qt.WA_TranslucentBackground, True)
        bw = QHBoxLayout(bar_wrap)
        bw.setContentsMargins(26, 0, 26, 10)
        bw.setSpacing(0)
        bar = QFrame(bar_wrap)
        bar.setObjectName("GlassBar")
        bar.setStyleSheet(
            "#GlassBar { background: rgba(255,255,255,0.78);"
            "border: 1px solid rgba(232,234,243,0.95); border-radius: 16px; }")
        _shadow(bar, blur=26, dy=8, alpha=24)
        bw.addWidget(bar)
        bl = QHBoxLayout(bar)
        bl.setContentsMargins(11, 11, 11, 11)
        bl.setSpacing(9)

        self.search = QLineEdit(bar)
        self.search.setPlaceholderText("搜索名称或包 ID")
        self.search.setFixedHeight(38)
        self.search.setMinimumWidth(200)
        self.search.addAction(search_icon(), QLineEdit.LeadingPosition)
        self.search.setClearButtonEnabled(True)
        self.search.setStyleSheet(f"""
            QLineEdit {{
                border: 1px solid {LINE}; border-radius: 10px; padding: 0 10px;
                background: rgba(250,251,253,0.92); font-size: 9.5pt; color: {TEXT};
                selection-background-color: {ACCENT_SOFT};
            }}
            QLineEdit:focus {{ border: 1px solid {ACCENT}; background: #FFFFFF; }}
        """)
        self.search.textChanged.connect(lambda *_: self._search_timer.start())
        self.search.installEventFilter(self)
        bl.addWidget(self.search, 1)

        self.scan_btn = self._btn(bar, "重新扫描", "outline", self._start_scan)
        bl.addWidget(self.scan_btn)
        for text, slot in (("全选", lambda: self._set_all(True)),
                           ("全不选", lambda: self._set_all(False)),
                           ("反选", self._invert),
                           ("取消运行库", self._apps_only)):
            b = self._btn(bar, text, "soft", slot, height=34)
            bl.addWidget(b)
            self._bulk.append(b)
        root.addWidget(bar_wrap)

        # 分类胶囊
        chip_wrap = QWidget(self.shell)
        chip_wrap.setAttribute(Qt.WA_TranslucentBackground, True)
        cw = QHBoxLayout(chip_wrap)
        cw.setContentsMargins(26, 0, 26, 10)
        cw.setSpacing(0)
        self.chips_host = QWidget()
        self.chips_host.setStyleSheet("background: transparent;")
        self.chips_lay = QHBoxLayout(self.chips_host)
        self.chips_lay.setContentsMargins(0, 0, 0, 0)
        self.chips_lay.setSpacing(7)
        self.chips_lay.addStretch(1)
        self.chip_scroll = QScrollArea(chip_wrap)
        self.chip_scroll.setWidgetResizable(True)
        self.chip_scroll.setFrameShape(QFrame.NoFrame)
        self.chip_scroll.setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        # 分类多时允许横向滚动（此前被裁剪不可见）
        self.chip_scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        self.chip_scroll.setStyleSheet("""
            QScrollArea { background: transparent; border: none; }
            QScrollBar:horizontal { background: transparent; height: 5px; margin: 0 10px; }
            QScrollBar::handle:horizontal { background: #D2D6E4; border-radius: 2px;
                                            min-width: 48px; }
            QScrollBar::handle:horizontal:hover { background: #B8BED2; }
            QScrollBar::add-line, QScrollBar::sub-line { width: 0; height: 0; }
            QScrollBar::add-page, QScrollBar::sub-page { background: none; }
        """)
        self.chip_scroll.viewport().setStyleSheet("background: transparent;")
        self.chip_scroll.setFixedHeight(32)
        self.chip_scroll.setWidget(self.chips_host)
        cw.addWidget(self.chip_scroll)
        root.addWidget(chip_wrap)

        # 列表卡
        body = QWidget(self.shell)
        body.setAttribute(Qt.WA_TranslucentBackground, True)
        body_l = QVBoxLayout(body)
        body_l.setContentsMargins(26, 0, 26, 10)
        body_l.setSpacing(0)
        card = QFrame(body)
        card.setObjectName("MainCard")
        card.setStyleSheet(f"#MainCard {{ background: {CARD}; border: 1px solid {LINE};"
                           "border-radius: 16px; }}")
        _shadow(card, blur=30, dy=8, alpha=22)
        card_l = QVBoxLayout(card)
        card_l.setContentsMargins(0, 0, 0, 0)
        card_l.setSpacing(0)
        self.progress = MiniProgress(card)
        self.progress.hide()
        card_l.addWidget(self.progress)

        self.stack = QStackedWidget(card)
        self.stack.setStyleSheet("background: transparent;")
        self.scroll = QScrollArea(self.stack)
        self.scroll.setWidgetResizable(True)
        self.scroll.setFrameShape(QFrame.NoFrame)
        self.scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.scroll.setStyleSheet("""
            QScrollArea { background: transparent; border: none; }
            QScrollArea > QWidget > QWidget { background: transparent; }
            QScrollBar:vertical { background: transparent; width: 10px; margin: 6px 2px; }
            QScrollBar::handle:vertical { background: #D2D6E4; border-radius: 5px;
                                          min-height: 44px; margin: 0 2px; }
            QScrollBar::handle:vertical:hover { background: #B8BED2; }
            QScrollBar::add-line, QScrollBar::sub-line { height: 0; }
            QScrollBar::add-page, QScrollBar::sub-page { background: none; }
        """)
        self.rows_host = QWidget()
        self.rows_host.setStyleSheet("background: transparent;")
        self.rows_lay = QVBoxLayout(self.rows_host)
        self.rows_lay.setContentsMargins(0, 0, 0, 8)
        self.rows_lay.setSpacing(0)
        self.rows_lay.addStretch(1)
        self.scroll.setWidget(self.rows_host)
        self.stack.addWidget(self.scroll)
        self.stack.addWidget(self._build_empty())
        self.stack.addWidget(self._build_skeleton())   # index 2：加载骨架屏
        card_l.addWidget(self.stack, 1)
        body_l.addWidget(card, 1)
        root.addWidget(body, 1)

        # 日志
        log_wrap = QWidget(self.shell)
        log_wrap.setAttribute(Qt.WA_TranslucentBackground, True)
        lw = QVBoxLayout(log_wrap)
        lw.setContentsMargins(26, 0, 26, 12)
        lw.setSpacing(0)
        self.log_card = QFrame(log_wrap)
        self.log_card.setObjectName("LogCard")
        self.log_card.setMinimumHeight(150)
        self.log_card.setMaximumHeight(150)
        self.log_card.setStyleSheet(f"#LogCard {{ background: {LOG_BG};"
                                    " border: none; border-radius: 12px; }")
        _shadow(self.log_card, blur=20, dy=5, alpha=26)
        lc = QVBoxLayout(self.log_card)
        lc.setContentsMargins(0, 0, 0, 0)
        lc.setSpacing(0)
        lhead = QWidget(self.log_card)
        lhead.setFixedHeight(34)
        lhead.setStyleSheet("background: transparent;")
        lhl = QHBoxLayout(lhead)
        lhl.setContentsMargins(15, 0, 12, 0)
        dot = QLabel("", lhead)
        dot.setFixedSize(6, 6)
        dot.setStyleSheet("background:#4ADE80; border-radius:3px;")
        lhl.addWidget(dot)
        lhl.addSpacing(8)
        lt = QLabel("运行日志", lhead)
        lt.setStyleSheet("color:#8B949E; font-size:8.8pt; font-weight:600;"
                         "background:transparent;")
        lhl.addWidget(lt)
        lhl.addStretch(1)
        self.log_toggle = QLabel("收起", lhead)
        self.log_toggle.setCursor(Qt.PointingHandCursor)
        self.log_toggle.setStyleSheet("color:#6E7681; font-size:8.5pt; background:transparent;")
        lhl.addWidget(self.log_toggle)
        lhl.addSpacing(12)
        self.log_clear = QLabel("清空", lhead)
        self.log_clear.setCursor(Qt.PointingHandCursor)
        self.log_clear.setStyleSheet("color:#6E7681; font-size:8.5pt; background:transparent;")
        lhl.addWidget(self.log_clear)
        lc.addWidget(lhead)
        self.log = QTextEdit(self.log_card)
        self.log.setReadOnly(True)
        self.log.setFrameShape(QFrame.NoFrame)
        self.log.setStyleSheet(f"""
            QTextEdit {{ background: transparent; color: {LOG_FG}; border: none;
                         padding: 0 14px 8px 14px;
                         font-family: '{MONO_FAMILY}'; font-size: 8.8pt;
                         selection-background-color: #25304A; }}
            QScrollBar:vertical {{ background: transparent; width: 8px; }}
            QScrollBar::handle:vertical {{ background: #2A3247; border-radius: 4px;
                                           min-height: 30px; }}
            QScrollBar::add-line, QScrollBar::sub-line {{ height: 0; }}
        """)
        self.log.setMinimumHeight(0)
        self.log.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Ignored)
        lc.addWidget(self.log, 1)
        lw.addWidget(self.log_card)
        self.log_clear.mouseReleaseEvent = lambda e: self.log.clear()
        self.log_toggle.mouseReleaseEvent = lambda e: self._toggle_log()
        root.addWidget(log_wrap)

        # 底部玻璃栏
        bottom = QFrame(self.shell)
        bottom.setObjectName("BottomBar")
        bottom.setFixedHeight(58)
        bottom.setStyleSheet("#BottomBar { background: rgba(255,255,255,0.82);"
                             "border-top: 1px solid rgba(232,234,243,0.95); }")
        bl2 = QHBoxLayout(bottom)
        bl2.setContentsMargins(26, 0, 22, 0)
        bl2.setSpacing(10)
        self.status_lbl = QLabel("正在扫描已安装软件…", bottom)
        self.status_lbl.setStyleSheet(f"color:{MUTED}; font-size:9pt; background:transparent;")
        bl2.addWidget(self.status_lbl)
        bl2.addStretch(1)
        self.sel_lbl = GradientLabel("0", 16)
        self.sel_lbl.setVisible(False)
        bl2.addWidget(self.sel_lbl)
        self.sel_suffix = QLabel("", bottom)
        self.sel_suffix.setStyleSheet(f"color:{MUTED}; font-size:9pt; background:transparent;")
        bl2.addWidget(self.sel_suffix)
        bl2.addSpacing(12)
        self.export_btn = self._btn(bottom, "导出配置", "primary", self._on_export, height=38)
        bl2.addWidget(self.export_btn)
        root.addWidget(bottom)

    def _build_empty(self) -> QWidget:
        page = QWidget()
        page.setStyleSheet("background: transparent;")
        lay = QVBoxLayout(page)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.addStretch(1)
        icon = QLabel(page)
        icon.setFixedSize(72, 72)
        icon.setAlignment(Qt.AlignCenter)
        icon.setStyleSheet(f"background:{ACCENT_SOFT}; color:{ACCENT};"
                           "border-radius:36px; font-size:24pt;")
        icon.setText("⌕")
        lay.addWidget(icon, 0, Qt.AlignHCenter)
        lay.addSpacing(14)
        self.empty_lbl = QLabel("", page)
        self.empty_lbl.setAlignment(Qt.AlignCenter)
        self.empty_lbl.setWordWrap(True)
        self.empty_lbl.setStyleSheet(f"color:{MUTED}; font-size:9.5pt; background:transparent;")
        lay.addWidget(self.empty_lbl, 0, Qt.AlignHCenter)
        lay.addSpacing(16)
        self.empty_clear = QPushButton("清除筛选", page)
        self.empty_clear.setCursor(Qt.PointingHandCursor)
        self.empty_clear.setFixedHeight(36)
        self.empty_clear.setStyleSheet(
            "QPushButton{border:1px solid #CDD2E2; border-radius:18px; padding:0 22px;"
            f"color:{ACCENT_DEEP}; background:#fff; font-size:9pt; font-weight:500;}}"
            "QPushButton:hover{background:#F5F6FC; border-color:#B9BFE0;}")
        self.empty_clear.clicked.connect(self._clear_filters)
        lay.addWidget(self.empty_clear, 0, Qt.AlignHCenter)
        lay.addStretch(1)
        return page

    def _build_skeleton(self) -> QWidget:
        """扫描中的加载骨架：6 行占位块 + 呼吸脉冲动画。"""
        host = QWidget()
        host.setStyleSheet("background: transparent;")
        v = QVBoxLayout(host)
        v.setContentsMargins(30, 20, 30, 20)
        v.setSpacing(18)
        for i in range(6):
            row = QWidget(host)
            h = QHBoxLayout(row)
            h.setContentsMargins(6, 0, 6, 0)
            h.setSpacing(14)
            av = QLabel(row)
            av.setFixedSize(38, 38)
            av.setStyleSheet("background:#E8EBF3; border-radius:11px;")
            h.addWidget(av, 0, Qt.AlignVCenter)
            col = QVBoxLayout()
            col.setSpacing(8)
            b1 = QLabel(row)
            b1.setFixedSize(230 + (i % 3) * 40, 11)
            b1.setStyleSheet("background:#E8EBF3; border-radius:5px;")
            b2 = QLabel(row)
            b2.setFixedSize(140 + (i % 2) * 30, 9)
            b2.setStyleSheet("background:#EFF1F7; border-radius:4px;")
            col.addWidget(b1)
            col.addWidget(b2)
            h.addLayout(col)
            h.addStretch(1)
            v.addWidget(row)
        v.addStretch(1)
        eff = QGraphicsOpacityEffect(host)
        eff.setOpacity(1.0)
        host.setGraphicsEffect(eff)
        anim = QPropertyAnimation(eff, b"opacity", host)
        anim.setDuration(900)
        anim.setStartValue(1.0)
        anim.setEndValue(0.45)
        anim.setLoopCount(-1)
        anim.setEasingCurve(QEasingCurve.InOutSine)
        anim.start()
        self._skel_anim = anim
        return host

    def _clear_filters(self) -> None:
        self.search.clear()
        self.active_cat = None
        self._rebuild_chips()
        self._rebuild_list()

    def _btn(self, parent: QWidget, text: str, kind: str, slot, height: int = 38) -> QPushButton:
        b = QPushButton(text, parent)
        b.setCursor(Qt.PointingHandCursor)
        b.setFixedHeight(height)
        b.setFocusPolicy(Qt.NoFocus)
        if kind == "primary":
            b.setStyleSheet(
                "QPushButton{border:none; border-radius:10px; padding:0 20px; color:#fff;"
                "font-size:9.5pt; font-weight:600;"
                f"background:qlineargradient(x1:0,y1:0,x2:1,y2:1,stop:0 {ACCENT},stop:1 {ACCENT_2});}}"
                "QPushButton:hover{background:qlineargradient(x1:0,y1:0,x2:1,y2:1,"
                "stop:0 #575AF0,stop:1 #7C4FF2);}"
                "QPushButton:pressed{background:#4F46E5;}"
                "QPushButton:disabled{background:#E9EBF4; color:#9AA1B5;}")
            _glow(b, blur=18, dy=4, alpha=85)
        elif kind == "outline":
            b.setStyleSheet(
                f"QPushButton{{border:1px solid {LINE}; border-radius:10px; padding:0 15px;"
                f"background:#fff; color:{TEXT}; font-size:9.3pt;}}"
                "QPushButton:hover{background:#FAFBFE; border-color:#CDD2E2;}"
                "QPushButton:disabled{color:#C3C8D8; background:#FBFCFE;}")
        elif kind == "soft":
            b.setStyleSheet(
                f"QPushButton{{border:1px solid {ACCENT_LINE}; border-radius:9px; padding:0 12px;"
                f"background:{ACCENT_SOFT}; color:{ACCENT_DEEP}; font-size:8.8pt; font-weight:500;}}"
                "QPushButton:hover{background:#E4E8FF; border-color:#C5CAFF;}"
                "QPushButton:disabled{background:#F5F6FB; color:#C3C8D8; border-color:#EDEFF7;}")
        else:
            b.setStyleSheet(
                "QPushButton{border:none; border-radius:10px; padding:0 16px; color:#fff;"
                "font-size:9.3pt; font-weight:600; background:#E5484D;}"
                "QPushButton:hover{background:#D6393F;}")
        b.clicked.connect(slot)
        return b

    # ── 交互 ──
    def _focus_search(self) -> None:
        self.search.setFocus()
        self.search.selectAll()

    def eventFilter(self, obj, event):  # noqa: N802
        if obj is self.search and event.type() == QEvent.KeyPress:
            if event.key() == Qt.Key_Escape:
                self.search.clear()
                self.search.clearFocus()
                return True
        if obj is self._central:
            if event.type() == QEvent.MouseMove:
                self._update_cursor(event.position().toPoint())
            elif event.type() == QEvent.MouseButtonPress and event.button() == Qt.LeftButton:
                edges = self._edges(event.position().toPoint())
                if edges:
                    wh = self.windowHandle()
                    if wh is not None:
                        wh.startSystemResize(edges)
                    return True
        return super().eventFilter(obj, event)

    def _edges(self, pos: QPoint):
        if self._maximized:
            return Qt.Edges()
        m = self.EDGE
        w, h = self.width(), self.height()
        edges = Qt.Edges()
        if pos.x() <= m:
            edges |= Qt.LeftEdge
        if pos.x() >= w - m:
            edges |= Qt.RightEdge
        if pos.y() <= m:
            edges |= Qt.TopEdge
        if pos.y() >= h - m:
            edges |= Qt.BottomEdge
        return edges

    def _update_cursor(self, pos: QPoint) -> None:
        e = self._edges(pos)
        if e in (Qt.LeftEdge, Qt.RightEdge):
            self._central.setCursor(Qt.SizeHorCursor)
        elif e in (Qt.TopEdge, Qt.BottomEdge):
            self._central.setCursor(Qt.SizeVerCursor)
        elif e in (Qt.LeftEdge | Qt.TopEdge, Qt.RightEdge | Qt.BottomEdge):
            self._central.setCursor(Qt.SizeFDiagCursor)
        elif e in (Qt.RightEdge | Qt.TopEdge, Qt.LeftEdge | Qt.BottomEdge):
            self._central.setCursor(Qt.SizeBDiagCursor)
        else:
            self._central.unsetCursor()

    def toggle_max(self) -> None:
        if self.isMaximized():
            self.showNormal()
            self._maximized = False
            self.centralWidget().layout().setContentsMargins(16, 14, 16, 16)
            self.shell.set_radius(16)
            self._shell_shadow.setEnabled(True)
            self.titlebar.btns["max"].setText("□")
        else:
            self.showMaximized()
            self._maximized = True
            self.centralWidget().layout().setContentsMargins(0, 0, 0, 0)
            self.shell.set_radius(0)
            self._shell_shadow.setEnabled(False)
            self.titlebar.btns["max"].setText("❐")

    def _toggle_log(self) -> None:
        self._log_expanded = not self._log_expanded
        self.log_toggle.setText("收起" if self._log_expanded else "展开")
        target = 150 if self._log_expanded else 34
        self.log.setVisible(self._log_expanded)
        for prop in (b"minimumHeight", b"maximumHeight"):
            a = QPropertyAnimation(self.log_card, prop, self)
            a.setDuration(200)
            a.setEasingCurve(QEasingCurve.InOutCubic)
            a.setStartValue(self.log_card.height())
            a.setEndValue(target)
            a.start()
            self._log_anims.append(a)

    def _fade_in(self) -> None:
        eff = QGraphicsOpacityEffect(self._central)
        self._central.setGraphicsEffect(eff)
        anim = QPropertyAnimation(eff, b"opacity", self)
        anim.setDuration(260)
        anim.setStartValue(0.0)
        anim.setEndValue(1.0)
        anim.setEasingCurve(QEasingCurve.OutCubic)
        anim.finished.connect(lambda: self._central.setGraphicsEffect(None))
        anim.start()
        self._fade_anim = anim

    # ── 状态机 ──
    def _set_state(self, state: str) -> None:
        self.state = state
        s = state
        self.scan_btn.setEnabled(s in ("READY", "SCAN_FAILED"))
        for b in self._bulk:
            b.setEnabled(s == "READY")
        self._update_status()
        self._update_empty()

    # ── 扫描 ──
    def _start_scan(self) -> None:
        if self.state == "SCANNING":
            return
        self._set_state("SCANNING")
        self.progress.start()
        self._scan_t0 = time.perf_counter()
        self._scan_timer.start()
        self.log_append("info", "开始扫描已安装软件…")
        self._scan_worker = ScanWorker(self.catalog)
        self._scan_worker.ok.connect(self._on_scan_ok)
        self._scan_worker.err.connect(self._on_scan_err)
        self._scan_worker.start()

    def _tick_scan(self) -> None:
        elapsed = time.perf_counter() - self._scan_t0
        self.status_lbl.setText(f"正在扫描 · 已用 {elapsed:.1f}s")

    def _on_scan_ok(self, snapshot) -> None:
        self._scan_timer.stop()
        self.progress.stop()
        self.snapshot = snapshot
        self.packages = [p for p in snapshot.packages if p.reinstallable]
        hidden = snapshot.counts["total"] - len(self.packages)
        self.log_append("info",
                        f"扫描完成：可重装 {len(self.packages)} 个；另有 {hidden} 个系统组件"
                        "无法自动重装，已隐藏。")
        valid = {p.packageIdentifier.lower() for p in self.packages}
        for k in [k for k in self.checked if k not in valid]:
            del self.checked[k]
        self.active_cat = None
        self._rebuild_chips()
        self._rebuild_list(keep_scroll=True)   # 重扫后保持滚动位置
        self._set_state("READY")
        self._save_selection()

    def _on_scan_err(self, msg: str) -> None:
        self._scan_timer.stop()
        self.progress.stop()
        self._set_state("SCAN_FAILED")
        self.status_lbl.setText("扫描失败")
        self.log_append("error", f"扫描失败：{msg}")

    # ── 分类胶囊 ──
    def _rebuild_chips(self) -> None:
        while self.chips_lay.count() > 1:
            it = self.chips_lay.takeAt(0)
            w = it.widget()
            if w is not None:
                w.setParent(None)
                w.deleteLater()
        counts: dict = {}
        for p in self.packages:
            counts[p.category] = counts.get(p.category, 0) + 1
        items = [("全部", len(self.packages), None)]
        for cat in sorted(counts, key=lambda c: (-counts[c], c)):
            items.append((cat, counts[cat], cat))
        for text, n, cat in items:
            b = QPushButton(f"{text} {n}")
            b.setCheckable(True)
            b.setCursor(Qt.PointingHandCursor)
            b.setFixedHeight(30)
            b.setFocusPolicy(Qt.NoFocus)
            b.setChecked(cat == self.active_cat)
            b.setStyleSheet(f"""
                QPushButton {{
                    border: 1px solid {LINE}; border-radius: 15px; padding: 0 13px;
                    background: #fff; color: {MUTED}; font-size: 8.8pt;
                }}
                QPushButton:hover {{ border-color: #CDD2E2; color: {TEXT}; }}
                QPushButton:checked {{
                    border: none; color: #fff; font-weight: 500;
                    background: qlineargradient(x1:0,y1:0,x2:1,y2:1,
                                                stop:0 {ACCENT}, stop:1 {ACCENT_2});
                }}
            """)
            b.clicked.connect(lambda _=False, c=cat: self._select_cat(c))
            self.chips_lay.insertWidget(self.chips_lay.count() - 1, b)

    def _select_cat(self, cat) -> None:
        self.active_cat = cat
        self._rebuild_chips()
        self._rebuild_list()

    # ── 列表 ──
    def _clear_rows(self) -> None:
        for sec in self.sections:
            self.expanded[sec.title] = sec.expanded
        self.sections.clear()
        self.rows.clear()
        while self.rows_lay.count() > 1:
            it = self.rows_lay.takeAt(0)
            w = it.widget()
            if w is not None:
                w.setParent(None)
                w.deleteLater()

    def _rebuild_list(self, keep_scroll: bool = False) -> None:
        sb = self.scroll.verticalScrollBar()
        pos = sb.value() if keep_scroll else 0
        # 批量重建期间挂起重绘，消除闪烁与中间态布局开销
        self.rows_host.setUpdatesEnabled(False)
        self._clear_rows()
        self._row_order = []
        keyword = self.search.text().strip().lower()
        groups: dict = {}
        for pkg in self.packages:
            if self.active_cat and pkg.category != self.active_cat:
                continue
            if keyword and keyword not in f"{pkg.name} {pkg.packageIdentifier}".lower():
                continue
            groups.setdefault(pkg.category, []).append(pkg)
        self._matched = sum(len(v) for v in groups.values())

        for category in sorted(groups, key=lambda c: (-len(groups[c]), c)):
            sec = GroupSection(category, self.expanded.get(category, True), self.rows_host)
            sec.header.select_all.connect(lambda c=category: self._toggle_group(c))
            for pkg in groups[category]:
                key = pkg.packageIdentifier.lower()
                row = PackageRow(pkg, self.checked.get(key, False), sec.body)
                row.toggled.connect(self._on_row_toggled)
                row.context_requested.connect(self._show_row_menu)
                row.copy_id.connect(lambda p: self._copy(p.packageIdentifier,
                                                          f"已复制包 ID：{p.packageIdentifier}"))
                row.nav_requested.connect(self._nav_row)
                if self._narrow:
                    row.set_narrow(True)
                sec.add_row(row)
                self.rows[key] = row
                self._row_order.append(row)
            sec.prepare()
            self.sections.append(sec)
            self.rows_lay.insertWidget(self.rows_lay.count() - 1, sec)
            self._update_section_count(category)

        self.rows_host.setUpdatesEnabled(True)
        sb.setValue(min(pos, sb.maximum()))
        self._update_status()
        self._update_empty()

    def _nav_row(self, row, delta: int) -> None:
        """↑/↓ 在可见行之间移动焦点并保证目标行可见。"""
        if row not in self._row_order:
            return
        idx = self._row_order.index(row) + delta
        if 0 <= idx < len(self._row_order):
            nxt = self._row_order[idx]
            nxt.setFocus()
            self.scroll.ensureWidgetVisible(nxt, 0, 60)

    def resizeEvent(self, e) -> None:  # noqa: N802
        super().resizeEvent(e)
        if not hasattr(self, "rows") or not hasattr(self, "search"):
            return
        narrow = self.width() < 1080
        if narrow != self._narrow:
            self._narrow = narrow
            for r in self.rows.values():
                r.set_narrow(narrow)

    def _toggle_group(self, category: str) -> None:
        rows = [r for r in self.rows.values() if r.pkg.category == category]
        if not rows:
            return
        target = not all(r._checked for r in rows)
        for r in rows:
            self.checked[r.pkg.packageIdentifier.lower()] = target
            r.set_checked(target)
        self._update_section_count(category)
        self._update_status()
        self._save_selection()

    def _update_section_count(self, category: str) -> None:
        for sec in self.sections:
            if sec.title != category:
                continue
            rows = [r for r in self.rows.values() if r.pkg.category == category]
            sel = sum(1 for r in rows if r._checked)
            sec.header.set_count(sel, len(rows))
            sec.header.sel_all.setText("全不选" if rows and sel == len(rows) else "全选本组")

    def _update_all_counts(self) -> None:
        for sec in self.sections:
            rows = [r for r in self.rows.values() if r.pkg.category == sec.title]
            sel = sum(1 for r in rows if r._checked)
            sec.header.set_count(sel, len(rows))
            sec.header.sel_all.setText("全不选" if rows and sel == len(rows) else "全选本组")

    def _update_empty(self) -> None:
        if not self.packages:
            if self.state in ("BOOT", "SCANNING"):
                self.stack.setCurrentIndex(2)          # 骨架屏加载态
            elif self.state == "SCAN_FAILED":
                self.empty_lbl.setText("扫描失败：winget 未安装或不可用\n可点击「重新扫描」重试")
                self.empty_clear.setVisible(False)
                self.stack.setCurrentIndex(1)
            else:
                self.empty_lbl.setText("未发现可重装的软件")
                self.empty_clear.setVisible(False)
                self.stack.setCurrentIndex(1)
            return
        kw = self.search.text().strip()
        has_filter = bool(kw or self.active_cat)
        if has_filter and self._matched == 0:
            if kw and self.active_cat:
                self.empty_lbl.setText(f"在「{self.active_cat}」中未找到与“{kw}”匹配的软件")
            elif kw:
                self.empty_lbl.setText(f"未找到与“{kw}”匹配的软件\n可尝试搜索软件名或包 ID 的一部分")
            else:
                self.empty_lbl.setText("该分类下暂无可重装的软件")
            self.empty_clear.setVisible(True)
            self.stack.setCurrentIndex(1)
            return
        self.stack.setCurrentIndex(0)

    # ── 勾选 ──
    def _on_row_toggled(self, pkg) -> None:
        key = pkg.packageIdentifier.lower()
        self.checked[key] = not self.checked.get(key, False)
        self._update_section_count(pkg.category)
        self._update_status()
        self._save_selection()

    def _set_all(self, value: bool) -> None:
        if self.state != "READY":
            return
        for p in self.packages:
            self.checked[p.packageIdentifier.lower()] = value
        for row in self.rows.values():
            row.set_checked(value)
        self._update_all_counts()
        self._update_status()
        self._save_selection()

    def _invert(self) -> None:
        if self.state != "READY":
            return
        for p in self.packages:
            key = p.packageIdentifier.lower()
            self.checked[key] = not self.checked.get(key, False)
        for key, row in self.rows.items():
            row.set_checked(self.checked.get(key, False))
        self._update_all_counts()
        self._update_status()
        self._save_selection()

    def _apps_only(self) -> None:
        if self.state != "READY":
            return
        for p in self.packages:
            self.checked[p.packageIdentifier.lower()] = p.category != "运行库与组件"
        for key, row in self.rows.items():
            row.set_checked(self.checked.get(key, False))
        self._update_all_counts()
        self._update_status()
        self._save_selection()

    def selected_packages(self) -> list:
        return [p for p in self.packages if self.checked.get(p.packageIdentifier.lower())]

    # ── 状态 ──
    def _update_status(self) -> None:
        total = len(self.packages)
        n = len(self.selected_packages())
        self.stat_total.set_value(str(self.snapshot.counts["total"] if self.snapshot else total))
        self.stat_reinstall.set_value(str(total))
        self.stat_selected.set_value(str(n))
        self.stat_deps.set_value(str(sum(1 for p in self.packages
                                         if p.category == "运行库与组件")))
        if n:
            self.sel_lbl.setText(str(n))
            self.sel_lbl.setVisible(True)
            self.sel_suffix.setText(f"/ {total} 项已勾选")
            self.sel_suffix.setVisible(True)
        else:
            self.sel_lbl.setVisible(False)
            self.sel_suffix.setVisible(False)
        self.export_btn.setEnabled(self.state in ("SCANNING", "SCAN_FAILED", "READY") and n > 0)
        if self.snapshot:
            self.sub_lbl.setText(
                f"共 {self.snapshot.counts['total']} 个已安装 · {total} 个可重装 · "
                f"扫描耗时 {self.snapshot.durationMs / 1000:.1f}s")
        if self.state == "SCANNING":
            return
        if self.search.text().strip() or self.active_cat:
            self.status_lbl.setText(f"筛选出 {self._matched} / 共 {total} 项")
        elif self.snapshot:
            self.status_lbl.setText(f"{total} 个软件可一键重装 · 勾选后点右下角「导出配置」")

    def _flash(self, text: str) -> None:
        self.status_lbl.setText(text)
        self._flash_timer.start()

    # ── 复制 ──
    def _copy(self, text: str, flash: str) -> None:
        QApplication.clipboard().setText(text)
        self._flash(flash)

    def _show_row_menu(self, row: PackageRow, pos: QPoint) -> None:
        menu = QMenu(self)
        menu.setStyleSheet(f"""
            QMenu {{ background:#fff; border:1px solid {LINE}; border-radius:10px; padding:5px; }}
            QMenu::item {{ padding:7px 20px; border-radius:7px; font-size:9pt; color:{TEXT}; }}
            QMenu::item:selected {{ background:{ACCENT_SOFT}; color:{ACCENT_DEEP}; }}
            QMenu::separator {{ height:1px; background:{LINE_SOFT}; margin:4px 8px; }}
        """)
        pkg = row.pkg
        menu.addAction("复制包 ID", lambda: self._copy(pkg.packageIdentifier,
                                                       f"已复制包 ID：{pkg.packageIdentifier}"))
        menu.addAction("复制软件名称", lambda: self._copy(pkg.name, f"已复制：{pkg.name}"))
        menu.addAction("复制「名称 (包ID)」",
                       lambda: self._copy(f"{pkg.name} ({pkg.packageIdentifier})", "已复制"))
        menu.addSeparator()
        menu.addAction("勾选 / 取消勾选", row.toggle)
        menu.exec(pos)

    # ── 日志 ──
    def log_append(self, tag: str, text: str) -> None:
        colors = {"ts": "#6E7681", "info": "#C9D1D9", "success": "#4ADE80",
                  "skip": "#C4B5FD", "fail": "#F87171", "error": "#FF8A8A",
                  "done": "#A5B4FC"}
        sb = self.log.verticalScrollBar()
        at_end = sb.value() >= sb.maximum() - 4
        cur = self.log.textCursor()
        cur.movePosition(QTextCursor.End)
        ts_fmt = QTextCharFormat()
        ts_fmt.setForeground(QColor(colors["ts"]))
        cur.insertText(datetime.now().strftime("%H:%M:%S") + "  ", ts_fmt)
        fmt = QTextCharFormat()
        fmt.setForeground(QColor(colors.get(tag, LOG_FG)))
        if tag == "done":
            fmt.setFontWeight(QFont.Bold)
        cur.insertText(text + "\n", fmt)
        if at_end:
            sb.setValue(sb.maximum())

    # ── 导出 ──
    def _on_export(self) -> None:
        selected = self.selected_packages()
        if not selected:
            QMessageBox.warning(self, APP_TITLE, "请先勾选需要保留的软件。")
            return
        ExportDialog(self, selected).exec()

    # ── 持久化 ──
    def _save_selection(self) -> None:
        self._save_timer.start()

    def _flush_selection(self) -> None:
        try:
            PERSIST_DIR.mkdir(parents=True, exist_ok=True)
            SELECTION_FILE.write_text(json.dumps(self.checked, ensure_ascii=False),
                                      encoding="utf-8")
        except Exception:  # noqa: BLE001
            pass

    def _load_selection(self) -> None:
        try:
            if SELECTION_FILE.is_file():
                data = json.loads(SELECTION_FILE.read_text(encoding="utf-8"))
                if isinstance(data, dict):
                    self.checked = {str(k).lower(): bool(v) for k, v in data.items()}
        except Exception:  # noqa: BLE001
            pass

    def closeEvent(self, e) -> None:  # noqa: N802
        self._flush_selection()
        e.accept()


def main() -> int:
    QApplication.setHighDpiScaleFactorRoundingPolicy(
        Qt.HighDpiScaleFactorRoundingPolicy.PassThrough)
    app = QApplication(sys.argv)
    app.setApplicationName(APP_TITLE)
    family = _pick_family(app)
    global FONT_FAMILY
    FONT_FAMILY = family
    app.setFont(QFont(family, 10))
    app.setWindowIcon(app_icon())
    try:
        cfg = load_config()
    except ConfigError as exc:
        QMessageBox.critical(None, APP_TITLE, str(exc))
        return 1
    win = MainWindow(cfg)
    win.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
