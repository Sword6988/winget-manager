using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WingetManager.Models;

/// <summary>机器信息（对应 Python 版 snapshot.machine）。</summary>
public sealed class MachineInfo
{
    /// <summary>主机名。</summary>
    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    /// <summary>操作系统描述（形如 <c>Windows 10.0.26100 (26100)</c>）。</summary>
    [JsonPropertyName("os")]
    public string Os { get; set; } = string.Empty;

    /// <summary>处理器架构（AMD64 / ARM64 / x86）。</summary>
    [JsonPropertyName("arch")]
    public string Arch { get; set; } = string.Empty;
}

/// <summary>统计计数（对应 Python 版 snapshot.counts 的四个键）。</summary>
public sealed class CatalogCounts
{
    /// <summary>去重后总数。</summary>
    [JsonPropertyName("total")]
    public int Total { get; set; }

    /// <summary>可被 winget 重装的数量。</summary>
    [JsonPropertyName("reinstallable")]
    public int Reinstallable { get; set; }

    /// <summary>winget 装不上的数量。</summary>
    [JsonPropertyName("unsupported")]
    public int Unsupported { get; set; }

    /// <summary>运行库/系统组件数量。</summary>
    [JsonPropertyName("dependencies")]
    public int Dependencies { get; set; }

    /// <summary>全零计数（空快照/初始态用）。</summary>
    public static CatalogCounts Empty { get; } = new();
}

/// <summary>
/// 一次扫描的完整快照（对应 <c>catalog.CatalogSnapshot</c>）。
/// 界面只读它，改判结果写在 <see cref="Package"/> 实例上（引用共享）。
/// </summary>
public sealed class CatalogSnapshot
{
    /// <summary>生成时间，形如 <c>2026-11-03T14:00:00+0800</c>（与 Python <c>%z</c> 口径一致）。</summary>
    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = string.Empty;

    /// <summary>扫描耗时（毫秒），含互斥重试退避。</summary>
    [JsonPropertyName("durationMs")]
    public int DurationMs { get; set; }

    /// <summary>winget 版本（拿不到时为 <c>unknown</c>）。</summary>
    [JsonPropertyName("wingetVersion")]
    public string WingetVersion { get; set; } = "unknown";

    /// <summary>机器信息。</summary>
    [JsonPropertyName("machine")]
    public MachineInfo Machine { get; set; } = new();

    /// <summary>统计计数。</summary>
    [JsonPropertyName("counts")]
    public CatalogCounts Counts { get; set; } = new();

    /// <summary>去重排序后的包列表（可重装在前，组内按名称升序）。</summary>
    [JsonPropertyName("packages")]
    public IReadOnlyList<Package> Packages { get; set; } = Array.Empty<Package>();
}
