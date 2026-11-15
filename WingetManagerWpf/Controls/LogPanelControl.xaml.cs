using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WingetManagerWpf.Core;
using WingetManagerWpf.ViewModels;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 运行日志面板。内容直接来自 <see cref="AppLogger.LogEmitted"/>，界面日志与文件日志同源，
    /// 不会出现「界面有这行、文件里没有」的排查困境。
    /// </summary>
    public partial class LogPanelControl : UserControl
    {
        /// <summary>面板里最多保留的行数（文件日志另有 512KB×2 轮转，这里只做界面内存上限）。</summary>
        private const int MaxEntries = 2000;

        private readonly List<LogEntry> _entries = new List<LogEntry>();
        private readonly object _pendingSync = new object();
        private readonly List<LogEntry> _pending = new List<LogEntry>();
        private bool _flushScheduled;

        private AppLogger _logger;
        private bool _collapsed;
        private double _expandedHeight = Metrics.LogDefault;

        private bool _dragging;
        private double _dragAnchorScreenY;
        private double _dragStartHeight;

        public LogPanelControl()
        {
            InitializeComponent();

            LogBox.Document = new FlowDocument();
            LogBox.Document.PagePadding = new Thickness(0);
            LogBox.Document.Blocks.Clear();

            Height = Metrics.LogDefault;

            Grip.MouseLeftButtonDown += OnGripDown;
            Grip.MouseMove += OnGripMove;
            Grip.MouseLeftButtonUp += OnGripUp;
            Grip.LostMouseCapture += OnGripLostCapture;

            LinkCollapse.MouseLeftButtonUp += delegate { SetCollapsed(!_collapsed); };
            LinkCopy.MouseLeftButtonUp += delegate { CopyAll(); };
            LinkSave.MouseLeftButtonUp += delegate { SaveToFile(); };
            LinkClear.MouseLeftButtonUp += delegate { Clear(); };

            LinkCollapse.ToolTip = Strings.LogPanelTitle;
            LinkCopy.ToolTip = Strings.LogCopyTip;
            LinkSave.ToolTip = Strings.LogSaveTip;
        }

        /// <summary>请求底栏 flash 一条提示（复制成功 / 保存成功等）。</summary>
        public event Action<string> FlashRequested;

        /// <summary>折叠状态变化（外层据此同步菜单文案/设置）。</summary>
        public event EventHandler CollapseChanged;

        /// <summary>用户拖拽结束（外层据此把高度写进 settings.json）。</summary>
        public event EventHandler HeightChanged;

        /// <summary>
        /// 新增或清空了一条日志。v3 用它驱动状态条的「N 个警告」徽章 ——
        /// 抽屉收起时也必须能看见「有警告」，否则等于把警告藏起来了。
        /// </summary>
        public event EventHandler EntriesChanged;

        /// <summary>当前是否折叠。</summary>
        public bool IsCollapsed
        {
            get { return _collapsed; }
        }

        /// <summary>展开时的高度（折叠不改它，展开还原用）。</summary>
        public double ExpandedHeight
        {
            get { return _expandedHeight; }
        }

        /// <summary>面板里的日志行（复制/另存/诊断用）。</summary>
        public IList<LogEntry> Entries
        {
            get { return _entries; }
        }

        /// <summary>
        /// 挂上全局日志。必须在 ViewModel 创建**之前**调用，
        /// 否则 ViewModel 构造函数里那几条日志（例如「已恢复上次的分组筛选」）进不了面板。
        /// </summary>
        public void AttachLogger(AppLogger logger)
        {
            if (_logger != null)
            {
                _logger.LogEmitted -= OnLogEmitted;
            }

            _logger = logger;
            if (_logger != null)
            {
                _logger.LogEmitted += OnLogEmitted;
            }
        }

        /// <summary>追加一行（<paramref name="level"/> 认 AppLogger 的 Info/Success/Warn/Error）。</summary>
        public void Append(string level, string text)
        {
            AppendEntry(new LogEntry(DateTime.Now, level, text));
        }

        /// <summary>清空面板（不清文件日志）。</summary>
        public void Clear()
        {
            _entries.Clear();
            LogBox.Document.Blocks.Clear();
            RaiseEntriesChanged();
        }

        /// <summary>折叠/展开。折叠时只留 34 高的标题行。</summary>
        public void SetCollapsed(bool collapsed)
        {
            if (_collapsed == collapsed)
            {
                return;
            }

            _collapsed = collapsed;

            if (collapsed)
            {
                // 记下当前高度再收起：展开时回到用户上次拖到的高度，而不是回到 150
                if (Height > Metrics.LogCollapsed)
                {
                    _expandedHeight = Height;
                }

                Height = Metrics.LogCollapsed;
                LogBox.Visibility = Visibility.Collapsed;
                Grip.Cursor = Cursors.Arrow;
                LinkCollapse.Text = Strings.LogExpand;
            }
            else
            {
                Height = ClampHeight(_expandedHeight);
                LogBox.Visibility = Visibility.Visible;
                Grip.Cursor = Cursors.SizeNS;
                LinkCollapse.Text = Strings.LogCollapse;
                LogBox.ScrollToEnd();
            }

            EventHandler handler = CollapseChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>恢复上次的高度与折叠状态（启动时读 settings.json 后调用）。</summary>
        public void RestoreLayout(int height, bool expanded)
        {
            _expandedHeight = ClampHeight(height);
            Height = _expandedHeight;

            if (!expanded)
            {
                _collapsed = false;
                SetCollapsed(true);
            }
        }

        /// <summary>整段日志文本（复制/另存用）。</summary>
        public string BuildText()
        {
            var builder = new StringBuilder();
            for (int i = 0; i < _entries.Count; i++)
            {
                builder.Append(_entries[i].DisplayLine).Append("\r\n");
            }

            return builder.ToString();
        }

        private void AppendEntry(LogEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            _entries.Add(entry);

            bool atBottom = IsScrolledToBottom();
            LogBox.Document.Blocks.Add(BuildParagraph(entry));
            Trim();

            if (atBottom)
            {
                LogBox.ScrollToEnd();
            }

            RaiseEntriesChanged();
        }

        private void RaiseEntriesChanged()
        {
            EventHandler handler = EntriesChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private Paragraph BuildParagraph(LogEntry entry)
        {
            var paragraph = new Paragraph();
            paragraph.Margin = new Thickness(0);

            var stamp = new Run(entry.TimeText + "  ");
            stamp.Foreground = ThemeLookup.GetBrush(this, "Brush.LogTs", Brushes.Gray);
            paragraph.Inlines.Add(stamp);

            var body = new Run(entry.Text);
            body.Foreground = BrushFor(entry.Tag);
            if (entry.IsEmphasis)
            {
                body.FontWeight = FontWeights.SemiBold;
            }

            paragraph.Inlines.Add(body);
            return paragraph;
        }

        private Brush BrushFor(string tag)
        {
            if (string.Equals(tag, LogEntry.TagSuccess, StringComparison.Ordinal))
            {
                return ThemeLookup.GetBrush(this, "Brush.LogOk", Brushes.Green);
            }

            if (string.Equals(tag, LogEntry.TagWarn, StringComparison.Ordinal))
            {
                return ThemeLookup.GetBrush(this, "Brush.Warn", Brushes.DarkGoldenrod);
            }

            if (string.Equals(tag, LogEntry.TagError, StringComparison.Ordinal))
            {
                return ThemeLookup.GetBrush(this, "Brush.LogFail", Brushes.Firebrick);
            }

            if (string.Equals(tag, LogEntry.TagSkip, StringComparison.Ordinal))
            {
                return ThemeLookup.GetBrush(this, "Brush.LogSkip", Brushes.Purple);
            }

            if (string.Equals(tag, LogEntry.TagDone, StringComparison.Ordinal))
            {
                return ThemeLookup.GetBrush(this, "Brush.LogDone", Brushes.MediumPurple);
            }

            return ThemeLookup.GetBrush(this, "Brush.LogFg", Brushes.Black);
        }

        private bool IsScrolledToBottom()
        {
            // 留 4px 容差：字体行高取整会让「到底」时仍差 0.5px，不加容差就永远不自动滚
            return LogBox.VerticalOffset >= LogBox.ExtentHeight - LogBox.ViewportHeight - 4;
        }

        private void Trim()
        {
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(0);

                Block first = LogBox.Document.Blocks.FirstBlock;
                if (first == null)
                {
                    break;
                }

                LogBox.Document.Blocks.Remove(first);
            }
        }

        private void CopyAll()
        {
            if (_entries.Count == 0)
            {
                return;
            }

            try
            {
                Clipboard.SetText(BuildText());
                RaiseFlash(Strings.FlashLogCopied(_entries.Count));
            }
            catch (Exception)
            {
                // 剪贴板被别的进程独占时会抛 COMException：这是常见现象，不打扰用户
            }
        }

        private void SaveToFile()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog();
            dialog.Title = Strings.LogSaveDialogTitle;
            dialog.Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
            dialog.DefaultExt = ".log";
            dialog.AddExtension = true;
            dialog.FileName = "winget-manager-"
                               + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log";

            bool? ok = dialog.ShowDialog(Window.GetWindow(this));
            if (ok != true)
            {
                return;
            }

            try
            {
                File.WriteAllText(dialog.FileName, BuildText(), new UTF8Encoding(true));
                RaiseFlash(Strings.FlashLogSaved(Path.GetFileName(dialog.FileName)));
            }
            catch (Exception ex)
            {
                RaiseFlash(Strings.FlashLogSaveFailed(ex.Message));
            }
        }

        private void RaiseFlash(string message)
        {
            Action<string> handler = FlashRequested;
            if (handler != null)
            {
                handler(message);
            }
        }

        private static double ClampHeight(double value)
        {
            if (value < Metrics.LogMin)
            {
                return Metrics.LogMin;
            }

            return value > Metrics.LogMax ? Metrics.LogMax : value;
        }

        // ══════════════ 抓手拖拽 ══════════════

        private void OnGripDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // 双击抓手 = 折叠/展开，和很多日志面板的习惯一致
                SetCollapsed(!_collapsed);
                e.Handled = true;
                return;
            }

            if (_collapsed)
            {
                return;
            }

            _dragging = true;
            _dragStartHeight = Height;

            // 用屏幕坐标做锚点：面板高度一变，鼠标相对控件的位置就跟着变，
            // 用控件坐标会形成正反馈，拖起来像在做加速
            _dragAnchorScreenY = PointToScreen(e.GetPosition(this)).Y;
            Grip.CaptureMouse();
            e.Handled = true;
        }

        private void OnGripMove(object sender, MouseEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            double currentY = PointToScreen(e.GetPosition(this)).Y;
            double delta = _dragAnchorScreenY - currentY;
            double next = ClampHeight(_dragStartHeight + delta);

            if (Math.Abs(next - Height) < 0.5)
            {
                return;
            }

            Height = next;
            _expandedHeight = next;
        }

        private void OnGripUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            Grip.ReleaseMouseCapture();

            EventHandler handler = HeightChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }

            e.Handled = true;
        }

        private void OnGripLostCapture(object sender, MouseEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;

            EventHandler handler = HeightChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // ══════════════ 日志事件（可能在后台线程触发） ══════════════

        private void OnLogEmitted(DateTime timestamp, string level, string text)
        {
            var entry = new LogEntry(timestamp, level, text);

            if (!Dispatcher.CheckAccess())
            {
                // 后台线程（目录构建、元数据核查）也会写日志：
                // 攒到一批再投递，避免每来一行就 BeginInvoke 一次
                lock (_pendingSync)
                {
                    _pending.Add(entry);
                    if (_flushScheduled)
                    {
                        return;
                    }

                    _flushScheduled = true;
                }

                ScheduleFlush();
                return;
            }

            AppendEntry(entry);
        }

        private void ScheduleFlush()
        {
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushPending));
            }
            catch (Exception)
            {
                // 窗口正在关闭、Dispatcher 已停：丢掉这批界面日志即可（文件日志已经写了）
                lock (_pendingSync)
                {
                    _pending.Clear();
                    _flushScheduled = false;
                }
            }
        }

        private void FlushPending()
        {
            List<LogEntry> batch;
            lock (_pendingSync)
            {
                batch = new List<LogEntry>(_pending);
                _pending.Clear();
                _flushScheduled = false;
            }

            for (int i = 0; i < batch.Count; i++)
            {
                AppendEntry(batch[i]);
            }
        }
    }
}
