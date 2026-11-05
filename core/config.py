"""集中配置：所有可变项来自环境变量，启动时一次性校验。

原则：**缺 winget 不是致命错误**。装到 Program Files 的机器上也可能没有 App
Installer，程序要能起来并把用户引导到安装页，而不是弹个框就退出。
"""

from __future__ import annotations

import os
import shutil
import sys
from dataclasses import dataclass, field
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent.parent


class ConfigError(RuntimeError):
    """配置非法时抛出；`load_config` 只捕获 winget 探测这一处，其余仍是硬错误。"""


@dataclass(frozen=True)
class Config:
    winget_exe: str
    cache_ttl: int
    command_timeout: int
    extra: dict = field(default_factory=dict)


def _env_int(name: str, default: int, minimum: int, maximum: int) -> int:
    raw = os.environ.get(name, "").strip()
    if not raw:
        return default
    try:
        value = int(raw)
    except ValueError as exc:
        raise ConfigError(f"环境变量 {name} 必须是整数，当前为 {raw!r}") from exc
    if not (minimum <= value <= maximum):
        raise ConfigError(f"环境变量 {name} 必须在 {minimum}~{maximum} 之间，当前为 {value}")
    return value


def find_winget() -> str:
    """定位 winget.exe；找不到说明系统缺少 App Installer。"""
    override = os.environ.get("WPM_WINGET_EXE", "").strip()
    if override:
        path = Path(override)
        if not path.is_file():
            raise ConfigError(f"WPM_WINGET_EXE 指向的文件不存在: {override}")
        return str(path)
    found = shutil.which("winget")
    if found:
        return found
    candidate = Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft/WindowsApps/winget.exe"
    if candidate.is_file():
        return str(candidate)
    raise ConfigError(
        "未找到 winget.exe。请先安装「应用安装程序 / App Installer」"
        "（https://aka.ms/getwinget），或通过 WPM_WINGET_EXE 指定路径。"
    )


def load_config() -> Config:
    """读取环境变量。winget 探测失败只记录原因，不让启动失败。"""
    extra: dict = {"python": sys.version.split()[0], "base_dir": str(BASE_DIR)}
    try:
        winget_exe = find_winget()
    except ConfigError as exc:
        # 非致命：主窗口照常起来，ScanWorker 会把 winget_error 转成 missing 信号，
        # 由 _show_winget_guide 给出「去商店安装」的操作引导。
        winget_exe = ""
        extra["winget_error"] = str(exc)

    return Config(
        winget_exe=winget_exe,
        cache_ttl=_env_int("WPM_CACHE_TTL", 60, 0, 3600),
        command_timeout=_env_int("WPM_COMMAND_TIMEOUT", 180, 10, 3600),
        extra=extra,
    )
