using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public class GridRectTests
{
    [Fact]
    public void RightAndBottom_AreExclusiveEdges()
    {
        var r = new GridRect(Col: 2, Row: 1, ColSpan: 3, RowSpan: 2);
        r.Right.Should().Be(5);
        r.Bottom.Should().Be(3);
    }

    [Theory]
    [InlineData(0, 0, 2, 2, 1, 1, 2, 2, true)]   // overlapping corner
    [InlineData(0, 0, 2, 2, 2, 0, 2, 2, false)]  // touching edges don't intersect
    [InlineData(0, 0, 2, 2, 0, 2, 2, 2, false)]  // stacked, touching rows
    [InlineData(0, 0, 4, 1, 1, 0, 1, 1, true)]   // containment
    public void Intersects_UsesExclusiveEdges(
        int c1, int r1, int cs1, int rs1, int c2, int r2, int cs2, int rs2, bool expected)
    {
        var a = new GridRect(c1, r1, cs1, rs1);
        var b = new GridRect(c2, r2, cs2, rs2);
        a.Intersects(b).Should().Be(expected);
        b.Intersects(a).Should().Be(expected);
    }

    [Theory]
    [InlineData(10, 0, 3, 1, 12, false)] // spills past column 12
    [InlineData(9, 0, 3, 1, 12, true)]   // exactly reaches edge
    [InlineData(-1, 0, 2, 1, 12, false)] // negative col
    [InlineData(0, -1, 2, 1, 12, false)] // negative row
    public void FitsWithinColumns_ValidatesBounds(int col, int row, int cs, int rs, int cols, bool expected)
    {
        new GridRect(col, row, cs, rs).FitsWithinColumns(cols).Should().Be(expected);
    }

    [Fact]
    public void ClampTo_MovesAndShrinksIntoGrid()
    {
        new GridRect(10, 0, 3, 1).ClampTo(12).Should().Be(new GridRect(9, 0, 3, 1));   // shift left
        new GridRect(0, 0, 15, 1).ClampTo(12).Should().Be(new GridRect(0, 0, 12, 1));  // shrink span
        new GridRect(-2, -1, 2, 1).ClampTo(12).Should().Be(new GridRect(0, 0, 2, 1));  // negative → origin
    }
}
