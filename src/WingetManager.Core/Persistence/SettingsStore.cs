using System;
using Microsoft.Win32;
using WingetManager.Infra;

namespace WingetManager.Persistence;

/// <summary>窗口位置与尺寸（对应 Python 版 <c>win/geometry</c> + <c>win/maximized</c>）。</summary>
/// <param name="X">左上角 X（屏幕坐标）。</param>
/// <param name="Y">左上角 Y（屏幕坐标）。</param>
/// <param name="Width">宽。</param>
/// <param name="Height">高。</param>
/// <param name="IsMaximized">是否最大化。</param>
public sealed record WindowBounds(int X, int Y, int Width, int Height, bool IsMaximized)
{
    /// <summary>默认窗口尺寸（1240×880，与 Python 版一致）。</summary>
    public static WindowBounds Default { get; } = new(120, 80, 1240, 880, false);
}

/// <summary>
/// 设置持久化：注册表 <c>HKCU\Software\Shibeng\WingetManager</c>
/// （对应 Python 版 QSettings(Shibeng, WingetManager)）。
///
/// 为什么不用 <c>ApplicationData.LocalSettings</c>：未打包应用该 API 依赖应用身份，
/// 且会丢老版本用户数据（PRD Q3 已裁定）。键名沿用原版，两版本可互读。
/// </summary>
public sealed class SettingsStore
{
    /// <summary>沿用原版的键路径。</summary>
    public const string DefaultKeyPath = @"Software\Shibeng\WingetManager";

    private readonly string _keyPath;
    private readonly LogService _log;

    /// <summary>
    /// 创建设置存储。
    /// </summary>
    /// <param name="keyPath">注册表子键；null 用 <see cref="DefaultKeyPath"/>（测试可传临时键）。</param>
    /// <param name="log">日志服务；null 用共享实例。</param>
    public SettingsStore(string? keyPath = null, LogService? log = null)
    {
        _keyPath = string.IsNullOrWhiteSpace(keyPath) ? DefaultKeyPath : keyPath;
        _log = log ?? LogService.Shared;
    }

    /// <summary>注册表子键路径。</summary>
    public string KeyPath => _keyPath;

    /// <summary>
    /// 上次导出路径（<c>export/path</c>）；没有记录返回空串。
    /// </summary>
    /// <returns>路径或空串。</returns>
    public string GetLastExportPath() => ReadString("export/path", string.Empty);

    /// <summary>记录上次导出路径。</summary>
    /// <param name="path">导出文件完整路径。</param>
    public void SetLastExportPath(string path) => WriteString("export/path", path ?? string.Empty);

    /// <summary>
    /// 上次的分组筛选（<c>view/category</c>，沿用原版键名：值与 Python 版一致才可互通）。
    /// </summary>
    /// <returns>分组名或空串。</returns>
    public string GetLastCategory() => ReadString("view/category", string.Empty);

    /// <summary>记录分组筛选。</summary>
    /// <param name="category">分组名；空串表示「全部」。</param>
    public void SetLastCategory(string? category) => WriteString("view/category", category ?? string.Empty);

    /// <summary>日志面板是否展开（<c>view/logExpanded</c>）。</summary>
    /// <returns>是否展开。</returns>
    public bool GetLogExpanded() => ReadInt("view/logExpanded", 1) != 0;

    /// <summary>记录日志面板展开状态。</summary>
    /// <param name="expanded">是否展开。</param>
    public void SetLogExpanded(bool expanded) => WriteInt("view/logExpanded", expanded ? 1 : 0);

    /// <summary>
    /// 读取窗口位置尺寸；没有记录或数据不完整返回 <see cref="WindowBounds.Default"/>。
    /// </summary>
    /// <returns>窗口 bounds。</returns>
    public WindowBounds GetWindowBounds()
    {
        var x = ReadInt("win/x", int.MinValue);
        var y = ReadInt("win/y", int.MinValue);
        var width = ReadInt("win/width", 0);
        var height = ReadInt("win/height", 0);
        var maximized = ReadInt("win/maximized", 0) != 0;

        if (x == int.MinValue || y == int.MinValue || width <= 0 || height <= 0)
        {
            return WindowBounds.Default with { IsMaximized = maximized };
        }

        return new WindowBounds(x, y, width, height, maximized);
    }

    /// <summary>记录窗口位置尺寸。</summary>
    /// <param name="bounds">窗口 bounds。</param>
    public void SetWindowBounds(WindowBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        WriteInt("win/x", bounds.X);
        WriteInt("win/y", bounds.Y);
        WriteInt("win/width", bounds.Width);
        WriteInt("win/height", bounds.Height);
        WriteInt("win/maximized", bounds.IsMaximized ? 1 : 0);
    }

    /// <summary>删除整个键（关于对话框的「重置设置」/ 测试清理用）。</summary>
    public void Clear()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            _log.Warn("清理注册表设置失败：{0}", ex.Message);
        }
    }

    private string ReadString(string name, string fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
            var value = key?.GetValue(name) as string;
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            _log.Warn("读取设置 {0} 失败：{1}", name, ex.Message);
            return fallback;
        }
    }

    /// <summary>
    /// 读整数设置（值统一按字符串存，避免 32/64 位视图差异带来的读不到）。
    /// </summary>
    private int ReadInt(string name, int fallback)
    {
        var raw = ReadString(name, string.Empty);
        if (raw.Length == 0)
        {
            return fallback;
        }

        return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private void WriteString(string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            key?.SetValue(name, value, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            _log.Warn("写入设置 {0} 失败：{1}", name, ex.Message);
        }
    }

    private void WriteInt(string name, int value)
    {
        WriteString(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
