using System.Collections.Generic;

namespace WingetManagerWpf.Models
{
    /// <summary>按来源统计可重装包（winget / msstore），用于导出后的提示。</summary>
    public sealed class SourceBreakdown
    {
        public SourceBreakdown()
        {
        }

        public SourceBreakdown(int winget, int msStore)
        {
            Winget = winget;
            MsStore = msStore;
        }

        public int Winget { get; set; }

        public int MsStore { get; set; }

        /// <summary>= 可重装勾选包数。</summary>
        public int Total
        {
            get { return Winget + MsStore; }
        }
    }

    /// <summary>
    /// 导出结果。<see cref="Files"/> 按实际写入顺序排列：json → bat（可选）→ txt（可选）。
    /// </summary>
    public sealed class ExportResult
    {
        public ExportResult()
        {
            Files = new List<string>();
            Breakdown = new SourceBreakdown();
            JsonPath = string.Empty;
        }

        public IList<string> Files { get; set; }

        /// <summary>勾选并写进清单的软件包数。</summary>
        public int PackageCount { get; set; }

        /// <summary>「无法自动重装清单」里的条目数（0 表示没生成该文件）。</summary>
        public int UninstallableCount { get; set; }

        public SourceBreakdown Breakdown { get; set; }

        /// <summary>apps.json 的完整路径（写入 settings 供下次默认值用）。</summary>
        public string JsonPath { get; set; }
    }
}
