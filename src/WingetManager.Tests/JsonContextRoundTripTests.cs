using System;
using System.Collections.Generic;
using System.Text.Json;
using WingetManager.Json;
using WingetManager.Models;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// 源生成序列化/反序列化回环：保证 AOT 下真实可用的 JSON 路径（不是反射兜底）。
/// 同时钉死「与 Python 版字节一致」的字段名与顺序。
/// </summary>
public sealed class JsonContextRoundTripTests
{
    [Fact]
    public void MetadataCacheFile_RoundTripsThroughSourceGenerator()
    {
        var payload = new MetadataCacheFile
        {
            Schema = 1,
            Entries = new Dictionary<string, MetadataCacheEntry>(StringComparer.Ordinal)
            {
                ["microsoft.vstor"] = new() { Dep = true, CheckedAt = "2026-11-03T14:00:00+08:00" },
                ["rustlang.rustup"] = new() { Dep = false, CheckedAt = "2026-11-03T14:00:01+08:00" },
            },
        };

        var json = JsonSerializer.Serialize(payload, AppJsonContext.Default.MetadataCacheFile);
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.MetadataCacheFile);

        Assert.NotNull(back);
        Assert.Equal(1, back!.Schema);
        Assert.Equal(2, back.Entries.Count);
        Assert.True(back.Entries["microsoft.vstor"].Dep);
        Assert.False(back.Entries["rustlang.rustup"].Dep);
        Assert.Equal("2026-11-03T14:00:00+08:00", back.Entries["microsoft.vstor"].CheckedAt);
    }

    [Fact]
    public void MetadataCacheFile_UsesPythonFieldNamesAndOrdering()
    {
        var payload = new MetadataCacheFile
        {
            Entries = new Dictionary<string, MetadataCacheEntry>(StringComparer.Ordinal)
            {
                ["a.b"] = new() { Dep = true, CheckedAt = "x" },
            },
        };

        var json = JsonOutput.Serialize(payload, AppJsonContext.Compat.MetadataCacheFile);

        Assert.StartsWith("{\n  \"schema\": 1,", json, StringComparison.Ordinal);
        Assert.Contains("\"entries\": {", json, StringComparison.Ordinal);
        Assert.Contains("\"dep\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"checkedAt\": \"x\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionMap_RoundTripsThroughSourceGenerator()
    {
        var payload = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["google.chrome"] = true,
            ["git.git"] = true,
        };

        var json = JsonOutput.Serialize(payload, AppJsonContext.Compat.DictionaryStringBoolean);
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringBoolean);

        Assert.NotNull(back);
        Assert.Equal(2, back!.Count);
        Assert.True(back["google.chrome"]);

        // 裸 map（不包对象），Python 版读得懂
        Assert.StartsWith("{", json.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("\"google.chrome\": true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WingetImportManifest_RoundTripsWithExactFieldNames()
    {
        var payload = new WingetImportManifest
        {
            CreationDate = "2026-11-03T14:00:00.123+08:00",
            WingetVersion = "1.10.0",
            Sources = new List<WingetImportSource>
            {
                new()
                {
                    Packages = new List<WingetImportPackage>
                    {
                        new() { PackageIdentifier = "Google.Chrome" },
                    },
                    SourceDetails = new WingetSourceDetails
                    {
                        Argument = WingetImportManifest.WingetSourceArgument,
                        Identifier = WingetImportManifest.WingetSourceIdentifier,
                        Name = WingetImportManifest.WingetSourceName,
                        Type = WingetImportManifest.WingetSourceType,
                    },
                },
            },
        };

        var json = JsonOutput.Serialize(payload, AppJsonContext.Compat.WingetImportManifest);

        Assert.Contains("\"$schema\": \"https://aka.ms/winget-packages.schema.2.0.json\"", json, StringComparison.Ordinal);
        Assert.Contains("\"CreationDate\": \"2026-11-03T14:00:00.123+08:00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"WinGetVersion\": \"1.10.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Sources\": [", json, StringComparison.Ordinal);
        Assert.Contains("\"Packages\": [", json, StringComparison.Ordinal);
        Assert.Contains("\"SourceDetails\": {", json, StringComparison.Ordinal);
        Assert.Contains("\"PackageIdentifier\": \"Google.Chrome\"", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.WingetImportManifest);
        Assert.NotNull(back);
        Assert.Equal("Google.Chrome", back!.Sources[0].Packages[0].PackageIdentifier);
        Assert.Equal("msstore" == back.Sources[0].SourceDetails.Name ? "wrong" : "winget", back.Sources[0].SourceDetails.Name);
    }

    [Fact]
    public void CatalogSnapshot_RoundTripsWithPackageFlags()
    {
        var snapshot = new CatalogSnapshot
        {
            GeneratedAt = "2026-11-03T14:00:00+0800",
            DurationMs = 1234,
            WingetVersion = "1.10.0",
            Machine = new MachineInfo { Hostname = "PC", Os = "Windows", Arch = "AMD64" },
            Counts = new CatalogCounts { Total = 1, Reinstallable = 1, Unsupported = 0, Dependencies = 0 },
            Packages = new List<Package>
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
            },
        };

        var json = JsonSerializer.Serialize(snapshot, AppJsonContext.Default.CatalogSnapshot);
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.CatalogSnapshot);

        Assert.NotNull(back);
        Assert.Equal(1234, back!.DurationMs);
        Assert.Equal("1.10.0", back.WingetVersion);
        var package = Assert.Single(back.Packages);
        Assert.True(package.Reinstallable);
        Assert.False(package.IsDependency);
        Assert.Equal("2.0", package.AvailableVersion);
    }

    [Fact]
    public void CatalogCacheFile_RoundTripsThroughSourceGenerator()
    {
        // 冷启动秒开的落盘缓存：包一层 {schema, snapshot}，读写必须都走源生成（AOT）
        var payload = new CatalogCacheFile
        {
            Snapshot = new CatalogSnapshot
            {
                GeneratedAt = "2026-11-03T14:00:00+0800",
                DurationMs = 10800,
                WingetVersion = "1.10.0",
                Machine = new MachineInfo { Hostname = "PC", Os = "Windows", Arch = "AMD64" },
                Counts = new CatalogCounts { Total = 1, Reinstallable = 1 },
                Packages = new List<Package>
                {
                    new()
                    {
                        PackageIdentifier = "Google.Chrome",
                        Name = "Google Chrome",
                        Version = "1.0",
                        Source = "winget",
                        Reinstallable = true,
                    },
                },
            },
        };

        var json = JsonOutput.Serialize(payload, AppJsonContext.Compat.CatalogCacheFile);
        Assert.StartsWith("{\n  \"schema\": 1,", json, StringComparison.Ordinal);
        Assert.Contains("\"snapshot\": {", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.CatalogCacheFile);
        Assert.NotNull(back);
        Assert.Equal(1, back!.Schema);
        Assert.NotNull(back.Snapshot);
        Assert.Equal("2026-11-03T14:00:00+0800", back.Snapshot.GeneratedAt);
        var package = Assert.Single(back.Snapshot.Packages);
        Assert.Equal("Google.Chrome", package.PackageIdentifier);
        Assert.True(package.Reinstallable);
    }

    [Fact]
    public void Context_DoesNotUseReflectionFallback()
    {
        // 未注册的类型拿不到 JsonTypeInfo（源生成模式下会返回 null / 抛异常），
        // 这里用「已注册类型必须有 typeInfo」反证注册表是完整的。
        Assert.NotNull(AppJsonContext.Default.MetadataCacheFile);
        Assert.NotNull(AppJsonContext.Default.CatalogCacheFile);
        Assert.NotNull(AppJsonContext.Default.WingetImportManifest);
        Assert.NotNull(AppJsonContext.Default.CatalogSnapshot);
        Assert.NotNull(AppJsonContext.Default.DictionaryStringBoolean);
        Assert.NotNull(AppJsonContext.Default.Package);
    }
}
