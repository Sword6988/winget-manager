using System.Collections.Generic;
using WingetManager.Models;
using WingetManager.Services;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// 搜索 / 筛选谓词用例：多关键词 AND、<c>id:</c> / <c>name:</c> 前缀、分组胶囊、仅看已选、批量作用域。
/// </summary>
public sealed class FilterEngineTests
{
    private static Package Pkg(string id, string name, bool reinstallable = true, bool dependency = false)
        => new()
        {
            PackageIdentifier = id,
            Name = name,
            Reinstallable = reinstallable,
            IsDependency = dependency,
        };

    private static readonly Package Chrome = Pkg("Google.Chrome", "Google Chrome");
    private static readonly Package Git = Pkg("Git.Git", "Git");
    private static readonly Package Python = Pkg("Python.Python.3.14", "Python 3.14");
    private static readonly Package VcRedist = Pkg("Microsoft.VCRedist.2015+.x64", "Microsoft Visual C++ 2015-2022 x64", dependency: true);
    private static readonly Package Registry = Pkg("Vendor.Registry.Tool", "注册表项", reinstallable: false);

    private static List<Package> All => new() { Chrome, Git, Python, VcRedist, Registry };

    // ── 查询解析 ──────────────────────────────────────────────────────

    [Fact]
    public void ParseQuery_EmptyReturnsNoTerms()
    {
        Assert.Empty(FilterEngine.ParseQuery(null));
        Assert.Empty(FilterEngine.ParseQuery("   "));
    }

    [Fact]
    public void ParseQuery_SplitsOnWhitespaceAndLowercases()
    {
        var terms = FilterEngine.ParseQuery("  Chrome   GIT ");

        Assert.Equal(2, terms.Count);
        Assert.Equal(FilterEngine.TermField.Any, terms[0].Field);
        Assert.Equal("chrome", terms[0].Value);
        Assert.Equal("git", terms[1].Value);
    }

    [Fact]
    public void ParseQuery_UnderstandsIdAndNamePrefixes()
    {
        var terms = FilterEngine.ParseQuery("id:chrome name:google plain");

        Assert.Equal(3, terms.Count);
        Assert.Equal(FilterEngine.TermField.Id, terms[0].Field);
        Assert.Equal("chrome", terms[0].Value);
        Assert.Equal(FilterEngine.TermField.Name, terms[1].Field);
        Assert.Equal("google", terms[1].Value);
        Assert.Equal(FilterEngine.TermField.Any, terms[2].Field);
    }

    [Fact]
    public void ParseQuery_TreatsBarePrefixAsPlainTerm()
    {
        // "id:" / "name:" 后面没内容时不能丢词，否则搜索框会被静默忽略
        var terms = FilterEngine.ParseQuery("id: name:");

        Assert.Equal(2, terms.Count);
        Assert.All(terms, term => Assert.Equal(FilterEngine.TermField.Any, term.Field));
    }

    // ── 匹配 ─────────────────────────────────────────────────────────

    [Fact]
    public void Matches_MultipleKeywordsAreAnd()
    {
        Assert.True(FilterEngine.Matches(Python, FilterEngine.ParseQuery("python 3")));
        Assert.False(FilterEngine.Matches(Chrome, FilterEngine.ParseQuery("chrome firefox")));
    }

    [Fact]
    public void Matches_IdPrefixOnlyLooksAtPackageId()
    {
        var predicate = FilterEngine.BuildPredicate("id:git", null, false);

        Assert.True(predicate(Git));
        Assert.True(predicate(Pkg("Vendor.GitHelper", "Helper"))); // ID 里含 git
        Assert.False(predicate(Pkg("Vendor.Other", "Git 工具")));  // 只有名称含 git，不算
    }

    [Fact]
    public void Matches_NamePrefixOnlyLooksAtName()
    {
        var predicate = FilterEngine.BuildPredicate("name:chrome", null, false);

        Assert.True(predicate(Chrome));
        Assert.False(predicate(Pkg("Google.Chrome.Helper", "Helper")));
    }

    [Fact]
    public void Matches_PlainTermLooksAtNameAndId()
    {
        var predicate = FilterEngine.BuildPredicate("chrom", null, false);

        Assert.True(predicate(Chrome));                      // 名称命中
        Assert.True(predicate(Pkg("Vendor.Chromium", "别的"))); // 包 ID 命中
    }

    // ── 分组胶囊 ──────────────────────────────────────────────────────

    [Fact]
    public void GroupOf_UsesReinstallableThenDependency()
    {
        Assert.Equal(PackageGroups.Software, PackageGroups.Of(Chrome));
        Assert.Equal(PackageGroups.Dependency, PackageGroups.Of(VcRedist));
        Assert.Equal(PackageGroups.Unsupported, PackageGroups.Of(Registry));
    }

    [Fact]
    public void GroupRank_FollowsFixedOrder()
    {
        Assert.Equal(0, PackageGroups.Rank(PackageGroups.Software));
        Assert.Equal(1, PackageGroups.Rank(PackageGroups.Dependency));
        Assert.Equal(2, PackageGroups.Rank(PackageGroups.Unsupported));
        Assert.Equal(3, PackageGroups.Rank("未知分组"));
    }

    [Fact]
    public void BuildPredicate_FiltersByGroup()
    {
        var dependency = FilterEngine.BuildPredicate(null, PackageGroups.Dependency, false);
        var unsupported = FilterEngine.BuildPredicate(null, PackageGroups.Unsupported, false);
        var all = FilterEngine.BuildPredicate(null, PackageGroups.All, false);

        Assert.True(dependency(VcRedist));
        Assert.False(dependency(Chrome));
        Assert.True(unsupported(Registry));
        Assert.True(all(Chrome));
        Assert.True(all(Registry));
    }

    [Fact]
    public void BuildPredicate_CombinesGroupSearchAndOnlySelected()
    {
        var checkedIds = new HashSet<string> { "google.chrome" };
        var predicate = FilterEngine.BuildPredicate("chrome", PackageGroups.Software, true,
            package => checkedIds.Contains(package.PackageIdentifier.ToLowerInvariant()));

        Assert.True(predicate(Chrome));

        checkedIds.Clear();
        Assert.False(predicate(Chrome)); // 取消勾选后立刻不可见
    }

    // ── 批量作用域 ────────────────────────────────────────────────────

    [Fact]
    public void ScopedPackages_WithoutFilterReturnsAllReinstallable()
    {
        var scoped = FilterEngine.ScopedPackages(All, null, null, false);

        Assert.Equal(4, scoped.Count); // 不含「winget 不支持」的注册表项
        Assert.DoesNotContain(Registry, scoped);
    }

    [Fact]
    public void ScopedPackages_WithSearchReturnsOnlyMatched()
    {
        var scoped = FilterEngine.ScopedPackages(All, "chrome", null, false);

        Assert.Single(scoped);
        Assert.Same(Chrome, scoped[0]);
    }

    [Fact]
    public void ScopedPackages_WithGroupReturnsOnlyThatGroup()
    {
        var scoped = FilterEngine.ScopedPackages(All, null, PackageGroups.Dependency, false);

        Assert.Single(scoped);
        Assert.Same(VcRedist, scoped[0]);
    }

    [Fact]
    public void ScopedPackages_UnsupportedGroupIsNeverSelectable()
    {
        var scoped = FilterEngine.ScopedPackages(All, null, PackageGroups.Unsupported, false);

        Assert.Empty(scoped);
    }

    [Fact]
    public void ScopedPackages_OnlySelectedRespectsCheckedSet()
    {
        var checkedIds = new HashSet<string> { "git.git" };
        var scoped = FilterEngine.ScopedPackages(All, null, null, true,
            package => checkedIds.Contains(package.PackageIdentifier.ToLowerInvariant()));

        Assert.Single(scoped);
        Assert.Same(Git, scoped[0]);
    }
}
