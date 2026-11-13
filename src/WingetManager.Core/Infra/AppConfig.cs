using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace WingetManager.Infra;

/// <summary>
/// 集中配置：所有可变项来自环境变量，启动时一次性校验（对应 <c>core/config.py</c>）。
///
/// 原则：<b>缺 winget 不是致命错误</b>。装到 Program Files 的机器上也可能没有 App
/// Installer，程序要能起来并把用户引导到安装页，而不是弹个框就退出。
/// </summary>
public sealed class AppConfig
{
    /// <summary>环境变量：指定 winget.exe 路径。</summary>
    public const string EnvWingetExe = "WPM_WINGET_EXE";

    /// <summary>环境变量：目录快照缓存 TTL（秒），默认 60，允许 0-3600（0 = 每次重扫）。</summary>
    public const string EnvCacheTtl = "WPM_CACHE_TTL";

    /// <summary>环境变量：单条 winget 命令超时（秒），默认 180，允许 10-3600。</summary>
    public const string EnvCommandTimeout = "WPM_COMMAND_TIMEOUT";

    /// <summary>默认缓存 TTL（秒）。</summary>
    public const int DefaultCacheTtlSeconds = 60;

    /// <summary>默认命令超时（秒）。</summary>
    public const int DefaultCommandTimeoutSeconds = 180;

    private readonly Dictionary<string, string> _extra;

    private AppConfig(string wingetExe, int cacheTtlSeconds, int commandTimeoutSeconds,
                      string? wingetError, Dictionary<string, string> extra)
    {
        WingetExe = wingetExe;
        CacheTtlSeconds = cacheTtlSeconds;
        CommandTimeoutSeconds = commandTimeoutSeconds;
        WingetError = wingetError;
        _extra = extra;
    }

    /// <summary>winget.exe 的完整路径；空串表示没探测到（此时看 <see cref="WingetError"/>）。</summary>
    public string WingetExe { get; }

    /// <summary>目录快照缓存有效期（秒）。0 表示每次扫描都重新跑 winget。</summary>
    public int CacheTtlSeconds { get; }

    /// <summary>单条 winget 命令的总超时（秒，含互斥重试退避）。</summary>
    public int CommandTimeoutSeconds { get; }

    /// <summary>winget 探测失败原因；null 表示探测成功。</summary>
    public string? WingetError { get; }

    /// <summary>是否有可用的 winget 可执行文件。</summary>
    public bool HasWinget => !string.IsNullOrEmpty(WingetExe);

    /// <summary>附加信息（诊断用，对应 Python 版 config.extra）。</summary>
    public IReadOnlyDictionary<string, string> Extra => _extra;

    /// <summary>
    /// 读环境变量。<b>winget 探测失败只记录原因，不让启动失败</b>（对应 load_config）。
    /// </summary>
    /// <exception cref="ConfigInvalidException">WPM_CACHE_TTL / WPM_COMMAND_TIMEOUT 非法。</exception>
    public static AppConfig Load()
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dotnet"] = Environment.Version.ToString(),
            ["base_dir"] = AppPaths.InstallDirectory,
        };

        string wingetExe;
        string? wingetError = null;
        try
        {
            wingetExe = FindWinget();
        }
        catch (ConfigInvalidException ex)
        {
            // 非致命：主窗口照常起来，VM 把 WingetError 转成 missing 信号，给「去商店安装」引导。
            wingetExe = string.Empty;
            wingetError = ex.Message;
            extra["winget_error"] = ex.Message;
        }

        return new AppConfig(
            wingetExe,
            ReadEnvInt(EnvCacheTtl, DefaultCacheTtlSeconds, 0, 3600),
            ReadEnvInt(EnvCommandTimeout, DefaultCommandTimeoutSeconds, 10, 3600),
            wingetError,
            extra);
    }

    /// <summary>
    /// 构造一份测试用配置（不探测 winget）。
    /// </summary>
    public static AppConfig CreateForTests(string wingetExe = "", int cacheTtlSeconds = DefaultCacheTtlSeconds,
                                           int commandTimeoutSeconds = DefaultCommandTimeoutSeconds,
                                           string? wingetError = null)
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        if (wingetError is not null)
        {
            extra["winget_error"] = wingetError;
        }

        return new AppConfig(wingetExe, cacheTtlSeconds, commandTimeoutSeconds, wingetError, extra);
    }

    /// <summary>
    /// 定位 winget.exe；找不到说明系统缺少 App Installer。
    /// 顺序：WPM_WINGET_EXE（显式指定，指向不存在文件要报错）→ PATH → WindowsApps 默认位置。
    /// </summary>
    /// <exception cref="ConfigInvalidException">三个位置都没有。</exception>
    public static string FindWinget()
    {
        var overridePath = Environment.GetEnvironmentVariable(EnvWingetExe)?.Trim() ?? string.Empty;
        if (overridePath.Length > 0)
        {
            if (!File.Exists(overridePath))
            {
                throw new ConfigInvalidException($"{EnvWingetExe} 指向的文件不存在: {overridePath}");
            }

            return overridePath;
        }

        var onPath = FindOnPath("winget.exe");
        if (!string.IsNullOrEmpty(onPath))
        {
            return onPath;
        }

        var candidate = Path.Combine(AppPaths.LocalAppData, "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new ConfigInvalidException(
            "未找到 winget.exe。请先安装「应用安装程序 / App Installer」"
            + "（https://aka.ms/getwinget），或通过 WPM_WINGET_EXE 指定路径。");
    }

    /// <summary>在 PATH 各目录里找可执行文件（等价 shutil.which）。</summary>
    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separators = new[] { Path.PathSeparator };
        foreach (var raw in path.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0)
            {
                continue;
            }

            try
            {
                var full = Path.Combine(dir, fileName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch (ArgumentException)
            {
                // PATH 里可能有非法字符的目录项，跳过即可（等价 shutil.which 的容错）
            }
            catch (NotSupportedException)
            {
            }
        }

        return null;
    }

    private static int ReadEnvInt(string name, int defaultValue, int minimum, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(name)?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ConfigInvalidException($"环境变量 {name} 必须是整数，当前为 '{raw}'");
        }

        if (value < minimum || value > maximum)
        {
            throw new ConfigInvalidException($"环境变量 {name} 必须在 {minimum}~{maximum} 之间，当前为 {value}");
        }

        return value;
    }
}
