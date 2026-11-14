using System;
using System.Linq;
using WingetManager.Services;
using Xunit;

namespace WingetManager.Tests;

/// <summary>
/// <see cref="SelectionBook"/> 的约束测试。
///
/// 重点覆盖 v3.0.0 的那个 bug：<b>批量勾选后真相集合必须已经更新</b>。
/// 当时 <c>SetRowsChecked</c> 抑制了行事件再落盘，集合没变，界面与数据脱节，
/// 一切换分组重建列表勾选就消失。
/// </summary>
public sealed class SelectionBookTests
{
    [Fact]
    public void NewBook_IsEmpty()
    {
        var book = new SelectionBook();

        Assert.Equal(0, book.Count);
        Assert.True(book.IsEmpty);
        Assert.Empty(book.ToArray());
    }

    [Fact]
    public void Constructor_SkipsBlankEntries()
    {
        var book = new SelectionBook(new[] { "A.B", "", "   ", null, "C.D" });

        Assert.Equal(2, book.Count);
        Assert.True(book.Contains("A.B"));
        Assert.True(book.Contains("C.D"));
    }

    [Fact]
    public void Contains_IsCaseInsensitive()
    {
        var book = new SelectionBook(new[] { "Microsoft.WindowsTerminal" });

        Assert.True(book.Contains("microsoft.windowsterminal"));
        Assert.True(book.Contains("MICROSOFT.WINDOWSTERMINAL"));
        Assert.False(book.Contains("Microsoft.PowerToys"));
    }

    [Fact]
    public void Contains_NullOrWhitespace_IsFalse()
    {
        var book = new SelectionBook(new[] { "A.B" });

        Assert.False(book.Contains(null));
        Assert.False(book.Contains(string.Empty));
        Assert.False(book.Contains("   "));
    }

    [Fact]
    public void Add_And_Remove_ReportChanges()
    {
        var book = new SelectionBook();

        Assert.True(book.Add("A.B"));
        Assert.False(book.Add("A.B"));       // 已存在
        Assert.Equal(1, book.Count);

        Assert.True(book.Remove("a.b"));     // 大小写不敏感
        Assert.False(book.Remove("a.b"));    // 已不存在
        Assert.True(book.IsEmpty);
    }

    /// <summary>
    /// 核心回归：批量勾选必须真的写进集合（当年的 bug 就是这里没写）。
    /// </summary>
    [Fact]
    public void SetMany_True_ActuallyAddsEveryId()
    {
        var book = new SelectionBook();

        var changed = book.SetMany(new[] { "A.One", "B.Two", "C.Three" }, selected: true);

        Assert.Equal(3, changed);
        Assert.Equal(3, book.Count);
        Assert.True(book.Contains("A.One"));
        Assert.True(book.Contains("B.Two"));
        Assert.True(book.Contains("C.Three"));
    }

    [Fact]
    public void SetMany_False_ActuallyRemovesEveryId()
    {
        var book = new SelectionBook(new[] { "A.One", "B.Two", "KEEP.Me" });

        var changed = book.SetMany(new[] { "A.One", "b.two" }, selected: false);

        Assert.Equal(2, changed);
        Assert.Equal(1, book.Count);
        Assert.True(book.Contains("KEEP.Me"));
    }

    [Fact]
    public void SetMany_IsIdempotent_AndSkipsBlanks()
    {
        var book = new SelectionBook(new[] { "A.One" });

        var first = book.SetMany(new[] { "A.One", "", null, "B.Two" }, selected: true);
        var second = book.SetMany(new[] { "A.One", "B.Two" }, selected: true);

        Assert.Equal(1, first);   // 只有 B.Two 是新的
        Assert.Equal(0, second);  // 再来一次没有变化
        Assert.Equal(2, book.Count);
    }

    [Fact]
    public void Toggle_InvertsEachIdExactlyOnce()
    {
        var book = new SelectionBook(new[] { "A.One" });

        var changed = book.Toggle(new[] { "A.One", "B.Two", "b.two" }); // B.Two 重复

        Assert.Equal(2, changed);
        Assert.False(book.Contains("A.One"));
        Assert.True(book.Contains("B.Two"));
    }

    [Fact]
    public void PruneTo_DropsIdsThatNoLongerExist()
    {
        var book = new SelectionBook(new[] { "Gone.App", "Still.Here" });

        var removed = book.PruneTo(new[] { "still.here", "Other.App" });

        Assert.Equal(1, removed);
        Assert.Equal(1, book.Count);
        Assert.True(book.Contains("Still.Here"));
    }

    [Fact]
    public void Clear_EmptiesEverything()
    {
        var book = new SelectionBook(new[] { "A.One", "B.Two" });

        book.Clear();

        Assert.True(book.IsEmpty);
        Assert.Empty(book.ToArray());
    }

    [Fact]
    public void ReplaceAll_ReplacesInsteadOfMerging()
    {
        var book = new SelectionBook(new[] { "Old.One", "Old.Two" });

        book.ReplaceAll(new[] { "New.One" });

        Assert.Equal(1, book.Count);
        Assert.False(book.Contains("Old.One"));
        Assert.True(book.Contains("New.One"));
    }

    [Fact]
    public void ReplaceAll_Null_Clears()
    {
        var book = new SelectionBook(new[] { "Old.One" });

        book.ReplaceAll(null);

        Assert.True(book.IsEmpty);
    }

    [Fact]
    public void ToArray_RoundTripsThroughConstructor()
    {
        var book = new SelectionBook(new[] { "A.One", "B.Two", "C.Three" });

        var restored = new SelectionBook(book.ToArray());

        Assert.Equal(book.Count, restored.Count);
        foreach (var id in book.ToArray())
        {
            Assert.True(restored.Contains(id));
        }
    }

    [Fact]
    public void SetMany_NullSequence_Throws()
    {
        var book = new SelectionBook();

        Assert.Throws<ArgumentNullException>(() => book.SetMany(null!, true));
        Assert.Throws<ArgumentNullException>(() => book.Toggle(null!));
        Assert.Throws<ArgumentNullException>(() => book.PruneTo(null!));
    }
}
