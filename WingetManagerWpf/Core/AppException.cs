using System;

namespace WingetManagerWpf.Core
{
    /// <summary>
    /// 所有可预期错误的基类。约定：
    /// <list type="bullet">
    /// <item><see cref="Exception.Message"/> 是技术细节，只进日志；</item>
    /// <item><see cref="UserMessage"/> 是给用户看的中文文案，UI 只显示它；</item>
    /// <item><see cref="Code"/> / <see cref="Status"/> 与原版 errors.py 对齐，便于诊断信息里带错误码。</item>
    /// </list>
    /// 命名说明：架构文档 §8.7 用 <c>AppException</c>（原版类名是 AppError），此处沿用前者。
    /// </summary>
    public class AppException : Exception
    {
        public AppException(string detail)
            : this(detail, null, 500, "internal_error")
        {
        }

        public AppException(string detail, string userMessage)
            : this(detail, userMessage, 500, "internal_error")
        {
        }

        public AppException(string detail, string userMessage, int status, string code)
            : base(string.IsNullOrEmpty(detail) ? (userMessage ?? "服务内部错误，请查看日志。") : detail)
        {
            UserMessage = string.IsNullOrEmpty(userMessage) ? "服务内部错误，请查看日志。" : userMessage;
            Status = status;
            Code = code;
        }

        /// <summary>面向用户的中文文案（UI 只显示这个）。</summary>
        public string UserMessage { get; private set; }

        /// <summary>HTTP 风格的语义状态码（与原版保持一致，用于诊断信息）。</summary>
        public int Status { get; private set; }

        /// <summary>稳定的错误码字符串（用于诊断信息与日志检索）。</summary>
        public string Code { get; private set; }
    }

    /// <summary>配置不合法（环境变量越界 / 非整数等）。启动期只记日志并用默认值继续。</summary>
    public sealed class ConfigInvalidException : AppException
    {
        public ConfigInvalidException(string detail)
            : base(detail, "配置不正确，请检查环境变量或 winget 是否可用。", 500, "config_invalid")
        {
        }
    }

    /// <summary>找不到 winget.exe（没装 App Installer 或 WPM_WINGET_EXE 指向不存在的文件）。</summary>
    public sealed class WingetNotFoundException : AppException
    {
        public WingetNotFoundException(string detail)
            : base(detail, "未找到 winget.exe，请先安装「应用安装程序 / App Installer」。", 500, "winget_not_found")
        {
        }
    }

    /// <summary>winget 跑起来了但失败（超时、非 0 退出且无输出等）。</summary>
    public sealed class WingetCommandFailedException : AppException
    {
        public WingetCommandFailedException(string detail)
            : base(detail, "winget 执行失败，请查看详细输出。", 502, "winget_command_failed")
        {
        }
    }

    /// <summary>请求参数不合法（JSON 解析失败、文件名含 GBK 无法表示的字符等）。</summary>
    public sealed class BadRequestException : AppException
    {
        public BadRequestException(string detail)
            : this(detail, null)
        {
        }

        public BadRequestException(string detail, string userMessage)
            : base(detail, userMessage ?? "请求参数不合法。", 400, "bad_request")
        {
        }
    }

    /// <summary>用户主动取消扫描（不是错误，状态机据此回到 Ready/Cancelled）。</summary>
    public sealed class ScanCancelledException : AppException
    {
        public ScanCancelledException(string detail)
            : base(detail, "扫描已取消。", 499, "scan_cancelled")
        {
        }
    }
}
