"""HTTP 控制器层：只做「解析请求 → 调服务 → 格式化响应」，不含业务逻辑。"""

from __future__ import annotations

import json
import mimetypes
import re
import secrets
import threading
import time
import uuid
from collections import defaultdict, deque
from http import HTTPStatus
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from core.config import Config, STATIC_DIR
from core.errors import (
    AppError,
    BadRequest,
    NotFound,
    ProfileInvalid,
    RateLimited,
    Unauthorized,
)
from core.logging_setup import get_logger, log_extra, request_id_var
from services.catalog import CatalogService
from services.jobs import JobManager, JobEvent
from services.profiles import load_profile, write_exports
from services.winget_cli import WingetRepository

logger = get_logger("http")

MAX_BODY_BYTES = 8 * 1024 * 1024
COOKIE_NAME = "wpm_session"
# 只剔除路径分隔符与非法字符，保留中文等 Unicode 文件名
UNSAFE_NAME = re.compile(r'[\\/:*?"<>|\x00-\x1f]+')


def _safe_name(raw: str) -> str:
    """清洗文件名：去掉路径分隔符与保留字符，禁止 .. 与隐藏文件，保留中文。"""
    name = UNSAFE_NAME.sub("", str(raw or "")).strip().lstrip(".").strip()
    return name[:120]


def _resolve_in(base: Path, name: str) -> Path:
    target = (base / name).resolve()
    root = base.resolve()
    if root != target and root not in target.parents:
        raise BadRequest("文件路径不合法", user_message="文件名不合法。")
    return target


class AppContext:
    """依赖容器，启动时装配一次，避免控制器里出现全局状态。"""

    def __init__(self, config: Config):
        self.config = config
        self.session_token = secrets.token_urlsafe(24)
        self.repository = WingetRepository(config)
        self.catalog = CatalogService(config, self.repository)
        self.jobs = JobManager(self.repository, config.export_dir)
        self.rate = _RateLimiter(limit=120, window=60.0)


class _RateLimiter:
    def __init__(self, limit: int, window: float):
        self._limit = limit
        self._window = window
        self._hits: dict[str, deque[float]] = defaultdict(deque)
        self._lock = threading.Lock()

    def check(self, key: str) -> None:
        now = time.time()
        with self._lock:
            bucket = self._hits[key]
            while bucket and now - bucket[0] > self._window:
                bucket.popleft()
            if len(bucket) >= self._limit:
                raise RateLimited(f"客户端 {key} 超出速率限制")
            bucket.append(now)


class Handler(BaseHTTPRequestHandler):
    server_version = "WingetPackageManager/1.0"
    sys_version = ""
    protocol_version = "HTTP/1.1"

    ctx: AppContext  # 由工厂注入

    # ── 基础设施 ────────────────────────────────────────────────
    def log_message(self, fmt: str, *args) -> None:  # 交给结构化日志
        logger.info("%s - %s", self.address_string(), fmt % args)

    def do_GET(self) -> None:
        self._dispatch("GET")

    def do_POST(self) -> None:
        self._dispatch("POST")

    def _dispatch(self, method: str) -> None:
        request_id_var.set(uuid.uuid4().hex[:12])
        started = time.perf_counter()
        parsed = urlparse(self.path)
        path = parsed.path
        self._query = parse_qs(parsed.query)
        try:
            self._route(method, path)
        except AppError as exc:
            logger.warning("业务错误 %s: %s", exc.code, exc.detail or exc.user_message)
            if not getattr(self, "_streaming", False):
                self._send_json(exc.status, exc.to_public(request_id_var.get()))
        except (BrokenPipeError, ConnectionResetError):
            logger.info("客户端断开连接")
        except Exception as exc:  # noqa: BLE001 - 全局兜底，绝不把堆栈给客户端
            logger.exception("未处理异常: %s", exc)
            if not getattr(self, "_streaming", False):
                self._send_json(
                    500,
                    {
                        "error": {
                            "code": "internal_error",
                            "message": "服务内部错误，请查看命令行日志。",
                            "requestId": request_id_var.get(),
                        }
                    },
                )
        finally:
            logger.info(
                "%s %s -> %.1fms",
                method,
                path,
                (time.perf_counter() - started) * 1000,
                extra=log_extra(method=method, path=path),
            )

    def _route(self, method: str, path: str) -> None:
        if method == "GET" and path in ("/", "/index.html"):
            return self._serve_index()
        if method == "GET" and path == "/api/health":
            return self._send_json(200, {"status": "ok", "ts": int(time.time())})
        if path.startswith("/api/"):
            self._require_session()
            self.ctx.rate.check(self.address_string())

        if method == "GET" and path == "/api/catalog":
            return self._catalog()
        if method == "POST" and path == "/api/export":
            return self._export()
        if method == "GET" and path == "/api/exports":
            return self._list_exports()
        if method == "GET" and path == "/api/download":
            return self._download()
        if method == "POST" and path == "/api/import":
            return self._import()
        if method == "GET" and path.startswith("/api/jobs/"):
            return self._job_route(path)
        if method == "POST" and path.startswith("/api/jobs/"):
            return self._job_route(path)
        raise NotFound(f"未知路由 {method} {path}", user_message="接口不存在。")

    # ── 中间件 ──────────────────────────────────────────────────
    def _require_session(self) -> None:
        raw = self.headers.get("Cookie")
        if not raw:
            raise Unauthorized("缺少会话 Cookie")
        cookie = SimpleCookie()
        try:
            cookie.load(raw)
        except Exception as exc:  # noqa: BLE001
            raise Unauthorized("Cookie 解析失败") from exc
        morsel = cookie.get(COOKIE_NAME)
        if not morsel or not secrets.compare_digest(morsel.value, self.ctx.session_token):
            raise Unauthorized("会话令牌不匹配，请刷新页面")

    def _read_json(self) -> dict:
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            raise BadRequest("请求体为空")
        if length > MAX_BODY_BYTES:
            raise BadRequest("请求体过大", user_message="上传内容超过 8MB 限制。")
        raw = self.rfile.read(length)
        try:
            payload = json.loads(raw.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError) as exc:
            raise BadRequest(f"JSON 解析失败: {exc}") from exc
        if not isinstance(payload, dict):
            raise BadRequest("请求体必须是 JSON 对象")
        return payload

    def _send_json(self, status: int, payload: dict) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self._security_headers()
        self.end_headers()
        self.wfile.write(body)

    def _security_headers(self) -> None:
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("Cache-Control", "no-store")

    # ── 端点实现 ────────────────────────────────────────────────
    def _serve_index(self) -> None:
        index = STATIC_DIR / "index.html"
        if not index.is_file():
            raise NotFound("前端文件缺失", user_message="static/index.html 不存在。")
        body = index.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header(
            "Set-Cookie",
            f"{COOKIE_NAME}={self.ctx.session_token}; Path=/; HttpOnly; SameSite=Strict",
        )
        self.send_header(
            "Content-Security-Policy",
            "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; "
            "img-src 'self' data:; connect-src 'self'",
        )
        self._security_headers()
        self.end_headers()
        self.wfile.write(body)

    def _catalog(self) -> None:
        force = (self._query.get("refresh") or ["0"])[0] in ("1", "true")
        snapshot = self.ctx.catalog.get_catalog(force=force)
        self._send_json(200, snapshot.to_dict())

    def _export(self) -> None:
        payload = self._read_json()
        selected_ids = payload.get("selectedIds") or []
        if not isinstance(selected_ids, list):
            raise BadRequest("selectedIds 必须是数组")
        profile_name = str(payload.get("profileName") or "我的软件清单").strip()[:60]
        description = str(payload.get("description") or "").strip()[:300]
        formats = payload.get("formats") or {}
        want_profile = bool(formats.get("profile", True))
        want_winget = bool(formats.get("winget", True))

        snapshot = self.ctx.catalog.get_catalog()
        index = {p.packageIdentifier.lower(): p for p in snapshot.packages}
        selected = []
        unknown = []
        for pid in selected_ids:
            if not isinstance(pid, str):
                continue
            package = index.get(pid.lower())
            if package is None:
                unknown.append(pid)
            else:
                selected.append(package)
        if not selected:
            raise BadRequest("勾选的软件都不在当前目录中", user_message="勾选的软件已失效，请重新扫描后再导出。")

        files = write_exports(
            self.ctx.config.export_dir,
            profile_name,
            description,
            selected,
            snapshot,
            snapshot.wingetVersion,
            want_profile=want_profile,
            want_winget=want_winget,
        )
        self._send_json(
            200,
            {
                "profilePath": str(files.profile_path) if files.profile_path else None,
                "wingetPath": str(files.winget_path) if files.winget_path else None,
                "profileFile": files.profile_path.name if files.profile_path else None,
                "wingetFile": files.winget_path.name if files.winget_path else None,
                "packageCount": files.package_count,
                "skipped": files.skipped,
                "unknownIds": unknown,
            },
        )

    def _list_exports(self) -> None:
        items = []
        for path in sorted(self.ctx.config.export_dir.glob("*.json"), key=lambda p: p.stat().st_mtime, reverse=True)[:50]:
            items.append(
                {
                    "name": path.name,
                    "size": path.stat().st_size,
                    "modified": time.strftime("%Y-%m-%d %H:%M", time.localtime(path.stat().st_mtime)),
                }
            )
        self._send_json(200, {"dir": str(self.ctx.config.export_dir), "files": items})

    def _download(self) -> None:
        name = (self._query.get("file") or [""])[0]
        safe = _safe_name(name)
        if not safe or not safe.lower().endswith(".json"):
            raise BadRequest("文件名不合法", user_message="只能下载 .json 配置文件。")
        target = _resolve_in(self.ctx.config.export_dir, safe)
        if not target.is_file():
            raise NotFound(f"文件不存在: {safe}", user_message="文件不存在或已被删除。")
        body = target.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Content-Disposition", f'attachment; filename="{safe}"')
        self._security_headers()
        self.end_headers()
        self.wfile.write(body)

    def _import(self) -> None:
        payload = self._read_json()
        content = payload.get("content")
        filename = _safe_name(str(payload.get("filename") or "import.json")) or "import.json"
        if not filename.lower().endswith(".json"):
            filename += ".json"

        if content:
            if not isinstance(content, str) or len(content) > MAX_BODY_BYTES:
                raise BadRequest("文件内容不合法", user_message="上传的配置文件过大或内容不合法。")
            import_dir = self.ctx.config.export_dir / "imports"
            import_dir.mkdir(parents=True, exist_ok=True)
            stamp = time.strftime("%Y%m%d-%H%M%S")
            target = _resolve_in(import_dir, f"{stamp}-{filename}")
            target.write_text(content, encoding="utf-8")
        elif payload.get("file"):
            target = _resolve_in(self.ctx.config.export_dir, _safe_name(str(payload["file"])))
            if not target.is_file():
                raise NotFound("指定的配置文件不存在", user_message="这个配置文件已经不存在了。")
        else:
            raise BadRequest("需要提供 content 或 file", user_message="请选择一个 JSON 配置文件。")

        profile = load_profile(Path(target))
        job = self.ctx.jobs.start_import(profile)
        self._send_json(
            200,
            {
                "jobId": job.id,
                "total": job.total,
                "profileName": profile.profile_name,
                "format": profile.format,
                "file": str(target),
            },
        )

    def _job_route(self, path: str) -> None:
        parts = [p for p in path.split("/") if p]
        if len(parts) < 3:
            raise NotFound("作业路由不合法")
        job_id = parts[2]
        job = self.ctx.jobs.get(job_id)

        if self.command == "GET" and len(parts) == 3:
            return self._send_json(200, job.snapshot())
        if self.command == "GET" and len(parts) == 4 and parts[3] == "events":
            return self._stream_events(job)
        if self.command == "POST" and len(parts) == 4 and parts[3] == "cancel":
            job.request_cancel()
            return self._send_json(200, {"jobId": job.id, "cancelRequested": True})
        raise NotFound("作业路由不合法")

    def _stream_events(self, job) -> None:
        after = int((self._query.get("after") or ["0"])[0] or 0)
        self._streaming = True
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache, no-transform")
        self.send_header("Connection", "keep-alive")
        self.send_header("X-Accel-Buffering", "no")
        self.end_headers()

        def write_frame(event: JobEvent | None) -> None:
            if event is None:
                self.wfile.write(b": ping\n\n")
            else:
                data = json.dumps(event.to_dict(), ensure_ascii=False)
                self.wfile.write(f"id: {event.seq}\ndata: {data}\n\n".encode("utf-8"))
            self.wfile.flush()

        try:
            write_frame(
                JobEvent(0, time.strftime("%H:%M:%S"), "hello", "已连接事件流", {"status": job.status})
            )
            for event in job.tail(after):
                write_frame(event)
                if event is not None and event.type == "done":
                    break
            self.wfile.write(b'event: close\ndata: {"type": "close"}\n\n')
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            logger.info("SSE 客户端断开: job=%s", job.id)


def create_server(config: Config) -> tuple[ThreadingHTTPServer, AppContext]:
    ctx = AppContext(config)
    handler = type("BoundHandler", (Handler,), {"ctx": ctx})
    server = ThreadingHTTPServer((config.host, config.port), handler)
    server.daemon_threads = True
    return server, ctx
