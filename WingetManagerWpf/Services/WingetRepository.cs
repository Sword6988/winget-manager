using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WingetManagerWpf.Core;

namespace WingetManagerWpf.Services
{
    /// <summary>winget 仓储层（唯一子进程入口）。</summary>
    public interface IWingetRepository
    {
        /// <summary>winget.exe 路径；空串表示未探测到。</summary>
        string WingetExe { get; }

        /// <summary>带互斥重试的通用执行；<paramref name="timeoutSeconds"/> 为空时取 AppConfig.CommandTimeout。</summary>
        Task<CommandResult> RunAsync(string[] args, int? timeoutSeconds, CancellationToken cancellationToken);

        /// <summary><c>winget --version</c>；空输出返回 "unknown"；找不到 exe 抛 <see cref="WingetNotFoundException"/>。</summary>
        Task<string> VersionAsync(CancellationToken cancellationToken);

        /// <summary><c>winget list</c>（超时不低于 60s）。</summary>
        Task<CommandResult> ListInstalledAsync(CancellationToken cancellationToken);

        /// <summary><c>winget show --id &lt;id&gt; --exact</c>（超时 30s，失败不抛）。</summary>
        Task<CommandResult> ShowMetadataAsync(string packageId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 仓储层实现。约定（与原版 services/winget_cli.py 一致）：
    /// <list type="bullet">
    /// <item>子命令统一追加 <c>--disable-interactivity --accept-source-agreements</c>，
    /// 保证可被无人值守调用（<c>--version</c> 这类顶层开关不追加，否则 winget 会报未知参数）；</item>
    /// <item>输出先收原始字节，再统一走 <see cref="EncodedText.Decode"/>；</item>
    /// <item>撞上 winget 全局互斥（rc=0x8A150001）自动退避重试，**退避时间计入总超时**。</item>
    /// </list>
    /// </summary>
    public sealed class WingetRepository : IWingetRepository
    {
        private const string AcceptSourceAgreements = "--accept-source-agreements";
        private const string DisableInteractivity = "--disable-interactivity";
        private const int VersionTimeoutSeconds = 30;
        private const int ShowTimeoutSeconds = 30;
        private const int ListTimeoutFloorSeconds = 60;

        private readonly AppConfig _config;
        private readonly AppLogger _log;

        public WingetRepository(AppConfig config, AppLogger log)
        {
            _config = config ?? throw new ArgumentNullException("config");
            _log = log;
        }

        public string WingetExe
        {
            get { return _config.WingetExe; }
        }

        public Task<CommandResult> RunAsync(string[] args, int? timeoutSeconds, CancellationToken cancellationToken)
        {
            string[] captured = args ?? new string[0];
            int? timeout = timeoutSeconds;

            // Process 是同步阻塞的：放到线程池里跑，UI 线程只 await
            return Task.Run(() => RunCore(captured, timeout, cancellationToken), cancellationToken);
        }

        public async Task<string> VersionAsync(CancellationToken cancellationToken)
        {
            CommandResult result = await RunAsync(new[] { "--version" }, VersionTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(result.Combined) || result.Combined.Trim().Length == 0)
            {
                // 与别的 winget 实例并发时可能重试完仍空输出（软件其实装着）；
                // 只有「找不到 exe / 无法启动」才判 NO_WINGET
                if (_log != null)
                {
                    _log.Warn("winget --version 无输出（rc=" + result.ExitCode.ToString(CultureInfo.InvariantCulture)
                              + "），按 unknown 继续。");
                }

                return "unknown";
            }

            string text = (string.IsNullOrEmpty(result.StdOut) ? result.StdErr : result.StdOut) ?? string.Empty;
            string first = FirstNonEmptyLine(text);
            if (first.Length == 0)
            {
                return "unknown";
            }

            // winget 输出形如 "v1.30.100-preview"，但 import schema 要求数字开头
            return first[0] == 'v' || first[0] == 'V' ? first.Substring(1) : first;
        }

        public async Task<CommandResult> ListInstalledAsync(CancellationToken cancellationToken)
        {
            // 超时不低于 60s：首次扫描要下载源索引，180s 的默认值够用，但配置被改小时也要保住下限
            int timeout = Math.Max(_config.CommandTimeout, ListTimeoutFloorSeconds);
            CommandResult result = await RunAsync(new[] { "list" }, timeout, cancellationToken).ConfigureAwait(false);

            if (!result.Ok && string.IsNullOrEmpty((result.StdOut ?? string.Empty).Trim()))
            {
                // 非 0 退出且一个字都没输出：这不是「没有已安装软件」，而是真失败
                throw new WingetCommandFailedException(
                    result.Combined.Trim().Length > 0 ? result.Combined.Trim() : "winget list 无输出");
            }

            return result;
        }

        public Task<CommandResult> ShowMetadataAsync(string packageId, CancellationToken cancellationToken)
        {
            // 包不在目录里时 winget 以非 0 退出并在 stderr 里带原因：
            // 这里不抛错，把原始结果交给上层解析（解析不出就按「未知」处理，不写缓存）
            return RunAsync(
                new[] { "show", "--id", packageId ?? string.Empty, "--exact" },
                ShowTimeoutSeconds,
                cancellationToken);
        }

        private CommandResult RunCore(string[] args, int? timeoutSeconds, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_config.WingetExe))
            {
                // 启动时就没探测到 winget：直接给出安装引导文案，
                // 而不是让 Process.Start 抛一个「参数为空」的晦涩异常
                throw new WingetNotFoundException(
                    string.IsNullOrEmpty(_config.WingetError) ? "未找到 winget.exe" : _config.WingetError);
            }

            string[] effective = AppendGlobalFlags(args);
            int limit = timeoutSeconds.HasValue && timeoutSeconds.Value > 0
                ? timeoutSeconds.Value
                : _config.CommandTimeout;

            var stopwatch = Stopwatch.StartNew();
            var budget = TimeSpan.FromSeconds(limit);
            int attempt = 0;
            CommandResult result = null;

            while (true)
            {
                TimeSpan remaining = budget - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new WingetCommandFailedException(
                        "winget 命令超时（>" + limit.ToString(CultureInfo.InvariantCulture)
                        + "s，含重试退避）：" + JoinArguments(args));
                }

                ProcessRunResult raw = ProcessRunner.Run(
                    _config.WingetExe, effective, (int)Math.Ceiling(remaining.TotalSeconds), cancellationToken, _log);

                result = new CommandResult(
                    effective,
                    raw.ExitCode,
                    EncodedText.Decode(raw.StandardOutput),
                    EncodedText.Decode(raw.StandardError),
                    raw.DurationMs);

                LogCommand(effective, result, attempt);

                bool shouldRetry = !result.Ok
                                   && attempt < Metrics.MutexRetryMax
                                   && result.IsMutexConflict;

                if (!shouldRetry)
                {
                    break;
                }

                attempt++;
                if (_log != null)
                {
                    _log.Warn("撞上 winget 互斥（rc=" + result.ExitCode.ToString(CultureInfo.InvariantCulture)
                              + "），第 " + attempt.ToString(CultureInfo.InvariantCulture) + " 次重试："
                              + JoinArguments(args));
                }

                if (!WaitBackoff(Metrics.MutexBackoffMs, budget, stopwatch, cancellationToken, args))
                {
                    break;
                }
            }

            result.DurationMs = (int)stopwatch.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// 追加全局开关。例外：<c>--version</c> 这类以 '-' 开头的顶层开关不追加 ——
        /// winget 的 <c>--version</c> 不接受子命令选项，追加会直接失败。
        /// 已经显式写过的开关不重复追加。
        /// </summary>
        private static string[] AppendGlobalFlags(string[] args)
        {
            var list = new System.Collections.Generic.List<string>();
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (!string.IsNullOrEmpty(args[i]))
                    {
                        list.Add(args[i]);
                    }
                }
            }

            if (list.Count == 0 || list[0].StartsWith("-", StringComparison.Ordinal))
            {
                return list.ToArray();
            }

            if (!Contains(list, DisableInteractivity))
            {
                list.Add(DisableInteractivity);
            }

            if (!Contains(list, AcceptSourceAgreements))
            {
                list.Add(AcceptSourceAgreements);
            }

            return list.ToArray();
        }

        private static bool Contains(System.Collections.Generic.List<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 退避等待；返回 false 表示时间预算已用完，不该再重试。
        /// 用 <see cref="CancellationToken.WaitHandle"/> 而不是 Thread.Sleep：
        /// 命中取消时立刻醒来，不会把「取消」拖到退避结束之后。
        /// </summary>
        private static bool WaitBackoff(int milliseconds, TimeSpan budget, Stopwatch stopwatch,
                                        CancellationToken cancellationToken, string[] args)
        {
            if (stopwatch.Elapsed + TimeSpan.FromMilliseconds(milliseconds) > budget)
            {
                return false;
            }

            try
            {
                if (cancellationToken.WaitHandle.WaitOne(milliseconds))
                {
                    throw new ScanCancelledException("winget 命令已取消：" + JoinArguments(args));
                }
            }
            catch (ObjectDisposedException)
            {
                // CancellationTokenSource 已释放：当作没取消，继续等
                Thread.Sleep(milliseconds);
            }

            return true;
        }

        private void LogCommand(string[] effective, CommandResult result, int retryCount)
        {
            if (_log == null)
            {
                return;
            }

            var sb = new StringBuilder(160);
            sb.Append("winget ").Append(JoinArguments(effective));
            sb.Append(" -> rc=").Append(result.ExitCode.ToString(CultureInfo.InvariantCulture));
            sb.Append(" (").Append(result.DurationMs.ToString(CultureInfo.InvariantCulture)).Append("ms)");
            if (retryCount > 0)
            {
                sb.Append(" 重试=").Append(retryCount.ToString(CultureInfo.InvariantCulture));
            }

            _log.Info(sb.ToString());
        }

        private static string JoinArguments(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return string.Empty;
            }

            return string.Join(" ", args);
        }

        private static string FirstNonEmptyLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string normalized = EncodedText.NormalizeNewlines(text);
            string[] lines = normalized.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length > 0)
                {
                    return line;
                }
            }

            return string.Empty;
        }
    }
}
