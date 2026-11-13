using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WingetManager.Infra;
using WingetManager.Models;

namespace WingetManager.Services;

/// <summary>
/// 仓储层：<b>唯一</b>与 winget.exe 交互的地方（对应 <c>services/winget_cli.py</c>）。
///
/// 约定：
/// <list type="bullet">
/// <item>所有命令统一加 <c>--disable-interactivity</c> / <c>--accept-source-agreements</c>，
///       保证非交互可被服务调用；</item>
/// <item>输出统一解码（utf-8-sig → utf-8 → cp936 → gbk → 容错）；</item>
/// <item>每条命令都可传 <see cref="CancellationToken"/>，触发时立刻
///       <c>Kill(entireProcessTree: true)</c> 并抛 <see cref="ScanCancelledException"/>；</item>
/// <item>撞上 winget 进程级全局互斥会自动退避重试（最多 2 次 × 0.4s，计入同一总超时预算）。</item>
/// </list>
/// </summary>
public sealed class WingetRepository
{
    /// <summary>轮询间隔：太小空转烧 CPU，太大取消手感发钝；0.2s 下用户最多等半拍。</summary>
    public const int PollIntervalMilliseconds = 200;

    /// <summary>winget 全局互斥冲突时的退出码（0x8A150001，十进制 2316632065）。</summary>
    public const int MutexExitCode = unchecked((int)0x8A150001);

    /// <summary>最多重试次数；配合 0.4s 退避，最坏多花约 1.2s。</summary>
    public const int MutexRetryMax = 2;

    /// <summary>互斥退避时长（毫秒）。</summary>
    public const int MutexBackoffMilliseconds = 400;

    /// <summary>等待子进程输出排空的上限（进程被 kill 后管道可能不干净）。</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly AppConfig _config;
    private readonly LogService _log;

    /// <summary>
    /// 进程内串行闸门：扫描与后台富化都走这里，避免自己去撞 winget 的全局互斥
    /// （ARCHITECTURE §8.5）。
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 创建仓储层。
    /// </summary>
    /// <param name="config">应用配置（提供 winget 路径、超时）。</param>
    /// <param name="log">日志服务；null 用共享实例。</param>
    public WingetRepository(AppConfig config, LogService? log = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? LogService.Shared;
    }

    /// <summary>当前使用的 winget 可执行文件路径（空串表示不可用）。</summary>
    public string WingetExe => _config.WingetExe;

    // ── 基础执行 ──────────────────────────────────────────────────────

    /// <summary>
    /// 执行一条 winget 命令；撞上 winget 互斥会自动退避重试。
    ///
    /// winget 有进程级全局互斥，外部任何 winget 进程在跑都会让本次调用在 ~190ms 内
    /// 空输出失败。不重试的话「外部占一下就整次扫描失败」。
    /// </summary>
    /// <param name="args">不含 exe 路径的参数列表。</param>
    /// <param name="timeoutSeconds">本条命令的总超时（秒），null 用配置默认值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令结果（<see cref="CommandResult.DurationMs"/> 含退避）。</returns>
    /// <exception cref="WingetNotFoundException">没有可用的 winget.exe。</exception>
    /// <exception cref="WingetCommandFailedException">超时。</exception>
    /// <exception cref="ScanCancelledException">用户取消。</exception>
    public async Task<CommandResult> RunAsync(IReadOnlyList<string> args, int? timeoutSeconds = null,
                                              CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new ScanCancelledException(CancelledMessage(args));
        }

        try
        {
            return await RunCoreAsync(args, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CommandResult> RunCoreAsync(IReadOnlyList<string> args, int? timeoutSeconds,
                                                  CancellationToken cancellationToken)
    {
        if (!_config.HasWinget)
        {
            // 启动时就没探测到 winget：直接给出安装引导文案，而不是抛一个晦涩的「参数为空」
            throw new WingetNotFoundException(_config.WingetError ?? "未找到 winget.exe");
        }

        var argv = new List<string>(args.Count + 1) { _config.WingetExe };
        argv.AddRange(args);

        var limitSeconds = timeoutSeconds ?? _config.CommandTimeoutSeconds;
        var total = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(limitSeconds);
        var attempt = 0;

        CommandResult result;
        while (true)
        {
            var elapsed = total.Elapsed;
            result = await Task
                .Run(() => RunOnce(argv, elapsed, deadline, limitSeconds, cancellationToken),
                     CancellationToken.None)
                .ConfigureAwait(false);

            if (result.Ok || attempt >= MutexRetryMax || !IsMutexConflict(result))
            {
                break;
            }

            if (!await WaitBackoffAsync(MutexBackoffMilliseconds, total.Elapsed, deadline, cancellationToken, args)
                    .ConfigureAwait(false))
            {
                break; // 时间预算已用完，别把总耗时拉爆
            }

            attempt++;
            _log.Info("撞上 winget 互斥（rc={0}），第 {1} 次重试：{2}",
                      result.ReturnCode, attempt, string.Join(' ', args));
        }

        result.DurationMs = (int)total.ElapsedMilliseconds; // 含退避
        return result;
    }

    /// <summary>
    /// 跑一次子进程：<c>Process</c> + 200ms 轮询而不是同步等待。
    ///
    /// 同步等待的话取消只能等超时（默认 180s）——用户点「取消扫描」后界面要僵住三分钟，
    /// 体验上等同卡死。轮询版每 200ms 检查一次取消，置位就 kill，真正返回。
    /// </summary>
    private CommandResult RunOnce(IReadOnlyList<string> argv, TimeSpan alreadyElapsed, TimeSpan deadline,
                                  int totalLimitSeconds, CancellationToken cancellationToken)
    {
        var argsText = string.Join(' ', argv);
        var sw = Stopwatch.StartNew();

        var startInfo = new ProcessStartInfo
        {
            FileName = argv[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        for (var i = 1; i < argv.Count; i++)
        {
            startInfo.ArgumentList.Add(argv[i]);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new WingetNotFoundException($"无法执行 {argv[0]}");
        }
        catch (Win32Exception ex)
        {
            throw new WingetNotFoundException($"无法执行 {argv[0]}：{ex.Message}");
        }

        using (process)
        {
            var stdoutBuffer = new MemoryStream();
            var stderrBuffer = new MemoryStream();
            var copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer);
            var copyErr = process.StandardError.BaseStream.CopyToAsync(stderrBuffer);

            try
            {
                while (true)
                {
                    if (process.WaitForExit(PollIntervalMilliseconds))
                    {
                        break;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        KillTree(process);
                        Drain(copyOut, copyErr);
                        _log.Info("winget {0} 已被取消", string.Join(' ', argv.Skip(1)));
                        throw new ScanCancelledException(CancelledMessage(argv.Skip(1).ToList()));
                    }

                    if (alreadyElapsed + sw.Elapsed >= deadline)
                    {
                        KillTree(process);
                        Drain(copyOut, copyErr);
                        throw new WingetCommandFailedException(
                            $"winget 命令超时（>{totalLimitSeconds}s，含重试退避）");
                    }
                }

                // 进程已退出：等管道把剩余输出读完（有上限，绝不无限等）
                Drain(copyOut, copyErr);
            }
            finally
            {
                // 任何异常路径都别把子进程留在后台偷偷跑
                if (!HasExited(process))
                {
                    KillTree(process);
                    try
                    {
                        process.WaitForExit(3000);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }

            var duration = (int)sw.ElapsedMilliseconds;
            var result = new CommandResult
            {
                Args = new List<string>(argv),
                ReturnCode = process.ExitCode,
                Stdout = DecodeOutput(stdoutBuffer.ToArray()),
                Stderr = DecodeOutput(stderrBuffer.ToArray()),
                DurationMs = duration,
            };

            _log.Info("winget {0} -> {1} ({2}ms)", string.Join(' ', argv.Skip(1)), result.ReturnCode, duration);
            return result;
        }
    }

    /// <summary>
    /// 退避等待；返回 false 表示时间预算已用完，不该再重试。
    /// 用可取消的 <see cref="Task.Delay(int, CancellationToken)"/>：命中取消时立刻醒来，
    /// 不会把「取消」拖到退避结束之后。
    /// </summary>
    private static async Task<bool> WaitBackoffAsync(int milliseconds, TimeSpan elapsed, TimeSpan deadline,
                                                     CancellationToken cancellationToken, IReadOnlyList<string> args)
    {
        if (elapsed + TimeSpan.FromMilliseconds(milliseconds) > deadline)
        {
            return false;
        }

        try
        {
            await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new ScanCancelledException(CancelledMessage(args));
        }

        return true;
    }

    // ── 具体能力 ──────────────────────────────────────────────────────

    /// <summary>
    /// winget 版本号；拿不到就返回 <c>"unknown"</c>。
    ///
    /// 注意别把 "unknown" 当成「没装 winget」：<c>winget --version</c> 与别的 winget 实例
    /// 并发时可能重试完仍空输出（实测 4/10），而软件其实装着。判定「没装」只看
    /// <see cref="WingetNotFoundException"/>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>形如 <c>1.30.100</c> 的版本串（去掉前导 v），或 <c>unknown</c>。</returns>
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(new[] { "--version" }, 30, cancellationToken).ConfigureAwait(false);
        var version = NormalizeVersionLine(result.Combined());
        if (version == UnknownVersion)
        {
            _log.Warn("winget --version 无输出（rc={0}），按 unknown 继续", result.ReturnCode);
        }

        return version;
    }

    /// <summary>版本探测失败时的占位值（<b>不等于</b>「没装 winget」）。</summary>
    public const string UnknownVersion = "unknown";

    /// <summary>
    /// 从 <c>winget --version</c> 的输出里取版本号：取第一行、去掉前导 <c>v</c>；取不到给 <c>unknown</c>。
    ///
    /// winget 输出形如 <c>v1.30.100-preview</c>，但 import 的 schema 只接受数字开头。
    /// </summary>
    /// <param name="combinedOutput">stdout + stderr 的合并文本。</param>
    /// <returns>归一化版本串或 <c>unknown</c>。</returns>
    public static string NormalizeVersionLine(string? combinedOutput)
    {
        var text = combinedOutput?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return UnknownVersion;
        }

        var lines = TableParser.SplitLines(text);
        var raw = lines.Count > 0 ? lines[0].Trim() : string.Empty;
        if (raw.Length == 0)
        {
            return UnknownVersion;
        }

        return raw[0] is 'v' or 'V' ? raw[1..] : raw;
    }

    /// <summary>
    /// 扫描已安装包（<c>winget list</c>）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令结果（stdout 是待解析的表格）。</returns>
    /// <exception cref="WingetCommandFailedException">命令失败且 stdout 一个字都没有。</exception>
    public async Task<CommandResult> ListInstalledAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            new[] { "list", "--accept-source-agreements", "--disable-interactivity" },
            Math.Max(_config.CommandTimeoutSeconds, 60),
            cancellationToken).ConfigureAwait(false);

        ThrowIfListFailed(result);
        return result;
    }

    /// <summary>
    /// <c>winget list</c> 「失败且一个字都不输出」说明它根本没跑起来（互斥/权限/损坏），
    /// 这时解析只会得到空目录——必须显式失败，不能静默把用户当成「没装软件」。
    /// </summary>
    /// <param name="result">list 命令结果。</param>
    /// <exception cref="WingetCommandFailedException">失败且 stdout 为空。</exception>
    public static void ThrowIfListFailed(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Ok && string.IsNullOrWhiteSpace(result.Stdout))
        {
            var detail = result.Combined().Trim();
            throw new WingetCommandFailedException(detail.Length > 0 ? detail : "winget list 无输出");
        }
    }

    /// <summary>
    /// 单个包的 <c>winget show</c> 原始输出：给元数据富化用。
    ///
    /// 走 <see cref="RunAsync"/> 自带的互斥重试。包不在目录里时 winget 以非 0 退出并在
    /// stderr 里带原因——这里不抛错，把原始结果交给上层解析（解析不出描述/标记就按
    /// 「未知」处理，不写缓存）。
    /// </summary>
    /// <param name="packageId">包 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令结果。</returns>
    public Task<CommandResult> ShowMetadataAsync(string packageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);

        return RunAsync(
            new[] { "show", "--id", packageId, "--exact", "--accept-source-agreements", "--disable-interactivity" },
            30,
            cancellationToken);
    }

    // ── 静态判定 / 解码 ───────────────────────────────────────────────

    /// <summary>
    /// 这次失败是不是「winget 被别的实例占用」。
    ///
    /// 实测冲突时 winget 会以退出码 <c>0x8A150001</c> 在 ~190ms 内空输出退出；个别版本不回
    /// 这个码、只是失败且一个字都不输出——两者对调用方的含义一样，都是「等一会儿再来」。
    /// 重试有次数上限，真出错时最坏只是多花 0.8s。
    /// </summary>
    /// <param name="result">命令结果。</param>
    /// <returns>是否按互斥冲突处理。</returns>
    public static bool IsMutexConflict(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.ReturnCode == MutexExitCode)
        {
            return true;
        }

        return result.ReturnCode != 0 && result.Combined().Trim().Length == 0;
    }

    /// <summary>
    /// 把 winget 的输出字节解成字符串（详见 <see cref="TextEncoding.DecodeChain"/>）。
    /// </summary>
    /// <param name="raw">原始字节。</param>
    /// <returns>解码后的文本。</returns>
    public static string DecodeOutput(byte[] raw) => TextEncoding.DecodeChain(raw);

    private static string CancelledMessage(IReadOnlyList<string> args)
    {
        return $"winget 命令已取消: {string.Join(' ', args)}";
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 已经退出了
        }
        catch (Win32Exception)
        {
            // 进程已消失 / 无权限：都当作已终止
        }
        catch (NotSupportedException)
        {
        }
    }

    private static void Drain(Task copyOut, Task copyErr)
    {
        try
        {
            Task.WaitAll(new[] { copyOut, copyErr }, DrainTimeout);
        }
        catch (AggregateException)
        {
            // 进程被 kill 时管道读取会抛，输出已经写到哪算哪
        }
    }
}
