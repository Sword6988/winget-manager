using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WingetManager.Infra;
using WingetManager.Models;
using WingetManager.Services;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// 富化任务用例：SDK 豁免的第二道闸（元数据层）与进度回报。
///
/// 只把 <c>winget show</c> 换成预置文本，缓存、改判、收尾、进度全走真实实现。
/// </summary>
public sealed class EnrichmentWorkerTests
{
    private static string TempFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wpm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static LogService TempLog() => new(TempFile("test.log"));

    /// <summary>只覆写 show：直接返回预置文本，不起子进程。</summary>
    private sealed class FakeShowRepository : WingetRepository
    {
        private readonly IReadOnlyDictionary<string, string> _outputs;

        public FakeShowRepository(IReadOnlyDictionary<string, string> outputs, LogService log)
            : base(AppConfig.CreateForTests("fake-winget.exe", 60, 30), log)
        {
            _outputs = outputs;
        }

        public override Task<CommandResult> ShowMetadataAsync(string packageId,
                                                             CancellationToken cancellationToken = default)
        {
            var text = _outputs.TryGetValue(packageId, out var found) ? found : string.Empty;
            return Task.FromResult(new CommandResult { ReturnCode = 0, Stdout = text });
        }
    }

    private sealed record RunResult(EnrichmentSummary Summary, IReadOnlyList<EnrichmentProgress> Progress,
                                    int ChangeBatches, RuntimeCache Cache);

    private static async Task<RunResult> RunWorkerAsync(IReadOnlyDictionary<string, string> outputs,
                                                       IReadOnlyDictionary<string, string> names,
                                                       IReadOnlyDictionary<string, bool> keywords)
    {
        var log = TempLog();
        var cache = new RuntimeCache(TempFile("metadata-cache.json"), log);
        var worker = new EnrichmentWorker(new FakeShowRepository(outputs, log), cache, log);

        var progress = new List<EnrichmentProgress>();
        var changeBatches = 0;
        var completed = new TaskCompletionSource<EnrichmentSummary>(TaskCreationOptions.RunContinuationsAsynchronously);

        worker.Progress += item =>
        {
            lock (progress)
            {
                progress.Add(item);
            }
        };
        worker.Changes += _ => Interlocked.Increment(ref changeBatches);
        worker.Completed += summary => completed.TrySetResult(summary);

        worker.Start(names.Keys.ToList(), names, keywords);

        var summary = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        lock (progress)
        {
            return new RunResult(summary, progress.ToList(), changeBatches, cache);
        }
    }

    /// <summary>拼一段 winget show 输出（中文标签行，与真实的 zh-CN 输出同构）。</summary>
    private static string ShowOutput(string packageId, string description)
        => $"Found Package [{packageId}]\r\nVersion: 1.0.0\r\n描述: {description}\r\n";

    [Fact]
    public async Task MetadataPromotion_IsExemptedForSdks()
    {
        // .NET SDK 的描述自称「包含 .NET 运行时」，元数据会判 true；
        // 关键词层已经把它判成软件，这里必须顶住，否则 3.0.3 的豁免等于白做
        const string id = "Microsoft.DotNet.SDK.10";
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [id] = "Microsoft .NET SDK 10.0.402",
        };
        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [id] = ShowOutput(id, "The .NET SDK includes the .NET runtime and command line tools."),
        };
        var keywords = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [id] = false };

        var result = await RunWorkerAsync(outputs, names, keywords);

        Assert.False(result.Cache.Get(id));
        Assert.Equal(0, result.Summary.Promoted);
        Assert.Equal(1, result.Summary.Kept);
        Assert.Equal(1, result.Summary.SdkExempted);
        Assert.Equal(0, result.ChangeBatches); // 初判与结论一致 → 不惊动界面
    }

    [Fact]
    public async Task MetadataPromotion_KeepsRealRuntimesWithSdkInName()
    {
        const string id = "Microsoft.WindowsAppRuntime.1.7";
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [id] = "Windows App SDK Runtime 1.7",
        };
        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [id] = ShowOutput(id, "Runtime for apps built with the Windows App SDK."),
        };
        var keywords = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [id] = false };

        var result = await RunWorkerAsync(outputs, names, keywords);

        Assert.True(result.Cache.Get(id));
        Assert.Equal(1, result.Summary.Promoted);
        Assert.Equal(0, result.Summary.SdkExempted);
        Assert.Equal(1, result.ChangeBatches);
    }

    [Fact]
    public async Task MetadataPromotion_RedistributableBeatsSdkExemption()
    {
        // 「整包即分发件」的强信号压过豁免：名字带 SDK 也照判运行库
        const string id = "Vendor.Toolkit.SDK";
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [id] = "Vendor Toolkit SDK",
        };
        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [id] = ShowOutput(id, "Microsoft Visual C++ Redistributable package for the toolkit."),
        };
        var keywords = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [id] = false };

        var result = await RunWorkerAsync(outputs, names, keywords);

        Assert.True(result.Cache.Get(id));
        Assert.Equal(1, result.Summary.Promoted);
        Assert.Equal(0, result.Summary.SdkExempted);
    }

    [Fact]
    public async Task Progress_ReportsEveryItemStartingFromZero()
    {
        var ids = new[] { "Vendor.One", "Vendor.Two" };
        var names = ids.ToDictionary(id => id, id => id, StringComparer.OrdinalIgnoreCase);
        var keywords = ids.ToDictionary(id => id, _ => false, StringComparer.OrdinalIgnoreCase);
        var outputs = ids.ToDictionary(
            id => id,
            id => ShowOutput(id, "A desktop application."),
            StringComparer.OrdinalIgnoreCase);

        var result = await RunWorkerAsync(outputs, names, keywords);

        var progress = result.Progress;
        Assert.NotEmpty(progress);
        Assert.Equal(0, progress[0].Done);        // 先报 0/N，界面立刻有解释
        Assert.Equal(2, progress[0].Total);
        Assert.Equal(2, progress[^1].Done);       // 收尾时走满
        Assert.Equal(2, progress[^1].Total);
        Assert.Equal(0, result.Summary.Promoted);
        Assert.Equal(2, result.Summary.Kept);
    }
}
