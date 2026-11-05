"""Winget 软件管家 · 桌面版入口：主窗口与后台扫描。

界面组件在 qt_ui，主题与工具在 qt_common；本文件只保留
ScanWorker（后台扫描）与 MainWindow（窗口装配与交互）。
"""
from __future__ import annotations

import json
import logging
import logging.handlers
import sys
import threading
import time
from datetime import datetime
from pathlib import Path

if sys.platform == "win32":
    import ctypes.wintypes as _wt
    _WIN_MSG = _wt.MSG           # nativeEvent 里解析 Windows 消息用
else:
    _WIN_MSG = None

from PySide6.QtCore import (QEvent, QPoint, Qt, QThread, QTimer, QtMsgType, Signal,
                            qInstallMessageHandler)
from PySide6.QtGui import (QFont, QKeySequence, QShortcut, QTextCharFormat,
                           QTextCursor)
from PySide6.QtNetwork import QLocalServer, QLocalSocket
from PySide6.QtWidgets import (QApplication, QFileDialog, QFrame, QGraphicsOpacityEffect,
                               QHBoxLayout, QLabel, QLineEdit, QMainWindow, QMenu,
                               QMessageBox, QPushButton, QScrollArea, QSizePolicy,
                               QStackedWidget, QTextEdit, QWidget)

from qt_common import *  # noqa: F401,F403
from qt_common import (_brand_arrow_path, _glow, _grad, _hl_html,  # noqa: F401
                       _pick_family, _shadow, app_icon,
                       default_export_path, search_icon,
                       set_font_family, settings)
from qt_ui import *  # noqa: F401,F403
from qt_ui import AboutDialog, ClickableLabel, ExportDialog, GradientLabel, PackageRow  # noqa: F401
from core.config import load_config  # noqa: E402
from core.errors import ScanCancelled  # noqa: E402
from core.fsio import atomic_write_json  # noqa: E402
from services.profiles import build_winget_manifest  # noqa: E402
from services.winget_cli import WingetRepository  # noqa: E402

class ScanWorker(QThread):
    """后台扫描。先探 winget 是否可用，再取目录。

    取消是「放弃等待」语义：winget 子进程无法安全中断，取消后把结果丢弃，
    界面立即恢复可用，后台线程自行结束（QThread 不允许强杀）。

    取消标志用 `threading.Event` 而不是 bool：bool 的读写跨线程没有可见性保证，
    且 Event 的 set/is_set 天然是原子的。
    """

    ok = Signal(object)
    err = Signal(str)
    missing = Signal(str)      # winget 不存在 / 不可执行

    def __init__(self, catalog: CatalogService, repo: WingetRepository | None = None) -> None:
        super().__init__()
        self._catalog = catalog
        self._repo = repo
        self._cancel = threading.Event()

    def request_cancel(self) -> None:
        self._cancel.set()

    def run(self) -> None:
        try:
            ver = ""
            if self._repo is not None and not self._cancel.is_set():
                # 前置探测：winget 不在时直接给引导，而不是丢一条晦涩的错误
                try:
                    ver = self._repo.version(cancel=self._cancel)
                except ScanCancelled:
                    return             # 用户取消，静默退出（不是错误）
                except Exception as exc:  # noqa: BLE001
                    self.missing.emit(str(exc) or "无法启动 winget 进程")
                    return
                if not ver or ver == "unknown":
                    self.missing.emit("winget --version 无输出（未安装或不在 PATH）")
                    return
            if self._cancel.is_set():
                return
            snap = self._catalog.get_catalog(force=True, cancel=self._cancel,
                                             winget_version=ver)
            if self._cancel.is_set():
                return
            self.ok.emit(snap)
        except ScanCancelled:
            return                     # 必须排在 except Exception 之前，否则会被吞
        except Exception as exc:  # noqa: BLE001
            if not self._cancel.is_set():
                self.err.emit(str(exc))


# ══════════════════════════ 导出对话框 ══════════════════════════
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
        # 所有活着的扫描线程。必须持有强引用：QThread 被 Python 析构而线程还在跑，
        # Qt 会直接 qFatal 掉进程（exit 127，没有 traceback）。取消/关闭时也只是
        # 「放弃结果」，线程本身归这个集合管到 finished 为止。
        self._workers: set = set()
        self._scan_t0 = 0.0
        self._maximized = False
        self._normal_geo = None
        self._converting = False
        self._min_from_max = False
        self._bulk: list = []
        self._log_expanded = True
        self._log_anims: list = []
        self._narrow = False
        self.only_selected = False       # 「仅看已选」筛选，仅本次会话生效
        self._row_order: list = []
        self._restore_cat = settings().value("view/category", "", type=str) or None

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
        # 940×600：125% 缩放的 1366×768 笔记本逻辑分辨率约 1093×614，
        # 下限必须低于它，否则窗口底部（导出按钮所在栏）会被切掉。
        self.setMinimumSize(940, 600)

        self._build_ui()
        self._load_selection()
        self._set_state("BOOT")
        self._restore_window_state()
        self._strip_native_border()
        self._fade_in()
        self._bind_shortcuts()
        self._central.installEventFilter(self)
        QTimer.singleShot(80, self._start_scan)

    def _bind_shortcuts(self) -> None:
        keys = {
            "Ctrl+F": self._focus_search,
            "Ctrl+A": self._select_all_shortcut,
            "Ctrl+Shift+A": lambda: self._set_all(False),
            "Ctrl+I": self._invert,
            "Ctrl+E": self._on_export,
            "F5": self._on_f5,
            "Escape": self._clear_filters,
        }
        for seq, slot in keys.items():
            QShortcut(QKeySequence(seq), self).activated.connect(slot)

    def _on_f5(self) -> None:
        """F5 是「刷新」，不是「中断」。扫描中按 F5 最可能是误触，
        悄悄取消会让人以为扫描崩了——这里只提示，真正取消要用户自己点按钮。"""
        if self.state == "SCANNING":
            self._flash("扫描进行中，请点「取消扫描」中断")
            return
        self._start_scan()

    def _select_all_shortcut(self) -> None:
        # 焦点在搜索框时 Ctrl+A 应该选中输入的文字，而不是全选软件
        if self.search.hasFocus():
            self.search.selectAll()
            return
        self._set_all(True)

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
            f"#GlassBar {{ background: {GLASS};"
            f"border: 1px solid {GLASS_BORDER}; border-radius: 16px; }}")
        _shadow(bar, blur=26, dy=8, alpha=24)
        bw.addWidget(bar)
        bl = QHBoxLayout(bar)
        bl.setContentsMargins(11, 11, 11, 11)
        bl.setSpacing(9)

        self.search = QLineEdit(bar)
        self.search.setPlaceholderText("搜索名称或包 ID（空格分隔多个词）")
        self.search.setFixedHeight(38)
        self.search.setMinimumWidth(200)
        self.search.addAction(search_icon(), QLineEdit.LeadingPosition)
        self.search.setClearButtonEnabled(True)
        self.search.setStyleSheet(f"""
            QLineEdit {{
                border: 1px solid {LINE}; border-radius: 10px; padding: 0 10px;
                background: {GLASS_INPUT}; font-size: 9.5pt; color: {TEXT};
                selection-background-color: {ACCENT_SOFT};
            }}
            QLineEdit:focus {{ border: 1px solid {ACCENT}; background: {SURFACE}; }}
        """)
        self.search.textChanged.connect(lambda *_: self._search_timer.start())
        self.search.installEventFilter(self)
        bl.addWidget(self.search, 1)

        self.scan_btn = self._btn(bar, "重新扫描", "outline", self._start_scan)
        self.scan_btn.setMinimumWidth(96)
        bl.addWidget(self.scan_btn)

        # 「仅看已选」：导出前复核勾选项，避免在几十行里找蓝色勾
        self.only_btn = QPushButton("仅看已选", bar)
        self.only_btn.setCheckable(True)
        self.only_btn.setFixedHeight(34)
        self.only_btn.setCursor(Qt.PointingHandCursor)
        self.only_btn.setFocusPolicy(Qt.NoFocus)
        self.only_btn.setToolTip("列表只显示已勾选的软件，方便导出前核对")
        self.only_btn.setStyleSheet(
            f"QPushButton{{border:1px solid {LINE}; border-radius:9px; padding:0 12px;"
            f"background:{SURFACE}; color:{MUTED}; font-size:8.8pt;}}"
            f"QPushButton:hover{{border-color:{BORDER_HOVER}; color:{TEXT};}}"
            f"QPushButton:checked{{border-color:{ACCENT_LINE}; background:{ACCENT_SOFT};"
            f"color:{ACCENT_DEEP}; font-weight:600;}}")
        self.only_btn.toggled.connect(self._on_only_selected)
        bl.addWidget(self.only_btn)

        for text, slot in (("全选", lambda: self._set_all(True)),
                           ("全不选", lambda: self._set_all(False)),
                           ("反选", self._invert)):
            b = self._btn(bar, text, "soft", slot, height=34)
            bl.addWidget(b)
            self._bulk.append(b)
        root.addWidget(bar_wrap)

        # 分组胶囊（全部 / winget 可装 / winget 不支持）
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
        # 组名较长时允许横向滚动（此前被裁剪不可见）
        self.chip_scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        self.chip_scroll.setStyleSheet("""
            QScrollArea { background: transparent; border: none; }
            QScrollBar:horizontal { background: transparent; height: 5px; margin: 0 10px; }
            QScrollBar::handle:horizontal { background: __SC__; border-radius: 2px;
                                            min-width: 48px; }
            QScrollBar::handle:horizontal:hover { background: __SCH__; }
            QScrollBar::add-line, QScrollBar::sub-line { width: 0; height: 0; }
            QScrollBar::add-page, QScrollBar::sub-page { background: none; }
        """.replace("__SC__", SCROLL).replace("__SCH__", SCROLL_HI))
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
                           f"border-radius: 16px; }}")
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
            QScrollBar::handle:vertical { background: __SC__; border-radius: 5px;
                                          min-height: 44px; margin: 0 2px; }
            QScrollBar::handle:vertical:hover { background: __SCH__; }
            QScrollBar::add-line, QScrollBar::sub-line { height: 0; }
            QScrollBar::add-page, QScrollBar::sub-page { background: none; }
        """.replace("__SC__", SCROLL).replace("__SCH__", SCROLL_HI))
        self.rows_host = QWidget()
        self.rows_host.setStyleSheet("background: transparent;")
        self.rows_lay = QVBoxLayout(self.rows_host)
        self.rows_lay.setContentsMargins(0, 0, 0, 8)
        self.rows_lay.setSpacing(0)
        self.rows_lay.addStretch(1)
        self.scroll.setWidget(self.rows_host)
        # 吸顶分组头：挂在 viewport 上，滚动时浮在顶部
        self.sticky = StickyHeader(self.scroll.viewport())
        self.sticky.jump.connect(self._scroll_to_section)
        self.scroll.verticalScrollBar().valueChanged.connect(self._update_sticky)
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
        self.log_grip = LogGrip(self)
        lw.addWidget(self.log_grip)
        self.log_card = QFrame(log_wrap)
        self.log_card.setObjectName("LogCard")
        self._log_height = int(settings().value("view/logHeight", 150, type=int) or 150)
        self._log_height = max(90, min(420, self._log_height))
        self.log_card.setMinimumHeight(self._log_height)
        self.log_card.setMaximumHeight(self._log_height)
        self.log_card.setStyleSheet(f"#LogCard {{ background: {LOG_BG};"
                                    f" border: 1px solid {LINE}; border-radius: 12px; }}")
        _shadow(self.log_card, blur=20, dy=5, alpha=22)
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
        dot.setStyleSheet(f"background:{ACCENT}; border-radius:3px;")
        lhl.addWidget(dot)
        lhl.addSpacing(8)
        lt = QLabel("运行日志", lhead)
        lt.setStyleSheet(f"color:{LOG_HEAD}; font-size:8.8pt; font-weight:600;"
                         "background:transparent;")
        lhl.addWidget(lt)
        lhl.addStretch(1)
        self.log_toggle = ClickableLabel("收起", lhead)
        self.log_toggle.setCursor(Qt.PointingHandCursor)
        self.log_toggle.setStyleSheet(f"color:{LOG_ACT}; font-size:8.5pt; background:transparent;")
        lhl.addWidget(self.log_toggle)
        lhl.addSpacing(12)
        self.log_copy = ClickableLabel("复制", lhead)
        self.log_copy.setCursor(Qt.PointingHandCursor)
        self.log_copy.setToolTip("复制全部日志到剪贴板，方便贴给别人排查")
        self.log_copy.setStyleSheet(f"color:{LOG_ACT}; font-size:8.5pt; background:transparent;")
        lhl.addWidget(self.log_copy)
        lhl.addSpacing(12)
        self.log_save = ClickableLabel("保存…", lhead)
        self.log_save.setCursor(Qt.PointingHandCursor)
        self.log_save.setToolTip("把日志另存为文本文件")
        self.log_save.setStyleSheet(f"color:{LOG_ACT}; font-size:8.5pt; background:transparent;")
        lhl.addWidget(self.log_save)
        lhl.addSpacing(12)
        self.log_clear = ClickableLabel("清空", lhead)
        self.log_clear.setCursor(Qt.PointingHandCursor)
        self.log_clear.setStyleSheet(f"color:{LOG_ACT}; font-size:8.5pt; background:transparent;")
        lhl.addWidget(self.log_clear)
        lc.addWidget(lhead)
        self.log = QTextEdit(self.log_card)
        self.log.setReadOnly(True)
        self.log.setFrameShape(QFrame.NoFrame)
        self.log.setStyleSheet(f"""
            QTextEdit {{ background: transparent; color: {LOG_FG}; border: none;
                         padding: 2px 14px 8px 14px;
                         font-family: '{MONO_FAMILY}'; font-size: 8.8pt;
                         selection-background-color: {LOG_SEL};
                         selection-color: {TEXT}; }}
            QScrollBar:vertical {{ background: transparent; width: 8px; }}
            QScrollBar::handle:vertical {{ background: {LOG_SCROLL}; border-radius: 4px;
                                           min-height: 30px; }}
            QScrollBar::handle:vertical:hover {{ background: {SCROLL_HI}; }}
            QScrollBar::add-line, QScrollBar::sub-line {{ height: 0; }}
        """)
        self.log.setMinimumHeight(0)
        self.log.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Ignored)
        lc.addWidget(self.log, 1)
        lw.addWidget(self.log_card)
        self.log_clear.clicked.connect(self.log.clear)
        self.log_toggle.clicked.connect(self._toggle_log)
        self.log_copy.clicked.connect(self._copy_log)
        self.log_save.clicked.connect(self._save_log)
        root.addWidget(log_wrap)

        # 底部玻璃栏
        bottom = QFrame(self.shell)
        bottom.setObjectName("BottomBar")
        bottom.setFixedHeight(58)
        bottom.setStyleSheet(f"#BottomBar {{ background: {GLASS_2};"
                             f"border-top: 1px solid {GLASS_BORDER}; }}")
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
        # 定宽 + 左右弹簧：wordWrap 的 QLabel 只有宽度确定时 heightForWidth 才准，
        # 直接用 AlignHCenter 加入布局会按 sizeHint 给高度，两行文案会叠在一起。
        self.empty_lbl = QLabel("", page)
        self.empty_lbl.setAlignment(Qt.AlignCenter)
        self.empty_lbl.setWordWrap(True)
        self.empty_lbl.setFixedWidth(460)
        self.empty_lbl.setMinimumHeight(24)
        self.empty_lbl.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Minimum)
        self.empty_lbl.setStyleSheet(f"color:{MUTED}; font-size:9.5pt;"
                                     " background:transparent; line-height:150%;")
        row = QHBoxLayout()
        row.setContentsMargins(0, 0, 0, 0)
        row.addStretch(1)
        row.addWidget(self.empty_lbl)
        row.addStretch(1)
        lay.addLayout(row)
        lay.addSpacing(16)
        self.empty_clear = QPushButton("清除筛选", page)
        self.empty_clear.setCursor(Qt.PointingHandCursor)
        self.empty_clear.setFixedHeight(36)
        self.empty_clear.setStyleSheet(
            f"QPushButton{{border:1px solid {BORDER_HOVER}; border-radius:18px;"
            f" padding:0 22px; color:{ACCENT_DEEP}; background:{SURFACE};"
            " font-size:9pt; font-weight:500;}"
            f"QPushButton:hover{{background:{SURFACE_3}; border-color:{BORDER_HOVER};}}")
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
            av.setStyleSheet(f"background:{SKEL_1}; border-radius:11px;")
            h.addWidget(av, 0, Qt.AlignVCenter)
            col = QVBoxLayout()
            col.setSpacing(8)
            b1 = QLabel(row)
            b1.setFixedSize(230 + (i % 3) * 40, 11)
            b1.setStyleSheet(f"background:{SKEL_1}; border-radius:5px;")
            b2 = QLabel(row)
            b2.setFixedSize(140 + (i % 2) * 30, 9)
            b2.setStyleSheet(f"background:{SKEL_2}; border-radius:4px;")
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
        if self.only_selected:
            self.only_selected = False
            self.only_btn.blockSignals(True)
            self.only_btn.setChecked(False)
            self.only_btn.blockSignals(False)
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
                f"QPushButton:disabled{{background:{ROW_ALT}; color:{MUTED2};}}")
            _glow(b, blur=18, dy=4, alpha=85)
        elif kind == "outline":
            b.setStyleSheet(
                f"QPushButton{{border:1px solid {LINE}; border-radius:10px; padding:0 15px;"
                f"background:{SURFACE}; color:{TEXT}; font-size:9.3pt;}}"
                f"QPushButton:hover{{background:{SURFACE_3}; border-color:{BORDER_HOVER};}}"
                f"QPushButton:disabled{{color:{MUTED3}; background:{SURFACE_2};}}")
        elif kind == "soft":
            b.setStyleSheet(
                f"QPushButton{{border:1px solid {ACCENT_LINE}; border-radius:9px; padding:0 12px;"
                f"background:{ACCENT_SOFT}; color:{ACCENT_DEEP}; font-size:8.8pt; font-weight:500;}}"
                f"QPushButton:hover{{background:{ACCENT_SOFT}; border-color:{ACCENT_LINE};}}"
                f"QPushButton:disabled{{background:{SURFACE_3}; color:{MUTED3};"
                f" border-color:{LINE_SOFT};}}")
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

    # ── 筛选 ──
    def _on_only_selected(self, on: bool) -> None:
        self.only_selected = on
        self._rebuild_list(keep_scroll=not on)
        self._update_status()

    def _search_tokens(self) -> list:
        """把搜索框拆成 (字段, 值) 列表：空格分隔多词，天然是 AND 关系。

        支持 `id:` 只匹配包 ID、`name:` 只匹配名称，其余按「名称 + 包 ID」模糊匹配。
        """
        tokens: list = []
        for raw in self.search.text().strip().lower().split():
            if raw.startswith("id:") and len(raw) > 3:
                tokens.append(("id", raw[3:]))
            elif raw.startswith("name:") and len(raw) > 5:
                tokens.append(("name", raw[5:]))
            elif raw:
                tokens.append(("any", raw))
        return tokens

    @staticmethod
    def _pkg_matches(pkg, tokens: list) -> bool:
        name = pkg.name.lower()
        pid = pkg.packageIdentifier.lower()
        for field, val in tokens:
            if field == "id":
                if val not in pid:
                    return False
            elif field == "name":
                if val not in name:
                    return False
            elif val not in f"{name} {pid}":
                return False
        return True

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
        if self._maximized:
            self._manual_restore()
        else:
            self._manual_maximize()

    def _strip_native_border(self) -> None:
        """去掉 Win11 DWM 给顶层窗口画的 1px 边框。

        这个边框贴在窗口矩形外沿，普通态藏在阴影里看不出，
        最大化铺满屏幕后就显形成一圈外框。DWMWA_BORDER_COLOR
        置为 DWMWA_COLOR_NONE 即可；Win10 不支持该属性，静默忽略。
        """
        if sys.platform != "win32":
            return
        try:
            import ctypes
            color = ctypes.c_uint32(0xFFFFFFFE)      # DWMWA_COLOR_NONE
            ctypes.windll.dwmapi.DwmSetWindowAttribute(
                int(self.winId()), 34, ctypes.byref(color), ctypes.sizeof(color))
        except Exception:  # noqa: BLE001
            pass

    def changeEvent(self, e) -> None:  # noqa: N802
        super().changeEvent(e)
        if e.type() != QEvent.Type.WindowStateChange:
            return
        self._strip_native_border()   # DWM 在状态切换后可能重画边框，补一刀
        if self._converting:
            return
        if self.isMinimized():
            # 记住最小化前的形态，还原时据此恢复
            self._min_from_max = self._maximized
            return
        if self.isMaximized():
            # 系统路径的最大化（Win+↑ / Aero Snap / 任务栏最大化）：系统会把
            # 无边框窗钳制在工作区且渲染内容内缩 1px，统一转成手动最大化。
            # Qt 对最大化的几何处理是异步收尾的，立即 setGeometry 会被覆盖，
            # 所以先退出系统状态、置标志，再用定时器重申几何。
            self._converting = True
            self.setWindowState(Qt.WindowNoState)
            self._converting = False
            self._maximized = True
            for delay in (0, 150, 400, 800):
                QTimer.singleShot(delay, self._reassert_max)
        elif self._maximized and self._min_from_max:
            # 从最小化还原：保持手动最大化，重新外扩一次防几何被重置
            self._min_from_max = False
            for delay in (0, 150, 400):
                QTimer.singleShot(delay, self._reassert_max)
        elif self._maximized:
            # 用户取消最大化（Win+↓ / 还原按钮）
            self._manual_restore()

    def _reassert_max(self) -> None:
        if self._maximized and not self.isMinimized():
            self._manual_maximize()

    def _apply_max_look(self, maxed: bool) -> None:
        lay = self.centralWidget().layout()
        sh = getattr(self, "_shell_shadow", None)
        if maxed:
            lay.setContentsMargins(0, 0, 0, 0)
            self.shell.set_radius(0)
            if sh is not None:
                sh.setEnabled(False)
            self.titlebar.btns["max"].setText("❐")
        else:
            lay.setContentsMargins(16, 14, 16, 16)
            self.shell.set_radius(16)
            if sh is not None:
                sh.setEnabled(True)
            self.titlebar.btns["max"].setText("□")

    def _manual_maximize(self) -> None:
        if self.isMaximized():
            self._converting = True
            self.setWindowState(Qt.WindowNoState)
            self._converting = False
        if self._normal_geo is None:
            self._normal_geo = self.geometry()
        self._maximized = True
        self._min_from_max = False
        self._apply_max_look(maxed=True)
        # 外扩 1 逻辑px：盖住无边框窗最大化时渲染内容内缩的 1px 壁纸缝
        av = self.screen().availableGeometry()
        self.setGeometry(av.x() - 1, av.y() - 1, av.width() + 2, av.height() + 2)

    def _manual_restore(self) -> None:
        self._maximized = False
        self._min_from_max = False
        self._apply_max_look(maxed=False)
        geo = self._normal_geo
        self._normal_geo = None
        if self.isMaximized():
            self._converting = True
            self.setWindowState(Qt.WindowNoState)
            self._converting = False
        if geo is not None:
            self.setGeometry(geo)

    def _track_normal_geo(self) -> None:
        """持续记录普通态几何，供最大化外扩后还原使用。

        手动最大化期间（_maximized=True）与系统最大化/最小化中不记录，
        避免把整屏矩形当成还原位置。
        """
        if (self.isVisible() and self._maximized is False
                and not self.isMaximized() and not self.isMinimized()):
            self._normal_geo = self.geometry()

    def nativeEvent(self, etype, message):  # noqa: N802
        # 拦掉 WM_NCCALCSIZE：客户区占满整个窗口，原生边框在最小化/还原后
        # 也不会再被 Windows 画回来（Qt 无边框窗体的已知怪癖）。
        if _WIN_MSG is not None and etype == b"windows_generic_MSG":
            try:
                msg = _WIN_MSG.from_address(int(message))
                if msg.message == 0x0083 and msg.wParam:   # WM_NCCALCSIZE
                    return True, 0
            except Exception:  # noqa: BLE001
                pass
        return super().nativeEvent(etype, message)

    def _set_log_height(self, h: int) -> None:
        """拖拽调高日志面板：90~420，落盘记住。"""
        h = max(90, min(420, int(h)))
        self._log_height = h
        self.log_card.setMinimumHeight(h)
        self.log_card.setMaximumHeight(h)
        settings().setValue("view/logHeight", h)

    def _toggle_log(self) -> None:
        self._log_expanded = not self._log_expanded
        self.log_toggle.setText("收起" if self._log_expanded else "展开")
        target = getattr(self, "_log_height", 150) if self._log_expanded else 34
        self.log.setVisible(self._log_expanded)
        # 反复折叠会不断 new 动画；已跑完的必须先扔掉，否则列表只增不减
        self._log_anims = [a for a in self._log_anims
                           if a.state() == QPropertyAnimation.Running]
        for prop in (b"minimumHeight", b"maximumHeight"):
            a = QPropertyAnimation(self.log_card, prop, self)
            a.setDuration(200)
            a.setEasingCurve(QEasingCurve.InOutCubic)
            a.setStartValue(self.log_card.height())
            a.setEndValue(target)
            a.start()
            self._log_anims.append(a)

    def _fade_in(self, dur: int = 260, start: float = 0.0) -> None:
        eff = QGraphicsOpacityEffect(self._central)
        self._central.setGraphicsEffect(eff)
        anim = QPropertyAnimation(eff, b"opacity", self)
        anim.setDuration(dur)
        anim.setStartValue(start)
        anim.setEndValue(1.0)
        anim.setEasingCurve(QEasingCurve.OutCubic)
        anim.finished.connect(lambda: self._central.setGraphicsEffect(None))
        anim.start()
        self._fade_anim = anim

    # ── 状态机 ──
    def _set_state(self, state: str) -> None:
        self.state = state
        s = state
        # 骨架屏的无限脉冲动画只在扫描态跑；离开就停，重新扫描再启
        skel = getattr(self, "_skel_anim", None)
        if skel is not None:
            if s == "SCANNING":
                skel.start()
            else:
                skel.stop()
        # 扫描中同一个按钮变成「取消扫描」，避免再多占一个按钮位
        self.scan_btn.setText("取消扫描" if s == "SCANNING" else "重新扫描")
        self.scan_btn.setEnabled(s != "BOOT")
        for b in self._bulk:
            b.setEnabled(s == "READY")
        self._update_status()
        self._update_empty()

    # ── 扫描 ──
    def _start_scan(self) -> None:
        if self.state == "SCANNING":
            self._cancel_scan()
            return
        if self._workers:
            # 上一轮已取消但线程还在收尾：再起一轮会让两份结果抢同一个 _scan_worker
            self._flash("上一轮扫描仍在收尾，请稍候")
            return
        self._set_state("SCANNING")
        self.progress.start()
        self._scan_t0 = time.perf_counter()
        self._scan_timer.start()
        self.log_append("info", "开始扫描已安装软件…")
        w = ScanWorker(self.catalog, self.repo)
        self._workers.add(w)
        self._scan_worker = w
        w.ok.connect(self._on_scan_ok)
        w.err.connect(self._on_scan_err)
        w.missing.connect(self._on_scan_missing)
        # 这里连「绑定方法」而不是 lambda：绑定方法带 QObject 接收者，Qt 才会把
        # 回调排到主线程执行（lambda 没有接收者，会在工作线程里直接跑，改界面控件
        # 就是未定义行为）。sender() 在槽里拿到的正是这个 worker。
        w.finished.connect(self._on_worker_finished)
        w.start()

    def _on_worker_finished(self, w=None) -> None:
        """线程收尾：放掉引用、允许 deleteLater，然后按状态兜底。

        信号顺序假设：`ok` / `err` / `missing` 都在 `run()` 里先于 return 发出，
        而 QThread 的 `finished` 在 `run()` 返回之后才发，所以本方法一定晚于
        `_on_scan_*`；`_scan_worker` 此时不会被提前置空，刚拿到的结果不会被误丢。
        """
        w = w or self.sender()
        if w is None:
            return
        self._workers.discard(w)
        if self._scan_worker is w:
            self._scan_worker = None
        w.deleteLater()
        if self.state == "SCANNING" and not self._workers:
            # 线程异常退出且一个信号都没发（比如 run() 里抛了非 Exception），
            # 别把界面永远留在扫描态
            self._set_state("SCAN_FAILED")
        self.scan_btn.setEnabled(True)

    def _cancel_scan(self) -> None:
        w = self._scan_worker
        if w is not None:
            w.request_cancel()
        # 只解绑「当前结果」：生命周期交给 _workers，线程活着就没人能析构它
        self._scan_worker = None
        self._scan_timer.stop()
        self.progress.stop()
        self.log_append("skip", "已取消扫描，后台 winget 进程会在数秒内退出，本次结果不再采用。")
        self._set_state("READY" if self.packages else "CANCELLED")
        self._flash("已取消扫描 · 稍候可重新扫描")
        # 等 _on_worker_finished 再放开，避免上一轮还没退出就叠一轮新的
        self.scan_btn.setEnabled(False)

    def _tick_scan(self) -> None:
        elapsed = time.perf_counter() - self._scan_t0
        self.status_lbl.setText(f"正在扫描 · 已用 {elapsed:.1f}s · 点「取消扫描」可中断等待")

    def _on_scan_ok(self, snapshot) -> None:
        if self.sender() is not self._scan_worker:
            return                     # 过期结果：已被取消或被新一轮扫描取代
        self._scan_timer.stop()
        self.progress.stop()
        self.snapshot = snapshot
        self.packages = list(snapshot.packages)
        n_re = sum(1 for p in self.packages if p.reinstallable)
        self.log_append("info",
                        f"扫描完成：共 {len(self.packages)} 个软件，其中可重装 {n_re} 个；"
                        f"另有 {len(self.packages) - n_re} 项 winget 装不上，"
                        f"统一归入「{UNSUPPORTED_GROUP}」分组"
                        f"（灰显、不可勾选），排在各分组之后。")
        if getattr(snapshot, "exportError", ""):
            # 可重装清单拿不到时所有包都会被判成「不可重装」，必须让用户看见原因
            self.log_append("fail", f"无法获取可重装清单：{snapshot.exportError}")
            self.log_append("skip",
                            "可稍后重新扫描；本次所有软件按不可自动重装处理（勾选与导出会受影响）。")
        valid = {p.packageIdentifier.lower() for p in self.packages if p.reinstallable}
        for k in [k for k in self.checked if k not in valid]:
            del self.checked[k]
        # 重扫后勾选若已被清空，「仅看已选」会得到空列表，自动关掉免得像出了故障
        if self.only_selected and not self.checked:
            self.only_selected = False
            self.only_btn.blockSignals(True)
            self.only_btn.setChecked(False)
            self.only_btn.blockSignals(False)
        # 首次扫描后恢复上次的分组筛选（该分组已不存在时回落到全部）
        cat = self._restore_cat
        self._restore_cat = None
        if cat and any(self._group_of(p) == cat for p in self.packages):
            self.active_cat = cat
            self.log_append("info", f"已恢复上次的分组筛选「{cat}」")
        else:
            self.active_cat = None
        self._rebuild_chips()
        self._rebuild_list(keep_scroll=True)   # 重扫后保持滚动位置
        self._set_state("READY")
        self._save_selection()

    def _on_scan_err(self, msg: str) -> None:
        if self.sender() is not self._scan_worker:
            return
        self._scan_timer.stop()
        self.progress.stop()
        self._set_state("SCAN_FAILED")
        self.status_lbl.setText("扫描失败")
        self.log_append("error", f"扫描失败：{msg}")

    def _on_scan_missing(self, detail: str) -> None:
        """winget 不可用：不做成一条日志了事，给出可操作的引导。"""
        if self.sender() is not self._scan_worker:
            return
        self._scan_timer.stop()
        self.progress.stop()
        self._set_state("NO_WINGET")
        self.status_lbl.setText("未检测到 winget，无法扫描已安装软件")
        self.log_append("error",
                        f"winget 不可用：{(detail or '命令不存在').splitlines()[0][:160]}")
        self._show_winget_guide(detail)

    def _show_winget_guide(self, detail: str) -> None:
        box = QMessageBox(self)
        box.setWindowTitle("未检测到 winget")
        box.setIcon(QMessageBox.Warning)
        box.setText("本工具依赖 Windows 包管理器 winget，当前无法调用。")
        box.setInformativeText(
            "winget 由微软「应用安装程序 / App Installer」提供。\n"
            "Windows 10 1809 及以上可在 Microsoft Store 安装，"
            "或从 GitHub 下载 winget-cli 的 msixbundle 手动安装。\n\n"
            f"系统返回：{(detail or '命令不存在').splitlines()[0][:140]}")
        store = box.addButton("打开应用商店", QMessageBox.AcceptRole)
        dl = box.addButton("复制下载地址", QMessageBox.ActionRole)
        box.addButton("知道了", QMessageBox.RejectRole)
        box.exec()
        if box.clickedButton() is store:
            try:
                os.startfile("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1")  # noqa: S606
            except Exception:  # noqa: BLE001
                pass
        elif box.clickedButton() is dl:
            QApplication.clipboard().setText("https://github.com/microsoft/winget-cli/releases")
            self._flash("下载地址已复制到剪贴板")

    # ── 分组胶囊 ──
    def _rebuild_chips(self) -> None:
        while self.chips_lay.count() > 1:
            it = self.chips_lay.takeAt(0)
            w = it.widget()
            if w is not None:
                w.setParent(None)
                w.deleteLater()
        counts: dict = {}
        for p in self.packages:
            g = self._group_of(p)
            counts[g] = counts.get(g, 0) + 1
        items = [("全部", len(self.packages), None)]
        for group_name in sorted(counts, key=lambda c: (c == UNSUPPORTED_GROUP, c)):
            items.append((group_name, counts[group_name], group_name))
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
                    background: {SURFACE}; color: {MUTED}; font-size: 8.8pt;
                }}
                QPushButton:hover {{ border-color: {BORDER_HOVER}; color: {TEXT}; }}
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
    def _group_of(self, pkg) -> str:
        """分组归属：只看能不能被 winget 重装，「winget 不支持」固定沉底。"""
        return REINSTALLABLE_GROUP if pkg.reinstallable else UNSUPPORTED_GROUP

    def _section_members(self, title: str) -> list:
        return [r for r in self.rows.values() if self._group_of(r.pkg) == title]

    # ── 吸顶分组头 ──
    def _update_sticky(self, *_args) -> None:
        """按滚动位置算出当前分组，决定吸顶头内容与位置（带下一组推入效果）。"""
        if not hasattr(self, "sticky"):
            return
        if not self.sections or self.stack.currentIndex() != 0:
            self.sticky.hide()
            return
        top = self.scroll.verticalScrollBar().value()
        # 第一个「组头上沿尚未滚出视口」的分组，它是推入者
        nxt_i = None
        for i, sec in enumerate(self.sections):
            if sec.mapTo(self.rows_host, QPoint(0, 0)).y() >= top:
                nxt_i = i
                break
        cur_i = (nxt_i - 1) if nxt_i is not None else len(self.sections) - 1
        if cur_i < 0:                    # 还没滚过第一组头，用真头即可
            self.sticky.hide()
            return
        cur = self.sections[cur_i]
        rows = self._section_members(cur.title)
        sel = sum(1 for r in rows if r._checked)
        self.sticky.set_section(cur.title, sel, len(rows))
        h = self.sticky.BAR_H
        y = 0
        if nxt_i is not None:
            ny = self.sections[nxt_i].mapTo(self.rows_host, QPoint(0, 0)).y() - top
            if ny < h:
                y = ny - h               # 被下一组顶上去
        self.sticky.setGeometry(0, y, self.scroll.viewport().width(),
                                self.sticky.height())
        self.sticky.raise_()
        self.sticky.show()

    def _scroll_to_section(self, title: str) -> None:
        """点吸顶头 → 回到该分组开头。"""
        for sec in self.sections:
            if sec.title == title:
                self.scroll.verticalScrollBar().setValue(
                    max(0, sec.mapTo(self.rows_host, QPoint(0, 0)).y()))
                return

    def _clear_rows(self) -> None:
        for sec in self.sections:
            self.expanded[sec.title] = sec.expanded
        self.sections.clear()
        self.rows.clear()
        if hasattr(self, "sticky"):
            self.sticky.hide()
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
        tokens = self._search_tokens()
        words = [v for _f, v in tokens]
        groups: dict = {}
        for pkg in self.packages:
            group = self._group_of(pkg)
            if self.active_cat and group != self.active_cat:
                continue
            if tokens and not self._pkg_matches(pkg, tokens):
                continue
            if self.only_selected and not self.checked.get(pkg.packageIdentifier.lower(), False):
                continue
            groups.setdefault(group, []).append(pkg)
        self._matched = sum(len(v) for v in groups.values())

        for group_name in sorted(groups, key=lambda c: (c == UNSUPPORTED_GROUP, c)):
            sec = GroupSection(group_name, self.expanded.get(group_name, True), self.rows_host)
            sec.header.select_all.connect(lambda c=group_name: self._toggle_group(c))
            if group_name == UNSUPPORTED_GROUP:
                sec.header.title.setToolTip(
                    "winget 装不上的软件都在这里：\n"
                    "· 没有可用的安装源（MSIX/UWP、注册表安装、源里已下架等）\n"
                    "· 灰显且不可勾选，仅作核对，不影响导出")
            # 组内先可勾选、后不可重装（灰行统一沉到底部）
            order = sorted(groups[group_name],
                           key=lambda p: (not p.reinstallable, p.name.lower()))
            for pkg in order:
                key = pkg.packageIdentifier.lower()
                row = PackageRow(pkg, self.checked.get(key, False), sec.body)
                row.toggled.connect(self._on_row_toggled)
                row.context_requested.connect(self._show_row_menu)
                row.copy_id.connect(lambda p: self._copy(p.packageIdentifier,
                                                          f"已复制包 ID：{p.packageIdentifier}"))
                row.nav_requested.connect(self._nav_row)
                row.set_highlight(words)
                if self._narrow:
                    row.set_narrow(True)
                sec.add_row(row)
                self.rows[key] = row
                self._row_order.append(row)
            sec.prepare()
            self.sections.append(sec)
            self.rows_lay.insertWidget(self.rows_lay.count() - 1, sec)
            self._update_section_count(group_name)

        self.rows_host.setUpdatesEnabled(True)
        sb.setValue(min(pos, sb.maximum()))
        self._update_status()
        self._update_empty()
        self._update_sticky()

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
        self._track_normal_geo()
        if not hasattr(self, "rows") or not hasattr(self, "search"):
            return
        narrow = self.width() < 1080
        if narrow != self._narrow:
            self._narrow = narrow
            for r in self.rows.values():
                r.set_narrow(narrow)
        self._update_sticky()

    def moveEvent(self, e) -> None:  # noqa: N802
        super().moveEvent(e)
        self._track_normal_geo()

    def _toggle_group(self, group_name: str) -> None:
        rows = self._section_members(group_name)
        if not rows:
            return
        sel_rows = [r for r in rows if r.selectable]
        target = bool(sel_rows) and not all(r._checked for r in sel_rows)
        for r in rows:
            if r.selectable:
                self.checked[r.pkg.packageIdentifier.lower()] = target
            r.set_checked(target)
        self._update_section_count(group_name)
        self._update_status()
        self._save_selection()

    def _sync_header(self, sec, rows) -> None:
        sel = sum(1 for r in rows if r._checked)
        sel_ok = [r for r in rows if r.selectable]
        sec.header.set_count(sel, len(rows))
        # 整组都不可勾选（「winget 不支持」）时不显示「全选本组」，避免点了没反应
        sec.header.sel_all.setVisible(bool(sel_ok))
        if sel_ok:
            sec.header.sel_all.setText("全不选" if sel == len(sel_ok) else "全选本组")

    def _update_section_count(self, group_name: str) -> None:
        for sec in self.sections:
            if sec.title != group_name:
                continue
            rows = self._section_members(group_name)
            self._sync_header(sec, rows)

    def _update_all_counts(self) -> None:
        for sec in self.sections:
            rows = self._section_members(sec.title)
            self._sync_header(sec, rows)

    def _update_empty(self) -> None:
        if not self.packages:
            if self.state in ("BOOT", "SCANNING"):
                self.stack.setCurrentIndex(2)          # 骨架屏加载态
            elif self.state == "NO_WINGET":
                self.empty_lbl.setText("未检测到 winget\n安装「应用安装程序 / App Installer」后"
                                       "点击「重新扫描」")
                self.empty_clear.setVisible(False)
                self.stack.setCurrentIndex(1)
            elif self.state == "CANCELLED":
                self.empty_lbl.setText("扫描已取消\n点击「重新扫描」重新开始")
                self.empty_clear.setVisible(False)
                self.stack.setCurrentIndex(1)
            elif self.state == "SCAN_FAILED":
                self.empty_lbl.setText("扫描失败：winget 未安装或不可用\n可点击「重新扫描」重试")
                self.empty_clear.setVisible(False)
                self.stack.setCurrentIndex(1)
            else:
                self.empty_lbl.setText("未发现可重装的软件")
                self.empty_clear.setVisible(False)
                self.stack.setCurrentIndex(1)
            return
        # 可重装清单没取到 → 整屏都是灰行，用户只会以为「软件都不支持」。
        # 这里给一条能动手的提示，而不是让他对着一屏灰条发愁。
        snap = self.snapshot
        if (self.state == "READY" and snap is not None and getattr(snap, "exportError", "")
                and not any(p.reinstallable for p in self.packages)):
            reason = (snap.exportError.splitlines() or [""])[0][:160]
            self.empty_lbl.setText(
                "没能获取「可重装清单」，所有软件暂时都显示为不可自动重装\n"
                f"原因：{reason}\n"
                "点「重新扫描」重试；仍失败请先确认 winget 能正常执行 winget list")
            self.empty_clear.setVisible(False)
            self.stack.setCurrentIndex(1)
            return
        kw = self.search.text().strip()
        has_filter = bool(kw or self.active_cat or self.only_selected)
        if has_filter and self._matched == 0:
            if self.only_selected and self.active_cat == UNSUPPORTED_GROUP and not kw:
                self.empty_lbl.setText(
                    f"「{UNSUPPORTED_GROUP}」里的软件 winget 都装不上，本来就不可勾选\n"
                    "关闭「仅看已选」即可查看这些条目")
            elif self.only_selected and not kw and not self.active_cat:
                self.empty_lbl.setText("还没有勾选任何软件\n取消「仅看已选」可继续浏览并勾选")
            elif self.only_selected and not kw:
                self.empty_lbl.setText(f"「{self.active_cat}」里没有已勾选的软件")
            elif kw and self.active_cat:
                self.empty_lbl.setText(f"在「{self.active_cat}」中未找到与“{kw}”匹配的软件")
            elif kw:
                self.empty_lbl.setText(f"未找到与“{kw}”匹配的软件\n"
                                       "可尝试搜索软件名或包 ID，多个词用空格分隔")
            else:
                self.empty_lbl.setText("该分组下暂无可重装的软件")
            self.empty_clear.setVisible(True)
            self.stack.setCurrentIndex(1)
            return
        self.stack.setCurrentIndex(0)
        self._update_sticky()

    # ── 勾选 ──
    def _on_row_toggled(self, pkg) -> None:
        key = pkg.packageIdentifier.lower()
        self.checked[key] = not self.checked.get(key, False)
        self._update_section_count(self._group_of(pkg))
        self._update_status()
        self._save_selection()
        # 「仅看已选」下取消勾选，行应立即从列表消失（延迟一拍，避免销毁自身控件）
        if self.only_selected and not self.checked.get(key):
            QTimer.singleShot(0, lambda: self._rebuild_list(keep_scroll=True))

    def _scoped_packages(self) -> list:
        """当前搜索/分组/仅看已选下的可见可勾选包；无任何筛选时返回全部。

        批量操作的语义是「对我正在看的这批生效」：搜了 chrome 再按 Ctrl+A，
        用户期望勾上搜出来的那些，而不是屏幕外的几百个。
        """
        if self.search.text().strip() or self.active_cat or self.only_selected:
            return [r.pkg for r in self.rows.values() if getattr(r, "selectable", False)]
        return [p for p in self.packages if p.reinstallable]

    def _set_all(self, value: bool) -> None:
        if self.state != "READY":
            return
        scope = self._scoped_packages()
        for p in scope:
            self.checked[p.packageIdentifier.lower()] = value
        for row in self.rows.values():
            row.set_checked(self.checked.get(row.pkg.packageIdentifier.lower(), False))
        self._update_all_counts()
        self._update_status()
        self._save_selection()
        # 有筛选时只动当前范围（没在看的勾选保持不动），这就是「当前视图」语义；
        # flash 必须放最后——_update_status 会重写底栏文案
        self._flash(f"已{'全选' if value else '取消选中'}当前 {len(scope)} 项")

    def _invert(self) -> None:
        if self.state != "READY":
            return
        scope = self._scoped_packages()
        for p in scope:
            key = p.packageIdentifier.lower()
            self.checked[key] = not self.checked.get(key, False)
        for key, row in self.rows.items():
            row.set_checked(self.checked.get(key, False))
        self._update_all_counts()
        self._update_status()
        self._save_selection()
        self._flash(f"已反选当前 {len(scope)} 项")

    def selected_packages(self) -> list:
        return [p for p in self.packages if self.checked.get(p.packageIdentifier.lower())]

    # ── 状态 ──
    def _update_status(self) -> None:
        total = len(self.packages)
        n_re = sum(1 for p in self.packages if p.reinstallable)
        n = len(self.selected_packages())
        # 统计卡与副标题一律用内存里的列表算，别去读快照里的 counts——
        # 两者会因「快照里过滤过 / 列表被后续操作改过」而对不上
        self.stat_total.set_value(str(total))
        self.stat_reinstall.set_value(str(n_re))
        self.stat_selected.set_value(str(n))
        self.stat_deps.set_value(str(sum(1 for p in self.packages
                                         if p.isDependency and p.reinstallable)))
        if n:
            self.sel_lbl.setText(str(n))
            self.sel_lbl.setVisible(True)
            self.sel_suffix.setText(f"/ {n_re} 项已勾选")
            self.sel_suffix.setVisible(True)
        else:
            self.sel_lbl.setVisible(False)
            self.sel_suffix.setVisible(False)
        self.export_btn.setEnabled(self.state in ("SCANNING", "SCAN_FAILED", "READY") and n > 0)
        if self.snapshot:
            warn = " · 可重装清单获取失败" if getattr(self.snapshot, "exportError", "") else ""
            self.sub_lbl.setText(
                f"共 {total} 个已安装 · {n_re} 个可重装 · "
                f"{total - n_re} 项 winget 装不上（归入「{UNSUPPORTED_GROUP}」） · "
                f"扫描耗时 {self.snapshot.durationMs / 1000:.1f}s{warn}")
        if self.state == "SCANNING":
            return
        if self.search.text().strip() or self.active_cat or self.only_selected:
            suffix = " · 仅显示已勾选" if self.only_selected else ""
            self.status_lbl.setText(f"筛选出 {self._matched} / 共 {total} 项{suffix}")
        elif self.snapshot:
            self.status_lbl.setText(
                f"{n_re} 个软件可一键重装 · {total - n_re} 项 winget 装不上，"
                f"已归入「{UNSUPPORTED_GROUP}」"
                f" · 勾选后点右下角「导出配置」")

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
            QMenu {{ background:{SURFACE}; border:1px solid {LINE}; border-radius:10px; padding:5px; }}
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
        if pkg.reinstallable:
            menu.addAction(
                "复制安装命令",
                lambda: self._copy(
                    f"winget install --id {pkg.packageIdentifier} -e "
                    "--accept-package-agreements --accept-source-agreements",
                    "已复制安装命令（可直接粘到终端）"))
        menu.addSeparator()
        menu.addAction("勾选 / 取消勾选", row.toggle)
        menu.exec(pos)

    # ── 日志 ──
    def log_append(self, tag: str, text: str) -> None:
        colors = {"ts": LOG_TS, "info": LOG_FG, "success": LOG_OK,
                  "skip": LOG_SKIP, "fail": LOG_FAIL, "error": LOG_FAIL,
                  "done": LOG_DONE}
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

    def _copy_log(self) -> None:
        text = self.log.toPlainText()
        if not text.strip():
            self._flash("日志还是空的")
            return
        QApplication.clipboard().setText(text)
        self._flash(f"已复制 {len(text.splitlines())} 行日志")

    def _save_log(self) -> None:
        text = self.log.toPlainText()
        if not text.strip():
            self._flash("日志还是空的")
            return
        default = str(Path.home() / f"winget-manager-{datetime.now():%Y%m%d-%H%M}.log")
        chosen, _ = QFileDialog.getSaveFileName(
            self, "保存日志", default, "日志文件 (*.log);;文本文件 (*.txt)")
        if not chosen:
            return
        try:
            Path(chosen).write_text(text, encoding="utf-8")
            self._flash(f"日志已保存：{Path(chosen).name}")
        except OSError as exc:
            self._flash(f"保存失败：{exc}")

    # ── 导出 ──
    def _on_export(self) -> None:
        selected = self.selected_packages()
        if not selected:
            QMessageBox.warning(self, APP_TITLE, "请先勾选需要保留的软件。")
            return
        ExportDialog(self, selected).exec()

    # ── 持久化 ──
    def _restore_window_state(self) -> None:
        st = settings()
        geo = st.value("win/geometry")
        if geo is not None:
            self.restoreGeometry(geo)
        if not st.value("view/logExpanded", True, type=bool):
            self._log_expanded = False
            self.log_toggle.setText("展开")
            self.log.setVisible(False)
            self.log_card.setMinimumHeight(34)
            self.log_card.setMaximumHeight(34)

    def _save_window_state(self) -> None:
        st = settings()
        st.setValue("win/maximized", self._maximized)
        if not self.isMaximized():
            st.setValue("win/geometry", self.saveGeometry())
        st.setValue("view/logExpanded", self._log_expanded)
        st.setValue("view/category", self.active_cat or "")

    def _save_selection(self) -> None:
        self._save_timer.start()

    def _flush_selection(self) -> None:
        # atomic_write_json 自己会建父目录；写失败只记日志，勾选丢了也不该影响关闭
        try:
            atomic_write_json(SELECTION_FILE, self.checked)
        except OSError as exc:
            logging.getLogger("selection").warning("勾选状态保存失败: %s", exc)

    def _load_selection(self) -> None:
        if not SELECTION_FILE.is_file():
            return
        try:
            data = json.loads(SELECTION_FILE.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:   # 半截 JSON / 无权限读，都退回空状态
            logging.getLogger("selection").warning("勾选状态读取失败: %s", exc)
            self.checked = {}
            return
        if isinstance(data, dict):
            # 严格布尔：老文件里可能存成 "false" / 0 之类，bool("false") 是 True
            self.checked = {
                str(k).lower(): str(v).strip().lower() in ("1", "true", "yes", "on")
                for k, v in data.items()
            }

    def closeEvent(self, e) -> None:  # noqa: N802
        for t in (self._scan_timer, self._flash_timer, self._save_timer, self._search_timer):
            t.stop()
        try:
            self._skel_anim.stop()
        except AttributeError:
            pass
        self._flush_selection()
        self._save_window_state()
        # 窗口先别急着关：线程还活着时进程退出会撞上 QThread 析构，直接 qFatal
        for w in list(self._workers):
            w.request_cancel()
            if not w.wait(3000):
                logging.getLogger("scan").warning("扫描线程 3s 内未退出，放弃等待")
        e.accept()


def _install_logging() -> None:
    """异常与 Qt 警告落盘：闪退时至少留下现场，不用靠猜。"""
    try:
        LOG_DIR.mkdir(parents=True, exist_ok=True)
        handler = logging.handlers.RotatingFileHandler(
            LOG_FILE, maxBytes=512 * 1024, backupCount=2, encoding="utf-8")
        handler.setFormatter(logging.Formatter(
            "%(asctime)s [%(levelname)s] %(message)s", "%Y-%m-%d %H:%M:%S"))
        root = logging.getLogger()
        root.setLevel(logging.INFO)
        root.addHandler(handler)
    except Exception:  # noqa: BLE001  写不了日志也不能影响启动
        return

    logging.info("启动 %s %s (%s) · Python %s · frozen=%s",
                 APP_TITLE, APP_VERSION, APP_TAG,
                 sys.version.split()[0], bool(getattr(sys, "frozen", False)))

    def _hook(exc_type, exc, tb) -> None:
        logging.getLogger("crash").critical("未捕获异常", exc_info=(exc_type, exc, tb))
        # 无控制台时给用户一个可见的提示，并指到日志文件
        try:
            QMessageBox.critical(
                None, f"{APP_TITLE} 出错",
                f"{exc_type.__name__}: {exc}\n\n"
                f"详情已写入日志：\n{LOG_FILE}\n\n"
                "可点标题栏 ⓘ →「复制诊断信息」反馈问题。")
        except Exception:  # noqa: BLE001
            pass
        sys.__excepthook__(exc_type, exc, tb)

    sys.excepthook = _hook

    def _qt_msg(mode, _ctx, msg) -> None:
        # 只落 Critical/Fatal：QtWarningMsg 里大量是 QSS 解析之类的噪声，
        # 逐条写盘会把真正有用的日志淹掉（也拖慢滚动）。
        if mode in (QtMsgType.QtCriticalMsg, QtMsgType.QtFatalMsg):
            logging.getLogger("qt").error("%s", msg)

    try:
        qInstallMessageHandler(_qt_msg)
    except Exception:  # noqa: BLE001
        pass


def _claim_single_instance() -> tuple:
    """单实例：已有窗口时通知它浮到前台。

    返回 (server, should_exit)。server 为 None 表示本机监听不可用（放行）；
    should_exit 为 True 表示已有实例，本进程应直接退出。
    """
    probe = QLocalSocket()
    probe.connectToServer(SINGLE_KEY)
    if probe.waitForConnected(200):
        probe.write(b"raise")
        probe.flush()
        probe.waitForBytesWritten(200)
        probe.disconnectFromServer()
        return None, True
    # 上次异常退出可能留下 socket 文件，先清掉再监听
    QLocalServer.removeServer(SINGLE_KEY)
    srv = QLocalServer()
    if not srv.listen(SINGLE_KEY):
        logging.warning("单实例监听失败，放行多开：%s", srv.errorString())
        return None, False
    return srv, False


def main() -> int:
    _install_logging()
    QApplication.setHighDpiScaleFactorRoundingPolicy(
        Qt.HighDpiScaleFactorRoundingPolicy.PassThrough)
    app = QApplication(sys.argv)
    app.setOrganizationName(SETTINGS_ORG)
    app.setApplicationName(SETTINGS_APP)
    app.setApplicationDisplayName(APP_TITLE)
    family = _pick_family(app)
    set_font_family(family)
    app.setFont(QFont(family, 10))
    app.setWindowIcon(app_icon())
    srv, should_exit = _claim_single_instance()
    if should_exit:
        logging.info("已有实例在运行，通知其前台显示后退出")
        return 0
    try:
        cfg = load_config()
    except ConfigError as exc:
        QMessageBox.critical(None, APP_TITLE, str(exc))
        return 1
    win = MainWindow(cfg)
    win.show()
    if srv is not None:
        def _raise_self() -> None:
            while srv.hasPendingConnections():
                conn = srv.nextPendingConnection()
                if conn is not None:
                    conn.close()
            if win.isMinimized():
                win.showNormal()
            win.raise_()
            win.activateWindow()
        srv.newConnection.connect(_raise_self)
    if settings().value("win/maximized", False, type=bool):
        win._manual_maximize()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
