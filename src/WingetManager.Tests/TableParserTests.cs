using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WingetManager.Models;
using WingetManager.Services;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// 表格解析用例：显示宽度切列 / 中英表头 / 噪声行 / 空格兜底 / 退化用例。
/// 断言数据来自原 Python 版（tools/test_smoke.py）与真实 winget 输出结构。
/// </summary>
public sealed class TableParserTests
{
    private static string TestDataPath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "TestData", fileName);

    private static string ReadTestData(string fileName)
        => File.ReadAllText(TestDataPath(fileName));

    [Fact]
    public void DisplayWidth_AsciiCountsOnePerChar()
    {
        Assert.Equal(0, TableParser.DisplayWidth(string.Empty));
        Assert.Equal(11, TableParser.DisplayWidth("Google.Chrm"));
        Assert.Equal(3, TableParser.DisplayWidth("Git"));
    }

    [Fact]
    public void DisplayWidth_ChineseCountsTwoPerChar()
    {
        // 「名称」两个字 = 4 列，这是整个按显示宽度切列算法的前提
        Assert.Equal(4, TableParser.DisplayWidth("名称"));
        Assert.Equal(12, TableParser.DisplayWidth("火绒安全软件"));
        Assert.Equal(12, TableParser.DisplayWidth("名称    版本"));
    }

    [Fact]
    public void DisplayWidth_CombiningMarkCountsZero()
    {
        // e + U+0301（组合重音符）在显示上仍占 1 列
        Assert.Equal(1, TableParser.DisplayWidth("e\u0301"));
        Assert.Equal(2, TableParser.DisplayWidth("ab\u0301"));
    }

    [Fact]
    public void DisplayWidth_EmojiCountsTwo()
    {
        Assert.Equal(2, TableParser.DisplayWidth("😀"));
        Assert.Equal(4, TableParser.DisplayWidth("😀😀"));
    }

    [Fact]
    public void ParseListOutput_ChineseHeaderSplitsByDisplayWidth()
    {
        var packages = TableParser.ParseListOutput(ReadTestData("winget-list-zh.txt"));

        Assert.Equal(6, packages.Count);

        var chrome = packages[0];
        Assert.Equal("Google Chrome", chrome.Name);
        Assert.Equal("Google.Chrome", chrome.PackageId);
        Assert.Equal("131.0.6778.86", chrome.Version);
        Assert.Equal("131.0.6778.86", chrome.Available);
        Assert.Equal("winget", chrome.Source);

        // 第二行：名称是汉字（显示宽度 12），必须靠「显示列号」而不是字符下标切片
        var huorong = packages[1];
        Assert.Equal("火绒安全软件", huorong.Name);
        Assert.Equal("Huorong.Huorong", huorong.PackageId);
        Assert.Equal("6.0.4.2", huorong.Version);
        Assert.Equal(string.Empty, huorong.Available);
        Assert.Equal("winget", huorong.Source);

        var vcredist = packages[2];
        Assert.Equal("Microsoft Visual C++ 2015-2022 x64", vcredist.Name);
        Assert.Equal("Microsoft.VCRedist.2015+.x64", vcredist.PackageId);
        Assert.Equal("14.40.33810", vcredist.Version);
        Assert.Equal("14.42.34438", vcredist.Available);

        Assert.Equal("msstore", packages[3].Source);
        Assert.Equal("Microsoft.WindowsStore_8wekyb3d8bbwe", packages[3].PackageId);
    }

    [Fact]
    public void ParseListOutput_KeepsRowWithEmptySource()
    {
        var packages = TableParser.ParseListOutput(ReadTestData("winget-list-zh.txt"));
        var registry = packages.Single(p => p.PackageId == "Vendor.Registry.Tool");

        Assert.Equal("注册表安装项", registry.Name);
        Assert.Equal("3.0.1", registry.Version);
        Assert.Equal(string.Empty, registry.Source);
    }

    [Fact]
    public void ParseListOutput_EnglishHeaderWorks()
    {
        var packages = TableParser.ParseListOutput(ReadTestData("winget-list-en.txt"));

        Assert.Equal(2, packages.Count);
        Assert.Equal("Git", packages[0].Name);
        Assert.Equal("Git.Git", packages[0].PackageId);
        Assert.Equal("2.47.1", packages[0].Version);
        Assert.Equal("winget", packages[0].Source);
        Assert.Equal("voidtools.Everything", packages[1].PackageId);
        Assert.Equal(string.Empty, packages[1].Available);
    }

    [Fact]
    public void ParseListOutput_FallsBackToGapSplitWhenNoSeparator()
    {
        var packages = TableParser.ParseListOutput(ReadTestData("winget-list-gap.txt"));

        Assert.Equal(2, packages.Count);
        Assert.Equal("Visual Studio Tools", packages[0].Name);
        Assert.Equal("Microsoft.VSTOR", packages[0].PackageId);
        Assert.Equal("10.0", packages[0].Version);
        Assert.Equal("winget", packages[0].Source);
        Assert.Equal("Rustup", packages[1].Name);
        Assert.Equal("Rustlang.Rustup", packages[1].PackageId);
    }

    [Fact]
    public void ParseListOutput_FiltersNoiseLines()
    {
        const string Text =
            "名称      ID            版本   源\n" +
            "---------------------------------\n" +
            "Git       Git.Git       2.47.1 winget\n" +
            "\n" +
            "已找到 1 个已安装的程序包。\n";

        var packages = TableParser.ParseListOutput(Text);

        Assert.Single(packages);
        Assert.Equal("Git.Git", packages[0].PackageId);
    }

    [Fact]
    public void ParseListOutput_FiltersEnglishNoiseLines()
    {
        const string Text =
            "Name      Id            Version Source\n" +
            "-------------------------------------\n" +
            "Git       Git.Git       2.47.1  winget\n" +
            "The following packages were found:\n";

        var packages = TableParser.ParseListOutput(Text);

        // 「The following…」这行没有 ID 列内容，靠噪声前缀与空 ID 双重过滤掉
        Assert.Single(packages);
    }

    [Fact]
    public void ParseListOutput_EmptyInputReturnsEmpty()
    {
        Assert.Empty(TableParser.ParseListOutput(string.Empty));
        Assert.Empty(TableParser.ParseListOutput("   \r\n\r\n   "));
        Assert.Empty(TableParser.ParseListOutput("-----"));
    }

    [Fact]
    public void ParseListOutput_SkipsRowsWithoutPackageId()
    {
        const string Text =
            "名称      ID            版本\n" +
            "--------------------------\n" +
            "只有名字                 1.0\n" +
            "Git       Git.Git       2.47.1\n";

        var packages = TableParser.ParseListOutput(Text);

        Assert.Single(packages);
        Assert.Equal("Git.Git", packages[0].PackageId);
    }

    [Fact]
    public void ParseListOutput_HeaderWithoutIdFallsBackToGapSplit()
    {
        // 表头没有 ID 列 → 切列不可信 → 走空格兜底（与本机 winget 行为对齐）
        const string Text =
            "名称      版本     源\n" +
            "---------------------\n" +
            "Rustup    1.27.0   winget\n";

        var packages = TableParser.ParseListOutput(Text);

        Assert.Single(packages);
        Assert.Equal("Rustup", packages[0].Name);
        Assert.Equal("1.27.0", packages[0].PackageId);
    }

    [Fact]
    public void ParseListOutput_PadsShortRows()
    {
        // 行比表头短：缺的单元格补空串，不能因为越界少列
        const string Text =
            "名称       ID               版本     可用     源\n" +
            "-------------------------------------------------\n" +
            "Git        Git.Git          2.47.1\n";

        var packages = TableParser.ParseListOutput(Text);

        Assert.Single(packages);
        Assert.Equal("2.47.1", packages[0].Version);
        Assert.Equal(string.Empty, packages[0].Available);
        Assert.Equal(string.Empty, packages[0].Source);
    }

    [Fact]
    public void ParseListOutput_HandlesBomPrefixedFirstLine()
    {
        // 解码链会吃掉 BOM；即使漏到这里，也不该把第一列名称带成 U+FEFF
        var text = "\uFEFF" + ReadTestData("winget-list-en.txt");
        var packages = TableParser.ParseListOutput(text);

        Assert.Equal(2, packages.Count);
    }

    [Fact]
    public void ParseListOutput_LfOnlyLineEndingsWork()
    {
        var text = ReadTestData("winget-list-en.txt").Replace("\r\n", "\n", StringComparison.Ordinal);
        var packages = TableParser.ParseListOutput(text);

        Assert.Equal(2, packages.Count);
        Assert.Equal("Git.Git", packages[0].PackageId);
    }

    [Fact]
    public void SplitLines_HandlesAllLineEndings()
    {
        var lines = TableParser.SplitLines("a\r\nb\nc\rd");

        Assert.Equal(new[] { "a", "b", "c", "d" }, lines.ToArray());
    }

    [Fact]
    public void ParseListOutput_OverflowingNameKeepsRowButGarblesColumns()
    {
        // 名称超宽挤占后续列：真实 winget 会自己截断名称，这里只保证「不丢行」——
        // 与 Python 版行为一致（按表头显示列号硬切，超宽必然串列）。
        const string Text =
            "名称        ID                版本     源\n" +
            "------------------------------------------\n" +
            "A Very Long Application Name That Overflows  Git.Git   2.47.1   winget\n";

        IReadOnlyList<RawPackage> packages = TableParser.ParseListOutput(Text);

        Assert.Single(packages);
        Assert.NotEmpty(packages[0].Name);
        Assert.NotEmpty(packages[0].PackageId);
    }
}
