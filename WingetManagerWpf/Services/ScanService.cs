using System;
using System.Threading;
using System.Threading.Tasks;
using WingetManagerWpf.Core;
using WingetManagerWpf.Models;

namespace WingetManagerWpf.Services
{
    /// <summary>
    /// 扫描编排：<c>winget --version</c>（失败不致命）→ <c>GetCatalogAsync(force:true)</c> → 快照。
    /// <para>
    /// 异常语义（供 ViewModel 映射成 UI 状态）：
    /// <list type="bullet">
    /// <item><see cref="WingetNotFoundException"/> → NO_WINGET（弹安装引导）；</item>
    /// <item><see cref="ScanCancelledException"/> / <see cref="OperationCanceledException"/> → 取消；</item>
    /// <item>其余 → SCAN_FAILED（保留旧数据，只记日志）。</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class ScanService
    {
        private readonly PackageCatalog _catalog;
        private readonly IWingetRepository _repo;
        private readonly AppLogger _log;

        public ScanService(PackageCatalog catalog, IWingetRepository repository, AppLogger log)
        {
            _catalog = catalog ?? throw new ArgumentNullException("catalog");
            _repo = repository ?? throw new ArgumentNullException("repository");
            _log = log;
        }

        /// <summary>执行一次扫描（等价于 <c>ScanAsync(force:true, null, ct)</c>）。</summary>
        public Task<CatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
        {
            return ScanAsync(true, null, cancellationToken);
        }

        /// <summary>
        /// 执行一次扫描。<paramref name="force"/> 为 false 时允许命中目录 TTL 缓存；
        /// <paramref name="progress"/> 只用来报「正在读列表」这类粗粒度阶段（细粒度进度由 UI 计时器负责）。
        /// </summary>
        public async Task<CatalogSnapshot> ScanAsync(bool force, IProgress<string> progress, CancellationToken cancellationToken)
        {
            string version = "unknown";

            try
            {
                // 前置探测：只有「真的找不到 winget 可执行文件」才给安装引导。
                // 拿不到版本号不能当成没装 —— 与别的 winget 实例并发时可能空输出（软件其实装着）
                version = await _repo.VersionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ScanCancelledException)
            {
                throw;
            }
            catch (WingetNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 探测本身出错（超时等）也不该拦着扫描：后面 list 会给出真实结论
                if (_log != null)
                {
                    _log.Warn("winget 版本探测失败，按 unknown 继续：" + ex.Message);
                }

                version = "unknown";
            }

            if (string.IsNullOrEmpty(version))
            {
                version = "unknown";
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new ScanCancelledException("扫描已取消");
            }

            if (progress != null)
            {
                progress.Report("正在读取已安装软件列表…");
            }

            CatalogSnapshot snapshot = await _catalog.GetCatalogAsync(force, cancellationToken, version)
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new ScanCancelledException("扫描已取消");
            }

            return snapshot;
        }
    }
}
