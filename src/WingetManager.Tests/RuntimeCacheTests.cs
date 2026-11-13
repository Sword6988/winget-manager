using System;
using System.IO;
using System.Text.Json;
using WingetManager.Infra;
using WingetManager.Services;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// 运行库判定缓存用例：落盘回读、schema 不符作废、损坏回退、与 Python 版文件互通。
/// </summary>
public sealed class RuntimeCacheTests
{
    private static string TempFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wpm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    /// <summary>临时日志：用例会触发损坏/schema 回退的 WARN，注入独立文件避免写进真实 app.log。</summary>
    private static LogService TempLog() => new(TempFile("test.log"));

    [Fact]
    public void Get_ReturnsNullWhenCacheEmpty()
    {
        var cache = new RuntimeCache(TempFile("metadata-cache.json"), TempLog());

        Assert.Null(cache.Get("Microsoft.VSTOR"));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Put_WritesSchemaOneAndReloadsFromDisk()
    {
        var path = TempFile("metadata-cache.json");
        var cache = new RuntimeCache(path, TempLog());

        cache.Put("Microsoft.VSTOR", true);

        Assert.True(cache.Get("microsoft.vstor")); // ID 大小写不敏感
        Assert.True(File.Exists(path));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());

        var entry = root.GetProperty("entries").GetProperty("microsoft.vstor");
        Assert.True(entry.GetProperty("dep").GetBoolean());
        Assert.True(entry.TryGetProperty("checkedAt", out var checkedAt));
        Assert.False(string.IsNullOrWhiteSpace(checkedAt.GetString()));

        // 新实例读同一份文件
        Assert.True(new RuntimeCache(path, TempLog()).Get("Microsoft.VSTOR"));
    }

    [Fact]
    public void Put_FalseIsPersistedUnlikeMissingEntry()
    {
        var path = TempFile("metadata-cache.json");
        var cache = new RuntimeCache(path, TempLog());

        cache.Put("Git.Git", false);

        Assert.False(cache.Get("Git.Git"));
        Assert.Null(cache.Get("Unknown.App"));
        Assert.False(new RuntimeCache(path, TempLog()).Get("Git.Git"));
    }

    [Fact]
    public void Load_InvalidSchemaFallsBackToEmpty()
    {
        var path = TempFile("metadata-cache.json");
        File.WriteAllText(path, "{\"schema\": 99, \"entries\": {\"a.b\": {\"dep\": true, \"checkedAt\": \"x\"}}}");

        var cache = new RuntimeCache(path, TempLog());

        Assert.Null(cache.Get("a.b"));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Load_CorruptJsonFallsBackToEmptyAndCanBeRebuilt()
    {
        var path = TempFile("metadata-cache.json");
        File.WriteAllText(path, "{\"schema\": 1, \"entries\": {\"x\": tr");

        var cache = new RuntimeCache(path, TempLog());

        Assert.Null(cache.Get("microsoft.vstor"));

        // 损坏后 put 应重建出合法文件
        cache.Put("a.b", false);
        Assert.False(new RuntimeCache(path, TempLog()).Get("a.b"));
    }

    [Fact]
    public void Load_SkipsEntriesWithNonBooleanDep()
    {
        var path = TempFile("metadata-cache.json");
        File.WriteAllText(
            path,
            "{\"schema\":1,\"entries\":{\"good.app\":{\"dep\":true,\"checkedAt\":\"2026-01-01T00:00:00+08:00\"},"
            + "\"bad.app\":{\"dep\":\"yes\",\"checkedAt\":\"x\"},\"nested.app\":42}}");

        var cache = new RuntimeCache(path, TempLog());

        Assert.True(cache.Get("good.app")); // 好条目留下（单条坏掉不连坐整份缓存）
        Assert.Null(cache.Get("bad.app"));
        Assert.Null(cache.Get("nested.app"));
    }

    [Fact]
    public void Load_ReadsPythonWrittenFile()
    {
        // Python 版 json.dumps(ensure_ascii=False, indent=2) 的产物形态（含中文键不出现，但结构一致）
        var path = TempFile("metadata-cache.json");
        File.WriteAllText(
            path,
            "{\n  \"schema\": 1,\n  \"entries\": {\n    \"microsoft.vstor\": {\n      \"dep\": true,\n"
            + "      \"checkedAt\": \"2026-11-03T14:00:00+08:00\"\n    },\n"
            + "    \"rustlang.rustup\": {\n      \"dep\": false,\n      \"checkedAt\": \"2026-11-03T14:00:01+08:00\"\n    }\n  }\n}\n");

        var cache = new RuntimeCache(path, TempLog());

        Assert.True(cache.Get("Microsoft.VSTOR"));
        Assert.False(cache.Get("Rustlang.Rustup"));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Save_ProducesPythonCompatibleShape()
    {
        var path = TempFile("metadata-cache.json");
        var cache = new RuntimeCache(path, TempLog());
        cache.Put("Microsoft.VSTOR", true);

        var text = File.ReadAllText(path);

        // 顶层 key 顺序与 Python dict 一致：schema 在前、entries 在后；两空格缩进
        Assert.StartsWith("{\n  \"schema\": 1,\n  \"entries\": {", text, StringComparison.Ordinal);
        Assert.Contains("\"dep\": true", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Put_UpdatesExistingEntry()
    {
        var path = TempFile("metadata-cache.json");
        var cache = new RuntimeCache(path, TempLog());

        cache.Put("a.b", false);
        cache.Put("a.b", true);

        Assert.True(cache.Get("a.b"));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Snapshot_ReturnsCopy()
    {
        var cache = new RuntimeCache(TempFile("metadata-cache.json"), TempLog());
        cache.Put("a.b", true);

        var snapshot = cache.Snapshot();

        cache.Put("c.d", true);
        Assert.Single(snapshot);
    }
}
