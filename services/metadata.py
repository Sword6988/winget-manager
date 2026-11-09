"""服务层：用 winget 目录的元数据自动判定「运行库 vs 软件」。

取代人工维护关键词表：关键词只做初判，扫描完成后对判定存疑的包逐个
`winget show`，从输出里抠出描述/标记/安装器 URL，找运行库信号词。
结果落盘缓存（dep 是权威值），命中的 ID 不再发起 show；
查询失败（winget 报错/超时/输出解析不出）不写缓存，下次扫描再试。
"""
from __future__ import annotations

import json
import re
import threading
from datetime import datetime
from pathlib import Path

from core.errors import ScanCancelled
from core.fsio import atomic_write_json
from core.logging_setup import get_logger

logger = get_logger("metadata")

SCHEMA_VERSION = 1

# 描述/标记/安装器 URL 里出现这些词（大小写不敏感）即判为运行库
SIGNAL_WORDS: tuple[str, ...] = (
    "runtime", "redistributable", "redist", "运行库", "运行时", "required to run",
)

# winget show 的段落标签：中文/英文、半角/全角冒号都认
_LABEL_RE = re.compile(
    r"^(描述|Description|标记|标签|Tags|安装程序 ?URL|Installer URL)\s*[:：]\s*(.*)$",
    re.IGNORECASE,
)
_URL_RE = re.compile(r"https?://\S+")


def _extract_sections(text: str) -> tuple[list[str], list[str], list[str]]:
    """从 show 输出里抠出（描述行, 标记词, 安装器 URL）。

    标记是「标签行之后的缩进列表」，直到空行或下一个 `xxx:` 标签行为止；
    描述同理支持折行。解析不出就返回空列表，由调用方判 None。
    """
    desc: list[str] = []
    tags: list[str] = []
    urls: list[str] = []
    mode: str | None = None                 # None / "desc" / "tags"
    for raw in text.splitlines():
        line = raw.rstrip()
        stripped = line.strip()
        if not stripped:
            mode = None
            continue
        m = _LABEL_RE.match(stripped)
        if m:
            label = m.group(1).lower().replace(" ", "")
            rest = m.group(2).strip()
            if label in ("描述", "description"):
                mode = "desc"
                if rest:
                    desc.append(rest)
            elif label in ("标记", "标签", "tags"):
                mode = "tags"
                if rest:                    # 有的版本把标记写在同一行
                    tags.extend(rest.split())
            else:                           # 安装程序 URL / Installer URL
                mode = None
                found = _URL_RE.search(rest)
                if found:
                    urls.append(found.group(0))
            continue
        if mode and line[:1].isspace():     # 缩进行：当前段落的延续
            if mode == "desc":
                desc.append(stripped)
            else:
                tags.extend(stripped.split())
            continue
        mode = None                         # 顶格的非标签行：段落结束
    return desc, tags, urls


def matched_signal(text: str) -> str | None:
    """在（已拼接的）文本里找第一个命中的运行库信号词；没有则 None。"""
    lowered = text.lower()
    for word in SIGNAL_WORDS:
        if word in lowered:
            return word
    return None


def analyze_runtime_signal(text: str) -> tuple[bool | None, str | None]:
    """综合判定：返回（是否运行库, 命中的信号词）。

    - 命中信号词 → (True, 词)
    - 解析出了描述/标记但没命中 → (False, None)
    - 输出为空、或解析不出描述/标记（包不在目录等）→ (None, None)：
      「未知」，绝不当成 False 用。
    """
    if not text or not text.strip():
        return None, None
    desc, tags, urls = _extract_sections(text)
    if not desc and not tags:
        return None, None
    signal = matched_signal("\n".join((*desc, *tags, *urls)))
    return (signal is not None), signal


def parse_runtime_signal(text: str) -> bool | None:
    """是否运行库：True / False / None（未知）。详见 analyze_runtime_signal。"""
    return analyze_runtime_signal(text)[0]


def fetch_show_output(repo, package_id: str,
                      cancel: threading.Event | None = None) -> str:
    """取一次 winget show 的原始文本。

    走 repo.show_metadata（run() 自带互斥重试）。取消要向上传播；
    其他失败（超时/包不在目录）记日志后返回空串——上层会解析成 None，
    不写缓存，下次扫描再试。
    """
    try:
        result = repo.show_metadata(package_id, cancel=cancel)
    except ScanCancelled:
        raise
    except Exception as exc:  # noqa: BLE001  单个包失败不该拖垮整轮富化
        logger.warning("winget show %s 失败：%s", package_id, exc)
        return ""
    return result.stdout or ""


class RuntimeCache:
    """运行库判定的磁盘缓存。

    结构：{"schema": 1, "entries": {"<id 小写>": {"dep": bool, "checkedAt": ISO}}}
    语义：dep 是元数据判定的权威结果，命中即不再发起 winget show；
    查询失败不写缓存。文件损坏/结构不符一律回退空缓存（重建只是多花几分钟）。
    """

    def __init__(self, path: Path):
        self._path = Path(path)
        self._entries: dict = self._load()

    def _load(self) -> dict:
        if not self._path.is_file():
            return {}
        try:
            data = json.loads(self._path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            logger.warning("元数据缓存损坏，回退空缓存：%s", exc)
            return {}
        if not isinstance(data, dict) or data.get("schema") != SCHEMA_VERSION:
            return {}
        entries = data.get("entries")
        return entries if isinstance(entries, dict) else {}

    def get(self, package_id: str) -> bool | None:
        entry = self._entries.get(str(package_id).lower())
        if isinstance(entry, dict) and isinstance(entry.get("dep"), bool):
            return entry["dep"]
        return None

    def put(self, package_id: str, dep: bool) -> None:
        """写入并立即落盘：富化线程随时可能被取消，不能攒着不写。"""
        self._entries[str(package_id).lower()] = {
            "dep": bool(dep),
            "checkedAt": datetime.now().astimezone().isoformat(timespec="seconds"),
        }
        self._save()

    def _save(self) -> None:
        try:
            atomic_write_json(self._path, {"schema": SCHEMA_VERSION,
                                           "entries": self._entries})
        except OSError as exc:
            logger.warning("元数据缓存写盘失败（下次扫描会重查）：%s", exc)

    def __len__(self) -> int:
        return len(self._entries)
