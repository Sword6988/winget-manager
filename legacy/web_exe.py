"""Winget 软件管家（Web 版）exe 入口。

双击运行：自动挑选空闲端口 → 启动本地服务 → 自动打开浏览器。
关闭本窗口即退出服务。开发时也可 `python web_exe.py` 直接运行。
"""

from __future__ import annotations

import os
import socket
import sys
import threading
import webbrowser
from pathlib import Path

# 打包成 exe 后：导出目录放在 exe 旁边，而不是临时解压目录
if getattr(sys, "frozen", False):
    BASE_DIR = Path(sys.executable).resolve().parent
    os.environ.setdefault("WPM_EXPORT_DIR", str(BASE_DIR / "exports"))
else:
    BASE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BASE_DIR))

from core.config import ConfigError, load_config  # noqa: E402


def _free_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def main() -> int:
    if not os.environ.get("WPM_PORT"):
        os.environ["WPM_PORT"] = str(_free_port())
    os.environ.setdefault("WPM_OPEN_BROWSER", "false")
    try:
        config = load_config()
    except ConfigError as exc:
        print(f"[启动失败] {exc}")
        input("按回车键退出…")
        return 1

    from core.logging_setup import setup_logging

    setup_logging(config.log_level, None)

    from server import create_server

    server, _ctx = create_server(config)
    url = f"http://{config.host}:{config.port}/"

    print("=" * 60)
    print("  Winget 软件管家（网页版）")
    print(f"  界面地址 : {url}")
    print("           浏览器将自动打开，请勿关闭本窗口")
    print(f"  导出目录 : {config.export_dir}")
    print("  退出方式 : 直接关闭本窗口即可")
    print("=" * 60)

    threading.Timer(1.0, lambda: webbrowser.open(url)).start()
    try:
        server.serve_forever(poll_interval=0.3)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
        print("服务已停止。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
