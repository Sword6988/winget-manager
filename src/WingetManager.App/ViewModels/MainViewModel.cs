using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WingetManager.AppServices;
using WingetManager.Infra;
using WingetManager.Models;
using WingetManager.Persistence;
using WingetManager.Services;

namespace WingetManager.ViewModels;

/// <summary>
/// 主窗口编排中枢（对应 ARCHITECTURE §3 <c>MainViewModel</c> / §4 时序图）。
///
/// 职责边界：所有跨线程与状态迁移在这里收口，窗口只做「渲染 + 取路径 + 弹框」。
/// 关键语义（全部对齐 Python 版）：
/// <list type="number">
/// <item>扫描与取消：同一按钮两态，取消在 0.2s 内真正 kill 掉 winget 子进程；</item>
/// <item>勾选：以 <see cref="SelectionBook"/> 为唯一真相，行对象随时可丢弃重建；</item>
/// <item>批量操作只作用于「当前可见且可勾选」的行；</item>
/// <item>后台富化改判按批应用，一批只重建一次列表；</item>
/// <item>勾选即时落盘 selection.json，损坏回退为空。</item>
/// </list>
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>扫描计时刷新间隔（毫秒）。</summary>
    private const int ElapsedTickMilliseconds = 200;

    /// <summary>未找到 winget 时的兜底说明。</summary>
    private const string DefaultWingetMessage =
        "未找到 winget.exe。请先安装「应用安装程序 / App Installer」"
        + "（https://aka.ms/getwinget），或通过环境变量 WPM_WINGET_EXE 指定路径。";

    /// <summary>App Installer 的商店页面地址（点按钮打开）。</summary>
    private const string AppInstallerStoreUri = "ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1";

    private readonly IMainView _view;
    private readonly LogService _log;
    private readonly RuntimeCache _cache;
    private readonly CatalogSnapshotStore _snapshotStore;
    private readonly SelectionStore _selectionStore;
    private readonly SettingsStore _settings;
    private readonly ExportService _export;

    private readonly List<Package> _packages = new();

    /// <summary>
    /// 勾选真相集合。界面上的复选框、组头文案、「已选 N 个」、导出清单全部由它派生；
    /// 行对象（<see cref="PackageRow"/>）只是可丢弃的投影。
    ///
    /// 只经由 <see cref="SelectionBook"/> 的写方法变更——历史 bug 就出在「批量勾选只改了行、
    /// 没改集合」，重建列表后勾选全部消失。
    /// </summary>
    private readonly SelectionBook _selected = new();

    private AppConfig _config;
    private WingetRepository _repository;
    private CatalogService _catalog;
    private EnrichmentWorker _enricher;

    private CancellationTokenSource? _scanCts;
    private DispatcherQueueTimer? _elapsedTimer;
    private DateTime _scanStartedAt;
    private CatalogSnapshot? _snapshot;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// 创建编排中枢（构造即完成装配，界面 <c>Loaded</c> 时调 <see cref="InitialScanAsync"/>）。
    /// </summary>
    /// <param name="view">窗口能力（取导出路径 / 弹提示）。</param>
    /// <param name="log">日志服务；null 用共享实例。</param>
    public MainViewModel(IMainView view, LogService? log = null)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _log = log ?? LogService.Shared;

        _cache = new RuntimeCache(AppPaths.MetadataCacheFile, _log);
        _snapshotStore = new CatalogSnapshotStore(AppPaths.CatalogCacheFile, _log);
        _selectionStore = new SelectionStore(AppPaths.SelectionFile, _log);
        _settings = SettingsStore.CreateDefault(_log);
        _export = new ExportService(_log);

        _config = LoadConfigSafe();
        _repository = new WingetRepository(_config, _log);
        _catalog = new CatalogService(_config, _repository, _cache, _log, _snapshotStore);
        _enricher = CreateEnricher();

        ShowGuide = !_config.HasWinget;
        WingetErrorText = _config.HasWinget ? string.Empty : _config.WingetError ?? DefaultWingetMessage;
        StatusText = _config.HasWinget ? "准备就绪，正在扫描本机软件…" : WingetErrorText;

        RestoreSelection();
        ActiveGroup = NormalizeGroup(_settings.GetLastCategory());

        _initialized = true;
        RebuildGroups();
        UpdateCounts();

        // 冷启动秒开：有落盘快照就先渲染（毫秒级），InitialScanAsync 里再后台静默刷新
        TryRestoreSnapshotFromDisk();
    }

    /// <summary>
    /// 列表条目（组头/行拍平后的扁平集合，<c>ListView.ItemsSource</c> 的数据源）。
    /// 不用 <c>CollectionViewSource</c> 分组的原因见 <see cref="ListEntry"/> 的注释（AOT bug #10881）。
    /// </summary>
    public ObservableCollection<ListEntry> Entries { get; } = new();

    /// <summary>当前的三个分组 VM（仅内部编排用；组对象不再直接交给视图）。</summary>
    private readonly List<PackageGroup> _groups = new();

    /// <summary>搜索框文本（多关键词 AND，支持 id:/name: 前缀）。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>当前分组胶囊（全部 / 软件 / 运行库 / winget 不支持）。</summary>
    [ObservableProperty]
    private string _activeGroup = PackageGroups.All;

    /// <summary>仅看已选（只在本次会话生效，P0-10）。</summary>
    [ObservableProperty]
    private bool _onlySelected;

    /// <summary>是否正在扫描（决定按钮文案与进度条动画）。</summary>
    [ObservableProperty]
    private bool _isScanning;

    /// <summary>
    /// 是否在后台静默刷新（冷启动已渲染落盘快照时的补扫）。
    /// 与 <see cref="IsScanning"/> 分开：刷新期间不打扰列表交互，只在进度条区域给个轻提示。
    /// </summary>
    [ObservableProperty]
    private bool _isBackgroundRefreshing;

    /// <summary>底部状态文字。</summary>
    [ObservableProperty]
    private string _statusText = "准备就绪。";

    /// <summary>扫描已用时间提示（扫描中每 200ms 刷新）。</summary>
    [ObservableProperty]
    private string _elapsedText = string.Empty;

    /// <summary>未找到 winget 的说明（缺 winget 引导页展示）。</summary>
    [ObservableProperty]
    private string _wingetErrorText = string.Empty;

    /// <summary>是否切到「缺 winget」引导页（P0-15：不退出程序）。</summary>
    [ObservableProperty]
    private bool _showGuide;

    /// <summary>是否正在导出（防重入）。</summary>
    [ObservableProperty]
    private bool _isExporting;

    /// <summary>统计卡：总数。</summary>
    [ObservableProperty]
    private string _totalText = "0";

    /// <summary>统计卡：软件数。</summary>
    [ObservableProperty]
    private string _softwareText = "0";

    /// <summary>统计卡：运行库数。</summary>
    [ObservableProperty]
    private string _dependencyText = "0";

    /// <summary>统计卡：winget 不支持数。</summary>
    [ObservableProperty]
    private string _unsupportedText = "0";

    /// <summary>已选数量文案。</summary>
    [ObservableProperty]
    private string _selectedCountText = "已选 0 个";

    /// <summary>当前是否有可导出的勾选项。</summary>
    [ObservableProperty]
    private bool _hasSelection;

    /// <summary>是否显示空态提示（无结果 / 仅看已选为空 / 尚未扫描）。</summary>
    [ObservableProperty]
    private bool _showEmptyState;

    /// <summary>空态提示文案。</summary>
    [ObservableProperty]
    private string _emptyStateText = "正在扫描本机软件…";

    /// <summary>导出按钮是否可用（导出中禁用，防重复点击）。</summary>
    public bool CanExport => !IsExporting;

    /// <summary>扫描/取消按钮文案。</summary>
    public string ScanButtonText => IsScanning ? "取消扫描" : _snapshot is null ? "开始扫描" : "重新扫描";

    /// <summary>已探测到的 winget 版本。</summary>
    public string WingetVersionText
    {
        get
        {
            var version = _snapshot?.WingetVersion ?? string.Empty;
            return version.Length == 0 || string.Equals(version, "unknown", StringComparison.OrdinalIgnoreCase)
                ? "winget 版本未知"
                : "winget " + version;
        }
    }

    /// <summary>分组胶囊的四个选项（XAML 里逐个 SelectorBarItem 声明，这里是唯一真相）。</summary>
    public IReadOnlyList<string> GroupOptions { get; } = new[]
    {
        PackageGroups.All, PackageGroups.Software, PackageGroups.Dependency, PackageGroups.Unsupported,
    };

    /// <summary>
    /// 首屏扫描（窗口 Loaded 时调用一次）。缺 winget 时只切引导页，不抛异常、不退出。
    ///
    /// 冷启动秒开：构造函数里已经渲染了落盘快照的话，这里直接发起后台静默刷新
    /// （force=true 全量重扫，完成后用新快照替换列表并回写 catalog-cache.json）；
    /// 无缓存（首次运行）仍走原有完整扫描路径。
    /// </summary>
    /// <returns>任务。</returns>
    public async Task InitialScanAsync()
    {
        if (!EnsureWinget())
        {
            ShowGuide = true;
            StatusText = WingetErrorText;
            return;
        }

        if (_snapshot is not null)
        {
            IsBackgroundRefreshing = true;
            try
            {
                await ScanAsync(force: true, silentRefresh: true).ConfigureAwait(true);
            }
            finally
            {
                IsBackgroundRefreshing = false;
            }

            return;
        }

        await ScanAsync(force: true).ConfigureAwait(true);
    }

    /// <summary>
    /// 扫描（force=true 忽略 TTL 缓存）。取消与失败都只改状态文字，不弹窗、不崩。
    ///
    /// 防重入：后台静默刷新期间用户点「重新扫描」直接 return（按钮此时是「取消扫描」，
    /// 再点是取消），<see cref="WingetRepository"/> 内部的 SemaphoreSlim 也会拦住并发 winget。
    /// </summary>
    /// <param name="force">是否强制重新扫描。</param>
    /// <param name="silentRefresh">
    /// true = 后台静默刷新：不覆盖状态文字里「已加载上次扫描结果」的提示，
    /// 失败/取消时保留已渲染的旧快照，不切引导页。
    /// </param>
    /// <returns>任务。</returns>
    public async Task ScanAsync(bool force, bool silentRefresh = false)
    {
        if (_disposed || IsScanning)
        {
            return;
        }

        if (!EnsureWinget())
        {
            ShowGuide = true;
            StatusText = WingetErrorText;
            return;
        }

        _scanCts?.Dispose();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        // 新扫描抢占：上一轮富化的 show 会在 0.2s 内被 kill，没查完的缓存外 ID 下轮续上
        _enricher.Cancel();

        IsScanning = true;
        OnPropertyChanged(nameof(ScanButtonText));
        ShowGuide = false;
        ElapsedText = "已用 0.0s · 点「取消扫描」可中断";
        if (!silentRefresh)
        {
            // 静默刷新时保留「已加载上次扫描结果（…），正在后台刷新…」的提示
            StatusText = "正在扫描本机软件…";
        }

        _scanStartedAt = DateTime.UtcNow;
        StartElapsedTimer();

        try
        {
            var versionProbe = await Task.Run(() => _repository.GetVersionAsync(cts.Token), cts.Token)
                .ConfigureAwait(true);
            var snapshot = await Task.Run(
                () => _catalog.GetCatalogAsync(force, cts.Token, versionProbe),
                cts.Token).ConfigureAwait(true);

            cts.Token.ThrowIfCancellationRequested();
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
            StatusText = silentRefresh
                ? "已取消后台刷新，当前显示的是上次扫描结果。"
                : "已取消扫描。";
            _log.Info("用户取消了扫描");
        }
        catch (WingetNotFoundException ex)
        {
            if (silentRefresh)
            {
                // 已经渲染了旧快照，别把用户切到引导页；列表留着，文字说明即可
                StatusText = "后台刷新失败：未找到 winget，已显示上次扫描结果。";
                _log.Warn("后台刷新失败（未找到 winget）：{0}", ex.Detail);
            }
            else
            {
                ShowGuide = true;
                WingetErrorText = ex.UserMessage;
                StatusText = ex.UserMessage;
                _log.Warn("扫描失败（未找到 winget）：{0}", ex.Detail);
            }
        }
        catch (AppException ex)
        {
            StatusText = silentRefresh ? "后台刷新失败，已显示上次扫描结果。" : ex.UserMessage;
            _log.Error("扫描失败：{0}", ex.Detail);
        }
        catch (Exception ex)
        {
            StatusText = silentRefresh ? "后台刷新失败，已显示上次扫描结果。" : "扫描失败，请查看日志。";
            _log.Error("扫描异常：{0}", ex);
        }
        finally
        {
            StopElapsedTimer();
            ElapsedText = string.Empty;
            IsScanning = false;
            OnPropertyChanged(nameof(ScanButtonText));

            if (ReferenceEquals(_scanCts, cts))
            {
                _scanCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// 把快照应用到界面：合并勾选 → 重建分组 → 更新统计 → 启动后台元数据核查。
    /// </summary>
    /// <param name="snapshot">扫描结果。</param>
    /// <param name="startEnrichment">
    /// 是否启动后台元数据核查；冷启动渲染落盘快照时传 false——紧随其后的后台刷新
    /// 会用新快照重新走一遍完整流程，先起富化只会被 <see cref="ScanAsync"/> 立刻取消。
    /// </param>
    /// <param name="pruneSelection">
    /// 是否按当前快照剪枝已选集合。冷启动渲染落盘快照时<b>必须传 false</b>：缓存可能是
    /// 过期数据，按它剪枝会把「快照里没有但本机还在」的已选项从内存里删掉，用户一旦
    /// 勾选任何一行就会把剪枝结果落盘 selection.json（静默丢数据）。剪枝一律等后台
    /// 刷新产出新快照后再做——启动路径对用户数据保持只读。
    /// </param>
    public void ApplySnapshot(CatalogSnapshot snapshot, bool startEnrichment = true, bool pruneSelection = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshot = snapshot;

        _packages.Clear();
        _packages.AddRange(snapshot.Packages);

        if (pruneSelection)
        {
            // 勾选合并：只保留「本机当前存在且可重装」的 ID，
            // 卸掉的软件、或变成 winget 不支持的项不该继续躺在导出清单里
            _selected.PruneTo(_packages.Where(p => p.Reinstallable).Select(p => p.PackageIdentifier));

            // 「仅看已选」不持久化，但重扫后勾选为空时必须自动关闭，否则界面看起来是空的（P0-10）
            if (_selected.IsEmpty && OnlySelected)
            {
                OnlySelected = false;
            }
        }

        // RebuildGroups 内部已调 UpdateCounts，这里不再重复调用（审计 F7）
        RebuildGroups();

        // 剪枝可能改变了勾选集合，计数与导出口径要立刻跟上（否则底栏会停留在剪枝前的数字）
        RefreshSelectionState();
        OnPropertyChanged(nameof(WingetVersionText));

        var duration = snapshot.DurationMs / 1000.0;
        StatusText = string.Format(
            CultureInfo.InvariantCulture,
            "扫描完成：共 {0} 个，可自动重装 {1} 个（用时 {2:F1}s）。",
            _packages.Count,
            _packages.Count(p => p.Reinstallable),
            duration);

        if (startEnrichment)
        {
            StartEnrichment(snapshot);
        }
    }

    /// <summary>
    /// 按「搜索词 + 分组胶囊 + 仅看已选」重建列表（空组不产生组头）。
    ///
    /// 整体重建而不做差异更新：一次扫描几百上千行，逐条 diff 的复杂度换不来体验，
    /// 反而容易在虚拟化容器复用上踩坑（<c>x:Bind</c> 一次性绑定 + 容器回收）。
    ///
    /// 条目按「组头 → 该组全部行」的顺序拍平进 <see cref="Entries"/>；
    /// 组对象进 <see cref="_groups"/>（仅编排用，不进视图）。
    /// </summary>
    public void RebuildGroups()
    {
        if (!_initialized)
        {
            return;
        }

        var predicate = FilterEngine.BuildPredicate(
            SearchText, ActiveGroup, OnlySelected,
            package => _selected.Contains(package.PackageIdentifier));

        var grouped = new Dictionary<string, List<PackageRow>>(StringComparer.Ordinal);
        foreach (var groupName in PackageGroups.Order)
        {
            grouped[groupName] = new List<PackageRow>();
        }

        foreach (var package in _packages)
        {
            if (!predicate(package))
            {
                continue;
            }

            // 行的初始勾选状态只从真相集合取：行是可丢弃投影，重建后必须能还原
            var row = new PackageRow(package, _selected.Contains(package.PackageIdentifier));
            grouped[PackageGroups.Of(package)].Add(row);
        }

        _groups.Clear();
        Entries.Clear();
        foreach (var groupName in PackageGroups.Order)
        {
            var rows = grouped[groupName];
            if (rows.Count == 0)
            {
                continue;
            }

            var group = new PackageGroup(groupName, rows, ToggleGroupSelection);
            _groups.Add(group);
            Entries.Add(new GroupHeaderEntry(group));
            foreach (var row in rows)
            {
                Entries.Add(new PackageRowEntry(row));
            }
        }

        NotifyGroupsSelectionChanged();
        UpdateCounts();
        UpdateEmptyState();
    }

    /// <summary>
    /// 组头「全选本组 / 全不选」：组内全勾则全不选，否则全选。
    /// </summary>
    /// <param name="groupName">组名。</param>
    public void ToggleGroupSelection(string groupName)
    {
        var group = _groups.FirstOrDefault(g => string.Equals(g.Title, groupName, StringComparison.Ordinal));
        if (group is null)
        {
            return;
        }

        var selectable = group.Where(row => row.IsSelectable).ToList();
        if (selectable.Count == 0)
        {
            return;
        }

        var target = !selectable.All(row => row.IsChecked);
        SetRowsChecked(selectable, target);
    }

    /// <summary>取消扫描（0.2s 内真正终止 winget 子进程，P0-13）。</summary>
    public void CancelScan()
    {
        try
        {
            _scanCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 批量设置勾选（「全选」「反选」「全选本组」的唯一落点）。
    ///
    /// 同时改两处：行对象（界面投影）与真相集合（<see cref="_selected"/>）。
    /// 两者必须一起改——只改行不改集合，界面看着勾上了，一切换分组重建列表就全没了
    /// （v3.0.0 的 bug 就是这个）。
    /// </summary>
    /// <param name="rows">目标行。</param>
    /// <param name="value">目标状态。</param>
    private void SetRowsChecked(IReadOnlyList<PackageRow> rows, bool value)
    {
        var ids = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            if (!row.IsSelectable)
            {
                continue;
            }

            row.IsChecked = value;
            ids.Add(row.PackageIdentifier);
        }

        _selected.SetMany(ids, value);
        PersistSelection();
        RefreshSelectionState();
    }

    /// <summary>
    /// 单行勾选（界面点击的唯一入口）。
    ///
    /// 界面用 OneWay 绑定 + <c>Click</c> 显式上报，而不是 TwoWay 绑定：
    /// <c>CheckBox.IsChecked</c> 是 <c>bool?</c>，而列表虚拟化会回收容器并在重建时
    /// 重置容器状态，TwoWay 可能把「非用户意图」的 false/null 写回行对象，
    /// 直接表现为「切换分组后勾选丢失」。让数据流单向（界面只读、点击显式调用）
    /// 从结构上消除这类误写。
    /// </summary>
    /// <param name="row">目标行。</param>
    /// <param name="value">目标状态。</param>
    /// <returns>行是否可勾选；不可勾选时调用方应把界面状态还原。</returns>
    public bool TrySetRowChecked(PackageRow row, bool value)
    {
        if (row is null || !row.IsSelectable)
        {
            return false;
        }

        if (row.IsChecked == value)
        {
            return true; // 已是目标状态：界面与数据一致，无需写盘
        }

        row.IsChecked = value;
        if (value)
        {
            _selected.Add(row.PackageIdentifier);
        }
        else
        {
            _selected.Remove(row.PackageIdentifier);
        }

        PersistSelection();
        RefreshSelectionState();

        // 「仅看已选」下取消勾选 -> 行应立即消失。必须延后一拍，
        // 不能在 CheckBox 的点击回调里同步重建集合（会重入虚拟化容器）。
        if (OnlySelected && !value)
        {
            UiDispatch.Post(RebuildGroups);
        }

        return true;
    }

    /// <summary>勾选落盘（小文件、原子写；失败只在日志里留痕，不影响交互）。</summary>
    private void PersistSelection()
    {
        var snapshot = _selected.ToArray();
        _selectionStore.Save(snapshot);
    }

    /// <summary>刷新「已选 N 个」与组头按钮文案（按当前列表内的已勾选计数）。</summary>
    private void RefreshSelectionState()
    {
        // 启动渲染落盘快照的窗口里 selection.json 可能含缓存快照之外的 ID（尚未剪枝），
        // 直接用 _selected.Count 会短暂多算；按「当前列表内已勾选」计数与导出口径一致
        // （SelectedPackages 也是 _packages 交集；稳态下 _selected ⊆ _packages，两者相等）。
        var selectedCount = 0;
        foreach (var package in _packages)
        {
            if (_selected.Contains(package.PackageIdentifier))
            {
                selectedCount++;
            }
        }

        SelectedCountText = "已选 " + selectedCount.ToString(CultureInfo.InvariantCulture) + " 个";
        HasSelection = selectedCount > 0;
        NotifyGroupsSelectionChanged();
    }

    private void NotifyGroupsSelectionChanged()
    {
        foreach (var group in _groups)
        {
            group.NotifySelectionChanged();
        }
    }

    /// <summary>
    /// 冷启动秒开：读落盘快照（catalog-cache.json）并直接渲染。
    ///
    /// 只在「有 winget、有有效缓存」时生效；损坏/缺失由 <see cref="CatalogSnapshotStore"/>
    /// 返回 null，静默回退到原有完整扫描路径。渲染时跳过元数据核查（后台刷新马上会
    /// 全量重扫并重新起富化）。统计与「上次扫描时间」一律按快照里的 GeneratedAt 显示。
    ///
    /// 这里再套一层 try/catch（防御纵深）：本方法在构造函数里跑，缓存链路上任何
    /// 意外异常最多退回「无缓存完整扫描」，绝不允许打断窗口构造。
    /// </summary>
    private void TryRestoreSnapshotFromDisk()
    {
        try
        {
            if (ShowGuide)
            {
                return;
            }

            var cached = _snapshotStore.Load();
            if (cached is null)
            {
                return;
            }

            ApplySnapshot(cached, startEnrichment: false, pruneSelection: false);
            // 渲染缓存快照没有剪枝，这里按「列表内已勾选」重算一次计数，
            // 避免 selection.json 与缓存不同步时状态栏短暂多算
            RefreshSelectionState();
            StatusText = string.Format(
                CultureInfo.InvariantCulture,
                "已加载上次扫描结果（{0}），正在后台刷新…",
                FormatGeneratedAtDisplay(cached.GeneratedAt));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _log.Warn("加载落盘快照失败，本次启动按无缓存处理：{0}", ex);
        }
    }

    /// <summary>
    /// 把快照的 GeneratedAt（<c>2026-11-03T14:00:00+0800</c>）折成 <c>2026-11-03 14:00</c>。
    /// 解析不了就原样返回——提示文案永远不该让启动失败。
    /// </summary>
    /// <param name="generatedAt">快照生成时间原文。</param>
    /// <returns>展示用短文本。</returns>
    private static string FormatGeneratedAtDisplay(string? generatedAt)
    {
        if (!string.IsNullOrEmpty(generatedAt) && generatedAt.Length >= 16
            && generatedAt[10] == 'T' && char.IsDigit(generatedAt[0]) && char.IsDigit(generatedAt[11]))
        {
            return generatedAt[..10] + " " + generatedAt[11..16];
        }

        return generatedAt ?? string.Empty;
    }

    /// <summary>从 selection.json 恢复勾选（损坏文件由 SelectionStore 兜底成空集）。</summary>
    private void RestoreSelection()
    {
        _selected.ReplaceAll(_selectionStore.Load());

        SelectedCountText = "已选 " + _selected.Count.ToString(CultureInfo.InvariantCulture) + " 个";
        HasSelection = _selected.Count > 0;
    }

    /// <summary>统计卡与「可自动重装」提示（口径与三组一致）。</summary>
    private void UpdateCounts()
    {
        var software = 0;
        var dependency = 0;
        var unsupported = 0;

        foreach (var package in _packages)
        {
            switch (PackageGroups.Of(package))
            {
                case PackageGroups.Software:
                    software++;
                    break;
                case PackageGroups.Dependency:
                    dependency++;
                    break;
                default:
                    unsupported++;
                    break;
            }
        }

        TotalText = _packages.Count.ToString(CultureInfo.InvariantCulture);
        SoftwareText = software.ToString(CultureInfo.InvariantCulture);
        DependencyText = dependency.ToString(CultureInfo.InvariantCulture);
        UnsupportedText = unsupported.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>启动后台元数据核查（只查「可重装 ∧ 关键词判软件 ∧ 缓存未命中」）。</summary>
    private void StartEnrichment(CatalogSnapshot snapshot)
    {
        var pending = _catalog.PendingMetadataIds(snapshot);
        if (pending.Count == 0)
        {
            return;
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keywordDependencies = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in snapshot.Packages)
        {
            names[package.PackageIdentifier] = package.Name;
            keywordDependencies[package.PackageIdentifier] =
                CatalogService.IsDependencyByKeyword(new RawPackage(package.Name, package.PackageIdentifier));
        }

        _log.Info("后台元数据核查启动：{0} 个待查", pending.Count);
        _enricher.Start(pending, names, keywordDependencies, () => IsScanning);
    }

    /// <summary>一批改判（后台线程触发 → 切 UI 线程后按批应用，P0-6）。</summary>
    private void OnMetadataChanges(IReadOnlyList<MetadataChange> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        UiDispatch.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            var changed = false;
            foreach (var change in changes)
            {
                var package = _packages.FirstOrDefault(
                    p => string.Equals(p.PackageIdentifier, change.PackageId, StringComparison.OrdinalIgnoreCase));
                if (package is null || package.IsDependency == change.IsDependency)
                {
                    continue;
                }

                package.IsDependency = change.IsDependency;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            // 一批一次重建、一次统计，绝不逐条刷新
            RebuildGroups();
        });
    }

    /// <summary>一轮核查结束（后台线程触发）。</summary>
    private void OnEnrichmentCompleted(EnrichmentSummary summary)
    {
        UiDispatch.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            _log.Info(
                "元数据核查结束：查 {0}，补漏 {1}，确认非运行库 {2}，失败 {3}，取消 {4}",
                summary.Checked, summary.Promoted, summary.Kept, summary.Failed, summary.Cancelled);
        });
    }

    private EnrichmentWorker CreateEnricher()
    {
        var worker = new EnrichmentWorker(_repository, _cache, _log);
        worker.Changes += OnMetadataChanges;
        worker.Completed += OnEnrichmentCompleted;
        return worker;
    }

    /// <summary>
    /// 确保 winget 可用。启动时没找到的话，每次扫描前再探一次——
    /// 用户可能刚装完 App Installer，不该逼他重启程序（P0-15）。
    /// </summary>
    private bool EnsureWinget()
    {
        if (_config.HasWinget)
        {
            return true;
        }

        try
        {
            _ = AppConfig.FindWinget();
        }
        catch (ConfigInvalidException ex)
        {
            WingetErrorText = ex.UserMessage;
            return false;
        }

        _config = LoadConfigSafe();
        _repository = new WingetRepository(_config, _log);
        _catalog = new CatalogService(_config, _repository, _cache, _log, _snapshotStore);
        _enricher.Changes -= OnMetadataChanges;
        _enricher.Completed -= OnEnrichmentCompleted;
        _enricher.Cancel();
        _enricher = CreateEnricher();

        ShowGuide = false;
        WingetErrorText = string.Empty;
        _log.Info("重新探测到 winget：{0}", _config.WingetExe);
        return _config.HasWinget;
    }

    /// <summary>读配置；环境变量越界不该让程序起不来，降级成默认值 + 提示。</summary>
    private AppConfig LoadConfigSafe()
    {
        try
        {
            return AppConfig.Load();
        }
        catch (ConfigInvalidException ex)
        {
            _log.Error("配置非法，降级为默认值：{0}", ex.Detail);
            return AppConfig.CreateForTests(
                string.Empty,
                AppConfig.DefaultCacheTtlSeconds,
                AppConfig.DefaultCommandTimeoutSeconds,
                ex.UserMessage);
        }
    }

    private static string NormalizeGroup(string? group)
    {
        return !string.IsNullOrEmpty(group) && PackageGroups.Order.Contains(group, StringComparer.Ordinal)
            ? group
            : PackageGroups.All;
    }

    private void StartElapsedTimer()
    {
        StopElapsedTimer();
        _elapsedTimer = UiDispatch.CreateTimer(TimeSpan.FromMilliseconds(ElapsedTickMilliseconds), UpdateElapsedText);
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        if (_elapsedTimer is null)
        {
            return;
        }

        _elapsedTimer.Stop();
        _elapsedTimer = null;
    }

    private void UpdateElapsedText()
    {
        if (!IsScanning)
        {
            return;
        }

        var seconds = (DateTime.UtcNow - _scanStartedAt).TotalSeconds;
        ElapsedText = string.Format(
            CultureInfo.InvariantCulture, "已用 {0:F1}s · 点「取消扫描」可中断", seconds);
    }

    partial void OnSearchTextChanged(string value)
    {
        RebuildGroups();
    }

    partial void OnActiveGroupChanged(string value)
    {
        if (!_initialized)
        {
            // 构造期从注册表恢复胶囊选中项，不回头再写一遍注册表
            return;
        }

        _settings.SetLastCategory(value);
        RebuildGroups();
    }

    partial void OnOnlySelectedChanged(bool value)
    {
        RebuildGroups();
    }

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(ScanButtonText));
        UpdateEmptyState();
    }

    partial void OnIsExportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExport));
    }

    partial void OnShowGuideChanged(bool value)
    {
        UpdateEmptyState();
    }

    /// <summary>
    /// 空态提示（P1-2）：把「为什么列表是空的」说清楚，别让用户以为程序卡了。
    /// </summary>
    private void UpdateEmptyState()
    {
        if (ShowGuide)
        {
            ShowEmptyState = false;
            return;
        }

        var visibleCount = 0;
        foreach (var group in _groups)
        {
            visibleCount += group.Count;
        }

        if (visibleCount > 0)
        {
            ShowEmptyState = false;
            return;
        }

        ShowEmptyState = true;
        if (IsScanning)
        {
            EmptyStateText = "正在扫描本机软件…";
        }
        else if (OnlySelected)
        {
            EmptyStateText = "「仅看已选」下没有勾选项。勾选软件后再回来，或关掉这个开关看全部。";
        }
        else if (_packages.Count == 0)
        {
            EmptyStateText = "还没有扫描结果。点右下角「开始扫描」重新枚举本机软件。";
        }
        else
        {
            EmptyStateText = "没有匹配的软件。换个关键词，或把分组切回「全部」。";
        }
    }

    // ─────────────────────────────── 命令 ───────────────────────────────

    /// <summary>「开始扫描 / 取消扫描 / 重新扫描」同一个按钮。</summary>
    [RelayCommand]
    private async Task RescanOrCancelAsync()
    {
        if (IsScanning)
        {
            CancelScan();
            return;
        }

        await ScanAsync(force: true).ConfigureAwait(true);
    }

    /// <summary>全选：只作用于当前可见且可勾选项（P0-11）。</summary>
    [RelayCommand]
    private void SelectAll()
    {
        SetRowsChecked(VisibleSelectableRows(), true);
        if (OnlySelected)
        {
            RebuildGroups();
        }
    }

    /// <summary>反选：只作用于当前可见且可勾选项（P0-11）。</summary>
    [RelayCommand]
    private void InvertSelection()
    {
        var rows = VisibleSelectableRows();
        var ids = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            row.IsChecked = !row.IsChecked;
            ids.Add(row.PackageIdentifier);
        }

        // 行与集合一起翻：集合是真相，行是投影，漏改任何一边都会在重建后暴露
        _selected.Toggle(ids);
        PersistSelection();
        RefreshSelectionState();

        if (OnlySelected)
        {
            RebuildGroups();
        }
    }

    /// <summary>清空勾选（可见范围内不做限制：清空就是清空）。</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var group in _groups)
        {
            foreach (var row in group)
            {
                row.IsChecked = false;
            }
        }

        _selected.Clear();
        PersistSelection();
        RefreshSelectionState();
        RebuildGroups();
    }

    /// <summary>导出三件套（路径由窗口给，写入由 <see cref="ExportService"/> 做）。</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsExporting)
        {
            return;
        }

        var selected = SelectedPackages();
        if (selected.Count == 0)
        {
            await _view.ShowMessageAsync(
                "还没有勾选任何软件",
                "勾选想恢复的软件后再导出。\n\n"
                + "提示：「winget 不支持」组里的软件没有可用的安装源，不会被导出。").ConfigureAwait(true);
            return;
        }

        var suggested = ExportService.ResolveDefaultExportPath(_settings.GetLastExportPath());
        var target = await _view.PickExportPathAsync(suggested).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        IsExporting = true;
        StatusText = "正在导出…";
        try
        {
            var version = _snapshot?.WingetVersion ?? string.Empty;
            var result = await _export.ExportAsync(target, selected, _packages, version).ConfigureAwait(true);

            _settings.SetLastExportPath(result.Files.Count > 0 ? result.Files[0] : target);
            StatusText = string.Format(
                CultureInfo.InvariantCulture,
                "已导出 {0} 个软件包：{1}",
                selected.Count,
                Path.GetFileName(result.Files.Count > 0 ? result.Files[0] : target));

            await _view.ShowMessageAsync("导出成功", BuildExportMessage(result, selected.Count)).ConfigureAwait(true);
        }
        catch (AppException ex)
        {
            StatusText = ex.UserMessage;
            _log.Error("导出失败：{0}", ex.Detail);
            await _view.ShowMessageAsync("导出失败", ex.UserMessage).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = "导出失败，请查看日志。";
            _log.Error("导出异常：{0}", ex);
            await _view.ShowMessageAsync("导出失败", "导出时出错，详情见日志：" + ex.Message).ConfigureAwait(true);
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>打开 Microsoft Store 的 App Installer 页面（缺 winget 引导用）。</summary>
    [RelayCommand]
    private void OpenAppInstaller()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppInstallerStoreUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.Error("打开应用商店失败：{0}", ex);
            StatusText = "无法打开应用商店，可手动访问 https://aka.ms/getwinget";
        }
    }

    /// <summary>重新探测 winget（用户刚装完 App Installer 时点）。</summary>
    [RelayCommand]
    private async Task RecheckWingetAsync()
    {
        if (EnsureWinget())
        {
            await ScanAsync(force: true).ConfigureAwait(true);
            return;
        }

        ShowGuide = true;
        StatusText = WingetErrorText;
    }

    /// <summary>勾选的包（只含可重装项，顺序与列表一致）。</summary>
    /// <returns>包列表。</returns>
    public IReadOnlyList<Package> SelectedPackages()
    {
        var result = new List<Package>(_selected.Count);
        foreach (var package in _packages)
        {
            if (package.Reinstallable && _selected.Contains(package.PackageIdentifier))
            {
                result.Add(package);
            }
        }

        return result;
    }

    private List<PackageRow> VisibleSelectableRows()
    {
        var rows = new List<PackageRow>();
        foreach (var group in _groups)
        {
            foreach (var row in group)
            {
                if (row.IsSelectable)
                {
                    rows.Add(row);
                }
            }
        }

        return rows;
    }

    private static string BuildExportMessage(ExportResult result, int selectedCount)
    {
        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, "已导出 {0} 个软件包。", selectedCount),
            string.Empty,
            string.Format(
                CultureInfo.InvariantCulture,
                "来源分布：winget {0} 个 · msstore {1} 个",
                result.WingetCount, result.MsStoreCount),
            string.Empty,
            "生成的文件：",
        };

        foreach (var file in result.Files)
        {
            lines.Add("· " + Path.GetFileName(file));
        }

        lines.Add(string.Empty);
        lines.Add("重装系统后，双击「恢复-" + BaseStem(result) + ".bat」即可批量安装。");

        if (result.MsStoreCount > 0)
        {
            lines.Add(string.Empty);
            lines.Add("注意：msstore 来源的软件可能要求先在 Microsoft Store 登录账号。");
        }

        if (result.RestoreScriptPath is null)
        {
            lines.Add(string.Empty);
            lines.Add("恢复脚本未能生成，清单本身仍然可用（winget import -i apps.json）。");
        }

        foreach (var warning in result.Warnings)
        {
            lines.Add(string.Empty);
            lines.Add("提示：" + warning);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BaseStem(ExportResult result)
    {
        if (result.Files.Count == 0)
        {
            return "apps";
        }

        return Path.GetFileNameWithoutExtension(result.Files[0]);
    }

    /// <summary>释放定时器与取消源。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        StopElapsedTimer();
        _enricher.Changes -= OnMetadataChanges;
        _enricher.Completed -= OnEnrichmentCompleted;
        _enricher.Cancel();

        _scanCts?.Dispose();
        _scanCts = null;
    }
}
