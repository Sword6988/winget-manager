using System;

namespace WingetManager.Infra;

/// <summary>
/// 所有可预期错误的基类（对应 <c>core/errors.py</c> 的 AppError，去掉 HTTP status 字段）。
/// 带 <see cref="Detail"/>（内部上下文，进日志）与 <see cref="UserMessage"/>（界面文案）。
/// </summary>
public class AppException : Exception
{
    /// <summary>错误码（日志/诊断用，与 Python 版 code 一致）。</summary>
    public const string CodeInternalError = "internal_error";

    public AppException(string detail = "", string? userMessage = null, string code = CodeInternalError)
        : base(detail.Length > 0 ? detail : userMessage ?? "服务内部错误，请查看日志。")
    {
        Detail = detail;
        UserMessage = userMessage ?? "服务内部错误，请查看日志。";
        Code = code;
    }

    /// <summary>内部上下文（完整原因，写日志）。</summary>
    public string Detail { get; }

    /// <summary>给用户看的一句话（界面状态栏/对话框）。</summary>
    public string UserMessage { get; }

    /// <summary>错误码。</summary>
    public string Code { get; }
}

/// <summary>配置非法（环境变量越界、WPM_WINGET_EXE 指向不存在的文件）。</summary>
public sealed class ConfigInvalidException : AppException
{
    public ConfigInvalidException(string detail)
        : base(detail, "配置不正确，请检查环境变量或 winget 是否可用。", "config_invalid")
    {
    }
}

/// <summary>未找到 winget.exe：只有这种情况才走「去商店安装」引导。</summary>
public sealed class WingetNotFoundException : AppException
{
    public WingetNotFoundException(string detail)
        : base(detail, "未找到 winget.exe，请先安装「应用安装程序 / App Installer」。", "winget_not_found")
    {
    }
}

/// <summary>winget 执行失败（非零退出且没有任何输出、或命令超时）。</summary>
public sealed class WingetCommandFailedException : AppException
{
    public WingetCommandFailedException(string detail)
        : base(detail, "winget 执行失败，请查看详细输出。", "winget_command_failed")
    {
    }
}

/// <summary>导出参数非法（如恢复脚本文件名无法用 GBK 编码）。</summary>
public sealed class ExportInvalidException : AppException
{
    public ExportInvalidException(string detail, string? userMessage = null)
        : base(detail, userMessage, "export_invalid")
    {
    }
}

/// <summary>
/// 扫描被用户取消。
///
/// 继承 <see cref="OperationCanceledException"/>：一方面让 async/await、Task、
/// CancellationToken 的标准取消语义直接生效（Core 与 UI 层都按取消处理），
/// 另一方面保留 <see cref="AppException"/> 那套文案字段，界面上给的是「扫描已取消」。
/// </summary>
public sealed class ScanCancelledException : OperationCanceledException
{
    public ScanCancelledException(string detail = "扫描已取消", Exception? innerException = null)
        : base(detail, innerException)
    {
        Detail = detail;
    }

    /// <summary>内部上下文。</summary>
    public string Detail { get; }

    /// <summary>给用户看的一句话。</summary>
    public string UserMessage => "扫描已取消。";

    /// <summary>错误码（与 Python 版一致）。</summary>
    public string Code => "scan_cancelled";
}
