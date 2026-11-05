"""Winget 软件管家 · UI 层：自绘控件与对话框。

所有颜色/尺寸令牌来自 qt_common；界面固定为浅色主题。
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

from PySide6.QtCore import (QEasingCurve, QPoint, QPointF, QPropertyAnimation,
                            QRect, QRectF, Qt, QTimer, Property, Signal)
from PySide6.QtGui import (QBrush, QColor, QCursor, QFont, QFontMetrics,
                           QLinearGradient, QPainter, QPainterPath, QPen,
                           QPolygonF, QRadialGradient)
from PySide6.QtWidgets import (QApplication, QDialog, QFileDialog, QFrame, QHBoxLayout,
                               QLabel, QPushButton, QSizePolicy, QStackedWidget,
                               QVBoxLayout, QWidget)

from qt_common import *  # noqa: F401,F403
from qt_common import (_brand_arrow_path, _glow, _grad, _hl_html, _pick_family,  # noqa: F401
                       _shadow, _tone_for, app_icon, default_export_path,
                       search_icon, settings)
from core.fsio import atomic_write_json, atomic_write_text
from services.catalog import CatalogService  # noqa: F401
from services.profiles import (build_restore_bat, build_unreinstallable_list,  # noqa: F401
                              build_winget_manifest, source_breakdown)
from services.winget_cli import WingetRepository  # noqa: F401

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
        p.setPen(QPen(QColor(CHK_RING_HOVER if self._hover > 0.5 else LINE), 1))
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
            border = QColor(ACCENT) if (self._hover and self._prog < 0.05) else QColor(MUTED3)
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
        p.fillRect(self.rect(), QColor(ROW_ALT))
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
        self.selectable = bool(getattr(pkg, "reinstallable", True))
        self.setFixedHeight(46)
        self.setFocusPolicy(Qt.StrongFocus)
        self.setCursor(Qt.PointingHandCursor if self.selectable else Qt.ArrowCursor)
        self.setAttribute(Qt.WA_Hover, True)

        lay = QHBoxLayout(self)
        lay.setContentsMargins(16, 0, 16, 0)
        lay.setSpacing(0)
        if self.selectable:
            self.box = CheckBox(checked)
            self.box.setAttribute(Qt.WA_TransparentForMouseEvents, True)
            lay.addWidget(self.box, 0, Qt.AlignVCenter)
            lay.addSpacing(13)
        else:
            lay.addSpacing(31)      # 与复选框列对齐
            self.box = None

        name_box = QVBoxLayout()
        name_box.setContentsMargins(0, 0, 0, 0)
        name_box.setSpacing(1)
        self.lbl_name = QLabel(pkg.name, self)
        self.lbl_name.setToolTip(pkg.name)
        # Ignored：长文本被裁剪而非撑宽行，避免横向错位
        self.lbl_name.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        self._name_color = TEXT if self.selectable else NAME_OFF
        self._id_color = MUTED2 if self.selectable else ID_OFF
        self.lbl_name.setStyleSheet(
            f"color:{self._name_color}; font-size:10pt;"
            "font-weight:500; background:transparent;")
        name_box.addWidget(self.lbl_name)
        self.lbl_id = QLabel(pkg.packageIdentifier, self)
        self.lbl_id.setToolTip(pkg.packageIdentifier)
        self.lbl_id.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        self.lbl_id.setStyleSheet(
            f"color:{self._id_color}; font-family:'{MONO_FAMILY}';"
            "font-size:8.5pt; background:transparent;")
        name_box.addWidget(self.lbl_id)
        lay.addLayout(name_box, 1)

        self.tag = QLabel(self)
        self.tag.setAlignment(Qt.AlignCenter)
        dep = pkg.category == "运行库与组件" or getattr(pkg, "isDependency", False)
        if not self.selectable:
            self.tag.setText("不可重装")
            self.tag.setToolTip("winget 没有可用的自动重装源（如系统组件/商店应用），仅列出供参考")
            self.tag.setStyleSheet(
                f"color:{TAG_DIM_FG}; background:{TAG_DIM_BG};"
                f" border:1px solid {TAG_DIM_LINE};"
                "border-radius:6px; padding:2px 7px; font-size:8pt;")
        elif dep:
            self.tag.setText("运行库")
            self.tag.setStyleSheet(
                f"color:{WARN}; background:{WARN_BG}; border:1px solid {WARN_LINE};"
                "border-radius:6px; padding:2px 7px; font-size:8pt;")
        else:
            self.tag.setText(pkg.source or "winget")
            self.tag.setStyleSheet(
                f"color:{TAG_SRC_FG}; background:{TAG_SRC_BG};"
                f" border:1px solid {TAG_SRC_LINE};"
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
            hl = QColor(ROW_HOVER)
            hl.setAlphaF(self._hover)
            p.fillRect(self.rect(), hl)
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
        if not self.selectable:
            return
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
        if not self.selectable:
            return
        self._checked = on
        self.box.set_checked(on, animate=False)

    def set_highlight(self, keywords) -> None:
        """搜索关键词高亮：命中片段加浅黄底，未命中时回落纯文本（省渲染开销）。

        keywords 可以是单个字符串，也可以是分词后的列表（空格分隔的多个词）。
        """
        kws = [k for k in ([keywords] if isinstance(keywords, str) else list(keywords)) if k]
        if not kws:
            for lbl, text in ((self.lbl_name, self.pkg.name),
                              (self.lbl_id, self.pkg.packageIdentifier)):
                if lbl.textFormat() != Qt.PlainText:
                    lbl.setTextFormat(Qt.PlainText)
                    lbl.setText(text)
            return
        for lbl, text, color in ((self.lbl_name, self.pkg.name, self._name_color),
                                 (self.lbl_id, self.pkg.packageIdentifier, self._id_color)):
            lbl.setTextFormat(Qt.RichText)
            lbl.setText(_hl_html(text, kws, color))


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
        base.setColorAt(0, QColor(GRAD_H1))
        base.setColorAt(1, QColor(GRAD_H2))
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
        p.setPen(QPen(QColor(PILL_RING_FULL) if full else QColor(PILL_RING), 1))
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
            p.setPen(QColor(LINK_FG) if self._sel else QColor(LINK_FG_OFF))
            p.drawText(QPointF(x, y), a)
            p.setPen(QColor(LINK_FG_HOVER))
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
        base = QColor(CHEV)
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


class ClickableLabel(QLabel):
    """可点击的文字标签：左键松开发 `clicked`。

    存在的理由：以前满地都是 `label.mouseReleaseEvent = lambda e: ...` 的猴补丁，
    既绕过了 Qt 的事件分发（右键/双击/按下都会误触发），也没法在别处复用。
    """

    clicked = Signal()

    def __init__(self, text: str = "", parent: QWidget | None = None) -> None:
        super().__init__(text, parent)
        self.setCursor(Qt.PointingHandCursor)

    def mouseReleaseEvent(self, e) -> None:  # noqa: N802
        if e.button() == Qt.LeftButton and self.rect().contains(e.position().toPoint()):
            self.clicked.emit()
        super().mouseReleaseEvent(e)


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

    def __init__(self, title: str, expanded: bool,
                 parent: QWidget | None = None) -> None:
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
        top = QColor(HEAD_TOP)
        bot = QColor(HEAD_BOT)
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

    def __init__(self, title: str, expanded: bool,
                 parent: QWidget | None = None) -> None:
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
        a1, a2 = 32, 34           # 双径向光晕的透明度
        self._glows = [
            (QPointF(w * 0.95, -h * 0.08), max(w, h) * 0.52, QColor(139, 92, 246, a1)),
            (QPointF(-w * 0.06, -h * 0.03), max(w, h) * 0.42, QColor(99, 102, 241, a2)),
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
        s = 24.0
        r = QRectF(0, 0, s, s)
        path = QPainterPath()
        path.addRoundedRect(r, 7, 7)
        p.fillPath(path, QBrush(_grad(r)))
        arrow, tray = _brand_arrow_path(s)
        p.fillPath(arrow, QColor("#FFFFFF"))
        p.fillPath(tray, QColor(255, 255, 255, 235))
        p.end()


class StickyHeader(QWidget):
    """吸顶分组头：列表滚动时，当前所在分组名浮在卡片顶部。

    视觉完全复用 GroupHeader（同渐变、同计数胶囊），只去掉「全选本组」，
    底部多留 4px 画一条渐隐阴影表示浮起。点击回跳到该分组开头。
    """

    jump = Signal(str)

    BAR_H = 42
    SHADOW_H = 4

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._title = ""
        self.setFixedHeight(self.BAR_H + self.SHADOW_H)
        self.header = GroupHeader("", True, self)
        self.header.sel_all.setVisible(False)
        self.header.setGeometry(0, 0, self.width(), self.BAR_H)
        self.header.clicked.connect(lambda: self.jump.emit(self._title))
        self.hide()

    def set_section(self, title: str, sel: int, total: int) -> None:
        if title != self._title:
            self._title = title
            self.header.title.setText(title)
        self.header.set_count(sel, total, animate=False)

    def resizeEvent(self, e) -> None:  # noqa: N802
        super().resizeEvent(e)
        self.header.setGeometry(0, 0, self.width(), self.BAR_H)

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        g = QLinearGradient(0, self.BAR_H, 0, self.BAR_H + self.SHADOW_H)
        a = QColor(24, 28, 40, 26)
        g.setColorAt(0, a)
        g.setColorAt(1, QColor(a.red(), a.green(), a.blue(), 0))
        p.fillRect(QRectF(0, self.BAR_H, self.width(), self.SHADOW_H), QBrush(g))
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
        self.about_btn = QPushButton("ⓘ", self)
        self.about_btn.setFixedSize(30, 28)
        self.about_btn.setCursor(Qt.PointingHandCursor)
        self.about_btn.setFocusPolicy(Qt.NoFocus)
        self.about_btn.setToolTip(f"关于 {APP_TITLE} · 版本 {APP_VERSION}")
        self.about_btn.setStyleSheet(
            f"QPushButton{{border:none; border-radius:7px; color:{MUTED};"
            "font-size:10.5pt; background:transparent;}"
            f"QPushButton:hover{{background:{CHIP_BG}; color:{TEXT};}}")
        self.about_btn.clicked.connect(lambda: AboutDialog(host).exec())
        lay.addWidget(self.about_btn)
        lay.addSpacing(6)
        self.btns = {}
        for key, glyph, hbg, hfg in (("min", "—", CHIP_BG, TEXT),
                                     ("max", "□", CHIP_BG, TEXT),
                                     ("close", "✕", "#E5484D", "#FFFFFF")):
            b = QPushButton(glyph, self)
            b.setFixedSize(30, 28)
            b.setCursor(Qt.PointingHandCursor)
            b.setFocusPolicy(Qt.NoFocus)
            b.setStyleSheet(
                f"QPushButton{{border:none; border-radius:7px; color:{MUTED};"
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
        p.setPen(QPen(QColor(LINE_SOFT), 1))
        p.drawLine(0, self.height() - 1, self.width(), self.height() - 1)
        p.end()


# ══════════════════════════ 后台线程 ══════════════════════════


class ExportDialog(QDialog):
    def __init__(self, app: "MainWindow", selected: list) -> None:
        super().__init__(app)
        self.app = app
        self.selected = selected
        self.dest = default_export_path()
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
        desc = QLabel(f"将已勾选的 {len(self.selected)} 个软件包以 winget 官方格式导出，"
                      "重装系统后执行 winget import -i 即可一键恢复。", page)
        desc.setWordWrap(True)
        desc.setStyleSheet(f"color:{MUTED}; font-size:9pt;")
        lay.addWidget(desc)
        lay.addSpacing(14)

        dest_row = QWidget(page)
        dr = QHBoxLayout(dest_row)
        dr.setContentsMargins(0, 0, 0, 0)
        dr.setSpacing(8)
        self.path_lbl = QLabel(str(self.dest), dest_row)
        self.path_lbl.setToolTip(str(self.dest))
        self.path_lbl.setStyleSheet(
            f"border:1px solid {LINE}; border-radius:9px; padding:0 11px;"
            f"background:{SURFACE_2}; color:{TEXT}; font-family:'{MONO_FAMILY}';"
            "font-size:8.6pt;")
        self.path_lbl.setFixedHeight(36)
        self.path_lbl.setMinimumWidth(320)
        self.path_lbl.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Fixed)
        dr.addWidget(self.path_lbl, 1)
        pick = QPushButton("更改…", dest_row)
        pick.setCursor(Qt.PointingHandCursor)
        pick.setFixedHeight(36)
        pick.setFocusPolicy(Qt.NoFocus)
        pick.setStyleSheet(
            f"QPushButton{{border:1px solid {LINE}; border-radius:9px; padding:0 14px;"
            f"background:{SURFACE}; color:{TEXT}; font-size:9pt;}}"
            f"QPushButton:hover{{background:{SURFACE_3}; border-color:{BORDER_HOVER};}}")
        pick.clicked.connect(self._pick_path)
        dr.addWidget(pick)
        lay.addWidget(dest_row)

        self.hint = QLabel("", page)
        self.hint.setWordWrap(True)
        self.hint.setMinimumHeight(22)
        self.hint.setStyleSheet(f"color:{MUTED2}; font-size:8.5pt;")
        lay.addWidget(self.hint)
        self.ok_text = self._sync_dest_hint()
        lay.addSpacing(6)
        btns = QHBoxLayout()
        btns.setContentsMargins(0, 20, 0, 0)
        btns.addStretch(1)
        cancel = QPushButton("取消", page)
        cancel.setCursor(Qt.PointingHandCursor)
        cancel.setFixedHeight(38)
        cancel.setStyleSheet(
            f"QPushButton{{border:1px solid {LINE}; border-radius:10px; padding:0 18px;"
            f"background:{SURFACE}; color:{TEXT}; font-size:9.5pt;}}"
            f"QPushButton:hover{{background:{SURFACE_3}; border-color:{BORDER_HOVER};}}")
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
            f"QPushButton:disabled{{background:{ROW_ALT}; color:{MUTED2};}}")
        _glow(self.ok_btn, blur=18, dy=4, alpha=90)
        self.ok_btn.clicked.connect(self._do_export)
        btns.addWidget(self.ok_btn)
        lay.addLayout(btns)
        lay.addStretch(1)
        return page

    def _sync_dest_hint(self) -> str:
        """按目标文件是否存在切换提示文案与主按钮标题。"""
        br = source_breakdown(self.selected)
        ms = br["msstore"]
        stem = self.dest.stem
        extra = (f" · 同目录另存「恢复-{stem}.bat」"
                 f"与「无法自动重装清单-{stem}.txt」")
        if ms:
            extra = f" · 含 {ms} 个 msstore 包（恢复时可能需登录商店）" + extra
        if self.dest.is_file():
            self.hint.setStyleSheet(f"color:{WARN}; font-size:8.5pt;")
            self.hint.setText("目标文件已存在，导出将覆盖它" + extra)
            return "覆盖导出"
        self.hint.setStyleSheet(f"color:{MUTED2}; font-size:8.5pt;")
        self.hint.setText("winget import 标准格式，重装系统后可直接恢复" + extra)
        return "立即导出"

    def _pick_path(self) -> None:
        chosen, _ = QFileDialog.getSaveFileName(
            self, "选择导出位置", str(self.dest), "JSON 文件 (*.json)")
        if not chosen:
            return
        p = Path(chosen)
        if p.suffix.lower() != ".json":
            p = p.with_suffix(".json")
        self.dest = p
        self.path_lbl.setText(str(p))
        self.path_lbl.setToolTip(str(p))
        self.ok_btn.setText(self._sync_dest_hint())

    def _do_export(self) -> None:
        snapshot = self.app.snapshot
        out = self.dest
        self.ok_btn.setEnabled(False)
        self.hint.setStyleSheet(f"color:{MUTED2}; font-size:8.5pt;")
        self.hint.setText("正在导出…")
        try:
            payload = build_winget_manifest(
                self.selected,
                snapshot.wingetVersion if snapshot else "1.10.0")
            # 原子写：不要 QApplication.processEvents()，也不要 write_text——
            # 前者会把 in-flight 的重绘/点击重入进来，后者中途失败会留下半截 JSON。
            atomic_write_json(out, payload)
            settings().setValue("export/path", str(out))
            files = [str(out)]
            br = source_breakdown(self.selected)
            # 同目录放一份双击即恢复的 bat（GBK 写盘，cmd 代码页 936）
            try:
                bat = out.with_name(f"恢复-{out.stem}.bat")
                atomic_write_text(
                    bat, build_restore_bat(out.name, sum(br.values()), br["msstore"]),
                    encoding="gbk")
                files.append(str(bat))
            except Exception as exc:  # noqa: BLE001  脚本失败不影响清单
                self.app.log_append("fail", f"恢复脚本生成失败（清单已导出）：{exc}")
            # 再放一份「无法自动重装清单」，界面关掉后这些项就再也查不到了
            try:
                others = [p for p in self.app.packages if not p.reinstallable]
                if others:
                    txt = out.with_name(f"无法自动重装清单-{out.stem}.txt")
                    atomic_write_text(txt, build_unreinstallable_list(others),
                                      encoding="utf-8-sig")
                    files.append(str(txt))
            except Exception as exc:  # noqa: BLE001
                self.app.log_append("fail", f"无法重装清单生成失败：{exc}")
        except AppError as exc:
            self.ok_btn.setEnabled(True)
            self.hint.setStyleSheet(f"color:{ERR}; font-size:8.5pt;")
            self.hint.setText(exc.user_message)
            self.app.log_append("fail", f"导出失败：{exc.user_message}")
            return
        except Exception as exc:  # noqa: BLE001
            self.ok_btn.setEnabled(True)
            self.hint.setStyleSheet(f"color:{ERR}; font-size:8.5pt;")
            self.hint.setText(f"导出失败：{exc}")
            self.app.log_append("fail", f"导出失败：{exc}")
            return
        self._files = files
        self.app.log_append("done", f"已导出 {len(self.selected)} 个软件包：")
        for f in files:
            self.app.log_append("info", f"  {f}")
        if br["msstore"]:
            self.app.log_append("skip", f"  其中 {br['msstore']} 个来自 msstore，"
                                        "恢复时可能要求登录微软商店账号")
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
            p = Path(f)
            suffix = p.suffix.lower()
            kind = {".bat": "恢复脚本", ".txt": "核对清单"}.get(suffix, "清单文件")
            item = QLabel(f"{kind}   {p.name}", page)
            item.setToolTip(str(p))
            item.setStyleSheet(f"color:{MUTED}; font-family:'{MONO_FAMILY}'; font-size:8.5pt;")
            lay.addWidget(item)
        if any(Path(f).suffix.lower() == ".bat" for f in self._files):
            lay.addSpacing(8)
            tip = QLabel("重装系统后不必记命令 —— 双击「恢复软件.bat」，"
                         "它会自动执行 winget import 并逐包安装。", page)
            tip.setWordWrap(True)
            tip.setStyleSheet(f"color:{MUTED2}; font-size:8.5pt;")
            lay.addWidget(tip)
        if any(Path(f).suffix.lower() == ".txt" for f in self._files):
            lay.addSpacing(6)
            txt_tip = QLabel("「无法自动重装清单.txt」列出了没有安装源的软件，"
                             "重装后照它手动装一遍即可。", page)
            txt_tip.setWordWrap(True)
            txt_tip.setStyleSheet(f"color:{MUTED2}; font-size:8.5pt;")
            lay.addWidget(txt_tip)
        lay.addSpacing(16)
        self._open_flag = {"v": True}
        row = ClickableLabel("", page)
        rl = QHBoxLayout(row)
        rl.setContentsMargins(0, 0, 0, 0)
        rl.setSpacing(9)
        self.open_box = CheckBox(True, row)
        self.open_box.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        rl.addWidget(self.open_box)
        lbl = QLabel("导出后打开文件夹", row)
        lbl.setStyleSheet(f"color:{TEXT}; font-size:9pt;")
        # 点击穿透到整行，避免「点文字没反应、点空白才有反应」
        lbl.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        rl.addWidget(lbl)
        rl.addStretch(1)

        def _toggle() -> None:
            self._open_flag["v"] = not self._open_flag["v"]
            self.open_box.set_checked(self._open_flag["v"])

        row.clicked.connect(_toggle)
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
                os.startfile(str(self.dest.parent))  # noqa: S606
            except Exception:  # noqa: BLE001
                pass
        self.accept()


# ══════════════════════════ 主窗口 ══════════════════════════
class LogGrip(QWidget):
    """日志面板上沿的拖拽条：上下拖动改变日志高度，双击折叠/展开。"""

    def __init__(self, host: "MainWindow") -> None:
        super().__init__(host)
        self._host = host
        self._y0 = 0.0
        self._h0 = 0
        self.setFixedHeight(9)
        self.setCursor(Qt.SizeVerCursor)
        self.setToolTip("拖动调整日志高度 · 双击折叠")

    def mousePressEvent(self, e) -> None:  # noqa: N802
        if e.button() != Qt.LeftButton:
            return
        if not self._host._log_expanded:
            self._host._toggle_log()
            QTimer.singleShot(210, self._begin)
            return
        self._begin()

    def _begin(self) -> None:
        self._y0 = QCursor.pos().y()
        self._h0 = self._host.log_card.height()

    def mouseMoveEvent(self, e) -> None:  # noqa: N802
        if not (e.buttons() & Qt.LeftButton):
            return
        dy = self._y0 - QCursor.pos().y()      # 向上拖为正
        self._host._set_log_height(int(self._h0 + dy))

    def mouseDoubleClickEvent(self, _e) -> None:  # noqa: N802
        self._host._toggle_log()


class AboutDialog(QDialog):
    """关于：版本、winget 版本、配置与日志位置，诊断信息可一键复制。"""

    def __init__(self, app: "MainWindow") -> None:
        super().__init__(app)
        self.app = app
        self.setWindowTitle(f"关于 {APP_TITLE}")
        self.setModal(True)
        self.setFixedWidth(560)
        self.setWindowIcon(app_icon())

        outer = QVBoxLayout(self)
        outer.setContentsMargins(0, 0, 0, 0)
        page = QWidget(self)
        page.setStyleSheet(f"background:{CARD};")
        outer.addWidget(page)
        lay = QVBoxLayout(page)
        lay.setContentsMargins(28, 24, 28, 20)
        lay.setSpacing(0)

        # 头部：应用图标 + 名称 + 版本
        head = QHBoxLayout()
        head.setContentsMargins(0, 0, 0, 0)
        head.setSpacing(13)
        icon = QLabel(page)
        icon.setFixedSize(44, 44)
        icon.setPixmap(app_icon().pixmap(44, 44))
        icon.setStyleSheet("background:transparent;")
        head.addWidget(icon)
        tb = QVBoxLayout()
        tb.setContentsMargins(0, 0, 0, 0)
        tb.setSpacing(1)
        tb.addWidget(GradientLabel(APP_TITLE, 17))
        sub = QLabel(f"版本 {APP_VERSION} · {APP_TAG}", page)
        sub.setStyleSheet(f"color:{MUTED}; font-size:9pt;")
        tb.addWidget(sub)
        head.addLayout(tb)
        head.addStretch(1)
        lay.addLayout(head)

        lay.addSpacing(12)
        intro = QLabel("清点本机已安装软件，把可自动重装的包导出为 winget 官方清单；"
                       "重装系统后执行 winget import -i apps.json 即可批量恢复。",
                       page)
        intro.setWordWrap(True)
        intro.setStyleSheet(f"color:{MUTED}; font-size:8.8pt; line-height:150%;")
        lay.addWidget(intro)
        lay.addSpacing(14)

        snap = app.snapshot
        n = len(app.packages)
        n_re = sum(1 for p in app.packages if p.reinstallable)
        # 只读快照：不要在「关于」里同步跑 winget --version，
        # 那会在主线程上等一个子进程，没装 winget 时还会卡一下再抛异常。
        wg = (getattr(snap, "wingetVersion", "") if snap else "") or "未扫描"
        info = [
            ("应用版本", f"{APP_VERSION}（{APP_TAG}）", False),
            ("winget", wg, False),
            ("已扫描", f"{n} 个软件 · {n_re} 个可重装" if n else "尚未扫描", False),
            ("导出路径", str(default_export_path()), True),
            ("配置文件", str(SELECTION_FILE), True),
            ("运行日志", str(LOG_FILE) if LOG_FILE.is_file() else "本次未产生", True),
        ]
        grid = QWidget(page)
        gl = QVBoxLayout(grid)
        gl.setContentsMargins(0, 0, 0, 0)
        gl.setSpacing(0)
        for i, (k, v, mono) in enumerate(info):
            if i:
                ln = QFrame(grid)
                ln.setFixedHeight(1)
                ln.setStyleSheet(f"background:{LINE_SOFT};")
                gl.addWidget(ln)
            row = QWidget(grid)
            rl = QHBoxLayout(row)
            rl.setContentsMargins(2, 7, 2, 7)
            rl.setSpacing(10)
            kl = QLabel(k, row)
            kl.setFixedWidth(72)
            kl.setStyleSheet(f"color:{MUTED2}; font-size:8.6pt; background:transparent;")
            rl.addWidget(kl)
            vl = QLabel(v, row)
            fam = f"font-family:'{MONO_FAMILY}';" if mono else ""
            vl.setStyleSheet(f"color:{TEXT}; font-size:8.6pt; {fam}"
                             "background:transparent;")
            vl.setToolTip(v)
            vl.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
            vl.setTextInteractionFlags(Qt.TextSelectableByMouse)
            rl.addWidget(vl, 1)
            gl.addWidget(row)
        lay.addWidget(grid)

        lay.addSpacing(18)
        btns = QHBoxLayout()
        btns.setContentsMargins(0, 0, 0, 0)
        btns.setSpacing(9)
        copy = QPushButton("复制诊断信息", page)
        copy.setCursor(Qt.PointingHandCursor)
        copy.setFixedHeight(36)
        copy.setFocusPolicy(Qt.NoFocus)
        copy.setStyleSheet(
            f"QPushButton{{border:1px solid {LINE}; border-radius:9px; padding:0 14px;"
            f"background:{SURFACE}; color:{TEXT}; font-size:9pt;}}"
            f"QPushButton:hover{{background:{SURFACE_3}; border-color:{BORDER_HOVER};}}")
        copy.clicked.connect(lambda: self._copy_diag(wg, copy))
        btns.addWidget(copy)
        folder = QPushButton("打开数据目录", page)
        folder.setCursor(Qt.PointingHandCursor)
        folder.setFixedHeight(36)
        folder.setFocusPolicy(Qt.NoFocus)
        folder.setStyleSheet(copy.styleSheet())
        folder.clicked.connect(self._open_folder)
        btns.addWidget(folder)
        btns.addStretch(1)
        close = QPushButton("关闭", page)
        close.setCursor(Qt.PointingHandCursor)
        close.setFixedHeight(36)
        close.setDefault(True)
        close.setStyleSheet(
            "QPushButton{border:none; border-radius:9px; padding:0 20px; color:#fff;"
            "font-size:9.3pt; font-weight:600;"
            f"background:qlineargradient(x1:0,y1:0,x2:1,y2:1,stop:0 {ACCENT},"
            f"stop:1 {ACCENT_2});}}"
            "QPushButton:hover{background:#575AF0;}")
        close.clicked.connect(self.accept)
        btns.addWidget(close)
        lay.addLayout(btns)

    def _copy_diag(self, wg: str, btn: QPushButton) -> None:
        snap = self.app.snapshot
        lines = [
            f"{APP_TITLE} {APP_VERSION} ({APP_TAG})",
            f"winget: {wg}",
            f"Python: {sys.version.split()[0]}{' (frozen)' if getattr(sys, 'frozen', False) else ''}",
            f"扫描: {len(self.app.packages)} 个 / {self.app.state}",
            f"配置: {SELECTION_FILE}",
            f"日志: {LOG_FILE}",
        ]
        if snap is not None:
            lines.append(f"扫描耗时: {snap.durationMs / 1000:.1f}s")
        QApplication.clipboard().setText("\n".join(lines))
        btn.setText("已复制")
        QTimer.singleShot(1400, lambda: btn.setText("复制诊断信息"))

    def _open_folder(self) -> None:
        try:
            PERSIST_DIR.mkdir(parents=True, exist_ok=True)
            os.startfile(str(PERSIST_DIR))  # noqa: S606
        except Exception:  # noqa: BLE001
            pass


