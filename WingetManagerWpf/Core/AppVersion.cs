namespace WingetManagerWpf.Core
{
    /// <summary>
    /// 版本号单一来源：关于框、诊断信息、日志、AssemblyInfo 都必须是这一个值。
    /// 改版本只改这里（同时改 AssemblyInfo.cs 的四个数字）。
    /// </summary>
    public static class AppVersion
    {
        /// <summary>
        /// 产品版本号。基线对齐 Python 原版的 <c>APP_VERSION</c>（2.18.0），
        /// 此后 WPF 侧独立递增：第三位只在「界面/交互有可见变化」时才动，
        /// 与发布标签 <c>v2.18.1-wpf</c> 严格一一对应，避免用户看到的版本和下载页对不上。
        /// </summary>
        public const string Version = "2.18.1";
    }
}
