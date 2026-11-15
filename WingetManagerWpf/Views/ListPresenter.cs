using System;
using System.Collections.Generic;
using WingetManagerWpf.Models;
using WingetManagerWpf.ViewModels;

namespace WingetManagerWpf.Views
{
    /// <summary>
    /// 列表视图三态（v3）。与「分组筛选」「搜索」正交：
    /// 分组是主筛选、搜索是关键词、视图只决定「看已勾的 / 看还没勾的 / 全看」。
    /// </summary>
    public enum ListViewMode
    {
        All = 0,
        Selected = 1,
        Unselected = 2
    }

    /// <summary>一个分组的渲染数据（分组头 + 该组的行）。</summary>
    public sealed class GroupRender
    {
        public GroupRender(string title)
        {
            Title = title ?? string.Empty;
            Rows = new List<Package>();
            Expanded = true;
        }

        /// <summary>分组名（"软件" / "运行库" / "winget 不支持"）。</summary>
        public string Title { get; private set; }

        /// <summary>该组要渲染的包（保持快照顺序，不在这里再排序）。</summary>
        public IList<Package> Rows { get; private set; }

        /// <summary>是否展开（来自 ViewModel 的折叠集合）。</summary>
        public bool Expanded { get; set; }

        /// <summary>该组已勾选数。</summary>
        public int SelectedCount { get; set; }

        /// <summary>该组可勾选行数（「winget 不支持」组为 0）。</summary>
        public int SelectableCount { get; set; }

        /// <summary>
        /// 该组是否可勾选。「winget 不支持」组整组不可勾，分组头也不显示「全选本组」。
        /// </summary>
        public bool Selectable
        {
            get { return !string.Equals(Title, Groups.Unsupported, StringComparison.Ordinal); }
        }
    }

    /// <summary>
    /// 列表编排：把「全部包 + 筛选条件 + 勾选表 + 折叠表」算成「要渲染哪几组、每组哪些行」。
    /// <para>
    /// 刻意**不引用任何 System.Windows 类型**：它只做数据折叠，测试时可以直接 new 出来断言
    /// （例如「勾了 3 个运行库，运行库组头应显示 3/12」）。渲染由 MainWindow 负责。
    /// </para>
    /// 过滤规则与 <see cref="MainViewModel.VisiblePackages"/> 共用同一套
    /// <see cref="SearchQuery"/> / <see cref="Groups"/>，所以「批量勾选的范围」和
    /// 「屏幕上看到的行」永远是同一批。
    /// <para>
    /// 性能说明：本类只负责「算哪些行」，行控件由 MainWindow 全量创建（无虚拟化）。
    /// 已安装软件 800 行以上时滚动会明显掉帧，届时需要把渲染层换成
    /// <c>ItemsControl + VirtualizingStackPanel</c>，本类的数据折叠逻辑可以原样复用。
    /// </para>
    /// </summary>
    public sealed class ListPresenter
    {
        /// <summary>
        /// 生成渲染分组。空分组不返回（否则列表里会留下一排没有行的分组头）。
        /// </summary>
        public IList<GroupRender> Rebuild(IEnumerable<Package> packages, string activeCategory,
                                          IList<SearchToken> searchTokens, bool onlySelected,
                                          IDictionary<string, bool> checkedMap,
                                          IDictionary<string, bool> expandedMap)
        {
            return Rebuild(packages, activeCategory, searchTokens,
                           onlySelected ? ListViewMode.Selected : ListViewMode.All,
                           checkedMap, expandedMap);
        }

        /// <summary>
        /// v3 重载：视图三态。「未选」只列**可勾选且未勾选**的行——
        /// 「winget 不支持」的行永远不可勾，放进「未选」会让人以为漏了。
        /// </summary>
        public IList<GroupRender> Rebuild(IEnumerable<Package> packages, string activeCategory,
                                          IList<SearchToken> searchTokens, ListViewMode view,
                                          IDictionary<string, bool> checkedMap,
                                          IDictionary<string, bool> expandedMap)
        {
            var sections = new List<GroupRender>();
            var byTitle = new Dictionary<string, GroupRender>(StringComparer.Ordinal);

            for (int i = 0; i < Groups.Order.Length; i++)
            {
                string title = Groups.Order[i];
                var section = new GroupRender(title);
                if (expandedMap != null)
                {
                    bool expanded;
                    if (expandedMap.TryGetValue(title, out expanded))
                    {
                        section.Expanded = expanded;
                    }
                }

                byTitle[title] = section;
            }

            string category = activeCategory ?? string.Empty;

            if (packages != null)
            {
                foreach (Package package in packages)
                {
                    if (package == null)
                    {
                        continue;
                    }

                    string group = Groups.GroupOf(package);
                    if (category.Length > 0 && !string.Equals(group, category, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!SearchQuery.Matches(package, searchTokens))
                    {
                        continue;
                    }

                    bool isChecked = IsChecked(checkedMap, package.Key);
                    if (view == ListViewMode.Selected && !(package.Reinstallable && isChecked))
                    {
                        continue;
                    }

                    if (view == ListViewMode.Unselected && (!package.Reinstallable || isChecked))
                    {
                        continue;
                    }

                    GroupRender section;
                    if (!byTitle.TryGetValue(group, out section))
                    {
                        // 理论上不会发生（Groups.GroupOf 只会返回这三个值），兜底避免吞行
                        continue;
                    }

                    section.Rows.Add(package);
                    if (package.Reinstallable)
                    {
                        section.SelectableCount++;
                        if (isChecked)
                        {
                            section.SelectedCount++;
                        }
                    }
                }
            }

            for (int i = 0; i < Groups.Order.Length; i++)
            {
                GroupRender section = byTitle[Groups.Order[i]];
                if (section.Rows.Count == 0)
                {
                    continue;
                }

                sections.Add(section);
            }

            return sections;
        }

        private static bool IsChecked(IDictionary<string, bool> checkedMap, string key)
        {
            if (checkedMap == null || string.IsNullOrEmpty(key))
            {
                return false;
            }

            bool value;
            return checkedMap.TryGetValue(key, out value) && value;
        }
    }
}
