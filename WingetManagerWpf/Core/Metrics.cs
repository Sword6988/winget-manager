namespace WingetManagerWpf.Core
{
    /// <summary>
    /// 布局常量的唯一来源。XAML 里用 {x:Static core:Metrics.RowHeight} 引用，
    /// 代码里直接读 —— **禁止**在别处再写 46 / 52 / 1240 这类数字，
    /// 否则改行高会出现「列表行改了、吸顶头没改」的对不齐。
    /// 单位：尺寸是设备无关像素（96 DPI 下的 1/96 英寸），时间是毫秒。
    /// </summary>
    public static class Metrics
    {
        // ── 行 / 分组 ──
        /// <summary>列表行固定高度（v3：46→48，命中区更大）。</summary>
        public const double RowHeight = 48;
        /// <summary>分组头高度（v3：42→36，把省下的 6px 让给列表）。</summary>
        public const double GroupHeaderHeight = 36;
        /// <summary>吸顶分组头高度（与分组头一致，便于复用同一控件）。</summary>
        public const double StickyBarHeight = 36;
        /// <summary>吸顶头下方的渐变阴影高度。</summary>
        public const double StickyShadow = 4;

        // ── 窗口 / 布局 ──
        public const double TitleBarHeight = 44;
        /// <summary>命令条高度（v3 合并「工具条 + 分组胶囊行」后的单行）。</summary>
        public const double CommandBarHeight = 56;
        /// <summary>状态条高度（v3 用 32 一行顶掉 58 底栏 + 150 日志面板）。</summary>
        public const double StatusBarHeight = 32;
        /// <summary>浮动选择条高度。</summary>
        public const double SelectionBarHeight = 46;
        /// <summary>选择条浮在列表上时要补的底部留白，否则末行永远被压住。</summary>
        public const double ListBottomPad = 64;
        /// <summary>日志抽屉高度（覆盖在列表下缘，不挤占布局）。</summary>
        public const double LogDrawerHeight = 236;
        public const double BottomBarHeight = 58;
        public const double WindowWidth = 1240;
        public const double WindowHeight = 880;
        public const double MinWidth = 960;
        public const double MinHeight = 600;
        /// <summary>页面左右内边距（v3 固定 16，不再随窗宽换挡）。</summary>
        public const double PagePadding = 16;
        /// <summary>WindowChrome 的缩放边缘厚度。</summary>
        public const double ResizeEdge = 7;
        /// <summary>窄窗阈值：小于此宽度隐藏标签列与版本列。</summary>
        public const double NarrowWidth = 1080;

        // ── 统计卡 / 勾选框 / 版本列 ──
        public const double StatCardWidth = 104;
        public const double StatCardHeight = 70;
        public const double CheckBoxSize = 20;
        /// <summary>不可勾选行的占位宽度（20 + 11），保证名称列不错位。</summary>
        public const double CheckBoxPlaceholder = 31;
        public const double PillHeight = 20;
        public const double VersionColumnWidth = 110;

        // ── 日志面板 ──
        public const double LogMin = 90;
        public const double LogMax = 420;
        public const double LogDefault = 150;
        /// <summary>收起时的卡片高度（只露标题行）。</summary>
        public const double LogCollapsed = 34;

        // ── 圆角 ──
        public const double CornerCard = 16;
        public const double CornerInner = 12;

        // ── 节流 / 轮询（毫秒） ──
        public const int DebounceSearchMs = 130;
        public const int DebounceSaveMs = 300;
        public const int FlashMs = 2200;
        public const int EnrichBatch = 5;
        public const int MutexRetryMax = 2;
        public const int MutexBackoffMs = 400;
        public const int PollMs = 200;
        public const int ScanTickMs = 100;
        public const int FirstScanDelayMs = 80;
    }
}
