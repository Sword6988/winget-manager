using System;
using System.Windows;
using System.Windows.Controls;
using WingetManagerWpf.Core;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 空态面板。只负责显示，按钮点击只抛 <see cref="ClearRequested"/>，
    /// 具体清哪些筛选由 ViewModel 决定。
    /// </summary>
    public partial class EmptyStatePanel : UserControl
    {
        public EmptyStatePanel()
        {
            InitializeComponent();
            ClearButton.Click += OnClearClick;
            ClearButton.Content = Strings.ButtonClearFilters;
        }

        /// <summary>用户点了「清除筛选」。</summary>
        public event EventHandler ClearRequested;

        /// <summary>
        /// 显示文案。<paramref name="showClearButton"/> 为 false 时不给按钮
        /// （「未检测到 winget」「扫描失败」这类空态没有筛选可清，给按钮只会误导）。
        /// </summary>
        public void ShowMessage(string message, bool showClearButton)
        {
            MessageText.Text = message ?? string.Empty;
            ClearButton.Visibility = showClearButton ? Visibility.Visible : Visibility.Collapsed;
            Visibility = Visibility.Visible;
        }

        /// <summary>隐藏面板。</summary>
        public void HidePanel()
        {
            Visibility = Visibility.Collapsed;
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            EventHandler handler = ClearRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
