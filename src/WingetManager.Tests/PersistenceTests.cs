using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WingetManager.Models;
using WingetManager.Persistence;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// 持久化用例：勾选原子写 / 损坏回退、注册表设置的读写与兜底。
/// 测试只碰临时目录与临时注册表键，不动用户的真实数据。
/// </summary>
public sealed class PersistenceTests
{
    private static string TempFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wpm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    // ── SelectionStore ────────────────────────────────────────────────

    [Fact]
    public void SelectionStore_RoundTripsCheckedIds()
    {
        var path = TempFile("selection.json");
        var store = new SelectionStore(path);

        store.Save(new[] { "Google.Chrome", "Git.Git" });
        var loaded = store.Load();

        Assert.Equal(2, loaded.Count);
        Assert.Contains("google.chrome", loaded); // 统一小写
        Assert.Contains("git.git", loaded);
    }

    [Fact]
    public void SelectionStore_WritesBareMapCompatibleWithPython()
    {
        var path = TempFile("selection.json");
        new SelectionStore(path).Save(new[] { "Google.Chrome" });

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.True(document.RootElement.GetProperty("google.chrome").GetBoolean());
    }

    [Fact]
    public void SelectionStore_EmptySaveProducesEmptyObject()
    {
        var path = TempFile("selection.json");
        var store = new SelectionStore(path);

        store.Save(Array.Empty<string>());

        Assert.Empty(store.Load());
        // 空 dict 的 Python 写法就是 "{}\n"（json.dumps({}, indent=2)）
        Assert.Equal("{}\n", File.ReadAllText(path));
    }

    [Fact]
    public void SelectionStore_CorruptJsonFallsBackToEmpty()
    {
        var path = TempFile("selection.json");
        File.WriteAllText(path, "{\"google.chrome\": tr");

        var store = new SelectionStore(path);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void SelectionStore_CoercesLegacyTruthyValues()
    {
        // 老文件里可能存成 "true"/1/"on"：Python bool("false") 是 True，所以这里按白名单判定
        var path = TempFile("selection.json");
        File.WriteAllText(
            path,
            "{\"a.app\": true, \"b.app\": \"true\", \"c.app\": 1, \"d.app\": \"on\","
            + " \"e.app\": false, \"f.app\": \"false\", \"g.app\": 0}");

        var loaded = new SelectionStore(path).Load();

        Assert.Equal(new[] { "a.app", "b.app", "c.app", "d.app" },
                     new List<string>(loaded).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void SelectionStore_OverwritesAtomicallyLeavingNoTempFiles()
    {
        var path = TempFile("selection.json");
        var store = new SelectionStore(path);

        store.Save(new[] { "a.app" });
        store.Save(new[] { "b.app" });

        var directory = Path.GetDirectoryName(path)!;
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        var loaded = store.Load();
        Assert.Single(loaded);
        Assert.Contains("b.app", loaded);
    }

    // ── SettingsStore ─────────────────────────────────────────────────

    private static SettingsStore CreateTempSettings()
    {
        // 用临时子键，绝不污染真实的 Shibeng\WingetManager
        var keyPath = @"Software\Shibeng\WingetManager.Tests\" + Guid.NewGuid().ToString("N");
        var store = new SettingsStore(keyPath);
        store.Clear();
        return store;
    }

    [Fact]
    public void SettingsStore_RoundTripsLastExportPath()
    {
        var store = CreateTempSettings();
        try
        {
            Assert.Equal(string.Empty, store.GetLastExportPath());

            store.SetLastExportPath(@"D:\apps.json");

            Assert.Equal(@"D:\apps.json", store.GetLastExportPath());
        }
        finally
        {
            store.Clear();
        }
    }

    [Fact]
    public void SettingsStore_RoundTripsWindowBounds()
    {
        var store = CreateTempSettings();
        try
        {
            Assert.Equal(WindowBounds.Default, store.GetWindowBounds());

            store.SetWindowBounds(new WindowBounds(10, 20, 1240, 880, true));

            var bounds = store.GetWindowBounds();
            Assert.Equal(10, bounds.X);
            Assert.Equal(20, bounds.Y);
            Assert.Equal(1240, bounds.Width);
            Assert.Equal(880, bounds.Height);
            Assert.True(bounds.IsMaximized);
        }
        finally
        {
            store.Clear();
        }
    }

    [Fact]
    public void SettingsStore_RoundTripsCategoryAndLogExpanded()
    {
        var store = CreateTempSettings();
        try
        {
            store.SetLastCategory("运行库");
            store.SetLogExpanded(false);

            Assert.Equal("运行库", store.GetLastCategory());
            Assert.False(store.GetLogExpanded());
        }
        finally
        {
            store.Clear();
        }
    }

    [Fact]
    public void SettingsStore_UsesOriginalKeyPathByDefault()
    {
        var store = new SettingsStore();

        Assert.Equal(@"Software\Shibeng\WingetManager", store.KeyPath);
        Assert.Equal(WingetManager.Infra.AppPaths.RegistryKeyPath, store.KeyPath);
    }

    [Fact]
    public void SettingsStore_MissingValuesFallBackToDefaults()
    {
        var store = CreateTempSettings();
        try
        {
            Assert.Equal(string.Empty, store.GetLastExportPath());
            Assert.Equal(string.Empty, store.GetLastCategory());
            Assert.True(store.GetLogExpanded());
            Assert.Equal(WindowBounds.Default, store.GetWindowBounds());
        }
        finally
        {
            store.Clear();
        }
    }

    [Fact]
    public void RegistryKeyPath_MatchesPythonVersion()
    {
        // 与 Python 版 QSettings("Shibeng", "WingetManager") 等价，两版本可互读
        Assert.Equal(@"Software\Shibeng\WingetManager", WingetManager.Infra.AppPaths.RegistryKeyPath);
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Shibeng");
        // 允许不存在（干净机器），存在则必须是 HKCU 下的同名键
        Assert.True(key is null || key.Name.Contains("Shibeng", StringComparison.OrdinalIgnoreCase));
    }
}
