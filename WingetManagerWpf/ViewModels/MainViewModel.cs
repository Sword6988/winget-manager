using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WingetManagerWpf.Core;
using WingetManagerWpf.Models;
using WingetManagerWpf.Services;

namespace WingetManagerWpf.ViewModels
{
    /// <summary>
    /// 主界面状态机。六个状态互斥，所有「按钮能不能点 / 显示骨架还是空态」都由它派生，
    /// 避免在各处散落 <c>if (_scanning)</c>。
    /// </summary>
    public enum ViewState
    {
        /// <summary>启动后首扫还没回来（显示骨架屏 + 「正在扫描已安装软件…」）。</summary>
        Boot = 0,

        /// <summary>扫描中（可取消，已有旧数据时继续显示旧列表）。</summary>
        Scanning = 1,

        /// <summary>扫描成功，正常可操作。</summary>
        Ready = 2,

        /// <summary>扫描失败，保留旧数据；没有旧数据时显示失败空态。</summary>
        ScanFailed = 3,

        /// <summary>未检测到 winget（弹安装引导，底栏给固定文案）。</summary>
        NoWinget = 4,

        /// <summary>用户主动取消，且此前没有任何数据。</summary>
        Cancelled = 5
    }

    /// <summary>
    /// 主界面 ViewModel：扫描编排、筛选、勾选、计数、导出前置检查、日志。
    /// <para>
    /// 硬约束：本文件**只 using System / System.Threading / System.Threading.Tasks**，
    /// 不碰任何 <c>System.Windows*</c>。所有「需要窗口才能做的事」（弹框、剪贴板、
    /// Dispatcher 调度、计时器）都通过 <see cref="IMainShell"/> 回调出去。
    /// 这样 QA 可以用控制台工程链接 ViewModels + Core + Services 直接跑逻辑自测。
    /// </para>
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        // ── 依赖（全部由 MainWindow 注入） ──
        private readonly AppLogger _log;
        private readonly IWingetRepository _repo;
        private readonly PackageCatalog _catalog;
        private readonly RuntimeCache _cache;
        private readonly ScanService _scan;
        private readonly SelectionStore _selectionStore;
        private readonly IMainShell _shell;

        // ── 数据 ──
        /// <summary>全部包（含 winget 装不上的），顺序即快照顺序。</summary>
        private List<Package> _packages = new List<Package>();

        /// <summary>勾选表：包 ID 小写 → 是否勾选。只存 true 的项也能工作，但保留 false 便于内存内反选。</summary>
        private Dictionary<string, bool> _checked = new Dictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>被折叠的分组（默认全部展开，故记录「折叠的」而不是「展开的」）。</summary>
        private HashSet<string> _collapsed = new HashSet<string>(StringComparer.Ordinal);

        // ── 筛选 ──
        private string _activeCategory = string.Empty;
        private bool _onlySelected;
        private string _searchText = string.Empty;

        // ── 计数 ──
        private int _totalCount;
        private int _reinstallableCount;
        private int _dependencyCount;
        private int _unsupportedCount;
        private int _selectedCount;
        private int _matchedCount;

        // ── 状态 ──
        private ViewState _state = ViewState.Boot;
        private double _elapsedSeconds;
        private double _lastDurationSeconds;
        private string _wingetVersion = "unknown";
        private string _statusOverride;

        // ── 扫描 / 富化的取消与闸门 ──
        private CancellationTokenSource _scanCts;
        private bool _scanRunning;
        private readonly ManualResetEventSlim _scanGate = new ManualResetEventSlim(false);
        private CancellationTokenSource _enrichCts;
        private int _enrichChanged;

        // ── 勾选落盘的防抖 ──
        private readonly object _saveSync = new object();
        private Timer _saveTimer;
        private bool _saveDirty;
        private Dictionary<string, bool> _saveSnapshot;

        /// <summary>计数/状态变了但不需要重建列表时触发（勾选一个行只更新胶囊与底栏）。</summary>
        public event Action CountsInvalidated;

        /// <summary>状态机切换（视图据此换骨架屏/空态/按钮文案）。</summary>
        public event Action<ViewState> StateChanged;

        /// <summary>
        /// 当前（或最近一次）扫描任务。窗口关闭时需要它来「等扫描收尾」——
        /// 不暴露出来就只能盲目 Sleep，既可能白等 3 秒也可能提前退出留下 winget 子进程。
        /// </summary>
        public Task CurrentScanTask { get; private set; }

        public MainViewModel(AppLogger log, IWingetRepository repository, PackageCatalog catalog,
                             RuntimeCache cache, ScanService scan, SelectionStore selectionStore,
                             AppSettings settings, IMainShell shell)
        {
            _log = log;
            _repo = repository;
            _catalog = catalog;
            _cache = cache;
            _scan = scan;
            _selectionStore = selectionStore;
            _shell = shell;

            ScanOrCancelCommand = new RelayCommand(ExecuteScanOrCancel);
            SelectAllCommand = new RelayCommand(delegate { SetAll(true); }, IsReady);
            SelectNoneCommand = new RelayCommand(delegate { SetAll(false); }, IsReady);
            InvertCommand = new RelayCommand(Invert, IsReady);
            ClearFiltersCommand = new RelayCommand(ClearFilters);

            // 这里必须用「() =>」而不是「delegate { }」：无参数列表的匿名方法
            // 对 Action / Action<object> 都可转换，(Action, Func<bool>) 与
            // (Action<object>, Func<object,bool>) 两个重载会形成歧义（CS0121）
            ExportCommand = new RelayCommand(
                () => { if (_shell != null) { _shell.OpenExportDialog(); } },
                () => CanExport);

            // 勾选状态先读盘：即使扫描失败，勾选表也已经在内存里，点导出仍能导出上次的结果
            IDictionary<string, bool> stored = _selectionStore != null
                ? _selectionStore.Load()
                : new Dictionary<string, bool>(StringComparer.Ordinal);
            _checked = new Dictionary<string, bool>(stored, StringComparer.Ordinal);

            if (settings != null && !string.IsNullOrEmpty(settings.Category) && Groups.IsKnown(settings.Category))
            {
                _activeCategory = settings.Category;
                if (_log != null)
                {
                    // 这行必须在日志面板订阅之后才看得到，故放在构造函数里由 MainWindow 控制订阅顺序
                    _log.Info(Strings.LogCategoryRestored(_activeCategory));
                }
            }

            RecomputeCounts();
        }

        // ══════════════════════════ 命令 ══════════════════════════

        public RelayCommand ScanOrCancelCommand { get; private set; }

        public RelayCommand SelectAllCommand { get; private set; }

        public RelayCommand SelectNoneCommand { get; private set; }

        public RelayCommand InvertCommand { get; private set; }

        public RelayCommand ClearFiltersCommand { get; private set; }

        public RelayCommand ExportCommand { get; private set; }

        // ══════════════════════════ 只读视图属性 ══════════════════════════

        public ViewState State
        {
            get { return _state; }
        }

        /// <summary>全部包（只读视图；渲染层遍历它做分组）。</summary>
        public IList<Package> Packages
        {
            get { return _packages; }
        }

        public IDictionary<string, bool> CheckedMap
        {
            get { return _checked; }
        }

        public string ActiveCategory
        {
            get { return _activeCategory; }
        }

        public bool OnlySelected
        {
            get { return _onlySelected; }
            set
            {
                if (!SetProperty(ref _onlySelected, value, "OnlySelected"))
                {
                    return;
                }

                // 「仅看已选」直接改变可见集合，必须重建；状态文案也要跟着变
                RefreshMatched();
                RefreshStatus();
                RequestRelayout();
            }
        }

        public string SearchText
        {
            get { return _searchText; }
            set
            {
                string text = value ?? string.Empty;
                if (!SetProperty(ref _searchText, text, "SearchText"))
                {
                    return;
                }

                RefreshMatched();
                RefreshStatus();
                RequestRelayout();
            }
        }

        public int TotalCount
        {
            get { return _totalCount; }
        }

        /// <summary>可重装数（= 软件 + 运行库）。</summary>
        public int ReinstallableCount
        {
            get { return _reinstallableCount; }
        }

        /// <summary>软件数（可重装 ∧ 非运行库）。</summary>
        public int SoftwareCount
        {
            get { return _reinstallableCount - _dependencyCount; }
        }

        public int DependencyCount
        {
            get { return _dependencyCount; }
        }

        public int UnsupportedCount
        {
            get { return _unsupportedCount; }
        }

        /// <summary>已勾选的可重装包数。</summary>
        public int SelectedCount
        {
            get { return _selectedCount; }
        }

        /// <summary>当前筛选后被渲染的行数。</summary>
        public int MatchedCount
        {
            get { return _matchedCount; }
        }

        public double ElapsedSeconds
        {
            get { return _elapsedSeconds; }
        }

        public double LastDurationSeconds
        {
            get { return _lastDurationSeconds; }
        }

        public string WingetVersion
        {
            get { return _wingetVersion; }
        }

        /// <summary>扫描按钮文案：扫描中显示「取消扫描」。</summary>
        public string ScanButtonText
        {
            get { return _state == ViewState.Scanning ? Strings.ButtonCancelScan : Strings.ButtonScan; }
        }

        /// <summary>批量按钮（全选/全不选/反选）是否可用：只在 Ready 可用，扫描中禁用避免改到半成品数据。</summary>
        public bool IsBatchEnabled
        {
            get { return _state == ViewState.Ready; }
        }

        /// <summary>搜索框是否可用：扫描中输入没有意义（列表还没建）。</summary>
        public bool IsSearchEnabled
        {
            get { return _state != ViewState.Scanning; }
        }

        /// <summary>导出按钮可用：非 Boot/NoWinget/Cancelled，且有勾选。</summary>
        public bool CanExport
        {
            get
            {
                if (_selectedCount <= 0)
                {
                    return false;
                }

                return _state == ViewState.Ready
                       || _state == ViewState.Scanning
                       || _state == ViewState.ScanFailed;
            }
        }

        /// <summary>是否需要显示骨架屏：首扫期间且没有任何数据。</summary>
        public bool ShowSkeleton
        {
            get
            {
                return (_state == ViewState.Boot || _state == ViewState.Scanning) && _packages.Count == 0;
            }
        }

        /// <summary>是否需要显示空态面板（有数据时不显示，交给列表）。</summary>
        public bool ShowEmpty
        {
            get { return !ShowSkeleton && _matchedCount == 0; }
        }

        /// <summary>底栏文案（flash 期间被临时文案覆盖）。</summary>
        public string StatusText
        {
            get
            {
                if (_statusOverride != null)
                {
                    return _statusOverride;
                }

                return BuildStatusText();
            }
        }

        /// <summary>头部副标题。</summary>
        public string SubTitle
        {
            get
            {
                if (_state == ViewState.Boot)
                {
                    return Strings.StatusBoot;
                }

                if (_totalCount == 0)
                {
                    return _state == ViewState.Scanning ? Strings.StatusBoot : string.Empty;
                }

                return Strings.SubtitleDone(_lastDurationSeconds);
            }
        }

        /// <summary>底栏「/ R 项已勾选」。</summary>
        public string SelectedSuffixText
        {
            get { return Strings.SelectedSuffix(_reinstallableCount); }
        }

        /// <summary>当前搜索高亮关键词（去前缀、去重）。</summary>
        public string[] HighlightKeywords
        {
            get { return SearchQuery.HighlightKeywords(SearchQuery.Tokenize(_searchText)); }
        }

        // ══════════════════════════ 扫描 ══════════════════════════

        /// <summary>
        /// 扫描按钮的唯一入口：扫描中点击 = 取消，否则 = 重新扫描。
        /// </summary>
        public void ExecuteScanOrCancel()
        {
            if (_state == ViewState.Scanning)
            {
                CancelScan();
                return;
            }

            // 不 await：本方法不是 async，而且 StartScanAsync 内部已把所有异常收口
            // （try/catch/finally 覆盖到底），丢弃返回值不会产生「未观察的异常」。
            // 但要把 Task 存下来：窗口关闭时要等它收尾（≤3s），避免 winget 子进程残留。
            CurrentScanTask = StartScanAsync();
        }

        /// <summary>执行一次强制扫描。<see cref="ViewState"/> 的全部迁移都在这里收口。</summary>
        public async Task StartScanAsync()
        {
            if (_scanRunning)
            {
                // 上一轮还在收尾（多半卡在 taskkill）：给出提示而不是静默忽略点击
                if (_shell != null)
                {
                    _shell.Flash(Strings.FlashScanPending);
                }

                return;
            }

            // 顺序很重要：先停富化、再置扫描闸门。反过来的话富化可能刚好抢到 show，
            // 两边一起撞 winget 的全局互斥（0x8A150001），扫描凭空多等一轮退避。
            CancelEnrichment();
            _scanGate.Set();

            _scanRunning = true;
            _scanCts = new CancellationTokenSource();
            CancellationToken token = _scanCts.Token;

            _elapsedSeconds = 0;
            ClearStatusOverride();
            SetState(ViewState.Scanning);
            if (_log != null)
            {
                _log.Info(Strings.LogScanStart);
            }

            try
            {
                var progress = new Progress<string>(OnScanProgress);
                CatalogSnapshot snapshot = await _scan.ScanAsync(true, progress, token);

                if (token.IsCancellationRequested)
                {
                    throw new ScanCancelledException("扫描已取消");
                }

                OnScanOk(snapshot);
            }
            catch (ScanCancelledException)
            {
                OnScanCancelled();
            }
            catch (OperationCanceledException)
            {
                OnScanCancelled();
            }
            catch (WingetNotFoundException ex)
            {
                OnWingetMissing(ex);
            }
            catch (Exception ex)
            {
                OnScanFailed(ex);
            }
            finally
            {
                _scanRunning = false;
                _scanGate.Reset();

                if (_scanCts != null)
                {
                    try
                    {
                        _scanCts.Dispose();
                    }
                    catch (Exception)
                    {
                        // 已释放/正在释放：忽略
                    }

                    _scanCts = null;
                }

                RaiseStateDependentCanExecute();
            }
        }

        /// <summary>请求取消当前扫描（后台 winget 进程会在数秒内退出，结果不再采用）。</summary>
        public void CancelScan()
        {
            CancellationTokenSource cts = _scanCts;
            if (!_scanRunning || cts == null)
            {
                return;
            }

            if (_log != null)
            {
                _log.Info(Strings.LogScanCancelled);
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 刚好在收尾：无需再取消
            }

            if (_shell != null)
            {
                _shell.Flash(Strings.FlashCancelled);
            }
        }

        /// <summary>
        /// View 的 100ms 计时器调用它刷新「已用 X.Xs」。
        /// 不做成 VM 内部计时器：Timer 回调在后台线程，改属性会跨线程碰 UI，
        /// 而 ViewModel 不允许持有 Dispatcher。
        /// </summary>
        public void NotifyElapsedSeconds(double seconds)
        {
            if (_state != ViewState.Scanning)
            {
                return;
            }

            _elapsedSeconds = seconds < 0 ? 0 : seconds;
            OnPropertyChanged("ElapsedSeconds");
            if (_statusOverride == null)
            {
                OnPropertyChanged("StatusText");
            }
        }

        private void OnScanProgress(string message)
        {
            if (_log != null && !string.IsNullOrEmpty(message))
            {
                _log.Info(message);
            }
        }

        private void OnScanOk(CatalogSnapshot snapshot)
        {
            var list = new List<Package>();
            if (snapshot != null && snapshot.Packages != null)
            {
                for (int i = 0; i < snapshot.Packages.Count; i++)
                {
                    if (snapshot.Packages[i] != null)
                    {
                        list.Add(snapshot.Packages[i]);
                    }
                }
            }

            if (snapshot != null)
            {
                _wingetVersion = string.IsNullOrEmpty(snapshot.WingetVersion) ? "unknown" : snapshot.WingetVersion;
                _lastDurationSeconds = snapshot.DurationMs / 1000.0;
            }

            _packages = list;

            PurgeSelection();
            RecomputeCounts();
            RefreshMatched();

            SetState(ViewState.Ready);
            RefreshStatus();
            RequestRelayout();

            if (_log != null)
            {
                _log.Success(Strings.LogScanDone(_totalCount, _reinstallableCount, SoftwareCount, _dependencyCount));
            }

            StartEnrichment(snapshot);
        }

        private void OnScanCancelled()
        {
            // 取消文案已经在 CancelScan 里记过了，这里只切状态
            SetState(_packages.Count > 0 ? ViewState.Ready : ViewState.Cancelled);
            RefreshStatus();
            RequestRelayout();
        }

        private void OnWingetMissing(WingetNotFoundException ex)
        {
            if (_log != null)
            {
                _log.Error(Strings.LogWingetMissing(ex != null ? ex.Message : string.Empty));
            }

            SetState(ViewState.NoWinget);
            RefreshMatched();
            RefreshStatus();
            RequestRelayout();

            if (_shell != null)
            {
                _shell.ShowWingetGuide(ex != null ? ex.Message : string.Empty);
            }
        }

        private void OnScanFailed(Exception ex)
        {
            string detail = ex != null ? ex.Message : string.Empty;
            if (_log != null)
            {
                _log.Error(Strings.LogScanFailed(detail), ex);
            }

            // 失败保留旧数据：用户上次的勾选还在，能直接导出，比清空友好得多
            SetState(_packages.Count > 0 ? ViewState.Ready : ViewState.ScanFailed);
            RefreshMatched();
            RefreshStatus();
            RequestRelayout();
        }

        // ══════════════════════════ 元数据核查 ══════════════════════════

        private void StartEnrichment(CatalogSnapshot snapshot)
        {
            if (snapshot == null || _catalog == null || _repo == null)
            {
                return;
            }

            IList<string> pending = _catalog.PendingMetadataIds(snapshot);
            if (pending == null || pending.Count == 0)
            {
                return;
            }

            if (_log != null)
            {
                _log.Info(Strings.LogEnrichStart(pending.Count));
            }

            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var keywordDeps = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int i = 0; i < _packages.Count; i++)
            {
                Package package = _packages[i];
                if (package == null)
                {
                    continue;
                }

                if (!names.ContainsKey(package.PackageIdentifier))
                {
                    names[package.PackageIdentifier] = package.Name;
                }

                // 富化内部按小写 ID 查关键词结论，这里必须用小写键
                keywordDeps[package.Key] = package.IsDependency;
            }

            _enrichChanged = 0;

            // 每一轮核查持有**自己**的取消源，并把它随回调一起传下去。
            // 只用字段 _enrichCts 的话会出现这种错位：上一轮被取消后用户立刻重扫，
            // 新一轮已经把 _enrichCts 换成 B，而上一轮的「收尾」回调这时才到，
            // 于是它释放的是 B —— B 从此再也取消不掉，扫描闸门会被它一直挡着。
            var owner = new CancellationTokenSource();
            _enrichCts = owner;

            var progress = new Progress<MetadataBatch>(
                delegate(MetadataBatch batch) { OnMetadataBatch(batch, owner); });
            Task enrichment = MetadataProbe.EnrichAsync(_repo, _cache, pending, names, keywordDeps, progress,
                                                        _scanGate, owner.Token);

            // 富化是「发射后不管」的后台任务，必须显式接住它的异常：
            // 否则任务里的意外错误（写缓存时磁盘满、解析器抛非预期异常等）只会变成
            // UnobservedTaskException —— 由 GC 在某个不确定的时刻静默吞掉，
            // 用户看到的是「核查中」永远不结束，日志里一行线索都没有。
            // 这里只加「出错才跑」的续体，正常路径不受影响。
            enrichment.ContinueWith(
                delegate(Task finished)
                {
                    Exception error = finished.Exception == null ? null : finished.Exception.GetBaseException();
                    if (_log != null)
                    {
                        _log.Error("元数据核查在后台异常退出（本次结果可能不完整）："
                                   + (error == null ? "未知错误" : error.Message), error);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private void OnMetadataBatch(MetadataBatch batch, CancellationTokenSource owner)
        {
            if (batch == null)
            {
                return;
            }

            if (batch.Changes != null && batch.Changes.Count > 0)
            {
                for (int i = 0; i < batch.Changes.Count; i++)
                {
                    MetadataChange change = batch.Changes[i];
                    if (change == null)
                    {
                        continue;
                    }

                    ApplyMetadataChange(change);
                    _enrichChanged++;

                    if (_log != null)
                    {
                        string group = change.Dep ? Groups.Dependency : Groups.Software;
                        _log.Info(Strings.LogEnrichChange(change.Name, group, change.Signal));
                    }
                }

                RecomputeCounts();
                RefreshMatched();
                RefreshStatus();
                RequestRelayout();
            }

            if (!batch.IsFinal)
            {
                return;
            }

            if (_log != null)
            {
                if (batch.Canceled)
                {
                    _log.Info(Strings.LogEnrichInterrupted(batch.Checked, _enrichChanged));
                }
                else
                {
                    _log.Info(Strings.LogEnrichDone(batch.Checked, batch.Promoted, batch.Kept, batch.Failed));
                }
            }

            // 只释放「这一轮自己的」取消源：
            // 若 _enrichCts 已经指向新一轮（说明期间发生过重扫），就只释放 owner，
            // 绝不能把新一轮的取消源一起带走（见 StartEnrichment 里的说明）。
            if (owner != null)
            {
                if (ReferenceEquals(_enrichCts, owner))
                {
                    _enrichCts = null;
                }

                try
                {
                    owner.Dispose();
                }
                catch (Exception)
                {
                    // 已被 CancelEnrichment 释放过：Dispose 可重入，这里只是兜底
                }
            }
        }

        private void ApplyMetadataChange(MetadataChange change)
        {
            for (int i = 0; i < _packages.Count; i++)
            {
                Package package = _packages[i];
                if (package == null)
                {
                    continue;
                }

                if (!string.Equals(package.PackageIdentifier, change.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                package.IsDependency = change.Dep;
            }
        }

        private void CancelEnrichment()
        {
            CancellationTokenSource cts = _enrichCts;
            if (cts == null)
            {
                return;
            }

            _enrichCts = null;
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch (Exception)
            {
                // 取消/释放失败不影响扫描
            }
        }

        // ══════════════════════════ 筛选 ══════════════════════════

        /// <summary>切换分组胶囊（"" = 全部）。</summary>
        public void SelectCategory(string category)
        {
            string next = category ?? string.Empty;
            if (!string.IsNullOrEmpty(next) && !Groups.IsKnown(next))
            {
                next = string.Empty;
            }

            if (string.Equals(_activeCategory, next, StringComparison.Ordinal))
            {
                return;
            }

            _activeCategory = next;
            OnPropertyChanged("ActiveCategory");
            RefreshMatched();
            RefreshStatus();
            RequestRelayout();
        }

        /// <summary>清除全部筛选（搜索词 + 分组 + 仅看已选）。</summary>
        public void ClearFilters()
        {
            bool changed = false;

            if (_searchText.Length > 0)
            {
                _searchText = string.Empty;
                OnPropertyChanged("SearchText");
                changed = true;
            }

            if (_activeCategory.Length > 0)
            {
                _activeCategory = string.Empty;
                OnPropertyChanged("ActiveCategory");
                changed = true;
            }

            if (_onlySelected)
            {
                _onlySelected = false;
                OnPropertyChanged("OnlySelected");
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            RefreshMatched();
            RefreshStatus();
            RequestRelayout();
        }

        /// <summary>是否有任何筛选条件（决定批量操作的范围与底栏文案）。</summary>
        public bool HasActiveFilters()
        {
            return _onlySelected
                   || _activeCategory.Length > 0
                   || (_searchText ?? string.Empty).Trim().Length > 0;
        }

        /// <summary>当前可见的包（按分组/搜索/仅看已选过滤，未排序）。</summary>
        public IList<Package> VisiblePackages()
        {
            IList<SearchToken> tokens = SearchQuery.Tokenize(_searchText);
            var result = new List<Package>();

            for (int i = 0; i < _packages.Count; i++)
            {
                Package package = _packages[i];
                if (package == null)
                {
                    continue;
                }

                if (_activeCategory.Length > 0
                    && !string.Equals(Groups.GroupOf(package), _activeCategory, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!SearchQuery.Matches(package, tokens))
                {
                    continue;
                }

                if (_onlySelected && !(package.Reinstallable && IsChecked(package.Key)))
                {
                    continue;
                }

                result.Add(package);
            }

            return result;
        }

        // ══════════════════════════ 勾选 ══════════════════════════

        /// <summary>某个包是否已勾选。</summary>
        public bool IsChecked(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            bool value;
            return _checked.TryGetValue(key, out value) && value;
        }

        /// <summary>行点击/空格：切换单个包的勾选。不可重装的行直接忽略。</summary>
        public void ToggleRow(Package package)
        {
            if (package == null || !package.Reinstallable)
            {
                return;
            }

            bool next = !IsChecked(package.Key);
            _checked[package.Key] = next;
            MarkSelectionDirty();

            RecomputeCounts();

            // 底栏数字、统计卡、分组胶囊必须**立刻**跟上（只改一个勾选不值得重建整表），
            // 而可见集合是否变化要看有没有筛选：有筛选时该行可能整体消失，必须重建
            RaiseCountsInvalidated();

            if (_onlySelected || HasActiveFilters())
            {
                RefreshMatched();
                RequestRelayout();
            }

            RefreshStatus();
        }

        /// <summary>
        /// 全选 / 全不选。<c>select=true</c> 全选，false 全不选。
        /// 范围：有任一筛选时只影响当前可见行；无筛选时等同全部可重装包。
        /// </summary>
        public void SetAll(bool select)
        {
            IList<Package> targets = VisiblePackages();
            int count = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                Package package = targets[i];
                if (package == null || !package.Reinstallable)
                {
                    continue;
                }

                _checked[package.Key] = select;
                count++;
            }

            AfterBatchChange();
            if (_shell != null)
            {
                _shell.Flash(Strings.FlashSelectAll(select, count));
            }
        }

        /// <summary>反选当前可见行。</summary>
        public void Invert()
        {
            IList<Package> targets = VisiblePackages();
            int count = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                Package package = targets[i];
                if (package == null || !package.Reinstallable)
                {
                    continue;
                }

                _checked[package.Key] = !IsChecked(package.Key);
                count++;
            }

            AfterBatchChange();
            if (_shell != null)
            {
                _shell.Flash(Strings.FlashInvert(count));
            }
        }

        /// <summary>分组头的「全选本组 / 全不选」。</summary>
        public void SelectGroup(string group, bool select)
        {
            if (string.IsNullOrEmpty(group))
            {
                return;
            }

            int count = 0;
            for (int i = 0; i < _packages.Count; i++)
            {
                Package package = _packages[i];
                if (package == null || !package.Reinstallable)
                {
                    continue;
                }

                if (!string.Equals(Groups.GroupOf(package), group, StringComparison.Ordinal))
                {
                    continue;
                }

                _checked[package.Key] = select;
                count++;
            }

            AfterBatchChange();
            if (_shell != null)
            {
                _shell.Flash(Strings.FlashSelectAll(select, count));
            }
        }

        private void AfterBatchChange()
        {
            MarkSelectionDirty();
            RecomputeCounts();
            RefreshMatched();
            RefreshStatus();

            // 统计卡/底栏/胶囊与列表必须一起刷新，否则会出现「列表全勾上了但底栏还是 0」
            RaiseCountsInvalidated();

            // 批量改动影响每一个行的勾选态，只能整表重建
            RequestRelayout();
        }

        /// <summary>分组是否展开。</summary>
        public bool IsGroupExpanded(string group)
        {
            return !_collapsed.Contains(group ?? string.Empty);
        }

        /// <summary>
        /// 折叠/展开分组（分组头整行点击），返回折叠后是否展开。
        /// **刻意不触发重排**：分组折叠只是把行容器藏起来，控件本身一个都不用重建。
        /// 之前这里走 <c>RequestRelayout()</c>，于是每次点分组头都要把 95 行控件全部销毁重建，
        /// 点击到画面响应之间能明显看到一段空档。
        /// </summary>
        public bool ToggleGroup(string group)
        {
            string key = group ?? string.Empty;
            if (key.Length == 0)
            {
                return true;
            }

            if (!_collapsed.Remove(key))
            {
                _collapsed.Add(key);
            }

            return !_collapsed.Contains(key);
        }

        /// <summary>给 ListPresenter 用的展开表（只含三个已知分组）。</summary>
        public IDictionary<string, bool> BuildExpandedMap()
        {
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int i = 0; i < Groups.Order.Length; i++)
            {
                map[Groups.Order[i]] = IsGroupExpanded(Groups.Order[i]);
            }

            return map;
        }

        /// <summary>把已勾选的可重装包取出来（导出用）。</summary>
        public IList<Package> SelectedPackages()
        {
            var result = new List<Package>();
            for (int i = 0; i < _packages.Count; i++)
            {
                Package package = _packages[i];
                if (package != null && package.Reinstallable && IsChecked(package.Key))
                {
                    result.Add(package);
                }
            }

            return result;
        }

        /// <summary>全部包（导出时用于生成「无法自动重装清单」）。</summary>
        public IList<Package> AllPackages()
        {
            return new List<Package>(_packages);
        }

        // ══════════════════════════ 计数与状态 ══════════════════════════

        /// <summary>重算计数并通知界面（勾选变化、元数据改判、扫描完成都会调）。</summary>
        public void RefreshCounts()
        {
            RecomputeCounts();
            RaiseCountsInvalidated();
        }

        /// <summary>只刷新底栏文案（flash 结束后回落用）。</summary>
        public void RefreshStatus()
        {
            OnPropertyChanged("StatusText");
        }

        /// <summary>写入一条 flash 临时文案（由 View 在 2200ms 后调 ClearStatusOverride）。</summary>
        public void SetStatusOverride(string text)
        {
            _statusOverride = string.IsNullOrEmpty(text) ? null : text;
            OnPropertyChanged("StatusText");
        }

        /// <summary>取消 flash 覆盖，回到正常状态文案。</summary>
        public void ClearStatusOverride()
        {
            if (_statusOverride == null)
            {
                return;
            }

            _statusOverride = null;
            OnPropertyChanged("StatusText");
        }

        /// <summary>立即把勾选表落盘（关闭窗口前调用，别等 300ms 防抖）。</summary>
        public void FlushSelection()
        {
            Dictionary<string, bool> snapshot;
            lock (_saveSync)
            {
                if (_saveTimer != null)
                {
                    _saveTimer.Dispose();
                    _saveTimer = null;
                }

                if (!_saveDirty)
                {
                    return;
                }

                _saveDirty = false;
                snapshot = _saveSnapshot;
                _saveSnapshot = null;
            }

            if (snapshot != null && _selectionStore != null)
            {
                _selectionStore.Save(snapshot);
            }
        }

        /// <summary>把所有计时器/取消源停掉（窗口关闭时调用）。</summary>
        public void Shutdown()
        {
            CancelEnrichment();

            CancellationTokenSource cts = _scanCts;
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (Exception)
                {
                    // 忽略
                }
            }

            FlushSelection();
        }

        private void RecomputeCounts()
        {
            int total = 0;
            int reinstallable = 0;
            int dependency = 0;
            int unsupported = 0;
            int selected = 0;

            for (int i = 0; i < _packages.Count; i++)
            {
                Package package = _packages[i];
                if (package == null)
                {
                    continue;
                }

                total++;
                if (!package.Reinstallable)
                {
                    unsupported++;
                    continue;
                }

                reinstallable++;
                if (package.IsDependency)
                {
                    dependency++;
                }

                if (IsChecked(package.Key))
                {
                    selected++;
                }
            }

            _totalCount = total;
            _reinstallableCount = reinstallable;
            _dependencyCount = dependency;
            _unsupportedCount = unsupported;
            _selectedCount = selected;

            OnPropertyChanged("TotalCount");
            OnPropertyChanged("ReinstallableCount");
            OnPropertyChanged("SoftwareCount");
            OnPropertyChanged("DependencyCount");
            OnPropertyChanged("UnsupportedCount");
            OnPropertyChanged("SelectedCount");
            OnPropertyChanged("SelectedSuffixText");
            OnPropertyChanged("CanExport");

            RaiseStateDependentCanExecute();
        }

        private void RaiseCountsInvalidated()
        {
            OnPropertyChanged("SelectedCount");
            OnPropertyChanged("SelectedSuffixText");
            OnPropertyChanged("CanExport");

            Action handler = CountsInvalidated;
            if (handler != null)
            {
                handler();
            }
        }

        private void RefreshMatched()
        {
            _matchedCount = VisiblePackages().Count;
            OnPropertyChanged("MatchedCount");
            OnPropertyChanged("ShowEmpty");
            OnPropertyChanged("ShowSkeleton");
        }

        private void RequestRelayout()
        {
            OnPropertyChanged("ShowEmpty");
            OnPropertyChanged("ShowSkeleton");

            if (_shell != null)
            {
                _shell.RequestBackgroundRelayout();
            }
        }

        private void SetState(ViewState state)
        {
            _state = state;

            OnPropertyChanged("State");
            OnPropertyChanged("StatusText");
            OnPropertyChanged("SubTitle");
            OnPropertyChanged("ScanButtonText");
            OnPropertyChanged("IsBatchEnabled");
            OnPropertyChanged("IsSearchEnabled");
            OnPropertyChanged("CanExport");
            OnPropertyChanged("ShowSkeleton");
            OnPropertyChanged("ShowEmpty");

            RaiseStateDependentCanExecute();

            Action<ViewState> handler = StateChanged;
            if (handler != null)
            {
                handler(state);
            }
        }

        private bool IsReady()
        {
            return _state == ViewState.Ready;
        }

        private void RaiseStateDependentCanExecute()
        {
            if (ScanOrCancelCommand != null)
            {
                ScanOrCancelCommand.RaiseCanExecuteChanged();
            }

            if (SelectAllCommand != null)
            {
                SelectAllCommand.RaiseCanExecuteChanged();
            }

            if (SelectNoneCommand != null)
            {
                SelectNoneCommand.RaiseCanExecuteChanged();
            }

            if (InvertCommand != null)
            {
                InvertCommand.RaiseCanExecuteChanged();
            }

            if (ExportCommand != null)
            {
                ExportCommand.RaiseCanExecuteChanged();
            }
        }

        private string BuildStatusText()
        {
            if (_state == ViewState.Scanning)
            {
                return Strings.StatusScanning(_elapsedSeconds);
            }

            if (_state == ViewState.NoWinget)
            {
                return Strings.StatusNoWinget;
            }

            if (_state == ViewState.Boot)
            {
                return Strings.StatusBoot;
            }

            if (_totalCount == 0)
            {
                if (_state == ViewState.ScanFailed)
                {
                    return Strings.StatusScanFailed;
                }

                return Strings.StatusBoot;
            }

            if (HasActiveFilters())
            {
                return Strings.StatusFiltered(_matchedCount, _totalCount, _onlySelected);
            }

            return Strings.StatusIdle(SoftwareCount, _dependencyCount, _unsupportedCount);
        }

        /// <summary>
        /// 清掉「已卸载软件的残留勾选」：换机/卸载后 selection.json 里会留着已经不存在的包 ID，
        /// 不清会让「已勾选 N 项」比列表里能看到的项多。与原版一致，清了要记一条日志。
        /// </summary>
        private void PurgeSelection()
        {
            var alive = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _packages.Count; i++)
            {
                Package package = _packages[i];
                if (package != null && package.Reinstallable)
                {
                    alive.Add(package.Key);
                }
            }

            var dead = new List<string>();
            foreach (KeyValuePair<string, bool> pair in _checked)
            {
                if (!alive.Contains(pair.Key))
                {
                    dead.Add(pair.Key);
                }
            }

            if (dead.Count == 0)
            {
                return;
            }

            for (int i = 0; i < dead.Count; i++)
            {
                _checked.Remove(dead[i]);
            }

            MarkSelectionDirty();

            if (_log != null)
            {
                _log.Info(Strings.LogSelectionPurged(dead.Count));
            }
        }

        /// <summary>
        /// 勾选变化后 300ms 落盘。快照在**调用线程（UI 线程）**上拍好，
        /// 计时器线程只负责写文件，避免两边同时遍历同一个字典。
        /// </summary>
        private void MarkSelectionDirty()
        {
            var snapshot = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, bool> pair in _checked)
            {
                // 只存 true 的项：文件更小，且读取侧本来就只认 true
                if (pair.Value)
                {
                    snapshot[pair.Key] = true;
                }
            }

            lock (_saveSync)
            {
                _saveSnapshot = snapshot;
                _saveDirty = true;

                if (_saveTimer == null)
                {
                    _saveTimer = new Timer(OnSaveTimer, null, Metrics.DebounceSaveMs, Timeout.Infinite);
                }
                else
                {
                    _saveTimer.Change(Metrics.DebounceSaveMs, Timeout.Infinite);
                }
            }
        }

        private void OnSaveTimer(object state)
        {
            FlushSelection();
        }
    }
}
