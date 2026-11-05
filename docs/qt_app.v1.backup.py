"""Winget 软件管家 · 桌面版（PySide6 / Qt 原生现代 UI）。

设计目标：彻底摆脱 Tk 观感，做到 --
  · 真·圆角卡片 + QGraphicsDropShadowEffect 柔和投影
  · 自绘复选框（勾选动画）、行悬停渐变高亮、可折叠分组（高度动画）
  · 靛蓝强调色设计系统、深色日志终端、胶囊标签
  · 窗口淡入、进度条流动、状态机禁用矩阵

业务逻辑与 core/ services/ 完全复用，不改动任何后端代码。
"""
from __future__ import annotations

import json
import os
import re
import sys
import time
from datetime import datetime
from pathlib import Path

from PySide6.QtCore import (QEasingCurve, QEvent, QObject, QPoint, QPropertyAnimation,
                            QRectF, QSize, Qt, QThread, QTimer, Property, Signal)
from PySide6.QtGui import (QAction, QColor, QFont, QFontDatabase, QIcon, QKeySequence,
                           QPainter, QPainterPath, QPen, QPixmap, QShortcut, QTextCharFormat,
                           QTextCursor)
from PySide6.QtWidgets import (QApplication, QCheckBox, QDialog, QFileDialog, QFrame,
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

from core.config import Config, ConfigError, find_winget, load_config  # noqa: E402
from core.errors import AppError  # noqa: E402
from services.catalog import CatalogService  # noqa: E402
from services.jobs import _ANSI, _FAIL, _FATAL, _FOUND, _SKIP, _SUCCESS  # noqa: E402
from services.profiles import load_profile, write_exports  # noqa: E402
from services.winget_cli import WingetRepository  # noqa: E402

APP_TITLE = "Winget 软件管家"
PERSIST_DIR = Path.home() / "AppData/Local/WingetManager"
SELECTION_FILE = PERSIST_DIR / "selection.json"

# ── 设计令牌 ──
BG = "#F2F4F8"
CARD = "#FFFFFF"
BORDER = "#E5E9F0"
TEXT = "#1A2233"
SUB = "#697386"
FAINT = "#A6AEBB"
ACCENT = "#3E63DD"
ACCENT_HOVER = "#3651C9"
ACCENT_SOFT = "#EBEEFD"
DANGER = "#E5484D"
DANGER_T = "#D64545"
SUCCESS = "#1F9D55"
LOG_BG = "#151B28"

FONT_FAMILY = "HarmonyOS Sans SC"


def _pick_family(app: QApplication) -> str:
    families = set(QFontDatabase.families())
    for name in (FONT_FAMILY, "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI"):
        if name in families:
            return name
    return app.font().family()


def _mix(c1: str, c2: str, t: float) -> QColor:
    a, b = QColor(c1), QColor(c2)
    t = max(0.0, min(1.0, t))
    return QColor(
        round(a.red() + (b.red() - a.red()) * t),
        round(a.green() + (b.green() - a.green()) * t),
        round(a.blue() + (b.blue() - a.blue()) * t),
    )


def add_shadow(widget: QWidget, blur: int = 28, dy: int = 6, alpha: int = 24) -> None:
    eff = QGraphicsDropShadowEffect(widget)
    eff.setBlurRadius(blur)
    eff.setOffset(0, dy)
    eff.setColor(QColor(23, 37, 84, alpha))
    widget.setGraphicsEffect(eff)


def make_button(text: str, kind: str, slot, parent: QWidget | None = None,
                min_w: int = 0) -> QPushButton:
    btn = QPushButton(text, parent)
    btn.setObjectName(kind)
    btn.setCursor(Qt.PointingHandCursor)
    btn.setFocusPolicy(Qt.NoFocus if kind == "Ghost" else Qt.TabFocus)
    if min_w:
        btn.setMinimumWidth(min_w)
    btn.clicked.connect(slot)
    return btn


def magnifier_icon(color: str = SUB, size: int = 16) -> QIcon:
    pm = QPixmap(size, size)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    pen = QPen(QColor(color), 1.8)
    pen.setCapStyle(Qt.RoundCap)
    p.setPen(pen)
    r = size * 0.28
    p.drawEllipse(QRectF(size * 0.12, size * 0.12, r * 2, r * 2))
    p.drawLine(int(size * 0.62), int(size * 0.62), int(size * 0.86), int(size * 0.86))
    p.end()
    return QIcon(pm)


def app_icon() -> QIcon:
    pm = QPixmap(64, 64)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    p.setPen(Qt.NoPen)
    p.setBrush(QColor(ACCENT))
    p.drawRoundedRect(QRectF(2, 2, 60, 60), 16, 16)
    p.setPen(QColor("#FFFFFF"))
    f = QFont("Segoe UI", 30)
    f.setBold(True)
    p.setFont(f)
    p.drawText(QRectF(2, 2, 60, 60), Qt.AlignCenter, "W")
    p.end()
    return QIcon(pm)


# ────────────────────────────── 自绘控件 ──────────────────────────────
class CheckBox(QWidget):
    """18px 圆角复选框，勾选时填充+描线动画。"""

    def __init__(self, checked: bool = False, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.setFixedSize(20, 20)
        self._checked = checked
        self._prog = 1.0 if checked else 0.0
        self._anim = QPropertyAnimation(self, b"progress", self)
        self._anim.setDuration(170)
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

    def set_progress(self, value: float) -> None:
        self._prog = value
        self.update()

    progress = Property(float, get_progress, set_progress)

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        rect = QRectF(1.5, 1.5, 17, 17)
        fill = _mix("#FFFFFF", ACCENT, self._prog)
        border = _mix("#C9D1DE", ACCENT, self._prog)
        p.setPen(QPen(border, 1.4))
        p.setBrush(fill)
        p.drawRoundedRect(rect, 5.5, 5.5)
        if self._prog > 0.05:
            p.setOpacity(self._prog)
            pen = QPen(QColor("#FFFFFF"), 1.9)
            pen.setCapStyle(Qt.RoundCap)
            pen.setJoinStyle(Qt.RoundJoin)
            p.setPen(pen)
            path = QPainterPath()
            path.moveTo(5.4, 10.4)
            path.lineTo(8.4, 13.3)
            path.lineTo(14.1, 7.0)
            p.drawPath(path)
        p.end()


class ProgressBar(QWidget):
    """3px 流动进度条（不确定态）。"""

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
        self._phase = (self._phase + 0.011) % 1.0
        self.update()

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setPen(Qt.NoPen)
        p.fillRect(self.rect(), QColor(ACCENT_SOFT))
        w = self.width()
        seg = max(120, int(w * 0.26))
        x = int((w + seg) * self._phase) - seg
        p.setBrush(QColor(ACCENT))
        p.drawRoundedRect(QRectF(x, 0, seg, 3), 1.5, 1.5)
        p.end()


class PackageRow(QWidget):
    """单行：复选框 + 名称 + 包 ID + 版本，悬停背景渐变。"""

    toggled = Signal(object)
    context_requested = Signal(object, QPoint)
    copy_id = Signal(object)

    def __init__(self, pkg, checked: bool, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.pkg = pkg
        self._checked = checked
        self._hover = 0.0
        self.setFixedHeight(44)
        self.setFocusPolicy(Qt.StrongFocus)
        self.setCursor(Qt.PointingHandCursor)
        self.setAttribute(Qt.WA_Hover, True)

        lay = QHBoxLayout(self)
        lay.setContentsMargins(14, 0, 16, 0)
        lay.setSpacing(0)
        self.box = CheckBox(checked)
        self.box.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        lay.addWidget(self.box, 0, Qt.AlignVCenter)
        lay.addSpacing(15)

        self.lbl_name = QLabel(pkg.name, self)
        self.lbl_name.setMinimumWidth(210)
        self.lbl_name.setStyleSheet(f"color:{TEXT}; font-size:10.5pt;")
        lay.addWidget(self.lbl_name, 5)

        lay.addSpacing(14)
        self.lbl_id = QLabel(pkg.packageIdentifier, self)
        self.lbl_id.setStyleSheet(f"color:{SUB}; font-size:9pt;")
        self.lbl_id.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        lay.addWidget(self.lbl_id, 6)

        ver = f"v{pkg.version}" if pkg.version else "—"
        self.lbl_ver = QLabel(ver, self)
        self.lbl_ver.setFixedWidth(118)
        self.lbl_ver.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        self.lbl_ver.setStyleSheet("color:#8A93A5; font-size:9pt;")
        lay.addWidget(self.lbl_ver, 0)

        self._anim = QPropertyAnimation(self, b"hover", self)
        self._anim.setDuration(130)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)

    # ── hover 动画属性 ──
    def get_hover(self) -> float:
        return self._hover

    def set_hover(self, v: float) -> None:
        self._hover = v
        self.update()

    hover = Property(float, get_hover, set_hover)

    def _animate_hover(self, target: float) -> None:
        self._anim.stop()
        self._anim.setStartValue(self._hover)
        self._anim.setEndValue(target)
        self._anim.start()

    def enterEvent(self, _e) -> None:  # noqa: N802
        self._animate_hover(1.0)

    def leaveEvent(self, _e) -> None:  # noqa: N802
        self._animate_hover(0.0)

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        rect = QRectF(0, 1, self.width(), self.height() - 2)
        if self._hover > 0.01:
            p.setPen(Qt.NoPen)
            p.setBrush(_mix(CARD, "#F4F6FC", self._hover))
            p.drawRoundedRect(rect, 9, 9)
        if self.hasFocus():
            pen = QPen(QColor(ACCENT), 1.3)
            p.setPen(pen)
            p.setBrush(Qt.NoBrush)
            p.drawRoundedRect(rect.adjusted(1, 1, -1, -1), 9, 9)
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
        else:
            super().keyPressEvent(e)

    def toggle(self) -> None:
        self._checked = not self._checked
        self.box.set_checked(self._checked)
        self.toggled.emit(self.pkg)

    def set_checked(self, on: bool) -> None:
        self._checked = on
        self.box.set_checked(on, animate=False)


class ClickLabel(QLabel):
    """可点击的文本链接（悬停变色）。"""

    def __init__(self, text: str, color: str, hover: str, slot, parent=None) -> None:
        super().__init__(text, parent)
        self._color, self._hover, self._slot = color, hover, slot
        self.setCursor(Qt.PointingHandCursor)
        self.setStyleSheet(f"color:{color};")
        self._bind_hover()

    def _bind_hover(self) -> None:
        def enter(_e):
            self.setStyleSheet(f"color:{self._hover};")
        def leave(_e):
            self.setStyleSheet(f"color:{self._color};")
        self.enterEvent = enter
        self.leaveEvent = leave

    def mouseReleaseEvent(self, e) -> None:  # noqa: N802
        if e.button() == Qt.LeftButton:
            self._slot()


class GroupHeader(QWidget):
    """分组标题行：折叠箭头 + 名称 + 计数。"""

    clicked = Signal()

    def __init__(self, title: str, expanded: bool, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._hover = 0.0
        self.setFixedHeight(34)
        self.setCursor(Qt.PointingHandCursor)
        lay = QHBoxLayout(self)
        lay.setContentsMargins(14, 0, 16, 0)
        lay.setSpacing(8)
        self.chevron = QLabel("▾" if expanded else "▸", self)
        self.chevron.setFixedWidth(12)
        self.chevron.setStyleSheet(f"color:{SUB}; font-size:9pt;")
        lay.addWidget(self.chevron)
        self.title = QLabel(title, self)
        self.title.setStyleSheet(f"color:{TEXT}; font-size:10pt; font-weight:600;")
        lay.addWidget(self.title, 1)
        self.count = QLabel("0/0", self)
        self.count.setStyleSheet("color:#8A93A5; font-size:9pt;")
        lay.addWidget(self.count, 0)
        self._anim = QPropertyAnimation(self, b"hover", self)
        self._anim.setDuration(120)

    def get_hover(self) -> float:
        return self._hover

    def set_hover(self, v: float) -> None:
        self._hover = v
        self.update()

    hover = Property(float, get_hover, set_hover)

    def enterEvent(self, _e) -> None:  # noqa: N802
        self._anim.stop(); self._anim.setStartValue(self._hover)
        self._anim.setEndValue(1.0); self._anim.start()

    def leaveEvent(self, _e) -> None:  # noqa: N802
        self._anim.stop(); self._anim.setStartValue(self._hover)
        self._anim.setEndValue(0.0); self._anim.start()

    def paintEvent(self, _e) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        if self._hover > 0.01:
            p.setPen(Qt.NoPen)
            p.setBrush(_mix(CARD, "#F7F9FD", self._hover))
            p.drawRoundedRect(QRectF(4, 1, self.width() - 8, self.height() - 2), 8, 8)
        p.setPen(QPen(QColor("#EEF1F6"), 1))
        p.drawLine(14, self.height() - 1, self.width() - 16, self.height() - 1)
        p.end()

    def mouseReleaseEvent(self, e) -> None:  # noqa: N802
        if e.button() == Qt.LeftButton:
            self.clicked.emit()
        super().mouseReleaseEvent(e)


class GroupSection(QWidget):
    """可折叠分组：标题 + 行容器（高度动画）。"""

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
        self.body = QWidget(self)
        self.body_lay = QVBoxLayout(self.body)
        self.body_lay.setContentsMargins(6, 2, 6, 6)
        self.body_lay.setSpacing(2)
        lay.addWidget(self.body)
        if not expanded:
            self.body.setMaximumHeight(0)
            self.body.setVisible(False)
        self._anim = QPropertyAnimation(self.body, b"maximumHeight", self)
        self._anim.setDuration(190)
        self._anim.setEasingCurve(QEasingCurve.InOutCubic)

    def add_row(self, row: QWidget) -> None:
        self.body_lay.addWidget(row)

    def toggle(self) -> None:
        self.expanded = not self.expanded
        self.header.chevron.setText("▾" if self.expanded else "▸")
        target = self.body.sizeHint().height() if self.expanded else 0
        self._anim.stop()
        if self.expanded:
            self.body.setVisible(True)
        self._anim.setStartValue(self.body.maximumHeight()
                                if self.body.maximumHeight() > 0 else 0)
        self._anim.setEndValue(target)
        self._anim.finished.connect(self._after_toggle)

    def _after_toggle(self) -> None:
        try:
            self._anim.finished.disconnect(self._after_toggle)
        except (RuntimeError, TypeError):
            pass
        if self.expanded:
            self.body.setMaximumHeight(16777215)
        else:
            self.body.setVisible(False)


# ────────────────────────────── 后台线程 ──────────────────────────────
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


class ImportWorker(QThread):
    line = Signal(str)
    done = Signal(int, str)

    def __init__(self, repo: WingetRepository, path: Path, cancel_event) -> None:
        super().__init__()
        self._repo = repo
        self._path = path
        self._cancel = cancel_event

    def run(self) -> None:
        try:
            rc = self._repo.import_file(self._path, on_line=self.line.emit,
                                        cancel_flag=self._cancel.is_set)
            self.done.emit(rc, "")
        except Exception as exc:  # noqa: BLE001
            self.done.emit(-1, str(exc))


# ────────────────────────────── 导出对话框 ──────────────────────────────
class ExportDialog(QDialog):
    def __init__(self, app: "MainWindow", selected: list) -> None:
        super().__init__(app)
        self.app = app
        self.selected = selected
        self.setWindowTitle("导出配置")
        self.setModal(True)
        self.setFixedWidth(540)
        self.setWindowIcon(app_icon())
        self.stack = QStackedWidget(self)
        outer = QVBoxLayout(self)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.addWidget(self.stack)
        self.stack.addWidget(self._build_form())
        self._result_files: list[str] = []

    # ── 第一段：命名 ──
    def _build_form(self) -> QWidget:
        page = QWidget(self)
        lay = QVBoxLayout(page)
        lay.setContentsMargins(26, 24, 26, 22)
        lay.setSpacing(0)

        title = QLabel("导出配置", page)
        title.setStyleSheet(f"color:{TEXT}; font-size:14pt; font-weight:700;")
        lay.addWidget(title)
        lay.addSpacing(6)
        desc = QLabel(f"将导出已勾选的 {len(self.selected)} 个软件包\n"
                      "同时生成 winget 原生清单与富 profile 两份 JSON", page)
        desc.setStyleSheet(f"color:{SUB}; font-size:9.5pt;")
        lay.addWidget(desc)
        lay.addSpacing(18)

        lbl = QLabel("配置名称", page)
        lbl.setStyleSheet(f"color:{TEXT}; font-size:9.5pt; font-weight:600;")
        lay.addWidget(lbl)
        lay.addSpacing(6)
        self.name = QLineEdit("我的软件配置", page)
        self.name.setObjectName("Dialog")
        self.name.setMinimumHeight(38)
        self.name.textChanged.connect(lambda *_: self._validate())
        lay.addWidget(self.name)
        self.hint = QLabel("", page)
        self.hint.setFixedHeight(20)
        self.hint.setStyleSheet(f"color:{SUB}; font-size:9pt;")
        lay.addWidget(self.hint)
        path_lbl = QLabel(f"保存位置：{self.app.cfg.export_dir}", page)
        path_lbl.setStyleSheet("color:#8A93A5; font-size:8.5pt;")
        path_lbl.setWordWrap(True)
        lay.addWidget(path_lbl)

        btns = QHBoxLayout()
        btns.setContentsMargins(0, 18, 0, 0)
        btns.addStretch(1)
        cancel = make_button("取消", "Secondary", self.reject, page)
        btns.addWidget(cancel)
        btns.addSpacing(8)
        self.ok_btn = make_button("导出配置", "Primary", self._do_export, page, min_w=100)
        btns.addWidget(self.ok_btn)
        lay.addLayout(btns)

        self.name.setFocus()
        self.name.selectAll()
        QTimer.singleShot(30, self._validate)
        return page

    def _validate(self) -> None:
        name = self.name.text().strip()
        bad = not name or any(ch in name for ch in '\\/:*?"<>|')
        if bad:
            self.name.setStyleSheet(
                "border:1px solid #E5484D; border-radius:8px; padding:8px 10px;"
                "background:#FDF2F2; color:#D64545;")
            self.hint.setStyleSheet(f"color:{DANGER_T}; font-size:9pt;")
            self.ok_btn.setEnabled(False)
            self.hint.setText("配置名称不能为空" if not name else '名称不能包含 \\ / : * ? " < > |')
            return
        self.name.setStyleSheet("")
        self.ok_btn.setEnabled(True)
        try:
            dup = any(self.app.cfg.export_dir.glob(f"{name}*"))
        except Exception:  # noqa: BLE001
            dup = False
        if dup:
            self.hint.setStyleSheet("color:#92400E; font-size:9pt;")
            self.hint.setText("同名配置将被覆盖")
            self.ok_btn.setText("覆盖导出")
        else:
            self.hint.setStyleSheet(f"color:{SUB}; font-size:9pt;")
            self.hint.setText("")
            self.ok_btn.setText("导出配置")

    def _do_export(self) -> None:
        name = self.name.text().strip()
        snapshot = self.app.snapshot
        try:
            result = write_exports(self.app.cfg.export_dir, name, "", self.selected, snapshot,
                                   snapshot.wingetVersion if snapshot else "1.0.0")
        except AppError as exc:
            self.hint.setStyleSheet(f"color:{DANGER_T}; font-size:9pt;")
            self.hint.setText(exc.user_message)
            return
        except Exception as exc:  # noqa: BLE001
            self.hint.setStyleSheet(f"color:{DANGER_T}; font-size:9pt;")
            self.hint.setText(f"导出失败：{exc}")
            return
        self._result_files = [str(result.primary_path)]
        if result.winget_path:
            self._result_files.append(str(result.winget_path))
        self.app.log_append("done", f"已导出 {result.package_count} 个软件包：")
        for f in self._result_files:
            self.app.log_append("info", f"  {f}")
        self.stack.addWidget(self._build_success(result.package_count))
        self.stack.setCurrentIndex(1)

    # ── 第二段：成功 ──
    def _build_success(self, count: int) -> QWidget:
        page = QWidget(self)
        lay = QVBoxLayout(page)
        lay.setContentsMargins(26, 26, 26, 22)
        lay.setSpacing(0)

        badge = QLabel("✓", page)
        badge.setFixedSize(46, 46)
        badge.setAlignment(Qt.AlignCenter)
        badge.setStyleSheet(
            f"background:{ACCENT_SOFT}; color:{ACCENT}; border-radius:23px;"
            "font-size:20pt; font-weight:700;")
        lay.addWidget(badge)
        lay.addSpacing(14)
        title = QLabel(f"已导出 {count} 个软件包", page)
        title.setStyleSheet(f"color:{TEXT}; font-size:13pt; font-weight:700;")
        lay.addWidget(title)
        lay.addSpacing(10)
        for f in self._result_files:
            item = QLabel(Path(f).name, page)
            item.setStyleSheet(f"color:{SUB}; font-size:9pt;")
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
        self.open_lbl = ClickLabel("导出后打开文件夹", TEXT, ACCENT, lambda: None, row)
        self.open_lbl.setStyleSheet(f"color:{TEXT}; font-size:9.5pt;")
        self.open_lbl._bind_hover = lambda: None
        rl.addWidget(self.open_lbl)
        rl.addStretch(1)

        def _toggle(_e=None):
            self._open_flag["v"] = not self._open_flag["v"]
            self.open_box.set_checked(self._open_flag["v"])

        row.mouseReleaseEvent = _toggle
        self.open_lbl.mouseReleaseEvent = _toggle
        lay.addWidget(row)
        self.open_after = self.open_box

        btns = QHBoxLayout()
        btns.setContentsMargins(0, 20, 0, 0)
        btns.addStretch(1)
        close = make_button("关闭", "Primary", self._close, page, min_w=88)
        btns.addWidget(close)
        lay.addLayout(btns)
        return page

    def _close(self) -> None:
        if self._open_flag["v"]:
            try:
                os.startfile(self.app.cfg.export_dir)  # noqa: S606
            except Exception:  # noqa: BLE001
                pass
        self.accept()


# ────────────────────────────── 主窗口 ──────────────────────────────
class MainWindow(QMainWindow):
    def __init__(self, cfg: Config) -> None:
        super().__init__()
        self.cfg = cfg
        self.repo = WingetRepository(cfg)
        self.catalog = CatalogService(cfg, self.repo)
        self.snapshot = None
        self.packages: list = []
        self.checked: dict[str, bool] = {}
        self.rows: dict[str, PackageRow] = {}
        self.sections: list[GroupSection] = []
        self.expanded: dict[str, bool] = {}
        self._matched = 0
        self.state = "BOOT"
        self._chip_text = ""
        self._import_base = ""
        self._counts = {"success": 0, "skipped": 0, "failed": 0}
        self._scan_worker = None
        self._import_worker = None
        self._cancel_event = None
        self._scan_t0 = 0.0
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
        self.resize(1180, 820)
        self.setMinimumSize(1000, 680)

        self._build_ui()
        self._load_selection()
        self._apply_styles()
        self._set_state("BOOT")
        self._fade_in()
        sc = QShortcut(QKeySequence("Ctrl+F"), self)
        sc.activated.connect(self._focus_search)
        self._start_scan()

    # ── 样式表（设计系统唯一出口）──
    def _apply_styles(self) -> None:
        self.setStyleSheet(f"""
        QWidget {{
            font-family: "{FONT_FAMILY}", "Microsoft YaHei UI", "Segoe UI";
            font-size: 10pt; color: {TEXT};
        }}
        #Root {{ background: {BG}; }}
        #Header {{ background: {CARD}; border-bottom: 1px solid {BORDER}; }}
        #AppTitle {{ font-size: 12.5pt; font-weight: 700; color: {TEXT}; }}
        #Chip {{
            background: {ACCENT_SOFT}; color: {ACCENT}; border-radius: 12px;
            padding: 3px 11px; font-size: 8.5pt;
        }}
        #Card {{ background: {CARD}; border: 1px solid {BORDER}; border-radius: 12px; }}
        #CardHead {{ background: transparent; border-bottom: 1px solid #F0F3F8;
                     border-top-left-radius: 12px; border-top-right-radius: 12px; }}
        #CardTitle {{ font-size: 10.5pt; font-weight: 700; color: {TEXT}; }}
        #Muted {{ color: {SUB}; font-size: 9pt; }}
        #Tiny {{ color: #8A93A5; font-size: 8.5pt; }}

        QPushButton#Primary {{
            background: {ACCENT}; color: #FFFFFF; border: none; border-radius: 9px;
            padding: 8px 20px; font-size: 9.5pt; font-weight: 600; min-height: 20px;
        }}
        QPushButton#Primary:hover {{ background: {ACCENT_HOVER}; }}
        QPushButton#Primary:pressed {{ background: #2E46B0; }}
        QPushButton#Primary:disabled {{ background: #EEF1F6; color: {FAINT}; }}

        QPushButton#Secondary {{
            background: {CARD}; color: #3A4454; border: 1px solid #D5DBE5;
            border-radius: 9px; padding: 8px 16px; font-size: 9.5pt; min-height: 20px;
        }}
        QPushButton#Secondary:hover {{ background: #F5F7FB; border-color: #C7D0DE; }}
        QPushButton#Secondary:pressed {{ background: #EDF0F6; }}
        QPushButton#Secondary:disabled {{ background: #F7F8FB; color: {FAINT};
                                          border-color: #E9EDF3; }}

        QPushButton#Ghost {{
            background: #F1F4F9; color: #4A5468; border: none; border-radius: 8px;
            padding: 6px 12px; font-size: 8.8pt; font-weight: 600; min-height: 16px;
        }}
        QPushButton#Ghost:hover {{ background: #E7EBF3; color: {TEXT}; }}
        QPushButton#Ghost:pressed {{ background: #DDE3EC; }}
        QPushButton#Ghost:disabled {{ background: #F6F7FA; color: #BFC6D2; }}

        QPushButton#DangerGhost {{
            background: #F1F4F9; color: {DANGER_T}; border: none; border-radius: 9px;
            padding: 8px 14px; font-size: 9.5pt; min-height: 20px;
        }}
        QPushButton#DangerGhost:hover {{ background: #FDECEC; }}
        QPushButton#DangerGhost:disabled {{ background: #F7F8FB; color: #CFC3C3; }}

        QPushButton#DangerSolid {{
            background: {DANGER}; color: #FFFFFF; border: none; border-radius: 9px;
            padding: 8px 14px; font-size: 9.5pt; font-weight: 600; min-height: 20px;
        }}
        QPushButton#DangerSolid:hover {{ background: #D6393F; }}
        QPushButton#DangerSolid:pressed {{ background: #C2313A; }}

        QLineEdit#Search {{
            background: {CARD}; border: 1px solid #DDE3EC; border-radius: 10px;
            padding: 7px 10px; font-size: 9.5pt; color: {TEXT};
            selection-background-color: {ACCENT_SOFT}; selection-color: {TEXT};
        }}
        QLineEdit#Search:focus {{ border: 1px solid {ACCENT}; }}
        QLineEdit#Dialog {{
            border: 1px solid #D5DBE5; border-radius: 8px; padding: 8px 10px;
            font-size: 9.5pt; background: {CARD};
        }}
        QLineEdit#Dialog:focus {{ border: 1px solid {ACCENT}; }}

        QScrollArea {{ background: transparent; border: none; }}
        QScrollArea > QWidget > QWidget {{ background: transparent; }}
        QScrollBar:vertical {{ background: transparent; width: 10px; margin: 6px 2px 6px 0; }}
        QScrollBar::handle:vertical {{ background: #D8DEE9; border-radius: 4px;
                                       min-height: 44px; }}
        QScrollBar::handle:vertical:hover {{ background: #C0C9D8; }}
        QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {{ height: 0; }}
        QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical {{ background: none; }}

        #LogHead {{ background: transparent; border-bottom: 1px solid #232B3D; }}
        #LogTitle {{ color: #E2E8F0; font-size: 9.5pt; font-weight: 600; }}
        #LogLink {{ color: #7C8AA0; font-size: 9pt; }}
        #LogLink:hover {{ color: #CBD5E1; }}
        QTextEdit#Log {{ background: {LOG_BG}; color: #C7D0DE; border: none;
                         border-bottom-left-radius: 12px;
                         border-bottom-right-radius: 12px;
                         padding: 8px 12px; selection-background-color: #334155;
                         font-size: 9.5pt; }}

        #StatusBar {{ background: {CARD}; border-top: 1px solid {BORDER}; }}
        QMenu {{ background: {CARD}; border: 1px solid {BORDER}; border-radius: 9px;
                 padding: 5px; }}
        QMenu::item {{ padding: 7px 20px; border-radius: 6px; font-size: 9.5pt; }}
        QMenu::item:selected {{ background: {ACCENT_SOFT}; color: {ACCENT}; }}
        QDialog {{ background: {CARD}; }}
        """)

    # ── UI 构建 ──
    def _build_ui(self) -> None:
        root = QWidget(self)
        root.setObjectName("Root")
        root.setAttribute(Qt.WA_StyledBackground, True)
        self.setCentralWidget(root)
        outer = QVBoxLayout(root)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)

        # 顶栏
        header = QFrame(root)
        header.setObjectName("Header")
        header.setFixedHeight(64)
        hl = QHBoxLayout(header)
        hl.setContentsMargins(22, 0, 22, 0)
        hl.setSpacing(0)
        dot = QLabel(header)
        dot.setFixedSize(10, 10)
        dot.setStyleSheet(f"background:{ACCENT}; border-radius:5px;")
        hl.addWidget(dot)
        hl.addSpacing(10)
        title = QLabel(APP_TITLE, header)
        title.setObjectName("AppTitle")
        hl.addWidget(title)
        hl.addSpacing(14)
        self.chip = QLabel("", header)
        self.chip.setObjectName("Chip")
        self.chip.setVisible(False)
        hl.addWidget(self.chip)
        hl.addStretch(1)
        outer.addWidget(header)

        # 工具栏
        bar = QWidget(root)
        bar.setFixedHeight(66)
        bl = QHBoxLayout(bar)
        bl.setContentsMargins(22, 0, 22, 0)
        bl.setSpacing(0)
        self.search = QLineEdit(bar)
        self.search.setObjectName("Search")
        self.search.setPlaceholderText("搜索名称或包 ID")
        self.search.setFixedSize(250, 38)
        self.search.addAction(magnifier_icon(), QLineEdit.LeadingPosition)
        self.search.setClearButtonEnabled(True)
        self.search.textChanged.connect(lambda *_: self._search_timer.start())
        self.search.installEventFilter(self)
        bl.addWidget(self.search)
        bl.addSpacing(12)

        self.scan_btn = make_button("重新扫描", "Secondary", self._start_scan, bar)
        bl.addWidget(self.scan_btn)
        bl.addSpacing(10)

        self.bulk_btns = []
        for text, slot in (("全选", lambda: self._set_all(True)),
                           ("全不选", lambda: self._set_all(False)),
                           ("反选", self._invert), ("取消运行库", self._apps_only)):
            b = make_button(text, "Ghost", slot, bar)
            bl.addWidget(b)
            bl.addSpacing(6)
            self.bulk_btns.append(b)

        bl.addStretch(1)
        self.cancel_btn = make_button("取消导入", "DangerGhost", self._on_cancel, bar)
        bl.addWidget(self.cancel_btn)
        bl.addSpacing(8)
        self.import_btn = make_button("导入恢复…", "Secondary", self._on_import, bar)
        bl.addWidget(self.import_btn)
        bl.addSpacing(8)
        self.export_btn = make_button("导出配置", "Primary", self._on_export, bar, min_w=110)
        bl.addWidget(self.export_btn)
        outer.addWidget(bar)

        # 进度条
        self.progress = ProgressBar(root)
        self.progress.hide()
        outer.addWidget(self.progress)

        # 内容区
        content = QWidget(root)
        cl = QVBoxLayout(content)
        cl.setContentsMargins(22, 12, 22, 0)
        cl.setSpacing(12)
        outer.addWidget(content, 1)

        # 列表卡片
        card = QFrame(content)
        card.setObjectName("Card")
        add_shadow(card)
        card_l = QVBoxLayout(card)
        card_l.setContentsMargins(0, 0, 0, 8)
        card_l.setSpacing(0)
        head = QWidget(card)
        head.setObjectName("CardHead")
        head.setFixedHeight(50)
        head_l = QHBoxLayout(head)
        head_l.setContentsMargins(18, 0, 18, 0)
        head_l.setSpacing(10)
        ct = QLabel("已安装软件", head)
        ct.setObjectName("CardTitle")
        head_l.addWidget(ct)
        self.count_lbl = QLabel("", head)
        self.count_lbl.setObjectName("Muted")
        head_l.addWidget(self.count_lbl)
        head_l.addStretch(1)
        self.hint_lbl = QLabel("勾选后点右上角「导出配置」", head)
        self.hint_lbl.setObjectName("Tiny")
        head_l.addWidget(self.hint_lbl)
        card_l.addWidget(head)

        self.stack = QStackedWidget(card)
        self.scroll = QScrollArea(self.stack)
        self.scroll.setWidgetResizable(True)
        self.scroll.setFrameShape(QFrame.NoFrame)
        self.scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.rows_host = QWidget()
        self.rows_lay = QVBoxLayout(self.rows_host)
        self.rows_lay.setContentsMargins(8, 6, 10, 6)
        self.rows_lay.setSpacing(0)
        self.rows_lay.addStretch(1)
        self.scroll.setWidget(self.rows_host)
        self.stack.addWidget(self.scroll)
        self.stack.addWidget(self._build_empty())
        card_l.addWidget(self.stack, 1)
        cl.addWidget(card, 1)

        # 日志卡片
        log_card = QFrame(content)
        log_card.setObjectName("Card")
        log_card.setFixedHeight(184)
        add_shadow(log_card, blur=22, dy=5, alpha=20)
        ll = QVBoxLayout(log_card)
        ll.setContentsMargins(0, 0, 0, 0)
        ll.setSpacing(0)
        lhead = QWidget(log_card)
        lhead.setObjectName("LogHead")
        lhead.setFixedHeight(38)
        lhead_l = QHBoxLayout(lhead)
        lhead_l.setContentsMargins(16, 0, 12, 0)
        lt = QLabel("运行日志", lhead)
        lt.setObjectName("LogTitle")
        lhead_l.addWidget(lt)
        lhead_l.addStretch(1)
        self.clear_log_link = ClickLabel("清空", "#7C8AA0", "#CBD5E1",
                                         lambda: self.log.clear(), lhead)
        lhead_l.addWidget(self.clear_log_link)
        log_card.setStyleSheet(f"#Card{{background:{LOG_BG}; border:1px solid {BORDER};"
                               f"border-radius:12px;}} #LogHead{{background:transparent;"
                               "border-bottom:1px solid #232B3D;"
                               "border-top-left-radius:12px; border-top-right-radius:12px;}")
        ll.addWidget(lhead)
        self.log = QTextEdit(log_card)
        self.log.setObjectName("Log")
        self.log.setReadOnly(True)
        self.log.setFrameShape(QFrame.NoFrame)
        self.log.setVerticalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        ll.addWidget(self.log, 1)
        cl.addWidget(log_card)

        # 状态栏
        status = QFrame(root)
        status.setObjectName("StatusBar")
        status.setFixedHeight(38)
        sl = QHBoxLayout(status)
        sl.setContentsMargins(22, 0, 22, 0)
        self.status_lbl = QLabel("正在扫描已安装软件…", status)
        self.status_lbl.setObjectName("Muted")
        sl.addWidget(self.status_lbl)
        sl.addStretch(1)
        self.imp_lbl = QLabel("", status)
        self.imp_lbl.setTextFormat(Qt.RichText)
        self.imp_lbl.setVisible(False)
        sl.addWidget(self.imp_lbl)
        self.sel_lbl = QLabel("", status)
        self.sel_lbl.setTextFormat(Qt.RichText)
        sl.addWidget(self.sel_lbl)
        outer.addWidget(status)

    def _build_empty(self) -> QWidget:
        page = QWidget()
        lay = QVBoxLayout(page)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.addStretch(1)
        icon = QLabel(page)
        icon.setFixedSize(64, 64)
        icon.setAlignment(Qt.AlignCenter)
        icon.setStyleSheet("background:#F5F7FB; border-radius:32px;"
                           "color:#B6C0CF; font-size:22pt;")
        icon.setText("⌕")
        lay.addWidget(icon, 0, Qt.AlignHCenter)
        lay.addSpacing(12)
        self.empty_lbl = QLabel("", page)
        self.empty_lbl.setAlignment(Qt.AlignCenter)
        self.empty_lbl.setStyleSheet(f"color:{SUB}; font-size:10pt;")
        lay.addWidget(self.empty_lbl, 0, Qt.AlignHCenter)
        lay.addStretch(1)
        return page

    # ── 交互辅助 ──
    def _focus_search(self) -> None:
        self.search.setFocus()
        self.search.selectAll()

    def eventFilter(self, obj, event):  # noqa: N802
        if obj is self.search and event.type() == QEvent.KeyPress:
            if event.key() == Qt.Key_Escape:
                self.search.clear()
                self.search.clearFocus()
                return True
        return super().eventFilter(obj, event)

    def resizeEvent(self, e) -> None:  # noqa: N802
        super().resizeEvent(e)
        if not hasattr(self, "hint_lbl"):
            return
        narrow = self.width() < 1080
        self.search.setFixedWidth(190 if narrow else 250)
        self.hint_lbl.setVisible(not narrow)
        self._sync_chip()

    def _sync_chip(self) -> None:
        text = "正在扫描…" if self.state == "SCANNING" else self._chip_text
        narrow = self.width() < 1080
        self.chip.setVisible(bool(text) and not narrow)
        self.chip.setText(text)

    # ── 窗口淡入 ──
    def _fade_in(self) -> None:
        eff = QGraphicsOpacityEffect(self.centralWidget())
        self.centralWidget().setGraphicsEffect(eff)
        anim = QPropertyAnimation(eff, b"opacity", self)
        anim.setDuration(280)
        anim.setStartValue(0.0)
        anim.setEndValue(1.0)
        anim.setEasingCurve(QEasingCurve.OutCubic)

        def done():
            self.centralWidget().setGraphicsEffect(None)

        anim.finished.connect(done)
        anim.start()
        self._fade_anim = anim

    # ── 状态机 ──
    def _set_state(self, state: str) -> None:
        self.state = state
        s = state
        self.scan_btn.setEnabled(s in ("READY", "SCAN_FAILED"))
        bulk_ok = s in ("READY", "IMPORTING", "CANCELING")
        for b in self.bulk_btns:
            b.setEnabled(bulk_ok)
        self.import_btn.setEnabled(s in ("READY", "SCAN_FAILED"))
        if s in ("IMPORTING", "CANCELING"):
            self.cancel_btn.setObjectName("DangerSolid")
            self.cancel_btn.setEnabled(s == "IMPORTING")
        else:
            self.cancel_btn.setObjectName("DangerGhost")
            self.cancel_btn.setEnabled(False)
        self.cancel_btn.style().unpolish(self.cancel_btn)
        self.cancel_btn.style().polish(self.cancel_btn)
        self._sync_chip()
        if s in ("IMPORTING", "CANCELING"):
            self.sel_lbl.setVisible(False)
            self.imp_lbl.setVisible(True)
        else:
            self.imp_lbl.setVisible(False)
            self.sel_lbl.setVisible(True)
        self._update_status()
        self._update_empty()

    # ── 扫描 ──
    def _start_scan(self) -> None:
        if self.state in ("SCANNING", "IMPORTING", "CANCELING"):
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
        prefix = "已导入 · " if self._import_base else ""
        self.status_lbl.setText(f"{prefix}正在扫描 · 已用 {elapsed:.1f}s")

    def _on_scan_ok(self, snapshot) -> None:
        self._scan_timer.stop()
        self.progress.stop()
        self.snapshot = snapshot
        self.packages = [p for p in snapshot.packages if p.reinstallable]
        hidden = snapshot.counts["total"] - len(self.packages)
        self._chip_text = f"winget {snapshot.wingetVersion} · {snapshot.machine.get('hostname', '')}"
        self.log_append("info",
                        f"扫描完成：可重装 {len(self.packages)} 个；另有 {hidden} 个系统组件"
                        "无法自动重装，已隐藏。")
        valid = {p.packageIdentifier.lower() for p in self.packages}
        removed = [k for k in self.checked if k not in valid]
        for k in removed:
            del self.checked[k]
        if removed:
            self.log_append("info", f"已移除 {len(removed)} 个已卸载软件的勾选记录")
        self._rebuild_list()
        self._set_state("READY")
        self._save_selection()

    def _on_scan_err(self, msg: str) -> None:
        self._scan_timer.stop()
        self.progress.stop()
        self._set_state("SCAN_FAILED")
        self.status_lbl.setText("扫描失败")
        self.log_append("error", f"扫描失败：{msg}")

    # ── 列表重建 ──
    def _clear_rows(self) -> None:
        for sec in self.sections:
            self.expanded[sec.title] = sec.expanded
        self.sections.clear()
        self.rows.clear()
        while self.rows_lay.count() > 1:
            item = self.rows_lay.takeAt(0)
            w = item.widget()
            if w is not None:
                w.setParent(None)
                w.deleteLater()

    def _rebuild_list(self) -> None:
        self._clear_rows()
        keyword = self.search.text().strip().lower()
        groups: dict[str, list] = {}
        for pkg in self.packages:
            if keyword and keyword not in f"{pkg.name} {pkg.packageIdentifier}".lower():
                continue
            groups.setdefault(pkg.category, []).append(pkg)
        self._matched = sum(len(v) for v in groups.values())

        for category in sorted(groups, key=lambda c: (-len(groups[c]), c)):
            pkgs = groups[category]
            sec = GroupSection(category, self.expanded.get(category, True), self.rows_host)
            for pkg in pkgs:
                key = pkg.packageIdentifier.lower()
                row = PackageRow(pkg, self.checked.get(key, False), sec.body)
                row.toggled.connect(self._on_row_toggled)
                row.context_requested.connect(self._show_row_menu)
                row.copy_id.connect(lambda p: self._copy(p.packageIdentifier,
                                                          f"已复制包 ID：{p.packageIdentifier}"))
                sec.add_row(row)
                self.rows[key] = row
            self.sections.append(sec)
            self.rows_lay.insertWidget(self.rows_lay.count() - 1, sec)
            self._update_section_count(category)

        self._update_status()
        self._update_empty()

    def _update_section_count(self, category: str) -> None:
        for sec in self.sections:
            if sec.title != category:
                continue
            rows = [r for r in self.rows.values() if r.pkg.category == category]
            n = len(rows)
            sel = sum(1 for r in rows if r._checked)
            sec.header.count.setText(f"{sel}/{n}")

    def _update_all_counts(self) -> None:
        for sec in self.sections:
            rows = [r for r in self.rows.values() if r.pkg.category == sec.title]
            sec.header.count.setText(f"{sum(1 for r in rows if r._checked)}/{len(rows)}")

    def _update_empty(self) -> None:
        if not self.packages:
            if self.state in ("BOOT", "SCANNING"):
                self.empty_lbl.setText("正在扫描已安装软件，约需 5~20 秒…")
            elif self.state == "SCAN_FAILED":
                self.empty_lbl.setText("扫描失败：winget 未安装或不可用\n可点击上方「重新扫描」重试")
            else:
                self.empty_lbl.setText("未发现可重装的软件")
            self.stack.setCurrentIndex(1)
            return
        keyword = self.search.text().strip()
        if keyword and self._matched == 0:
            self.empty_lbl.setText(f"未找到与「{keyword}」匹配的软件")
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
        if self.state not in ("READY", "IMPORTING", "CANCELING"):
            return
        for p in self.packages:
            self.checked[p.packageIdentifier.lower()] = value
        for row in self.rows.values():
            row.set_checked(value)
        self._update_all_counts()
        self._update_status()
        self._save_selection()

    def _invert(self) -> None:
        if self.state not in ("READY", "IMPORTING", "CANCELING"):
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
        if self.state not in ("READY", "IMPORTING", "CANCELING"):
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

    # ── 状态栏 ──
    def _update_status(self) -> None:
        total = len(self.packages)
        n = len(self.selected_packages())
        if n:
            self.sel_lbl.setText(
                f"<span style='color:{SUB}; font-size:9pt;'>已勾选 </span>"
                f"<span style='color:{ACCENT}; font-size:10pt; font-weight:700;'>{n}</span>"
                f"<span style='color:{SUB}; font-size:9pt;'> / {total} 项</span>")
        else:
            self.sel_lbl.setText(
                f"<span style='color:{SUB}; font-size:9pt;'>已勾选 0 项 · 勾选后点「导出配置」</span>")
        self.export_btn.setEnabled(self.state in ("SCANNING", "SCAN_FAILED", "READY") and n > 0)
        if self._matched and self.search.text().strip():
            self.count_lbl.setText(f"匹配 {self._matched} / 共 {total}")
        else:
            self.count_lbl.setText(f"{total} 个可重装" if total else "")
        if self.state == "SCANNING":
            return
        if self.state == "IMPORTING":
            self.status_lbl.setText(self._import_base)
            self.imp_lbl.setText(
                f"<span style='color:{SUCCESS}; font-size:9pt;'>成功 {self._counts['success']}</span>"
                f"<span style='color:{SUB}; font-size:9pt;'> · 跳过 {self._counts['skipped']}"
                f" · </span><span style='color:{DANGER_T}; font-size:9pt;'>"
                f"失败 {self._counts['failed']}</span>")
            return
        if self.state == "CANCELING":
            self.status_lbl.setText("正在取消导入…（winget 终止中）")
            return
        kw = self.search.text().strip()
        if kw:
            self.status_lbl.setText(f"匹配 {self._matched} / 共 {total} 项")
        elif self.snapshot:
            self.status_lbl.setText(
                f"共 {total} 个可重装软件 · 扫描耗时 {self.snapshot.durationMs / 1000:.1f}s")

    def _flash(self, text: str) -> None:
        self.status_lbl.setText(text)
        self._flash_timer.start()

    # ── 复制 / 右键 ──
    def _copy(self, text: str, flash: str) -> None:
        QApplication.clipboard().setText(text)
        self._flash(flash)

    def _show_row_menu(self, row: PackageRow, pos: QPoint) -> None:
        menu = QMenu(self)
        pkg = row.pkg
        menu.addAction("复制包 ID", lambda: self._copy(pkg.packageIdentifier,
                                                        f"已复制包 ID：{pkg.packageIdentifier}"))
        menu.addAction("复制软件名称", lambda: self._copy(pkg.name, f"已复制：{pkg.name}"))
        menu.addAction(f"复制「名称 (包ID)」",
                       lambda: self._copy(f"{pkg.name} ({pkg.packageIdentifier})", "已复制"))
        menu.addSeparator()
        menu.addAction("勾选 / 取消勾选", row.toggle)
        menu.exec(pos)

    # ── 日志 ──
    def log_append(self, tag: str, text: str) -> None:
        colors = {"ts": "#5C6B85", "info": "#C7D0DE", "success": "#4ADE80",
                  "skip": "#7C8AA0", "fail": "#F87171", "error": "#FCA5A5",
                  "done": "#93B4FF"}
        sb = self.log.verticalScrollBar()
        at_end = sb.value() >= sb.maximum() - 4
        cursor = self.log.textCursor()
        cursor.movePosition(QTextCursor.End)
        ts_fmt = QTextCharFormat()
        ts_fmt.setForeground(QColor(colors["ts"]))
        ts_fmt.setFontFamilies(["Consolas"])
        ts_fmt.setFontPointSize(8.5)
        cursor.insertText(
            datetime.now().strftime("%H:%M:%S") + "  ", ts_fmt)
        fmt = QTextCharFormat()
        fmt.setForeground(QColor(colors.get(tag, "#C7D0DE")))
        if tag == "done":
            fmt.setFontWeight(QFont.Bold)
        if tag == "error":
            fmt.setBackground(QColor("#3B1D22"))
        cursor.insertText(text + "\n", fmt)
        if at_end:
            sb.setValue(sb.maximum())

    # ── 导出 / 导入 ──
    def _on_export(self) -> None:
        selected = self.selected_packages()
        if not selected:
            QMessageBox.warning(self, APP_TITLE, "请先勾选需要保留的软件。")
            return
        dlg = ExportDialog(self, selected)
        dlg.exec()

    def _on_import(self) -> None:
        if self.state in ("IMPORTING", "CANCELING"):
            QMessageBox.information(self, APP_TITLE, "已有导入任务正在进行。")
            return
        path, _ = QFileDialog.getOpenFileName(
            self, "选择配置文件", str(self.cfg.export_dir),
            "JSON 配置 (*.json);;全部文件 (*.*)")
        if not path:
            return
        try:
            profile = load_profile(Path(path))
        except AppError as exc:
            QMessageBox.critical(self, APP_TITLE, exc.user_message)
            return
        except Exception as exc:  # noqa: BLE001
            QMessageBox.critical(self, APP_TITLE, f"读取失败：{exc}")
            return
        ok = QMessageBox.question(
            self, APP_TITLE,
            f"即将安装「{profile.profile_name}」中的 {len(profile.packages)} 个软件包。\n"
            "已安装的会自动跳过。是否继续？",
            QMessageBox.Yes | QMessageBox.No, QMessageBox.Yes)
        if ok != QMessageBox.Yes:
            return
        self._start_import(profile)

    def _start_import(self, profile) -> None:
        import threading
        self._cancel_event = threading.Event()
        self._counts = {"success": 0, "skipped": 0, "failed": 0}
        self._import_base = f"正在导入「{profile.profile_name}」…"
        self._set_state("IMPORTING")
        self.progress.start()
        self.log_append("done", f"开始导入「{profile.profile_name}」，"
                                f"共 {len(profile.packages)} 个软件包")
        self._import_worker = ImportWorker(self.repo, profile.path, self._cancel_event)
        self._import_worker.line.connect(self._on_import_line)
        self._import_worker.done.connect(self._on_import_done)
        self._import_worker.start()

    def _on_import_line(self, raw: str) -> None:
        line = _ANSI.sub("", raw).replace("\r", " ").strip()
        if not line or set(line) <= {"-", " ", "\\", "|", "/", "█", "░", "━"}:
            return
        if _FATAL.search(line):
            self.log_append("error", line)
            return
        if _SUCCESS.search(line):
            self._counts["success"] += 1
            self.log_append("success", line)
        elif _SKIP.search(line):
            self._counts["skipped"] += 1
            self.log_append("skip", line)
        elif _FAIL.search(line):
            self._counts["failed"] += 1
            self.log_append("fail", line)
        elif _FOUND.search(line):
            self.log_append("info", line)
        else:
            self.log_append("info", line)
        if self.state == "IMPORTING":
            self._update_status()

    def _on_cancel(self) -> None:
        if self.state != "IMPORTING" or self._cancel_event is None:
            return
        self._cancel_event.set()
        self._set_state("CANCELING")
        self.log_append("info", "已请求取消，正在终止 winget 进程…")

    def _on_import_done(self, rc: int, error: str) -> None:
        self.progress.stop()
        c = self._counts
        if error:
            self._set_state("READY")
            self.log_append("error", f"导入中断：{error}")
            self.status_lbl.setText("导入中断")
            QMessageBox.critical(self, APP_TITLE, f"导入中断：{error}")
            return
        summary = (f"导入完成：成功 {c['success']}、跳过 {c['skipped']}、失败 {c['failed']}"
                   if rc == 0 else
                   f"winget 退出码 {rc}：成功 {c['success']}、跳过 {c['skipped']}、失败 {c['failed']}")
        self.log_append("done", summary)
        self._import_base = ""
        self._set_state("READY")
        self.status_lbl.setText(summary)
        QTimer.singleShot(600, self._start_scan)

    # ── 勾选持久化 ──
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

    # ── 关窗 ──
    def closeEvent(self, e) -> None:  # noqa: N802
        if self.state in ("IMPORTING", "CANCELING"):
            ok = QMessageBox.question(self, APP_TITLE, "导入尚未完成，确定取消并关闭吗？",
                                      QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
            if ok != QMessageBox.Yes:
                e.ignore()
                return
            if self._cancel_event:
                self._cancel_event.set()
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
