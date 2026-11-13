using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace WingetManager.Infra;

/// <summary>
/// 文件日志（对应 <c>core/logging_setup.py</c> 的 RotatingFileHandler 语义）：
/// <c>%LOCALAPPDATA%\WingetManager\logs\app.log</c>，512KB × 2 轮转，UTF-8。
///
/// 不引第三方日志库（Serilog 等对 AOT 有反射坑面）。winget 的完整命令行由
/// <c>WingetRepository</c> 逐条写入，便于事后核对「当时到底跑了什么」。
/// </summary>
public sealed class LogService
{
    /// <summary>单文件字节上限（512KB，与 Python 版一致）。</summary>
    public const long MaxBytes = 512 * 1024;

    /// <summary>保留的历史文件个数（app.log.1 / app.log.2）。</summary>
    public const int BackupCount = 2;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _path;
    private static LogService? _shared;

    /// <summary>
    /// 进程级共享实例：Core 内部服务用它打日志，避免把 logger 一路塞进构造函数。
    /// App 启动时用 <see cref="SetShared"/> 换成自己那份（同一路径，订阅 UI 事件）。
    /// </summary>
    public static LogService Shared => _shared ??= new LogService();

    /// <summary>替换共享实例（App 启动时调用一次）。</summary>
    public static void SetShared(LogService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _shared = service;
    }

    /// <summary>
    /// 创建日志服务。
    /// </summary>
    /// <param name="path">日志文件路径；默认 <see cref="AppPaths.LogFile"/>。</param>
    public LogService(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? AppPaths.LogFile : path;
    }

    /// <summary>一条日志被写入时触发（UI 日志面板用；无订阅者时零开销）。</summary>
    public event Action<LogEntry>? Written;

    /// <summary>日志文件路径。</summary>
    public string FilePath => _path;

    /// <summary>普通信息。</summary>
    public void Info(string message, params object[] args) => Write("INFO", message, args);

    /// <summary>警告。</summary>
    public void Warn(string message, params object[] args) => Write("WARN", message, args);

    /// <summary>错误。</summary>
    public void Error(string message, params object[] args) => Write("ERROR", message, args);

    /// <summary>写一条日志：落盘（失败静默，日志本身不能影响主流程）+ 通知订阅者。</summary>
    public void Write(string level, string message, params object[] args)
    {
        var text = Format(message, args);
        var timestamp = DateTimeOffset.Now;

        try
        {
            lock (_gate)
            {
                RotateIfNeeded();
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}{3}",
                    timestamp,
                    level,
                    text,
                    Environment.NewLine);

                File.AppendAllText(_path, line, Utf8NoBom);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (NotSupportedException)
        {
        }

        try
        {
            Written?.Invoke(new LogEntry(timestamp, level, text));
        }
        catch (Exception)
        {
            // 订阅者（界面）出错不得反噬日志写入
        }
    }

    /// <summary>超过 512KB 就把 app.log 顶成 app.log.1（并把 .1 顶成 .2），再重新开始写。</summary>
    private void RotateIfNeeded()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        long length;
        try
        {
            length = new FileInfo(_path).Length;
        }
        catch (IOException)
        {
            return;
        }

        if (length < MaxBytes)
        {
            return;
        }

        for (var index = BackupCount - 1; index >= 1; index--)
        {
            var older = _path + "." + index.ToString(CultureInfo.InvariantCulture);
            var newer = _path + "." + (index + 1).ToString(CultureInfo.InvariantCulture);
            if (File.Exists(older))
            {
                TryMove(older, newer);
            }
        }

        TryMove(_path, _path + ".1");
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>格式化消息：参数个数对不上时退化成原串，绝不因为日志抛异常。</summary>
    private static string Format(string message, object[] args)
    {
        if (args is null || args.Length == 0)
        {
            return message;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, message, args);
        }
        catch (FormatException)
        {
            return message;
        }
    }
}

/// <summary>一条日志记录（UI 展示用）。</summary>
/// <param name="Timestamp">本地时间。</param>
/// <param name="Level">INFO / WARN / ERROR。</param>
/// <param name="Message">正文。</param>
public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Message);
