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
from core.errors import BadRequest, ScanCancelled  # noqa: E402
from core.fsio import atomic_write_json  # noqa: E402
from services.catalog import CatalogService, CatalogSnapshot, Package  # noqa: E402
from services.profiles import build_restore_bat, build_winget_manifest  # noqa: E402
from services.winget_cli import WingetRepository, _decode  # noqa: E402

# 真实的 _start_scan（setUpClass 打桩前保存），放在模块级才不会被描述符绑成方法
_ORIG_START_SCAN = None


def make_config() -> Config:
    """直接构造配置：走 load_config() 会真的去探测 winget，测试不该依赖机器状态。"""
    return Config(winget_exe="", cache_ttl=60, command_timeout=5, extra={})


def make_packages() -> list:
    """三类可重装 + 一堆不可重装，覆盖「其他」兜底分组。"""
    data = [
        ("Google.Chrome", "Google Chrome", "浏览器", True),
        ("Mozilla.Firefox", "Mozilla Firefox", "浏览器", True),
        ("Microsoft.Edge", "Microsoft Edge", "浏览器", True),
        ("Git.Git", "Git", "开发工具", True),
        ("Python.Python.3.14", "Python 3.14", "开发工具", True),
        ("Microsoft.VisualStudioCode", "Visual Studio Code", "开发工具", True),
        ("7zip.7zip", "7-Zip", "效率与系统工具", True),
        ("voidtools.Everything", "Everything", "效率与系统工具", True),
        ("Valve.Steam", "Steam", "游戏", True),
        ("Huorong.Huorong", "火绒安全", "安全", True),
    ]
    pkgs = [Package(packageIdentifier=i, name=n, category=c, reinstallable=r,
                    version="1.0.0" if r else "", source="winget" if r else "")
            for i, n, c, r in data]
    # 不可重装：散落在各分类，最终都该进「其他」
    for i in range(8):
        pkgs.append(Package(packageIdentifier=f"Vendor.Tool{i}", name=f"参考组件 {i}",
                            category=["浏览器", "开发工具", "游戏"][i % 3],
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
        # 勾选状态落到临时目录，别碰真实的 %LOCALAPPDATA%
        cls._patches = [
            mock.patch("qt_app.SELECTION_FILE", cls.tmp / "selection.json"),
            mock.patch("qt_app.PERSIST_DIR", cls.tmp),
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
    def test_sections_built(self) -> None:
        self.assertGreater(len(self.win.sections), 1)
        self.assertEqual(len(self.win.rows), len(self.pkgs))
        self.assertEqual(self.win.sections[-1].title, Q.OTHER_CAT,
                         "「其他」必须排在最后")

    def test_other_group_all_grey(self) -> None:
        other = [r for r in self.win.rows.values() if self.win._group_of(r.pkg) == Q.OTHER_CAT]
        self.assertTrue(other)
        for r in other:
            self.assertFalse(r.selectable, "「其他」组内应为不可勾选的灰行")
            self.assertFalse(r.pkg.reinstallable)
        self.assertFalse(self.win.sections[-1].header.sel_all.isVisible(),
                         "整组不可勾时不应显示「全选本组」")

    def test_group_toggle(self) -> None:
        self.win._toggle_group("浏览器")
        self.app.processEvents()
        rows = [r for r in self.win.rows.values()
                if self.win._group_of(r.pkg) == "浏览器" and r.selectable]
        self.assertTrue(rows)
        self.assertTrue(all(r._checked for r in rows), "全选本组应勾上所有可勾行")
        self.win._toggle_group("浏览器")   # 复位
        self.app.processEvents()

    # ── 吸顶分组头 ──
    def test_sticky_follows_scroll(self) -> None:
        sb = self.win.scroll.verticalScrollBar()
        sb.setValue(0)
        self.app.processEvents()
        self.assertFalse(self.win.sticky.isVisible(), "顶部时不应出现吸顶头")

        if len(self.win.sections) < 2 or sb.maximum() <= 0:
            self.skipTest("内容不足一屏，无法验证滚动")
        second = self.win.sections[1]
        y = second.mapTo(self.win.rows_host, QPoint(0, 0)).y()
        sb.setValue(min(y + 20, sb.maximum()))
        self.app.processEvents()
        if sb.value() <= 20:
            self.skipTest("窗口过小，滚不到第二组")
        self.assertTrue(self.win.sticky.isVisible(), "滚过组头后应出现吸顶头")
        self.assertEqual(self.win.sticky._title, second.title)

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
        self.assertIn("【", text, "应按分类分组")
        self.assertNotIn("Google Chrome", text, "可重装的包不该出现在核对清单里")
        self.assertTrue(text.startswith("Winget 软件管家"))

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

    # ── 导出探测失败要可见 ──
    def test_catalog_export_failure_sets_error(self) -> None:
        class FakeRepo:
            def list_installed(self, cancel=None):
                return SimpleNamespace(ok=True, returncode=0,
                                       stdout="名称      ID         版本\n"
                                              "Git       Git.Git    2.0\n",
                                       stderr="")

            def export_ids(self, cancel=None):
                return set(), "源不可用"

        svc = CatalogService(make_config(), FakeRepo())
        snap = svc.get_catalog(force=True, winget_version="1.10.0")
        self.assertEqual(snap.exportError, "源不可用")
        self.assertEqual(snap.counts["reinstallable"], 0, "拿不到清单时没有包可判为可重装")
        self.assertEqual(snap.wingetVersion, "1.10.0", "版本号应由 ScanWorker 传入")
        self.assertTrue(snap.packages, "包本身仍应被列出")

    # ── 缓存命中不再重复构建 ──
    def test_catalog_cache_avoids_second_build(self) -> None:
        calls: list = []

        class FakeRepo:
            def list_installed(self, cancel=None):
                calls.append("list")
                return SimpleNamespace(ok=True, returncode=0, stdout="", stderr="")

            def export_ids(self, cancel=None):
                calls.append("export")
                return set(), None

        svc = CatalogService(make_config(), FakeRepo())     # cache_ttl=60
        first = svc.get_catalog(winget_version="1.0")
        second = svc.get_catalog(winget_version="1.0")
        self.assertEqual(calls, ["list", "export"],
                         "第二次应命中缓存，不能再跑一遍 winget")
        self.assertIs(first, second, "命中缓存应返回同一份快照对象")


if __name__ == "__main__":
    unittest.main(verbosity=2)
