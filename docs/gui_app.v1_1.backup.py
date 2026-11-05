"""Winget 软件管家 - 独立桌面版（Tkinter，无浏览器、无第三方依赖）。

用法：
    python gui_app.py           # 开发运行
    打包：pyinstaller 打包为单个 exe（见 build_exe.bat）

功能：
    1. 自动枚举 winget 已安装软件（只显示可重装的包，MSIX/注册表项自动隐藏）；
    2. 勾选需要保留/重装的软件，导出 JSON（winget 原生格式 + 富 profile 格式）；
    3. 重装系统后选择 JSON 一键批量恢复安装，实时日志输出。

UI 设计：Win11 Fluent 浅色，遵循 docs/visual-spec-v1.md (v1.1) 与 docs/UX_SPEC_v1.md。
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

# ── 色板（视觉规范 v1.1）──
G0 = "#FFFFFF"; G1 = "#FAFBFC"; G2 = "#F4F6F8"; G3 = "#EDEFF3"; G4 = "#E1E5EA"
G5 = "#CFD5DC"; G6 = "#A8B0BB"; G7 = "#6B7480"; G8 = "#4A525C"; G9 = "#2A3038"
G10 = "#16191D"
B50 = "#EFF6FF"; B100 = "#DBEAFE"; B200 = "#BFDBFE"; B300 = "#93C5FD"
B600 = "#2563EB"; B700 = "#1D4ED8"; B800 = "#1E40AF"
SUCCESS = "#166534"; DANGER_T = "#B91C1C"; DANGER = "#DC2626"
WARN_T = "#92400E"; WARN_BORDER = "#FCD34D"; DANGER_BG = "#FEF2F2"

F_BODY = (FONT_NAME, 10); F_TITLE = (FONT_NAME, 12, "bold")
F_SECTION = (FONT_NAME, 10, "bold"); F_AUX = (FONT_NAME, 9)
F_HEAD = (FONT_NAME, 9, "bold"); F_MONO = ("Consolas", 9)

# 按钮 kind → 外观。hb = 外层 1px 常驻描边色（由父级 Frame.bg 绘制）；
# 内层 2px 焦点环由 highlightthickness=2 绘制（厚度恒定只切颜色）。
BTN_KINDS = {
    "primary":      dict(bg=B600,   fg="#ffffff", hover=B700,     hb=B600,   padx=14, font=F_BODY),
    "secondary":    dict(bg=G0,     fg=G8,        hover=G2,       hb=G5,     padx=14, font=F_BODY),
    "ghost":        dict(bg=G2,     fg=G8,        hover=G3,       hb=G2,     padx=8,  font=(FONT_NAME, 9, "bold")),
    "danger_ghost": dict(bg=G2,     fg=DANGER_T,  hover=DANGER_BG, hb=G2,    padx=10, font=F_BODY),
    "danger_solid": dict(bg=DANGER, fg="#ffffff", hover=DANGER_T,  hb=DANGER, padx=12, font=F_BODY),
}


def build_config() -> Config:
    winget_exe = find_winget()
    return Config(host="127.0.0.1", port=0, export_dir=BASE_DIR / "exports",
        winget_exe=winget_exe, cache_ttl=60, command_timeout=300,
        open_browser=False, log_level="INFO", log_file=None, extra={})


class App(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title(APP_TITLE); self.geometry("1080x760"); self.minsize(960, 620)
        self.configure(bg=G2)
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
        self._import_summary = ""
        self._import_base = ""
        self._scan_after = None; self._scan_tenths = 0
        self._prog_after = None; self._prog_active = False; self._prog_x = 0
        self._save_job = None; self._flash_job = None; self._cancel_timer = None
        self._poll_job = None
        self._setup_style(); self._build_ui(); self._load_selection()
        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self._bind_keys()
        self.search_entry.focus_set()
        self._poll_job = self.after(100, self._poll); self.rescan()

    # ── DPI 换算（root 已建，勿再手动设 tk scaling，避免双重缩放）──
    def _px(self, n: int) -> int:
        return round(n * self.winfo_fpixels("1i") / 96)

    # ── 按钮工厂：外层 1px 描边 Frame + 内层 2px 焦点环（highlightthickness 恒定）──
    def _make_button(self, parent, text: str, kind: str, command) -> tk.Frame:
        k = BTN_KINDS[kind]
        wrap = tk.Frame(parent, bg=k["hb"])  # 外层 1px 语义描边
        btn = tk.Button(wrap, text=text, command=command, bd=0, relief="flat",
                        font=k["font"], cursor="hand2", highlightthickness=2,
                        highlightcolor=B600, highlightbackground=k["bg"],
                        padx=self._px(k["padx"]), pady=self._px(7), takefocus=1)
        btn.pack(padx=1, pady=1)
        wrap._btn = btn; wrap._kind = kind

        def _enter(_e):
            if str(btn["state"]) == "normal":  # 禁用态不被 hover 点亮
                btn.config(bg=k["hover"])

        def _leave(_e):
            self._set_enabled(wrap, kind, str(btn["state"]) == "normal")

        btn.bind("<Enter>", _enter); btn.bind("<Leave>", _leave)
        self._set_enabled(wrap, kind, True)
        return wrap

    def _set_enabled(self, wrap: tk.Frame, kind: str, enabled: bool) -> None:
        btn = wrap._btn
        if enabled:
            k = BTN_KINDS[kind]
            wrap.config(bg=k["hb"])
            btn.config(state="normal", bg=k["bg"], fg=k["fg"],
                       disabledforeground=k["fg"], highlightbackground=k["bg"],
                       cursor="hand2")
        else:
            # 禁用态统一：文字 G6 / 底 G1 / 描边 G3，不区分级别
            wrap.config(bg=G3)
            btn.config(state="disabled", bg=G1, fg=G6, disabledforeground=G6,
                       highlightbackground=G1, cursor="arrow")

    def _setup_style(self) -> None:
        style = ttk.Style(self)
        try: style.theme_use("vista")  # 必须在建任何 widget 之前
        except tk.TclError: pass
        style.configure(".", font=F_BODY, background=G2, foreground=G9)
        style.layout("Treeview", [("Treeview.treearea", {"sticky": "nswe"})])
        style.configure("Treeview", font=F_BODY, rowheight=self._px(28),
                        background=G0, fieldbackground=G0, borderwidth=0)
        style.configure("Treeview.Heading", font=F_HEAD, background=G3,
                        foreground=G8, padding=(10, 8), borderwidth=0, relief="flat")
        # 选中行恒 B100 / G9；数据行 tag 只设 foreground，不设 background
        style.map("Treeview", background=[("selected", B100)],
                  foreground=[("selected", G9)])

    # ── UI 构建 ──
    def _build_ui(self) -> None:
        px = self._px
        # 顶栏 h56 / G0
        top = tk.Frame(self, bg=G0, height=px(56)); top.pack(fill="x")
        top.pack_propagate(False)
        tk.Label(top, text=APP_TITLE, bg=G0, fg=G10, font=F_TITLE).pack(side="left", padx=(px(16), 0))
        self.winget_label = tk.Label(top, text="", bg=G2, fg=G7, font=F_AUX, padx=px(8), pady=2)
        self.winget_label.pack(side="left", padx=(px(12), 0))
        tk.Frame(self, bg=G4, height=1).pack(fill="x")

        # 工具栏 h52 / G2
        bar = tk.Frame(self, bg=G2, height=px(52)); bar.pack(fill="x")
        bar.pack_propagate(False)
        # 搜索框（先创建，Tab 序靠前）：外层 1px G5 描边 Frame + 内层 2px 焦点环
        self.search_var = tk.StringVar()
        self.search_var.trace_add("write", self._on_search_changed)
        outer = tk.Frame(bar, bg=G5, width=px(220), height=px(32))
        outer.pack_propagate(False)
        inner = tk.Frame(outer, bg=G0, highlightthickness=2,
                         highlightcolor=B600, highlightbackground=G0)
        inner.pack(fill="both", expand=True, padx=1, pady=1)
        self.search_entry = tk.Entry(inner, textvariable=self.search_var, bd=0, relief="flat",
            bg=G0, fg=G9, font=F_BODY, insertbackground=G9, highlightthickness=0,
            cursor="xterm", takefocus=1)
        self.search_entry.pack(side="left", fill="both", expand=True, padx=(px(8), 0))
        # 内嵌清空钮：tk.Label + place 20×20，「×」U+00D7 12pt，不参与 Tab 序
        self._clear_btn = tk.Label(inner, text="×", bg=G0, fg=G6, font=(FONT_NAME, 12),
                                   cursor="hand2", takefocus=0)
        self._clear_btn.place(relx=1.0, rely=0.5, anchor="e", x=-px(2),
                              width=px(20), height=px(20))
        self._clear_btn.bind("<Enter>", lambda e: self._clear_btn.config(bg=G3))
        self._clear_btn.bind("<Leave>", lambda e: self._clear_btn.config(bg=G0))
        self._clear_btn.bind("<Button-1>", self._on_clear_search)
        self._ph = tk.Label(inner, text="搜索名称或包 ID", bg=G0, fg=G7, font=F_BODY, takefocus=0)
        self._search_inner = inner
        self.search_entry.bind("<FocusIn>", self._on_search_focus_in)
        self.search_entry.bind("<FocusOut>", self._on_search_focus_out)
        self.search_entry.bind("<Escape>", self._on_search_esc)
        # 左组：重新扫描
        self.scan_btn = self._make_button(bar, "重新扫描", "secondary", self.rescan)
        self.scan_btn.pack(side="left", padx=(px(16), 0))
        outer.pack(side="left", padx=(px(8), 0))
        # 1px 竖线
        tk.Frame(bar, bg=G4, width=1, height=px(20)).pack(side="left", padx=(px(16), px(16)))
        # 中组：4 个 Ghost（SCANNING 下仅这 4 个禁用，搜索框保持可用）
        self._bulk_btns = []
        for text, cmd in (("全选", lambda: self._set_all(True)),
                          ("全不选", lambda: self._set_all(False)),
                          ("反选", self._invert), ("取消运行库", self._apps_only)):
            b = self._make_button(bar, text, "ghost", cmd)
            b.pack(side="left", padx=(0, px(8)))
            self._bulk_btns.append(b)
        # spacer
        tk.Frame(bar, bg=G2).pack(side="left", fill="both", expand=True)
        # 右组（pack side=right 时先 pack 的在最右）
        self.export_btn = self._make_button(bar, "导出配置", "primary", self.on_export)
        self.export_btn.pack(side="right", padx=(0, px(16)))
        self.import_btn = self._make_button(bar, "导入恢复…", "secondary", self.on_import)
        self.import_btn.pack(side="right", padx=(0, px(8)))
        self.cancel_btn = self._make_button(bar, "取消导入", "danger_ghost", self.on_cancel)
        self.cancel_btn.pack(side="right", padx=(0, px(8)))
        self._set_enabled(self.cancel_btn, "danger_ghost", False)

        # 状态栏（先 pack 到底部，进度条才能压在其上方）
        self._status_bar = tk.Frame(self, bg=G0, height=px(34))
        self._status_bar.pack(fill="x", side="bottom")
        self._status_bar.pack_propagate(False)
        tk.Frame(self, bg=G4, height=1).pack(fill="x", side="bottom")
        self.status_var = tk.StringVar(value="正在扫描已安装软件…")
        tk.Label(self._status_bar, textvariable=self.status_var, bg=G0, fg=G8,
                 font=F_AUX).pack(side="left", padx=(px(16), 0))
        # 右侧：勾选计数（默认）
        self._sel_frame = tk.Frame(self._status_bar, bg=G0)
        self._sel_zero = tk.Label(self._sel_frame, text="已勾选 0 项 · 勾选后点「导出配置」",
                                  bg=G0, fg=G7, font=F_AUX)
        self._sel_pre = tk.Label(self._sel_frame, text="已勾选 ", bg=G0, fg=G8, font=F_AUX)
        self._sel_num = tk.Label(self._sel_frame, text="0", bg=G0, fg=B600,
                                 font=(FONT_NAME, 10, "bold"))
        self._sel_post = tk.Label(self._sel_frame, text=" / 0 项", bg=G0, fg=G8, font=F_AUX)
        self._sel_frame.pack(side="right", padx=(0, px(16)))
        # 右侧：导入三色计数
        self._imp_frame = tk.Frame(self._status_bar, bg=G0)
        self._imp_s = tk.Label(self._imp_frame, text="成功 0", bg=G0, fg=SUCCESS, font=F_AUX)
        self._imp_s1 = tk.Label(self._imp_frame, text=" · ", bg=G0, fg=G7, font=F_AUX)
        self._imp_k = tk.Label(self._imp_frame, text="跳过 0", bg=G0, fg=G7, font=F_AUX)
        self._imp_s2 = tk.Label(self._imp_frame, text=" · ", bg=G0, fg=G7, font=F_AUX)
        self._imp_f = tk.Label(self._imp_frame, text="失败 0", bg=G0, fg=DANGER_T, font=F_AUX)
        for w in (self._imp_s, self._imp_s1, self._imp_k, self._imp_s2, self._imp_f):
            w.pack(side="left")
        # 进度条（3px，自绘，默认隐藏）
        self._prog_track = tk.Frame(self, bg=G3, height=3)
        self._prog_track.pack_propagate(False)
        self._prog_slider = tk.Frame(self._prog_track, bg=B600, width=px(140), height=3)

        # 主体：列表 + 日志
        body = tk.Frame(self, bg=G2); body.pack(fill="both", expand=True, padx=px(16))
        tree_wrap = tk.Frame(body, bg=G0, highlightthickness=1, highlightbackground=G4)
        tree_wrap.pack(fill="both", expand=True, pady=(px(12), 0))
        # 顶部焦点条：pack 真实占位 2px（不用 place，避免压住表头）
        self._tree_focusbar = tk.Frame(tree_wrap, bg=G4, height=2)
        self._tree_focusbar.pack(side="top", fill="x")
        columns = ("name", "id", "ver", "cat")
        self.tree = ttk.Treeview(tree_wrap, columns=columns, show="tree headings",
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
        vsb = ttk.Scrollbar(tree_wrap, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=vsb.set)
        vsb.pack(side="right", fill="y")
        self.tree.pack(side="left", fill="both", expand=True)
        self.tree.tag_configure("on", foreground=G9)
        self.tree.tag_configure("off", foreground=G8)
        self.tree.tag_configure("group", foreground=G9, background=G2, font=F_SECTION)
        self.tree.bind("<FocusIn>", lambda e: self._tree_focusbar.config(bg=B600))
        self.tree.bind("<FocusOut>", lambda e: self._tree_focusbar.config(bg=G4))
        self._empty = tk.Label(tree_wrap, bg=G0, fg=G7, font=F_BODY, justify="center")
        # 右键菜单
        self._menu = tk.Menu(self, tearoff=0)
        self._menu.add_command(label="复制包 ID", command=lambda: self._copy_menu("id"))
        self._menu.add_command(label="复制软件名称", command=lambda: self._copy_menu("name"))
        self._menu.add_command(label="复制「名称 (包ID)」", command=lambda: self._copy_menu("both"))
        self._menu_pkg = None

        # 日志区（固定高约 148）
        logwrap = tk.Frame(body, bg=G0, highlightthickness=1, highlightbackground=G4)
        logwrap.pack(fill="x", pady=(px(8), px(12)))
        head = tk.Frame(logwrap, bg=G0, height=px(32)); head.pack(fill="x")
        head.pack_propagate(False)
        tk.Label(head, text="导入 / 运行日志", bg=G0, fg=G9, font=F_SECTION).pack(side="left", padx=(px(12), 0))
        clear_lbl = tk.Label(head, text="清空", bg=G0, fg=G7, font=F_AUX, cursor="hand2")
        clear_lbl.pack(side="right", padx=(0, px(12)))
        clear_lbl.bind("<Enter>", lambda e: clear_lbl.config(fg=G9))
        clear_lbl.bind("<Leave>", lambda e: clear_lbl.config(fg=G7))
        clear_lbl.bind("<Button-1>", lambda e: self._clear_log())
        tk.Frame(logwrap, bg=G4, height=1).pack(fill="x")
        self.log = tk.Text(logwrap, height=6, bg=G1, bd=0, relief="flat",
                           padx=px(12), pady=px(8), font=F_BODY, wrap="word",
                           spacing1=2, spacing3=3, selectbackground=B100,
                           state="disabled", takefocus=0)
        self.log.pack(fill="x")
        for tag, kw in (("ts", dict(font=F_MONO, foreground=G7)),
                        ("info", dict(foreground=G8)),
                        ("success", dict(foreground=SUCCESS)),
                        ("skip", dict(foreground=G7)),
                        ("fail", dict(foreground=DANGER_T)),
                        ("error", dict(foreground=DANGER_T, background=DANGER_BG)),
                        ("done", dict(foreground=B800, font=(FONT_NAME, 10, "bold")))):
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
        self._refresh_search_decor()

    # ── 键盘 ──
    def _bind_keys(self) -> None:
        self.bind("<Control-f>", self._on_ctrl_f)
        self.bind_all("<Control-a>", self._on_ctrl_a)

    def _on_ctrl_f(self, _e) -> str:
        self.search_entry.focus_set()
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
    def _refresh_search_decor(self) -> None:
        val = self.search_var.get()
        focused = self.focus_get() is self.search_entry
        if val:
            self._clear_btn.place(relx=1.0, rely=0.5, anchor="e", x=-self._px(2),
                                  width=self._px(20), height=self._px(20))
            self._ph.place_forget()
        else:
            self._clear_btn.place_forget()
            if focused:
                self._ph.place_forget()
            else:
                self._ph.place(x=self._px(9), rely=0.5, anchor="w")

    def _on_search_changed(self, *_):
        self._refresh_search_decor()
        self._render_tree()

    def _on_search_focus_in(self, _e=None) -> None:
        self._search_inner.config(highlightbackground=B600)  # 2px 焦点环，厚度不变
        self._refresh_search_decor()
        if self.search_var.get():
            self.search_entry.select_range(0, "end")

    def _on_search_focus_out(self, _e=None) -> None:
        self._search_inner.config(highlightbackground=G0)
        self._refresh_search_decor()

    def _on_search_esc(self, _e=None) -> str:
        self.search_var.set("")
        self.tree.focus_set()
        return "break"

    def _on_tree_esc(self, _e=None) -> str:
        self.search_var.set("")
        return "break"

    def _on_clear_search(self, _e=None) -> None:
        self.search_var.set("")
        self.search_entry.focus_set()

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
        self.winget_label.config(text="正在扫描…" if s == "SCANNING" else self._chip_text)
        if s in ("IMPORTING", "CANCELING"):
            self._sel_frame.pack_forget()
            self._imp_frame.pack(side="right", padx=(0, self._px(16)))
        else:
            self._imp_frame.pack_forget()
            self._sel_frame.pack(side="right", padx=(0, self._px(16)))
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
            for pkg in items:
                on = self.checked.get(pkg.packageIdentifier.lower(), False)
                iid = tree.insert(gid, "end", text="☑" if on else "☐",
                    values=(pkg.name, pkg.packageIdentifier, pkg.version or "-", pkg.category),
                    tags=("on",) if on else ("off",))
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
                self._empty.config(text="正在扫描已安装软件，约需 5~20 秒…")
            elif self.state == "SCAN_FAILED":
                self._empty.config(text="扫描失败：winget 未安装或不可用\n可点击上方「重新扫描」重试")
            elif self.state == "READY":
                self._empty.config(text="未发现可重装的软件")
            else:
                show = False
        elif kw and self._matched == 0:
            self._empty.config(text=f"未找到与『{kw}』匹配的软件")
        else:
            show = False
        if show:
            self._empty.place(relx=0.5, rely=0.45, anchor="center")
        else:
            self._empty.place_forget()

    # ── Treeview 交互 ──
    def _toggle_row(self, iid: str) -> None:
        pkg = self.iid_pkg.get(iid)
        if pkg is not None:
            key = pkg.packageIdentifier.lower()
            on = not self.checked.get(key, False)
            self.checked[key] = on
            self.tree.item(iid, text="☑" if on else "☐",
                           tags=("on",) if on else ("off",))
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
                                   tags=("on",) if target else ("off",))
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

    def _on_motion(self, event) -> None:
        iid = self.tree.identify_row(event.y)
        cur = "hand2" if iid in self._gid_cat else "arrow"
        if cur != self._cursor:  # 值不变不 config，避免 Motion 高频重绘卡顿
            self._cursor = cur
            self.tree.config(cursor=cur)

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

    # ── 导出（自绘两段式对话框，宽 520，双层描边，固定高度容器防跳窗）──
    def on_export(self) -> None:
        selected = self.selected_packages()
        if not selected:
            messagebox.showwarning(APP_TITLE, "请先勾选需要保留的软件。", parent=self)  # 防御性，理论不可达
            return
        px = self._px
        dlg = tk.Toplevel(self); dlg.title("导出配置"); dlg.configure(bg=G4)  # 1px 外描边
        dlg.transient(self); dlg.resizable(False, False)
        dlg.bind("<Escape>", lambda e: dlg.destroy())
        white = tk.Frame(dlg, bg=G0)
        white.pack(fill="both", expand=True, padx=1, pady=1)
        content = tk.Frame(white, bg=G0, width=px(470), height=px(250))
        content.pack(fill="both", expand=True, padx=px(24), pady=px(20))
        content.pack_propagate(False)
        # ── 第一段 ──
        btns = tk.Frame(content, bg=G0); btns.pack(side="bottom", fill="x")
        cancel_b = self._make_button(btns, "取消", "secondary", dlg.destroy)
        cancel_b.pack(side="right")
        export_b = self._make_button(btns, "导出配置", "primary",
            lambda: self._do_export(dlg, content, name_var, entry_wrap, entry,
                                    entry_inner, hint, selected))
        export_b.pack(side="right", padx=(0, px(8)))
        tk.Label(content, bg=G0, fg=G8, font=F_BODY, justify="left",
                 text=f"将导出已勾选的 {len(selected)} 个软件包\n（两份 JSON：winget 原生格式 + 富 profile 格式）")\
            .pack(anchor="w", pady=(0, px(12)))
        tk.Label(content, text="配置名称", bg=G0, fg=G9, font=F_BODY).pack(anchor="w")
        # 双层：外层 1px 状态边（错误红/警告琥珀/常驻 G5）+ 内层 2px 焦点环
        entry_wrap = tk.Frame(content, bg=G5)
        entry_wrap.pack(fill="x", pady=(px(4), 0))
        entry_inner = tk.Frame(entry_wrap, bg=G0, highlightthickness=2,
                               highlightcolor=B600, highlightbackground=G0)
        entry_inner.pack(fill="x", padx=1, pady=1)
        name_var = tk.StringVar(value="我的软件配置")
        entry = tk.Entry(entry_inner, textvariable=name_var, font=F_BODY, bd=0,
                         relief="flat", bg=G0, fg=G9, insertbackground=G9,
                         highlightthickness=0, cursor="xterm")
        entry.pack(fill="x", padx=px(8), ipady=px(5))
        hint = tk.Label(content, bg=G0, fg=G7, font=F_AUX, justify="left",
                        anchor="w", height=1)  # 固定高 20 占位，防跳窗
        hint.pack(fill="x", pady=(px(2), 0))
        tk.Label(content, text=f"保存位置：{self.config.export_dir}", bg=G0, fg=G7,
                 font=F_AUX, justify="left", wraplength=px(470)).pack(anchor="w")
        entry.bind("<KeyRelease>",
            lambda e: self._validate_export_name(name_var, entry_wrap, entry, hint, export_b))
        entry.bind("<FocusIn>", lambda e: entry_inner.config(highlightbackground=B600))
        entry.bind("<FocusOut>", lambda e: entry_inner.config(highlightbackground=G0))
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
            # 错误态：外框红 + 底淡红 + 文字红（区别于警告态）
            entry_wrap.config(bg=DANGER_T)
            entry.config(bg=DANGER_BG, fg=DANGER_T, insertbackground=DANGER_T)
            hint.config(text="配置名称不能为空" if not name
                        else "名称不能包含 \\ / : * ? \" < > | 字符", fg=DANGER_T)
            self._set_enabled(btn, "primary", False); return
        entry_wrap.config(bg=G5)
        entry.config(bg=G0, fg=G9, insertbackground=G9)
        try:
            dup = any(self.config.export_dir.glob(f"{name}*"))
        except Exception:  # noqa: BLE001
            dup = False
        if dup:
            # 警告态：外框琥珀、底保持白、文字 #92400E
            entry_wrap.config(bg=WARN_BORDER)
            hint.config(text="同名配置将被覆盖", fg=WARN_T)
            btn.config(text="覆盖导出")
        else:
            hint.config(text="", fg=G7)
        self._set_enabled(btn, "primary", True)

    def _do_export(self, dlg, content, name_var, entry_wrap, entry, entry_inner,
                   hint, selected) -> None:
        name = name_var.get().strip()
        try:
            result = write_exports(self.config.export_dir, name, "", selected, self.snapshot,
                self.snapshot.wingetVersion if self.snapshot else "1.0.0")
        except AppError as exc:
            entry_wrap.config(bg=DANGER_T)
            entry.config(bg=DANGER_BG, fg=DANGER_T, insertbackground=DANGER_T)
            hint.config(text=exc.user_message, fg=DANGER_T); return
        except Exception as exc:  # noqa: BLE001
            entry_wrap.config(bg=DANGER_T)
            entry.config(bg=DANGER_BG, fg=DANGER_T, insertbackground=DANGER_T)
            hint.config(text=f"导出失败：{exc}", fg=DANGER_T); return
        files = [str(result.primary_path)]
        if result.winget_path: files.append(str(result.winget_path))
        self.log_append("done", f"已导出 {result.package_count} 个软件包：")
        for f in files: self.log_append("info", f"  {f}")
        # ── 第二段（同一固定高度容器内原位切换）──
        for w in content.winfo_children(): w.destroy()
        open_var = {"v": True}
        close_b = self._make_button(content, "关闭", "secondary",
            lambda: self._close_export(dlg, open_var))
        close_b.pack(side="bottom", anchor="e", pady=(px(12), 0))
        check_lbl = tk.Label(content, bg=G0, fg=G8, font=F_BODY, cursor="hand2",
                             text="☑ 导出完成后打开文件夹")
        check_lbl.pack(anchor="w", pady=(px(12), 0))

        def _toggle_check(_e) -> None:
            open_var["v"] = not open_var["v"]
            check_lbl.config(text=("☑ " if open_var["v"] else "☐ ") + "导出完成后打开文件夹")

        check_lbl.bind("<Button-1>", _toggle_check)
        tk.Label(content, text=f"✓ 已导出 {result.package_count} 个软件包", bg=G0,
                 fg=SUCCESS, font=F_SECTION).pack(anchor="w", pady=(0, px(8)))
        for f in files:
            tk.Label(content, text=Path(f).name, bg=G0, fg=G8, font=F_BODY).pack(anchor="w")

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
        self.rescan()  # 自动重扫描，状态栏显示「已导入：… · 正在扫描 · 已用 X.Xs」

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
        for job in (self._flash_job, self._cancel_timer, self._poll_job):
            if job:
                try: self.after_cancel(job)
                except Exception: pass  # noqa: BLE001
        self._flash_job = self._cancel_timer = self._poll_job = None


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
