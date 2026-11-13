using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WingetManager.Models;

/// <summary>
/// 一条 winget 子进程的执行结果（对应 <c>winget_cli.CommandResult</c>）。
/// </summary>
public sealed class CommandResult
{
    /// <summary>完整 argv（第 0 项是 winget.exe 路径），日志与诊断用。</summary>
    [JsonPropertyName("args")]
    public IReadOnlyList<string> Args { get; set; } = new List<string>();

    /// <summary>进程退出码。</summary>
    [JsonPropertyName("returnCode")]
    public int ReturnCode { get; set; }

    /// <summary>已解码的标准输出。</summary>
    [JsonPropertyName("stdout")]
    public string Stdout { get; set; } = string.Empty;

    /// <summary>已解码的标准错误。</summary>
    [JsonPropertyName("stderr")]
    public string Stderr { get; set; } = string.Empty;

    /// <summary>该命令总耗时（毫秒，含互斥重试退避；由仓储层在重试结束后回填）。</summary>
    [JsonPropertyName("durationMs")]
    public int DurationMs { get; set; }

    /// <summary>退出码是否为 0。</summary>
    [JsonIgnore]
    public bool Ok => ReturnCode == 0;

    /// <summary>stdout + stderr 的合并视图（判定「一个字都没输出」时用）。</summary>
    public string Combined()
    {
        return string.IsNullOrEmpty(Stderr) ? Stdout ?? string.Empty : (Stdout ?? string.Empty) + "\n" + Stderr;
    }
}
