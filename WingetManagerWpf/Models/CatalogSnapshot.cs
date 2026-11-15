using System.Collections.Generic;
using System.Globalization;
using WingetManagerWpf.Core.Json;

namespace WingetManagerWpf.Models
{
    /// <summary>
    /// 目录统计（独立计数，供统计卡、副标题与导出 JSON 使用）。
    /// <para>
    /// 口径约定（必须与界面一致）：<see cref="Dependencies"/> 与界面上的「运行库 N」、
    /// 以及「运行库」分组同为 **可重装 ∧ IsDependency**；含不可重装的全部运行库另记在
    /// <see cref="DependenciesAll"/>。为什么保留 all：导出 JSON 的消费方（如重装脚本的
    /// 生成逻辑、诊断报告）需要知道「这台机器上到底装了多少运行库」，而界面只关心能重装的那部分，
    /// 两个数字天然不同，混用一个字段会让「界面 31 / JSON 47」这种对不上号的问题复发。
    /// </para>
    /// </summary>
    public sealed class CatalogCounts
    {
        /// <summary>全部唯一包数。</summary>
        public int Total { get; set; }

        /// <summary>可重装数量。</summary>
        public int Reinstallable { get; set; }

        /// <summary>winget 装不上的数量（= Total - Reinstallable）。</summary>
        public int Unsupported { get; set; }

        /// <summary>
        /// 运行库数量，口径为 **可重装 ∧ IsDependency**（= 界面「运行库 N」= 「运行库」分组行数）。
        /// </summary>
        public int Dependencies { get; set; }

        /// <summary>
        /// 运行库数量，口径为**全部 IsDependency**（含不可重装的，与原版口径一致）。
        /// 恒有 <c>DependenciesAll &gt;= Dependencies</c>，差值即「不可重装的运行库」。
        /// </summary>
        public int DependenciesAll { get; set; }
    }

    /// <summary>
    /// 一次扫描的完整快照。UI 一律以内存里的 Package 列表为准重算计数，
    /// 快照里的 <see cref="Counts"/> 只作参考（避免「快照过滤过 / 列表被改过」对不上）。
    /// </summary>
    public sealed class CatalogSnapshot
    {
        public CatalogSnapshot()
        {
            GeneratedAt = string.Empty;
            WingetVersion = "unknown";
            Hostname = string.Empty;
            Os = string.Empty;
            Arch = string.Empty;
            Counts = new CatalogCounts();
            Packages = new List<Package>();
        }

        /// <summary>生成时间，格式 <c>yyyy-MM-dd'T'HH:mm:sszzz</c>。</summary>
        public string GeneratedAt { get; set; }

        /// <summary>扫描耗时（毫秒，含重试退避）。</summary>
        public int DurationMs { get; set; }

        /// <summary>winget 版本；拿不到时为 "unknown"（**不代表没装 winget**）。</summary>
        public string WingetVersion { get; set; }

        public string Hostname { get; set; }

        public string Os { get; set; }

        public string Arch { get; set; }

        public CatalogCounts Counts { get; set; }

        public IList<Package> Packages { get; set; }

        /// <summary>序列化（诊断信息用）；结构与原版 <c>CatalogSnapshot.to_dict()</c> 对齐。</summary>
        public JsonValue ToJson()
        {
            JsonValue root = JsonValue.NewObject();
            root.Set("generatedAt", JsonValue.From(GeneratedAt));
            root.Set("durationMs", JsonValue.From(DurationMs));
            root.Set("wingetVersion", JsonValue.From(WingetVersion));

            JsonValue machine = JsonValue.NewObject();
            machine.Set("hostname", JsonValue.From(Hostname));
            machine.Set("os", JsonValue.From(Os));
            machine.Set("arch", JsonValue.From(Arch));
            root.Set("machine", machine);

            JsonValue counts = JsonValue.NewObject();
            counts.Set("total", JsonValue.From(Counts != null ? Counts.Total : 0));
            counts.Set("reinstallable", JsonValue.From(Counts != null ? Counts.Reinstallable : 0));
            counts.Set("unsupported", JsonValue.From(Counts != null ? Counts.Unsupported : 0));
            // dependencies：与界面「运行库 N」同口径（可重装 ∧ IsDependency）
            counts.Set("dependencies", JsonValue.From(Counts != null ? Counts.Dependencies : 0));
            // dependencies_all：含不可重装的全部运行库，供诊断/脚本侧使用
            counts.Set("dependencies_all", JsonValue.From(Counts != null ? Counts.DependenciesAll : 0));
            root.Set("counts", counts);

            JsonValue packages = JsonValue.NewArray();
            if (Packages != null)
            {
                foreach (Package package in Packages)
                {
                    packages.Add(package != null ? package.ToJson() : JsonValue.Null);
                }
            }

            root.Set("packages", packages);
            return root;
        }

        /// <summary>「2024-05-01T12:00:00+08:00」风格的时间戳。</summary>
        public static string FormatTimestamp(System.DateTimeOffset value)
        {
            return value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }
    }
}
