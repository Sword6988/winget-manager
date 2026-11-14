using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WingetManager.Infra;

namespace WingetManager.Services;

/// <summary>一条元数据改判（与关键词初判不一致时才发）。</summary>
/// <param name="PackageId">包 ID。</param>
/// <param name="Name">显示名称。</param>
/// <param name="IsDependency">改判后的结果。</param>
/// <param name="Signal">命中的信号词（未命中为 null）。</param>
public sealed record MetadataChange(string PackageId, string Name, bool IsDependency, string? Signal);

/// <summary>一轮富化的收尾摘要。</summary>
/// <param name="Checked">实际发起过 show 的条数。</param>
/// <param name="Promoted">改判为运行库的条数。</param>
/// <param name="Kept">确认为软件的条数。</param>
/// <param name="Failed">判定不出（下次重试）的条数。</param>
/// <param name="Cancelled">是否被取消。</param>
public sealed record EnrichmentSummary(int Checked, int Promoted, int Kept, int Failed, bool Cancelled);

/// <summary>
/// 后台元数据富化任务（对应 Python 版 <c>MetadataWorker</c>）。
///
/// 与扫描共用唯一的 winget 入口（<see cref="WingetRepository"/> 内部 <c>SemaphoreSlim</c>
/// 串行化），协调方式：
/// <list type="bullet">
/// <item>每次 show 前若扫描在跑（<c>isScanActive</c> 返回 true）就挂起让路；</item>
/// <item>新扫描启动时整条任务被取消，进行中的 show 会在 0.2s 内被 kill 掉，
///       没查完的 ID 下轮扫描重新入队；</item>
/// <item>每条结果写入 <see cref="RuntimeCache"/>（写后立即落盘）；每次 show 前再查一遍
///       缓存，被取消前的成果不会白费，也不会与新一轮重复 show。</item>
/// </list>
/// 改判按批回报（每攒 <see cref="BatchSize"/> 条发一次），界面一批只重建一次列表。
/// </summary>
public sealed class EnrichmentWorker
{
    /// <summary>每批改判的条数（对应 Python 版 BATCH_SIZE=5）。</summary>
    public const int BatchSize = 5;

    /// <summary>让路轮询间隔（毫秒）：扫描进行中周期性醒来查看取消标志。</summary>
    public const int YieldPollMilliseconds = 200;

    private readonly WingetRepository _repository;
    private readonly RuntimeCache _cache;
    private readonly LogService _log;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _task;

    /// <summary>创建富化任务（构造后调用 <see cref="Start"/> 才会真正跑）。</summary>
    /// <param name="repository">winget 仓储层。</param>
    /// <param name="cache">运行库判定缓存。</param>
    /// <param name="log">日志服务；null 用共享实例。</param>
    public EnrichmentWorker(WingetRepository repository, RuntimeCache cache, LogService? log = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _log = log ?? LogService.Shared;
    }

    /// <summary>一批改判（在后台线程触发，订阅方负责切 UI 线程）。</summary>
    public event Action<IReadOnlyList<MetadataChange>>? Changes;

    /// <summary>一轮结束（在后台线程触发）。</summary>
    public event Action<EnrichmentSummary>? Completed;

    /// <summary>是否有正在跑的任务。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _task is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// 启动一轮富化；已有任务会先被取消（新扫描抢占）。
    /// </summary>
    /// <param name="pendingIds">待核查的包 ID（见 <see cref="CatalogService.PendingMetadataIds"/>）。</param>
    /// <param name="names">包 ID → 显示名称（日志用）。</param>
    /// <param name="keywordDependencies">包 ID（小写）→ 关键词初判结果，用于只报「改判」。</param>
    /// <param name="isScanActive">扫描是否在进行（true 时让路）；null 表示不需要让路。</param>
    public void Start(IReadOnlyList<string> pendingIds,
                      IReadOnlyDictionary<string, string>? names = null,
                      IReadOnlyDictionary<string, bool>? keywordDependencies = null,
                      Func<bool>? isScanActive = null)
    {
        ArgumentNullException.ThrowIfNull(pendingIds);

        Cancel();

        var cts = new CancellationTokenSource();
        var nameMap = names is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);
        var keywordMap = keywordDependencies is null
            ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, bool>(keywordDependencies, StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>(pendingIds);

        lock (_gate)
        {
            _cts = cts;
            _task = Task.Run(() => RunAsync(ids, nameMap, keywordMap, isScanActive, cts.Token), CancellationToken.None);
        }
    }

    /// <summary>
    /// 取消当前任务（不等待）。已写入缓存的结果保留，没查完的 ID 下轮自动续上。
    /// </summary>
    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 等待当前任务收尾（界面关闭时用，避免后台线程还在跑就退出进程）。
    /// </summary>
    /// <param name="timeout">最长等待时间。</param>
    /// <returns>是否已收尾。</returns>
    public async Task<bool> WaitAsync(TimeSpan timeout)
    {
        Task? task;
        lock (_gate)
        {
            task = _task;
        }

        if (task is null)
        {
            return true;
        }

        var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        return ReferenceEquals(finished, task) && task.IsCompleted;
    }

    private async Task RunAsync(IReadOnlyList<string> pendingIds,
                               IReadOnlyDictionary<string, string> names,
                               IReadOnlyDictionary<string, bool> keywordDependencies,
                               Func<bool>? isScanActive,
                               CancellationToken cancellationToken)
    {
        var checkedCount = 0;
        var promoted = 0;
        var kept = 0;
        var failed = 0;
        var batch = new List<MetadataChange>(BatchSize);

        void Flush()
        {
            if (batch.Count == 0)
            {
                return;
            }

            var snapshot = new List<MetadataChange>(batch);
            batch.Clear();
            try
            {
                Changes?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                _log.Warn("元数据改判回调失败：{0}", ex.Message);
            }
        }

        try
        {
            foreach (var packageId in pendingIds)
            {
                if (!await WaitForScanIdleAsync(isScanActive, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                if (_cache.Get(packageId) is not null)
                {
                    continue; // 别的轮次已核查过：跳过，不再发起 show
                }

                string text;
                try
                {
                    text = await FetchShowOutputAsync(packageId, cancellationToken).ConfigureAwait(false);
                }
                catch (ScanCancelledException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                checkedCount++;
                var (isDependency, signal) = RuntimeMetadataService.AnalyzeRuntimeSignal(text);
                if (isDependency is null)
                {
                    failed++; // 查询失败/解析不出：不写缓存，下次再试
                    continue;
                }

                _cache.Put(packageId, isDependency.Value);
                if (isDependency.Value)
                {
                    promoted++;
                }
                else
                {
                    kept++;
                }

                var hasKeyword = keywordDependencies.TryGetValue(packageId, out var keyword);
                if (!hasKeyword || isDependency.Value != keyword)
                {
                    // 只报「改判」：与关键词初判一致的不用打扰用户
                    // （初判缺失时按 Python 版口径一律上报）
                    var name = names.TryGetValue(packageId, out var display) ? display : packageId;
                    batch.Add(new MetadataChange(packageId, name, isDependency.Value, signal));
                    if (batch.Count >= BatchSize)
                    {
                        Flush();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 审计 F4 兜底：循环里的意外异常不该变成无人观察的任务异常静默消失
            // （Task.Run 的结果没人 await），至少留下日志；已查到的改判照常在 finally 里刷出。
            _log.Error("元数据富化意外中断：{0}", ex);
        }
        finally
        {
            Flush();
            var summary = new EnrichmentSummary(checkedCount, promoted, kept, failed,
                                               cancellationToken.IsCancellationRequested);
            try
            {
                Completed?.Invoke(summary);
            }
            catch (Exception ex)
            {
                _log.Warn("元数据收尾回调失败：{0}", ex.Message);
            }
        }
    }

    /// <summary>扫描进行中就挂起让路；返回 false 表示该收工（被取消）。</summary>
    private static async Task<bool> WaitForScanIdleAsync(Func<bool>? isScanActive, CancellationToken cancellationToken)
    {
        while (isScanActive is not null && isScanActive())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await Task.Delay(YieldPollMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return !cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// 取一次 winget show 的原始文本：取消要向上传播；其他失败（超时/包不在目录）
    /// 记日志后返回空串——上层会解析成 null，不写缓存，下次扫描再试。
    /// </summary>
    private async Task<string> FetchShowOutputAsync(string packageId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _repository.ShowMetadataAsync(packageId, cancellationToken).ConfigureAwait(false);
            return result.Stdout ?? string.Empty;
        }
        catch (ScanCancelledException)
        {
            throw;
        }
        catch (AppException ex)
        {
            _log.Warn("winget show {0} 失败：{1}", packageId, ex.Message);
            return string.Empty;
        }
        catch (Exception ex)
        {
            // 单个包失败不该拖垮整轮富化
            _log.Warn("winget show {0} 失败：{1}", packageId, ex.Message);
            return string.Empty;
        }
    }
}
