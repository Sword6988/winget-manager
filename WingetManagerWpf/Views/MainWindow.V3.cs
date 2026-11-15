using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WingetManagerWpf.Core;
using WingetManagerWpf.Models;
using WingetManagerWpf.ViewModels;

namespace WingetManagerWpf.Views
{
    /// <summary>
    /// v3「清单工作台」的外壳逻辑：命令条分段筛选、视图三态、浮动选择条、
    /// 撤销与 Toast、⋯ 溢出菜单、状态条与日志抽屉。
    /// <para>
    /// 单独拆一个 partial 文件，是为了让 <c>MainWindow.xaml.cs</c> 里那些
    /// 列表渲染 / 吸顶头 / 窗口外壳的既有逻辑保持原样——这次改版只是「换外壳」，
    /// 不该把稳定的渲染管线一起重写。
    /// </para>
    /// </summary>
    public partial class MainWindow
    {
        private readonly Dictionary<string, ToggleButton> _segByCategory =
            new Dictionary<string, ToggleButton>(StringComparer.Ordinal);

        private readonly List<ToggleButton> _viewButtons = new List<ToggleButton>();

        private ListViewMode _viewMode = ListViewMode.All;

        /// <summary>撤销快照（勾选表副本）。null = 没有可撤销的操作。</summary>
        private Dictionary<string, bool> _undoSnapshot;

        /// <summary>撤销对应的说明文案（例如「已取消勾选「Git」」）。</summary>
        private string _undoMessage;

        private DispatcherTimer _toastTimer;
        private Action _toastAction;

        // ══════════════════════════ 初始化 ══════════════════════════

        /// <summary>由 <c>ApplyStaticText</c> 调用：把 v3 新控件的文案与事件接上。</summary>
        private void InitV3Chrome()
        {
            MoreButton.ToolTip = "更多操作";
            LogToggleButton.ToolTip = "展开运行日志（Ctrl L）";

            MoreButton.Click += OnMoreClick;
            LogToggleButton.Click += delegate { SetLogDrawerVisible(!IsLogDrawerVisible()); };
            LogBadgeButton.Click += delegate { SetLogDrawerVisible(!IsLogDrawerVisible()); };
            StatusLabel.MouseLeftButtonUp += delegate { SetLogDrawerVisible(!IsLogDrawerVisible()); };
            ToastActionButton.Click += OnToastActionClick;
            LogPanel.EntriesChanged += delegate { UpdateStatusChrome(); };

            DataDirText.Text = ShortenPath(AppPaths.DataDir);
            DataDirText.ToolTip = AppPaths.DataDir;
            WingetVersionText.Text = "winget " + _vm.WingetVersion;

            _toastTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _toastTimer.Interval = TimeSpan.FromMilliseconds(5200);
            _toastTimer.Tick += OnToastTick;

            BuildViewSegments();
        }

        private static string ShortenPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            // 状态条只有 32 高，右侧空间有限：只留末两段，全路径放 ToolTip
            string[] parts = path.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 2)
            {
                return path;
            }

            return parts[parts.Length - 2] + "\\" + parts[parts.Length - 1];
        }

        /// <summary>建立分组分段器（全部 / 软件 / 运行库 / 不支持）。计数由 UpdateSegments 填。</summary>
        private void BuildSegments()
        {
            CategoryHost.Children.Clear();
            _segByCategory.Clear();

            AddSegment(string.Empty, Strings.CategoryAll);
            for (int i = 0; i < Groups.Order.Length; i++)
            {
                AddSegment(Groups.Order[i], Groups.Order[i]);
            }

            UpdateSegments();
        }

        private void AddSegment(string category, string label)
        {
            var button = new ToggleButton();
            if (TryFindResource("Button.Segment") is Style)
            {
                button.Style = (Style)TryFindResource("Button.Segment");
            }

            button.Content = label;
            button.Margin = new Thickness(0, 0, 1, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Tag = category;

            // 捕获到局部变量：闭包必须按值固定，否则所有分段都会指向最后一个分组
            string captured = category;
            button.Click += delegate
            {
                _vm.SelectCategory(captured);
                UpdateSegments();
                UpdateViewSegments();
                UpdateSelectionBar();
            };

            CategoryHost.Children.Add(button);
            _segByCategory[category] = button;
        }

        private void BuildViewSegments()
        {
            ViewHost.Children.Clear();
            _viewButtons.Clear();

            AddViewButton(ListViewMode.All, "全部");
            AddViewButton(ListViewMode.Selected, "已选");
            AddViewButton(ListViewMode.Unselected, "未选");
        }

        private void AddViewButton(ListViewMode mode, string label)
        {
            var button = new ToggleButton();

            // v3.4：与左侧分组筛选共用 Button.Segment。
            // 原来视图组用的是另一套（浅蓝底 + 蓝字），同一个命令条里出现两种「选中」语言；
            // 现在两边都是「轨道 + 白底胶囊」，选中态读法一致。
            if (TryFindResource("Button.Segment") is Style)
            {
                button.Style = (Style)TryFindResource("Button.Segment");
            }

            button.Content = label;
            button.Margin = new Thickness(mode == ListViewMode.All ? 0 : 1, 0, 0, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.ToolTip = mode == ListViewMode.Selected
                ? "只看已勾选的行"
                : (mode == ListViewMode.Unselected ? "找出还没勾的（已全部勾完时为空）" : "显示全部行");

            ListViewMode captured = mode;
            button.Click += delegate { SetView(captured); };

            ViewHost.Children.Add(button);
            _viewButtons.Add(button);
        }

        // ══════════════════════════ 分段器与计数 ══════════════════════════

        /// <summary>刷新分段器文案与选中态。计数是 facet：只统计当前搜索/视图范围内。</summary>
        private void UpdateSegments()
        {
            foreach (KeyValuePair<string, ToggleButton> pair in _segByCategory)
            {
                bool active = string.Equals(_vm.ActiveCategory, pair.Key, StringComparison.Ordinal);
                if (pair.Value.IsChecked != active)
                {
                    pair.Value.IsChecked = active;
                }

                string label = pair.Key.Length == 0 ? Strings.CategoryAll : pair.Key;
                pair.Value.Content = label + " " + CountFacet(pair.Key).ToString();
            }
        }

        private void UpdateViewSegments()
        {
            for (int i = 0; i < _viewButtons.Count; i++)
            {
                var mode = (ListViewMode)i;
                bool active = _viewMode == mode;
                if (_viewButtons[i].IsChecked != active)
                {
                    _viewButtons[i].IsChecked = active;
                }
            }
        }

        /// <summary>某个分组在当前「搜索 + 视图」范围内的行数。category 为空 = 全部。</summary>
        private int CountFacet(string category)
        {
            IList<SearchToken> tokens = SearchQuery.Tokenize(_vm.SearchText);
            IList<Package> packages = _vm.Packages;
            int count = 0;

            for (int i = 0; i < packages.Count; i++)
            {
                Package package = packages[i];
                if (package == null)
                {
                    continue;
                }

                if (category != null && category.Length > 0
                    && !string.Equals(Groups.GroupOf(package), category, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!SearchQuery.Matches(package, tokens))
                {
                    continue;
                }

                if (!PassesView(package))
                {
                    continue;
                }

                count++;
            }

            return count;
        }

        /// <summary>当前视图是否包含该行（与 ListPresenter 的过滤规则一一对应）。</summary>
        private bool PassesView(Package package)
        {
            if (package == null)
            {
                return false;
            }

            if (_viewMode == ListViewMode.All)
            {
                return true;
            }

            bool on = _vm.IsChecked(package.Key);
            if (_viewMode == ListViewMode.Selected)
            {
                return package.Reinstallable && on;
            }

            return package.Reinstallable && !on;
        }

        // ══════════════════════════ 视图三态 ══════════════════════════

        private void SetView(ListViewMode mode)
        {
            if (_viewMode == mode)
            {
                return;
            }

            _viewMode = mode;
            UpdateViewSegments();
            UpdateSegments();
            UpdateSelectionBar();
            ScheduleRelayout();
        }

        // ══════════════════════════ 浮动选择条 ══════════════════════════

        /// <summary>
        /// 选择条只反映「已选 N 项」，但筛选时要补一句「视图内 M」——
        /// 否则搜完只剩 1 行、却显示已选 6，用户会以为漏选。
        /// <para>
        /// 同时给滚动区补底部留白：选择条浮在列表之上，不留白就会永久压住末行。
        /// </para>
        /// </summary>
        private void UpdateSelectionBar()
        {
            int total = _vm.SelectedCount;
            bool show = total > 0 && _vm.State != ViewState.Scanning;

            SelectionBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            // 末行被压住的问题：选择条可见时给列表补 64px。
            // 用「内容底部外边距」而不是 ScrollViewer.Padding —— 外边距一定计入滚动范围，
            // Padding 在部分模板下不进 extent，滚到底仍会差一截。
            ListScroll.Padding = new Thickness(0);
            SectionsHost.Margin = show
                ? new Thickness(0, 2, 0, 8 + Metrics.ListBottomPad)
                : new Thickness(0, 2, 0, 8);

            if (!show)
            {
                return;
            }

            SelectedCountText.Text = total.ToString();

            int visibleSelected = CountVisibleSelected();
            bool filtered = (_vm.SearchText ?? string.Empty).Trim().Length > 0
                            || _vm.ActiveCategory.Length > 0
                            || _viewMode != ListViewMode.All;

            SelectedSuffixText.Text = (filtered && visibleSelected < total)
                ? "项将被导出 · 视图内 " + visibleSelected
                : "项将被导出";
        }

        private int CountVisibleSelected()
        {
            IList<SearchToken> tokens = SearchQuery.Tokenize(_vm.SearchText);
            IList<Package> packages = _vm.Packages;
            int count = 0;

            for (int i = 0; i < packages.Count; i++)
            {
                Package package = packages[i];
                if (package == null || !package.Reinstallable || !_vm.IsChecked(package.Key))
                {
                    continue;
                }

                if (_vm.ActiveCategory.Length > 0
                    && !string.Equals(Groups.GroupOf(package), _vm.ActiveCategory, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!SearchQuery.Matches(package, tokens) || !PassesView(package))
                {
                    continue;
                }

                count++;
            }

            return count;
        }

        // ══════════════════════════ 撤销 / Toast ══════════════════════════

        /// <summary>在「减少勾选」之前留一份快照，5.2 秒内可撤销，Ctrl Z 一直有效到下一次操作。</summary>
        private void BeginUndo(string message)
        {
            var snapshot = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, bool> pair in _vm.CheckedMap)
            {
                snapshot[pair.Key] = pair.Value;
            }

            _undoSnapshot = snapshot;
            _undoMessage = message;
            ShowToast(message, "撤销", UndoLastSelectionChange);
        }

        private void UndoLastSelectionChange()
        {
            if (_undoSnapshot == null)
            {
                Flash("没有可撤销的操作");
                return;
            }

            Dictionary<string, bool> snapshot = _undoSnapshot;
            string message = _undoMessage;
            _undoSnapshot = null;
            _undoMessage = null;

            _vm.CheckedMap.Clear();
            foreach (KeyValuePair<string, bool> pair in snapshot)
            {
                _vm.CheckedMap[pair.Key] = pair.Value;
            }

            _vm.RefreshCounts();
            _vm.FlushSelection();
            ScheduleRelayout();
            UpdateSelectionBar();
            HideToast();
            Flash("已撤销：" + (message ?? "上一次勾选"));
        }

        private void ShowToast(string text, string actionLabel, Action action)
        {
            ToastText.Text = text;
            _toastAction = action;

            if (action != null)
            {
                ToastActionButton.Content = actionLabel ?? "撤销";
                ToastActionButton.Visibility = Visibility.Visible;
                _toastTimer.Interval = TimeSpan.FromMilliseconds(5200);
            }
            else
            {
                ToastActionButton.Visibility = Visibility.Collapsed;
                _toastTimer.Interval = TimeSpan.FromMilliseconds(2600);
            }

            ToastBar.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void HideToast()
        {
            _toastTimer.Stop();
            ToastBar.Visibility = Visibility.Collapsed;
            _toastAction = null;
        }

        private void OnToastTick(object sender, EventArgs e)
        {
            HideToast();
        }

        private void OnToastActionClick(object sender, RoutedEventArgs e)
        {
            Action action = _toastAction;
            HideToast();
            if (action != null)
            {
                action();
            }
        }

        // ══════════════════════════ ⋯ 溢出菜单 ══════════════════════════

        /// <summary>
        /// v3 用真菜单取代 v2 那条「点了只弹一句提示」的假按钮，
        /// 顺带给「导出配置」一个**不依赖勾选**的入口——选择条在 0 选中时整条隐藏，
        /// 如果导出只挂在那里，未勾选时用户就找不到它。
        /// </summary>
        private void OnMoreClick(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            menu.PlacementTarget = MoreButton;
            menu.Placement = PlacementMode.Bottom;

            AddMenuItem(menu, "勾选当前视图全部（Ctrl A）", delegate { SelectAllVisible(); });
            AddMenuItem(menu, "全不选（Ctrl Shift A）", delegate
            {
                if (_vm.SelectedCount == 0)
                {
                    Flash("当前没有勾选任何软件");
                    return;
                }

                BeginUndo("已清空 " + _vm.SelectedCount + " 项勾选");
                _vm.SetAll(false);
                ScheduleRelayout();
            });
            AddMenuItem(menu, "反选当前视图（Ctrl I）", delegate { InvertVisible(); });

            menu.Items.Add(new Separator());

            AddMenuItem(menu, "导出配置…（Ctrl E）", delegate { OpenExportDialog(); });
            AddMenuItem(menu, "打开数据目录", delegate { OpenDataDir(); });

            menu.IsOpen = true;
        }

        private void SelectAllVisible()
        {
            int before = _vm.SelectedCount;
            _vm.SetAll(true);
            ScheduleRelayout();

            int added = _vm.SelectedCount - before;
            ShowToast(added > 0 ? "已勾选视图内 " + added + " 项" : "视图内可勾选的行都已勾选", null, null);
        }

        private void InvertVisible()
        {
            BeginUndo("已反选当前视图");

            IList<SearchToken> tokens = SearchQuery.Tokenize(_vm.SearchText);
            IList<Package> packages = _vm.Packages;

            for (int i = 0; i < packages.Count; i++)
            {
                Package package = packages[i];
                if (package == null || !package.Reinstallable || !PassesView(package))
                {
                    continue;
                }

                if (_vm.ActiveCategory.Length > 0
                    && !string.Equals(Groups.GroupOf(package), _vm.ActiveCategory, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!SearchQuery.Matches(package, tokens))
                {
                    continue;
                }

                _vm.CheckedMap[package.Key] = !_vm.IsChecked(package.Key);
            }

            _vm.RefreshCounts();
            _vm.FlushSelection();
            ScheduleRelayout();
            UpdateSelectionBar();
        }

        private void OpenDataDir()
        {
            try
            {
                AppPaths.EnsureDataDir();

                // 必须给路径加引号：数据目录默认落在 exe 旁边的 Data\，而 exe 很可能位于
                // 「C:\Program Files\...」或带空格的用户名目录下。Process.Start 的第二个参数
                // 是**一整条命令行**，不加引号会被 explorer 按空格拆成两个参数，结果要么
                // 打开「文档」而不是目标目录，要么直接报错。
                var startInfo = new ProcessStartInfo();
                startInfo.FileName = "explorer.exe";
                startInfo.Arguments = "\"" + AppPaths.DataDir + "\"";
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                if (_log != null)
                {
                    _log.Warn("打开数据目录失败：" + ex.Message);
                }

                Flash("打开数据目录失败：" + ex.Message);
            }
        }

        // ══════════════════════════ 状态条 / 日志抽屉 ══════════════════════════

        /// <summary>状态条右侧（版本、数据目录）与警告徽章。徽章收起时也必须看得见。</summary>
        private void UpdateStatusChrome()
        {
            WingetVersionText.Text = "winget " + (string.IsNullOrEmpty(_vm.WingetVersion) ? "未知" : _vm.WingetVersion);

            int warns = 0;
            int errors = 0;
            IList<LogEntry> entries = LogPanel.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                LogEntry entry = entries[i];
                if (entry == null)
                {
                    continue;
                }

                if (string.Equals(entry.Tag, LogEntry.TagWarn, StringComparison.Ordinal))
                {
                    warns++;
                }
                else if (string.Equals(entry.Tag, LogEntry.TagError, StringComparison.Ordinal))
                {
                    errors++;
                }
            }

            if (warns > 0 || errors > 0)
            {
                LogBadgeButton.Visibility = Visibility.Visible;
                LogBadgeButton.Content = errors > 0 ? errors + " 个错误" : warns + " 个警告";
                LogBadgeButton.Foreground = LookupBrush(errors > 0 ? "Brush.Err" : "Brush.Warn", Brushes.DarkOrange);
                LogBadgeButton.Background = LookupBrush("Brush.WarnBg", Brushes.LightYellow);
            }
            else
            {
                LogBadgeButton.Visibility = Visibility.Collapsed;
            }

            StatusDot.Fill = LookupBrush(errors > 0 ? "Brush.Err" : (warns > 0 ? "Brush.Warn" : "Brush.Muted3"), Brushes.Gray);
        }

        private Brush LookupBrush(string key, Brush fallback)
        {
            object resource = TryFindResource(key);
            return resource as Brush ?? fallback;
        }

        private bool IsLogDrawerVisible()
        {
            return LogDrawer.Visibility == Visibility.Visible;
        }

        private void SetLogDrawerVisible(bool visible)
        {
            if (visible)
            {
                // 抽屉高度由外层 Border 决定（236），面板自己撑满即可，
                // 不要写死高度：Border 有 1px 描边，写死会溢出 2px
                LogPanel.SetCollapsed(false);
                LogPanel.Height = double.NaN;
                UpdateStatusChrome();
            }

            LogDrawer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
