"""仓储层：唯一与 winget.exe 交互的地方。

约定：
- 所有命令统一加 --disable-interactivity / --accept-source-agreements，保证非交互可被服务调用；
- 输出统一解码为 str（winget 重定向时输出 UTF-8，个别环境回落 GBK）；
- 每条命令都可传入 cancel（threading.Event），置位时直接 kill 子进程并抛 ScanCancelled。
"""

from __future__ import annotations

import json
import locale
import os
import subprocess
import tempfile
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

from core.config import Config
from core.errors import ScanCancelled, WingetCommandFailed, WingetNotFound
from core.logging_setup import get_logger

logger = get_logger("winget.cli")

_CREATE_NO_WINDOW = 0x08000000 if os.name == "nt" else 0
# 轮询间隔：太小空转烧 CPU，太大取消手感发钝；0.2s 下用户最多等半拍
_POLL_S = 0.2


@dataclass
class CommandResult:
    args: list[str]
    returncode: int
    stdout: str
    stderr: str
    duration_ms: int

    @property
    def ok(self) -> bool:
        return self.returncode == 0

    @property
    def combined(self) -> str:
        return (self.stdout or "") + ("\n" + self.stderr if self.stderr else "")


def _decode(raw: bytes) -> str:
    """把 winget 的输出字节解成 str。

    顺序很重要：`utf-8-sig` 必须先试，否则 BOM 会变成 U+FEFF 混进第一行，
    后面按行首匹配包 ID / 版本号的地方都会莫名其妙地失败；之后才是 utf-8，
    再回落到本机首选编码（中文 Windows 一般是 cp936），最后才是容错解码。
    """
    for encoding in ("utf-8-sig", "utf-8"):
        try:
            return raw.decode(encoding)
        except UnicodeDecodeError:
            continue
    for encoding in (locale.getpreferredencoding(False), "gbk"):
        try:
            return raw.decode(encoding)
        except (UnicodeDecodeError, LookupError):
            continue
    return raw.decode("utf-8", errors="replace")


class WingetRepository:
    def __init__(self, config: Config):
        self._config = config
        self._exe = config.winget_exe

    # ── 基础执行 ────────────────────────────────────────────────
    def run(self, args: Iterable[str], timeout: int | None = None,
            cancel: threading.Event | None = None) -> CommandResult:
        """执行一条 winget 命令。

        用 Popen + 0.2s 轮询而不是 `subprocess.run`：`run` 是阻塞的，取消只能等
        超时（默认 180s）——用户点「取消扫描」后界面要僵住三分钟，体验上等同卡死。
        轮询版每 0.2s 检查一次 cancel，置位就 kill，真正返回。
        """
        if not self._exe:
            # 启动时就没探测到 winget：直接给出安装引导文案，而不是让 subprocess
            # 抛一个「参数为空」的晦涩异常。ScanWorker 会把它转成 missing 信号。
            raise WingetNotFound(
                str(self._config.extra.get("winget_error") or "未找到 winget.exe"))
        argv = [self._exe, *args]
        limit = timeout or self._config.command_timeout
        started = time.perf_counter()
        try:
            proc = subprocess.Popen(
                argv,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                creationflags=_CREATE_NO_WINDOW,
                env=self._env(),
            )
        except FileNotFoundError as exc:
            raise WingetNotFound(f"无法执行 {self._exe}") from exc

        out_b, err_b = b"", b""
        deadline = started + limit
        try:
            while True:
                try:
                    # communicate 会累积已读到的输出，反复调用是安全的
                    out_b, err_b = proc.communicate(timeout=_POLL_S)
                    break
                except subprocess.TimeoutExpired:
                    if cancel is not None and cancel.is_set():
                        proc.kill()
                        out_b, err_b = proc.communicate()
                        logger.info("winget %s 已被取消", " ".join(args))
                        raise ScanCancelled(
                            f"winget 命令已取消: {' '.join(argv[1:])}") from None
                    if time.perf_counter() >= deadline:
                        proc.kill()
                        out_b, err_b = proc.communicate()
                        raise WingetCommandFailed(
                            f"winget 命令超时（>{limit}s）") from None
        finally:
            # 任何异常路径都别把子进程留在后台偷偷跑
            if proc.poll() is None:
                proc.kill()
                proc.communicate()

        duration = int((time.perf_counter() - started) * 1000)
        result = CommandResult(
            args=argv,
            returncode=proc.returncode,
            stdout=_decode(out_b or b""),
            stderr=_decode(err_b or b""),
            duration_ms=duration,
        )
        logger.info(
            "winget %s -> %s (%sms)",
            " ".join(args),
            result.returncode,
            duration,
            extra={"extra_fields": {"argv": argv[1:], "rc": result.returncode, "ms": duration}},
        )
        return result

    @staticmethod
    def _env() -> dict:
        env = dict(os.environ)
        env.setdefault("PYTHONIOENCODING", "utf-8")
        return env

    # ── 具体能力 ────────────────────────────────────────────────
    def version(self, cancel: threading.Event | None = None) -> str:
        result = self.run(["--version"], timeout=30, cancel=cancel)
        if not result.combined.strip():
            return "unknown"
        raw = (result.stdout or result.stderr).strip().splitlines()[0].strip()
        # winget 输出形如 "v1.30.100-preview"，但 import 的 schema 只接受数字开头
        return raw[1:] if raw[:1].lower() == "v" else raw

    def list_installed(self, cancel: threading.Event | None = None) -> CommandResult:
        result = self.run(
            ["list", "--accept-source-agreements", "--disable-interactivity"],
            timeout=max(self._config.command_timeout, 60),
            cancel=cancel,
        )
        if not result.ok and not result.stdout.strip():
            raise WingetCommandFailed(result.combined.strip() or "winget list 无输出")
        return result

    def export_ids(self, cancel: threading.Event | None = None) -> tuple[set[str], str | None]:
        """借助官方 export 拿到「可被 winget 重装」的包 ID 集合。

        返回值第二个元素是「人话错误」：拿不到清单时必须能被界面上看见——
        早先这里只 logger.warning，结果整屏软件都被当成「不可重装」，用户毫无头绪。
        """
        fd, path = tempfile.mkstemp(prefix="wpm-export-", suffix=".json")
        os.close(fd)
        try:
            result = self.run(
                ["export", "-o", path, "--accept-source-agreements", "--disable-interactivity"],
                timeout=max(self._config.command_timeout, 120),
                cancel=cancel,
            )
            ids: set[str] = set()
            parse_error = ""
            file_path = Path(path)
            if file_path.is_file() and file_path.stat().st_size > 0:
                try:
                    payload = json.loads(file_path.read_text(encoding="utf-8"))
                    for source in payload.get("Sources", []):
                        for pkg in source.get("Packages", []):
                            pid = pkg.get("PackageIdentifier")
                            if pid:
                                ids.add(pid)
                except (json.JSONDecodeError, OSError) as exc:
                    parse_error = f"export 结果解析失败：{exc}"
                    logger.warning("export 结果解析失败: %s", exc)
            if ids:
                return ids, None
            detail = (result.combined.strip().splitlines() or [""])[0].strip()[:200]
            return set(), (
                parse_error
                or detail
                or f"winget export 退出码 {result.returncode}，未产出任何可重装包"
            )
        finally:
            try:
                os.unlink(path)
            except OSError:
                pass
