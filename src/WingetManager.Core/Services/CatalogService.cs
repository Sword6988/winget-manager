using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WingetManager.Infra;
using WingetManager.Models;

namespace WingetManager.Services;

/// <summary>
/// 把 winget 的原始数据加工成前端可用的目录（去噪、判定能否重装），
/// 对应 <c>services/catalog.py</c>。
///
/// 分组判定只有两个输入：<see cref="Package.Reinstallable"/>（能不能被 winget 重装）与
/// <see cref="Package.IsDependency"/>（是不是运行库/系统组件）。本类只负责把这两个布尔值
/// 算准，具体分几组由界面按 <see cref="PackageGroups"/> 决定。
/// </summary>
public sealed class CatalogService
{
    /// <summary>「源」列里出现这些值，就说明 winget 有安装源、能重装。</summary>
    public static readonly IReadOnlySet<string> ReinstallableSources =
        new HashSet<string>(StringComparer.Ordinal) { "winget", "msstore" };

    /// <summary>
    /// 运行库/系统组件关键词表：只做初判（给列表行打「运行库」角标、给统计卡计数），
    /// 之后由 <c>winget show</c> 元数据核查补漏。
    /// </summary>
    public static readonly IReadOnlyList<string> DependencyKeywords = new[]
    {
        "vcredist", "visualc++", "redistributable", "dotnet", "windowsappruntime", "vclibs",
        "ui.xaml", "appinstaller", "directx", "webview2", ".net framework", "net framework",
        "net.native", "desktopappruntime", "xna",
    };

    private readonly AppConfig _config;
    private readonly WingetRepository _repository;
    private readonly RuntimeCache? _cache;
    private readonly LogService _log;
    private readonly object _gate = new();

    private CatalogSnapshot? _snapshot;
    private long _snapshotAtTicks;

    /// <summary>
    /// 创建目录服务。
    /// </summary>
    /// <param name="config">应用配置（缓存 TTL）。</param>
    /// <param name="repository">winget 仓储层。</param>
    /// <param name="cache">运行库判定缓存；null 表示关闭元数据合并（只看关键词）。</param>
    /// <param name="log">日志服务；null 用共享实例。</param>
    public CatalogService(AppConfig config, WingetRepository repository, RuntimeCache? cache = null,
                          LogService? log = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _cache = cache;
        _log = log ?? LogService.Shared;
    }

    /// <summary>当前缓存的快照（可能为 null）。</summary>
    public CatalogSnapshot? CurrentSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>
    /// 取目录快照：TTL 内且未强制刷新时直接返回上一份（对应 Python <c>get_catalog</c>）。
    ///
    /// 锁只保护 <c>_snapshot</c> 的读写，<b>绝不在锁内跑 winget</b>：一次扫描动辄几十秒，
    /// 把锁跨在子进程外面会让「取消」和后续刷新全部堵死。
    /// </summary>
    /// <param name="force">true = 忽略缓存重新扫描。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="wingetVersion">已探测到的 winget 版本（空则记 unknown）。</param>
    /// <returns>快照。</returns>
    public async Task<CatalogSnapshot> GetCatalogAsync(bool force, CancellationToken cancellationToken,
                                                       string wingetVersion = "")
    {
        TimeSpan age;
        CatalogSnapshot? cached;
        lock (_gate)
        {
            cached = _snapshot;
            age = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - _snapshotAtTicks);
        }

        if (!force && cached is not null && age.TotalSeconds < _config.CacheTtlSeconds)
        {
            return cached;
        }

        var snapshot = await BuildSnapshotAsync(cancellationToken, wingetVersion).ConfigureAwait(false);
        lock (_gate)
        {
            _snapshot = snapshot;
            _snapshotAtTicks = DateTime.UtcNow.Ticks;
        }

        return snapshot;
    }

    /// <summary>强制重新扫描并产出快照（对应 Python <c>_build_snapshot</c>）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="wingetVersion">已探测到的 winget 版本。</param>
    /// <returns>快照。</returns>
    public async Task<CatalogSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken,
                                                          string wingetVersion = "")
    {
        var stopwatch = Stopwatch.StartNew();

        var listResult = await _repository.ListInstalledAsync(cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            throw new ScanCancelledException("扫描已取消");
        }

        var rawPackages = TableParser.ParseListOutput(listResult.Stdout);

        var unique = new Dictionary<string, Package>(StringComparer.Ordinal);
        foreach (var raw in rawPackages)
        {
            if (string.IsNullOrEmpty(raw.PackageId))
            {
                continue;
            }

            var key = raw.PackageId.ToLowerInvariant();
            var cachedDependency = _cache?.Get(key);
            var keywordDependency = IsDependencyByKeyword(raw);

            var package = new Package
            {
                PackageIdentifier = raw.PackageId,
                Name = raw.Name.Trim().Length > 0 ? raw.Name.Trim() : raw.PackageId,
                Version = raw.Version,
                AvailableVersion = raw.Available,
                Source = raw.Source,
                IdKind = IdKind(raw.PackageId),
                Reinstallable = IsReinstallable(raw),

                // 元数据只补漏、不打假（产品裁定）：
                // - 缓存 true  = 元数据认定是运行库 → 覆盖关键词，补上关键词漏掉的；
                // - 缓存 false = 「已核查过，描述/标记里没有运行库信号」，仅用于避免下一轮
                //   重复 show，**不构成否定**——关键词已判运行库的包不会被移出该组；
                // - 缓存未命中 = 沿关键词初判，等待后台富化补齐。
                IsDependency = cachedDependency == true ? true : keywordDependency,
            };

            // 同一 ID 可能有多行（x86/x64 组件），保留信息更全的一条
            if (!unique.TryGetValue(key, out var previous)
                || (!previous.Reinstallable && package.Reinstallable))
            {
                unique[key] = package;
            }
        }

        // 可重装的排前面，组内按名称（不区分大小写）
        var packages = unique.Values
            .OrderBy(p => p.Reinstallable ? 0 : 1)
            .ThenBy(p => p.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();

        stopwatch.Stop();
        var durationMs = (int)stopwatch.ElapsedMilliseconds;

        var counts = new CatalogCounts
        {
            Total = packages.Count,
            Reinstallable = packages.Count(p => p.Reinstallable),
            Unsupported = packages.Count(p => !p.Reinstallable),
            Dependencies = packages.Count(p => p.IsDependency),
        };

        var snapshot = new CatalogSnapshot
        {
            GeneratedAt = FormatGeneratedAt(DateTimeOffset.Now),
            DurationMs = durationMs,
            WingetVersion = string.IsNullOrWhiteSpace(wingetVersion) ? WingetRepository.UnknownVersion : wingetVersion,
            Machine = DescribeMachine(),
            Counts = counts,
            Packages = packages,
        };

        _log.Info("目录刷新完成: {0} 项（可重装 {1}）{2}ms", counts.Total, counts.Reinstallable, durationMs);
        return snapshot;
    }

    /// <summary>
    /// 值得发起 <c>winget show</c> 的包 ID：可重装 ∧ 关键词判软件 ∧ 缓存未命中。
    ///
    /// 关键词已判运行库的包不会被核查降级，所以不必为它们花一次 show；只查关键词判「软件」
    /// 且缓存里还没有核查记录的可重装包。顺序与快照一致，重复 ID 只取一次。
    /// </summary>
    /// <param name="snapshot">目录快照。</param>
    /// <returns>待核查的包 ID 列表。</returns>
    public IReadOnlyList<string> PendingMetadataIds(CatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_cache is null)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<string>();
        foreach (var package in snapshot.Packages)
        {
            // IsDependency 为 true 的包：要么关键词判运行库，要么元数据已确认，
            // 两种情况下 show 都不会改变分组 → 不必核查
            if (!package.Reinstallable || package.IsDependency)
            {
                continue;
            }

            var key = package.PackageIdentifier.ToLowerInvariant();
            if (!seen.Add(key) || _cache.Get(key) is not null)
            {
                continue;
            }

            pending.Add(package.PackageIdentifier);
        }

        return pending;
    }

    /// <summary>
    /// 能不能被 winget 重装：<b>只看</b>「源」列 ∈ {winget, msstore}。源列为空 =
    /// 注册表安装 / MSIX 商店桥 / 源里已下架的包，winget 装不回去。
    /// </summary>
    /// <param name="raw">原始行。</param>
    /// <returns>是否可重装。</returns>
    public static bool IsReinstallable(RawPackage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        return ReinstallableSources.Contains(raw.Source.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// 关键词初判：名称或包 ID 命中运行库词表即算运行库。
    /// </summary>
    /// <param name="raw">原始行。</param>
    /// <returns>是否疑似运行库。</returns>
    public static bool IsDependencyByKeyword(RawPackage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var haystack = (raw.Name + " " + raw.PackageId).ToLowerInvariant();
        foreach (var keyword in DependencyKeywords)
        {
            if (haystack.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>包 ID 形态（msix / registry / winget），仅展示与诊断用。</summary>
    /// <param name="packageId">包 ID。</param>
    /// <returns>形态常量。</returns>
    public static string IdKind(string packageId)
    {
        var lowered = (packageId ?? string.Empty).ToLowerInvariant();
        if (lowered.StartsWith("msix\\", StringComparison.Ordinal))
        {
            return PackageIdKind.Msix;
        }

        if (lowered.StartsWith("arp\\", StringComparison.Ordinal))
        {
            return PackageIdKind.Registry;
        }

        return lowered.Contains('\\', StringComparison.Ordinal) ? PackageIdKind.Registry : PackageIdKind.Winget;
    }

    /// <summary>
    /// 快照时间：对齐 Python <c>time.strftime("%Y-%m-%dT%H:%M:%S%z")</c>（偏移不带冒号）。
    /// </summary>
    private static string FormatGeneratedAt(DateTimeOffset value)
    {
        var offset = value.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", string.Empty, StringComparison.Ordinal);
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + offset;
    }

    /// <summary>机器信息（对应 Python 版 platform.node/system/release/version/machine）。</summary>
    private static MachineInfo DescribeMachine()
    {
        var version = Environment.OSVersion.Version;
        return new MachineInfo
        {
            Hostname = Environment.MachineName,
            Os = string.Format(CultureInfo.InvariantCulture, "Windows {0} ({1})", version, Environment.OSVersion.VersionString),
            Arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "AMD64",
                Architecture.Arm64 => "ARM64",
                Architecture.X86 => "x86",
                var other => other.ToString(),
            },
        };
    }
}
