using System.Threading.Tasks;

namespace WingetManager.ViewModels;

/// <summary>
/// ViewModel 需要的窗口能力（导出取路径、弹提示）。
///
/// 抽成接口的收益：<see cref="MainViewModel"/> 不直接摸 <c>FileSavePicker</c>/<c>ContentDialog</c>，
/// 导出编排（选中项 → ExportService → 提示文案）可以纯粹地被单测覆盖；
/// 窗口句柄初始化这类「只有真窗口能做」的事留在 <c>MainWindow</c> 里。
/// </summary>
public interface IMainView
{
    /// <summary>弹出「另存为」让用户选清单路径。</summary>
    /// <param name="suggestedPath">默认路径（已按「上次路径 → D:\ → 文档」链算好）。</param>
    /// <returns>用户选的完整路径；取消返回 null。</returns>
    Task<string?> PickExportPathAsync(string suggestedPath);

    /// <summary>弹一条信息提示。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    Task ShowMessageAsync(string title, string message);

    /// <summary>弹确认框。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    /// <param name="primaryButtonText">主按钮文案。</param>
    /// <param name="closeButtonText">关闭按钮文案。</param>
    /// <returns>用户是否点了主按钮。</returns>
    Task<bool> ConfirmAsync(string title, string message, string primaryButtonText, string closeButtonText);
}
