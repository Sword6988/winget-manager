using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 分组头。两种交互：整行点击折叠/展开、右侧按钮批量勾选本组。
    /// 按钮必须把事件标记为 Handled，否则一次点击会同时触发「折叠」和「批量勾选」。
    /// </summary>
    public partial class GroupHeaderControl : UserControl
    {
        private const string SelectAllLabel = "全选本组";

        private const string AllSelectedLabel = "全不选";

        /// <summary>原始渐变底（hover 结束后要还回去）。</summary>
        private readonly Brush _normalBackground;

        /// <summary>本次绑定是否允许显示批量按钮（「winget 不支持」组整组不可勾选）。</summary>
        private bool _showAction;

        /// <summary>本组是否已全选；决定按钮文字是「全不选」还是「全选本组」。</summary>
        private bool _allSelected;

        public GroupHeaderControl()
        {
            InitializeComponent();

            // 渐变底在构造时就缓存：放在 Bind 里缓存的话，鼠标停在分组头上时刷新
            // 会把 hover 底色当成常态存下来，之后鼠标移开也擦不掉。
            _normalBackground = RootGrid.Background;

            RootGrid.MouseLeftButtonUp += OnRowClick;
            GroupAction.Click += OnGroupActionClick;
            MouseEnter += OnMouseEnter;
            MouseLeave += OnMouseLeave;
        }

        /// <summary>整行点击：请求折叠/展开。</summary>
        public event EventHandler ToggleRequested;

        /// <summary>
        /// 点右侧按钮。<see cref="GroupActionEventArgs.Select"/> 是目标状态。
        /// 一个事件带参数，取代原来的两个事件——动作只有一个，方向由当前状态推出来。
        /// </summary>
        public event EventHandler<GroupActionEventArgs> GroupActionRequested;

        /// <summary>当前绑定的分组名（事件回调里用）。</summary>
        public string GroupTitle { get; private set; }

        /// <summary>
        /// 绑定一个分组。<paramref name="showAction"/> 为 false 时不显示批量按钮
        /// （「winget 不支持」组整组不可勾选，给它「全选本组」只会误导）。
        /// </summary>
        public void Bind(string title, int selectedCount, int selectableCount, bool expanded, bool showAction)
        {
            GroupTitle = title ?? string.Empty;
            TitleText.Text = GroupTitle;
            Pill.Count = selectedCount;
            Pill.Total = selectableCount;
            Pill.Visibility = Visibility.Visible;

            SetExpandedVisual(expanded);

            _showAction = showAction;
            GroupAction.Visibility = showAction ? Visibility.Visible : Visibility.Collapsed;

            // 全部勾满时按钮改成「全不选」——文字永远说下一步做什么，不描述过去。
            // 部分勾选与全未勾都按「全选本组」处理：补满是最可能的意图，再点一次则整体取消。
            _allSelected = selectableCount > 0 && selectedCount >= selectableCount;
            if (showAction)
            {
                GroupAction.Content = _allSelected ? AllSelectedLabel : SelectAllLabel;
            }

            // 整行都可点（折叠），所以无论有没有批量按钮都显示手型光标
            Cursor = Cursors.Hand;
        }

        /// <summary>吸顶头复用同一套外观，但不需要批量按钮与计数胶囊。</summary>
        public void BindSticky(string title, int selectedCount, int selectableCount)
        {
            Bind(title, selectedCount, selectableCount, true, false);
        }

        /// <summary>只翻转折叠三角。折叠/展开是就地改可见性、不重建控件，所以单独给一个入口。</summary>
        public void SetExpandedVisual(bool expanded)
        {
            ChevronRotate.Angle = expanded ? 90 : 0;
        }

        private void OnRowClick(object sender, MouseButtonEventArgs e)
        {
            EventHandler handler = ToggleRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }

            e.Handled = true;
        }

        private void OnGroupActionClick(object sender, RoutedEventArgs e)
        {
            if (!_showAction)
            {
                return;
            }

            EventHandler<GroupActionEventArgs> handler = GroupActionRequested;
            if (handler != null)
            {
                handler(this, new GroupActionEventArgs(!_allSelected));
            }

            // 阻止冒泡到整行：否则批量勾选会连带把这一组折叠起来
            e.Handled = true;
        }

        private void OnMouseEnter(object sender, MouseEventArgs e)
        {
            RootGrid.Background = ThemeLookup.GetBrush(this, "Brush.RowHover", Brushes.WhiteSmoke);
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            // 恢复成 XAML 里定义的渐变（构造时缓存下来的那一份），
            // 不缓存就只能每次重新拼渐变，鼠标划过整列分组头时会白建几十个画刷
            if (_normalBackground != null)
            {
                RootGrid.Background = _normalBackground;
            }
        }
    }

    /// <summary>分组批量勾选请求。</summary>
    public class GroupActionEventArgs : EventArgs
    {
        public GroupActionEventArgs(bool select)
        {
            Select = select;
        }

        /// <summary>目标状态：<c>true</c> = 全选本组，<c>false</c> = 全不选。</summary>
        public bool Select { get; private set; }
    }
}
