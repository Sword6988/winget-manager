"""原子写盘：先写同目录临时文件，fsync 后 os.replace 覆盖。

为什么要这么麻烦：`Path.write_text` 直接截断目标文件再写，写到一半崩溃/断电/
磁盘满，用户手里就只剩半个 JSON。同目录临时文件 + os.replace 是 POSIX/NTFS 上
唯一的「要么旧、要么新」保证；临时文件必须和目标同盘同目录，否则 replace 退化成
跨盘拷贝，原子性就没了。
"""

from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path


def atomic_write_text(path, text: str, encoding: str = "utf-8") -> None:
    """把 text 原子写入 path（父目录不存在时先建）。

    `newline=""` 必须保留：.bat 依赖 CRLF，默认的通用换行会把 \\r\\n 转成 \\n，
    拿到 cmd 里就是一堆行为诡异的脚本。失败时清理临时文件，不留垃圾。
    """
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=str(path.parent), prefix=path.name + ".", suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding=encoding, newline="") as f:
            f.write(text)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def atomic_write_json(path, payload) -> None:
    """原子写入 JSON（UTF-8、两空格缩进、结尾带换行）。"""
    atomic_write_text(path, json.dumps(payload, ensure_ascii=False, indent=2) + "\n")
