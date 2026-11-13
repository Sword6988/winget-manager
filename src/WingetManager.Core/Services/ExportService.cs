using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WingetManager.Infra;
using WingetManager.Json;
using WingetManager.Models;

namespace WingetManager.Services;

/// <summary>
/// 一次导出的结果。
/// </summary>
/// <param name="Files">实际写出的文件（按写入顺序）。</param>
/// <param name="Manifest">apps.json 的内容对象。</param>
/// <param name="WingetCount">清单里来自 winget 源的包数。</param>
/// <param name="MsStoreCount">清单里来自 msstore 源的包数。</param>
/// <param name="Warnings">非致命问题（如恢复脚本里的文件名无法 GBK 编码）。</param>
/// <param name="RestoreScriptPath">恢复脚本路径（失败时为 null）。</param>
/// <param name="UnreinstallableListPath">无法自动重装清单路径（没有这类包时为 null）。</param>
public sealed record ExportResult(
    IReadOnlyList<string> Files,
    WingetImportManifest Manifest,
    int WingetCount,
    int MsStoreCount,
    IReadOnlyList<string> Warnings,
    string? RestoreScriptPath,
    string? UnreinstallableListPath);

/// <summary>
/// 三件套导出（对应 <c>services/profiles.py</c>）：
/// <list type="number">
/// <item><c>apps.json</c>：winget 官方 import schema 2.0，按源分组、版本归一化；</item>
/// <item><c>恢复-&lt;stem&gt;.bat</c>：GBK 写盘 + <c>chcp 936</c> + 管理员检查 + <c>--ignore-unavailable</c>；</item>
/// <item><c>无法自动重装清单-&lt;stem&gt;.txt</c>：按名称平铺 + 导出时间戳（UTF-8 BOM）。</item>
/// </list>
/// 只做「导出」这一件事：导入/恢复走 winget 自己的 <c>winget import</c>。
/// </summary>
public sealed partial class ExportService
{
    /// <summary>默认清单文件名。</summary>
    public const string DefaultManifestFileName = "apps.json";

    /// <summary>恢复脚本名前缀。</summary>
    public const string RestoreScriptPrefix = "恢复-";

    /// <summary>无法自动重装清单名前缀。</summary>
    public const string UnreinstallableListPrefix = "无法自动重装清单-";

    /// <summary>D 盘默认导出位置（PRD P0-17 的路径链第二跳）。</summary>
    public const string PreferredDriveRoot = @"D:\";

    private readonly LogService _log;

    /// <summary>创建导出服务。</summary>
    /// <param name="log">日志服务；null 用共享实例。</param>
    public ExportService(LogService? log = null)
    {
        _log = log ?? LogService.Shared;
    }

    /// <summary>winget 版本号归一化用的正则（对应 Python <c>_VERSION_RE</c>）。</summary>
    [GeneratedRegex(@"^v?(\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.\-]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    /// <summary>
    /// winget 的 import schema 对 <c>WinGetVersion</c> 有正则约束：必须是数字开头的版本号。
    /// 解析不出时回退 <c>1.0.0</c>。
    /// </summary>
    /// <param name="raw">原始版本串（可能带前导 v / preview 后缀）。</param>
    /// <returns>归一化版本号。</returns>
    public static string NormalizeWingetVersion(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        var match = VersionRegex().Match(text);
        return match.Success ? match.Groups[1].Value : "1.0.0";
    }

    /// <summary>
    /// 标准 winget import 格式：可直接 <c>winget import -i file.json</c>。
    ///
    /// 只纳入可重装包；源不在 {winget, msstore} 的包按 winget 源归组（与 Python 版一致）；
    /// <c>Sources</c> 按源名排序，保证同一输入产出稳定文件。
    /// </summary>
    /// <param name="selected">勾选的包（不可重装的会被跳过）。</param>
    /// <param name="wingetVersion">winget 版本。</param>
    /// <returns>清单对象。</returns>
    public WingetImportManifest BuildManifest(IEnumerable<Package> selected, string? wingetVersion)
    {
        ArgumentNullException.ThrowIfNull(selected);

        var bySource = new SortedDictionary<string, List<Package>>(StringComparer.Ordinal);
        foreach (var package in selected)
        {
            if (package is null || !package.Reinstallable)
            {
                continue;
            }

            var source = package.Source is "winget" or "msstore" ? package.Source : "winget";
            if (!bySource.TryGetValue(source, out var items))
            {
                items = new List<Package>();
                bySource[source] = items;
            }

            items.Add(package);
        }

        var manifest = new WingetImportManifest
        {
            Schema = WingetImportManifest.SchemaUrl,
            CreationDate = FormatCreationDate(DateTimeOffset.Now),
            WingetVersion = NormalizeWingetVersion(wingetVersion),
            Sources = new List<WingetImportSource>(bySource.Count),
        };

        foreach (var pair in bySource)
        {
            var source = pair.Key;
            var sourceInfo = new WingetImportSource
            {
                Packages = pair.Value
                    .Select(p => new WingetImportPackage { PackageIdentifier = p.PackageIdentifier })
                    .ToList(),
                SourceDetails = CreateSourceDetails(source),
            };
            manifest.Sources.Add(sourceInfo);
        }

        return manifest;
    }

    /// <summary>按来源统计可重装包（winget / msstore），用于导出后的提示。</summary>
    /// <param name="selected">勾选的包。</param>
    /// <returns>固定含 winget / msstore 两个键的计数。</returns>
    public IReadOnlyDictionary<string, int> SourceBreakdown(IEnumerable<Package> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["winget"] = 0, ["msstore"] = 0 };
        foreach (var package in selected)
        {
            if (package is null || !package.Reinstallable)
            {
                continue;
            }

            var key = package.Source is "winget" or "msstore" ? package.Source : "winget";
            counts[key]++;
        }

        return counts;
    }

    /// <summary>
    /// 生成「双击即恢复」的批处理文本（放在清单同目录，重装系统后直接跑）。
    ///
    /// cmd 默认代码页是 936，所以调用方要用 GBK 写盘，否则中文提示会乱码。
    /// <c>--ignore-unavailable</c>：源里已下架/改名的包会被跳过而不是中断整批。
    ///
    /// 安全要点：清单名是用户可控的（导出时的文件名），直接拼进 cmd 会被 <c>&amp;</c>、
    /// <c>|</c>、<c>%VAR%</c> 解释成命令。这里只取 basename 防目录穿越、<c>%</c> 双写防变量
    /// 展开，并在 echo 里用引号隔离。
    /// </summary>
    /// <param name="jsonName">清单文件名（可含目录，导出时只取 basename）。</param>
    /// <param name="count">清单里的包数。</param>
    /// <param name="msStoreCount">其中来自 msstore 的包数。</param>
    /// <returns>批处理文本（CRLF）。</returns>
    /// <exception cref="ExportInvalidException">文件名含 GBK 无法表示的字符。</exception>
    public static string BuildRestoreBat(string jsonName, int count, int msStoreCount = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(jsonName);

        var name = Path.GetFileName(jsonName).Replace("%", "%%", StringComparison.Ordinal);
        var text = RenderRestoreBat(name, count, msStoreCount);

        try
        {
            // cmd 代码页 936，编不进去的字符写盘时会炸——提前给出可操作的提示
            _ = TextEncoding.GbkStrict.GetByteCount(text);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ExportInvalidException(
                $"清单文件名无法用 GBK 编码: {name}（{ex.Message}）",
                "文件名里有 emoji 等 GBK 无法表示的字符，请改用中文/英文/数字后重试。");
        }

        return text;
    }

    /// <summary>
    /// 无法自动重装的软件清单（纯文本，重装系统后照着手动装）。
    ///
    /// 这些包全部属于同一个分组（winget 不支持），所以按名称平铺即可，不再按分类切小节。
    /// </summary>
    /// <param name="packages">全量包（内部筛出不可重装的）。</param>
    /// <returns>文本（CRLF）。</returns>
    public static string BuildUnreinstallableList(IEnumerable<Package> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);

        var items = packages.Where(p => p is not null && !p.Reinstallable).ToList();
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        var head = new List<string>
        {
            "Winget 软件管家 · 无法自动重装清单",
            $"导出时间：{stamp}",
            string.Empty,
            $"共 {items.Count} 项。这些软件没有可用的 winget 安装源（winget 不支持），",
            "重装系统后需要手动下载安装（本清单仅供核对，不会被恢复脚本处理）。",
            string.Empty,
        };

        if (items.Count == 0)
        {
            head.Add("（没有这类软件，全部都能自动重装）");
            return string.Join("\r\n", head) + "\r\n";
        }

        var body = new List<string>();
        foreach (var package in items
                     .OrderBy(p => p.Name.ToLowerInvariant(), StringComparer.Ordinal)
                     .ThenBy(p => p.PackageIdentifier.ToLowerInvariant(), StringComparer.Ordinal))
        {
            var version = package.Version.Length > 0 ? "  v" + package.Version : string.Empty;
            var source = package.Source.Length > 0 ? "  [" + package.Source + "]" : string.Empty;
            body.Add($"  · {package.Name}{version}");
            body.Add($"    {package.PackageIdentifier}{source}");
        }

        head.AddRange(body);
        return string.Join("\r\n", head) + "\r\n";
    }

    /// <summary>
    /// 导出三件套到 <paramref name="targetJsonPath"/> 所在目录。
    ///
    /// 恢复脚本 / 清单 txt 的失败不影响 apps.json（与 Python 版一致：只记日志与警告）；
    /// 但 GBK 编码问题会作为 <see cref="ExportResult.Warnings"/> 明确返回给界面。
    /// </summary>
    /// <param name="targetJsonPath">清单文件完整路径（扩展名会被规范成 .json）。</param>
    /// <param name="selected">勾选的包。</param>
    /// <param name="allPackages">本次扫描的全量包（用于生成「无法自动重装清单」）。</param>
    /// <param name="wingetVersion">winget 版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导出结果。</returns>
    public async Task<ExportResult> ExportAsync(string targetJsonPath, IReadOnlyList<Package> selected,
                                               IReadOnlyList<Package> allPackages, string? wingetVersion,
                                               CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetJsonPath);
        ArgumentNullException.ThrowIfNull(selected);

        var jsonPath = EnsureJsonExtension(targetJsonPath);
        var directory = Path.GetDirectoryName(jsonPath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        var stem = Path.GetFileNameWithoutExtension(jsonPath);
        var manifest = BuildManifest(selected, wingetVersion);
        var files = new List<string>();
        var warnings = new List<string>();

        // 1) apps.json —— 原子写；失败必须抛出去（这是导出的核心产物）
        await Task.Run(
            () => AtomicFile.WriteJson(jsonPath, manifest, AppJsonContext.Compat.WingetImportManifest),
            cancellationToken).ConfigureAwait(false);
        files.Add(jsonPath);

        var breakdown = SourceBreakdown(selected);
        var wingetCount = breakdown["winget"];
        var msStoreCount = breakdown["msstore"];

        // 2) 恢复脚本 —— 同目录放一份双击即恢复的 bat（GBK 写盘，cmd 代码页 936）
        string? restorePath = null;
        try
        {
            var batPath = Path.Combine(directory, RestoreScriptPrefix + stem + ".bat");
            var batText = BuildRestoreBat(Path.GetFileName(jsonPath), wingetCount + msStoreCount, msStoreCount);
            await Task.Run(
                () => AtomicFile.WriteText(batPath, batText, TextEncoding.Gbk),
                cancellationToken).ConfigureAwait(false);
            restorePath = batPath;
            files.Add(batPath);
        }
        catch (ExportInvalidException ex)
        {
            _log.Error("恢复脚本生成失败（清单已导出）：{0}", ex.UserMessage);
            warnings.Add(ex.UserMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("恢复脚本生成失败（清单已导出）：{0}", ex.Message);
            warnings.Add("恢复脚本生成失败：" + ex.Message);
        }

        // 3) 无法自动重装清单 —— 界面关掉后这些项就再也查不到了
        string? unreinstallablePath = null;
        try
        {
            var all = allPackages ?? Array.Empty<Package>();
            if (all.Any(p => p is not null && !p.Reinstallable))
            {
                var txtPath = Path.Combine(directory, UnreinstallableListPrefix + stem + ".txt");
                var txt = BuildUnreinstallableList(all);
                await Task.Run(
                    () => AtomicFile.WriteText(txtPath, txt, TextEncoding.Utf8Bom),
                    cancellationToken).ConfigureAwait(false);
                unreinstallablePath = txtPath;
                files.Add(txtPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("无法重装清单生成失败：{0}", ex.Message);
            warnings.Add("无法自动重装清单生成失败：" + ex.Message);
        }

        _log.Info("已导出 {0} 个软件包到 {1}", selected.Count, jsonPath);
        return new ExportResult(files, manifest, wingetCount, msStoreCount, warnings, restorePath, unreinstallablePath);
    }

    /// <summary>
    /// 导出默认路径链（对应 Python <c>default_export_path</c>）：
    /// 上次使用路径 → <c>D:\apps.json</c>（D 盘存在时）→ <c>文档\apps.json</c>。
    /// </summary>
    /// <param name="lastUsedPath">上次导出路径（可为空）。</param>
    /// <returns>建议的清单路径。</returns>
    public static string ResolveDefaultExportPath(string? lastUsedPath)
    {
        if (!string.IsNullOrWhiteSpace(lastUsedPath))
        {
            try
            {
                var parent = Path.GetDirectoryName(lastUsedPath);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    return lastUsedPath;
                }
            }
            catch (ArgumentException)
            {
                // 路径非法就当没记录
            }
        }

        var preferred = Path.Combine(PreferredDriveRoot, DefaultManifestFileName);
        if (DriveExists(PreferredDriveRoot))
        {
            return preferred;
        }

        var documents = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        var baseDirectory = Directory.Exists(documents)
            ? documents
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(baseDirectory, DefaultManifestFileName);
    }

    private static bool DriveExists(string root)
    {
        try
        {
            return Directory.Exists(root);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string EnsureJsonExtension(string path)
    {
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : path + ".json";
    }

    /// <summary>对应 Python <c>datetime.now().astimezone().isoformat(timespec="milliseconds")</c>。</summary>
    private static string FormatCreationDate(DateTimeOffset value)
    {
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
    }

    private static WingetSourceDetails CreateSourceDetails(string source)
    {
        return source switch
        {
            "msstore" => new WingetSourceDetails
            {
                Argument = WingetImportManifest.MsStoreSourceArgument,
                Identifier = WingetImportManifest.MsStoreSourceIdentifier,
                Name = WingetImportManifest.MsStoreSourceName,
                Type = WingetImportManifest.MsStoreSourceType,
            },
            _ => new WingetSourceDetails
            {
                Argument = WingetImportManifest.WingetSourceArgument,
                Identifier = WingetImportManifest.WingetSourceIdentifier,
                Name = WingetImportManifest.WingetSourceName,
                Type = WingetImportManifest.WingetSourceType,
            },
        };
    }

    /// <summary>按成品清单名渲染批处理文本（name 已做过 basename 与 % 转义）。</summary>
    private static string RenderRestoreBat(string name, int count, int msStoreCount)
    {
        var msTip = string.Empty;
        if (msStoreCount > 0)
        {
            msTip = $"echo   其中 {msStoreCount} 个来自微软商店 msstore，可能要求先登录商店账号\r\n";
        }

        var lines = new List<string>
        {
            "@echo off",
            "chcp 936 >nul",
            "title 恢复软件清单",
            "cd /d \"%~dp0\"",
            "",
            "where winget >nul 2>nul",
            "if errorlevel 1 (",
            "  echo.",
            "  echo [x] 未找到 winget，请先安装「应用安装程序 / App Installer」。",
            "  echo     打开 Microsoft Store 搜索 App Installer 即可安装。",
            "  echo.",
            "  pause",
            "  exit /b 1",
            ")",
            "",
            "net session >nul 2>nul",
            "if errorlevel 1 (",
            "  echo.",
            "  echo [!] 当前不是管理员权限。部分机器级安装包会失败，",
            "  echo     建议右键本文件 - 以管理员身份运行。继续执行请按任意键。",
            "  pause >nul",
            ")",
            "",
            "echo.",
            "echo   Winget 软件管家 · 恢复脚本",
            "echo   ----------------------------------------",
            $"echo   清单: \"{name}\"（{count} 个软件包）",
            msTip.Length > 0 ? msTip.TrimEnd('\r', '\n') : "echo.",
            "echo.",
            "echo   即将执行 winget import，请保持网络畅通。",
            "echo   源里已下架的包会被自动跳过，不影响其余安装。",
            "echo.",
            "pause",
            "",
            $"winget import -i \"%~dp0{name}\" --accept-package-agreements --accept-source-agreements --ignore-unavailable",
            "",
            "echo.",
            "echo   ----------------------------------------",
            "echo   执行结束。按任意键关闭。",
            "pause >nul",
        };

        return string.Join("\r\n", lines) + "\r\n";
    }
}
