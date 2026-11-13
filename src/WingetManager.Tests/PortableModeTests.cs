using System;
using System.IO;
using System.Text.Json;
using WingetManager.Infra;
using WingetManager.Persistence;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// 便携（绿色）模式用例：数据根探测 + 设置的文件后端。
///
/// 要点：绿色版必须「删掉文件夹即零残留」，所以
/// <list type="bullet">
///   <item>数据根跟着 exe 走（<c>&lt;exe 目录&gt;\data</c>），</item>
///   <item>设置不能写注册表，改存 settings.json。</item>
/// </list>
/// 测试只碰临时目录，不写真实注册表也不碰用户数据。
/// </summary>
public sealed class PortableModeTests
{
    private static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wpm-portable", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LogService TempLog(string directory) =>
        new(Path.Combine(directory, "test.log"));

    // ── 数据根探测 ───────────────────────────────────────────────────

    [Fact]
    public void Probe_NoMarker_ReturnsNull()
    {
        var dir = TempDirectory();

        Assert.Null(AppPaths.ProbePortableDataDirectory(dir, explicitDataDirectory: null));
    }

    [Fact]
    public void Probe_WithMarker_ReturnsDataSubfolderAndCreatesIt()
    {
        var dir = TempDirectory();
        File.WriteAllText(Path.Combine(dir, AppPaths.PortableMarkerFileName), "portable");

        var data = AppPaths.ProbePortableDataDirectory(dir, explicitDataDirectory: null);

        Assert.NotNull(data);
        Assert.Equal(Path.Combine(dir, AppPaths.PortableDataFolderName), data);
        Assert.True(Directory.Exists(data));
        // 写入探测文件必须清理干净，否则用户会看到一个莫名奇妙的 .write-probe
        Assert.False(File.Exists(Path.Combine(data!, ".write-probe")));
    }

    [Fact]
    public void Probe_ExplicitOverrideWinsWithoutMarker()
    {
        var dir = TempDirectory();
        var explicitDir = Path.Combine(dir, "elsewhere", "wm-data");

        var data = AppPaths.ProbePortableDataDirectory(dir, explicitDir);

        Assert.NotNull(data);
        Assert.Equal(Path.GetFullPath(explicitDir), data);
    }

    [Fact]
    public void Probe_ExplicitOverrideWinsOverMarker()
    {
        var dir = TempDirectory();
        File.WriteAllText(Path.Combine(dir, AppPaths.PortableMarkerFileName), "portable");
        var explicitDir = Path.Combine(dir, "custom-data");

        var data = AppPaths.ProbePortableDataDirectory(dir, explicitDir);

        Assert.Equal(Path.GetFullPath(explicitDir), data);
    }

    [Fact]
    public void Probe_BlankArguments_ReturnNull()
    {
        Assert.Null(AppPaths.ProbePortableDataDirectory(string.Empty, null));
        Assert.Null(AppPaths.ProbePortableDataDirectory("   ", null));
        Assert.Null(AppPaths.ProbePortableDataDirectory(TempDirectory(), "   "));
    }

    [Fact]
    public void Probe_RelativeAppDirectory_IsMadeAbsolute()
    {
        var dir = TempDirectory();
        File.WriteAllText(Path.Combine(dir, AppPaths.PortableMarkerFileName), "portable");

        var data = AppPaths.ProbePortableDataDirectory(Path.Combine(dir, "."), null);

        Assert.NotNull(data);
        Assert.True(Path.IsPathRooted(data));
        Assert.Equal(Path.Combine(Path.GetFullPath(dir), AppPaths.PortableDataFolderName), data);
    }

    // ── 派生路径 ─────────────────────────────────────────────────────

    [Fact]
    public void DerivedPaths_AllSitUnderDataDirectory()
    {
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "settings.json"), AppPaths.SettingsFile);
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "selection.json"), AppPaths.SelectionFile);
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "logs", "app.log"), AppPaths.LogFile);
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "catalog-cache.json"), AppPaths.CatalogCacheFile);
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "metadata-cache.json"), AppPaths.MetadataCacheFile);
    }

    [Fact]
    public void Probe_DefaultProcessIsNotPortable()
    {
        // 测试宿主目录没有 portable.tag，除非跑测试的人手动放了（那就说明环境有问题）
        Assert.False(AppPaths.IsPortable);
        Assert.Equal(Path.Combine(AppPaths.LocalAppData, "WingetManager"), AppPaths.DataDirectory);
    }

    // ── 设置的文件后端 ───────────────────────────────────────────────

    [Fact]
    public void FileBackend_RoundTripsAllSettings()
    {
        var dir = TempDirectory();
        var path = Path.Combine(dir, "settings.json");
        var store = SettingsStore.ForFile(path, TempLog(dir));

        store.SetWindowBounds(new WindowBounds(10, 20, 1280, 900, true));
        store.SetLastCategory("开发工具");
        store.SetLastExportPath(@"D:\导出\清单.txt");
        store.SetLogExpanded(false);

        var reopened = SettingsStore.ForFile(path, TempLog(dir));

        Assert.Equal(new WindowBounds(10, 20, 1280, 900, true), reopened.GetWindowBounds());
        Assert.Equal("开发工具", reopened.GetLastCategory());
        Assert.Equal(@"D:\导出\清单.txt", reopened.GetLastExportPath());
        Assert.False(reopened.GetLogExpanded());
    }

    [Fact]
    public void FileBackend_MissingFile_ReturnsDefaults()
    {
        var dir = TempDirectory();
        var store = SettingsStore.ForFile(Path.Combine(dir, "settings.json"), TempLog(dir));

        Assert.Equal(WindowBounds.Default, store.GetWindowBounds());
        Assert.Equal(string.Empty, store.GetLastCategory());
        Assert.Equal(string.Empty, store.GetLastExportPath());
        Assert.True(store.GetLogExpanded()); // 默认展开
    }

    [Fact]
    public void FileBackend_WritesFlatJsonObject()
    {
        var dir = TempDirectory();
        var path = Path.Combine(dir, "settings.json");
        SettingsStore.ForFile(path, TempLog(dir)).SetLastCategory("全部");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal("全部", document.RootElement.GetProperty("view/category").GetString());
    }

    [Fact]
    public void FileBackend_CorruptFile_FallsBackToDefaultsWithoutThrowing()
    {
        var dir = TempDirectory();
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");

        var store = SettingsStore.ForFile(path, TempLog(dir));

        Assert.Equal(WindowBounds.Default, store.GetWindowBounds());
        Assert.Equal(string.Empty, store.GetLastCategory());
    }

    [Fact]
    public void FileBackend_JsonNullRoot_FallsBackToDefaults()
    {
        var dir = TempDirectory();
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "null");

        Assert.Equal(string.Empty, SettingsStore.ForFile(path, TempLog(dir)).GetLastCategory());
    }

    [Fact]
    public void FileBackend_ClearRemovesFileAndResetsValues()
    {
        var dir = TempDirectory();
        var path = Path.Combine(dir, "settings.json");
        var store = SettingsStore.ForFile(path, TempLog(dir));
        store.SetLastCategory("系统工具");
        Assert.True(File.Exists(path));

        store.Clear();

        Assert.False(File.Exists(path));
        Assert.Equal(string.Empty, store.GetLastCategory());
    }

    [Fact]
    public void FileBackend_MarksItselfAndKeepsDefaultKeyPath()
    {
        var dir = TempDirectory();
        var store = SettingsStore.ForFile(Path.Combine(dir, "settings.json"), TempLog(dir));

        Assert.True(store.IsFileBacked);
        Assert.NotNull(store.FilePath);
        Assert.True(Path.IsPathRooted(store.FilePath));
        Assert.Equal(SettingsStore.DefaultKeyPath, store.KeyPath);
    }

    [Fact]
    public void RegistryBackend_IsNotFileBacked()
    {
        var store = new SettingsStore(@"Software\Shibeng\WingetManager.Tests.Noop");

        Assert.False(store.IsFileBacked);
        Assert.Null(store.FilePath);
        Assert.Equal(@"Software\Shibeng\WingetManager.Tests.Noop", store.KeyPath);
    }

    [Fact]
    public void ForFile_RejectsBlankPath()
    {
        Assert.ThrowsAny<ArgumentException>(() => SettingsStore.ForFile("   "));
    }
}
