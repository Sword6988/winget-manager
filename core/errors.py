"""类型化错误体系：内部异常带上下文，对外暴露规范化错误结构。"""

from __future__ import annotations


class AppError(Exception):
    """所有可预期错误的基类。"""

    status = 500
    code = "internal_error"
    user_message = "服务内部错误，请查看日志。"

    def __init__(self, detail: str = "", *, user_message: str | None = None, status: int | None = None):
        super().__init__(detail or self.user_message)
        self.detail = detail
        if user_message:
            self.user_message = user_message
        if status:
            self.status = status


class ConfigInvalid(AppError):
    status = 500
    code = "config_invalid"
    user_message = "配置不正确，请检查环境变量或 winget 是否可用。"


class WingetNotFound(AppError):
    status = 500
    code = "winget_not_found"
    user_message = "未找到 winget.exe，请先安装「应用安装程序 / App Installer」。"


class WingetCommandFailed(AppError):
    status = 502
    code = "winget_command_failed"
    user_message = "winget 执行失败，请查看详细输出。"


class BadRequest(AppError):
    status = 400
    code = "bad_request"
    user_message = "请求参数不合法。"


class ScanCancelled(AppError):
    status = 499
    code = "scan_cancelled"
    user_message = "扫描已取消。"
