namespace WingetManagerWpf.Models
{
    /// <summary>
    /// settings.json 的 POCO（窗口几何 / 日志高度 / 上次分组 / 上次导出路径）。
    /// 已知差异：原版用 QSettings 存注册表，本版落 JSON 文件，便于随数据目录一起备份。
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>未设置坐标的哨兵值（区别于「坐标正好是 0」）。</summary>
        public const int UnsetCoordinate = int.MinValue;

        /// <summary>窗口左上角 X；未设置为 <see cref="UnsetCoordinate"/>。</summary>
        public int WindowX = UnsetCoordinate;

        /// <summary>窗口左上角 Y；未设置为 <see cref="UnsetCoordinate"/>。</summary>
        public int WindowY = UnsetCoordinate;

        public int WindowWidth = (int)Core.Metrics.WindowWidth;

        public int WindowHeight = (int)Core.Metrics.WindowHeight;

        /// <summary>关闭时是否最大化（最大化时不保存几何，避免还原成满屏）。</summary>
        public bool Maximized;

        /// <summary>日志面板高度（90~420）。</summary>
        public int LogHeight = (int)Core.Metrics.LogDefault;

        /// <summary>日志面板是否展开。</summary>
        public bool LogExpanded = true;

        /// <summary>上次的分组筛选（"" 表示「全部」）。</summary>
        public string Category = string.Empty;

        /// <summary>上次的导出完整路径（"" 表示未导出过）。</summary>
        public string ExportPath = string.Empty;

        /// <summary>是否有可用的窗口坐标（首次启动时没有，需要居中）。</summary>
        public bool HasWindowPosition
        {
            get { return WindowX != UnsetCoordinate && WindowY != UnsetCoordinate; }
        }

        public AppSettings Clone()
        {
            return new AppSettings
            {
                WindowX = WindowX,
                WindowY = WindowY,
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
                Maximized = Maximized,
                LogHeight = LogHeight,
                LogExpanded = LogExpanded,
                Category = Category ?? string.Empty,
                ExportPath = ExportPath ?? string.Empty
            };
        }
    }
}
