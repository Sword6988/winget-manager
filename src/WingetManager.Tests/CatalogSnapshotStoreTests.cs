using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WingetManager.Infra;
using WingetManager.Json;
using WingetManager.Models;
using WingetManager.Persistence;
using WingetManager.Services;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// 目录快照落盘缓存用例：读写回环、损坏回退、schema 不符回退、写失败不抛、
/// 扫描完成后自动落盘（CatalogService 集成）。
/// </summary>
public sealed class CatalogSnapshotStoreTests
{
    private static string TempFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wpm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static CatalogSnapshot BuildSnapshot()
    {
        return new CatalogSnapshot
        {
            GeneratedAt = "2026-11-03T14:00:00+0800",
            DurationMs = 10800,
            WingetVersion = "1.10.0",
            Machine = new MachineInfo { Hostname = "PC", Os = "Windows 10.0.26100 (26100)", Arch = "AMD64" },
            Counts = new CatalogCounts { Total = 2, Reinstallable = 1, Unsupported = 1, Dependencies = 1 },
            Packages = new System.Collections.Generic.List<Package>
            {
                new()
                {
                    PackageIdentifier = "Google.Chrome",
                    Name = "Google Chrome",
                    Version = "1.0",
                    AvailableVersion = "2.0",
                    Source = "winget",
                    IdKind = PackageIdKind.Winget,
                    Reinstallable = true,
                    IsDependency = false,
                },
                new()
                {
                    PackageIdentifier = "Microsoft.VCRedist.2015+.x64",
                    Name = "VC++ 运行库",
                    Version = "14.0",
                    Source = "winget",
                    IdKind = PackageIdKind.Winget,
                    Reinstallable = true,
                    IsDependency = true,
                },
            },
        };
    }

    // ── 读写回环 ──────────────────────────────────────────────────────

    [Fact]
    public void Save_ThenLoad_RoundTripsSnapshot()
    {
        var path = TempFile("catalog-cache.json");
        var store = new CatalogSnapshotStore(path);
        var snapshot = BuildSnapshot();

        store.Save(snapshot);

        Assert.True(File.Exists(path));
        var loaded = new CatalogSnapshotStore(path).Load();
        Assert.NotNull(loaded);

        var back = loaded!;
        Assert.Equal("2026-11-03T14:00:00+0800", back.GeneratedAt);
        Assert.Equal(10800, back.DurationMs);
        Assert.Equal("1.10.0", back.WingetVersion);
        Assert.Equal("PC", back.Machine.Hostname);
        Assert.Equal(2, back.Counts.Total);
        Assert.Equal(1, back.Counts.Dependencies);

        Assert.Equal(2, back.Packages.Count);
        Assert.Equal("Google.Chrome", back.Packages[0].PackageIdentifier);
        Assert.Equal("2.0", back.Packages[0].AvailableVersion);
        Assert.True(back.Packages[0].Reinstallable);
        Assert.True(back.Packages[1].IsDependency);
    }

    [Fact]
    public void Save_OverwritesPreviousSnapshot()
    {
        var path = TempFile("catalog-cache.json");
        var store = new CatalogSnapshotStore(path);

        var first = BuildSnapshot();
        first.GeneratedAt = "2026-11-01T08:00:00+0800";
        store.Save(first);

        var second = BuildSnapshot();
        second.GeneratedAt = "2026-11-02T09:00:00+0800";
        store.Save(second);

        var loaded = new CatalogSnapshotStore(path).Load();
        Assert.NotNull(loaded);
        Assert.Equal("2026-11-02T09:00:00+0800", loaded!.GeneratedAt);
    }

    [Fact]
    public void Save_WritesSchemaFieldWithCurrentVersion()
    {
        var path = TempFile("catalog-cache.json");
        new CatalogSnapshotStore(path).Save(BuildSnapshot());

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.True(root.TryGetProperty("snapshot", out _));
    }

    // ── 回退路径（绝不让启动失败） ────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(new CatalogSnapshotStore(TempFile("absent.json")).Load());
    }

    [Fact]
    public void Load_EmptyFile_ReturnsNull()
    {
        var path = TempFile("catalog-cache.json");
        File.WriteAllText(path, string.Empty);

        Assert.Null(new CatalogSnapshotStore(path).Load());
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNull()
    {
        var path = TempFile("catalog-cache.json");
        File.WriteAllText(path, "{\"schema\": 1, \"snapshot\": {\"generatedAt\": \"x");

        Assert.Null(new CatalogSnapshotStore(path).Load());
    }

    [Fact]
    public void Load_SchemaMismatch_ReturnsNull()
    {
        var path = TempFile("catalog-cache.json");
        var snapshot = BuildSnapshot();
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new CatalogCacheFile { Schema = 99, Snapshot = snapshot },
                AppJsonContext.Default.CatalogCacheFile));

        Assert.Null(new CatalogSnapshotStore(path).Load());
    }

    [Fact]
    public void Load_SnapshotWithoutPackages_ReturnsNull()
    {
        var path = TempFile("catalog-cache.json");
        File.WriteAllText(path, "{\"schema\": 1, \"snapshot\": {\"generatedAt\": \"2026-11-03T14:00:00+0800\"}}");

        Assert.Null(new CatalogSnapshotStore(path).Load());
    }

    [Fact]
    public void Load_NullSnapshot_ReturnsNull()
    {
        var path = TempFile("catalog-cache.json");
        File.WriteAllText(path, "{\"schema\": 1, \"snapshot\": null}");

        Assert.Null(new CatalogSnapshotStore(path).Load());
    }

    [Fact]
    public void Load_NotJson_ReturnsNull()
    {
        var path = TempFile("catalog-cache.json");
        File.WriteAllText(path, "这不是 JSON");

        Assert.Null(new CatalogSnapshotStore(path).Load());
    }

    [Fact]
    public void Save_ToDirectoryPath_DoesNotThrow()
    {
        // 目标位置被目录占用（异常环境）：写失败只记日志，绝不能把扫描流程炸掉
        var path = TempFile("occupied.json");
        Directory.CreateDirectory(path);

        var store = new CatalogSnapshotStore(path);
        store.Save(BuildSnapshot()); // 不抛即通过
    }

    // ── 与 CatalogService 的集成：扫描成功即落盘 ──────────────────────

    [Fact]
    public async Task CatalogService_PersistsSnapshotAfterScan()
    {
        const string listOutput =
            "名称            ID                            版本     源\n" +
            "Google Chrome   Google.Chrome                 1.0      winget\n";

        var path = TempFile("catalog-cache.json");
        var store = new CatalogSnapshotStore(path);
        var repo = new CatalogServiceTests.FakeRepository(listOutput);
        var service = new CatalogService(
            AppConfig.CreateForTests("fake-winget.exe", 60, 30), repo, null, null, store);

        await service.GetCatalogAsync(force: false, CancellationToken.None, "1.0");

        Assert.True(File.Exists(path));
        var loaded = new CatalogSnapshotStore(path).Load();
        Assert.NotNull(loaded);

        var package = Assert.Single(loaded!.Packages);
        Assert.Equal("Google.Chrome", package.PackageIdentifier);
        Assert.True(package.Reinstallable);
    }
}
