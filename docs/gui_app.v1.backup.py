"""Winget 软件管家 - 独立桌面版（Tkinter，无浏览器、无第三方依赖）。

用法：
    python gui_app.py           # 开发运行
    打包：pyinstaller 打包为单个 exe（见 build_exe.bat）

功能：
    1. 自动枚举 winget 已安装软件（只显示可重装的包，MSIX/注册表项自动隐藏）；
    2. 勾选需要保留/重装的软件，导出 JSON（winget 原生格式 + 富 profile 格式）；
    3. 重装系统后选择 JSON 一键批量恢复安装，实时日志输出。
"""

from __future__ import annotations

import queue
import re
import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import messagebox, simpledialog, ttk

# 让 PyInstaller 打包后在 exe 目录读写，而不是临时目录
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

# ── 主题 ─────────────────────────────────────────────────────────
FONT_NAME = "HarmonyOS Sans SC"
try:  # 用户机器装有鸿蒙字体则优先使用
    from tkinter import font as tkfont  # noqa: F401

    _probe = tk.Tk()
    _probe.withdraw()
    HAS_FONT = FONT_NAME in tkfont.families(_probe)
    _probe.destroy()
except Exception:  # noqa: BLE001
    HAS_FONT = False
if not HAS_FONT:
    FONT_NAME = "Microsoft YaHei UI"

BG = "#f5f6f8"
PANEL = "#ffffff"
FG = "#1f2328"
MUTED = "#6b7280"
ACCENT = "#2563eb"
ACCENT_DARK = "#1d4ed8"
BORDER = "#d9dce1"
GREEN = "#16a34a"
RED = "#dc2626"
PURPLE = "#7c3aed"


def build_config() -> Config:
    winget_exe = find_winget()
    return Config(
        host="127.0.0.1",
        port=0,
        export_dir=BASE_DIR / "exports",
        winget_exe=winget_exe,
        cache_ttl=60,
        command_timeout=300,
        open_browser=False,
        log_level="INFO",
        log_file=None,
        extra={},
    )


class App(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title(APP_TITLE)
        self.geometry("980x680")
        self.minsize(820, 560)
        self.configure(bg=BG)

        self.config = build_config()
        self.repo = WingetRepository(self.config)
        self.catalog = CatalogService(self.config, self.repo)

        self.snapshot = None
        self.packages: list = []  # 仅可重装包
        self.checked: dict[str, bool] = {}  # pid_lower -> bool
        self.iid_pkg: dict[str, object] = {}  # tree iid -> package
        self.group_iids: dict[str, str] = {}  # category -> tree iid
        self.scan_queue: queue.Queue = queue.Queue()
        self.import_queue: queue.Queue = queue.Queue()
        self.cancel_event = threading.Event()
        self.importing = False

        self._setup_style()
        self._build_ui()
        self._load_selection()
        self.after(100, self._poll)
        self.rescan()

    # ── 样式 ────────────────────────────────────────────────────
    def _setup_style(self) -> None:
        style = ttk.Style(self)
        try:
            style.theme_use("vista")
        except tk.TclError:
            pass
        base = (FONT_NAME, 10)
        style.configure(".", font=base, background=BG, foreground=FG)
        style.configure("TButton", padding=(10, 5))
        style.configure("Accent.TButton", foreground="#ffffff", background=ACCENT)
        style.configure("TEntry", padding=3)
        style.configure("Treeview", font=base, rowheight=26, background=PANEL,
                        fieldbackground=PANEL, borderwidth=0)
        style.configure("Treeview.Heading", font=(FONT_NAME, 10, "bold"),
                        background="#eef0f3", padding=6)
        style.map("Treeview", background=[("selected", "#dbeafe")],
                  foreground=[("selected", FG)])

    # ── 界面 ────────────────────────────────────────────────────
    def _build_ui(self) -> None:
        # 顶栏
        top = tk.Frame(self, bg=PANEL, padx=12, pady=10)
        top.pack(fill="x")
        tk.Label(top, text=APP_TITLE, bg=PANEL, fg=FG,
                 font=(FONT_NAME, 14, "bold")).pack(side="left")
        self.winget_label = tk.Label(top, text="", bg=PANEL, fg=MUTED, font=(FONT_NAME, 9))
        self.winget_label.pack(side="left", padx=(12, 0))

        # 工具栏
        bar = tk.Frame(self, bg=BG, padx=12, pady=8)
        bar.pack(fill="x")
        self.scan_btn = ttk.Button(bar, text="⟳ 重新扫描", command=self.rescan)
        self.scan_btn.pack(side="left")
        self.search_var = tk.StringVar()
        self.search_var.trace_add("write", lambda *_: self._render_tree())
        entry = ttk.Entry(bar, textvariable=self.search_var, width=26)
        entry.pack(side="left", padx=(10, 14))

        for text, cmd in (
            ("全选", lambda: self._set_all(True)),
            ("全不选", lambda: self._set_all(False)),
            ("反选", self._invert),
            ("仅选应用", self._apps_only),
        ):
            ttk.Button(bar, text=text, width=8, command=cmd).pack(side="left", padx=3)

        self.export_btn = ttk.Button(bar, text="导出配置 ▾", command=self.on_export)
        self.export_btn.pack(side="right")
        self.import_btn = ttk.Button(bar, text="导入恢复…", command=self.on_import)
        self.import_btn.pack(side="right", padx=(0, 8))
        self.cancel_btn = ttk.Button(bar, text="取消导入", command=self.on_cancel, state="disabled")
        self.cancel_btn.pack(side="right", padx=(0, 8))

        # 主体：左列表 + 右说明（列表占满）
        body = tk.Frame(self, bg=BG)
        body.pack(fill="both", expand=True, padx=12)

        columns = ("name", "id", "ver", "cat")
        self.tree = ttk.Treeview(body, columns=columns, show="tree headings", selectmode="none")
        self.tree.heading("#0", text="勾选", anchor="w")
        self.tree.column("#0", width=64, minwidth=56, stretch=False)
        self.tree.heading("name", text="软件名称")
        self.tree.column("name", width=250)
        self.tree.heading("id", text="包 ID")
        self.tree.column("id", width=280)
        self.tree.heading("ver", text="已装版本")
        self.tree.column("ver", width=110, anchor="center", stretch=False)
        self.tree.heading("cat", text="分类")
        self.tree.column("cat", width=90, anchor="center", stretch=False)
        vsb = ttk.Scrollbar(body, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=vsb.set)
        self.tree.pack(side="left", fill="both", expand=True)
        vsb.pack(side="left", fill="y")
        self.tree.bind("<Button-1>", self._on_click)

        # 导入日志
        logwrap = tk.LabelFrame(self, text="导入 / 状态日志", bg=PANEL, fg=MUTED,
                                padx=6, pady=4, font=(FONT_NAME, 9))
        logwrap.pack(fill="x", padx=12, pady=(6, 0))
        self.log = tk.Text(logwrap, height=7, font=(FONT_NAME, 9), bg="#fafafa",
                           relief="flat", state="disabled", wrap="word")
        self.log.pack(fill="x")
        for tag, color in (("success", GREEN), ("fail", RED), ("skip", PURPLE),
                           ("error", RED), ("info", MUTED), ("done", ACCENT_DARK)):
            self.log.tag_configure(tag, foreground=color)

        # 状态栏
        status = tk.Frame(self, bg=BG, padx=12, pady=8)
        status.pack(fill="x", side="bottom")
        self.status_var = tk.StringVar(value="正在扫描已安装软件…")
        tk.Label(status, textvariable=self.status_var, bg=BG, fg=MUTED,
                 font=(FONT_NAME, 9)).pack(side="left")
        self.sel_var = tk.StringVar(value="已勾选 0 项")
        tk.Label(status, textvariable=self.sel_var, bg=BG, fg=ACCENT_DARK,
                 font=(FONT_NAME, 9, "bold")).pack(side="right")

    # ── 扫描 ────────────────────────────────────────────────────
    def rescan(self) -> None:
        if getattr(self, "_scanning", False):
            return
        self._scanning = True
        self.scan_btn.config(state="disabled")
        self.status_var.set("正在扫描已安装软件（winget list + export），约需 5~20 秒…")
        self.log_append("info", "开始扫描…")

        def work() -> None:
            try:
                snapshot = self.catalog.get_catalog(force=True)
                self.scan_queue.put(("ok", snapshot))
            except Exception as exc:  # noqa: BLE001
                self.scan_queue.put(("err", exc))

        threading.Thread(target=work, daemon=True).start()

    def _apply_snapshot(self, snapshot) -> None:
        self.snapshot = snapshot
        self.packages = [p for p in snapshot.packages if p.reinstallable]
        hidden = snapshot.counts["total"] - len(self.packages)
        self.winget_label.config(
            text=f"winget {snapshot.wingetVersion} · {snapshot.machine['hostname']}"
        )
        self.log_append("info",
                        f"扫描完成：可重装 {len(self.packages)} 个；"
                        f"另有 {hidden} 个 MSIX/注册表项无法自动重装，已隐藏。")
        # 清掉已卸载软件的勾选
        valid = {p.packageIdentifier.lower() for p in self.packages}
        self.checked = {k: v for k, v in self.checked.items() if k in valid}
        self._render_tree()
        self._update_status()
        self._save_selection()

    def _render_tree(self) -> None:
        self.tree.delete(*self.tree.get_children())
        self.iid_pkg.clear()
        self.group_iids.clear()
        keyword = self.search_var.get().strip().lower()

        groups: dict[str, list] = {}
        for pkg in self.packages:
            if keyword and keyword not in f"{pkg.name} {pkg.packageIdentifier}".lower():
                continue
            groups.setdefault(pkg.category, []).append(pkg)

        for category in sorted(groups, key=lambda c: (-len(groups[c]), c)):
            items = groups[category]
            gid = self.tree.insert("", "end", open=True,
                                   text=f"☐ {category} ({len(items)})",
                                   values=("", "", "", ""))
            self.tree.item(gid, tags=("group",))
            self.group_iids[category] = gid
            for pkg in items:
                mark = "☑" if self.checked.get(pkg.packageIdentifier.lower()) else "☐"
                iid = self.tree.insert(
                    gid, "end",
                    text=mark,
                    values=(pkg.name, pkg.packageIdentifier, pkg.version or "-", pkg.category),
                )
                self.iid_pkg[iid] = pkg
        self.tree.tag_configure("group", font=(FONT_NAME, 10, "bold"), foreground=MUTED)

    def _on_click(self, event) -> None:
        iid = self.tree.identify_row(event.y)
        if not iid:
            return
        pkg = self.iid_pkg.get(iid)
        if pkg is not None:
            key = pkg.packageIdentifier.lower()
            self.checked[key] = not self.checked.get(key, False)
            self.tree.item(iid, text="☑" if self.checked[key] else "☐")
            self._update_group_mark(pkg.category)
            self._update_status()
            self._save_selection()
        elif iid in self.group_iids.values():
            # 点组头：整组切换（组头未全勾则全勾，否则全取消）
            children = self.tree.get_children(iid)
            if not children:
                return
            target = self.tree.item(children[0], "text") == "☐"
            for c in children:
                p = self.iid_pkg.get(c)
                if p is not None:
                    self.checked[p.packageIdentifier.lower()] = target
                    self.tree.item(c, text="☑" if target else "☐")
            category = next(k for k, v in self.group_iids.items() if v == iid)
            self._update_group_mark(category)
            self._update_status()
            self._save_selection()

    def _update_group_mark(self, category: str) -> None:
        gid = self.group_iids.get(category)
        if not gid:
            return
        children = self.tree.get_children(gid)
        n = len(children)
        sel = sum(1 for c in children if self.tree.item(c, "text") == "☑")
        if n and sel == n:
            mark = "☑"
        elif sel == 0:
            mark = "☐"
        else:
            mark = "▣"
        self.tree.item(gid, text=f"{mark} {category} ({sel}/{n})")

    def _set_all(self, value: bool) -> None:
        for p in self.packages:
            self.checked[p.packageIdentifier.lower()] = value
        self._render_tree()
        self._update_status()
        self._save_selection()

    def _invert(self) -> None:
        for p in self.packages:
            key = p.packageIdentifier.lower()
            self.checked[key] = not self.checked.get(key, False)
        self._render_tree()
        self._update_status()
        self._save_selection()

    def _apps_only(self) -> None:
        for p in self.packages:
            self.checked[p.packageIdentifier.lower()] = p.category != "运行库与组件"
        self._render_tree()
        self._update_status()
        self._save_selection()

    def selected_packages(self) -> list:
        return [p for p in self.packages if self.checked.get(p.packageIdentifier.lower())]

    def _update_status(self) -> None:
        if self.snapshot:
            self.status_var.set(
                f"共 {len(self.packages)} 个可重装软件 · 扫描耗时 {self.snapshot.durationMs}ms"
            )
        self.sel_var.set(f"已勾选 {len(self.selected_packages())} 项")

    # ── 导出 ────────────────────────────────────────────────────
    def on_export(self) -> None:
        selected = self.selected_packages()
        if not selected:
            messagebox.showwarning(APP_TITLE, "请先勾选需要保留的软件。", parent=self)
            return
        name = simpledialog.askstring(
            APP_TITLE, "配置名称（将作为文件名）：",
            initialvalue="我的软件配置", parent=self,
        )
        if not name or not name.strip():
            return
        try:
            result = write_exports(
                self.config.export_dir, name.strip(), "", selected, self.snapshot,
                self.snapshot.wingetVersion if self.snapshot else "1.0.0",
            )
        except AppError as exc:
            messagebox.showerror(APP_TITLE, exc.user_message, parent=self)
            return
        except Exception as exc:  # noqa: BLE001
            messagebox.showerror(APP_TITLE, f"导出失败：{exc}", parent=self)
            return
        files = [str(result.primary_path)]
        if result.winget_path:
            files.append(str(result.winget_path))
        self.log_append("done", f"已导出 {result.package_count} 个软件包：")
        for f in files:
            self.log_append("info", f"  {f}")
        if messagebox.askyesno(
            APP_TITLE,
            f"导出成功（{result.package_count} 个软件包）。\n\n"
            f"{chr(10).join(Path(f).name for f in files)}\n\n"
            f"是否打开所在文件夹？",
            parent=self,
        ):
            import os
            os.startfile(self.config.export_dir)  # noqa: S606

    # ── 导入 ────────────────────────────────────────────────────
    def on_import(self) -> None:
        if self.importing:
            messagebox.showinfo(APP_TITLE, "已有导入任务正在进行。", parent=self)
            return
        path = tk.filedialog.askopenfilename(
            parent=self, title="选择配置文件",
            initialdir=str(self.config.export_dir),
            filetypes=[("JSON 配置", "*.json"), ("全部文件", "*.*")],
        )
        if not path:
            return
        try:
            profile = load_profile(Path(path))
        except AppError as exc:
            messagebox.showerror(APP_TITLE, exc.user_message, parent=self)
            return
        except Exception as exc:  # noqa: BLE001
            messagebox.showerror(APP_TITLE, f"读取失败：{exc}", parent=self)
            return
        if not messagebox.askyesno(
            APP_TITLE,
            f"即将安装「{profile.profile_name}」中的 {len(profile.packages)} 个软件包。\n"
            "已安装的会自动跳过。是否继续？",
            parent=self,
        ):
            return
        self._start_import(profile)

    def _start_import(self, profile) -> None:
        self.importing = True
        self.cancel_event.clear()
        self.import_btn.config(state="disabled")
        self.cancel_btn.config(state="normal")
        self.status_var.set(f"正在导入「{profile.profile_name}」…")
        self.log_append("done", f"开始导入「{profile.profile_name}」，共 {len(profile.packages)} 个软件包")
        current = {"name": ""}
        counts = {"success": 0, "failed": 0, "skipped": 0}

        def on_line(raw: str) -> None:
            line = _ANSI.sub("", raw).replace("\r", " ").strip()
            if not line or set(line) <= {"-", " ", "\\", "|", "/", "█", "░", "━"}:
                return
            if _FATAL.search(line):
                self.import_queue.put(("error", line))
                return
            if _FOUND.search(line) and not _SUCCESS.search(line):
                current["name"] = line
                self.import_queue.put(("info", line))
                return
            if _SUCCESS.search(line):
                counts["success"] += 1
                self.import_queue.put(("success", line, dict(counts)))
                return
            if _SKIP.search(line):
                counts["skipped"] += 1
                self.import_queue.put(("skip", line, dict(counts)))
                return
            if _FAIL.search(line):
                counts["failed"] += 1
                self.import_queue.put(("fail", line, dict(counts)))
                return
            self.import_queue.put(("log", line))

        def work() -> None:
            try:
                rc = self.repo.import_file(
                    profile.path, on_line=on_line, cancel_flag=lambda: self.cancel_event.is_set()
                )
                self.import_queue.put(("finish", rc, dict(counts)))
            except Exception as exc:  # noqa: BLE001
                self.import_queue.put(("finish", -1, {"exception": str(exc), **counts}))

        threading.Thread(target=work, daemon=True).start()

    def on_cancel(self) -> None:
        self.cancel_event.set()
        self.log_append("info", "已请求取消，正在终止 winget 进程…")
        self.cancel_btn.config(state="disabled")

    # ── 事件轮询 ────────────────────────────────────────────────
    def _poll(self) -> None:
        try:
            while True:
                item = self.scan_queue.get_nowait()
                self._scanning = False
                self.scan_btn.config(state="normal")
                kind, payload = item[0], item[1]
                if kind == "ok":
                    self._apply_snapshot(payload)
                else:
                    self.status_var.set("扫描失败")
                    messagebox.showerror(APP_TITLE, f"扫描失败：{payload}", parent=self)
        except queue.Empty:
            pass

        try:
            while True:
                item = self.import_queue.get_nowait()
                kind = item[0]
                if kind == "finish":
                    self._finish_import(item)
                else:
                    tag = kind if kind in ("success", "skip", "fail", "error") else \
                        "info" if kind == "info" else "log"
                    extra = f"  [成功{item[2]['success']} 跳过{item[2]['skipped']} 失败{item[2]['failed']}]" \
                        if len(item) > 2 and isinstance(item[2], dict) and "exception" not in item[2] else ""
                    self.log_append(tag, item[1] + extra)
        except queue.Empty:
            pass
        self.after(120, self._poll)

    def _finish_import(self, item) -> None:
        self.importing = False
        self.import_btn.config(state="normal")
        self.cancel_btn.config(state="disabled")
        rc = item[1]
        counts = item[2]
        if "exception" in counts:
            self.log_append("error", f"导入中断：{counts['exception']}")
            self.status_var.set("导入中断")
            messagebox.showerror(APP_TITLE, f"导入中断：{counts['exception']}", parent=self)
            return
        summary = (f"导入完成：成功 {counts['success']}、跳过 {counts['skipped']}、失败 {counts['failed']}"
                   if rc == 0 else
                   f"winget 退出码 {rc}：成功 {counts['success']}、跳过 {counts['skipped']}、"
                   f"失败 {counts['failed']}")
        self.log_append("done", summary)
        self.status_var.set(summary)
        messagebox.showinfo(APP_TITLE, summary, parent=self)
        self.rescan()

    # ── 日志 ────────────────────────────────────────────────────
    def log_append(self, tag: str, text: str) -> None:
        self.log.config(state="normal")
        self.log.insert("end", f"{text}\n", tag if tag != "log" else ())
        self.log.see("end")
        self.log.config(state="disabled")

    # ── 勾选持久化 ──────────────────────────────────────────────
    def _load_selection(self) -> None:
        try:
            import json
            if SELECTION_FILE.is_file():
                data = json.loads(SELECTION_FILE.read_text(encoding="utf-8"))
                if isinstance(data, dict):
                    self.checked = {str(k).lower(): bool(v) for k, v in data.items()}
        except Exception:  # noqa: BLE001
            pass

    def _save_selection(self) -> None:
        try:
            import json
            PERSIST_DIR.mkdir(parents=True, exist_ok=True)
            SELECTION_FILE.write_text(
                json.dumps(self.checked, ensure_ascii=False), encoding="utf-8"
            )
        except Exception:  # noqa: BLE001
            pass


def main() -> int:
    try:
        import ctypes
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:  # noqa: BLE001
        pass
    try:
        app = App()
    except ConfigError as exc:
        root = tk.Tk()
        root.withdraw()
        messagebox.showerror(APP_TITLE, str(exc))
        return 1
    app.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
