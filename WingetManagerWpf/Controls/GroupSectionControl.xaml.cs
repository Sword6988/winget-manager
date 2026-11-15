using System.Windows;
using System.Windows.Controls;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 列表里的一个分组块。<see cref="Header"/> / <see cref="Rows"/> 交给 MainWindow
    /// 往里塞行控件（行的创建与事件绑定都在 MainWindow 里做，这里只负责容器与折叠）。
    /// </summary>
    public partial class GroupSectionControl : UserControl
    {
        public GroupSectionControl()
        {
            InitializeComponent();
        }

        /// <summary>分组头（x:Name 用 HeaderView 以免和本属性同名）。</summary>
        public GroupHeaderControl Header
        {
            get { return HeaderView; }
        }

        /// <summary>行的容器。</summary>
        public StackPanel Rows
        {
            get { return RowHost; }
        }

        /// <summary>折叠/展开：只改可见性，不动子元素。</summary>
        public void SetExpanded(bool expanded)
        {
            RowHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
