using System;
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
/// 端到端回归：用一个「假 winget」批处理脚本串起
/// <see cref="WingetRepository"/> → <see cref="TableParser"/> → <see cref="CatalogService"/> 全链路。
///
/// 这些用例对应原 Python 版 tools/test_smoke.py 的核心场景：
/// 中文 GBK 输出解码、UTF-8 样本解析、取消真终止。批处理输出的是 cmd 默认代码页（cp936）
/// 的字节流，与真实 winget 在中文系统上的行为一致。
/// </summary>
public sealed class EndToEndFakeWingetTests
{
    private static string TestDataPath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "TestData", fileName);

    /// <summary>创建一个假 winget：按第一参分发版本探测 / list / show。</summary>
    /// <remarks>批处理必须 CRLF——cmd 对 LF-only 脚本会跳行解析。</remarks>
    private static string CreateFakeWinget(string directory, params string[] lines)
    {
        var path = Path.Combine(directory, "fake-winget.bat");
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        return path;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "wpm-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task FullPipeline_ParsesUtf8SampleFromFakeWinget()
    {
        var directory = CreateTempDirectory();
        try
        {
            // 把 UTF-8 样本放进脚本目录，bat 用 type 原样输出字节流（等价 winget list stdout）
            File.Copy(TestDataPath("winget-list-zh.txt"), Path.Combine(directory, "sample.txt"), overwrite: true);
            var fakeWinget = CreateFakeWinget(
                directory,
                "@echo off",
                "if \"%~1\"==\"--version\" (",
                "  echo v1.9.25200",
                "  exit /b 0",
                ")",
                "if \"%~1\"==\"list\" (",
                "  type \"%~dp0sample.txt\"",
                "  exit /b 0",
                ")",
                "exit /b 1");

            var logPath = Path.Combine(directory, "test.log");
            var config = AppConfig.CreateForTests(fakeWinget, cacheTtlSeconds: 0);
            var repository = new WingetRepository(config, new LogService(logPath));
            var cache = new RuntimeCache(Path.Combine(directory, "cache.json"), new LogService(logPath));
            var catalog = new CatalogService(config, repository, cache, new LogService(logPath));

            var version = await repository.GetVersionAsync(CancellationToken.None);
            Assert.Equal("1.9.25200", version);

            var snapshot = await catalog.BuildSnapshotAsync(CancellationToken.None, version);

            // 中文表头必须被正确识别，样本里的每一行都进了快照
            Assert.True(snapshot.Packages.Count > 0, "解析结果不应为空");
            Assert.All(snapshot.Packages, package => Assert.True(package.PackageIdentifier.Length > 0));

            // 源列 ∈ {winget, msstore} 才可重装；样本里两类都有
            Assert.Contains(snapshot.Packages, package => package.Reinstallable);
            Assert.Contains(snapshot.Packages, package => !package.Reinstallable);

            // 分组口径：reinstallable ∧ !isDependency = 软件，否则运行库 / 不支持
            Assert.All(snapshot.Packages, package =>
            {
                var group = PackageGroups.Of(package);
                Assert.Contains(group, (System.Collections.Generic.IReadOnlyList<string>)PackageGroups.Order);
            });
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task FullPipeline_DecodesGbkOutputFromCmdCodePage()
    {
        var directory = CreateTempDirectory();
        try
        {
            // cmd 默认代码页是 936：脚本体本身用 GBK 保存，echo 出来的就是 GBK 字节
            var gbk = TextEncoding.Gbk;
            var scriptBody = string.Join("\r\n",
                "@echo off",
                "chcp 936 >nul",
                "echo 名称                         版本            ID                    可用            源",
                "echo -----------------------------------------------------------------------------------------------------------------",
                "echo 测试软件 Alpha                   1.0.0           Test.Alpha            2.0.0           winget",
                "echo 微软商店应用                     3.0.0           MS.Store.App          3.0.0           msstore",
                "echo 注册表残留                       9.9             arp\\leftover",
                "exit /b 0");
            var fakeWinget = Path.Combine(directory, "fake-winget-gbk.bat");
            File.WriteAllText(fakeWinget, scriptBody, gbk);

            var logPath = Path.Combine(directory, "test.log");
            var config = AppConfig.CreateForTests(fakeWinget, cacheTtlSeconds: 0);
            var repository = new WingetRepository(config, new LogService(logPath));

            var result = await repository.ListInstalledAsync(CancellationToken.None);

            // 解码链必须把 GBK 字节还原成中文，不许出现 U+FFFD
            Assert.DoesNotContain("\uFFFD", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("测试软件 Alpha", result.Stdout, StringComparison.Ordinal);

            var packages = TableParser.ParseListOutput(result.Stdout);
            var alpha = packages.Single(p => p.PackageId == "Test.Alpha");
            Assert.Equal("测试软件 Alpha", alpha.Name);
            Assert.Equal("1.0.0", alpha.Version);
            Assert.Equal("winget", alpha.Source);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task FullPipeline_CancelTerminatesFakeWingetQuickly()
    {
        var directory = CreateTempDirectory();
        try
        {
            // 假装 list 要跑 30 秒：取消后必须 < 5s 内返回（P0-13 真终止，不是等超时）
            var fakeWinget = CreateFakeWinget(
                directory,
                "@echo off",
                "if \"%~1\"==\"--version\" (",
                "  echo v1.9.25200",
                "  exit /b 0",
                ")",
                "if \"%~1\"==\"list\" (",
                "  ping -n 31 127.0.0.1 >nul",
                "  exit /b 0",
                ")",
                "exit /b 1");

            var logPath = Path.Combine(directory, "test.log");
            var config = AppConfig.CreateForTests(fakeWinget, cacheTtlSeconds: 0);
            var repository = new WingetRepository(config, new LogService(logPath));

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repository.ListInstalledAsync(cts.Token));

            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"取消应在 5s 内生效，实际 {stopwatch.ElapsedMilliseconds}ms");
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }
}
