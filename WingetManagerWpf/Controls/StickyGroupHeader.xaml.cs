using System;
using System.Windows.Controls;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 吸顶分组头。只负责「显示哪一组」和「被点一下要滚到哪」，
    /// 「现在该显示哪一组」由 MainWindow 根据滚动偏移二分查找后告诉它。
    /// </summary>
    public partial class StickyGroupHeader : UserControl
    {
        public StickyGroupHeader()
        {
            InitializeComponent();

            // 吸顶头本身可点：点它 = 跳到该组的第一行（用户常见的「回到本组开头」手势）
            HeaderView.ToggleRequested += OnHeaderClicked;
        }

        /// <summary>被点击（外层据此滚到该组）。</summary>
        public event EventHandler JumpRequested;

        /// <summary>当前展示的分组名。</summary>
        public string GroupTitle { get; private set; }

        /// <summary>更新显示内容。</summary>
        public void Bind(string title, int selectedCount, int selectableCount)
        {
            GroupTitle = title ?? string.Empty;
            HeaderView.BindSticky(GroupTitle, selectedCount, selectableCount);
        }

        private void OnHeaderClicked(object sender, EventArgs e)
        {
            EventHandler handler = JumpRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
