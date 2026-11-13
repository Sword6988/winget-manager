using System;
using System.IO;

namespace WingetManager.Infra;

/// <summary>
/// 数据目录约定（对应 Python 版 qt_common 的 PERSIST_DIR / LOG_DIR / SELECTION_FILE …）。
/// 路径与 Python 版保持一致，两个版本可共用同一份 selection.json / metadata-cache.json。
/// </summary>
public static class AppPaths
{
    /// <summary>设置用的注册表键（沿用原版键位，PRD Q3 已裁定）。</summary>
    public const string RegistryKeyPath = @"Software\Shibeng\WingetManager";

    /// <summary>%LOCALAPPDATA%：拿不到时回退到特殊文件夹解析结果。</summary>
    public static string LocalAppData { get; } = ResolveLocalAppData();

    /// <summary>%LOCALAPPDATA%\WingetManager\</summary>
    public static string DataDirectory { get; } = Path.Combine(LocalAppData, "WingetManager");

    /// <summary>%LOCALAPPDATA%\WingetManager\logs\</summary>
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>%LOCALAPPDATA%\WingetManager\logs\app.log</summary>
    public static string LogFile { get; } = Path.Combine(LogDirectory, "app.log");

    /// <summary>%LOCALAPPDATA%\WingetManager\selection.json</summary>
    public static string SelectionFile { get; } = Path.Combine(DataDirectory, "selection.json");

    /// <summary>%LOCALAPPDATA%\WingetManager\metadata-cache.json</summary>
    public static string MetadataCacheFile { get; } = Path.Combine(DataDirectory, "metadata-cache.json");

    /// <summary>程序所在目录（诊断信息用；AOT 下与 exe 同目录）。</summary>
    public static string InstallDirectory { get; } =
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ResolveLocalAppData()
    {
        var env = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.TrimEnd(Path.DirectorySeparatorChar);
        }

        var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            return folder.TrimEnd(Path.DirectorySeparatorChar);
        }

        return Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
    }
}
