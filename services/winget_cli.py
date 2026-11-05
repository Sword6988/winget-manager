"""仓储层：唯一与 winget.exe 交互的地方。

约定：
- 所有命令统一加 --disable-interactivity / --accept-source-agreements，保证非交互可被服务调用；
- 输出统一解码为 str（winget 重定向时输出 UTF-8，个别环境回落 GBK）；
- 每条命令都可传入 cancel（threading.Event），置位时直接 kill 子进程并抛 ScanCancelled。
"""

from __future__ import annotations

import locale
import os
import subprocess
import threading
import time
from dataclasses import dataclass
from typing import Iterable

from core.config import Config
from core.errors import ScanCancelled, WingetCommandFailed, WingetNotFound
from core.logging_setup import get_logger

logger = get_logger("winget.cli")

_CREATE_NO_WINDOW = 0x08000000 if os.name == "nt" else 0
# 轮询间隔：太小空转烧 CPU，太大取消手感发钝；0.2s 下用户最多等半拍
_POLL_S = 0.2

# winget 有进程级全局互斥：已有实例在跑时，新进程会在 ~190ms 内空输出退出，
# 退出码为 0x8A150001（十进制 2316632065）。实测 `--version` 并发 4/10 失败，
# list 与 export 并发 19/35 失败——扫描因此必须能自己重试。
_MUTEX_EXIT_CODE = 0x8A150001
_MUTEX_RETRY_MAX = 2        # 最多重试 2 次；配合 0.4s 退避，最坏多花约 1.2s
_MUTEX_BACKOFF_S = 0.4


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


def _is_mutex_conflict(result: CommandResult) -> bool:
    """这次失败是不是「winget 被别的实例占用」。

    实测冲突时 winget 会以退出码 0x8A150001 在 ~190ms 内空输出退出；
    个别版本不回这个码、只是失败且一个字都不输出——两者对调用方的含义一样，
    都是「等一会儿再来」，所以一并按互斥冲突处理。重试有次数上限，
    真出错时最坏只是多花 0.8s，不会无限重试。
    """
    if result.returncode == _MUTEX_EXIT_CODE:
        return True
    return result.returncode != 0 and not result.combined.strip()


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
        """执行一条 winget 命令；撞上 winget 互斥会自动退避重试。

        winget 有进程级全局互斥，外部任何 winget 进程在跑都会让本次调用
        在 ~190ms 内空输出失败。删掉 export 之后 list 是扫描唯一的子进程，
        不重试的话「外部占一下就整次扫描失败」。
        """
        if not self._exe:
            # 启动时就没探测到 winget：直接给出安装引导文案，而不是让 subprocess
            # 抛一个「参数为空」的晦涩异常。ScanWorker 会把它转成 missing 信号。
            raise WingetNotFound(
                str(self._config.extra.get("winget_error") or "未找到 winget.exe"))
        argv = [self._exe, *args]
        limit = timeout or self._config.command_timeout
        started = time.perf_counter()
        # 绝对截止时间：重试的退避时间也算在同一份额度里，避免重试把总耗时拉爆
        deadline = started + limit
        attempt = 0
        while True:
            result = self._run_once(argv, deadline, limit, cancel)
            if (result.ok or attempt >= _MUTEX_RETRY_MAX
                    or not _is_mutex_conflict(result)
                    or not self._wait_backoff(_MUTEX_BACKOFF_S, deadline, cancel, argv)):
                break
            attempt += 1
            logger.info("撞上 winget 互斥（rc=%s），第 %s 次重试：%s",
                        result.returncode, attempt, " ".join(args))
        result.duration_ms = int((time.perf_counter() - started) * 1000)   # 含退避
        return result

    def _run_once(self, argv: list[str], deadline: float, total_limit: int,
                  cancel: threading.Event | None) -> CommandResult:
        """跑一次子进程。

        用 Popen + 0.2s 轮询而不是 `subprocess.run`：`run` 是阻塞的，取消只能等
        超时（默认 180s）——用户点「取消扫描」后界面要僵住三分钟，体验上等同卡死。
        轮询版每 0.2s 检查一次 cancel，置位就 kill，真正返回。
        """
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
                        logger.info("winget %s 已被取消", " ".join(argv[1:]))
                        raise ScanCancelled(
                            f"winget 命令已取消: {' '.join(argv[1:])}") from None
                    if time.perf_counter() >= deadline:
                        proc.kill()
                        out_b, err_b = proc.communicate()
                        raise WingetCommandFailed(
                            f"winget 命令超时（>{total_limit}s，含重试退避）") from None
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
            " ".join(argv[1:]),
            result.returncode,
            duration,
            extra={"extra_fields": {"argv": argv[1:], "rc": result.returncode, "ms": duration}},
        )
        return result

    @staticmethod
    def _wait_backoff(seconds: float, deadline: float,
                      cancel: threading.Event | None, argv: list[str]) -> bool:
        """退避等待；返回 False 表示时间预算已用完，不该再重试。

        用 `Event.wait` 而不是 `time.sleep`：命中取消时立刻醒来抛 ScanCancelled，
        不会把「取消」拖到退避结束之后。
        """
        if time.perf_counter() + seconds > deadline:
            return False
        if cancel is not None:
            if cancel.wait(seconds):
                raise ScanCancelled(f"winget 命令已取消: {' '.join(argv[1:])}")
        else:
            time.sleep(seconds)
        return True

    @staticmethod
    def _env() -> dict:
        env = dict(os.environ)
        env.setdefault("PYTHONIOENCODING", "utf-8")
        return env

    # ── 具体能力 ────────────────────────────────────────────────
    def version(self, cancel: threading.Event | None = None) -> str:
        """winget 版本号；拿不到就返回 "unknown"。

        注意别把 "unknown" 当成「没装 winget」：`winget --version` 与别的
        winget 实例并发时可能重试完仍空输出（实测 4/10），而软件其实装着。
        判定「没装」只看 run() 抛出的 WingetNotFound。
        """
        result = self.run(["--version"], timeout=30, cancel=cancel)
        if not result.combined.strip():
            logger.warning("winget --version 无输出（rc=%s），按 unknown 继续",
                           result.returncode)
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
