"""Winget 软件管家 - 独立桌面版（Tkinter，无浏览器、无第三方依赖）v2.0 现代视觉。

用法：
    python gui_app.py           # 开发运行
    打包：pyinstaller 打包为单个 exe（见 build_exe.bat）

功能：
    1. 自动枚举 winget 已安装软件（只显示可重装的包，MSIX/注册表项自动隐藏）；
    2. 勾选需要保留/重装的软件，导出 JSON（winget 原生格式 + 富 profile 格式）；
    3. 重装系统后选择 JSON 一键批量恢复安装，实时日志输出。

UI 设计 v2.1 现代风：扁平轻拟物、冷灰底 + 靛蓝强调、Canvas 圆角按钮
（hover 渐变动画 / 按下下沉）、白卡片 + 投影、深色日志终端、窗口淡入、
窄窗口响应式收缩；v2.1 新增：斑马纹行底 + 悬停行高亮、细滚动条、
批量操作分段容器、按钮 Tooltip、clam 主题底座。
"""
from __future__ import annotations
import json, os, queue, re, sys, threading, tkinter as tk  # noqa: F401
from pathlib import Path
from tkinter import filedialog, messagebox, simpledialog, ttk  # noqa: F401
from tkinter import font as tkfont

if getattr(sys, "frozen", False):
    BASE_DIR = Path(sys.executable).resolve().parent
else:
    BASE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BASE_DIR))

from core.config import Config, ConfigError, find_winget  # noqa: E402
from core.errors import AppError  # noqa: E402
from services.catalog import CatalogService  # noqa: E402
from services.jobs import _ANSI, _FAIL, _FATAL, _FOUND, _SKIP, _SUCCESS  # noqa: E402
from services.profiles import load_profile, write_exports  # noqa: E402
from services.winget_cli import WingetRepository  # noqa: E402

APP_TITLE = "Winget 软件管家"
APP_VERSION = "v2.1"
PERSIST_DIR = Path.home() / "AppData/Local/WingetManager"
SELECTION_FILE = PERSIST_DIR / "selection.json"

FONT_NAME = "HarmonyOS Sans SC"
try:  # 用户机器装有鸿蒙字体则优先使用
    _probe = tk.Tk(); _probe.withdraw()
    HAS_FONT = FONT_NAME in tkfont.families(_probe)
    _probe.destroy()
except Exception:  # noqa: BLE001
    HAS_FONT = False
if not HAS_FONT:
    FONT_NAME = "Microsoft YaHei UI"

# ── 现代色板 v2.0（冷灰底 + 靛蓝强调）──
BG = "#F2F4F8"          # 页面底
CARD = "#FFFFFF"        # 卡片
BORDER = "#E5E9F0"      # 卡片描边
EDGE = "#DCE2EB"        # 卡片底部投影
TEXT = "#1A2233"        # 主文字
SUB = "#697386"         # 次文字
FAINT = "#A6AEBB"       # 禁用文字
DISABLED_BG = "#EEF1F6" # 禁用底
ACCENT = "#3E63DD"      # 靛蓝强调
ACCENT_HOVER = "#3651C9"
ACCENT_ACTIVE = "#2E46B0"
ACCENT_SOFT = "#EBEEFD"  # 选中/胶囊底
SUCCESS = "#1F9D55"; SUCCESS_LOG = "#4ADE80"
DANGER = "#E5484D"; DANGER_T = "#D64545"
LOG_BG = "#151B28"      # 日志深色终端底
ROW_HOVER = "#F0F4FB"   # 列表悬停行底
ROW_ZEBRA = "#FAFBFD"   # 列表偶数行底（斑马纹）

BTN_KINDS = {
    "primary":      dict(bg=ACCENT, fg="#ffffff", hover=ACCENT_HOVER, active=ACCENT_ACTIVE,
                         line=ACCENT, padx=14, font=(FONT_NAME, 10)),
    "secondary":    dict(bg=CARD, fg="#3A4454", hover="#F3F5F9", active="#E9EDF3",
                         line="#D5DBE5", padx=14, font=(FONT_NAME, 10)),
    "ghost":        dict(bg="#F1F4F9", fg="#3A4454", hover="#E7EBF2", active="#DDE3EC",
                         line="#F1F4F9", padx=9, font=(FONT_NAME, 9, "bold")),
    "danger_ghost": dict(bg="#F1F4F9", fg=DANGER_T, hover="#FDECEC", active="#F7DCDC",
                         line="#F1F4F9", padx=10, font=(FONT_NAME, 10)),
    "danger_solid": dict(bg=DANGER, fg="#ffffff", hover="#D6393F", active="#C2313A",
                         line=DANGER, padx=12, font=(FONT_NAME, 10)),
}

F_BODY = (FONT_NAME, 10); F_TITLE = (FONT_NAME, 12, "bold")
F_SECTION = (FONT_NAME, 10, "bold"); F_AUX = (FONT_NAME, 9)
F_HEAD = (FONT_NAME, 9, "bold"); F_MONO = ("Consolas", 9)


def _hex2rgb(c: str) -> tuple:
    c = c.lstrip("#")
    return tuple(int(c[i:i + 2], 16) for i in (0, 2, 4))


def _rgb2hex(t: tuple) -> str:
    return "#%02X%02X%02X" % tuple(max(0, min(255, round(v))) for v in t)


def _lerp_color(c1: str, c2: str, t: float) -> str:
    a, b = _hex2rgb(c1), _hex2rgb(c2)
    return _rgb2hex(tuple(a[i] + (b[i] - a[i]) * t for i in range(3)))


def round_rect(cv, x1, y1, x2, y2, r, **kw):
    """Canvas 圆角矩形（平滑多边形近似）。"""
    pts = [x1 + r, y1, x2 - r, y1, x2, y1, x2, y1 + r, x2, y2 - r, x2, y2,
           x2 - r, y2, x1 + r, y2, x1, y2, x1, y2 - r, x1, y1 + r, x1, y1]
    return cv.create_polygon(pts, smooth=True, **kw)


class ModernButton(tk.Canvas):
    """Canvas 圆角按钮：hover 渐变动画、按下下沉、禁用态统一灰。"""

    ANIM_MS = 60
    ANIM_STEPS = 4

    def __init__(self, master, app, text: str, kind: str, command) -> None:
        k = BTN_KINDS[kind]
        self._app = app; self._kind = kind; self._command = command
        self._colors = dict(k); self._text = text
        self._enabled = True; self._hover = False; self._pressed = False
        self._anim_job = None; self._anim_from = None; self._anim_t0 = 0.0
        try: master_bg = master.cget("bg")
        except Exception:  # noqa: BLE001
            master_bg = BG
        f = tkfont.Font(font=k["font"])
        tw = f.measure(text)
        self._r = app._px(8); self._h = app._px(34)
        self._bw = tw + 2 * app._px(k["padx"]) + self._r + app._px(4)
        super().__init__(master, width=self._bw, height=self._h, bg=master_bg,
                         highlightthickness=0, bd=0, cursor="hand2", takefocus=0)
        self._rect = round_rect(self, 0, 0, self._bw - 1, self._h - 1, self._r,
                                fill=k["bg"], outline=k["line"])
        self._txt = self.create_text(self._bw / 2, self._h / 2, text=text,
                                     font=k["font"], fill=k["fg"])
        self.bind("<Enter>", self._on_enter)
        self.bind("<Leave>", self._on_leave)
        self.bind("<ButtonPress-1>", self._on_press)
        self.bind("<ButtonRelease-1>", self._on_release)
        self.bind("<Destroy>", self._on_destroy)

    # ── 颜色渐变动画 ──
    def _anim_to(self, target: str) -> None:
        self._cancel_anim()
        cur = self.itemcget(self._rect, "fill")
        if cur == target:
            return
        self._anim_from = cur; self._anim_t0 = 0.0
        self._anim_target = target

        def _step():
            self._anim_t0 += 1 / self.ANIM_STEPS
            if self._anim_t0 >= 1.0:
                try: self.itemconfig(self._rect, fill=self._anim_target)
                except tk.TclError: pass
                self._anim_job = None
                return
            try:
                self.itemconfig(self._rect, fill=_lerp_color(
                    self._anim_from, self._anim_target, self._anim_t0))
            except tk.TclError:
                self._anim_job = None; return
            self._anim_job = self.after(self.ANIM_MS // self.ANIM_STEPS, _step)

        self._anim_job = self.after(self.ANIM_MS // self.ANIM_STEPS, _step)

    def _cancel_anim(self) -> None:
        if self._anim_job:
            try: self.after_cancel(self._anim_job)
            except Exception: pass  # noqa: BLE001
            self._anim_job = None

    def _on_destroy(self, event) -> None:
        if event.widget is self:
            self._cancel_anim()

    # ── 交互 ──
    def _state_colors(self) -> dict:
        k = self._colors
        if not self._enabled:
            return dict(fill=DISABLED_BG, line=DISABLED_BG, fg=FAINT, cursor="arrow")
        if self._pressed:
            return dict(fill=k["active"], line=k["line"], fg=k["fg"], cursor="hand2")
        if self._hover:
            return dict(fill=k["hover"], line=k["line"], fg=k["fg"], cursor="hand2")
        return dict(fill=k["bg"], line=k["line"], fg=k["fg"], cursor="hand2")

    def _apply_state(self, animate: bool = True) -> None:
        c = self._state_colors()
        if animate:
            self._anim_to(c["fill"])
        else:
            self._cancel_anim()
            try:
                self.itemconfig(self._rect, fill=c["fill"], outline=c["line"])
                self.itemconfig(self._txt, fill=c["fg"])
            except tk.TclError: pass
        try: self.config(cursor=c["cursor"])
        except tk.TclError: pass

    def _on_enter(self, _e=None) -> None:
        self._hover = True
        if self._enabled:
            self._apply_state()

    def _on_leave(self, _e=None) -> None:
        self._hover = False; self._pressed = False
        self._apply_state()

    def _on_press(self, e) -> None:
        if not self._enabled: return
        self._pressed = True
        self._apply_state()
        self.move(self._txt, 0, self._app._px(1))  # 按下下沉 1px

    def _on_release(self, e) -> None:
        if not self._pressed: return
        self._pressed = False
        self.move(self._txt, 0, -self._app._px(1))
        inside = 0 <= e.x <= self._bw and 0 <= e.y <= self._h
        self._apply_state()
        if inside and self._enabled and self._command:
            self._command()

    # ── 外部接口（与旧 Frame 按钮兼容）──
    def set_kind(self, kind: str) -> None:
        if kind == self._kind:
            return
        self._kind = kind
        self._colors = dict(BTN_KINDS[kind])
        self._apply_state(animate=False)

    def set_enabled(self, enabled: bool) -> None:
        self._enabled = bool(enabled)
        self._apply_state(animate=False)

    def config(self, **kw) -> None:  # noqa: A003
        if "text" in kw:
            self._text = kw.pop("text")
            try: self.itemconfig(self._txt, text=self._text)
            except tk.TclError: pass
        if kw:
            super().config(**kw)

    cget = tk.Canvas.cget


class Pill(tk.Canvas):
    """胶囊状态标签（winget 版本 · 主机名）。"""

    def __init__(self, master, app) -> None:
        self._app = app
        try: master_bg = master.cget("bg")
        except Exception:  # noqa: BLE001
            master_bg = CARD
        super().__init__(master, width=app._px(10), height=app._px(24), bg=master_bg,
                         highlightthickness=0, bd=0)
        self._r = app._px(12)
        self._rect = round_rect(self, 0, 0, app._px(9), app._px(23), self._r,
                                fill=ACCENT_SOFT, outline=ACCENT_SOFT)
        self._txt = self.create_text(0, app._px(12), text="", font=F_AUX, fill=ACCENT)

    def set_text(self, text: str) -> None:
        if not text:
            self.config(width=self._app._px(2)); return
        f = tkfont.Font(font=F_AUX)
        w = f.measure(text) + self._app._px(20)
        h = self._app._px(24)
        self.config(width=w, height=h)
        self.coords(self._rect, 0, 0, w - 1, h - 1)
        self.coords(self._txt, w / 2, h / 2)
        self.itemconfig(self._txt, text=text)


class ToolTip:
    """轻量工具提示：延迟 550ms 显示，深色气泡置于控件正下方。"""

    def __init__(self, widget, text: str, delay: int = 550) -> None:
        self._w = widget; self._text = text; self._delay = delay
        self._job = None; self._tip = None
        widget.bind("<Enter>", self._schedule, add="+")
        widget.bind("<Leave>", self._hide, add="+")
        widget.bind("<ButtonPress>", self._hide, add="+")
        widget.bind("<Destroy>", lambda e: self._hide(), add="+")

    def _schedule(self, _e=None) -> None:
        self._cancel()
        self._job = self._w.after(self._delay, self._show)

    def _cancel(self) -> None:
        if self._job:
            try: self._w.after_cancel(self._job)
            except Exception: pass  # noqa: BLE001
            self._job = None

    def _show(self) -> None:
        self._job = None
        if self._tip:
            return
        x = self._w.winfo_rootx() + self._w.winfo_width() // 2
        y = self._w.winfo_rooty() + self._w.winfo_height() + 8
        self._tip = tw = tk.Toplevel(self._w)
        tw.wm_overrideredirect(True)
        try: tw.attributes("-topmost", True)
        except tk.TclError: pass
        tk.Label(tw, text=self._text, bg="#2A3347", fg="#F2F5FA",
                 font=F_AUX, padx=10, pady=5, justify="left").pack()
        tw.update_idletasks()
        tw.wm_geometry(f"+{x - tw.winfo_reqwidth() // 2}+{y}")

    def _hide(self, _e=None) -> None:
        self._cancel()
        if self._tip:
            try: self._tip.destroy()
            except Exception: pass  # noqa: BLE001
            self._tip = None


class SearchBox(tk.Canvas):
    """圆角搜索框：聚焦描边变色动画、占位符、内嵌清空钮。"""

    def __init__(self, master, app, on_change) -> None:
        self._app = app
        super().__init__(master, width=app._px(220), height=app._px(36), bg=BG,
                         highlightthickness=0, bd=0)
        self._bw = app._px(220); self._h = app._px(36); self._r = app._px(10)
        self.var = tk.StringVar()
        self.var.trace_add("write", lambda *_: self._refresh())
        self._rect = round_rect(self, 0, 0, self._bw - 1, self._h - 1, self._r,
                                fill=CARD, outline=BORDER)
        self.entry = tk.Entry(self, textvariable=self.var, bd=0, relief="flat",
                              bg=CARD, fg=TEXT, font=F_BODY, insertbackground=TEXT,
                              highlightthickness=0, cursor="xterm", takefocus=1)
        self._win = self.create_window(self._px(13), self._h / 2, anchor="w",
                                       window=self.entry, width=self._bw - self._px(38),
                                       height=self._h - self._px(8))
        self._ph = self.create_text(self._px(13), self._h / 2, anchor="w",
                                    text="搜索名称或包 ID", font=F_BODY, fill=SUB,
                                    state="normal")
        self._clear = self.create_text(self._bw - self._px(14), self._h / 2, text="×",
                                       font=(FONT_NAME, 12), fill=SUB, state="hidden")
        self.tag_bind(self._clear, "<Enter>", lambda e: self.itemconfig(self._clear, fill=TEXT))
        self.tag_bind(self._clear, "<Leave>", lambda e: self.itemconfig(self._clear, fill=SUB))
        self.tag_bind(self._clear, "<Button-1>", self._clear_all)
        self.entry.bind("<FocusIn>", lambda e: self._focus(True))
        self.entry.bind("<FocusOut>", lambda e: self._focus(False))
        self.entry.bind("<Escape>", self._clear_all)
        self._refresh()
        self._resize_job = None

    def _px(self, n: int) -> int:
        return self._app._px(n)

    def _focus(self, on: bool) -> None:
        self._cancel_ring()
        target = ACCENT if on else BORDER
        cur = self.itemcget(self._rect, "outline")
        if cur != target:
            steps, i = 4, 0
            def _step():
                nonlocal i
                i += 1
                try:
                    if i >= steps:
                        self.itemconfig(self._rect, outline=target); return
                    self.itemconfig(self._rect, outline=_lerp_color(cur, target, i / steps))
                    self.after(20, _step)
                except tk.TclError: pass
            _step()
        if on and self.var.get():
            self.entry.select_range(0, "end")
        self._refresh()

    def _cancel_ring(self) -> None:
        pass  # 简单实现：短动画可被后续覆盖

    def _clear_all(self, _e=None) -> str:
        self.var.set("")
        if _e is None or getattr(_e, "keysym", None) != "Escape":
            self.entry.focus_set()
        else:
            self._app.focus_set()
        return "break"

    def _refresh(self) -> None:
        val = self.var.get()
        try:
            focused = self.focus_get() is self.entry
        except Exception:  # noqa: BLE001
            focused = False
        self.itemconfig(self._clear, state="normal" if val else "hidden")
        self.itemconfig(self._ph, state="hidden" if (val or focused) else "normal")

    def set_width(self, w: int) -> None:
        self._bw = w
        self.config(width=w)
        self.coords(self._rect, 0, 0, w - 1, self._h - 1)
        self.itemconfig(self._win, width=w - self._px(38))
        self.coords(self._clear, w - self._px(14), self._h / 2)

    def focus_entry(self) -> None:
        self.entry.focus_set()


def build_config() -> Config:
    winget_exe = find_winget()
    return Config(host="127.0.0.1", port=0, export_dir=BASE_DIR / "exports",
        winget_exe=winget_exe, cache_ttl=60, command_timeout=300,
        open_browser=False, log_level="INFO", log_file=None, extra={})


class App(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title(APP_TITLE); self.geometry("1080x760"); self.minsize(960, 620)
        self.configure(bg=BG)
        self.config = build_config()
        self.repo = WingetRepository(self.config)
        self.catalog = CatalogService(self.config, self.repo)
        self.snapshot = None
        self.packages: list = []
        self.checked: dict[str, bool] = {}
        self.iid_pkg: dict[str, object] = {}
        self.group_iids: dict[str, str] = {}
        self._gid_cat: dict[str, str] = {}
        self._group_open: dict[str, bool] = {}
        self.scan_queue: queue.Queue = queue.Queue()
        self.import_queue: queue.Queue = queue.Queue()
        self.cancel_event = threading.Event()
        self.importing = False
        # 状态机 / 计时器
        self.state = "BOOT"
        self._chip_text = ""
        self._matched = 0
        self._scroll_frac = 0.0
        self._cursor = "arrow"
        self._hover_iid = None
        self._import_summary = ""
        self._import_base = ""
        self._scan_after = None; self._scan_tenths = 0
        self._prog_after = None; self._prog_active = False; self._prog_x = 0
        self._save_job = None; self._flash_job = None; self._cancel_timer = None
        self._poll_job = None; self._fade_job = None
        self._setup_style(); self._build_ui(); self._load_selection()
        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self._bind_keys()
        self.bind("<Configure>", self._on_root_configure)
        self.search_box.focus_entry()
        self._fade_in()
        self._poll_job = self.after(100, self._poll); self.rescan()

    # ── DPI 换算 ──
    def _px(self, n: int) -> int:
        return round(n * self.winfo_fpixels("1i") / 96)

    # ── 窗口淡入动画 ──
    def _fade_in(self) -> None:
        try:
            self.attributes("-alpha", 0.0)
        except tk.TclError:
            return
        alpha = {"v": 0.0}

        def _step():
            alpha["v"] = min(1.0, alpha["v"] + 0.2)
            try: self.attributes("-alpha", alpha["v"])
            except tk.TclError: return
            if alpha["v"] < 1.0:
                self._fade_job = self.after(30, _step)

        self._fade_job = self.after(30, _step)

    # ── 响应式：窄窗口收缩 ──
    def _on_root_configure(self, event) -> None:
        if event.widget is not self:
            return
        w = self.winfo_width()
        if w <= 1:
            return
        if w < 1060:
            self.winget_chip.set_text("")  # 窄窗口隐藏 chip
        else:  # 恢复正常宽度时还原 chip
            self.winget_chip.set_text("正在扫描…" if self.state == "SCANNING" else self._chip_text)
        # 主体边距与搜索框宽度
        narrow = w < 1060
        try:
            self._body.config(padx=self._px(10 if narrow else 16))
        except tk.TclError: pass
        self.search_box.set_width(self._px(170 if narrow else 220))

    # ── 按钮工厂（ModernButton）──
    def _make_button(self, parent, text: str, kind: str, command) -> ModernButton:
        return ModernButton(parent, self, text, kind, command)

    def _set_enabled(self, btn, kind: str, enabled: bool) -> None:
        btn.set_kind(kind)
        btn.set_enabled(enabled)

    def _setup_style(self) -> None:
        style = ttk.Style(self)
        try: style.theme_use("clam")  # clam 提供完全的边框/配色控制
        except tk.TclError: pass
        style.configure(".", font=F_BODY, background=BG, foreground=TEXT)
        style.layout("Treeview", [("Treeview.treearea", {"sticky": "nswe"})])
        style.configure("Treeview", font=F_BODY, rowheight=self._px(32),
                        background=CARD, fieldbackground=CARD, borderwidth=0,
                        relief="flat", bordercolor=CARD, lightcolor=CARD,
                        darkcolor=CARD)
        style.configure("Treeview.Heading", font=F_HEAD, background=CARD,
                        foreground=SUB, padding=(12, 9), borderwidth=0,
                        relief="flat", bordercolor=CARD, lightcolor=CARD,
                        darkcolor=CARD)
        style.map("Treeview", background=[("selected", ACCENT_SOFT)],
                  foreground=[("selected", TEXT)])
        style.map("Treeview.Heading",
                  background=[("active", CARD), ("pressed", CARD)],
                  foreground=[("active", SUB), ("pressed", SUB)])
        # 细滚动条：隐藏箭头与抓痕，仅留圆润拇指
        style.layout("Vertical.TScrollbar", [
            ("Vertical.Scrollbar.trough", {"children": [
                ("Vertical.Scrollbar.thumb", {"expand": "1", "sticky": "nswe"})],
                "sticky": "ns"})])
        sb_thumb = "#C3CBD9"
        style.configure("Vertical.TScrollbar", troughcolor=CARD,
                        background=sb_thumb, bordercolor=CARD,
                        lightcolor=sb_thumb, darkcolor=sb_thumb,
                        gripcount=0, relief="flat", borderwidth=0)
        style.map("Vertical.TScrollbar",
                  background=[("active", "#A9B3C6"), ("pressed", "#8E9AB1")])

    # ── 卡片容器：白底 + 1px 描边 + 底部投影边 ──
    def _card(self, parent) -> tk.Frame:
        outer = tk.Frame(parent, bg=parent.cget("bg") if hasattr(parent, "cget") else BG)
        card = tk.Frame(outer, bg=CARD, highlightthickness=1, highlightbackground=BORDER)
        card.pack(fill="both", expand=True)
        tk.Frame(outer, bg=EDGE, height=self._px(2)).pack(fill="x")
        return outer

    # ── UI 构建 ──
    def _build_ui(self) -> None:
        px = self._px
        # 顶栏：白底 + 投影
        top = tk.Frame(self, bg=CARD, height=px(58)); top.pack(fill="x")
        top.pack_propagate(False)
        # 标题前的小圆点（品牌感）
        dot = tk.Canvas(top, width=px(10), height=px(10), bg=CARD, highlightthickness=0)
        dot.create_oval(1, 1, px(9), px(9), fill=ACCENT, outline="")
        dot.pack(side="left", padx=(px(18), px(8)))
        tk.Label(top, text=APP_TITLE, bg=CARD, fg=TEXT, font=F_TITLE).pack(side="left")
        tk.Label(top, text=APP_VERSION, bg=CARD, fg=FAINT, font=F_AUX)\
            .pack(side="right", padx=(0, px(18)))
        self.winget_chip = Pill(top, self)
        self.winget_chip.pack(side="left", padx=(px(14), 0))
        tk.Frame(self, bg=BORDER, height=1).pack(fill="x")
        tk.Frame(self, bg="#E9EDF3", height=1).pack(fill="x")

        # 工具栏：直接放在冷灰底上（悬浮感）
        bar = tk.Frame(self, bg=BG, height=px(58)); bar.pack(fill="x")
        bar.pack_propagate(False)
        # 搜索框（先创建，Tab 序靠前）
        self.search_box = SearchBox(bar, self, None)
        self.search_var = self.search_box.var
        self.search_var.trace_add("write", self._on_search_changed)
        self.search_entry = self.search_box.entry
        self.search_box.pack(side="left", padx=(px(18), 0))
        # 左组：重新扫描
        self.scan_btn = self._make_button(bar, "重新扫描", "secondary", self.rescan)
        self.scan_btn.pack(side="left", padx=(px(12), 0))
        # 中组：4 个 Ghost 收进白色分段容器，形成“一组操作”的视觉归类
        bulk_wrap = tk.Frame(bar, bg=CARD, highlightthickness=1,
                             highlightbackground=BORDER)
        bulk_wrap.pack(side="left", padx=(px(10), 0))
        bulk_inner = tk.Frame(bulk_wrap, bg=CARD)
        bulk_inner.pack(padx=px(5), pady=px(5))
        self._bulk_btns = []
        for i, (text, cmd) in enumerate((("全选", lambda: self._set_all(True)),
                                         ("全不选", lambda: self._set_all(False)),
                                         ("反选", self._invert),
                                         ("取消运行库", self._apps_only))):
            b = self._make_button(bulk_inner, text, "ghost", cmd)
            b.pack(side="left", padx=(0, px(4) if i < 3 else 0))
            self._bulk_btns.append(b)
        # spacer
        tk.Frame(bar, bg=BG).pack(side="left", fill="both", expand=True)
        # 右组
        self.export_btn = self._make_button(bar, "导出配置", "primary", self.on_export)
        self.export_btn.pack(side="right", padx=(0, px(18)))
        self.import_btn = self._make_button(bar, "导入恢复…", "secondary", self.on_import)
        self.import_btn.pack(side="right", padx=(0, px(8)))
        self.cancel_btn = self._make_button(bar, "取消导入", "danger_ghost", self.on_cancel)
        self.cancel_btn.pack(side="right", padx=(0, px(8)))
        self._set_enabled(self.cancel_btn, "danger_ghost", False)
        # 按钮工具提示（低频操作补足说明，不干扰熟练用户）
        for w, tip in ((self.scan_btn, "重新运行 winget 扫描并刷新列表"),
                       (self.export_btn, "把勾选的软件导出为 JSON 配置"),
                       (self.import_btn, "选择 JSON 配置，批量恢复安装"),
                       (self.cancel_btn, "终止当前导入进程")):
            ToolTip(w, tip)

        # 状态栏（先 pack 到底部）
        self._status_bar = tk.Frame(self, bg=CARD, height=px(36))
        self._status_bar.pack(fill="x", side="bottom")
        self._status_bar.pack_propagate(False)
        tk.Frame(self, bg=BORDER, height=1).pack(fill="x", side="bottom")
        self.status_var = tk.StringVar(value="正在扫描已安装软件…")
        tk.Label(self._status_bar, textvariable=self.status_var, bg=CARD, fg=SUB,
                 font=F_AUX).pack(side="left", padx=(px(18), 0))
        self._sel_frame = tk.Frame(self._status_bar, bg=CARD)
        self._sel_zero = tk.Label(self._sel_frame, text="已勾选 0 项 · 勾选后点「导出配置」",
                                  bg=CARD, fg=SUB, font=F_AUX)
        self._sel_pre = tk.Label(self._sel_frame, text="已勾选 ", bg=CARD, fg=SUB, font=F_AUX)
        self._sel_num = tk.Label(self._sel_frame, text="0", bg=CARD, fg=ACCENT,
                                 font=(FONT_NAME, 10, "bold"))
        self._sel_post = tk.Label(self._sel_frame, text=" / 0 项", bg=CARD, fg=SUB, font=F_AUX)
        self._sel_frame.pack(side="right", padx=(0, px(18)))
        self._imp_frame = tk.Frame(self._status_bar, bg=CARD)
        self._imp_s = tk.Label(self._imp_frame, text="成功 0", bg=CARD, fg=SUCCESS, font=F_AUX)
        self._imp_s1 = tk.Label(self._imp_frame, text=" · ", bg=CARD, fg=SUB, font=F_AUX)
        self._imp_k = tk.Label(self._imp_frame, text="跳过 0", bg=CARD, fg=SUB, font=F_AUX)
        self._imp_s2 = tk.Label(self._imp_frame, text=" · ", bg=CARD, fg=SUB, font=F_AUX)
        self._imp_f = tk.Label(self._imp_frame, text="失败 0", bg=CARD, fg=DANGER_T, font=F_AUX)
        for w in (self._imp_s, self._imp_s1, self._imp_k, self._imp_s2, self._imp_f):
            w.pack(side="left")
        # 进度条（3px，自绘）
        self._prog_track = tk.Frame(self, bg=ACCENT_SOFT, height=3)
        self._prog_track.pack_propagate(False)
        self._prog_slider = tk.Frame(self._prog_track, bg=ACCENT, width=px(140), height=3)

        # 主体：列表卡片 + 日志卡片
        self._body = tk.Frame(self, bg=BG)
        self._body.pack(fill="both", expand=True, padx=px(16))
        tree_wrap = self._card(self._body)
        tree_wrap.pack(fill="both", expand=True, pady=(px(14), 0))
        card_inner = tree_wrap.winfo_children()[0]  # 白色卡片本体
        # 顶部焦点条
        self._tree_focusbar = tk.Frame(card_inner, bg=BORDER, height=2)
        self._tree_focusbar.pack(side="top", fill="x")
        columns = ("name", "id", "ver", "cat")
        self.tree = ttk.Treeview(card_inner, columns=columns, show="tree headings",
                                 selectmode="browse")
        self.tree.heading("#0", text="", anchor="center")
        self.tree.heading("name", text="软件名称", anchor="w")
        self.tree.heading("id", text="包 ID", anchor="w")
        self.tree.heading("ver", text="已装版本", anchor="e")
        self.tree.heading("cat", text="分类", anchor="w")
        self.tree.column("#0", width=px(56), minwidth=px(56), stretch=False, anchor="center")
        self.tree.column("name", width=px(240), minwidth=px(220), anchor="w")
        self.tree.column("id", width=px(260), anchor="w")
        self.tree.column("ver", width=px(110), anchor="e", stretch=False)
        self.tree.column("cat", width=px(110), anchor="w", stretch=False)
        vsb = ttk.Scrollbar(card_inner, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=vsb.set)
        vsb.pack(side="right", fill="y")
        self.tree.pack(side="left", fill="both", expand=True)
        self.tree.tag_configure("on", foreground=TEXT)
        self.tree.tag_configure("off", foreground="#3A4454")
        self.tree.tag_configure("group", foreground=TEXT, background="#F7F9FC", font=F_SECTION)
        self.tree.tag_configure("zebra", background=ROW_ZEBRA)
        self.tree.tag_configure("hover", background=ROW_HOVER)
        self.tree.bind("<Leave>", lambda e: self._set_row_hover(None))
        self.tree.bind("<FocusIn>", lambda e: self._tree_focusbar.config(bg=ACCENT))
        self.tree.bind("<FocusOut>", lambda e: self._tree_focusbar.config(bg=BORDER))
        # 空状态：图标 + 文案
        self._empty = tk.Frame(card_inner, bg=CARD)
        icon = tk.Canvas(self._empty, width=px(56), height=px(56), bg=CARD,
                         highlightthickness=0)
        cx, cy, r = px(26), px(24), px(13)
        icon.create_oval(cx - r, cy - r, cx + r, cy + r, outline="#B6C0CF", width=px(3))
        icon.create_line(cx + r * 0.68, cy + r * 0.68, px(44), px(44),
                         width=px(4), fill="#B6C0CF", capstyle="round")
        icon.pack()
        self._empty_label = tk.Label(self._empty, bg=CARD, fg=SUB, font=F_BODY,
                                     justify="center")
        self._empty_label.pack(pady=(px(6), 0))
        # 右键菜单
        self._menu = tk.Menu(self, tearoff=0)
        self._menu.add_command(label="复制包 ID", command=lambda: self._copy_menu("id"))
        self._menu.add_command(label="复制软件名称", command=lambda: self._copy_menu("name"))
        self._menu.add_command(label="复制「名称 (包ID)」", command=lambda: self._copy_menu("both"))
        self._menu_pkg = None

        # 日志卡片（深色终端风）
        log_wrap = self._card(self._body)
        log_wrap.pack(fill="x", pady=(px(10), px(14)))
        log_card = log_wrap.winfo_children()[0]
        head = tk.Frame(log_card, bg=LOG_BG, height=px(34)); head.pack(fill="x")
        head.pack_propagate(False)
        tk.Label(head, text="导入 / 运行日志", bg=LOG_BG, fg="#E2E8F0",
                 font=F_SECTION).pack(side="left", padx=(px(14), 0))
        clear_lbl = tk.Label(head, text="清空", bg=LOG_BG, fg="#7C8AA0", font=F_AUX,
                             cursor="hand2")
        clear_lbl.pack(side="right", padx=(0, px(14)))
        clear_lbl.bind("<Enter>", lambda e: clear_lbl.config(fg="#CBD5E1"))
        clear_lbl.bind("<Leave>", lambda e: clear_lbl.config(fg="#7C8AA0"))
        clear_lbl.bind("<Button-1>", lambda e: self._clear_log())
        tk.Frame(log_card, bg="#232B3D", height=1).pack(fill="x")
        self.log = tk.Text(log_card, height=6, bg=LOG_BG, bd=0, relief="flat",
                           padx=px(14), pady=px(8), font=F_BODY, wrap="word",
                           spacing1=2, spacing3=3, selectbackground="#334155",
                           insertbackground="#E2E8F0",
                           state="disabled", takefocus=0)
        self.log.pack(fill="x")
        for tag, kw in (("ts", dict(font=F_MONO, foreground="#5C6B85")),
                        ("info", dict(foreground="#C7D0DE")),
                        ("success", dict(foreground=SUCCESS_LOG)),
                        ("skip", dict(foreground="#7C8AA0")),
                        ("fail", dict(foreground="#F87171")),
                        ("error", dict(foreground="#FCA5A5", background="#3B1D22")),
                        ("done", dict(foreground="#93B4FF", font=(FONT_NAME, 10, "bold")))):
            self.log.tag_configure(tag, **kw)
        # Treeview 事件
        self.tree.bind("<Button-1>", self._on_click)
        self.tree.bind("<Double-Button-1>", self._on_double)
        self.tree.bind("<Motion>", self._on_motion)
        self.tree.bind("<Button-3>", self._on_menu)
        self.tree.bind("<space>", self._on_space)
        self.tree.bind("<Return>", self._on_enter)
        self.tree.bind("<Left>", lambda e: self._on_lr(e, False))
        self.tree.bind("<Right>", lambda e: self._on_lr(e, True))
        self.tree.bind("<Control-c>", self._on_copy)
        self.tree.bind("<Escape>", self._on_tree_esc)

    # ── 键盘 ──
    def _bind_keys(self) -> None:
        self.bind("<Control-f>", self._on_ctrl_f)
        self.bind_all("<Control-a>", self._on_ctrl_a)

    def _on_ctrl_f(self, _e) -> str:
        self.search_box.focus_entry()
        if self.search_var.get():
            self.search_entry.select_range(0, "end")
        return "break"

    def _on_ctrl_a(self, _e) -> str:
        w = self.focus_get()
        if isinstance(w, (tk.Entry, tk.Text)):
            return None
        if self.state in ("READY", "IMPORTING", "CANCELING"):
            self._set_all(True)
        return "break"

    # ── 搜索框 ──
    def _on_search_changed(self, *_):
        self.search_box._refresh()
        self._render_tree()

    def _on_search_esc(self, _e=None) -> str:
        self.search_var.set("")
        self.tree.focus_set()
        return "break"

    def _on_tree_esc(self, _e=None) -> str:
        self.search_var.set("")
        return "break"

    def _on_clear_search(self, _e=None) -> None:
        self.search_var.set("")
        self.search_box.focus_entry()

    # ── 状态机（禁用矩阵唯一入口；SCANNING 下搜索框保持可用）──
    def _set_state(self, state: str) -> None:
        self.state = state
        s = state
        self._set_enabled(self.scan_btn, "secondary", s in ("SCAN_FAILED", "READY"))
        bulk_ok = s in ("READY", "IMPORTING", "CANCELING")
        for b in self._bulk_btns:
            self._set_enabled(b, "ghost", bulk_ok)
        self._set_enabled(self.import_btn, "secondary", s in ("SCAN_FAILED", "READY"))
        if s == "IMPORTING":
            self.cancel_btn._kind = "danger_solid"
            self._set_enabled(self.cancel_btn, "danger_solid", True)
        else:
            self.cancel_btn._kind = "danger_ghost"
            self._set_enabled(self.cancel_btn, "danger_ghost", s == "IMPORTING")
        self.winget_chip.set_text("正在扫描…" if s == "SCANNING" else self._chip_text)
        if s in ("IMPORTING", "CANCELING"):
            self._sel_frame.pack_forget()
            self._imp_frame.pack(side="right", padx=(0, self._px(18)))
        else:
            self._imp_frame.pack_forget()
            self._sel_frame.pack(side="right", padx=(0, self._px(18)))
        self._update_status()
        self._update_empty()

    # ── 进度条 ──
    def _progress_start(self) -> None:
        if self._prog_active:
            return
        self._prog_active = True
        self._prog_x = 0
        self._prog_track.pack(fill="x", side="bottom")
        self._prog_step()

    def _prog_step(self) -> None:
        if not self._prog_active:
            return
        w = self._px(140)
        tw = self._prog_track.winfo_width()
        self._prog_x += self._px(8)
        if tw and self._prog_x > tw:
            self._prog_x = -w
        self._prog_slider.place(x=self._prog_x, y=0, width=w, height=3)
        self._prog_after = self.after(16, self._prog_step)

    def _progress_stop(self) -> None:
        self._prog_active = False
        if self._prog_after:
            self.after_cancel(self._prog_after); self._prog_after = None
        self._prog_slider.place_forget(); self._prog_track.pack_forget()

    # ── 扫描 ──
    def rescan(self) -> None:
        if self.state in ("SCANNING", "IMPORTING", "CANCELING"):
            return
        self._set_state("SCANNING")
        self._progress_start()
        self._scan_tenths = 0
        self._scan_tick()
        self.log_append("info", "开始扫描…")

        def work() -> None:
            try:
                snapshot = self.catalog.get_catalog(force=True)
                self.scan_queue.put(("ok", snapshot))
            except Exception as exc:  # noqa: BLE001
                self.scan_queue.put(("err", exc))

        threading.Thread(target=work, daemon=True).start()

    def _scan_tick(self) -> None:
        t = self._scan_tenths / 10
        self._scan_tenths += 1
        prefix = f"{self._import_summary} · " if self._import_summary else ""
        self.status_var.set(f"{prefix}正在扫描 · 已用 {t:.1f}s")
        self._scan_after = self.after(100, self._scan_tick)

    def _scan_tick_stop(self) -> None:
        if self._scan_after:
            self.after_cancel(self._scan_after); self._scan_after = None

    def _apply_snapshot(self, snapshot) -> None:
        self.snapshot = snapshot
        self.packages = [p for p in snapshot.packages if p.reinstallable]
        hidden = snapshot.counts["total"] - len(self.packages)
        self._chip_text = f"winget {snapshot.wingetVersion} · {snapshot.machine['hostname']}"
        self.log_append("info",
            f"扫描完成：可重装 {len(self.packages)} 个；另有 {hidden} 个系统组件无法自动重装，已隐藏。")
        valid = {p.packageIdentifier.lower() for p in self.packages}
        removed = [k for k in self.checked if k not in valid]
        if removed:
            for k in removed:
                del self.checked[k]
            self.log_append("info", f"已移除 {len(removed)} 个已卸载软件的勾选记录")
        self._render_tree(); self._update_status(); self._save_selection()

    # ── Treeview 渲染 ──
    def _render_tree(self) -> None:
        self._empty.place_forget()  # 先撤浮层，防止盖住结果行
        tree = self.tree
        for cat, gid in self.group_iids.items():
            try: self._group_open[cat] = bool(tree.item(gid, "open"))
            except tk.TclError: pass
        if tree.get_children():
            self._scroll_frac = tree.yview()[0]
        tree.delete(*tree.get_children())
        self.iid_pkg.clear(); self.group_iids.clear(); self._gid_cat.clear()
        self._hover_iid = None
        keyword = self.search_var.get().strip().lower()
        groups: dict[str, list] = {}
        for pkg in self.packages:
            if keyword and keyword not in f"{pkg.name} {pkg.packageIdentifier}".lower():
                continue
            groups.setdefault(pkg.category, []).append(pkg)
        self._matched = sum(len(v) for v in groups.values())
        for category in sorted(groups, key=lambda c: (-len(groups[c]), c)):
            items = groups[category]
            gid = tree.insert("", "end", open=self._group_open.get(category, True),
                              text="", values=(category, "", "", ""), tags=("group",))
            self.group_iids[category] = gid; self._gid_cat[gid] = category
            for row_i, pkg in enumerate(items):
                on = self.checked.get(pkg.packageIdentifier.lower(), False)
                tags = ["on" if on else "off"]
                if row_i % 2:  # 斑马纹：组内奇数行更浅底色
                    tags.append("zebra")
                iid = tree.insert(gid, "end", text="☑" if on else "☐",
                    values=(pkg.name, pkg.packageIdentifier, pkg.version or "-", pkg.category),
                    tags=tuple(tags))
                self.iid_pkg[iid] = pkg
            self._update_group_mark(category)
        if not keyword and self._scroll_frac and tree.get_children():
            tree.yview_moveto(self._scroll_frac)
        self._update_empty(); self._update_status()

    def _update_group_mark(self, category: str) -> None:
        gid = self.group_iids.get(category)
        if not gid: return
        children = self.tree.get_children(gid); n = len(children)
        sel = sum(1 for c in children
                  if self.checked.get(self.iid_pkg[c].packageIdentifier.lower()))
        mark = "☑" if n and sel == n else ("☐" if sel == 0 else "▣")
        self.tree.item(gid, text=mark, values=(category, "", "", f"{sel}/{n}"))

    def _update_empty(self) -> None:
        show = True
        kw = self.search_var.get().strip()
        if not self.packages:
            if self.state == "SCANNING":
                self._empty_label.config(text="正在扫描已安装软件，约需 5~20 秒…")
            elif self.state == "SCAN_FAILED":
                self._empty_label.config(text="扫描失败：winget 未安装或不可用\n可点击上方「重新扫描」重试")
            elif self.state == "READY":
                self._empty_label.config(text="未发现可重装的软件")
            else:
                show = False
        elif kw and self._matched == 0:
            self._empty_label.config(text=f"未找到与『{kw}』匹配的软件")
        else:
            show = False
        if show:
            self._empty.place(relx=0.5, rely=0.42, anchor="center")
        else:
            self._empty.place_forget()

    # ── Treeview 交互 ──
    def _set_row_hover(self, iid) -> None:
        """悬停行高亮：移动 hover tag，值不变不重绘。"""
        if iid == self._hover_iid:
            return
        if self._hover_iid:
            try:
                old = [t for t in self.tree.item(self._hover_iid, "tags") if t != "hover"]
                self.tree.item(self._hover_iid, tags=old)
            except tk.TclError:
                pass
        self._hover_iid = None
        if iid and iid in self.iid_pkg:
            try:
                tags = list(self.tree.item(iid, "tags"))
                # hover 放在 zebra 之前，优先级更高（首个定义 background 的 tag 生效）
                tags = [tags[0]] + ["hover"] + [t for t in tags[1:] if t != "hover"]
                self.tree.item(iid, tags=tags)
                self._hover_iid = iid
            except tk.TclError:
                pass

    def _toggle_row(self, iid: str) -> None:
        pkg = self.iid_pkg.get(iid)
        if pkg is not None:
            key = pkg.packageIdentifier.lower()
            on = not self.checked.get(key, False)
            self.checked[key] = on
            self.tree.item(iid, text="☑" if on else "☐",
                           tags=self._row_tags(iid, on))
            self._update_group_mark(pkg.category)
        elif iid in self._gid_cat:
            self.tree.selection_remove(iid)  # 分组行不可选中
            children = self.tree.get_children(iid)
            if not children: return
            sel = sum(1 for c in children
                      if self.checked.get(self.iid_pkg[c].packageIdentifier.lower()))
            target = sel != len(children)  # 全选→清空；否则→全选
            for c in children:
                p = self.iid_pkg.get(c)
                if p is not None:
                    self.checked[p.packageIdentifier.lower()] = target
                    self.tree.item(c, text="☑" if target else "☐",
                                   tags=self._row_tags(c, target))
            self._update_group_mark(self._gid_cat[iid])
        self._update_status(); self._save_selection()

    def _toggle_group_open(self, iid: str) -> None:
        self.tree.selection_remove(iid)
        opened = not self.tree.item(iid, "open")
        self.tree.item(iid, open=opened)
        cat = self._gid_cat.get(iid)
        if cat: self._group_open[cat] = opened

    def _on_click(self, event) -> None:
        iid = self.tree.identify_row(event.y)
        if not iid: return
        if iid in self._gid_cat:
            # 类绑定会在 press 时置选中，after_idle 补摘除，避免蓝块压住分组色
            self.tree.after_idle(lambda: self.tree.selection_remove(iid))
            if self.tree.identify_column(event.x) != "#0": return
            if self.state == "SCANNING": return
            self._toggle_row(iid)
            return
        if self.tree.identify_column(event.x) != "#0":
            return  # 非 #0 区域：仅选中，不改勾选
        if self.state == "SCANNING":
            return  # 旧数据只读
        self._toggle_row(iid)

    def _on_double(self, event) -> str:
        iid = self.tree.identify_row(event.y)
        if not iid: return "break"
        pkg = self.iid_pkg.get(iid)
        if pkg is not None:
            self._copy_text(pkg.packageIdentifier, f"已复制包 ID：{pkg.packageIdentifier}")
        elif iid in self._gid_cat and self.state != "SCANNING":
            self.tree.after_idle(lambda: self.tree.selection_remove(iid))
            self._toggle_group_open(iid)
        return "break"

    def _row_tags(self, iid: str, on: bool) -> tuple:
        """勾选态换 tag 时保留斑马纹/悬停标记。"""
        keep = [t for t in self.tree.item(iid, "tags") if t in ("hover", "zebra")]
        return ("on" if on else "off", *keep)

    def _on_motion(self, event) -> None:
        iid = self.tree.identify_row(event.y)
        cur = "hand2" if iid in self._gid_cat else "arrow"
        if cur != self._cursor:  # 值不变不 config，避免 Motion 高频重绘卡顿
            self._cursor = cur
            self.tree.config(cursor=cur)
        self._set_row_hover(iid if iid in self.iid_pkg else None)

    def _on_space(self, _e) -> str:
        if self.state not in ("READY", "IMPORTING", "CANCELING"):
            return "break"
        iid = self.tree.focus()
        if iid:
            self._toggle_row(iid)
        return "break"

    def _on_enter(self, _e) -> str:
        iid = self.tree.focus()
        if not iid: return "break"
        pkg = self.iid_pkg.get(iid)
        if pkg is not None:
            self._copy_text(pkg.packageIdentifier, f"已复制包 ID：{pkg.packageIdentifier}")
        elif iid in self._gid_cat and self.state != "SCANNING":
            self._toggle_group_open(iid)
        return "break"

    def _on_lr(self, _e, open_: bool) -> str:
        iid = self.tree.focus()
        if iid in self._gid_cat and self.state != "SCANNING":
            self.tree.item(iid, open=open_)
            self._group_open[self._gid_cat[iid]] = open_
        return "break"

    def _on_copy(self, _e) -> str:
        sel = self.tree.selection()
        if sel:
            pkg = self.iid_pkg.get(sel[0])
            if pkg is not None:
                self._copy_text(pkg.packageIdentifier, f"已复制包 ID：{pkg.packageIdentifier}")
        return "break"

    def _on_menu(self, event) -> None:
        iid = self.tree.identify_row(event.y)
        if not iid: return
        pkg = self.iid_pkg.get(iid)
        if pkg is None: return
        self._menu_pkg = pkg
        self.tree.selection_set(iid); self.tree.focus(iid)
        try: self._menu.tk_popup(event.x_root, event.y_root)
        finally: self._menu.grab_release()

    def _copy_menu(self, which: str) -> None:
        pkg = self._menu_pkg
        if pkg is None: return
        if which == "id":
            self._copy_text(pkg.packageIdentifier, f"已复制包 ID：{pkg.packageIdentifier}")
        elif which == "name":
            self._copy_text(pkg.name, f"已复制软件名称：{pkg.name}")
        else:
            text = f"{pkg.name} ({pkg.packageIdentifier})"
            self._copy_text(text, f"已复制：{text}")

    def _copy_text(self, text: str, flash: str) -> None:
        self.clipboard_clear(); self.clipboard_append(text)
        self._flash_status(flash)

    # ── 批量勾选 ──
    def _set_all(self, value: bool) -> None:
        if self.state not in ("READY", "IMPORTING", "CANCELING"): return
        for p in self.packages: self.checked[p.packageIdentifier.lower()] = value
        self._render_tree(); self._save_selection()

    def _invert(self) -> None:
        if self.state not in ("READY", "IMPORTING", "CANCELING"): return
        for p in self.packages:
            key = p.packageIdentifier.lower()
            self.checked[key] = not self.checked.get(key, False)
        self._render_tree(); self._save_selection()

    def _apps_only(self) -> None:
        if self.state not in ("READY", "IMPORTING", "CANCELING"): return
        for p in self.packages:
            self.checked[p.packageIdentifier.lower()] = p.category != "运行库与组件"
        self._render_tree(); self._save_selection()

    def selected_packages(self) -> list:
        return [p for p in self.packages if self.checked.get(p.packageIdentifier.lower())]

    # ── 状态栏 ──
    def _update_status(self) -> None:
        total = len(self.packages)
        n = len(self.selected_packages())
        if n == 0:
            self._sel_zero.pack(side="left")
            for w in (self._sel_pre, self._sel_num, self._sel_post): w.pack_forget()
        else:
            self._sel_zero.pack_forget()
            self._sel_post.config(text=f" / {total} 项")
            self._sel_num.config(text=str(n))
            for w in (self._sel_pre, self._sel_num, self._sel_post): w.pack(side="left")
        self._set_enabled(self.export_btn, "primary",
            self.state in ("SCANNING", "SCAN_FAILED", "READY") and n > 0)
        if self.state == "SCANNING":
            return  # 扫描计时器负责状态文本
        if self.state == "IMPORTING":
            self.status_var.set(self._import_base); return
        if self.state == "CANCELING":
            self.status_var.set("正在取消导入…（winget 终止中）"); return
        kw = self.search_var.get().strip()
        if kw:
            self.status_var.set(f"匹配 {self._matched} / 共 {total} 项")
        elif self.snapshot:
            self.status_var.set(
                f"共 {total} 个可重装软件 · 扫描耗时 {self.snapshot.durationMs / 1000:.1f}s")

    def _flash_status(self, text: str) -> None:
        self.status_var.set(text)
        if self._flash_job:
            self.after_cancel(self._flash_job)

        def _restore():
            self._flash_job = None
            self._update_status()

        self._flash_job = self.after(2000, _restore)

    # ── 导出（自绘两段式对话框）──
    def on_export(self) -> None:
        selected = self.selected_packages()
        if not selected:
            messagebox.showwarning(APP_TITLE, "请先勾选需要保留的软件。", parent=self)  # 防御性
            return
        px = self._px
        dlg = tk.Toplevel(self); dlg.title("导出配置"); dlg.configure(bg=BORDER)
        dlg.transient(self); dlg.resizable(False, False)
        dlg.bind("<Escape>", lambda e: dlg.destroy())
        white = tk.Frame(dlg, bg=CARD)
        white.pack(fill="both", expand=True, padx=1, pady=1)
        content = tk.Frame(white, bg=CARD, width=px(470), height=px(250))
        content.pack(fill="both", expand=True, padx=px(24), pady=px(20))
        content.pack_propagate(False)
        # ── 第一段 ──
        btns = tk.Frame(content, bg=CARD); btns.pack(side="bottom", fill="x")
        cancel_b = self._make_button(btns, "取消", "secondary", dlg.destroy)
        cancel_b.pack(side="right")
        export_b = self._make_button(btns, "导出配置", "primary",
            lambda: self._do_export(dlg, content, name_var, entry_wrap, entry,
                                    entry_inner, hint, selected))
        export_b.pack(side="right", padx=(0, px(8)))
        tk.Label(content, bg=CARD, fg="#3A4454", font=F_BODY, justify="left",
                 text=f"将导出已勾选的 {len(selected)} 个软件包\n（两份 JSON：winget 原生格式 + 富 profile 格式）")\
            .pack(anchor="w", pady=(0, px(12)))
        tk.Label(content, text="配置名称", bg=CARD, fg=TEXT, font=F_BODY).pack(anchor="w")
        # 双层：外层 1px 状态边 + 内层 2px 焦点环
        entry_wrap = tk.Frame(content, bg="#D5DBE5")
        entry_wrap.pack(fill="x", pady=(px(4), 0))
        entry_inner = tk.Frame(entry_wrap, bg=CARD, highlightthickness=2,
                               highlightcolor=ACCENT, highlightbackground=CARD)
        entry_inner.pack(fill="x", padx=1, pady=1)
        name_var = tk.StringVar(value="我的软件配置")
        entry = tk.Entry(entry_inner, textvariable=name_var, font=F_BODY, bd=0,
                         relief="flat", bg=CARD, fg=TEXT, insertbackground=TEXT,
                         highlightthickness=0, cursor="xterm")
        entry.pack(fill="x", padx=px(8), ipady=px(5))
        hint = tk.Label(content, bg=CARD, fg=SUB, font=F_AUX, justify="left",
                        anchor="w", height=1)  # 固定高占位，防跳窗
        hint.pack(fill="x", pady=(px(2), 0))
        tk.Label(content, text=f"保存位置：{self.config.export_dir}", bg=CARD, fg=SUB,
                 font=F_AUX, justify="left", wraplength=px(470)).pack(anchor="w")
        entry.bind("<KeyRelease>",
            lambda e: self._validate_export_name(name_var, entry_wrap, entry, hint, export_b))
        entry.bind("<FocusIn>", lambda e: entry_inner.config(highlightbackground=ACCENT))
        entry.bind("<FocusOut>", lambda e: entry_inner.config(highlightbackground=CARD))
        self._validate_export_name(name_var, entry_wrap, entry, hint, export_b)
        entry.focus_set(); entry.select_range(0, "end")
        dlg.update_idletasks()
        w, h = dlg.winfo_reqwidth(), dlg.winfo_reqheight()
        dlg.geometry(f"+{self.winfo_x() + (self.winfo_width() - w) // 2}"
                     f"+{self.winfo_y() + (self.winfo_height() - h) // 2}")
        dlg.grab_set()

    def _validate_export_name(self, name_var, entry_wrap, entry, hint, btn) -> None:
        name = name_var.get().strip()
        btn.config(text="导出配置")
        if not name or any(ch in name for ch in '\\/:*?"<>|'):
            entry_wrap.config(bg=DANGER_T)
            entry.config(bg="#FDF2F2", fg=DANGER_T, insertbackground=DANGER_T)
            hint.config(text="配置名称不能为空" if not name
                        else "名称不能包含 \\ / : * ? \" < > | 字符", fg=DANGER_T)
            self._set_enabled(btn, "primary", False); return
        entry_wrap.config(bg="#D5DBE5")
        entry.config(bg=CARD, fg=TEXT, insertbackground=TEXT)
        try:
            dup = any(self.config.export_dir.glob(f"{name}*"))
        except Exception:  # noqa: BLE001
            dup = False
        if dup:
            entry_wrap.config(bg="#F5C34B")
            hint.config(text="同名配置将被覆盖", fg="#92400E")
            btn.config(text="覆盖导出")
        else:
            hint.config(text="", fg=SUB)
        self._set_enabled(btn, "primary", True)

    def _do_export(self, dlg, content, name_var, entry_wrap, entry, entry_inner,
                   hint, selected) -> None:
        name = name_var.get().strip()
        try:
            result = write_exports(self.config.export_dir, name, "", selected, self.snapshot,
                self.snapshot.wingetVersion if self.snapshot else "1.0.0")
        except AppError as exc:
            entry_wrap.config(bg=DANGER_T)
            entry.config(bg="#FDF2F2", fg=DANGER_T, insertbackground=DANGER_T)
            hint.config(text=exc.user_message, fg=DANGER_T); return
        except Exception as exc:  # noqa: BLE001
            entry_wrap.config(bg=DANGER_T)
            entry.config(bg="#FDF2F2", fg=DANGER_T, insertbackground=DANGER_T)
            hint.config(text=f"导出失败：{exc}", fg=DANGER_T); return
        files = [str(result.primary_path)]
        if result.winget_path: files.append(str(result.winget_path))
        self.log_append("done", f"已导出 {result.package_count} 个软件包：")
        for f in files: self.log_append("info", f"  {f}")
        # ── 第二段 ──
        for w in content.winfo_children(): w.destroy()
        open_var = {"v": True}
        close_b = self._make_button(content, "关闭", "secondary",
            lambda: self._close_export(dlg, open_var))
        close_b.pack(side="bottom", anchor="e", pady=(px(12), 0))
        check_lbl = tk.Label(content, bg=CARD, fg="#3A4454", font=F_BODY, cursor="hand2",
                             text="☑ 导出完成后打开文件夹")
        check_lbl.pack(anchor="w", pady=(px(12), 0))

        def _toggle_check(_e) -> None:
            open_var["v"] = not open_var["v"]
            check_lbl.config(text=("☑ " if open_var["v"] else "☐ ") + "导出完成后打开文件夹")

        check_lbl.bind("<Button-1>", _toggle_check)
        tk.Label(content, text=f"✓ 已导出 {result.package_count} 个软件包", bg=CARD,
                 fg=SUCCESS, font=F_SECTION).pack(anchor="w", pady=(0, px(8)))
        for f in files:
            tk.Label(content, text=Path(f).name, bg=CARD, fg="#3A4454", font=F_BODY).pack(anchor="w")

    def _close_export(self, dlg, open_var) -> None:
        dlg.destroy()
        if open_var["v"]:
            os.startfile(self.config.export_dir)  # noqa: S606

    # ── 导入 ──
    def on_import(self) -> None:
        if self.state in ("IMPORTING", "CANCELING"):
            messagebox.showinfo(APP_TITLE, "已有导入任务正在进行。", parent=self); return
        path = filedialog.askopenfilename(parent=self, title="选择配置文件",
            initialdir=str(self.config.export_dir),
            filetypes=[("JSON 配置", "*.json"), ("全部文件", "*.*")])
        if not path: return
        try: profile = load_profile(Path(path))
        except AppError as exc:
            messagebox.showerror(APP_TITLE, exc.user_message, parent=self); return
        except Exception as exc:  # noqa: BLE001
            messagebox.showerror(APP_TITLE, f"读取失败：{exc}", parent=self); return
        if not messagebox.askyesno(APP_TITLE,
            f"即将安装「{profile.profile_name}」中的 {len(profile.packages)} 个软件包。\n已安装的会自动跳过。是否继续？",
            parent=self): return
        self._start_import(profile)

    def _start_import(self, profile) -> None:
        self.importing = True; self.cancel_event.clear()
        self._import_base = f"正在导入「{profile.profile_name}」…"
        self._set_state("IMPORTING")
        self._update_import_counts({"success": 0, "skipped": 0, "failed": 0})
        self._progress_start()
        self.log_append("done", f"开始导入「{profile.profile_name}」，共 {len(profile.packages)} 个软件包")
        current = {"name": ""}; counts = {"success": 0, "failed": 0, "skipped": 0}

        def on_line(raw: str) -> None:
            line = _ANSI.sub("", raw).replace("\r", " ").strip()
            if not line or set(line) <= {"-", " ", "\\", "|", "/", "█", "░", "━"}:
                return
            if _FATAL.search(line): self.import_queue.put(("error", line)); return
            if _FOUND.search(line) and not _SUCCESS.search(line):
                current["name"] = line; self.import_queue.put(("info", line)); return
            if _SUCCESS.search(line):
                counts["success"] += 1; self.import_queue.put(("success", line, dict(counts))); return
            if _SKIP.search(line):
                counts["skipped"] += 1; self.import_queue.put(("skip", line, dict(counts))); return
            if _FAIL.search(line):
                counts["failed"] += 1; self.import_queue.put(("fail", line, dict(counts))); return
            self.import_queue.put(("log", line))

        def work() -> None:
            try:
                rc = self.repo.import_file(profile.path, on_line=on_line,
                    cancel_flag=lambda: self.cancel_event.is_set())
                self.import_queue.put(("finish", rc, dict(counts)))
            except Exception as exc:  # noqa: BLE001
                self.import_queue.put(("finish", -1, {"exception": str(exc), **counts}))

        threading.Thread(target=work, daemon=True).start()

    def on_cancel(self) -> None:
        if self.state != "IMPORTING": return
        self.cancel_event.set()
        self._set_state("CANCELING")
        self.log_append("info", "已请求取消，正在终止 winget 进程…")
        self._cancel_timer = self.after(10000,
            lambda: self.log_append("info", "取消耗时较长，可继续等待或直接关闭窗口"))

    def _update_import_counts(self, c: dict) -> None:
        self._imp_s.config(text=f"成功 {c.get('success', 0)}")
        self._imp_k.config(text=f"跳过 {c.get('skipped', 0)}")
        self._imp_f.config(text=f"失败 {c.get('failed', 0)}")

    def _poll(self) -> None:
        try:
            while True:
                item = self.scan_queue.get_nowait()
                self._scan_tick_stop(); self._progress_stop()
                kind, payload = item[0], item[1]
                if kind == "ok":
                    self._apply_snapshot(payload)
                    self._import_summary = ""
                    self._set_state("READY")
                else:
                    self._set_state("SCAN_FAILED")
                    self.status_var.set("扫描失败")
                    self.log_append("error", f"扫描失败：{payload}")
        except queue.Empty: pass
        try:
            while True:
                item = self.import_queue.get_nowait()
                kind = item[0]
                if kind == "finish":
                    self._finish_import(item)
                else:
                    tag = kind if kind in ("success", "skip", "fail", "error") else \
                        "info" if kind == "info" else "log"
                    counts = item[2] if len(item) > 2 and isinstance(item[2], dict) else None
                    if counts and "exception" not in counts:
                        self._update_import_counts(counts)
                        extra = f"  [成功{counts['success']} 跳过{counts['skipped']} 失败{counts['failed']}]"
                    else:
                        extra = ""
                    self.log_append(tag, item[1] + extra)
        except queue.Empty: pass
        self._poll_job = self.after(120, self._poll)

    def _finish_import(self, item) -> None:
        if self._cancel_timer:
            self.after_cancel(self._cancel_timer); self._cancel_timer = None
        self.importing = False
        rc = item[1]; counts = item[2]
        self._progress_stop()
        if "exception" in counts:
            self._set_state("READY")
            self.log_append("error", f"导入中断：{counts['exception']}")
            self.status_var.set("导入中断")
            messagebox.showerror(APP_TITLE, f"导入中断：{counts['exception']}", parent=self)
            return
        summary = (f"导入完成：成功 {counts['success']}、跳过 {counts['skipped']}、失败 {counts['failed']}"
            if rc == 0 else
            f"winget 退出码 {rc}：成功 {counts['success']}、跳过 {counts['skipped']}、失败 {counts['failed']}")
        self.log_append("done", summary)
        self._import_summary = f"已导入：成功 {counts['success']} 跳过 {counts['skipped']} 失败 {counts['failed']}"
        self._set_state("READY")
        self.status_var.set(summary)
        self.rescan()  # 自动重扫描

    # ── 日志 ──
    def log_append(self, tag: str, text: str) -> None:
        at_end = self.log.yview()[1] >= 0.99  # 用户上滚查阅时不打断
        self.log.config(state="normal")
        try:
            ts = self.tk.call("clock", "format", self.tk.call("clock", "seconds"),
                              "-format", "%H:%M:%S")
        except Exception:  # noqa: BLE001
            ts = ""
        if ts: self.log.insert("end", ts + "  ", "ts")
        self.log.insert("end", f"{text}\n", tag if tag != "log" else ())
        self.log.config(state="disabled")
        if at_end: self.log.see("end")

    def _clear_log(self) -> None:
        self.log.config(state="normal"); self.log.delete("1.0", "end")
        self.log.config(state="disabled")

    # ── 勾选持久化（300ms debounce）──
    def _save_selection(self) -> None:
        if self._save_job:
            self.after_cancel(self._save_job)
        self._save_job = self.after(300, self._flush_selection)

    def _flush_selection(self) -> None:
        if self._save_job:
            self.after_cancel(self._save_job); self._save_job = None
        try:
            PERSIST_DIR.mkdir(parents=True, exist_ok=True)
            SELECTION_FILE.write_text(
                json.dumps(self.checked, ensure_ascii=False), encoding="utf-8")
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
    def _on_close(self) -> None:
        if self.state in ("IMPORTING", "CANCELING"):
            if messagebox.askyesno(APP_TITLE, "导入尚未完成，确定取消并关闭吗？", parent=self):
                self.cancel_event.set()
                self._teardown_timers()
                self._flush_selection()
                self.after(200, self.destroy)
            return
        self._teardown_timers()
        self._flush_selection()
        self.destroy()

    def _teardown_timers(self) -> None:
        """退出前取消全部 after 回调，防止对已销毁 widget 配置。"""
        self._progress_stop(); self._scan_tick_stop()
        for job in (self._flash_job, self._cancel_timer, self._poll_job, self._fade_job):
            if job:
                try: self.after_cancel(job)
                except Exception: pass  # noqa: BLE001
        self._flash_job = self._cancel_timer = self._poll_job = self._fade_job = None


def main() -> int:
    try:
        import ctypes; ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:  # noqa: BLE001
        pass
    try: app = App()
    except ConfigError as exc:
        root = tk.Tk(); root.withdraw(); messagebox.showerror(APP_TITLE, str(exc)); return 1
    app.mainloop(); return 0


if __name__ == "__main__":
    sys.exit(main())
