using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WingetManagerWpf.Core;
using WingetManagerWpf.Models;

namespace WingetManagerWpf.Services
{
    /// <summary>
    /// 目录服务：把 winget 的原始数据加工成可用的包列表（去噪、去重、判定能否重装）。
    /// <para>
    /// 锁纪律：<c>_sync</c> **只**保护快照字段，绝不在锁内跑 winget ——
    /// 一次扫描动辄几十秒，把锁跨在子进程外面会让「取消」和后续刷新全部堵死。
    /// </para>
    /// 分组判定只有两个输入：Reinstallable（能不能重装）与 IsDependency（是不是运行库），
    /// 具体分几组由 UI 决定。
    /// </summary>
    public sealed class PackageCatalog
    {
        /// <summary>
        /// 运行库/系统组件关键词（前 15 个顺序照原版，后 4 个是补充的窄变体）。只用于**初判**：
        /// 元数据核查会补上漏判的（如 UI.Xaml 这类描述里不写 runtime 的框架）。
        /// <para>
        /// 为什么追加这 4 个：<c>VC_redist.x64</c> 的下划线让 <c>vcredist</c> 匹配不上（包 ID 形态
        /// <c>Microsoft.VC_redist.14</c>）；显示名 <c>.NET Runtime 8.x</c> / <c>WindowsDesktop App</c> /
        /// <c>ASP.NET Core Runtime</c> 则不命中任何原有词，只能等后台 <c>winget show</c> 才改判。
        /// 只加窄词（带下划线/带限定前缀）：<c>visual c++</c>、<c>runtime</c> 这类宽词会把
        /// 浏览器、杀毒软件等带 runtime 字样的软件全部误判成运行库，反而更糟。
        /// </para>
        /// </summary>
        public static readonly string[] DependencyKeywords =
        {
            "vcredist", "visualc++", "redistributable", "dotnet", "windowsappruntime", "vclibs",
            "ui.xaml", "appinstaller", "directx", "webview2", ".net framework", "net framework",
            "net.native", "desktopappruntime", "xna",
            "vc_redist", "net runtime", "windowsdesktop.app", "aspnetcore"
        };

        /// <summary>「源」列出现这些值就说明 winget 有安装源、能重装。</summary>
        private static readonly List<string> ReinstallableSources = new List<string> { "winget", "msstore" };

        private readonly AppConfig _config;
        private readonly IWingetRepository _repo;
        private readonly RuntimeCache _cache;
        private readonly AppLogger _log;
        private readonly object _sync = new object();

        private CatalogSnapshot _snapshot;
        private DateTime _snapshotAtUtc = DateTime.MinValue;

        public PackageCatalog(AppConfig config, IWingetRepository repository, RuntimeCache cache, AppLogger log)
        {
            _config = config ?? throw new ArgumentNullException("config");
            _repo = repository ?? throw new ArgumentNullException("repository");
            _cache = cache;
            _log = log;
        }

        /// <summary>
        /// 取目录快照。TTL 内且未强制刷新时直接返回上次结果（<c>WPM_CACHE_TTL=0</c> 时每次都真扫）。
        /// </summary>
        public async Task<CatalogSnapshot> GetCatalogAsync(bool force, CancellationToken cancellationToken, string wingetVersion)
        {
            CatalogSnapshot cached = null;
            double cachedAgeSeconds = 0;

            lock (_sync)
            {
                if (!force && _snapshot != null && _config.CacheTtlSeconds > 0)
                {
                    double ageSeconds = (DateTime.UtcNow - _snapshotAtUtc).TotalSeconds;
                    if (ageSeconds >= 0 && ageSeconds < _config.CacheTtlSeconds)
                    {
                        cached = _snapshot;
                        cachedAgeSeconds = ageSeconds;
                    }
                }
            }

            if (cached != null)
            {
                // 日志放在锁外：LogEmitted 的订阅方是 UI，别让 UI 回调被目录锁挡住
                if (_log != null)
                {
                    _log.Info("目录缓存命中（" + ((int)cachedAgeSeconds).ToString(CultureInfo.InvariantCulture)
                              + "s < " + _config.CacheTtlSeconds.ToString(CultureInfo.InvariantCulture)
                              + "s），跳过 winget list。");
                }

                return cached;
            }

            // 锁外构建：期间不持锁，取消/查询都不会被长任务挡住
            CatalogSnapshot built = await BuildSnapshotAsync(cancellationToken, wingetVersion).ConfigureAwait(false);

            lock (_sync)
            {
                _snapshot = built;
                _snapshotAtUtc = DateTime.UtcNow;
            }

            return built;
        }

        /// <summary>
        /// 值得发起 <c>winget show</c> 的包 ID：可重装 ∧ 关键词判软件 ∧ 缓存未命中。
        /// 顺序与快照一致，重复 ID 只取一次（供后台富化使用）。
        /// </summary>
        public IList<string> PendingMetadataIds(CatalogSnapshot snapshot)
        {
            var result = new List<string>();
            if (_cache == null || snapshot == null || snapshot.Packages == null)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Package package in snapshot.Packages)
            {
                if (package == null)
                {
                    continue;
                }

                // IsDependency 为 true：要么关键词已判运行库，要么元数据已确认 —— 再 show 也不会改判
                if (!package.Reinstallable || package.IsDependency)
                {
                    continue;
                }

                string key = (package.PackageIdentifier ?? string.Empty).ToLowerInvariant();
                if (key.Length == 0 || seen.Contains(key) || _cache.Get(key).HasValue)
                {
                    continue;
                }

                seen.Add(key);
                result.Add(package.PackageIdentifier);
            }

            return result;
        }

        /// <summary>能不能被 winget 重装：只看「源」列（空 = 注册表安装 / MSIX 桥 / 源里已下架）。</summary>
        public static bool IsReinstallable(RawPackage raw)
        {
            if (raw == null)
            {
                return false;
            }

            string source = (raw.Source ?? string.Empty).Trim().ToLowerInvariant();
            return ReinstallableSources.Contains(source);
        }

        /// <summary>关键词初判：名称或包 ID 命中即算运行库。</summary>
        public static bool IsDependencyKeyword(RawPackage raw)
        {
            if (raw == null)
            {
                return false;
            }

            return MatchedDependencyKeyword(raw.Name, raw.PackageId) != null;
        }

        /// <summary>
        /// 返回**第一个**命中的关键词（按 <see cref="DependencyKeywords"/> 的顺序判定），未命中返回
        /// <c>null</c>。为什么单独暴露这个方法：日志里要按「哪个词命中了几次」统计，
        /// 只有布尔结论时根本查不出误判是哪个词造成的。
        /// </summary>
        public static string MatchedDependencyKeyword(string name, string packageId)
        {
            string haystack = ((name ?? string.Empty) + " " + (packageId ?? string.Empty)).ToLowerInvariant();
            for (int i = 0; i < DependencyKeywords.Length; i++)
            {
                string keyword = DependencyKeywords[i];
                if (haystack.IndexOf(keyword, StringComparison.Ordinal) >= 0)
                {
                    return keyword;
                }
            }

            return null;
        }

        /// <summary>包 ID 形态：msix\ → Msix；含反斜杠（含 arp\）→ Registry；其余 Winget。</summary>
        public static IdKind IdKindOf(string packageId)
        {
            string text = packageId ?? string.Empty;
            string lowered = text.ToLowerInvariant();
            if (lowered.StartsWith("msix\\", StringComparison.Ordinal))
            {
                return IdKind.Msix;
            }

            if (lowered.StartsWith("arp\\", StringComparison.Ordinal))
            {
                return IdKind.Registry;
            }

            if (text.IndexOf('\\') >= 0)
            {
                return IdKind.Registry;
            }

            return IdKind.Winget;
        }

        private async Task<CatalogSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken, string wingetVersion)
        {
            var stopwatch = Stopwatch.StartNew();

            CommandResult listResult = await _repo.ListInstalledAsync(cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new ScanCancelledException("扫描已取消");
            }

            IList<RawPackage> rawPackages = WingetTableParser.Parse(listResult.StdOut);

            var unique = new Dictionary<string, Package>(StringComparer.Ordinal);
            int keywordDependencyCount = 0;

            // 命中词 → 次数。键统一小写（关键词表本身已是小写，这里再兜一层，避免大小写重复计数）
            var keywordHits = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < rawPackages.Count; i++)
            {
                RawPackage raw = rawPackages[i];
                if (raw == null || string.IsNullOrEmpty(raw.PackageId))
                {
                    continue;
                }

                string key = raw.PackageId.ToLowerInvariant();
                bool? cachedDep = _cache != null ? _cache.Get(key) : null;
                string matchedKeyword = MatchedDependencyKeyword(raw.Name, raw.PackageId);
                bool keywordDep = matchedKeyword != null;
                if (keywordDep)
                {
                    keywordDependencyCount++;
                    string hitKey = matchedKeyword.ToLowerInvariant();
                    int hitCount;
                    keywordHits[hitKey] = keywordHits.TryGetValue(hitKey, out hitCount) ? hitCount + 1 : 1;
                }

                var package = new Package
                {
                    PackageIdentifier = raw.PackageId,
                    Name = string.IsNullOrEmpty((raw.Name ?? string.Empty).Trim()) ? raw.PackageId : raw.Name.Trim(),
                    Version = raw.Version ?? string.Empty,
                    AvailableVersion = raw.Available ?? string.Empty,
                    Source = raw.Source ?? string.Empty,
                    IdKind = IdKindOf(raw.PackageId),
                    Reinstallable = IsReinstallable(raw),
                    // 元数据只补漏、不打假：缓存 True 覆盖关键词（补漏）；缓存 False 不构成否定
                    IsDependency = cachedDep.HasValue && cachedDep.Value ? true : keywordDep
                };

                // 同一 ID 可能有多行（x86/x64 组件）：保留信息更全的那条（优先可重装）
                Package previous;
                if (!unique.TryGetValue(key, out previous)
                    || (!previous.Reinstallable && package.Reinstallable))
                {
                    unique[key] = package;
                }
            }

            var packages = new List<Package>(unique.Count);
            foreach (var pair in unique)
            {
                packages.Add(pair.Value);
            }

            // 可重装的排前面，组内按名称小写升序（组归属由 UI 按 Reinstallable 判定）
            packages.Sort(ComparePackages);

            var counts = new CatalogCounts();
            counts.Total = packages.Count;
            for (int i = 0; i < packages.Count; i++)
            {
                if (packages[i].Reinstallable)
                {
                    counts.Reinstallable++;
                }
                else
                {
                    counts.Unsupported++;
                }

                if (packages[i].IsDependency)
                {
                    // all 口径：不管能不能重装都算（原版口径，供诊断/脚本使用）
                    counts.DependenciesAll++;

                    // 界面口径：只有可重装的才进「运行库 N」（与 Groups.GroupOf / RecomputeCounts 一致）
                    if (packages[i].Reinstallable)
                    {
                        counts.Dependencies++;
                    }
                }
            }

            var snapshot = new CatalogSnapshot
            {
                GeneratedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                WingetVersion = string.IsNullOrEmpty(wingetVersion) ? "unknown" : wingetVersion,
                Hostname = SafeMachineName(),
                Os = SafeOsDescription(),
                Arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "unknown",
                Counts = counts,
                Packages = packages
            };

            if (_log != null)
            {
                // 这里报的是「行」不是「包」：keywordDependencyCount 与 keywordHits 都在**去重前**的
                // raw 行循环里累加，同一 ID 的 x86/x64 两行会各计一次，所以它是诊断量，
                // 天然可能大于「运行库」分组行数 —— 文案写「行」就是为了别让人拿它去对分组数。
                // 命中词明细紧跟在计数后面：一眼看出「这次被判成运行库的主要是哪些词」，
                // 误判（例如某软件名里恰好含 dotnet）才有线索。
                _log.Info("目录刷新完成：" + counts.Total.ToString(CultureInfo.InvariantCulture)
                          + " 项（可重装 " + counts.Reinstallable.ToString(CultureInfo.InvariantCulture)
                          + " 项，关键词命中 " + keywordDependencyCount.ToString(CultureInfo.InvariantCulture)
                          + " 行" + FormatTopKeywordHits(keywordHits, 3) + "）"
                          + snapshot.DurationMs.ToString(CultureInfo.InvariantCulture) + "ms");
            }

            return snapshot;
        }

        /// <summary>
        /// 生成「：词a×次数, 词b×次数」形式的命中词明细（最多 <paramref name="top"/> 个），
        /// 供刷新日志追加。排序：次数降序，同次数按词名字母序 —— 固定顺序保证日志可复现，
        /// 否则同一个目录两次刷新会打出不同的词序，排查时无法对比。
        /// 没有命中词时返回空串（调用方无需判空，日志文案保持不变）。
        /// </summary>
        private static string FormatTopKeywordHits(Dictionary<string, int> hits, int top)
        {
            if (hits == null || hits.Count == 0 || top <= 0)
            {
                return string.Empty;
            }

            var words = new List<string>(hits.Keys);
            words.Sort(delegate(string left, string right)
            {
                int byCount = hits[right].CompareTo(hits[left]);
                if (byCount != 0)
                {
                    return byCount;
                }

                return string.CompareOrdinal(left, right);
            });

            var builder = new StringBuilder("：");
            int take = Math.Min(top, words.Count);
            for (int i = 0; i < take; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                string word = words[i];
                builder.Append(word).Append('×').Append(hits[word].ToString(CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static int ComparePackages(Package left, Package right)
        {
            // 可重装在前：b.Reinstallable.CompareTo(a.Reinstallable) 让 true 排前面
            int byReinstallable = right.Reinstallable.CompareTo(left.Reinstallable);
            if (byReinstallable != 0)
            {
                return byReinstallable;
            }

            string leftName = (left.Name ?? string.Empty).ToLowerInvariant();
            string rightName = (right.Name ?? string.Empty).ToLowerInvariant();
            int byName = string.CompareOrdinal(leftName, rightName);
            if (byName != 0)
            {
                return byName;
            }

            // 名称相同时按 ID 兜底，保证排序稳定可复现
            return string.CompareOrdinal((left.PackageIdentifier ?? string.Empty).ToLowerInvariant(),
                                         (right.PackageIdentifier ?? string.Empty).ToLowerInvariant());
        }

        private static string SafeMachineName()
        {
            try
            {
                return Environment.MachineName ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string SafeOsDescription()
        {
            try
            {
                Version version = Environment.OSVersion.Version;
                return "Windows " + version.Major.ToString(CultureInfo.InvariantCulture) + "."
                       + version.Minor.ToString(CultureInfo.InvariantCulture)
                       + " (" + version.ToString() + ")";
            }
            catch (Exception)
            {
                return "Windows";
            }
        }
    }
}
