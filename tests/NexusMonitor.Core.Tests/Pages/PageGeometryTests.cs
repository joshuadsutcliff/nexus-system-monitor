using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public class PageGeometryTests
{
    // 12 columns, width 1212, gap 12 → 11 gaps = 132; 1080/12 = 90 per cell. Clean numbers on purpose.
    private const double Width = 1212;
    private const int Cols = 12;
    private const double Gap = 12;
    private const double CellH = 72;

    [Fact]
    public void CellWidth_DividesRemainingSpaceEvenly()
    {
        PageGeometry.CellWidth(Width, Cols, Gap).Should().Be(90);
    }

    [Fact]
    public void ToPixelRect_OriginCell_SitsAtZero()
    {
        var px = PageGeometry.ToPixelRect(new GridRect(0, 0, 1, 1), Width, Cols, CellH, Gap);
        px.Should().Be(new PixelRect(0, 0, 90, 72));
    }

    [Fact]
    public void ToPixelRect_OffsetAndSpan_IncludeInteriorGaps()
    {
        // Col 4, span 8: X = 4*(90+12) = 408; W = 8*90 + 7*12 = 804.
        // Row 2, span 2: Y = 2*(72+12) = 168; H = 2*72 + 1*12 = 156.
        var px = PageGeometry.ToPixelRect(new GridRect(4, 2, 8, 2), Width, Cols, CellH, Gap);
        px.Should().Be(new PixelRect(408, 168, 804, 156));
    }

    [Fact]
    public void ToPixelRect_FullWidthRow_SpansExactlyAvailableWidth()
    {
        var px = PageGeometry.ToPixelRect(new GridRect(0, 0, 12, 1), Width, Cols, CellH, Gap);
        px.Width.Should().Be(Width);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 72)]
    [InlineData(4, 4 * 72 + 3 * 12)]
    public void TotalHeight_SumsRowsAndInteriorGaps(int rows, double expected)
    {
        PageGeometry.TotalHeight(rows, CellH, Gap).Should().Be(expected);
    }
}
