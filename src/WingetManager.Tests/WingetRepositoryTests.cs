using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WingetManager.Infra;
using WingetManager.Models;
using WingetManager.Services;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// 仓储层用例：解码链、互斥判定、互斥退避重试、取消真正杀子进程、缺 winget 的报错口径。
///
/// 这里刻意用一个**真实的子进程**（%ComSpec%）来验证「取消 0.2s 内真终止」，
/// 而不是打桩：只有真进程才能证明我们没有干等超时（对应 Python 版
/// test_run_cancel_kills_subprocess 的意图）。
/// </summary>
public sealed class WingetRepositoryTests
{
    private static string CmdExe => Environment.GetEnvironmentVariable("ComSpec")
                                   ?? @"C:\Windows\System32\cmd.exe";

    /// <summary>临时日志：这些用例会写「互斥/取消/失败」日志，注入独立文件避免污染真实 app.log。</summary>
    private static string TempLogPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wpm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "repo.log");
    }

    private static WingetRepository CreateRepo(int commandTimeoutSeconds = 30)
        => new(AppConfig.CreateForTests(CmdExe, 60, commandTimeoutSeconds), new LogService(TempLogPath()));

    // ── 解码链 ────────────────────────────────────────────────────────

    [Fact]
    public void DecodeOutput_StripsUtf8Bom()
    {
        var raw = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'N', (byte)'a', (byte)'m', (byte)'e' };

        // BOM 必须被吃掉，否则首行匹配全错
        Assert.Equal("Name", WingetRepository.DecodeOutput(raw));
    }

    [Fact]
    public void DecodeOutput_Utf8ChineseRoundTrips()
    {
        var raw = Encoding.UTF8.GetBytes("火绒安全");

        Assert.Equal("火绒安全", WingetRepository.DecodeOutput(raw));
    }

    [Fact]
    public void DecodeOutput_FallsBackToGbk()
    {
        TextEncoding.EnsureProvider();
        var raw = Encoding.GetEncoding(936).GetBytes("火绒安全");

        // 这些字节不是合法 UTF-8，必须回落到 cp936/gbk
        Assert.Equal("火绒安全", WingetRepository.DecodeOutput(raw));
    }

    [Fact]
    public void DecodeOutput_ReplacesInvalidBytesInsteadOfThrowing()
    {
        var raw = new byte[] { 0xFF, 0xFE, 0x81, 0x00 };

        var text = WingetRepository.DecodeOutput(raw);

        Assert.NotNull(text); // 不抛异常即可：解码链最后一跳是容错替换
    }

    [Fact]
    public void DecodeOutput_EmptyInputReturnsEmpty()
    {
        Assert.Equal(string.Empty, WingetRepository.DecodeOutput(Array.Empty<byte>()));
    }

    // ── 互斥判定 ──────────────────────────────────────────────────────

    [Fact]
    public void IsMutexConflict_TrueForMutexExitCode()
    {
        var result = new CommandResult { ReturnCode = WingetRepository.MutexExitCode };

        Assert.True(WingetRepository.IsMutexConflict(result));
    }

    [Fact]
    public void IsMutexConflict_TrueForFailureWithNoOutput()
    {
        // 个别 winget 版本不回 0x8A150001，只是失败且一个字都不输出——含义相同
        var result = new CommandResult { ReturnCode = 1, Stdout = string.Empty, Stderr = "  \r\n " };

        Assert.True(WingetRepository.IsMutexConflict(result));
    }

    [Fact]
    public void IsMutexConflict_FalseWhenFailureHasOutput()
    {
        var result = new CommandResult { ReturnCode = 1, Stderr = "找不到与输入匹配的已安装包" };

        Assert.False(WingetRepository.IsMutexConflict(result));
    }

    [Fact]
    public void IsMutexConflict_FalseWhenSuccessful()
    {
        var result = new CommandResult { ReturnCode = 0, Stdout = "v1.10.0" };

        Assert.False(WingetRepository.IsMutexConflict(result));
    }

    [Fact]
    public void MutexExitCode_MatchesWingetContract()
    {
        // 0x8A150001 = 2316632065（无符号）；.NET 的 Process.ExitCode 是 int，按位等价
        Assert.Equal(unchecked((int)0x8A150001), WingetRepository.MutexExitCode);
        Assert.Equal(2316632065, unchecked((uint)WingetRepository.MutexExitCode));
    }

    // ── 缺 winget ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ThrowsWingetNotFoundWhenExeMissing()
    {
        var repo = new WingetRepository(
            AppConfig.CreateForTests(string.Empty, 60, 30, "未找到 winget.exe"), new LogService(TempLogPath()));

        var ex = await Assert.ThrowsAsync<WingetNotFoundException>(
            () => repo.RunAsync(new[] { "--version" }));

        Assert.Contains("winget.exe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ThrowsWingetNotFoundWhenExeNotExecutable()
    {
        var repo = new WingetRepository(
            AppConfig.CreateForTests(@"Z:\definitely-not-here\winget.exe", 60, 30), new LogService(TempLogPath()));

        await Assert.ThrowsAsync<WingetNotFoundException>(() => repo.RunAsync(new[] { "--version" }));
    }

    // ── 取消：0.2s 内真正终止子进程 ───────────────────────────────────

    [Fact]
    public async Task RunAsync_CancelKillsSubprocessImmediately()
    {
        var repo = CreateRepo(commandTimeoutSeconds: 30);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(300);

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<ScanCancelledException>(
            () => repo.RunAsync(new[] { "/c", "ping", "-n", "31", "127.0.0.1" }, 30, cts.Token));
        stopwatch.Stop();

        // 等超时要 30s；真 kill 掉才是秒回
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                    $"取消应秒回，实际耗时 {stopwatch.Elapsed.TotalSeconds:F1}s（说明没 kill 只是等超时）");
    }

    [Fact]
    public async Task RunAsync_CancelBeforeStartDoesNotLaunchProcess()
    {
        var repo = CreateRepo();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<ScanCancelledException>(
            () => repo.RunAsync(new[] { "/c", "exit", "0" }, 30, cts.Token));
    }

    // ── 互斥退避重试 ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_RetriesAfterMutexConflictAndGivesUpAfterMax()
    {
        // 「失败且无输出」= 互斥冲突：初次 + 2 次重试 = 3 次进程，2 段 400ms 退避
        var repo = CreateRepo(commandTimeoutSeconds: 30);

        var result = await repo.RunAsync(new[] { "/c", "exit", "3" }, 30);

        Assert.False(result.Ok);
        Assert.Equal(3, result.ReturnCode);
        Assert.True(result.DurationMs >= 750,
                    $"两次退避至少 800ms，实际 {result.DurationMs}ms（说明没重试）");
    }

    [Fact]
    public async Task RunAsync_DoesNotRetryWhenFailureHasOutput()
    {
        var repo = CreateRepo(commandTimeoutSeconds: 30);

        var result = await repo.RunAsync(new[] { "/c", "echo", "boom", "1>&2", "&", "exit", "1" }, 30);

        Assert.Equal(1, result.ReturnCode);
        // 有输出的失败不是互斥：不重试，耗时应远小于两次退避
        Assert.True(result.DurationMs < 750, $"有输出的失败不该重试，实际 {result.DurationMs}ms");
    }

    [Fact]
    public async Task RunAsync_CancelDuringBackoffReturnsImmediately()
    {
        var repo = CreateRepo(commandTimeoutSeconds: 30);
        using var cts = new CancellationTokenSource();

        // 进程 60ms 就退出（冲突），退避 400ms 期间取消
        var task = repo.RunAsync(new[] { "/c", "exit", "3" }, 30, cts.Token);
        await Task.Delay(220, CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();

        await Assert.ThrowsAsync<ScanCancelledException>(() => task);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                    "取消应立刻返回，不能等退避跑完");
    }

    // ── 能力封装 ──────────────────────────────────────────────────────

    [Fact]
    public void NormalizeVersionLine_StripsLeadingV()
    {
        Assert.Equal("1.30.100-preview", WingetRepository.NormalizeVersionLine("v1.30.100-preview"));
        Assert.Equal("1.10.0", WingetRepository.NormalizeVersionLine("V1.10.0\r\n"));
        Assert.Equal("1.10.0", WingetRepository.NormalizeVersionLine("1.10.0"));
    }

    [Fact]
    public void NormalizeVersionLine_UsesFirstLineOnly()
    {
        Assert.Equal("1.10.0", WingetRepository.NormalizeVersionLine("v1.10.0\n一些额外输出\n"));
    }

    [Fact]
    public void NormalizeVersionLine_ReturnsUnknownOnEmpty()
    {
        // 拿不到版本号不能当成「没装 winget」（并发时 winget --version 可能空输出）
        Assert.Equal(WingetRepository.UnknownVersion, WingetRepository.NormalizeVersionLine(string.Empty));
        Assert.Equal(WingetRepository.UnknownVersion, WingetRepository.NormalizeVersionLine("   \r\n "));
        Assert.Equal(WingetRepository.UnknownVersion, WingetRepository.NormalizeVersionLine(null));
    }

    [Fact]
    public void ThrowIfListFailed_ThrowsWhenFailedWithNoStdout()
    {
        var result = new CommandResult { ReturnCode = 1, Stderr = "  " };

        var ex = Assert.Throws<WingetCommandFailedException>(() => WingetRepository.ThrowIfListFailed(result));
        Assert.Contains("无输出", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfListFailed_KeepsFailedResultThatStillHasStdout()
    {
        // 非零退出但 stdout 有内容（例如部分源不可用）：交给解析层处理，不抛
        var result = new CommandResult { ReturnCode = 1, Stdout = "名称  ID" };

        WingetRepository.ThrowIfListFailed(result);
    }

    [Fact]
    public async Task ListInstalledAsync_ThrowsWhenFailedWithNoOutput()
    {
        var repo = CreateRepo(commandTimeoutSeconds: 10);

        // cmd 把 "list" 当成不存在的命令：非零退出且 stdout 为空 → 判为 winget 失败
        await Assert.ThrowsAsync<WingetCommandFailedException>(() => repo.ListInstalledAsync());
    }
}
