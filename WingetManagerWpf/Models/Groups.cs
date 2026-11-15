using System;

namespace WingetManagerWpf.Models
{
    /// <summary>
    /// 三个分组的名称、顺序与归属判定（唯一来源）。
    /// 分组只由两个布尔决定：先看能不能被 winget 重装，再看是不是运行库。
    /// **不涉及任何关键词分类** —— 关键词只影响 IsDependency。
    /// </summary>
    public static class Groups
    {
        /// <summary>软件：可重装 ∧ 非运行库。</summary>
        public const string Software = "软件";

        /// <summary>运行库：可重装 ∧ 运行库。</summary>
        public const string Dependency = "运行库";

        /// <summary>winget 不支持：不可重装（灰显、不可勾选）。</summary>
        public const string Unsupported = "winget 不支持";

        /// <summary>界面渲染顺序：软件 → 运行库 → winget 不支持。</summary>
        public static readonly string[] Order = { Software, Dependency, Unsupported };

        /// <summary>分组排序权重（越小越靠前）。</summary>
        public static int Rank(string group)
        {
            for (int i = 0; i < Order.Length; i++)
            {
                if (string.Equals(Order[i], group, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return Order.Length;
        }

        /// <summary>包属于哪个分组。</summary>
        public static string GroupOf(Package package)
        {
            if (package == null)
            {
                return Unsupported;
            }

            if (!package.Reinstallable)
            {
                return Unsupported;
            }

            return package.IsDependency ? Dependency : Software;
        }

        /// <summary>是否为已知的分组名（用于「上次分组」恢复时的合法性校验）。</summary>
        public static bool IsKnown(string group)
        {
            return string.Equals(group, Software, StringComparison.Ordinal)
                   || string.Equals(group, Dependency, StringComparison.Ordinal)
                   || string.Equals(group, Unsupported, StringComparison.Ordinal);
        }
    }
}
