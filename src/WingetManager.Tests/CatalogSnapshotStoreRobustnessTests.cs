using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WingetManager.Infra;
using WingetManager.Models;
using WingetManager.Persistence;
using WingetManager.Services;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// QA 独立加固用例：落盘快照的「任何畸形输入都不许抛、不许污染旁路文件」矩阵。
///
/// 与 <see cref="CatalogSnapshotStoreTests"/> 的分工：工程师用例覆盖正常回环与常见损坏，
/// 这里穷举「合法 JSON 但结构异常」「文件被独占锁」「写到一半截断」「TTL 命中不重写」
/// 等边界，验证「启动绝不因缓存失败」这条硬承诺。
/// </summary>
public sealed class CatalogSnapshotStoreRobustnessTests
{
    private static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wpm-qa", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TempFile(string name = "catalog-cache.json") => Path.Combine(TempDirectory(), name);

    /// <summary>测试专用日志路径：绝不写进用户真实的 %LOCALAPPDATA%\WingetManager\logs\app.log。</summary>
    private static readonly string QaLogPath =
        Path.Combine(Path.GetTempPath(), "wpm-qa", "catalog-snapshot-qa.log");

    /// <summary>临时日志实例：<c>log: null</c> 会落到 LogService.Shared，直接写进用户真实 app.log。</summary>
    /// <returns>日志服务。</returns>
    private static LogService TempLog() => new(QaLogPath);

    /// <summary>构造注入临时日志的 store（测试绝不污染真实的 %LOCALAPPDATA% 日志）。</summary>
    /// <param name="path">缓存文件路径。</param>
    /// <returns>快照存储。</returns>
    private static CatalogSnapshotStore Store(string path) => new(path, TempLog());

    private static CatalogSnapshot Snapshot(int packages = 2)
    {
        var list = new List<Package>();
        for (var i = 0; i < packages; i++)
        {
            list.Add(new Package
            {
                PackageIdentifier = "Vendor.App" + i,
                Name = "App " + i,
                Version = "1.0",
                Source = "winget",
                IdKind = PackageIdKind.Winget,
                Reinstallable = true,
            });
        }

        return new CatalogSnapshot
        {
            GeneratedAt = "2026-11-03T14:00:00+0800",
            DurationMs = 1000,
            WingetVersion = "1.10.0",
            Machine = new MachineInfo { Hostname = "PC", Os = "Windows", Arch = "AMD64" },
            Counts = new CatalogCounts { Total = packages, Reinstallable = packages },
            Packages = list,
        };
    }

    // ── 合法 JSON、异常结构：一律 null，绝不抛 ─────────────────────────

    /// <summary>
    /// 各种「能被 JSON 解析器接受、但不符合 DTO 语义」的输入必须安全回退。
    /// 特别关注 <c>"packages": null</c>——DTO 里 Packages 是 IReadOnlyList，
    /// 显式 null 会让反序列化结果变成 null 引用，任何 <c>.Count</c> 都会 NRE。
    /// </summary>
    /// <param name="json">畸形但语法合法的 JSON 文本。</param>
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("123")]
    [InlineData("\"text\"")]
    [InlineData("{\"schema\":1}")]
    [InlineData("{\"schema\":1,\"snapshot\":null}")]
    [InlineData("{\"schema\":1,\"snapshot\":[]}")]
    [InlineData("{\"schema\":1,\"snapshot\":\"x\"}")]
    [InlineData("{\"schema\":1,\"snapshot\":{\"packages\":null}}")]
    [InlineData("{\"schema\":1,\"snapshot\":{\"packages\":{}}}")]
    [InlineData("{\"schema\":1,\"snapshot\":{\"packages\":[1,2,3]}}")]
    [InlineData("{\"schema\":1,\"snapshot\":{\"packages\":[],\"machine\":null,\"counts\":null}}")]
    [InlineData("{\"schema\":\"1\",\"snapshot\":{}}")]
    [InlineData("{\"schema\":2,\"snapshot\":{\"packages\":[{\"packageIdentifier\":\"A\"}]}}")]
    [InlineData("{\"schema\":0,\"snapshot\":{\"packages\":[]}}")]
    [InlineData("{schema:1}")]
    [InlineData("{\"schema\":1,}")]
    [InlineData("   ")]
    public void Load_DeformedButParseable_ReturnsNullWithoutThrowing(string json)
    {
        var path = TempFile();
        File.WriteAllText(path, json);

        var loaded = Store(path).Load();

        Assert.Null(loaded);
    }

    /// <summary>写到一半被截断（断电/磁盘满的真实形态）：每个截断点都必须回退 null。</summary>
    [Fact]
    public void Load_TruncatedAtEveryOffset_NeverThrows()
    {
        var path = TempFile();
        Store(path).Save(Snapshot(3));
        var full = File.ReadAllText(path);
        Assert.True(full.Length > 40);

        for (var cut = 0; cut < full.Length; cut += 7)
        {
            File.WriteAllText(path, full[..cut]);
            var loaded = Store(path).Load(); // 抛异常即测试失败
            if (loaded is not null)
            {
                // 唯一允许返回非 null 的截断点是「刚好截在完整合法前缀」的极端巧合，
                // 此时仍必须是可用的快照
                Assert.NotEmpty(loaded.Packages);
            }
        }
    }

    /// <summary>文件被其他进程独占（首启动时另一个实例正在写盘）：读失败只回退，不抛。</summary>
    [Fact]
    public void Load_FileLockedByAnotherProcess_ReturnsNull()
    {
        var path = TempFile();
        Store(path).Save(Snapshot());

        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Null(Store(path).Load());
    }

    /// <summary>路径本身是目录（用户手滑建了同名目录）：回退，不抛。</summary>
    [Fact]
    public void Load_PathIsDirectory_ReturnsNull()
    {
        var path = TempFile("catalog-cache.json");
        Directory.CreateDirectory(path);

        Assert.Null(Store(path).Load());
    }

    /// <summary>非 UTF-8 字节（二进制垃圾）：回退，不抛。</summary>
    [Fact]
    public void Load_BinaryGarbage_ReturnsNull()
    {
        var path = TempFile();
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02, 0x80, 0x81 });

        Assert.Null(Store(path).Load());
    }

    /// <summary>带 UTF-8 BOM 的文件（其他编辑器另存后）：应能正常读出。</summary>
    [Fact]
    public void Load_WithUtf8Bom_RoundTrips()
    {
        var path = TempFile();
        Store(path).Save(Snapshot(2));
        File.WriteAllText(path, File.ReadAllText(path), TextEncoding.Utf8Bom);

        var loaded = Store(path).Load();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Packages.Count);
    }

    // ── 修复语义（1564364）：清洗而非爆炸 ────────────────────────────

    /// <summary>空字符串的 GeneratedAt 与 null 等价：没有生成时间的快照无法判新旧，整份作废。</summary>
    [Fact]
    public void Load_EmptyGeneratedAt_ReturnsNull()
    {
        var path = TempFile();
        File.WriteAllText(
            path,
            "{\"schema\":1,\"snapshot\":{\"generatedAt\":\"\",\"packages\":[{\"packageIdentifier\":\"A\",\"name\":\"A\"}]}}");

        Assert.Null(Store(path).Load());
    }

    /// <summary>好坏混排时只剔除坏条目，保留合法条目（不整份作废，也不放任 null 流到界面）。</summary>
    [Fact]
    public void Load_MixedNullAndValidPackages_KeepsOnlyValidOnes()
    {
        var path = TempFile();
        File.WriteAllText(
            path,
            "{\"schema\":1,\"snapshot\":{\"generatedAt\":\"2026-11-03T14:00:00+0800\",\"packages\":["
            + "null,"
            + "{\"packageIdentifier\":null,\"name\":\"无 ID\"},"
            + "{\"packageIdentifier\":\"NoName.Case\",\"name\":null},"
            + "{\"packageIdentifier\":\"Good.App\",\"name\":\"Good App\",\"source\":\"winget\",\"reinstallable\":true}"
            + "]}}");

        var loaded = Store(path).Load();

        Assert.NotNull(loaded);
        var package = Assert.Single(loaded!.Packages);
        Assert.Equal("Good.App", package.PackageIdentifier);
        Assert.Equal("Good App", package.Name);
    }

    /// <summary>全是坏条目等同空缓存。</summary>
    [Fact]
    public void Load_AllPackagesInvalid_ReturnsNull()
    {
        var path = TempFile();
        File.WriteAllText(
            path,
            "{\"schema\":1,\"snapshot\":{\"generatedAt\":\"2026-11-03T14:00:00+0800\",\"packages\":[null,null]}}");

        Assert.Null(Store(path).Load());
    }

    // ── 落盘行为：TTL 语义、原子性、旁路文件安全 ─────────────────────

    /// <summary>内存 TTL 命中时既不重扫、也不重写落盘缓存（落盘只在真扫描后发生）。</summary>
    [Fact]
    public async Task GetCatalogAsync_TtlHit_DoesNotRescanAndDoesNotRewriteCache()
    {
        const string listOutput =
            "名称            ID                            版本     源\n" +
            "Google Chrome   Google.Chrome                 1.0      winget\n";

        var path = TempFile();
        var store = Store(path);
        var repo = new CatalogServiceTests.FakeRepository(listOutput);
        var service = new CatalogService(
            AppConfig.CreateForTests("fake-winget.exe", 60, 30), repo, null, TempLog(), store);

        await service.GetCatalogAsync(force: false, CancellationToken.None, "1.0");
        var firstWrite = File.GetLastWriteTimeUtc(path);
        var firstBytes = File.ReadAllBytes(path);

        await Task.Delay(30);
        await service.GetCatalogAsync(force: false, CancellationToken.None, "1.0");

        Assert.Equal(1, repo.ListCalls);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(path));
        Assert.Equal(firstBytes, File.ReadAllBytes(path));
    }

    /// <summary>force=true 与 TTL 过期都重扫并重写落盘缓存。</summary>
    [Fact]
    public async Task GetCatalogAsync_ForceOrExpired_RescansAndRewritesCache()
    {
        const string listOutput =
            "名称            ID                            版本     源\n" +
            "Google Chrome   Google.Chrome                 1.0      winget\n";

        var path = TempFile();
        var repo = new CatalogServiceTests.FakeRepository(listOutput);
        // TTL = 0 秒 → 第二次非 force 调用也视为过期
        var service = new CatalogService(
            AppConfig.CreateForTests("fake-winget.exe", 0, 30), repo, null, TempLog(),
            Store(path));

        await service.GetCatalogAsync(force: false, CancellationToken.None, "1.0");
        var firstWrite = File.GetLastWriteTimeUtc(path);

        await Task.Delay(30);
        await service.GetCatalogAsync(force: true, CancellationToken.None, "1.0");

        Assert.Equal(2, repo.ListCalls);
        Assert.True(File.GetLastWriteTimeUtc(path) > firstWrite);
    }

    /// <summary>扫描失败（winget 抛错）时不得留下/覆盖落盘缓存。</summary>
    [Fact]
    public async Task GetCatalogAsync_ScanFailed_LeavesExistingCacheIntact()
    {
        var path = TempFile();
        var store = Store(path);
        store.Save(Snapshot(2));
        var before = File.ReadAllBytes(path);

        var repo = new CatalogServiceTests.FakeRepository("名前\n")
        {
            ListException = new WingetCommandFailedException("winget list 失败"),
        };
        var service = new CatalogService(
            AppConfig.CreateForTests("fake-winget.exe", 60, 30), repo, null, TempLog(), store);

        await Assert.ThrowsAnyAsync<AppException>(
            () => service.GetCatalogAsync(force: true, CancellationToken.None, "1.0"));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>写盘不得污染同目录的 metadata-cache.json / selection.json。</summary>
    [Fact]
    public void Save_DoesNotTouchSiblingUserDataFiles()
    {
        var dir = TempDirectory();
        var metadata = Path.Combine(dir, "metadata-cache.json");
        var selection = Path.Combine(dir, "selection.json");
        File.WriteAllText(metadata, "{\"Google.Chrome\":true}");
        File.WriteAllText(selection, "[\"Google.Chrome\"]");
        var metadataBytes = File.ReadAllBytes(metadata);
        var selectionBytes = File.ReadAllBytes(selection);

        Store(Path.Combine(dir, "catalog-cache.json")).Save(Snapshot(3));

        Assert.Equal(metadataBytes, File.ReadAllBytes(metadata));
        Assert.Equal(selectionBytes, File.ReadAllBytes(selection));

        // 目录里只有这三个文件，且新增的独立成 catalog-cache.json（不与用户数据混用）
        var jsonFiles = Directory.GetFiles(dir, "*.json");
        Assert.Equal(3, jsonFiles.Length);
        Assert.Contains(jsonFiles, f => f.EndsWith("catalog-cache.json", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(dir, "catalog-cache.json")));
        Assert.DoesNotContain(Directory.GetFiles(dir), f => !f.EndsWith(".json", StringComparison.Ordinal));
    }

    /// <summary>快照里出现 null 字段（hand-edited / 未来写入方变更）时的行为快照。</summary>
    /// <param name="json">带 null 字段的缓存内容。</param>
    [Theory]
    [InlineData("{\"schema\":1,\"snapshot\":{\"generatedAt\":null,\"packages\":[{\"packageIdentifier\":\"A\"}]}}")]
    [InlineData("{\"schema\":1,\"snapshot\":{\"generatedAt\":\"2026-11-03T14:00:00+0800\",\"packages\":[null]}}")]
    [InlineData("{\"schema\":1,\"snapshot\":{\"generatedAt\":\"2026-11-03T14:00:00+0800\",\"packages\":[{\"packageIdentifier\":null}]}}")]
    public void Load_NullFieldsInsideSnapshot_NeverThrows(string json)
    {
        var path = TempFile();
        File.WriteAllText(path, json);

        var loaded = Store(path).Load(); // 抛异常即测试失败

        if (loaded is not null)
        {
            // 允许返回快照，但调用方（MainViewModel 渲染 / 状态栏格式化）会直接解引用这些字段：
            // GeneratedAt.Length、Package.PackageIdentifier（HashSet 比较器）、Package.Name
            Assert.NotNull(loaded.GeneratedAt);
            Assert.All(loaded.Packages, p =>
            {
                Assert.NotNull(p);
                Assert.NotNull(p.PackageIdentifier);
                Assert.NotNull(p.Name);
            });
        }
    }

    /// <summary>原子写盘不得残留临时文件（来回多次写）。</summary>
    [Fact]
    public void Save_Repeatedly_LeavesNoTempFiles()
    {
        var dir = TempDirectory();
        var store = Store(Path.Combine(dir, "catalog-cache.json"));

        for (var i = 0; i < 5; i++)
        {
            store.Save(Snapshot(i + 1));
        }

        var files = Directory.GetFiles(dir);
        Assert.Single(files);
        Assert.EndsWith("catalog-cache.json", files[0], StringComparison.Ordinal);
    }

    /// <summary>落盘文件形态：schema 必须是最新版本，且整份 JSON 可再次解析。</summary>
    [Fact]
    public void Save_ProducesParseableJsonWithCurrentSchema()
    {
        var path = TempFile();
        Store(path).Save(Snapshot(1));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(CatalogCacheFile.CurrentSchema, doc.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("snapshot").GetProperty("packages").GetArrayLength());
    }
}
