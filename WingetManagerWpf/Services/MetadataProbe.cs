using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WingetManagerWpf.Core;

namespace WingetManagerWpf.Services
{
    /// <summary>一次元数据判定的结果：<c>(是否为运行库, 命中的信号词)</c>。</summary>
    public struct RuntimeSignal
    {
        public RuntimeSignal(bool? isDependency, string signalWord)
        {
            IsDependency = isDependency;
            SignalWord = signalWord;
        }

        /// <summary>true=运行库；false=解析出描述/标记但没有信号；null=未知（**不能当成 false 用**）。</summary>
        public bool? IsDependency;

        /// <summary>命中的信号词；未命中为 null。</summary>
        public string SignalWord;
    }

    /// <summary>一条改判（供 UI 逐条打日志）。</summary>
    public sealed class MetadataChange
    {
        public MetadataChange(string id, string name, string signal, bool dep)
        {
            Id = id ?? string.Empty;
            Name = name ?? string.Empty;
            Signal = signal ?? string.Empty;
            Dep = dep;
        }

        public string Id { get; private set; }

        public string Name { get; private set; }

        /// <summary>命中的信号词（空表示「描述/标记未见运行库信号」）。</summary>
        public string Signal { get; private set; }

        public bool Dep { get; private set; }
    }

    /// <summary>
    /// 一批改判（最多 5 条）。<see cref="IsFinal"/> 为 true 时携带完整统计，
    /// 供 UI 打收尾日志 —— 这样「应用改判」和「打统计」只需两个回调。
    /// </summary>
    public sealed class MetadataBatch
    {
        public MetadataBatch()
        {
            Changes = new List<MetadataChange>();
        }

        public IList<MetadataChange> Changes { get; set; }

        public bool IsFinal { get; set; }

        public bool Canceled { get; set; }

        /// <summary>真正发起过 show 的条数。</summary>
        public int Checked { get; set; }

        /// <summary>元数据改判为运行库的条数。</summary>
        public int Promoted { get; set; }

        /// <summary>确认为软件的条数。</summary>
        public int Kept { get; set; }

        /// <summary>拿不到/解析不出、未写缓存的条数（下次扫描重试）。</summary>
        public int Failed { get; set; }
    }

    /// <summary>
    /// 用 winget 目录元数据自动判定「运行库 vs 软件」（逐行移植 <c>_ref/services/metadata.py</c>
    /// 与 <c>qt_app.MetadataWorker</c>）。
    /// <para>
    /// 关键词只做初判；扫描结束后对「可重装 ∧ 关键词判软件 ∧ 缓存未核查」的包逐个
    /// <c>winget show</c>，从描述/标记/安装器 URL 里找运行库信号词。
    /// </para>
    /// 与扫描的协调（易错点 23）：新扫描启动时先取消富化、再置 <c>scanGate</c>；
    /// 富化线程每次 show 前看到 gate 置位就挂起让路 —— 否则两边互撞 winget 互斥，扫描凭空变慢。
    /// </summary>
    public static class MetadataProbe
    {
        /// <summary>每攒够这么多条改判回报一次（UI 一批只重建一次列表）。</summary>
        public const int BatchSize = Metrics.EnrichBatch;

        /// <summary>描述/标记/安装器 URL 里出现这些词（大小写不敏感）即判为运行库。</summary>
        public static readonly string[] SignalWords =
        {
            "runtime", "redistributable", "redist", "运行库", "运行时", "required to run"
        };

        /// <summary>winget show 的段落标签：中文/英文、半角/全角冒号都认。</summary>
        private static readonly Regex LabelRegex = new Regex(
            @"^(描述|Description|标记|标签|Tags|安装程序 ?URL|Installer URL)\s*[:：]\s*(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex UrlRegex = new Regex(@"https?://\S+", RegexOptions.CultureInvariant);

        /// <summary>
        /// 综合判定：
        /// <list type="bullet">
        /// <item>命中信号词 → (true, 词)；</item>
        /// <item>解析出描述/标记但没命中 → (false, null)；</item>
        /// <item>输出为空或解析不出描述/标记（包不在目录等）→ (null, null)，绝不当作 false。</item>
        /// </list>
        /// </summary>
        public static RuntimeSignal Analyze(string showOutput)
        {
            if (string.IsNullOrEmpty(showOutput) || showOutput.Trim().Length == 0)
            {
                return new RuntimeSignal(null, null);
            }

            List<string> description;
            List<string> tags;
            List<string> urls;
            ExtractSections(showOutput, out description, out tags, out urls);

            if (description.Count == 0 && tags.Count == 0)
            {
                return new RuntimeSignal(null, null);
            }

            var joined = new List<string>(description.Count + tags.Count + urls.Count);
            joined.AddRange(description);
            joined.AddRange(tags);
            joined.AddRange(urls);

            string signal = MatchedSignal(string.Join("\n", joined.ToArray()));
            return new RuntimeSignal(signal != null, signal);
        }

        /// <summary>在文本里找第一个命中的运行库信号词；没有则返回 null。</summary>
        public static string MatchedSignal(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            string lowered = text.ToLowerInvariant();
            for (int i = 0; i < SignalWords.Length; i++)
            {
                if (lowered.IndexOf(SignalWords[i], StringComparison.Ordinal) >= 0)
                {
                    return SignalWords[i];
                }
            }

            return null;
        }

        /// <summary>
        /// 批量富化（无名字映射的重载：日志里会退回显示包 ID）。
        /// </summary>
        public static Task EnrichAsync(IWingetRepository repo, RuntimeCache cache, IEnumerable<string> pendingIds,
                                       IDictionary<string, bool> keywordDeps, IProgress<MetadataBatch> progress,
                                       ManualResetEventSlim scanGate, CancellationToken cancellationToken)
        {
            return EnrichAsync(repo, cache, pendingIds, null, keywordDeps, progress, scanGate, cancellationToken);
        }

        /// <summary>批量富化：每 5 条改判回报一批；扫描闸门置位时挂起让路；finally 里报收尾统计。</summary>
        public static Task EnrichAsync(IWingetRepository repo, RuntimeCache cache, IEnumerable<string> pendingIds,
                                       IDictionary<string, string> names, IDictionary<string, bool> keywordDeps,
                                       IProgress<MetadataBatch> progress, ManualResetEventSlim scanGate,
                                       CancellationToken cancellationToken)
        {
            if (repo == null)
            {
                throw new ArgumentNullException("repo");
            }

            // 入参在调用线程上复制一份：调用方可能在别的线程改这些集合
            var pending = pendingIds == null ? new List<string>() : new List<string>(pendingIds);
            var nameMap = names == null ? new Dictionary<string, string>(StringComparer.Ordinal)
                                        : new Dictionary<string, string>(names, StringComparer.Ordinal);
            var keywordMap = keywordDeps == null ? new Dictionary<string, bool>(StringComparer.Ordinal)
                                                 : new Dictionary<string, bool>(keywordDeps, StringComparer.Ordinal);

            return Task.Run(
                () => EnrichCore(repo, cache, pending, nameMap, keywordMap, progress, scanGate, cancellationToken),
                cancellationToken);
        }

        private static void EnrichCore(IWingetRepository repo, RuntimeCache cache, List<string> pending,
                                       Dictionary<string, string> names, Dictionary<string, bool> keywordDeps,
                                       IProgress<MetadataBatch> progress, ManualResetEventSlim scanGate,
                                       CancellationToken cancellationToken)
        {
            int checkedCount = 0;
            int promoted = 0;
            int kept = 0;
            int failed = 0;
            bool canceled = false;
            var batch = new List<MetadataChange>();

            try
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    string packageId = pending[i];
                    if (string.IsNullOrEmpty(packageId))
                    {
                        continue;
                    }

                    if (!WaitForScanGate(scanGate, cancellationToken))
                    {
                        canceled = true;
                        break;
                    }

                    // 别的轮次可能已经核查过：命中缓存就跳过，不再发起 show
                    if (cache != null && cache.Get(packageId).HasValue)
                    {
                        continue;
                    }

                    string text;
                    try
                    {
                        text = FetchShowOutput(repo, packageId, cancellationToken);
                    }
                    catch (ScanCancelledException)
                    {
                        canceled = true;
                        break;
                    }

                    checkedCount++;
                    RuntimeSignal signal = Analyze(text);
                    if (!signal.IsDependency.HasValue)
                    {
                        // 查询失败/解析不出：不写缓存，下次扫描再试
                        failed++;
                        continue;
                    }

                    bool dep = signal.IsDependency.Value;
                    if (cache != null)
                    {
                        cache.Put(packageId, dep);
                    }

                    if (dep)
                    {
                        promoted++;
                    }
                    else
                    {
                        kept++;
                    }

                    bool keywordDep;
                    bool differs = !keywordDeps.TryGetValue(packageId.ToLowerInvariant(), out keywordDep)
                                   || keywordDep != dep;
                    if (!differs)
                    {
                        // 与关键词初判一致的不用打扰用户
                        continue;
                    }

                    string name;
                    if (!names.TryGetValue(packageId, out name) || string.IsNullOrEmpty(name))
                    {
                        name = packageId;
                    }

                    batch.Add(new MetadataChange(packageId, name, signal.SignalWord, dep));
                    if (batch.Count >= BatchSize)
                    {
                        Report(progress, batch, false, false, checkedCount, promoted, kept, failed);
                        batch = new List<MetadataChange>();
                    }
                }
            }
            finally
            {
                // 无论正常结束/取消/异常，都要把剩余批次与统计送出去，
                // 否则 UI 永远等不到「核查完成」那行日志
                Report(progress, batch, true, canceled || cancellationToken.IsCancellationRequested,
                       checkedCount, promoted, kept, failed);
            }
        }

        private static void Report(IProgress<MetadataBatch> progress, List<MetadataChange> changes, bool isFinal,
                                   bool canceled, int checkedCount, int promoted, int kept, int failed)
        {
            if (progress == null)
            {
                return;
            }

            var batch = new MetadataBatch
            {
                Changes = new List<MetadataChange>(changes),
                IsFinal = isFinal,
                Canceled = canceled,
                Checked = checkedCount,
                Promoted = promoted,
                Kept = kept,
                Failed = failed
            };

            try
            {
                progress.Report(batch);
            }
            catch (Exception)
            {
                // UI 侧回调异常不能打断富化（否则剩下的包永远核查不完）
            }
        }

        /// <summary>扫描进行中就挂起让路；返回 false 表示该收工（被取消）。</summary>
        private static bool WaitForScanGate(ManualResetEventSlim scanGate, CancellationToken cancellationToken)
        {
            while (scanGate != null && scanGate.IsSet)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                scanGate.Wait(Metrics.PollMs);
            }

            return !cancellationToken.IsCancellationRequested;
        }

        /// <summary>
        /// 取一次 show 输出。取消向上传播；其他失败（超时/包不在目录）记日志后返回空串，
        /// 上层会解析成 None，不写缓存，下次扫描再试。
        /// </summary>
        private static string FetchShowOutput(IWingetRepository repo, string packageId, CancellationToken cancellationToken)
        {
            try
            {
                CommandResult result = repo.ShowMetadataAsync(packageId, cancellationToken)
                    .GetAwaiter().GetResult();
                return result == null ? string.Empty : (result.StdOut ?? string.Empty);
            }
            catch (ScanCancelledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 单个包失败不该拖垮整轮富化
                return string.Empty;
            }
        }

        /// <summary>
        /// 从 show 输出里抠出（描述行, 标记词, 安装器 URL）。
        /// 标记是「标签行之后的缩进列表」，直到空行或下一个 <c>xxx:</c> 标签行为止；
        /// 描述同样支持折行。
        /// </summary>
        private static void ExtractSections(string text, out List<string> description,
                                            out List<string> tags, out List<string> urls)
        {
            description = new List<string>();
            tags = new List<string>();
            urls = new List<string>();

            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string normalized = EncodedText.NormalizeNewlines(text);
            string[] lines = normalized.Split('\n');
            string mode = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                string stripped = line.Trim();
                if (stripped.Length == 0)
                {
                    mode = null;
                    continue;
                }

                Match match = LabelRegex.Match(stripped);
                if (match.Success)
                {
                    string label = match.Groups[1].Value.ToLowerInvariant().Replace(" ", string.Empty);
                    string rest = match.Groups[2].Value.Trim();

                    if (label == "描述" || label == "description")
                    {
                        mode = "desc";
                        if (rest.Length > 0)
                        {
                            description.Add(rest);
                        }

                        continue;
                    }

                    if (label == "标记" || label == "标签" || label == "tags")
                    {
                        mode = "tags";
                        if (rest.Length > 0)
                        {
                            // 有的 winget 版本把标记写在同一行
                            AddWords(tags, rest);
                        }

                        continue;
                    }

                    // 安装程序 URL / Installer URL
                    mode = null;
                    Match url = UrlRegex.Match(rest);
                    if (url.Success)
                    {
                        urls.Add(url.Value);
                    }

                    continue;
                }

                if (mode != null && line.Length > 0 && char.IsWhiteSpace(line[0]))
                {
                    // 缩进行 = 当前段落的延续
                    if (mode == "desc")
                    {
                        description.Add(stripped);
                    }
                    else
                    {
                        AddWords(tags, stripped);
                    }

                    continue;
                }

                // 顶格的非标签行：段落结束
                mode = null;
            }
        }

        private static void AddWords(List<string> target, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string[] parts = text.Split(new[] { ' ', '\t', '，', ',', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length > 0)
                {
                    target.Add(part);
                }
            }
        }
    }
}
