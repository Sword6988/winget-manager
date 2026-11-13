using WingetManager.Services;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// 元数据判定用例：段落抽取（中英、折行、tags 列表）、三态判定、信号词大小写。
/// 样本取自原 Python 版 tools/test_smoke.py 的真实 winget show 输出。
/// </summary>
public sealed class RuntimeMetadataServiceTests
{
    /// <summary>Microsoft.VSTOR：描述含 runtime / required to run，安装器 URL 是 redist → True。</summary>
    private const string ShowVstorZh =
        "找到 Microsoft.VSTOR\r\n"
        + "版本: 10.0.60830\r\n"
        + "发布程序: Microsoft Corporation\r\n"
        + "描述: 安装 Microsoft Visual Studio 2010 Tools for Office Runtime，\r\n"
        + "  which is required to run Microsoft Office solutions.\r\n"
        + "标记:\r\n"
        + "  office\r\n"
        + "安装程序 URL: https://download.microsoft.com/download/vstor_redist.exe\r\n";

    /// <summary>Rustup：标记只有 rust/rustlang，描述无信号词 → False。</summary>
    private const string ShowRustup =
        "找到 Rustup\r\n"
        + "版本: 1.27.0\r\n"
        + "描述: Rust 工具链安装与管理器。\r\n"
        + "标记:\r\n"
        + "  rust\r\n"
        + "  rustlang\r\n";

    /// <summary>Windows SDK：英文段落标签，无信号词 → False。</summary>
    private const string ShowSdkEn =
        "Found Windows SDK\r\n"
        + "Version: 10.0.22621\r\n"
        + "Description: Windows SDK provides headers, libraries and tools.\r\n"
        + "Tags:\r\n"
        + "  sdk\r\n"
        + "  windows\r\n";

    [Fact]
    public void AnalyzeRuntimeSignal_DetectsRuntimeAndReportsWord()
    {
        var (isDependency, signal) = RuntimeMetadataService.AnalyzeRuntimeSignal(ShowVstorZh);

        Assert.True(isDependency);
        Assert.Equal("runtime", signal); // 应报出第一个命中的信号词
    }

    [Fact]
    public void AnalyzeRuntimeSignal_ReturnsFalseWhenSectionsParsedButNoSignal()
    {
        Assert.False(RuntimeMetadataService.ParseRuntimeSignal(ShowRustup));
        Assert.False(RuntimeMetadataService.ParseRuntimeSignal(ShowSdkEn));
    }

    [Fact]
    public void AnalyzeRuntimeSignal_ReturnsNullForUnknown()
    {
        // 空输出 / 只有一句话没有描述标记段落 → 未知，绝不当成 False
        Assert.Null(RuntimeMetadataService.ParseRuntimeSignal(string.Empty));
        Assert.Null(RuntimeMetadataService.ParseRuntimeSignal("   \r\n"));
        Assert.Null(RuntimeMetadataService.ParseRuntimeSignal("找不到满足条件的包"));
        Assert.Null(RuntimeMetadataService.ParseRuntimeSignal(null));
    }

    [Fact]
    public void AnalyzeRuntimeSignal_HandlesEnglishLabelsAndFullWidthColon()
    {
        const string En = "Description: Visual C++ redistributable for Visual Studio\r\nTags:\r\n  cpp\r\n";
        Assert.True(RuntimeMetadataService.ParseRuntimeSignal(En));

        const string ZhFullWidth = "描述：此组件是 Visual C++ 可再发行组件（redistributable）。";
        Assert.True(RuntimeMetadataService.ParseRuntimeSignal(ZhFullWidth));
    }

    [Fact]
    public void AnalyzeRuntimeSignal_KeepsWrappedDescriptionAndTagList()
    {
        const string Multi =
            "描述: 用于运行游戏的第一方运行时组件，\r\n"
            + "  与游戏本体分开安装。\r\n"
            + "标记:\r\n"
            + "  game\r\n"
            + "  driver\r\n";

        Assert.True(RuntimeMetadataService.ParseRuntimeSignal(Multi));
    }

    [Fact]
    public void ExtractSections_SplitsDescriptionTagsAndUrls()
    {
        var (description, tags, urls) = RuntimeMetadataService.ExtractSections(ShowVstorZh);

        Assert.Equal(2, description.Count); // 折行描述的两段都在
        Assert.Contains("office", tags);
        Assert.Single(urls);
        Assert.Contains("vstor_redist.exe", urls[0], System.StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractSections_TagsOnSameLineAreAccepted()
    {
        const string Inline = "Found thing\r\nTags: rust rustlang\r\n";

        var (_, tags, _) = RuntimeMetadataService.ExtractSections(Inline);

        Assert.Equal(new[] { "rust", "rustlang" }, tags);
    }

    [Fact]
    public void ExtractSections_TopLevelLineEndsSection()
    {
        const string Text =
            "描述: 一段描述\r\n"
            + "  续行\r\n"
            + "版本: 1.0\r\n"
            + "  这一行不是描述的续行\r\n";

        var (description, _, _) = RuntimeMetadataService.ExtractSections(Text);

        Assert.Equal(2, description.Count);
        Assert.DoesNotContain(description, line => line.Contains("这一行", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Runtime Library", true)]
    [InlineData("RUNTIME LIBRARY", true)]
    [InlineData("Microsoft Visual C++ Redistributable", true)]
    [InlineData("redist package", true)]
    [InlineData("运行库组件", true)]
    [InlineData("游戏运行时", true)]
    [InlineData("Required to run Office", true)]
    [InlineData("a plain text editor", false)]
    public void MatchedSignal_IsCaseInsensitive(string text, bool expected)
    {
        Assert.Equal(expected, RuntimeMetadataService.MatchedSignal(text) is not null);
    }

    [Fact]
    public void MatchedSignal_ReturnsFirstHitInWordOrder()
    {
        // 信号词按表顺序命中：runtime 在 redistributable 之前
        Assert.Equal("runtime", RuntimeMetadataService.MatchedSignal("runtime and redistributable"));
    }

    [Fact]
    public void AnalyzeRuntimeSignal_IgnoresSignalWordOutsideSections()
    {
        // 信号词只从描述/标记/URL 段落里找：标题行里的 runtime 不算
        const string Text = "找到 Some Runtime Thing\r\n版本: 1.0\r\n描述: 一个文本编辑器。\r\n";

        var (isDependency, signal) = RuntimeMetadataService.AnalyzeRuntimeSignal(Text);

        Assert.False(isDependency);
        Assert.Null(signal);
    }
}
