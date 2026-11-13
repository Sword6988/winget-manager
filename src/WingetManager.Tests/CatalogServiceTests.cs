using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WingetManager.Infra;
using WingetManager.Models;
using WingetManager.Services;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// 目录服务用例：去重口径、reinstallable 判定、关键词判定、缓存 True 覆盖 / False 不打假、排序、统计。
/// 断言口径全部来自原 Python 版 tools/test_smoke.py。
/// </summary>
public sealed class CatalogServiceTests
{
    /// <summary>可替换 list/show 的假仓储（对应 Python 测试里的 FakeRepo / FakeScanRepo）。</summary>
    internal sealed class FakeRepository : WingetRepository
    {
        private readonly string _listStdout;
        private readonly Func<string, string>? _showOutput;

        public FakeRepository(string listStdout, Func<string, string>? showOutput = null)
            : base(AppConfig.CreateForTests("fake-winget.exe", 60, 30))
        {
            _listStdout = listStdout;
            _showOutput = showOutput;
        }

        public int ListCalls { get; private set; }

        public List<string> ShowCalls { get; } = new();

        public Exception? ListException { get; set; }

        public override Task<CommandResult> ListInstalledAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            if (ListException is not null)
            {
                throw ListException;
            }

            return Task.FromResult(new CommandResult
            {
                Args = new[] { "list" },
                ReturnCode = 0,
                Stdout = _listStdout,
            });
        }

        public override Task<CommandResult> ShowMetadataAsync(string packageId, CancellationToken cancellationToken = default)
        {
            ShowCalls.Add(packageId);
            var output = _showOutput?.Invoke(packageId) ?? string.Empty;
            return Task.FromResult(new CommandResult
            {
                Args = new[] { "show", packageId },
                ReturnCode = 0,
                Stdout = output,
            });
        }
    }

    private static string TempFile(string name)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpm-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, name);
    }

    // winget list 表（字段间留 2+ 空格，与真实输出同构）
    private const string ListHeader = "名称    ID                     版本  源";

    private static string ListRow(string name, string packageId, string version = "1.0", string source = "winget")
        => $"{name}    {packageId}    {version}    {source}";

    private static string BuildList(params string[] rows)
        => string.Join("\n", new[] { ListHeader }.Concat(rows)) + "\n";

    private static CatalogService CreateService(string listStdout, RuntimeCache? cache = null,
                                                FakeRepository? repository = null)
    {
        var repo = repository ?? new FakeRepository(listStdout);
        return new CatalogService(AppConfig.CreateForTests("fake-winget.exe", 60, 30), repo, cache);
    }

    // ── 分组三件套 ────────────────────────────────────────────────────

    [Fact]
    public async Task BuildSnapshot_ThreeGroupsDeterminedBySourceColumn()
    {
        var repo = new FakeRepository(
            "名称            ID                            版本     源\n" +
            "Google Chrome   Google.Chrome                 1.0      winget\n" +
            "VC++ 运行库     Microsoft.VCRedist.2015+.x64  14.0     winget\n" +
            "商店应用        Microsoft.WindowsStore        2.0      msstore\n" +
            "注册表项        Vendor.Registry.Tool          3.0\n");

        var snapshot = await CreateService(string.Empty, repository: repo).BuildSnapshotAsync(CancellationToken.None);
        var byId = new Dictionary<string, Package>(StringComparer.Ordinal);
        foreach (var package in snapshot.Packages)
        {
            byId[package.PackageIdentifier] = package;
        }

        Assert.Equal(4, byId.Count);
        Assert.True(byId["Google.Chrome"].Reinstallable);
        Assert.True(byId["Microsoft.WindowsStore"].Reinstallable); // msstore 源也能重装
        Assert.True(byId["Microsoft.VCRedist.2015+.x64"].IsDependency); // 运行库角标
        Assert.False(byId["Vendor.Registry.Tool"].Reinstallable); // 空源判为不可重装

        Assert.Equal(3, snapshot.Counts.Reinstallable);
        Assert.Equal(1, snapshot.Counts.Unsupported);
        Assert.Equal(1, snapshot.Counts.Dependencies);
        Assert.Equal(4, snapshot.Counts.Total);

        // 可重装的排前面，装不上的沉底
        Assert.False(snapshot.Packages[^1].Reinstallable);
    }

    [Theory]
    [InlineData("winget", true)]
    [InlineData("WINGET", true)]
    [InlineData("  winget  ", true)]
    [InlineData("\tWinget\n", true)]
    [InlineData("msstore", true)]
    [InlineData("MSStore", true)]
    [InlineData(" msstore ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("registry", false)]
    [InlineData("msix", false)]
    [InlineData("wingetx", false)]
    public void IsReinstallable_OnlyWingetAndMsStore(string source, bool expected)
    {
        var raw = new RawPackage("x", "Vendor.App", "1.0", string.Empty, source);

        Assert.Equal(expected, CatalogService.IsReinstallable(raw));
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022", "Microsoft.VCRedist.2015+.x64", true)]
    [InlineData("Microsoft .NET Runtime 10", "Microsoft.DotNet.Runtime.10", true)]
    [InlineData("UI.Xaml", "Microsoft.UI.Xaml.2.8", true)]
    [InlineData("Windows App Runtime", "Microsoft.WindowsAppRuntime.1.7", true)]
    [InlineData("Microsoft Edge WebView2 Runtime", "Microsoft.EdgeWebView2Runtime", true)]
    [InlineData("Google Chrome", "Google.Chrome", false)]
    [InlineData("7-Zip", "7zip.7zip", false)]
    public void IsDependencyByKeyword_MatchesNameOrId(string name, string packageId, bool expected)
    {
        var raw = new RawPackage(name, packageId);

        Assert.Equal(expected, CatalogService.IsDependencyByKeyword(raw));
    }

    [Theory]
    [InlineData("msix\\Microsoft.WindowsStore_8wekyb3d8bbwe", "msix")]
    [InlineData("ARP\\Machine\\X64\\Vendor.Tool", "registry")]
    [InlineData("Vendor\\Tool", "registry")]
    [InlineData("Google.Chrome", "winget")]
    [InlineData("Microsoft.VCRedist.2015+.x64", "winget")]
    public void IdKind_ClassifiesPackageId(string packageId, string expected)
    {
        Assert.Equal(expected, CatalogService.IdKind(packageId));
    }

    // ── 去重 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildSnapshot_DedupesByIdKeepingReinstallableRow()
    {
        // 同一 ID 两行（x86/x64 组件）：不可重装的那行让位给可重装的
        var stdout = BuildList(
            ListRow("Chrome (x86)", "Google.Chrome", "1.0", string.Empty),
            ListRow("Chrome", "google.chrome", "1.0", "winget"));

        var snapshot = await CreateService(stdout).BuildSnapshotAsync(CancellationToken.None);

        var package = Assert.Single(snapshot.Packages);
        // 保留的是「后出现的可重装行」这一整条（ID 大小写按原始行保留，与 Python 版一致）
        Assert.Equal("google.chrome", package.PackageIdentifier);
        Assert.True(package.Reinstallable);
        Assert.Equal("Chrome", package.Name);
    }

    [Fact]
    public async Task BuildSnapshot_DedupesCaseInsensitively()
    {
        var stdout = BuildList(
            ListRow("Chrome A", "Google.Chrome", "1.0"),
            ListRow("Chrome B", "GOOGLE.CHROME", "1.0"));

        var snapshot = await CreateService(stdout).BuildSnapshotAsync(CancellationToken.None);

        Assert.Single(snapshot.Packages);
    }

    // ── 排序 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildSnapshot_SortsReinstallableFirstThenByNameIgnoringCase()
    {
        var stdout = BuildList(
            ListRow("zulu", "Vendor.Zulu", "1.0", string.Empty),
            ListRow("beta", "Vendor.Beta", "1.0"),
            ListRow("Alpha", "Vendor.Alpha", "1.0"),
            ListRow("aaa", "Vendor.Aaa", "1.0", string.Empty));

        var snapshot = await CreateService(stdout).BuildSnapshotAsync(CancellationToken.None);

        Assert.Equal(new[] { "Vendor.Alpha", "Vendor.Beta", "Vendor.Aaa", "Vendor.Zulu" },
                     new[] { snapshot.Packages[0].PackageIdentifier, snapshot.Packages[1].PackageIdentifier,
                             snapshot.Packages[2].PackageIdentifier, snapshot.Packages[3].PackageIdentifier });
    }

    // ── 元数据优先级：只补漏不打假 ────────────────────────────────────

    [Fact]
    public async Task BuildSnapshot_MetadataOnlyOverridesWhenTrue()
    {
        var cache = new RuntimeCache(TempFile("mc-priority.json"));
        cache.Put("Vendor.VcRedist.AT", true);   // 关键词 T + 缓存 T
        cache.Put("Vendor.VcRedist.AF", false);  // 关键词 T + 缓存 F → 仍 T
        cache.Put("Vendor.App.CT", true);        // 关键词 F + 缓存 T → T
        cache.Put("Vendor.App.CF", false);       // 关键词 F + 缓存 F → F

        var stdout = BuildList(
            ListRow("VcRed A", "Vendor.VcRedist.AT"),
            ListRow("VcRed B", "Vendor.VcRedist.AF"),
            ListRow("Plain A", "Vendor.App.CT"),
            ListRow("Plain B", "Vendor.App.CF"));

        var snapshot = await CreateService(stdout, cache).BuildSnapshotAsync(CancellationToken.None);
        var dependency = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var package in snapshot.Packages)
        {
            dependency[package.PackageIdentifier] = package.IsDependency;
        }

        Assert.Equal(4, dependency.Count);
        Assert.True(dependency["Vendor.VcRedist.AT"]);
        Assert.True(dependency["Vendor.VcRedist.AF"], "关键词 T + 缓存 F：元数据没命中信号，不得把已认定的运行库降级");
        Assert.True(dependency["Vendor.App.CT"], "关键词 F + 缓存 T：补漏为运行库");
        Assert.False(dependency["Vendor.App.CF"]);
    }

    [Fact]
    public void PendingMetadataIds_SkipsKeywordDependenciesAndCached()
    {
        var cache = new RuntimeCache(TempFile("mc-pending-kw.json"));
        cache.Put("Vendor.App.Done", false);

        var packages = new List<Package>
        {
            new() { PackageIdentifier = "Microsoft.UI.Xaml.2.8", Name = "UI.Xaml", Reinstallable = true, IsDependency = true },
            new() { PackageIdentifier = "Microsoft.VSTOR", Name = "VS Tools", Reinstallable = true, IsDependency = false },
            new() { PackageIdentifier = "Vendor.App.Done", Name = "已核查软件", Reinstallable = true, IsDependency = false },
            new() { PackageIdentifier = "Vendor.Reg", Name = "注册表项", Reinstallable = false, IsDependency = false },
        };
        var snapshot = new CatalogSnapshot { Packages = packages };

        var service = new CatalogService(AppConfig.CreateForTests("fake-winget.exe"), new FakeRepository(string.Empty), cache);
        var pending = service.PendingMetadataIds(snapshot);

        Assert.Equal(new[] { "Microsoft.VSTOR" }, pending);
    }

    [Fact]
    public void PendingMetadataIds_ReturnsEmptyWithoutCache()
    {
        var snapshot = new CatalogSnapshot
        {
            Packages = new List<Package> { new() { PackageIdentifier = "Vendor.App", Reinstallable = true } },
        };
        var service = new CatalogService(AppConfig.CreateForTests("fake-winget.exe"), new FakeRepository(string.Empty));

        Assert.Empty(service.PendingMetadataIds(snapshot));
    }

    // ── 缓存 TTL ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetCatalog_SecondCallHitsCache()
    {
        var repo = new FakeRepository(string.Empty);
        var service = new CatalogService(AppConfig.CreateForTests("fake-winget.exe", 60, 30), repo);

        var first = await service.GetCatalogAsync(force: false, CancellationToken.None, "1.0");
        var second = await service.GetCatalogAsync(force: false, CancellationToken.None, "1.0");

        Assert.Equal(1, repo.ListCalls);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetCatalog_ForceRescans()
    {
        var repo = new FakeRepository(string.Empty);
        var service = new CatalogService(AppConfig.CreateForTests("fake-winget.exe", 60, 30), repo);

        await service.GetCatalogAsync(force: false, CancellationToken.None, "1.0");
        await service.GetCatalogAsync(force: true, CancellationToken.None, "1.0");

        Assert.Equal(2, repo.ListCalls);
    }

    [Fact]
    public async Task GetCatalog_ZeroTtlAlwaysRescans()
    {
        var repo = new FakeRepository(string.Empty);
        var service = new CatalogService(AppConfig.CreateForTests("fake-winget.exe", 0, 30), repo);

        await service.GetCatalogAsync(force: false, CancellationToken.None, "1.0");
        await service.GetCatalogAsync(force: false, CancellationToken.None, "1.0");

        Assert.Equal(2, repo.ListCalls);
    }

    // ── 取消 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildSnapshot_PropagatesCancellation()
    {
        var repo = new FakeRepository(string.Empty)
        {
            ListException = new ScanCancelledException("扫描已取消"),
        };
        var service = new CatalogService(AppConfig.CreateForTests("fake-winget.exe"), repo);

        await Assert.ThrowsAsync<ScanCancelledException>(
            () => service.BuildSnapshotAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BuildSnapshot_ThrowsWhenCancelledAfterList()
    {
        using var cts = new CancellationTokenSource();
        var repo = new FakeRepository(string.Empty);
        cts.Cancel();

        var service = new CatalogService(AppConfig.CreateForTests("fake-winget.exe"), repo);

        await Assert.ThrowsAsync<ScanCancelledException>(
            () => service.BuildSnapshotAsync(cts.Token));
    }

    // ── 快照元信息 ────────────────────────────────────────────────────

    [Fact]
    public async Task BuildSnapshot_FillsVersionMachineAndCounts()
    {
        var snapshot = await CreateService(BuildList(ListRow("Git", "Git.Git")))
            .BuildSnapshotAsync(CancellationToken.None, "1.10.0");

        Assert.Equal("1.10.0", snapshot.WingetVersion);
        Assert.NotEmpty(snapshot.GeneratedAt);
        Assert.NotEmpty(snapshot.Machine.Hostname);
        Assert.NotEmpty(snapshot.Machine.Os);
        Assert.NotEmpty(snapshot.Machine.Arch);
        Assert.True(snapshot.DurationMs >= 0);
    }

    [Fact]
    public async Task BuildSnapshot_EmptyVersionFallsBackToUnknown()
    {
        var snapshot = await CreateService(BuildList(ListRow("Git", "Git.Git")))
            .BuildSnapshotAsync(CancellationToken.None);

        Assert.Equal(WingetRepository.UnknownVersion, snapshot.WingetVersion);
    }

    [Fact]
    public async Task BuildSnapshot_EmptyNameFallsBackToPackageId()
    {
        var stdout = BuildList(ListRow(string.Empty, "Vendor.Anonymous"));
        var snapshot = await CreateService(stdout).BuildSnapshotAsync(CancellationToken.None);

        Assert.Equal("Vendor.Anonymous", Assert.Single(snapshot.Packages).Name);
    }
}
