"""唯一的日志入口：handler 由 qt_app 安装。

这里只提供 `get_logger`。不要再往这里加 formatter / setup 函数——
落盘格式和 handler 的生命周期归 GUI 层管（`qt_app._install_logging`），
业务模块只管拿 logger 打日志。
"""

from __future__ import annotations

import logging


def get_logger(name: str) -> logging.Logger:
    return logging.getLogger(name)
