"""入口：默认启动本地服务，也支持 list / export / import 三个命令行子命令。"""

from __future__ import annotations

import argparse
import json
import signal
import sys
import threading
import time
import webbrowser
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent
if str(BASE_DIR) not in sys.path:
    sys.path.insert(0, str(BASE_DIR))

from core.config import ConfigError, load_config  # noqa: E402
from core.logging_setup import get_logger, setup_logging  # noqa: E402


def _bootstrap():
    try:
        config = load_config()
    except ConfigError as exc:
        print(f"[启动失败] {exc}", file=sys.stderr)
        raise SystemExit(2) from exc
    setup_logging(config.log_level, config.log_file)
    return config


# ── 子命令 ────────────────────────────────────────────────────
def cmd_serve(args) -> int:
    config = _bootstrap()
    if getattr(args, "port", None):
        config = load_config_with(port=args.port)
    from server import create_server

    server, ctx = create_server(config)
    url = f"http://{config.host}:{config.port}/"

    logger = get_logger("app")
    logger.info("服务已启动: %s (winget=%s)", url, config.winget_exe)
    print("=" * 64)
    print("  Winget 软件包管理器已启动")
    print(f"  地址      : {url}")
    print(f"  winget    : {config.winget_exe}")
    print(f"  导出目录  : {config.export_dir}")
    print("  关闭方式  : 在本窗口按 Ctrl+C")
    print("=" * 64)

    if config.open_browser and not getattr(args, "no_browser", False):
        threading.Timer(0.8, lambda: webbrowser.open(url)).start()

    stopping = threading.Event()

    def shutdown(signum, frame):  # noqa: ARG001
        if stopping.is_set():
            return
        stopping.set()
        print("\n正在关闭服务…")
        threading.Thread(target=server.shutdown, daemon=True).start()

    signal.signal(signal.SIGINT, shutdown)
    try:
        signal.signal(signal.SIGTERM, shutdown)
    except (AttributeError, ValueError):
        pass

    try:
        server.serve_forever(poll_interval=0.3)
    finally:
        server.server_close()
        logger.info("服务已停止")
    return 0


def load_config_with(**overrides):
    """CLI 参数覆盖环境变量。"""
    import os

    mapping = {"port": "WPM_PORT"}
    for key, value in overrides.items():
        if value is not None and key in mapping:
            os.environ[mapping[key]] = str(value)
    return load_config()


def _fit(text: str, width: int) -> str:
    """按显示宽度补齐/截断，保证中文表格对齐。"""
    from services.winget_table import display_width

    text = text or ""
    while display_width(text) > width - 1:
        text = text[:-1]
    return text + " " * (width - display_width(text))


def cmd_list(args) -> int:
    config = _bootstrap()
    from services.catalog import CatalogService
    from services.winget_cli import WingetRepository

    service = CatalogService(config, WingetRepository(config))
    snapshot = service.get_catalog(force=True)
    if args.json:
        print(json.dumps(snapshot.to_dict(), ensure_ascii=False, indent=2))
        return 0
    print(_fit("名称", 40) + _fit("包 ID", 52) + _fit("版本", 20) + "可重装")
    print("-" * 118)
    for package in snapshot.packages:
        print(
            _fit(package.name, 40)
            + _fit(package.packageIdentifier, 52)
            + _fit(package.version, 20)
            + ("是" if package.reinstallable else "否")
        )
    counts = snapshot.counts
    print("-" * 118)
    print(f"共 {counts['total']} 个包，可从 winget 重装 {counts['reinstallable']} 个")
    return 0


def cmd_export(args) -> int:
    config = _bootstrap()
    from services.catalog import CatalogService
    from services.profiles import write_exports
    from services.winget_cli import WingetRepository

    service = CatalogService(config, WingetRepository(config))
    snapshot = service.get_catalog(force=True)
    if args.ids:
        wanted = {item.strip().lower() for item in args.ids.split(",") if item.strip()}
        selected = [p for p in snapshot.packages if p.packageIdentifier.lower() in wanted]
    else:
        selected = list(snapshot.packages)
    if not selected:
        print("没有匹配的软件包", file=sys.stderr)
        return 1
    files = write_exports(
        config.export_dir,
        args.name or "cli-export",
        "由命令行导出",
        selected,
        snapshot,
        snapshot.wingetVersion,
        want_profile=not args.only_winget,
        want_winget=True,
    )
    print(f"已导出 {files.package_count} 个包:")
    for path in (files.profile_path, files.winget_path):
        if path:
            print(f"  {path}")
    if files.skipped:
        print(f"另有 {len(files.skipped)} 个包不支持 winget 重装，已记入 skippedPackages")
    return 0


def cmd_import(args) -> int:
    config = _bootstrap()
    from services.profiles import load_profile
    from services.winget_cli import WingetRepository

    path = Path(args.file).expanduser()
    profile = load_profile(path)
    print(f"读取到 {len(profile.packages)} 个软件包（{profile.format} 格式），开始导入…")
    if args.dry_run:
        for item in profile.packages:
            print("  -", item["packageIdentifier"])
        return 0
    repo = WingetRepository(config)
    started = time.perf_counter()
    rc = repo.import_file(profile.path, on_line=lambda line: print(line, flush=True))
    print(f"winget 退出码 {rc}，耗时 {time.perf_counter() - started:.1f}s")
    return 0 if rc == 0 else 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="winget-manager", description="winget 软件包管理器：枚举 / 勾选 / 导出 / 恢复"
    )
    sub = parser.add_subparsers(dest="command")

    serve = sub.add_parser("serve", help="启动本地图形界面（默认）")
    serve.add_argument("--port", type=int, default=None, help="监听端口，默认 8765")
    serve.add_argument("--no-browser", action="store_true", help="不自动打开浏览器")
    serve.set_defaults(func=cmd_serve)

    lister = sub.add_parser("list", help="在命令行列出已安装的软件包")
    lister.add_argument("--json", action="store_true", help="以 JSON 输出")
    lister.set_defaults(func=cmd_list)

    exporter = sub.add_parser("export", help="按条件直接导出配置")
    exporter.add_argument("-o", "--ids", default="", help="逗号分隔的包 ID，留空表示全部")
    exporter.add_argument("-n", "--name", default="", help="配置名称")
    exporter.add_argument("--only-winget", action="store_true", help="只输出 winget 原生格式")
    exporter.set_defaults(func=cmd_export)

    importer = sub.add_parser("import", help="从 JSON 配置批量安装")
    importer.add_argument("file", help="JSON 配置文件路径")
    importer.add_argument("--dry-run", action="store_true", help="只解析不安装")
    importer.set_defaults(func=cmd_import)

    args = parser.parse_args(argv)
    if not getattr(args, "command", None):
        args = serve.parse_args([])
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
