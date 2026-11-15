namespace WingetManagerWpf.ViewModels
{
    /// <summary>
    /// ViewModel 需要的「外壳能力」集合：焦点、提示条、剪贴板、弹窗、日志区、滚动、重排。
    /// <para>
    /// 为什么要这层接口：<see cref="MainViewModel"/> 里全是业务编排（扫描、筛选、勾选、
    /// 内存计数），一旦直接 <c>MessageBox.Show</c> / <c>Clipboard.SetText</c> /
    /// <c>DispatcherTimer</c>，就再也无法在控制台工程里链接源码做自测了。
    /// 把「碰 WPF 的那一下」全部收敛到 <c>Views/MainWindow</c>，ViewModel 只依赖 System.dll。
    /// </para>
    /// </summary>
    public interface IMainShell
    {
        /// <summary>聚焦搜索框（Ctrl+F）。</summary>
        void FocusSearch();

        /// <summary>底栏临时提示（2200ms 后自动回落成状态文案）。</summary>
        void Flash(string message);

        /// <summary>写剪贴板；失败时内部会 flash 提示，不抛异常。</summary>
        void CopyToClipboard(string text);

        /// <summary>打开导出对话框（阶段 D 接入完整版；当前由主窗口兜底实现）。</summary>
        void OpenExportDialog();

        /// <summary>弹「未检测到 winget」引导框；<paramref name="detail"/> 是系统返回的原文。</summary>
        void ShowWingetGuide(string detail);

        /// <summary>弹「关于」窗口。</summary>
        void ShowAbout();

        /// <summary>把列表滚到指定分组（吸顶头点击）。</summary>
        void ScrollToGroup(string group);

        /// <summary>收起/展开日志区（键盘或标题行链接触发）。</summary>
        void SetLogCollapsed(bool collapsed);

        /// <summary>
        /// 请求在 <c>DispatcherPriority.Background</c> 重建列表。
        /// 用 Background 而不是立刻重建：「仅看已选」下取消勾选会改变可见集合，
        /// 若在点击事件里同步重建，被移除的行控件会在自己的一次点击处理中被销毁。
        /// </summary>
        void RequestBackgroundRelayout();
    }
}
