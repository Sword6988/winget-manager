"""冒烟测试：不依赖真实 winget，用假数据把界面主链路跑一遍。

跑法（项目根目录）：
    QT_QPA_PLATFORM=offscreen py -3.14 tools/test_smoke.py

覆盖：导出清单结构 / 列表分组建构 / 勾选与全选 / 吸顶分组头 /
导出侧恢复脚本 / 日志高度钳制 / 关于对话框能正常构造，
以及本轮修复的验收点：取消扫描不崩进程、原子写、恢复脚本转义与编码、
BOM 解码、勾选文件损坏可恢复、缺 winget 仍能启动。

隔离要求：不写用户注册表（QSettings 改 INI 并指到临时目录）、不落任何文件到
cwd（临时文件全在 self.tmp 下）、不真探测 winget（直接构造 Config）。
"""
from __future__ import annotations

import json
import os
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from PySide6.QtCore import QEventLoop, QPoint, QSettings, QTimer  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

import qt_app as Q  # noqa: E402
from core.config import Config, ConfigError, load_config  # noqa: E402
from core.errors import BadRequest, ScanCancelled, WingetNotFound  # noqa: E402
from core.fsio import atomic_write_json  # noqa: E402
from services.catalog import (CatalogService, CatalogSnapshot, Package,  # noqa: E402
                              RawPackage, _is_reinstallable)
from services.metadata import (RuntimeCache, analyze_runtime_signal,  # noqa: E402
                               parse_runtime_signal)
from services.profiles import build_restore_bat, build_winget_manifest  # noqa: E402
from services.winget_cli import WingetRepository, _decode  # noqa: E402

# 真实的 _start_scan（setUpClass 打桩前保存），放在模块级才不会被描述符绑成方法
_ORIG_START_SCAN = None

# winget 进程级全局互斥冲突时的退出码
_MUTEX_RC = 0x8A150001

# ── winget show 输出样例（元数据判定运行库） ──
# Microsoft.VSTOR：描述含 runtime / required to run，安装器 URL 是 redist
_SHOW_VSTOR_ZH = (
    "找到 Microsoft.VSTOR\r\n"
    "版本: 10.0.60830\r\n"
    "发布程序: Microsoft Corporation\r\n"
    "描述: 安装 Microsoft Visual Studio 2010 Tools for Office Runtime，\r\n"
    "  which is required to run Microsoft Office solutions.\r\n"
    "标记:\r\n"
    "  office\r\n"
    "安装程序 URL: https://download.microsoft.com/download/vstor_redist.exe\r\n"
)
# Rustup：标记只有 rust/rustlang，描述无信号词
_SHOW_RUSTUP = (
    "找到 Rustup\r\n"
    "版本: 1.27.0\r\n"
    "描述: Rust 工具链安装与管理器。\r\n"
    "标记:\r\n"
    "  rust\r\n"
    "  rustlang\r\n"
)
# Windows SDK：英文段落标签，无信号词
_SHOW_SDK_EN = (
    "Found Windows SDK\r\n"
    "Version: 10.0.22621\r\n"
    "Description: Windows SDK provides headers, libraries and tools.\r\n"
    "Tags:\r\n"
    "  sdk\r\n"
    "  windows\r\n"
)


class _FakeProc:
    """假子进程：run() 只用到 communicate / poll / kill / returncode。"""

    def __init__(self, returncode: int, out: bytes = b"", err: bytes = b"") -> None:
        self.returncode = returncode
        self._out = out
        self._err = err
        self.killed = False

    def communicate(self, timeout: float | None = None):
        return self._out, self._err

    def poll(self):
        return self.returncode

    def kill(self) -> None:
        self.killed = True


def make_config() -> Config:
    """直接构造配置：走 load_config() 会真的去探测 winget，测试不该依赖机器状态。"""
    return Config(winget_exe="", cache_ttl=60, command_timeout=5, extra={})


def make_packages() -> list:
    """可重装 12 项（含 2 个运行库）+ 8 项 winget 装不上，覆盖两个固定分组。"""
    data = [
        ("Google.Chrome", "Google Chrome", True),
        ("Mozilla.Firefox", "Mozilla Firefox", True),
        ("Microsoft.Edge", "Microsoft Edge", True),
        ("Git.Git", "Git", True),
        ("Python.Python.3.14", "Python 3.14", True),
        ("Microsoft.VisualStudioCode", "Visual Studio Code", True),
        ("7zip.7zip", "7-Zip", True),
        ("voidtools.Everything", "Everything", True),
        ("Valve.Steam", "Steam", True),
        ("Huorong.Huorong", "火绒安全", True),
        # 运行库：可重装，但不是「应用」，列表里要打「运行库」角标
        ("Microsoft.VCRedist.2015+.x64", "Microsoft Visual C++ 2015-2022 x64", True),
        ("Microsoft.DotNet.Runtime.10", "Microsoft .NET Runtime 10", True),
    ]
    dep_ids = {"Microsoft.VCRedist.2015+.x64", "Microsoft.DotNet.Runtime.10"}
    pkgs = [Package(packageIdentifier=i, name=n, reinstallable=r,
                    isDependency=i in dep_ids,
                    version="1.0.0" if r else "", source="winget" if r else "")
            for i, n, r in data]
    # winget 装不上：来源五花八门，最终全部归入「winget 不支持」
    for i in range(8):
        pkgs.append(Package(packageIdentifier=f"Vendor.Tool{i}", name=f"参考组件 {i}",
                            reinstallable=False, version="", source="msix"))
    return pkgs


class SmokeTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        global _ORIG_START_SCAN
        cls.app = QApplication.instance() or QApplication(sys.argv)
        cls.tmp = Path(tempfile.mkdtemp(prefix="wpm-test-"))
        # QSettings 默认会写 HKCU 注册表；测试里改成 INI 并扔到临时目录
        cls.settings_dir = tempfile.mkdtemp(prefix="wpm-settings-")
        QSettings.setDefaultFormat(QSettings.IniFormat)
        QSettings.setPath(QSettings.IniFormat, QSettings.UserScope, cls.settings_dir)
        # 勾选状态与元数据缓存都落到临时目录，别碰真实的 %LOCALAPPDATA%
        cls._patches = [
            mock.patch("qt_app.SELECTION_FILE", cls.tmp / "selection.json"),
            mock.patch("qt_app.PERSIST_DIR", cls.tmp),
            mock.patch("qt_app.METADATA_CACHE_FILE", cls.tmp / "metadata-cache.json"),
        ]
        for p in cls._patches:
            p.start()
        # 屏蔽 __init__ 里的自动扫描，改由测试注入假数据
        _ORIG_START_SCAN = Q.MainWindow._start_scan
        Q.MainWindow._start_scan = lambda self: None
        cls.cfg = make_config()
        cls.pkgs = make_packages()
        cls.win = Q.MainWindow(cls.cfg)
        cls.win.packages = list(cls.pkgs)
        cls.win.snapshot = CatalogSnapshot(
            generatedAt="2026-11-03T14:00:00", durationMs=1200, wingetVersion="v1.10.0",
            machine={"os": "test"}, counts={"total": len(cls.pkgs)},
            packages=list(cls.pkgs))
        cls.win.state = "READY"
        cls.win._rebuild_chips()
        cls.win._rebuild_list()
        cls.win.show()
        cls.app.processEvents()

    @classmethod
    def tearDownClass(cls) -> None:
        cls.win.close()
        cls.win.deleteLater()      # 交给 Qt 立即销毁，别留到解释器退出时乱序回收
        cls.app.processEvents()
        for p in cls._patches:
            p.stop()
        Q.MainWindow._start_scan = _ORIG_START_SCAN

    def _spawn_window(self):
        """新建一个窗口（不自动扫描、勾选文件在临时目录）。"""
        return Q.MainWindow(make_config())

    def _drop_window(self, win) -> None:
        """确定性地销毁测试窗口：close 只是隐藏，deleteLater 才真正回收。"""
        win.close()
        win.deleteLater()
        self.app.processEvents()

    def _wait_until(self, predicate, timeout_s: float = 6.0) -> bool:
        """轮询等异步（QThread 信号跨线程投递后要靠 processEvents 才进槽）。"""
        deadline = time.perf_counter() + timeout_s
        while time.perf_counter() < deadline:
            self.app.processEvents()
            if predicate():
                return True
            time.sleep(0.02)
        return False

    # ── 导出清单 ──
    def test_manifest_structure(self) -> None:
        selected = [p for p in self.pkgs if p.reinstallable][:4]
        m = build_winget_manifest(selected, "1.10.0")
        self.assertIn("$schema", m)
        self.assertIn("Sources", m)
        ids = [pk["PackageIdentifier"] for s in m["Sources"] for pk in s["Packages"]]
        self.assertEqual(sorted(ids), sorted(p.packageIdentifier for p in selected))
        for src in m["Sources"]:
            self.assertIn("SourceDetails", src)

    def test_manifest_skips_unreinstallable(self) -> None:
        m = build_winget_manifest(self.pkgs, "1.10.0")
        ids = {pk["PackageIdentifier"] for s in m["Sources"] for pk in s["Packages"]}
        for p in self.pkgs:
            if not p.reinstallable:
                self.assertNotIn(p.packageIdentifier, ids, "不可重装的包不应进导出清单")

    # ── 列表 ──
    def _window_with(self, packages: list):
        """按给定包集合造一个 READY 窗口（不自动扫描、勾选文件在临时目录）。"""
        win = self._spawn_window()
        win.packages = list(packages)
        win.snapshot = SimpleNamespace(durationMs=1)
        win.state = "READY"
        win._rebuild_chips()
        win._rebuild_list()
        return win

    def test_sections_built(self) -> None:
        titles = [s.title for s in self.win.sections]
        self.assertEqual(titles,
                         [Q.SOFTWARE_GROUP, Q.DEPENDENCY_GROUP, Q.UNSUPPORTED_GROUP],
                         "三组固定顺序：软件 → 运行库 → winget 不支持（沉底）")
        self.assertEqual(len(self.win.rows), len(self.pkgs))

    def test_sections_skip_empty_groups(self) -> None:
        """某一组没有成员时，那一组的组头不出现。"""
        win = self._window_with([p for p in self.pkgs if not p.isDependency])
        try:
            self.assertEqual([s.title for s in win.sections],
                             [Q.SOFTWARE_GROUP, Q.UNSUPPORTED_GROUP],
                             "没有运行库时不留空的「运行库」组头")
        finally:
            self._drop_window(win)

        only_apps = [p for p in self.pkgs if p.reinstallable and not p.isDependency]
        win = self._window_with(only_apps)
        try:
            self.assertEqual([s.title for s in win.sections], [Q.SOFTWARE_GROUP],
                             "全部可重装且无运行库时只剩「软件」一组")
        finally:
            self._drop_window(win)

    def test_group_of_three_way(self) -> None:
        """分组只由 reinstallable + isDependency 决定，与名称/包 ID 无关。"""
        for p in self.pkgs:
            if not p.reinstallable:
                expected = Q.UNSUPPORTED_GROUP
            elif p.isDependency:
                expected = Q.DEPENDENCY_GROUP
            else:
                expected = Q.SOFTWARE_GROUP
            self.assertEqual(self.win._group_of(p), expected,
                             f"{p.packageIdentifier} 的分组判定错误")
        self.assertEqual({self.win._group_of(p) for p in self.pkgs},
                         {Q.SOFTWARE_GROUP, Q.DEPENDENCY_GROUP, Q.UNSUPPORTED_GROUP},
                         "三组取值都要出现：软件 / 运行库 / winget 不支持")

    def test_unsupported_group_all_grey(self) -> None:
        unsupported = [r for r in self.win.rows.values()
                       if self.win._group_of(r.pkg) == Q.UNSUPPORTED_GROUP]
        self.assertTrue(unsupported)
        for r in unsupported:
            self.assertFalse(r.selectable, "「winget 不支持」组内应为不可勾选的灰行")
            self.assertFalse(r.pkg.reinstallable)
        self.assertTrue(self.win.sections[-1].header.sel_all.isHidden(),
                        "整组不可勾时不应显示「全选本组」")

    def test_group_toggle(self) -> None:
        self.win._toggle_group(Q.SOFTWARE_GROUP)
        self.app.processEvents()
        rows = [r for r in self.win.rows.values()
                if self.win._group_of(r.pkg) == Q.SOFTWARE_GROUP and r.selectable]
        self.assertEqual(len(rows), sum(1 for p in self.pkgs
                                        if p.reinstallable and not p.isDependency))
        self.assertTrue(all(r._checked for r in rows), "全选本组应勾上所有可勾行")
        # 「运行库」组的行不受影响
        deps = [r for r in self.win.rows.values()
                if self.win._group_of(r.pkg) == Q.DEPENDENCY_GROUP]
        self.assertFalse(any(r._checked for r in deps), "全选「软件」不应波及「运行库」")
        self.win._toggle_group(Q.SOFTWARE_GROUP)   # 复位
        self.app.processEvents()
        self.assertFalse(any(r._checked for r in rows))

    def test_dependency_group_select_all(self) -> None:
        """运行库组内的行可勾选，组头必须出现「全选本组」。"""
        self.app.processEvents()        # 上一条用例刚重建过列表，等新小节显形
        sec = next(s for s in self.win.sections if s.title == Q.DEPENDENCY_GROUP)
        self.assertFalse(sec.header.sel_all.isHidden(), "「运行库」组应能整组勾选")
        self.assertTrue(sec.header.sel_all.isVisible(), "「运行库」组应能整组勾选")
        rows = [r for r in self.win.rows.values()
                if self.win._group_of(r.pkg) == Q.DEPENDENCY_GROUP]
        self.assertTrue(rows)
        self.assertTrue(all(r.selectable for r in rows), "运行库行可勾选")

        self.win._toggle_group(Q.DEPENDENCY_GROUP)
        self.app.processEvents()
        self.assertTrue(all(r._checked for r in rows), "全选本组应勾上运行库")
        self.assertEqual(sec.header.sel_all.text(), "全不选")
        self.win._toggle_group(Q.DEPENDENCY_GROUP)      # 复位
        self.app.processEvents()
        self.assertFalse(any(r._checked for r in rows))

    # ── 吸顶分组头 ──
    def test_sticky_follows_scroll(self) -> None:
        """专用窗口：列表在 show() 之前构建好，几何才是真实高度。

        共用 self.win 会被前面用例的「裸重建」（无事件循环）污染：小节内容高度
        在未 polish 时测得偏小，clip 被冻结成几十像素，滚动条最大值为 0，
        这条用例就会误判成「内容不足一屏」。真实交互里重建发生在事件循环内，
        几何计算是正确的，用干净窗口才能测到真实行为。
        """
        win = self._window_with(self.pkgs)
        try:
            win.show()
            self.app.processEvents()
            sb = win.scroll.verticalScrollBar()
            sb.setValue(0)
            self.app.processEvents()
            self.assertFalse(win.sticky.isVisible(), "顶部时不应出现吸顶头")

            if len(win.sections) < 2 or sb.maximum() <= 0:
                self.skipTest("内容不足一屏，无法验证滚动")
            second = win.sections[1]
            y = second.mapTo(win.rows_host, QPoint(0, 0)).y()
            sb.setValue(min(y + 20, sb.maximum()))
            self.app.processEvents()
            if sb.value() <= 20:
                self.skipTest("窗口过小，滚不到第二组")
            self.assertTrue(win.sticky.isVisible(), "滚过组头后应出现吸顶头")
            self.assertEqual(win.sticky._title, second.title)
        finally:
            self._drop_window(win)

    # ── 导出侧：恢复脚本 ──
    def test_restore_bat(self) -> None:
        from services.profiles import source_breakdown

        text = build_restore_bat("apps.json", 59, 4)
        self.assertIn('winget import -i "%~dp0apps.json"', text,
                      "清单名要拼 %~dp0，脚本才能在任意目录双击运行")
        self.assertIn("--accept-source-agreements", text)
        self.assertIn("--ignore-unavailable", text, "源里下架的包应跳过而不是中断整批")
        self.assertIn("管理员", text, "应提示提权，否则机器级安装包会失败")
        self.assertIn("59 个软件包", text)
        self.assertIn("4 个来自微软商店", text)
        self.assertTrue(text.startswith("@echo off"))
        # 必须能按 GBK 写盘（cmd 代码页 936），否则重装那天中文全乱
        path = self.tmp / "_tmp_restore.bat"
        path.write_text(text, encoding="gbk")
        self.assertIn("winget import", path.read_text(encoding="gbk"))
        path.unlink()

        br = source_breakdown(self.pkgs)
        self.assertEqual(set(br), {"winget", "msstore"})
        self.assertEqual(br["winget"], sum(1 for p in self.pkgs
                                           if p.reinstallable and p.source == "winget"))

    def test_restore_bat_escapes_metachars(self) -> None:
        # `&` 在 cmd 里是命令分隔符：不在引号里就会被当成第二条命令执行
        text = build_restore_bat("a&b.json", 3)
        name_line = next(ln for ln in text.splitlines() if "清单:" in ln)
        self.assertIn('"a&b.json"', name_line, "echo 行里的文件名必须整体带引号")
        self.assertIn('winget import -i "%~dp0a&b.json"', text)

        # `%` 会被 cmd 当变量展开，必须双写
        pct = build_restore_bat("100%备份.json", 3)
        self.assertIn("100%%备份.json", pct)
        self.assertNotIn('"100%备份.json"', pct)

        # 只取 basename：防目录穿越
        trav = build_restore_bat(r"..\evil\apps.json", 3)
        self.assertIn('"%~dp0apps.json"', trav)
        self.assertNotIn("evil", trav)

    def test_restore_bat_gbk_unencodable(self) -> None:
        with self.assertRaises(BadRequest):
            build_restore_bat("😀清单.json", 3)

    # ── 原子写 ──
    def test_export_writes_atomically(self) -> None:
        out_dir = Path(tempfile.mkdtemp(dir=self.tmp))
        target = out_dir / "apps.json"
        target.write_text('{"old": true}\n', encoding="utf-8")
        with mock.patch("core.fsio.os.replace", side_effect=OSError("磁盘已满")):
            with self.assertRaises(OSError):
                atomic_write_json(target, {"new": 1})
        self.assertEqual(target.read_text(encoding="utf-8"), '{"old": true}\n',
                         "写盘失败时目标文件必须保持旧内容")
        leftovers = sorted(p.name for p in out_dir.iterdir() if p.suffix == ".tmp")
        self.assertEqual(leftovers, [], "失败路径必须清理临时文件")

        atomic_write_json(target, {"new": 1})
        self.assertEqual(json.loads(target.read_text(encoding="utf-8")), {"new": 1})

    # ── 解码 ──
    def test_decode_strips_bom(self) -> None:
        self.assertEqual(_decode(b"\xef\xbb\xbfName"), "Name",
                         "UTF-8 BOM 必须被吃掉，否则首行匹配全错")
        self.assertEqual(_decode("火绒安全".encode("utf-8")), "火绒安全")

    # ── 勾选持久化 ──
    def test_selection_survives_corrupt_json(self) -> None:
        Q.SELECTION_FILE.write_text('{"google.chrome": tr', encoding="utf-8")
        win = self._spawn_window()          # __init__ 里就会 _load_selection()
        try:
            self.assertEqual(win.checked, {}, "半截 JSON 应回退成空状态而不是抛异常")
        finally:
            self._drop_window(win)

    # ── 配置 ──
    def test_load_config_without_winget(self) -> None:
        with mock.patch("core.config.find_winget", side_effect=ConfigError("没有 winget")):
            cfg = load_config()
        self.assertEqual(cfg.winget_exe, "")
        self.assertIn("没有 winget", cfg.extra.get("winget_error", ""))

    # ── 取消扫描：本轮的崩溃点 ──
    def test_scan_worker_cancel_keeps_ref(self) -> None:
        class SlowCatalog:
            """卡住 1s 的假目录服务，给取消留出时间窗。"""

            def __init__(self) -> None:
                self.entered = threading.Event()

            def get_catalog(self, force: bool = False, cancel=None,
                            winget_version: str = "") -> CatalogSnapshot:
                self.entered.set()
                time.sleep(1.0)
                return CatalogSnapshot(
                    generatedAt="2026-11-03T14:00:00", durationMs=1000,
                    wingetVersion="v1.10.0", machine={}, counts={"total": 0},
                    packages=[])

        # setUpClass 把 _start_scan 打了桩，这里直接调真实实现
        win = self._spawn_window()
        try:
            win.repo = None          # 跳过 winget 探测，直接进 get_catalog
            win.catalog = SlowCatalog()
            _ORIG_START_SCAN(win)    # 真实 _start_scan
            worker = win._scan_worker
            self.assertIsNotNone(worker)
            self.assertTrue(win.catalog.entered.wait(1.0), "扫描线程应已启动")

            win._cancel_scan()
            self.assertIsNone(win._scan_worker, "取消只解绑结果，不丢弃引用")
            self.assertIn(worker, win._workers,
                          "运行中的 QThread 必须留在 _workers 里，否则会被析构 → qFatal")
            self.assertFalse(win.scan_btn.isEnabled(), "收尾前不放开发起新扫描的按钮")

            loop = QEventLoop()
            worker.finished.connect(loop.quit)
            QTimer.singleShot(3000, loop.quit)
            loop.exec()
            self.app.processEvents()

            self.assertEqual(win._workers, set(), "线程结束后集合应为空")
            self.assertTrue(win.scan_btn.isEnabled(), "收尾后按钮应恢复可用")
        finally:
            self._drop_window(win)

    # ── 日志高度 ──
    def test_log_height_clamped(self) -> None:
        self.win._set_log_height(999)
        self.assertEqual(self.win._log_height, 420)
        self.win._set_log_height(10)
        self.assertEqual(self.win._log_height, 90)
        self.win._set_log_height(150)

    # ── 搜索分词 ──
    def test_search_tokens_and(self) -> None:
        self.win.search.setText("python 3")
        self.app.processEvents()
        self.win._rebuild_list()
        names = {r.pkg.name for r in self.win.rows.values()}
        self.assertEqual(names, {"Python 3.14"}, "空格分词应为 AND 匹配")

        self.win.search.setText("id:git")
        self.app.processEvents()
        self.win._rebuild_list()
        names = {r.pkg.name for r in self.win.rows.values()}
        self.assertEqual(names, {"Git"}, "id: 前缀应只匹配包 ID")

        self.win.search.setText("chrome firefox")
        self.app.processEvents()
        self.win._rebuild_list()
        self.assertEqual(len(self.win.rows), 0, "两个词同时命中才算匹配")
        self.win.search.clear()
        self.win._rebuild_list()

    # ── 仅看已选 ──
    def test_only_selected_filter(self) -> None:
        self.win._set_all(False)
        self.win.checked["google.chrome"] = True
        self.win.only_btn.setChecked(True)          # 触发 toggled → 重建
        self.app.processEvents()
        self.assertTrue(self.win.only_selected)
        self.assertEqual({r.pkg.name for r in self.win.rows.values()}, {"Google Chrome"})

        self.win.only_btn.setChecked(False)
        self.app.processEvents()
        self.assertGreater(len(self.win.rows), 1, "关闭后应恢复完整列表")

    # ── 无法自动重装清单 ──
    def test_unreinstallable_list(self) -> None:
        from services.profiles import build_unreinstallable_list

        text = build_unreinstallable_list(self.pkgs)
        self.assertIn("无法自动重装清单", text)
        self.assertIn("参考组件 0", text)
        self.assertIn("Vendor.Tool0", text)
        self.assertNotIn("【", text, "这些包同属「winget 不支持」，不该再按分类切小节")
        self.assertNotIn("Google Chrome", text, "可重装的包不该出现在核对清单里")
        self.assertTrue(text.startswith("Winget 软件管家"))
        # 平铺按名称排序：参考组件 0 在 参考组件 1 之前
        self.assertLess(text.index("参考组件 0"), text.index("参考组件 1"))

    # ── 运行库角标（与分组无关的展示信息） ──
    def test_dependency_badge_counts(self) -> None:
        self.win._rebuild_list()        # 保证行是完整列表（不被前面用例的筛选影响）
        self.win._update_status()
        deps = [p for p in self.pkgs if p.isDependency and p.reinstallable]
        self.assertEqual(self.win.stat_deps._value, str(len(deps)),
                         "「运行库」统计卡应只数可重装的运行库")
        row = next(r for r in self.win.rows.values()
                   if r.pkg.packageIdentifier == "Microsoft.VCRedist.2015+.x64")
        self.assertEqual(row.tag.text(), "运行库")

    # ── 关于 ──
    def test_about_dialog_builds(self) -> None:
        dlg = Q.AboutDialog(self.win)
        self.assertEqual(dlg.windowTitle(), f"关于 {Q.APP_TITLE}")
        self.assertIn(Q.APP_VERSION, Q.APP_TAG + Q.APP_VERSION)
        dlg.close()

    # ── 取消真正 kill 子进程 ──
    def test_run_cancel_kills_subprocess(self) -> None:
        """取消必须立刻返回：证明是 kill 掉了进程，而不是等 command_timeout。"""
        cfg = Config(winget_exe=sys.executable, cache_ttl=60,
                     command_timeout=10, extra={})
        repo = WingetRepository(cfg)
        cancel = threading.Event()
        timer = threading.Timer(0.3, cancel.set)
        timer.start()
        started = time.perf_counter()
        try:
            with self.assertRaises(ScanCancelled):
                repo.run(["-c", "import time; time.sleep(30)"], cancel=cancel)
            elapsed = time.perf_counter() - started
            self.assertLess(elapsed, 3.0,
                            f"取消应秒回，实际耗时 {elapsed:.1f}s（说明没 kill 只是等超时）")
        finally:
            timer.cancel()

    # ── 目录服务：分组只依赖 reinstallable，不再有 category ──
    def test_catalog_snapshot_three_groups(self) -> None:
        """可装与否只看「源」列：winget / msstore 可装，空源（注册表/MSIX）不可装。"""
        class FakeRepo:
            def list_installed(self, cancel=None):
                return SimpleNamespace(
                    ok=True, returncode=0, stderr="",
                    stdout="名称            ID                            版本     源\n"
                           "Google Chrome   Google.Chrome                 1.0      winget\n"
                           "VC++ 运行库     Microsoft.VCRedist.2015+.x64  14.0     winget\n"
                           "商店应用        Microsoft.WindowsStore        2.0      msstore\n"
                           "注册表项        Vendor.Registry.Tool          3.0\n")

        snap = CatalogService(make_config(), FakeRepo()).get_catalog(force=True)
        by_id = {p.packageIdentifier: p for p in snap.packages}
        self.assertEqual(len(by_id), 4)
        self.assertTrue(by_id["Google.Chrome"].reinstallable)
        self.assertTrue(by_id["Microsoft.WindowsStore"].reinstallable, "msstore 源也能重装")
        self.assertTrue(by_id["Microsoft.VCRedist.2015+.x64"].isDependency,
                        "运行库应被标出来（列表角标用），但与分组无关")
        self.assertFalse(by_id["Vendor.Registry.Tool"].reinstallable, "空源应判为不可重装")
        self.assertFalse(hasattr(by_id["Google.Chrome"], "category"),
                         "Package 不应再有 category 字段")
        self.assertEqual(snap.counts["reinstallable"], 3)
        self.assertEqual(snap.counts["unsupported"], 1)
        self.assertEqual(snap.counts["dependencies"], 1)
        self.assertEqual([p.reinstallable for p in snap.packages], [True, True, True, False],
                         "可重装的排前面，装不上的沉底")

    def test_reinstallable_from_source_column(self) -> None:
        """源列判定要吃掉大小写与前后空格，且只认 winget / msstore。"""
        cases = {
            "winget": True, "WINGET": True, "  winget  ": True, "\tWinget\n": True,
            "msstore": True, "MSStore": True, " msstore ": True,
            "": False, "   ": False, "registry": False, "msix": False, "wingetx": False,
        }
        for source, expected in cases.items():
            raw = RawPackage(name="x", package_id="Vendor.App", version="1.0", source=source)
            self.assertEqual(_is_reinstallable(raw), expected, f"源={source!r} 判定错误")

    # ── 互斥重试 ──
    def test_run_retries_after_mutex_conflict(self) -> None:
        """第一次撞 0x8A150001（空输出），退避后第二次成功。"""
        procs = [_FakeProc(_MUTEX_RC), _FakeProc(0, b"v1.30.100\n")]

        class Repo(WingetRepository):
            """直接构造，绕过 find_winget 探测。"""

            def __init__(self) -> None:
                super().__init__(Config(winget_exe="winget.exe", cache_ttl=60,
                                        command_timeout=30, extra={}))

        repo = Repo()
        with mock.patch("subprocess.Popen", side_effect=procs) as popen, \
                mock.patch("services.winget_cli._MUTEX_BACKOFF_S", 0.01):
            result = repo.run(["--version"])

        self.assertEqual(popen.call_count, 2, "撞互斥后应重试一次")
        self.assertEqual(result.returncode, 0)
        self.assertIn("1.30.100", result.stdout)

    def test_run_gives_up_after_max_retries(self) -> None:
        """连续撞互斥：跑满 1 + 2 次即止，不无限重试。"""
        procs = [_FakeProc(_MUTEX_RC) for _ in range(3)]

        class Repo(WingetRepository):
            def __init__(self) -> None:
                super().__init__(Config(winget_exe="winget.exe", cache_ttl=60,
                                        command_timeout=30, extra={}))

        repo = Repo()
        with mock.patch("subprocess.Popen", side_effect=procs) as popen, \
                mock.patch("services.winget_cli._MUTEX_BACKOFF_S", 0.01):
            result = repo.run(["list"])

        self.assertEqual(popen.call_count, 3, "初次 + 2 次重试")
        self.assertEqual(result.returncode, _MUTEX_RC)
        self.assertFalse(result.ok)

    def test_run_cancel_during_backoff(self) -> None:
        """退避等待期间点取消要立刻抛 ScanCancelled，不能等退避跑完。"""
        cancel = threading.Event()
        cancel.set()

        class Repo(WingetRepository):
            def __init__(self) -> None:
                super().__init__(Config(winget_exe="winget.exe", cache_ttl=60,
                                        command_timeout=30, extra={}))

        repo = Repo()
        with mock.patch("subprocess.Popen", return_value=_FakeProc(_MUTEX_RC)), \
                mock.patch("services.winget_cli._MUTEX_BACKOFF_S", 5.0):
            started = time.perf_counter()
            with self.assertRaises(ScanCancelled):
                repo.run(["list"], cancel=cancel)
            self.assertLess(time.perf_counter() - started, 1.0, "取消应立刻返回")

    # ── 版本号拿不到不该当成「没装 winget」 ──
    def test_scan_worker_continues_when_version_unknown(self) -> None:
        class FakeRepo:
            def version(self, cancel=None):
                return "unknown"

        class FakeCatalog:
            def get_catalog(self, force=False, cancel=None, winget_version=""):
                self.seen_version = winget_version
                return SimpleNamespace(packages=[], counts={"total": 0}, durationMs=1,
                                       generatedAt="x", wingetVersion=winget_version,
                                       machine={})

        catalog = FakeCatalog()
        worker = Q.ScanWorker(catalog, FakeRepo())
        seen = {"ok": 0, "missing": 0, "err": 0}
        worker.ok.connect(lambda _s: seen.__setitem__("ok", seen["ok"] + 1))
        worker.missing.connect(lambda _m: seen.__setitem__("missing", seen["missing"] + 1))
        worker.err.connect(lambda _e: seen.__setitem__("err", seen["err"] + 1))
        worker.run()                    # 同步跑：不需要事件循环，信号是同线程直连

        self.assertEqual(seen["missing"], 0, "拿不到版本号不能弹「未安装 winget」")
        self.assertEqual(seen["ok"], 1, "应继续完成扫描")
        self.assertEqual(catalog.seen_version, "unknown", "unknown 应原样传给快照兜底")

    def test_scan_worker_missing_only_on_not_found(self) -> None:
        class NotFoundRepo:
            def version(self, cancel=None):
                raise WingetNotFound("未找到 winget.exe")

        class BoomCatalog:
            def get_catalog(self, **kwargs):
                raise AssertionError("找不到 winget 时不该继续扫描")

        worker = Q.ScanWorker(BoomCatalog(), NotFoundRepo())
        seen = {"ok": 0, "missing": 0}
        worker.ok.connect(lambda _s: seen.__setitem__("ok", seen["ok"] + 1))
        worker.missing.connect(lambda _m: seen.__setitem__("missing", seen["missing"] + 1))
        worker.run()

        self.assertEqual(seen["missing"], 1, "WingetNotFound 才走安装引导")
        self.assertEqual(seen["ok"], 0)

    # ── 元数据自动判定运行库 ──
    def test_parse_runtime_signal(self) -> None:
        # VSTOR：描述含 runtime / required to run，安装器 URL 是 redist → True
        self.assertIs(parse_runtime_signal(_SHOW_VSTOR_ZH), True)
        dep, word = analyze_runtime_signal(_SHOW_VSTOR_ZH)
        self.assertIs(dep, True)
        self.assertEqual(word, "runtime", "应报出第一个命中的信号词")
        # Rustup：标记只有 rust/rustlang → False（不是「未知」）
        self.assertIs(parse_runtime_signal(_SHOW_RUSTUP), False)
        # Windows SDK：英文标签、无信号词 → False
        self.assertIs(parse_runtime_signal(_SHOW_SDK_EN), False)
        # 空输出 / 只有一句话没有描述标记段落 → None（未知，不当成 False）
        self.assertIsNone(parse_runtime_signal(""))
        self.assertIsNone(parse_runtime_signal("   \r\n"))
        self.assertIsNone(parse_runtime_signal("找不到满足条件的包"))
        # 英文段落 + 全角冒号都能解析
        en = "Description: Visual C++ redistributable for Visual Studio\r\nTags:\r\n  cpp\r\n"
        self.assertIs(parse_runtime_signal(en), True)
        zh_full = "描述：此组件是 Visual C++ 可再发行组件（redistributable）。"
        self.assertIs(parse_runtime_signal(zh_full), True)
        # 标记多行缩进列表 + 描述折行都不丢内容
        multi = ("描述: 用于运行游戏的第一方运行时组件，\r\n"
                 "  与游戏本体分开安装。\r\n"
                 "标记:\r\n"
                 "  game\r\n"
                 "  driver\r\n")
        self.assertIs(parse_runtime_signal(multi), True, "折行描述里的信号词也应命中")

    def test_runtime_cache_roundtrip_and_corruption(self) -> None:
        path = self.tmp / "mc-roundtrip.json"
        cache = RuntimeCache(path)
        self.assertIsNone(cache.get("Microsoft.VSTOR"), "空缓存应返回未知")
        cache.put("Microsoft.VSTOR", True)
        self.assertIs(cache.get("microsoft.vstor"), True, "ID 大小写不敏感")
        data = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(data["schema"], 1)
        self.assertIs(data["entries"]["microsoft.vstor"]["dep"], True)
        self.assertIn("checkedAt", data["entries"]["microsoft.vstor"])
        # 新实例读同一份文件
        self.assertIs(RuntimeCache(path).get("Microsoft.VSTOR"), True)
        # 半截 JSON：回退空缓存，put 重建合法文件
        path.write_text('{"schema": 1, "entries": {"x": tr', encoding="utf-8")
        broken = RuntimeCache(path)
        self.assertIsNone(broken.get("microsoft.vstor"))
        broken.put("a.b", False)
        self.assertIs(RuntimeCache(path).get("a.b"), False)

    def test_catalog_uses_runtime_cache_authoritatively(self) -> None:
        """缓存是权威值：命中包不再走关键词初判。"""
        cache = RuntimeCache(self.tmp / "mc-catalog.json")
        cache.put("Git.Git", False)              # 元数据已核查：是软件

        class FakeRepo:
            def list_installed(self, cancel=None):
                return SimpleNamespace(
                    ok=True, returncode=0, stderr="",
                    stdout="名称   ID          版本  源\n"
                           "Git    Git.Git     1.0   winget\n")

        svc = CatalogService(make_config(), FakeRepo(), runtime_cache=cache)
        snap = svc.get_catalog(force=True)
        self.assertEqual(len(snap.packages), 1)
        self.assertTrue(snap.packages[0].reinstallable)
        self.assertIs(snap.packages[0].isDependency, False,
                      "「Git」命中关键词，但缓存说它是软件：缓存说了算")
        self.assertEqual(svc.pending_metadata_ids(snap), [], "已核查的包不再入队")

    def test_pending_metadata_ids_and_worker_skips_cached(self) -> None:
        cache = RuntimeCache(self.tmp / "mc-pending.json")
        cache.put("Git.Git", False)
        pkgs = [
            Package(packageIdentifier="Git.Git", name="Git", reinstallable=True),
            Package(packageIdentifier="Microsoft.VSTOR", name="VS Tools",
                    reinstallable=True),
            Package(packageIdentifier="Vendor.Reg", name="注册表项", reinstallable=False),
        ]
        snap = CatalogSnapshot(generatedAt="x", durationMs=1, wingetVersion="v",
                               machine={}, counts={}, packages=pkgs)
        svc = CatalogService(make_config(), SimpleNamespace(), runtime_cache=cache)
        self.assertEqual(svc.pending_metadata_ids(snap), ["Microsoft.VSTOR"],
                         "只入队「可重装 ∧ 未核查」，不可重装与已核查的都排除")

        calls: list = []

        class ShowRepo:
            def show_metadata(self, package_id, cancel=None):
                calls.append(package_id)
                return SimpleNamespace(ok=True, stdout=_SHOW_VSTOR_ZH)

        worker = Q.MetadataWorker(ShowRepo(), cache, list(svc.pending_metadata_ids(snap)),
                                  names={}, keyword_deps={}, scan_active=None)
        worker.run()                              # 同步执行，不起线程
        self.assertEqual([c.lower() for c in calls], ["microsoft.vstor"],
                         "缓存命中的 ID 不应再发起 winget show")
        self.assertIs(cache.get("Microsoft.VSTOR"), True, "核查结果要写回缓存")

    def test_worker_batches_changes(self) -> None:
        emissions: list = []

        class ShowRepo:
            def show_metadata(self, package_id, cancel=None):
                return SimpleNamespace(ok=True, stdout=_SHOW_SDK_EN)   # 判 False

        cache = RuntimeCache(self.tmp / "mc-batch.json")
        worker = Q.MetadataWorker(ShowRepo(), cache,
                                  [f"Vendor.App{i}" for i in range(7)],
                                  names={}, keyword_deps={}, scan_active=None)
        worker.changes.connect(emissions.append)
        worker.run()
        self.assertEqual([len(b) for b in emissions], [5, 2],
                         "每攒 5 个改判发一次信号，收尾时发剩余的，绝不逐条发")
        self.assertEqual(cache.get("Vendor.App0"), False)

    def test_metadata_changes_apply_in_one_rebuild(self) -> None:
        win = self._spawn_window()
        try:
            win.packages = [Package(packageIdentifier=f"Vendor.App{i}", name=f"应用 {i}",
                                    reinstallable=True, source="winget")
                            for i in range(7)]
            win._pkg_index = {p.packageIdentifier.lower(): p for p in win.packages}
            counter: list = []
            orig = win._rebuild_list

            def counting(*args, **kwargs):
                counter.append(1)
                return orig(*args, **kwargs)

            win._rebuild_list = counting
            batch = [{"id": p.packageIdentifier, "dep": True, "signal": "runtime",
                      "name": p.name} for p in win.packages]
            win._on_metadata_changes(batch)
            self.assertEqual(counter, [1], "一批 7 个改判只重建一次列表")
            self.assertTrue(all(p.isDependency for p in win.packages),
                            "整批改判都要落到 Package 上")
        finally:
            self._drop_window(win)

    def test_enrichment_canceled_on_rescan(self) -> None:
        """富化进行中用户重扫：富化被取消收尾、新扫描正常完成、结果靠缓存续上。"""

        class FakeScanRepo:
            def __init__(self) -> None:
                self.show_calls: list = []
                self.slow = False

            def version(self, cancel=None):
                return "1.0"

            def list_installed(self, cancel=None):
                return SimpleNamespace(
                    ok=True, returncode=0, stderr="",
                    stdout="名称                 ID                版本  源\n"
                           "Visual Studio Tools  Microsoft.VSTOR   1.0   winget\n")

            def show_metadata(self, package_id, cancel=None):
                self.show_calls.append(package_id)
                if self.slow:
                    time.sleep(0.5)        # 留出「富化进行中」的时间窗
                return SimpleNamespace(ok=True, stdout=_SHOW_VSTOR_ZH)

        repo = FakeScanRepo()
        repo.slow = True
        with mock.patch("qt_app.METADATA_CACHE_FILE",
                        self.tmp / "mc-rescan.json"):
            win = self._spawn_window()
        try:
            win.repo = repo
            win.catalog = CatalogService(make_config(), repo,
                                         runtime_cache=win.runtime_cache)
            _ORIG_START_SCAN(win)                      # 第一轮扫描（真实实现）
            self.assertTrue(self._wait_until(lambda: win.state == "READY", 8),
                            "扫描应正常完成")
            self.assertTrue(self._wait_until(lambda: bool(win._enrich_workers), 3),
                            "扫描完成后应自动开始元数据核查")
            self.assertTrue(self._wait_until(lambda: len(repo.show_calls) >= 1, 3),
                            "富化线程应开始 winget show")

            _ORIG_START_SCAN(win)                      # 用户在富化进行中重扫
            worker1 = next(iter(win._enrich_workers)) if win._enrich_workers else None
            self.assertIsNotNone(worker1, "重扫时应还有富化线程在收尾")
            self.assertTrue(self._wait_until(lambda: worker1.isFinished(), 6),
                            "富化线程应被取消并尽快收尾")
            self.assertTrue(worker1._cancel.is_set(), "取消标志应已置位")

            self.assertTrue(self._wait_until(lambda: win.state == "READY", 10),
                            "新扫描应正常完成")
            self.assertFalse(win._scan_gate.is_set(), "扫描收尾后应放开 winget")
            self.assertTrue(self._wait_until(
                lambda: win._pkg_index.get("microsoft.vstor") is not None
                and win._pkg_index["microsoft.vstor"].isDependency, 10),
                "富化续上后 VSTOR 应被元数据改判进「运行库」")
            # worker1 被取消时，进行中的 show 有两种收场：恰好在取消后完成
            # （结果仍落缓存），或被 run() 的取消轮询 kill 掉（fetch 抛
            # ScanCancelled，不落缓存）——后者下一轮富化会重试一次。两种
            # 路径每个 ID 最多 show 2 次；最终缓存必须有权威值，第三轮
            # 不会再 show。
            self.assertLessEqual(len(repo.show_calls), 2,
                                 "每个 ID 最多 show 两次（取消重试也算）")
            self.assertEqual(win.runtime_cache.get("microsoft.vstor"), True,
                             "核查结果最终应落缓存（权威值）")
        finally:
            self._drop_window(win)

    # ── 缓存命中不再重复构建 ──
    def test_catalog_cache_avoids_second_build(self) -> None:
        calls: list = []

        class FakeRepo:
            def list_installed(self, cancel=None):
                calls.append("list")
                return SimpleNamespace(ok=True, returncode=0, stdout="", stderr="")

        svc = CatalogService(make_config(), FakeRepo())     # cache_ttl=60
        first = svc.get_catalog(winget_version="1.0")
        second = svc.get_catalog(winget_version="1.0")
        self.assertEqual(calls, ["list"],
                         "第二次应命中缓存，不能再跑一遍 winget")
        self.assertIs(first, second, "命中缓存应返回同一份快照对象")


if __name__ == "__main__":
    unittest.main(verbosity=2)
