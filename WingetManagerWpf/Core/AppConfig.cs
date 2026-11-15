using System;
using System.Globalization;
using System.IO;

namespace WingetManagerWpf.Core
{
    /// <summary>
    /// 启动配置：全部可变项来自环境变量，启动时一次性校验。
    /// 设计原则（与原版 core/config.py 一致）：**缺 winget 不是致命错误**。
    /// 装在受限环境的机器上可能没有 App Installer，程序必须能起来并给出安装引导，
    /// 而不是弹个框就退出 —— 所以探测失败只填 <see cref="WingetError"/>，不抛异常。
    /// </summary>
    public sealed class AppConfig
    {
        public const int DefaultCommandTimeout = 180;
        public const int DefaultCacheTtlSeconds = 60;

        /// <summary>
        /// 元数据判定缓存的默认有效期（天）。<c>Services/RuntimeCache.DefaultTtlDays</c> **直接复用本常量**
        /// （依赖方向单向：Core 被 Services 引用，Core 不能反过来引 Services），要改默认值只改这里。
        /// </summary>
        public const int DefaultMetadataTtlDays = 90;

        private const int CommandTimeoutMin = 10;
        private const int CommandTimeoutMax = 3600;
        private const int CacheTtlMin = 0;
        private const int CacheTtlMax = 3600;
        private const int MetadataTtlDaysMin = 0;
        private const int MetadataTtlDaysMax = 3650;

        /// <summary>构造配置；<paramref name="metadataTtlDays"/> 可省略（默认 90 天）。</summary>
        /// <param name="metadataTtlDays">
        /// 元数据判定缓存的存活天数，默认 90（= <see cref="DefaultMetadataTtlDays"/>）。
        /// 声明成可选参数是为了让既有四参调用点（含自测）无需改动即可继续编译。
        /// </param>
        public AppConfig(string wingetExe, string wingetError, int commandTimeout, int cacheTtlSeconds,
                         int metadataTtlDays = DefaultMetadataTtlDays)
        {
            WingetExe = wingetExe ?? string.Empty;
            WingetError = wingetError ?? string.Empty;
            CommandTimeout = commandTimeout;
            CacheTtlSeconds = cacheTtlSeconds;
            MetadataTtlDays = metadataTtlDays;
        }

        /// <summary>winget.exe 的完整路径；空串表示未找到（此时 <see cref="WingetError"/> 有原因）。</summary>
        public string WingetExe { get; private set; }

        /// <summary>未找到 winget 时的可读原因（给引导框用）。</summary>
        public string WingetError { get; private set; }

        /// <summary>单条命令的总超时（秒，含重试退避），10~3600，默认 180。</summary>
        public int CommandTimeout { get; private set; }

        /// <summary>目录内存缓存的存活秒数，0~3600，默认 60；0 = 每次都重扫。</summary>
        public int CacheTtlSeconds { get; private set; }

        /// <summary>
        /// 元数据判定缓存（<c>metadata-cache.json</c>）的存活天数，0~3650，默认 90；0 = 永不过期。
        /// <para>
        /// 为什么需要过期：<c>checkedAt</c> 以前只写不读，一条「是软件 / 是运行库」的结论永久有效，
        /// 上游 winget 元数据更新后旧结论永远纠不回来。默认 90 天是在「结论不至于过时」与
        /// 「别每次都把几百个包重新 <c>winget show</c> 一遍」之间的折中。
        /// </para>
        /// </summary>
        public int MetadataTtlDays { get; private set; }

        /// <summary>读取环境变量并探测 winget；只有环境变量非法才抛 <see cref="ConfigInvalidException"/>。</summary>
        public static AppConfig Load()
        {
            int commandTimeout = ReadEnvInt("WPM_COMMAND_TIMEOUT", DefaultCommandTimeout, CommandTimeoutMin, CommandTimeoutMax);
            int cacheTtl = ReadEnvInt("WPM_CACHE_TTL", DefaultCacheTtlSeconds, CacheTtlMin, CacheTtlMax);
            int metadataTtlDays = ReadEnvInt("WPM_META_TTL_DAYS", DefaultMetadataTtlDays,
                                            MetadataTtlDaysMin, MetadataTtlDaysMax);

            string error;
            string exe = FindWinget(out error);
            return new AppConfig(exe, error, commandTimeout, cacheTtl, metadataTtlDays);
        }

        /// <summary>全默认配置（环境变量非法时兜底用，保证程序能起来）。</summary>
        public static AppConfig CreateDefault()
        {
            string error;
            string exe = FindWinget(out error);
            return new AppConfig(exe, error, DefaultCommandTimeout, DefaultCacheTtlSeconds,
                                 DefaultMetadataTtlDays);
        }

        /// <summary>
        /// 定位 winget.exe：WPM_WINGET_EXE → PATH → %LOCALAPPDATA%\Microsoft\WindowsApps。
        /// 直接查文件而不起 <c>where</c> 子进程：起子进程本身就会撞 winget 的全局互斥，
        /// 而且失败路径更难诊断。
        /// </summary>
        public static string FindWinget(out string error)
        {
            error = string.Empty;

            string overridePath = (Environment.GetEnvironmentVariable("WPM_WINGET_EXE") ?? string.Empty).Trim();
            if (overridePath.Length > 0)
            {
                if (File.Exists(overridePath))
                {
                    return overridePath;
                }

                error = "WPM_WINGET_EXE 指向的文件不存在: " + overridePath;
                return string.Empty;
            }

            string fromPath = SearchOnPath("winget.exe");
            if (!string.IsNullOrEmpty(fromPath))
            {
                return fromPath;
            }

            string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(localAppData))
            {
                localAppData = AppPaths.LocalAppDataRoot;
            }

            if (!string.IsNullOrEmpty(localAppData))
            {
                string alias = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
                if (File.Exists(alias))
                {
                    return alias;
                }
            }

            error = "未找到 winget.exe。请先安装「应用安装程序 / App Installer」"
                    + "（https://aka.ms/getwinget），或通过 WPM_WINGET_EXE 指定路径。";
            return string.Empty;
        }

        private static string SearchOnPath(string fileName)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            string[] parts = path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string dir = parts[i].Trim().Trim('"');
                if (dir.Length == 0)
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // PATH 里混进非法路径（含未转义引号等）时跳过该项即可
                }
            }

            return string.Empty;
        }

        private static int ReadEnvInt(string name, int fallback, int minimum, int maximum)
        {
            string raw = (Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                return fallback;
            }

            int value;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                throw new ConfigInvalidException(
                    "环境变量 " + name + " 必须是整数，当前为 '" + raw + "'");
            }

            if (value < minimum || value > maximum)
            {
                throw new ConfigInvalidException(
                    "环境变量 " + name + " 必须在 " + minimum + "~" + maximum
                    + " 之间，当前为 " + value.ToString(CultureInfo.InvariantCulture));
            }

            return value;
        }
    }
}
