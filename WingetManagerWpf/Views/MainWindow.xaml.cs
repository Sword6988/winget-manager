using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WingetManagerWpf.Controls;
using WingetManagerWpf.Core;
using WingetManagerWpf.Models;
using WingetManagerWpf.Services;
using WingetManagerWpf.ViewModels;

namespace WingetManagerWpf.Views
{
    /// <summary>
    /// 主窗口：组合根 + 外壳。持有全部服务实例与 ViewModel，负责把「数据折叠结果」
    /// 渲染成行控件，并实现 <see cref="IMainShell"/> 里那些必须碰 WPF 的动作。
    /// <para>
    /// 这里的代码只做四件事：布局与外观、事件转译、Dispatcher 调度、系统对话框。
    /// 任何业务判断（能不能导出、算多少项、写什么文案）都在 ViewModel 里。
    /// </para>
    /// </summary>
    public partial class MainWindow : Window, IMainShell
    {
        private readonly AppConfig _config;
        private readonly AppLogger _log;
        private readonly AppSettings _settings;
        private readonly SettingsStore _settingsStore;
        private readonly SelectionStore _selectionStore;
        private readonly IWingetRepository _repo;
        private readonly PackageCatalog _catalog;
        private readonly RuntimeCache _cache;
        private readonly ScanService _scan;
        private readonly MainViewModel _vm;
        private readonly ListPresenter _presenter = new ListPresenter();

        // ── 计时器 ──
        private readonly DispatcherTimer _scanTimer;
        private readonly DispatcherTimer _flashTimer;
        private readonly DispatcherTimer _searchTimer;

        // ── 列表状态 ──
        private readonly Dictionary<string, PackageRowControl> _rowByKey =
            new Dictionary<string, PackageRowControl>(StringComparer.Ordinal);

        private readonly List<PackageRowControl> _flatRows = new List<PackageRowControl>();

        private readonly Dictionary<string, GroupSectionControl> _sectionByTitle =
            new Dictionary<string, GroupSectionControl>(StringComparer.Ordinal);

        private readonly List<GroupSectionControl> _sectionOrder = new List<GroupSectionControl>();

        // ── 吸顶头的 Y 表缓存（只有布局变化时才重算） ──
        private readonly List<double> _sectionTops = new List<double>();
        private readonly List<string> _sectionTitles = new List<string>();
        private bool _stickyDirty = true;

        /// <summary>吸顶头的位移变换（复用同一个实例，滚动时不制造垃圾）。</summary>
        private readonly TranslateTransform _stickyLift = new TranslateTransform(0, 0);

        private bool _relayoutPending;
        private bool _narrow;
        private bool _closing;

        /// <summary>关闭时等扫描收尾的上限（毫秒）。2s 是观感与「别留孤儿 winget」的折中。</summary>
        private const int ScanShutdownWaitMs = 2000;

        /// <summary>是否正在等扫描收尾（防止 PushFrame 期间重入再等一遍）。</summary>
        private bool _waitingForScanShutdown;

        private bool _firstScanStarted;
        private DateTime _scanStartedUtc;

        public MainWindow(AppConfig config, AppLogger log)
        {
            _config = config ?? AppConfig.CreateDefault();
            _log = log;

            InitializeComponent();

            // 顺序很关键：日志面板必须先订阅 AppLogger，ViewModel 构造里那几条
            // （「已恢复上次的分组筛选」等）才进得了面板
            LogPanel.AttachLogger(_log);
            LogPanel.FlashRequested += delegate(string message) { Flash(message); };

            _settingsStore = new SettingsStore(AppPaths.SettingsFile, _log);
            _settings = _settingsStore.Load();
            _selectionStore = new SelectionStore(AppPaths.SelectionFile, _log);
            _repo = new WingetRepository(_config, _log);
            // 元数据判定缓存的有效期来自配置（WPM_META_TTL_DAYS，默认 90 天，0 = 永不过期）
            _cache = new RuntimeCache(AppPaths.MetadataCacheFile, _log, _config.MetadataTtlDays);
            _catalog = new PackageCatalog(_config, _repo, _cache, _log);
            _scan = new ScanService(_catalog, _repo, _log);

            _vm = new MainViewModel(_log, _repo, _catalog, _cache, _scan, _selectionStore, _settings, this);
            _vm.CountsInvalidated += OnCountsInvalidated;
            _vm.StateChanged += OnStateChanged;

            // 筛选变化只会改文案（不触发计数/状态事件），所以底栏要直接跟 StatusText 走；
            // flash 生效期间不覆盖，否则提示会被瞬间冲掉
            _vm.PropertyChanged += OnViewModelPropertyChanged;

            _scanTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _scanTimer.Interval = TimeSpan.FromMilliseconds(Metrics.ScanTickMs);
            _scanTimer.Tick += OnScanTick;

            _flashTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _flashTimer.Interval = TimeSpan.FromMilliseconds(Metrics.FlashMs);
            _flashTimer.Tick += OnFlashTick;

            _searchTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _searchTimer.Interval = TimeSpan.FromMilliseconds(Metrics.DebounceSearchMs);
            _searchTimer.Tick += OnSearchTick;

            ApplyStaticText();
            BuildSegments();
            WireEvents();
            RestoreWindowGeometry();

            LogPanel.RestoreLayout(_settings.LogHeight, _settings.LogExpanded);

            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
            Closing += OnClosing;
            PreviewKeyDown += OnPreviewKeyDown;

            RefreshAll();
        }

        // ══════════════════════════ 初始化 ══════════════════════════

        private void ApplyStaticText()
        {
            Title = Strings.WindowTitle;
            TitleGradient.Text = Strings.WindowTitle;
            TagText.Text = Strings.AppTag;

            SearchPlaceholder.Text = Strings.SearchPlaceholder;
            SearchBox.ToolTip = Strings.SearchPlaceholder;

            ScanButton.Content = Strings.ButtonScan;
            ExportButton.Content = Strings.ButtonExport;
            InvertButton.Content = Strings.ButtonInvert;
            ClearSelectionButton.Content = "清空";

            AboutButton.ToolTip = "关于 " + Strings.WindowTitle;
            MinimizeButton.ToolTip = "最小化";
            MaximizeButton.ToolTip = "最大化";
            CloseWindowButton.ToolTip = "关闭";

            SubtitleText.Text = Strings.StatusBoot;
            StatusLabel.Text = Strings.StatusBoot;

            InitV3Chrome();
        }

        private void WireEvents()
        {
            ScanButton.Command = _vm.ScanOrCancelCommand;
            ExportButton.Command = _vm.ExportCommand;

            SearchBox.TextChanged += OnSearchTextChanged;
            ClearSearchButton.Click += OnClearSearchClick;

            AboutButton.Click += delegate { ShowAbout(); };
            MinimizeButton.Click += delegate { WindowState = WindowState.Minimized; };
            MaximizeButton.Click += delegate { ToggleMaximize(); };
            CloseWindowButton.Click += delegate { Close(); };

            EmptyPanel.ClearRequested += delegate
            {
                SearchBox.Clear();
                _vm.ClearFilters();
                SetView(ListViewMode.All);
            };

            StickyHeader.RenderTransform = _stickyLift;
            StickyHeader.JumpRequested += delegate { ScrollToGroup(StickyHeader.GroupTitle); };

            // v3：日志面板从「常驻一行」改成「按需抽屉」，高度不再参与布局保存
            LogPanel.CollapseChanged += delegate
            {
                if (LogPanel.IsCollapsed)
                {
                    SetLogDrawerVisible(false);
                }
            };
        }

        private void RestoreWindowGeometry()
        {
            double minWidth = Metrics.MinWidth;
            double minHeight = Metrics.MinHeight;

            if (_settings.WindowWidth > minWidth)
            {
                Width = _settings.WindowWidth;
            }

            if (_settings.WindowHeight > minHeight)
            {
                Height = _settings.WindowHeight;
            }

            if (_settings.HasWindowPosition)
            {
                double left = _settings.WindowX;
                double top = _settings.WindowY;

                // 虚拟屏（全部显示器并集）校验：副屏被拔掉后 settings 里的旧坐标
                // 可能整个落在屏幕外，窗口就「消失」了 —— 与虚拟屏完全不相交时
                // 放弃坐标，回落 WindowStartupLocation=CenterScreen 系统居中
                double virtualLeft = SystemParameters.VirtualScreenLeft;
                double virtualTop = SystemParameters.VirtualScreenTop;
                double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
                double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

                bool intersects =
                    left < virtualRight && left + Width > virtualLeft &&
                    top < virtualBottom && top + Height > virtualTop;

                if (intersects)
                {
                    // 部分越界（只露出一个角）：推回虚拟屏内，保证标题栏够得着
                    if (left + Width > virtualRight)
                    {
                        left = virtualRight - Width;
                    }

                    if (left < virtualLeft)
                    {
                        left = virtualLeft;
                    }

                    if (top + Height > virtualBottom)
                    {
                        top = virtualBottom - Height;
                    }

                    if (top < virtualTop)
                    {
                        top = virtualTop;
                    }

                    Left = left;
                    Top = top;
                    WindowStartupLocation = WindowStartupLocation.Manual;
                }
            }

            if (_settings.Maximized)
            {
                // 在 Loaded 之前设 WindowState 会被 WPF 重置，故延后一帧
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
                {
                    WindowState = WindowState.Maximized;
                }));
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_firstScanStarted)
            {
                return;
            }

            _firstScanStarted = true;

            // 首扫延后 80ms：让窗口先画出来，用户看到的是「正在扫描」而不是一个白框
            var delay = new DispatcherTimer(DispatcherPriority.Normal);
            delay.Interval = TimeSpan.FromMilliseconds(Metrics.FirstScanDelayMs);
            delay.Tick += delegate
            {
                delay.Stop();
                _vm.ExecuteScanOrCancel();
            };
            delay.Start();
        }

        // ══════════════════════════ ViewModel 事件 ══════════════════════════

        private void OnStateChanged(ViewState state)
        {
            if (state == ViewState.Scanning)
            {
                _scanStartedUtc = DateTime.UtcNow;
                _scanTimer.Start();
            }
            else
            {
                _scanTimer.Stop();
            }

            ScanButton.Content = _vm.ScanButtonText;
            SearchBox.IsEnabled = _vm.IsSearchEnabled;
            ScanProgress.IsActive = state == ViewState.Scanning;

            RefreshAll();
        }

        private void OnCountsInvalidated()
        {
            RefreshAll();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || !string.Equals(e.PropertyName, "StatusText", StringComparison.Ordinal))
            {
                return;
            }

            if (!_flashTimer.IsEnabled)
            {
                StatusLabel.Text = _vm.StatusText;
            }
        }

        /// <summary>把 ViewModel 的当前状态刷到界面上（计数、文案、空态、分段器、分组头）。</summary>
        private void RefreshAll()
        {
            SubtitleText.Text = _vm.SubTitle;

            SelectedCountText.Text = _vm.SelectedCount.ToString();
            SelectedSuffixText.Text = _vm.SelectedSuffixText;

            if (!_flashTimer.IsEnabled)
            {
                StatusLabel.Text = _vm.StatusText;
            }

            UpdateSegments();
            UpdateViewSegments();
            UpdateSelectionBar();
            UpdateStatusChrome();
            UpdateSectionHeaders();
            UpdateEmptyPanel();
            UpdateSkeleton();
        }

        private void UpdateSectionHeaders()
        {
            for (int i = 0; i < _sectionOrder.Count; i++)
            {
                GroupSectionControl control = _sectionOrder[i];
                string title = control.Header.GroupTitle;
                CountGroupSelection(title, out int selected, out int selectable);
                control.Header.Bind(title, selected, selectable, _vm.IsGroupExpanded(title),
                                    !string.Equals(title, Groups.Unsupported, StringComparison.Ordinal));
                control.SetExpanded(_vm.IsGroupExpanded(title));
            }
        }

        private void CountGroupSelection(string group, out int selected, out int selectable)
        {
            selected = 0;
            selectable = 0;

            IList<Package> packages = _vm.Packages;
            for (int i = 0; i < packages.Count; i++)
            {
                Package package = packages[i];
                if (package == null || !package.Reinstallable)
                {
                    continue;
                }

                if (!string.Equals(Groups.GroupOf(package), group, StringComparison.Ordinal))
                {
                    continue;
                }

                selectable++;
                if (_vm.IsChecked(package.Key))
                {
                    selected++;
                }
            }
        }

        private void UpdateSkeleton()
        {
            Skeleton.Visibility = _vm.ShowSkeleton ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateEmptyPanel()
        {
            bool showClear;
            string message = BuildEmptyMessage(out showClear);

            if (message == null)
            {
                EmptyPanel.HidePanel();
            }
            else
            {
                EmptyPanel.ShowMessage(message, showClear);
            }

            if (_vm.ShowSkeleton || message != null)
            {
                StickyHeader.Visibility = Visibility.Collapsed;
            }
            else
            {
                _stickyDirty = true;
            }
        }

        private string BuildEmptyMessage(out bool showClearButton)
        {
            showClearButton = false;

            if (_vm.ShowSkeleton)
            {
                return null;
            }

            if (_vm.State == ViewState.NoWinget)
            {
                return Strings.EmptyNoWinget;
            }

            if (_vm.State == ViewState.ScanFailed)
            {
                return Strings.EmptyScanFailed;
            }

            if (_vm.State == ViewState.Cancelled)
            {
                return Strings.EmptyCancelled;
            }

            if (_vm.State == ViewState.Scanning)
            {
                return null;
            }

            if (_vm.MatchedCount > 0)
            {
                return null;
            }

            showClearButton = true;

            string keyword = (SearchBox.Text ?? string.Empty).Trim();
            string category = _vm.ActiveCategory;

            if (_vm.TotalCount == 0)
            {
                showClearButton = false;
                return Strings.EmptyNoResult;
            }

            if (_viewMode == ListViewMode.Selected
                && string.Equals(category, Groups.Unsupported, StringComparison.Ordinal))
            {
                return Strings.EmptyOnlySelectedUnsupported;
            }

            if (_viewMode == ListViewMode.Selected && category.Length > 0)
            {
                return Strings.EmptyOnlySelectedInGroup(category);
            }

            if (_viewMode == ListViewMode.Selected && keyword.Length == 0)
            {
                return Strings.EmptyViewSelected;
            }

            if (_viewMode == ListViewMode.Unselected)
            {
                // 「未选」为空不是错误，是任务完成——文案必须这么说，否则用户以为筛坏了
                if (keyword.Length > 0 || category.Length > 0)
                {
                    return Strings.EmptyViewUnselectedInScope(category.Length > 0 ? category : keyword);
                }

                return Strings.EmptyViewUnselected;
            }

            if (keyword.Length > 0 && category.Length > 0)
            {
                return Strings.EmptyNoSearchResultInGroup(category, keyword);
            }

            if (keyword.Length > 0)
            {
                return Strings.EmptyNoSearchResult(keyword);
            }

            if (category.Length > 0)
            {
                return Strings.EmptyCategoryNoSoftware;
            }

            return Strings.EmptyNoResult;
        }

        // ══════════════════════════ 列表渲染 ══════════════════════════

        private void ScheduleRelayout()
        {
            if (_relayoutPending || _closing)
            {
                return;
            }

            _relayoutPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RebuildList));
        }

        private void RebuildList()
        {
            _relayoutPending = false;
            if (_closing)
            {
                return;
            }

            IList<SearchToken> tokens = SearchQuery.Tokenize(_vm.SearchText);
            string[] keywords = _vm.HighlightKeywords;

            IList<GroupRender> sections = _presenter.Rebuild(
                _vm.Packages, _vm.ActiveCategory, tokens, _viewMode,
                _vm.CheckedMap, _vm.BuildExpandedMap());

            SectionsHost.Children.Clear();
            _rowByKey.Clear();
            _flatRows.Clear();
            _sectionByTitle.Clear();
            _sectionOrder.Clear();
            _sectionTops.Clear();
            _sectionTitles.Clear();

            for (int i = 0; i < sections.Count; i++)
            {
                GroupRender section = sections[i];
                var control = new GroupSectionControl();

                control.Header.Bind(section.Title, section.SelectedCount, section.SelectableCount,
                                    section.Expanded, section.Selectable);

                string capturedTitle = section.Title;
                GroupSectionControl capturedSection = control;
                control.Header.ToggleRequested += delegate { ToggleGroupInPlace(capturedTitle, capturedSection); };
                control.Header.GroupActionRequested += delegate(object s, GroupActionEventArgs args)
                {
                    // 只有「减少勾选」的方向才留快照：整组取消是一次点掉几十个勾，误点多发
                    if (!args.Select)
                    {
                        BeginUndo("已取消勾选「" + capturedTitle + "」整组");
                    }

                    _vm.SelectGroup(capturedTitle, args.Select);
                };

                control.SetExpanded(section.Expanded);

                for (int j = 0; j < section.Rows.Count; j++)
                {
                    Package package = section.Rows[j];
                    PackageRowControl row = CreateRow(package, keywords);
                    control.Rows.Children.Add(row);
                }

                SectionsHost.Children.Add(control);

                _sectionByTitle[section.Title] = control;
                _sectionOrder.Add(control);
                _sectionTitles.Add(section.Title);
            }

            _stickyDirty = true;
            UpdateStickyHeader();
            UpdateEmptyPanel();
        }

        /// <summary>
        /// 就地折叠/展开：只改行容器的可见性和折叠三角的角度，一个控件都不重建。
        /// 点分组头是这个列表里最高频的动作之一，走整表重建（95 行 UserControl 重新实例化 + 重新绑定）
        /// 会让人明显感觉到「点下去要等一下才动」。
        /// </summary>
        private void ToggleGroupInPlace(string title, GroupSectionControl section)
        {
            if (section == null)
            {
                return;
            }

            bool expanded = _vm.ToggleGroup(title);
            section.Header.SetExpandedVisual(expanded);
            section.SetExpanded(expanded);

            // 折叠会改变其后所有分组的 Y 位置，吸顶头的位置表必须重算；
            // 但此刻还没重新布局，读到的仍是旧坐标——等这一帧的布局跑完再算。
            _stickyDirty = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                _stickyDirty = true;
                UpdateStickyHeader();
            }));
        }

        private PackageRowControl CreateRow(Package package, string[] keywords)
        {
            var row = new PackageRowControl();
            row.Bind(package, _vm.IsChecked(package.Key));
            row.SetHighlight(keywords);
            row.SetCompact(_narrow);

            Package captured = package;

            row.ToggleRequested += delegate
            {
                // 整行可点的代价是误触：取消勾选前留一份快照，供「撤销」与 Ctrl Z 回退
                if (_vm.IsChecked(captured.Key))
                {
                    BeginUndo("已取消勾选「" + (captured.Name ?? captured.PackageIdentifier) + "」");
                }

                _vm.ToggleRow(captured);

                // ViewModel 是唯一真相：勾选表更新后回写行控件，
                // 避免「复选框自己翻了、内存里没翻」的两套状态
                PackageRowControl current;
                if (_rowByKey.TryGetValue(captured.Key, out current))
                {
                    current.ApplyCheckedState(_vm.IsChecked(captured.Key));
                }
            };

            row.CopyRequested += delegate { CopyPackageId(captured); };
            row.ContextMenu = BuildRowMenu(captured);

            _rowByKey[package.Key] = row;
            _flatRows.Add(row);
            return row;
        }

        private ContextMenu BuildRowMenu(Package package)
        {
            var menu = new ContextMenu();
            if (package == null)
            {
                return menu;
            }

            AddMenuItem(menu, Strings.MenuCopyPackageId, delegate
            {
                CopyToClipboard(package.PackageIdentifier);
                Flash(Strings.FlashCopiedPackageId + "：" + package.PackageIdentifier);
            });

            AddMenuItem(menu, Strings.MenuCopyName, delegate
            {
                CopyToClipboard(package.Name);
                Flash(Strings.FlashCopiedName);
            });

            AddMenuItem(menu, Strings.MenuCopyNameAndId, delegate
            {
                string text = (package.Name ?? string.Empty) + " (" + (package.PackageIdentifier ?? string.Empty) + ")";
                CopyToClipboard(text);
                Flash(Strings.FlashCopiedNameAndId);
            });

            if (package.Reinstallable)
            {
                AddMenuItem(menu, Strings.MenuCopyInstallCommand, delegate
                {
                    // 显式 --exact + 两个 accept：粘到终端里不用再回答交互提问
                    string command = "winget install --id " + package.PackageIdentifier
                                     + " -e --accept-package-agreements --accept-source-agreements";
                    CopyToClipboard(command);
                    Flash(Strings.FlashCopiedInstallCommand);
                });
            }

            menu.Items.Add(new Separator());

            AddMenuItem(menu, Strings.MenuToggleCheck, delegate
            {
                if (!package.Reinstallable)
                {
                    return;
                }

                _vm.ToggleRow(package);

                PackageRowControl row;
                if (_rowByKey.TryGetValue(package.Key, out row))
                {
                    row.ApplyCheckedState(_vm.IsChecked(package.Key));
                }
            });

            return menu;
        }

        private static void AddMenuItem(ContextMenu menu, string header, Action action)
        {
            var item = new MenuItem();
            item.Header = header;
            item.Click += delegate { action(); };
            menu.Items.Add(item);
        }

        private void CopyPackageId(Package package)
        {
            if (package == null)
            {
                return;
            }

            CopyToClipboard(package.PackageIdentifier);
            Flash("已复制包 ID：" + package.PackageIdentifier);
        }

        // ══════════════════════════ 吸顶分组头 ══════════════════════════

        private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateStickyHeader();
        }

        private void UpdateStickyHeader()
        {
            if (_sectionOrder.Count == 0 || _vm.ShowSkeleton || _vm.ShowEmpty)
            {
                StickyHeader.Visibility = Visibility.Collapsed;
                return;
            }

            if (_stickyDirty)
            {
                RebuildStickyTable();
            }

            if (_sectionTops.Count == 0)
            {
                StickyHeader.Visibility = Visibility.Collapsed;
                return;
            }

            double offset = ListScroll.VerticalOffset;
            int index = FindSectionIndex(offset);
            if (index < 0)
            {
                StickyHeader.Visibility = Visibility.Collapsed;
                return;
            }

            // 真组头还没滚出视野时不显示吸顶头，否则屏幕上会出现两条一模一样的标题
            if (offset < _sectionTops[index] + Metrics.GroupHeaderHeight)
            {
                StickyHeader.Visibility = Visibility.Collapsed;
                return;
            }

            string title = _sectionTitles[index];
            CountGroupSelection(title, out int selected, out int selectable);
            StickyHeader.Bind(title, selected, selectable);
            StickyHeader.Visibility = Visibility.Visible;

            // 下一组的组头逼近时把吸顶头往上顶。
            // 注意：架构文档写的是 `nextTop - offset - 42`，这里取的是它的负值
            // `-max(0, 42 - (nextTop - offset))`。原因是「推入」要让吸顶头往上走
            // （负位移），按文档原式算出来是正数，会把吸顶头往下推、盖住第一行。
            // 两种写法的触发条件完全一致，只有最终位移方向不同。
            double push = 0;
            if (index + 1 < _sectionTops.Count)
            {
                double nextTop = _sectionTops[index + 1];
                double gap = nextTop - offset - Metrics.StickyBarHeight;
                if (gap < 0)
                {
                    push = gap;
                }
            }

            _stickyLift.Y = push;
        }

        private void RebuildStickyTable()
        {
            _sectionTops.Clear();
            _stickyDirty = false;

            for (int i = 0; i < _sectionOrder.Count; i++)
            {
                double y = 0;
                try
                {
                    Point point = _sectionOrder[i].TranslatePoint(new Point(0, 0), SectionsHost);
                    y = point.Y;
                }
                catch (InvalidOperationException)
                {
                    // 控件还没进入可视树：用 0 兜底，下一次布局后 _stickyDirty 会再触发重算
                    y = 0;
                }

                _sectionTops.Add(y);
            }
        }

        /// <summary>二分查找「当前滚动位置所在的最后一组」；返回 -1 表示在第一组之前。</summary>
        private int FindSectionIndex(double offset)
        {
            int low = 0;
            int high = _sectionTops.Count - 1;
            int result = -1;

            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                if (_sectionTops[mid] <= offset + 0.5)
                {
                    result = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return result;
        }

        public void ScrollToGroup(string group)
        {
            if (string.IsNullOrEmpty(group))
            {
                return;
            }

            GroupSectionControl control;
            if (!_sectionByTitle.TryGetValue(group, out control))
            {
                return;
            }

            try
            {
                Point point = control.TranslatePoint(new Point(0, 0), SectionsHost);
                ListScroll.ScrollToVerticalOffset(point.Y);
            }
            catch (InvalidOperationException)
            {
                // 控件不在可视树（列表刚重建）：忽略这次滚动请求
            }
        }

        // ══════════════════════════ 搜索 ══════════════════════════

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            string text = SearchBox.Text ?? string.Empty;
            SearchPlaceholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearSearchButton.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            // 防抖 130ms：每敲一个字母都重建列表会在长列表上明显卡顿
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void OnSearchTick(object sender, EventArgs e)
        {
            _searchTimer.Stop();
            _vm.SearchText = SearchBox.Text ?? string.Empty;

            // 分段器计数是「当前搜索范围内的数量」：搜索一变就要重算，否则数字是死的
            UpdateSegments();
            UpdateViewSegments();
            UpdateSelectionBar();
        }

        private void OnClearSearchClick(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            _vm.SearchText = string.Empty;
            FocusSearch();
        }

        public void FocusSearch()
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        // ══════════════════════════ 扫描计时 ══════════════════════════

        private void OnScanTick(object sender, EventArgs e)
        {
            double seconds = (DateTime.UtcNow - _scanStartedUtc).TotalSeconds;
            _vm.NotifyElapsedSeconds(seconds);

            if (!_flashTimer.IsEnabled)
            {
                StatusLabel.Text = _vm.StatusText;
            }
        }

        // ══════════════════════════ 底栏 flash ══════════════════════════

        public void Flash(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            _vm.SetStatusOverride(message);
            StatusLabel.Text = message;

            _flashTimer.Stop();
            _flashTimer.Start();
        }

        private void OnFlashTick(object sender, EventArgs e)
        {
            _flashTimer.Stop();
            _vm.ClearStatusOverride();
            StatusLabel.Text = _vm.StatusText;
        }

        // ══════════════════════════ 窗口外壳 ══════════════════════════

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                e.Handled = true;
                return;
            }

            if (e.ButtonState != MouseButtonState.Pressed)
            {
                return;
            }

            if (WindowState == WindowState.Maximized)
            {
                // 最大化状态下 DragMove() 会抛 InvalidOperationException；
                // 先还原再拖，用户的手感就是「把窗口从最大化状态拽下来」
                WindowState = WindowState.Normal;
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标在 DragMove 前就松开了：无害
            }
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void OnWindowStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                // 无边框窗口最大化后，系统仍按 ResizeBorderThickness 把窗口撑大，
                // 四边会各溢出 7px（内容被裁掉）。用等量 Margin 收回来。
                RootGrid.Margin = new Thickness(Metrics.ResizeEdge);
                // 字形用矢量 DataTemplate 切换（□/❐ 字符大小粗细随字体漂移，已弃用）
                MaximizeButton.ContentTemplate = (DataTemplate)FindResource("Glyph.Window.Restore");
                MaximizeButton.ToolTip = "还原";
            }
            else
            {
                RootGrid.Margin = new Thickness(0);
                MaximizeButton.ContentTemplate = (DataTemplate)FindResource("Glyph.Window.Maximize");
                MaximizeButton.ToolTip = "最大化";
            }

            _stickyDirty = true;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool narrow = ActualWidth < Metrics.NarrowWidth;
            if (narrow != _narrow)
            {
                _narrow = narrow;

                // 只改可见性，不重建行：重建会丢掉当前的滚动位置与键盘焦点
                for (int i = 0; i < _flatRows.Count; i++)
                {
                    _flatRows[i].SetCompact(narrow);
                }

                // v2 响应式留白：宽窗 24px 呼吸感、窄窗 14px 保内容宽度。
                // XAML 里的页面边距全部走 DynamicResource 引用 Thickness.Page*，
                // 所以这里直接改 Application 级资源就能全局生效，不用遍历控件。
                ApplyResponsivePageMargin(narrow);
            }

            _stickyDirty = true;
        }

        /// <summary>
        /// 切换页面级边距令牌（舒适 ↔ 紧凑）。与 Tokens.xaml 的默认值保持一致，
        /// 保证设计器预览、首帧、切换后三者看到的是同一套数值。
        /// </summary>
        private void ApplyResponsivePageMargin(bool compact)
        {
            // v3：页面边距固定 16，不再随窗宽换挡。
            // 边距忽宽忽窄会让人误以为窗口在抖；窄窗要省空间，靠裁剪列来实现。
            var resources = Application.Current.Resources;
            var pad = new Thickness(Metrics.PagePadding, 0, Metrics.PagePadding, 0);
            resources["Thickness.Page"] = pad;
            resources["Thickness.PageTop"] = new Thickness(Metrics.PagePadding, 8, Metrics.PagePadding, 0);
            resources["Thickness.PageGap"] = new Thickness(Metrics.PagePadding, 8, Metrics.PagePadding, 0);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            var focusedTextBox = Keyboard.FocusedElement as TextBox;

            if (ctrl && e.Key == Key.F)
            {
                FocusSearch();
                e.Handled = true;
                return;
            }

            if (ctrl && shift && e.Key == Key.A)
            {
                _vm.SetAll(false);
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.A)
            {
                // 焦点在搜索框里时 Ctrl+A 是「全选文本」，不能抢
                if (focusedTextBox == null)
                {
                    _vm.SetAll(true);
                    e.Handled = true;
                }

                return;
            }

            if (ctrl && e.Key == Key.I)
            {
                _vm.Invert();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.E)
            {
                OpenExportDialog();
                e.Handled = true;
                return;
            }

            // 撤销必须有键盘通路：Toast 只活 5 秒，只用鼠标点等于对键盘用户没有撤销
            if (ctrl && e.Key == Key.Z)
            {
                UndoLastSelectionChange();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.L)
            {
                SetLogDrawerVisible(!IsLogDrawerVisible());
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F5)
            {
                if (_vm.State == ViewState.Scanning)
                {
                    Flash(Strings.FlashScanningBusy);
                }
                else
                {
                    _vm.ExecuteScanOrCancel();
                }

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (IsLogDrawerVisible())
                {
                    SetLogDrawerVisible(false);
                    e.Handled = true;
                    return;
                }

                if ((SearchBox.Text ?? string.Empty).Length > 0)
                {
                    SearchBox.Clear();
                    _vm.SearchText = string.Empty;
                    e.Handled = true;
                }

                return;
            }

            // 行内方向键：在行之间移动焦点，而不是让 ScrollViewer 自己滚
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                var row = Keyboard.FocusedElement as PackageRowControl;
                if (row != null)
                {
                    MoveRowFocus(row, e.Key == Key.Down ? 1 : -1);
                    e.Handled = true;
                }
            }
        }

        private void MoveRowFocus(PackageRowControl current, int direction)
        {
            int index = _flatRows.IndexOf(current);
            if (index < 0)
            {
                return;
            }

            int next = index + direction;
            if (next < 0 || next >= _flatRows.Count)
            {
                return;
            }

            PackageRowControl target = _flatRows[next];
            target.Focus();
            target.BringIntoView();
        }

        // ══════════════════════════ IMainShell ══════════════════════════

        public void CopyToClipboard(string text)
        {
            if (text == null)
            {
                return;
            }

            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception)
            {
                // 剪贴板被别的进程独占（COMException）是常态：不弹框打扰
            }
        }

        public void ShowWingetGuide(string detail)
        {
            WingetGuideDialog.ShowGuide(this, detail);
        }

        public void ShowAbout()
        {
            string exportPath = AppPaths.DefaultExportPath(_settings.ExportPath);
            AboutDialog.ShowAbout(this, exportPath, AppPaths.SelectionFile,
                                  _log != null ? _log.FilePath : null, BuildDiagnosticText());
        }

        public void SetLogCollapsed(bool collapsed)
        {
            // v3：日志是覆盖式抽屉，收起 = 整条隐藏（不是缩到 34 高）
            SetLogDrawerVisible(!collapsed);
        }

        public void RequestBackgroundRelayout()
        {
            ScheduleRelayout();
        }

        // ══════════════════════════ 导出 ══════════════════════════

        /// <summary>
        /// 导出入口。真正的「选路径 → 写三产物 → 成功页」流程都在
        /// <see cref="ExportDialog"/> 里；这里只做两件前置检查（扫描中 / 没勾选）
        /// 和导出完成后的底栏提示。
        /// </summary>
        public void OpenExportDialog()
        {
            if (_vm.State == ViewState.Scanning)
            {
                Flash(Strings.FlashScanningBusy);
                return;
            }

            IList<Package> selected = _vm.SelectedPackages();
            if (selected.Count == 0)
            {
                MessageBox.Show(this,
                    Strings.FlashExportNeedsSelection,
                    Strings.ExportTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string initialPath = AppPaths.DefaultExportPath(_settings.ExportPath);

            var dialog = new ExportDialog(
                selected,
                _vm.AllPackages(),
                _vm.WingetVersion,
                initialPath,
                _log,
                _settings,
                _settingsStore);

            dialog.Owner = this;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // 模态返回后再补一条底栏提示：对话框已经显示了成功页，
            // 这里只负责把「导出完成」留在主界面上（对话框是 200ms 后才关的，flash 不会被打断）
            bool? result = dialog.ShowDialog();
            if (result == true && dialog.Result != null)
            {
                Flash(Strings.ExportDoneTitle(dialog.Result.PackageCount));
            }
        }

        // ══════════════════════════ 设置与关闭 ══════════════════════════

        private void SaveLayoutSettings()
        {
            if (_settings == null || _settingsStore == null)
            {
                return;
            }

            _settings.LogHeight = (int)Math.Round(LogPanel.ExpandedHeight);
            _settings.LogExpanded = !LogPanel.IsCollapsed;
            _settings.Category = _vm.ActiveCategory;

            _settingsStore.Save(_settings);
        }

        private string BuildDiagnosticText()
        {
            var builder = new StringBuilder();
            builder.AppendLine(Strings.WindowTitle + " " + AppVersion.Version + "（" + Strings.AppTag + "）");
            builder.Append("winget：").Append(string.IsNullOrEmpty(_config.WingetExe) ? "未找到" : _config.WingetExe)
                   .AppendLine("（版本 " + _vm.WingetVersion + "）");
            builder.Append("数据目录：").AppendLine(AppPaths.DataDir);
            builder.Append("已安装 ").Append(_vm.TotalCount)
                   .Append(" 项，可重装 ").Append(_vm.ReinstallableCount)
                   .Append(" 项，已勾选 ").Append(_vm.SelectedCount).AppendLine(" 项");
            builder.AppendLine();
            builder.AppendLine("—— 最近 40 行日志 ——");

            if (_log != null)
            {
                IList<string> tail = _log.Tail(40);
                for (int i = 0; i < tail.Count; i++)
                {
                    builder.AppendLine(tail[i]);
                }
            }

            return builder.ToString();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_closing)
            {
                return;
            }

            _closing = true;

            _scanTimer.Stop();
            _flashTimer.Stop();
            _searchTimer.Stop();

            // 最大化时不保存几何：否则下次启动会先还原成满屏再还原，闪一下
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowX = (int)Math.Round(Left);
                _settings.WindowY = (int)Math.Round(Top);
                _settings.WindowWidth = (int)Math.Round(ActualWidth);
                _settings.WindowHeight = (int)Math.Round(ActualHeight);
            }

            _settings.Maximized = WindowState == WindowState.Maximized;
            _settings.LogHeight = (int)Math.Round(LogPanel.ExpandedHeight);
            _settings.LogExpanded = !LogPanel.IsCollapsed;
            _settings.Category = _vm.ActiveCategory;

            _settingsStore.Save(_settings);

            // 取消扫描 + 立刻把勾选表落盘（别指望 300ms 防抖还有机会跑），
            // 然后等扫描任务收尾（≤2s）：取消会触发 ProcessRunner 杀 winget 进程树，
            // 不等它的话窗口关了、杀进程的代码可能还没跑完，留下孤儿 winget
            _vm.Shutdown();
            WaitForScanShutdown();

            LogPanel.AttachLogger(null);
        }

        /// <summary>
        /// 等当前扫描任务收尾（最多 2 秒）。
        /// <para>
        /// 不能用 <c>task.Wait(2000)</c>：任务内部的 <c>await</c> 续体要回 UI 线程，
        /// 阻塞 UI 线程等待会把续体锁死（任务永远完不成，白等满 2 秒）。
        /// 这里用消息泵（<see cref="DispatcherFrame"/>）——任务完成或超时到点，先到先走。
        /// </para>
        /// <para>
        /// 重入保护：<c>PushFrame</c> 期间会继续派发消息，用户再次点 ✕ 可能重入
        /// <see cref="OnClosing"/>（那边已有 <c>_closing</c> 早退守卫）；本方法自身再用
        /// <c>_waitingForScanShutdown</c> 挡住「重入后再进一遍等待」。
        /// </para>
        /// </summary>
        private void WaitForScanShutdown()
        {
            if (_waitingForScanShutdown)
            {
                return;
            }

            Task scan = _vm.CurrentScanTask;
            if (scan == null || scan.IsCompleted)
            {
                return;
            }

            _waitingForScanShutdown = true;
            try
            {
                var frame = new DispatcherFrame(true);
                scan.ContinueWith(delegate { frame.Continue = false; },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.FromCurrentSynchronizationContext());

                var timeout = new DispatcherTimer(DispatcherPriority.Send);
                timeout.Interval = TimeSpan.FromMilliseconds(ScanShutdownWaitMs);
                timeout.Tick += delegate
                {
                    timeout.Stop();
                    frame.Continue = false;
                };
                timeout.Start();

                Dispatcher.PushFrame(frame);
                timeout.Stop();
            }
            finally
            {
                _waitingForScanShutdown = false;
            }
        }
    }
}
