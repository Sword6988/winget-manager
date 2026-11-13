using System.Text.Json.Serialization;

namespace WingetManager.Models;

/// <summary>
/// 包 ID 的形态（对应 <c>catalog.ID_KIND_*</c>）。仅用于展示/诊断，不参与分组。
/// </summary>
public static class PackageIdKind
{
    /// <summary>MSIX / UWP 包（<c>msix\...</c>）。</summary>
    public const string Msix = "msix";

    /// <summary>注册表安装项（<c>arp\...</c> 或含反斜杠）。</summary>
    public const string Registry = "registry";

    /// <summary>标准 winget 目录 ID。</summary>
    public const string Winget = "winget";
}

/// <summary>
/// 目录里的一个包（对应 <c>catalog.Package</c>）。
///
/// 分组判定只有两个输入：<see cref="Reinstallable"/>（能不能被 winget 重装）与
/// <see cref="IsDependency"/>（是不是运行库/系统组件）。界面据此分
/// 「软件 / 运行库 / winget 不支持」三组，不涉及任何关键词分类。
/// </summary>
public sealed class Package
{
    /// <summary>包 ID（分组、去重、导出都用它）。</summary>
    [JsonPropertyName("packageIdentifier")]
    public string PackageIdentifier { get; set; } = string.Empty;

    /// <summary>显示名称（空则回退成包 ID）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>已安装版本。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>可用版本。</summary>
    [JsonPropertyName("availableVersion")]
    public string AvailableVersion { get; set; } = string.Empty;

    /// <summary>源（winget / msstore / 空）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>包 ID 形态，见 <see cref="PackageIdKind"/>。</summary>
    [JsonPropertyName("idKind")]
    public string IdKind { get; set; } = PackageIdKind.Winget;

    /// <summary>能不能被 winget 重装：只看「源」列 ∈ {winget, msstore}。</summary>
    [JsonPropertyName("reinstallable")]
    public bool Reinstallable { get; set; }

    /// <summary>
    /// 是不是运行库/系统组件（展示用角标 + 统计卡计数，<b>与分组无关</b>）。
    /// 元数据核查会就地改判这个字段（缓存 True 覆盖关键词初判）。
    /// </summary>
    [JsonPropertyName("isDependency")]
    public bool IsDependency { get; set; }
}
