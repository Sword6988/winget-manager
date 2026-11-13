using System;
using System.IO;

namespace WingetManager.Infra;

/// <summary>
/// 数据目录约定（对应 Python 版 qt_common 的 PERSIST_DIR / LOG_DIR / SELECTION_FILE …）。
/// 路径与 Python 版保持一致，两个版本可共用同一份 selection.json / metadata-cache.json。
///
/// 两种运行形态：
/// <list type="bullet">
///   <item><b>安装版</b>（默认）：数据在 <c>%LOCALAPPDATA%\WingetManager</c>，
///         设置写注册表 —— 与 Python 版完全互通。</item>
///   <item><b>便携版</b>（绿色）：exe 同目录存在 <c>portable.tag</c> 时，数据落在
///         <c>&lt;exe 目录&gt;\data</c>，设置写 <c>data\settings.json</c>，不碰注册表 ——
///         整个文件夹拷走即带走全部状态，删掉即零残留。</item>
/// </list>
/// </summary>
public static class AppPaths
{
    /// <summary>设置用的注册表键（沿用原版键位，PRD Q3 已裁定）。仅安装版使用。</summary>
    public const string RegistryKeyPath = @"Software\Shibeng\WingetManager";

    /// <summary>
    /// 便携模式标记文件：放在 exe 同目录，存在即按便携模式运行。
    /// 用文件而不是探测「exe 同目录可写」：publish 目录被拷到 Program Files 时也可写探测会误判。
    /// </summary>
    public const string PortableMarkerFileName = "portable.tag";

    /// <summary>便携模式的数据子目录名（位于 exe 同目录）。</summary>
    public const string PortableDataFolderName = "data";

    /// <summary>
    /// 显式指定数据根的环境变量，优先级最高（便于同一份程序跑多套隔离配置）。
    /// 便携模式下若要「数据也放到别处」，设它即可。
    /// </summary>
    public const string DataDirectoryEnvVar = "WINGETMGR_DATA_DIR";

    /// <summary>%LOCALAPPDATA%：拿不到时回退到特殊文件夹解析结果。</summary>
    public static string LocalAppData { get; } = ResolveLocalAppData();

    /// <summary>程序所在目录（AOT 下与 exe 同目录；便携模式的数据根以它为准）。</summary>
    public static string InstallDirectory { get; } =
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>便携数据根的探测结果；null 表示按安装版处理。</summary>
    private static readonly string? PortableDataDirectory =
        ProbePortableDataDirectory(InstallDirectory, Environment.GetEnvironmentVariable(DataDirectoryEnvVar));

    /// <summary>是否正在以便携（绿色）模式运行。</summary>
    public static bool IsPortable => PortableDataDirectory is not null;

    /// <summary>数据根：便携模式为 <c>&lt;exe 目录&gt;\data</c>，否则 <c>%LOCALAPPDATA%\WingetManager</c>。</summary>
    public static string DataDirectory { get; } =
        PortableDataDirectory ?? Path.Combine(LocalAppData, "WingetManager");

    /// <summary>日志目录。</summary>
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>日志文件。</summary>
    public static string LogFile { get; } = Path.Combine(LogDirectory, "app.log");

    /// <summary>勾选记录文件。</summary>
    public static string SelectionFile { get; } = Path.Combine(DataDirectory, "selection.json");

    /// <summary>元数据缓存文件。</summary>
    public static string MetadataCacheFile { get; } = Path.Combine(DataDirectory, "metadata-cache.json");

    /// <summary>冷启动秒开用的快照缓存文件。</summary>
    public static string CatalogCacheFile { get; } = Path.Combine(DataDirectory, "catalog-cache.json");

    /// <summary>设置文件（仅便携模式使用；安装版仍写注册表）。</summary>
    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    /// <summary>
    /// 探测便携数据根（纯逻辑，便于单测）。
    ///
    /// 顺序：环境变量显式指定 → <c>portable.tag</c> 标记 → 安装版（null）。
    /// 标记存在但数据目录不可写（只读介质）时返回 null 回退安装版，
    /// 否则用户会在一个每次启动都崩的环境里反复打转。
    /// </summary>
    /// <param name="appDirectory">exe 所在目录。</param>
    /// <param name="explicitDataDirectory">环境变量显式指定的数据根；可为 null。</param>
    /// <returns>便携数据根绝对路径；null 表示按安装版处理。</returns>
    public static string? ProbePortableDataDirectory(string appDirectory, string? explicitDataDirectory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(explicitDataDirectory))
            {
                // 显式指定优先，即使不可写也照用：用户明确要放这里，失败应在日志里看见而不是被静默改到别处
                return Path.GetFullPath(explicitDataDirectory.Trim());
            }

            if (string.IsNullOrWhiteSpace(appDirectory))
            {
                return null;
            }

            var root = Path.GetFullPath(appDirectory);
            var marker = Path.Combine(root, PortableMarkerFileName);
            if (!File.Exists(marker))
            {
                return null;
            }

            var dataDirectory = Path.Combine(root, PortableDataFolderName);
            return TryEnsureWritable(dataDirectory) ? dataDirectory : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>建目录并做一次写入探测（U 盘只读、光盘、权限受限都会在这里暴露）。</summary>
    private static bool TryEnsureWritable(string directory)
    {
        var probe = Path.Combine(directory, ".write-probe");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(probe, Array.Empty<byte>());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 探测残留不影响使用
            }
        }
    }

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
