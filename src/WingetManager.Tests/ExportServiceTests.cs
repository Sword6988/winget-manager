using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WingetManager.Infra;
using WingetManager.Models;
using WingetManager.Services;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// 三件套导出用例（对应 <c>services/profiles.py</c>）。
///
/// 断言基准全部来自「原 Python 版真实产出」：<c>TestData\restore-bat-*.txt</c> 与
/// <c>TestData\unreinstallable.txt</c> 是用 <c>profiles.build_restore_bat()</c> /
/// <c>build_unreinstallable_txt()</c> 直接生成的字节级基线，保证改写后行为不走样。
/// </summary>
public sealed class ExportServiceTests
{
    private const string BaselineStamp = "导出时间：2026-11-13 10:07";

    private static string TestDataPath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "TestData", fileName);

    private static string ReadTestData(string fileName)
        => File.ReadAllText(TestDataPath(fileName), Encoding.UTF8);

    /// <summary>
    /// 把基线里的时间戳行替换成占位符，便于和运行时生成的文本比较。
    /// </summary>
    private static string NormalizeStamp(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("导出时间：", StringComparison.Ordinal))
            {
                lines[index] = BaselineStamp;
            }
        }

        return string.Join("\n", lines);
    }

    private static Package MakePackage(
        string id,
        string name = "",
        string version = "",
        string source = "winget",
        bool reinstallable = true,
        bool isDependency = false)
    {
        return new Package
        {
            PackageIdentifier = id,
            Name = name.Length > 0 ? name : id,
            Version = version,
            AvailableVersion = version,
            Source = source,
            IdKind = PackageIdKind.Winget,
            Reinstallable = reinstallable,
            IsDependency = isDependency,
        };
    }

    // ───────────────────────────── 版本归一化 ─────────────────────────────

    [Theory]
    [InlineData("v1.30.100-preview", "1.30.100-preview")]
    [InlineData("1.9.25200", "1.9.25200")]
    [InlineData("  1.7.10582  ", "1.7.10582")]
    [InlineData("1.6", "1.6")]
    [InlineData("2.1.3.4", "2.1.3.4")]
    [InlineData("1.2.3+build5", "1.2.3+build5")]
    [InlineData("winget 版本 1.5.0 可用", "1.0.0")]  // Python 用 re.match，锚定行首
    [InlineData("v1.5.0 (预览)", "1.5.0")]
    [InlineData("unknown", "1.0.0")]
    [InlineData("3", "1.0.0")]
    [InlineData("", "1.0.0")]
    [InlineData(null, "1.0.0")]
    public void NormalizeWingetVersion_FollowsImportSchemaConstraint(string? raw, string expected)
    {
        // winget 的 import schema 只接受数字开头的版本号，解析不出必须回退 1.0.0
        Assert.Equal(expected, ExportService.NormalizeWingetVersion(raw));
    }

    // ───────────────────────────── apps.json 结构 ─────────────────────────────

    [Fact]
    public void BuildManifest_UsesWingetImportSchema2AndNormalizedVersion()
    {
        var service = new ExportService(new LogService(Path.Combine(Path.GetTempPath(), "wpm-export-tests.log")));
        var manifest = service.BuildManifest(
            new[] { MakePackage("Google.Chrome", "Google Chrome", "120.0"), MakePackage("Microsoft.VCRedist", source: "msstore") },
            "v1.30.100-preview");

        Assert.Equal("https://aka.ms/winget-packages.schema.2.0.json", manifest.Schema);
        Assert.Equal("1.30.100-preview", manifest.WingetVersion);
        // creationDate 必须带毫秒与本地时区偏移（Python: isoformat(timespec="milliseconds")）
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2}$", manifest.CreationDate);
    }

    [Fact]
    public void BuildManifest_GroupsBySourceInOrdinalOrder()
    {
        var service = new ExportService(new LogService(Path.Combine(Path.GetTempPath(), "wpm-export-tests.log")));
        var manifest = service.BuildManifest(
            new[]
            {
                MakePackage("A.One"),
                MakePackage("B.Store", source: "msstore"),
                MakePackage("C.Two"),
            },
            "1.9.0");

        // SortedDictionary(Ordinal)：msstore 在 winget 之前，保证同一输入产出稳定文件
        Assert.Equal(2, manifest.Sources.Count);
        Assert.Equal("msstore", manifest.Sources[0].SourceDetails.Name);
        Assert.Equal("winget", manifest.Sources[1].SourceDetails.Name);
        Assert.Equal(new[] { "B.Store" }, manifest.Sources[0].Packages.Select(p => p.PackageIdentifier));
        Assert.Equal(new[] { "A.One", "C.Two" }, manifest.Sources[1].Packages.Select(p => p.PackageIdentifier));
    }

    [Fact]
    public void BuildManifest_FillsStandardSourceDetails()
    {
        var service = new ExportService(new LogService(Path.Combine(Path.GetTempPath(), "wpm-export-tests.log")));
        var manifest = service.BuildManifest(
            new[] { MakePackage("A.One"), MakePackage("B.Store", source: "msstore") },
            "1.9.0");

        var winget = manifest.Sources.Single(s => s.SourceDetails.Name == "winget").SourceDetails;
        Assert.Equal("https://cdn.winget.microsoft.com/cache", winget.Argument);
        Assert.Equal("Microsoft.Winget.Source_8wekyb3d8bbwe", winget.Identifier);
        Assert.Equal("Microsoft.PreIndexed.Package", winget.Type);

        var msstore = manifest.Sources.Single(s => s.SourceDetails.Name == "msstore").SourceDetails;
        Assert.Equal("https://storeedgefd.dsx.mp.microsoft.com/v9.0", msstore.Argument);
        Assert.Equal("StoreEdgeFD", msstore.Identifier);
        Assert.Equal("Microsoft.Rest", msstore.Type);
    }

    [Fact]
    public void BuildManifest_SkipsUnreinstallablePackages()
    {
        var service = new ExportService(new LogService(Path.Combine(Path.GetTempPath(), "wpm-export-tests.log")));
        var manifest = service.BuildManifest(
            new[]
            {
                MakePackage("Good.One"),
                MakePackage("arp\\Bad", "参考组件", source: "msix", reinstallable: false),
            },
            "1.9.0");

        var ids = manifest.Sources.SelectMany(s => s.Packages).Select(p => p.PackageIdentifier).ToList();
        Assert.Equal(new[] { "Good.One" }, ids);
    }

    [Fact]
    public void BuildManifest_UnknownSourceFallsBackToWinget()
    {
        var service = new ExportService(new LogService(Path.Combine(Path.GetTempPath(), "wpm-export-tests.log")));
        var manifest = service.BuildManifest(new[] { MakePackage("A.One", source: "somewhere") }, "1.9.0");

        Assert.Single(manifest.Sources);
        Assert.Equal("winget", manifest.Sources[0].SourceDetails.Name);
    }

    [Fact]
    public void BuildManifest_EmptySelectionProducesNoSources()
    {
        var service = new ExportService(new LogService(Path.Combine(Path.GetTempPath(), "wpm-export-tests.log")));
        var manifest = service.BuildManifest(Array.Empty<Package>(), null);

        Assert.Empty(manifest.Sources);
        Assert.Equal("1.0.0", manifest.WingetVersion);
    }

    [Fact]
    public void BuildManifest_RejectsNull()
    {
        var service = new ExportService(new LogService(Path.Combine(Path.GetTempPath(), "wpm-export-tests.log")));
        Assert.Throws<ArgumentNullException>(() => service.BuildManifest(null!, "1.0.0"));
    }

    [Fact]
    public void SourceBreakdown_CountsOnlyReinstallableAndAlwaysHasBothKeys()
    {
        var service = new ExportService(new LogService(Path.Combine(Path.GetTempPath(), "wpm-export-tests.log")));
        var counts = service.SourceBreakdown(new[]
        {
            MakePackage("A.One"),
            MakePackage("B.Two"),
            MakePackage("C.Store", source: "msstore"),
            MakePackage("arp\\Bad", source: "msix", reinstallable: false),
        });

        Assert.Equal(2, counts["winget"]);
        Assert.Equal(1, counts["msstore"]);

        var empty = service.SourceBreakdown(Array.Empty<Package>());
        Assert.Equal(0, empty["winget"]);
        Assert.Equal(0, empty["msstore"]);
    }

    // ───────────────────────────── 恢复脚本 ─────────────────────────────

    [Fact]
    public void BuildRestoreBat_MatchesPythonBaseline()
    {
        var actual = ExportService.BuildRestoreBat("apps.json", 3, 0);
        Assert.Equal(ReadTestData("restore-bat-plain.txt"), actual);
    }

    [Fact]
    public void BuildRestoreBat_MsStoreTipMatchesPythonBaseline()
    {
        var actual = ExportService.BuildRestoreBat("apps.json", 59, 4);
        Assert.Equal(ReadTestData("restore-bat-msstore.txt"), actual);
    }

    [Fact]
    public void BuildRestoreBat_EscapesPercentAndStripsDirectory()
    {
        // 清单名是用户可控的：basename 防目录穿越，% 双写防变量展开
        var actual = ExportService.BuildRestoreBat(@"..\evil\100%备份.json", 3, 0);
        Assert.Equal(ReadTestData("restore-bat-escape.txt"), actual);
        Assert.DoesNotContain(@"..\evil", actual, StringComparison.Ordinal);
        Assert.Contains("\"100%%备份.json\"", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRestoreBat_ContainsAdminCheckAndIgnoreUnavailable()
    {
        var actual = ExportService.BuildRestoreBat("apps.json", 1, 0);

        // 管理员检查与 --ignore-unavailable 是脚本的两条硬要求（PRD P0-16）
        Assert.Contains("net session >nul 2>nul", actual, StringComparison.Ordinal);
        Assert.Contains("--ignore-unavailable", actual, StringComparison.Ordinal);
        Assert.Contains("--accept-package-agreements", actual, StringComparison.Ordinal);
        Assert.Contains("--accept-source-agreements", actual, StringComparison.Ordinal);
        // cmd 代码页固定 936，中文才不会乱码
        Assert.Contains("chcp 936 >nul", actual, StringComparison.Ordinal);
        // cd /d "%~dp0" 保证双击时相对路径以脚本所在目录为基准
        Assert.Contains("cd /d \"%~dp0\"", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRestoreBat_UsesCrlfLineEndings()
    {
        var actual = ExportService.BuildRestoreBat("apps.json", 1, 0);

        Assert.EndsWith("\r\n", actual, StringComparison.Ordinal);
        // .bat 里刻意留了空行（\r\n\r\n），所以只能检查「没有孤立的 \n」
        Assert.DoesNotContain("\n", actual.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRestoreBat_RejectsGbkUnencodableFileName()
    {
        // emoji 在 cp936 里编不出来，写盘会炸；必须提前给出可操作的提示
        var error = Assert.Throws<ExportInvalidException>(
            () => ExportService.BuildRestoreBat("备份😀.json", 1, 0));

        Assert.Equal("export_invalid", error.Code);
        Assert.Contains("GBK", error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRestoreBat_RejectsEmptyName()
    {
        Assert.Throws<ArgumentException>(() => ExportService.BuildRestoreBat(string.Empty, 1, 0));
    }

    // ───────────────────────────── 无法自动重装清单 ─────────────────────────────

    [Fact]
    public void BuildUnreinstallableList_MatchesPythonBaseline()
    {
        var packages = new[]
        {
            MakePackage("Vendor.Tool0", "参考组件 0", source: string.Empty, reinstallable: false),
            MakePackage("Vendor.Tool1", "参考组件 1", "3.0", "msix", reinstallable: false),
            MakePackage("Good.One", "好软件", "1.0"),
        };

        var actual = NormalizeStamp(ExportService.BuildUnreinstallableList(packages));
        var expected = NormalizeStamp(ReadTestData("unreinstallable.txt"));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildUnreinstallableList_OrdersByNameThenId()
    {
        var packages = new[]
        {
            MakePackage("b.two", "Beta", source: "msix", reinstallable: false),
            MakePackage("a.one", "alpha", source: "msix", reinstallable: false),
            MakePackage("c.three", "Zulu", source: "msix", reinstallable: false),
        };

        var lines = ExportService.BuildUnreinstallableList(packages)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => line.StartsWith("  · ", StringComparison.Ordinal))
            .ToList();

        // 大小写不敏感的名字序：alpha < Beta < Zulu
        Assert.Equal(new[] { "  · alpha", "  · Beta", "  · Zulu" }, lines);
    }

    [Fact]
    public void BuildUnreinstallableList_EmptySaysEverythingIsReinstallable()
    {
        var text = ExportService.BuildUnreinstallableList(new[] { MakePackage("A.One") });

        Assert.Contains("共 0 项", text, StringComparison.Ordinal);
        Assert.Contains("（没有这类软件，全部都能自动重装）", text, StringComparison.Ordinal);
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
    }

    // ───────────────────────────── 端到端 ─────────────────────────────

    [Fact]
    public async Task ExportAsync_WritesTrioWithExpectedEncodings()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new ExportService(new LogService(Path.Combine(directory, "test.log")));
            var selected = new[]
            {
                MakePackage("Google.Chrome", "Google Chrome", "120.0.6099.130"),
                MakePackage("MoonshotAI.Kimi", "Kimi", source: "msstore"),
            };
            var all = selected
                .Concat(new[] { MakePackage("arp\\Bad", "参考组件", source: "msix", reinstallable: false) })
                .ToList();

            var result = await service.ExportAsync(
                Path.Combine(directory, "apps.json"), selected, all, "v1.9.25200", CancellationToken.None);

            Assert.Empty(result.Warnings);
            Assert.Equal(1, result.WingetCount);
            Assert.Equal(1, result.MsStoreCount);
            Assert.Equal(3, result.Files.Count);

            // 1) apps.json —— UTF-8 无 BOM，2 空格缩进、LF 结尾（与 Python json.dumps 对齐）
            var jsonPath = Path.Combine(directory, "apps.json");
            Assert.True(File.Exists(jsonPath));
            var jsonBytes = File.ReadAllBytes(jsonPath);
            Assert.False(jsonBytes.Length > 3 && jsonBytes[0] == 0xEF && jsonBytes[1] == 0xBB);
            var json = Encoding.UTF8.GetString(jsonBytes);
            Assert.StartsWith("{\n  \"$schema\": \"https://aka.ms/winget-packages.schema.2.0.json\",", json, StringComparison.Ordinal);
            Assert.Contains("\"Sources\": [", json, StringComparison.Ordinal);
            Assert.Contains("\"WinGetVersion\": \"1.9.25200\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\r\n", json, StringComparison.Ordinal);

            // 2) 恢复脚本 —— GBK 写盘（cmd 代码页 936），文件名派生自清单名
            var batPath = Path.Combine(directory, "恢复-apps.bat");
            Assert.Equal(batPath, result.RestoreScriptPath);
            Assert.True(File.Exists(batPath));
            var gbkText = TextEncoding.Gbk.GetString(File.ReadAllBytes(batPath));
            Assert.Contains("Winget 软件管家 · 恢复脚本", gbkText, StringComparison.Ordinal);
            Assert.Contains("（2 个软件包）", gbkText, StringComparison.Ordinal);
            Assert.Contains("其中 1 个来自微软商店 msstore", gbkText, StringComparison.Ordinal);

            // 3) 无法自动重装清单 —— UTF-8 BOM（记事本友好），只列不可重装项
            var txtPath = Path.Combine(directory, "无法自动重装清单-apps.txt");
            Assert.Equal(txtPath, result.UnreinstallableListPath);
            Assert.True(File.Exists(txtPath));
            var txtBytes = File.ReadAllBytes(txtPath);
            Assert.True(txtBytes.Length > 3 && txtBytes[0] == 0xEF && txtBytes[1] == 0xBB && txtBytes[2] == 0xBF);
            var txt = Encoding.UTF8.GetString(txtBytes);
            Assert.Contains("arp\\Bad", txt, StringComparison.Ordinal);
            Assert.DoesNotContain("Google.Chrome", txt, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task ExportAsync_AppendsJsonExtensionWhenMissing()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new ExportService(new LogService(Path.Combine(directory, "test.log")));
            var result = await service.ExportAsync(
                Path.Combine(directory, "我的清单"),
                new[] { MakePackage("A.One") },
                new[] { MakePackage("A.One") },
                "1.9.0",
                CancellationToken.None);

            Assert.Equal(Path.Combine(directory, "我的清单.json"), result.Files[0]);
            Assert.True(File.Exists(Path.Combine(directory, "恢复-我的清单.bat")));
            Assert.False(File.Exists(Path.Combine(directory, "无法自动重装清单-我的清单.txt")));
            Assert.Null(result.UnreinstallableListPath);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task ExportAsync_SkipsTxtWhenEverythingIsReinstallable()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new ExportService(new LogService(Path.Combine(directory, "test.log")));
            var packages = new[] { MakePackage("A.One"), MakePackage("B.Two") };
            var result = await service.ExportAsync(
                Path.Combine(directory, "apps.json"), packages, packages, "1.9.0", CancellationToken.None);

            Assert.Equal(2, result.Files.Count);
            Assert.Null(result.UnreinstallableListPath);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task ExportAsync_OverwritesExistingManifest()
    {
        var directory = CreateTempDirectory();
        try
        {
            var jsonPath = Path.Combine(directory, "apps.json");
            await File.WriteAllTextAsync(jsonPath, "旧的清单");

            var service = new ExportService(new LogService(Path.Combine(directory, "test.log")));
            await service.ExportAsync(
                jsonPath, new[] { MakePackage("A.One") }, new[] { MakePackage("A.One") }, "1.9.0",
                CancellationToken.None);

            var json = await File.ReadAllTextAsync(jsonPath);
            Assert.Contains("A.One", json, StringComparison.Ordinal);
            // 原子写不留临时文件
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task ExportAsync_RejectsNullSelection()
    {
        var service = new ExportService(new LogService(Path.Combine(Path.GetTempPath(), "wpm-export-tests.log")));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.ExportAsync("apps.json", null!, Array.Empty<Package>(), "1.0.0", CancellationToken.None))
            ;
    }

    // ───────────────────────────── 默认路径链 ─────────────────────────────

    [Fact]
    public void ResolveDefaultExportPath_PrefersLastUsedDirectory()
    {
        var directory = CreateTempDirectory();
        try
        {
            var lastUsed = Path.Combine(directory, "my-apps.json");
            Assert.Equal(lastUsed, ExportService.ResolveDefaultExportPath(lastUsed));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ResolveDefaultExportPath_IgnoresMissingDirectory()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "wpm-not-exists-" + Guid.NewGuid().ToString("N"), "apps.json");
        var resolved = ExportService.ResolveDefaultExportPath(bogus);

        Assert.NotEqual(bogus, resolved);
        Assert.EndsWith("apps.json", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveDefaultExportPath_FallsBackToDriveRootOrDocuments()
    {
        var resolved = ExportService.ResolveDefaultExportPath(null);
        Assert.EndsWith("apps.json", resolved, StringComparison.OrdinalIgnoreCase);

        var expectedRoot = Directory.Exists(ExportService.PreferredDriveRoot)
            ? Path.Combine(ExportService.PreferredDriveRoot, "apps.json")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"))
                    ? "Documents"
                    : string.Empty,
                "apps.json");

        Assert.Equal(expectedRoot, resolved);
    }

    [Fact]
    public void ResolveDefaultExportPath_TreatsBlankAsNoRecord()
    {
        var resolved = ExportService.ResolveDefaultExportPath("   ");
        Assert.EndsWith("apps.json", resolved, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "wpm-export-" + Guid.NewGuid().ToString("N"));
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
}
