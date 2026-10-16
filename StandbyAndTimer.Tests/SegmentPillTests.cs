using StandbyAndTimer.Views;

namespace StandbyAndTimer.Tests;

// The sliding tab highlight lands on the selected tab: tabs share the header width equally.
public class SegmentPillTests
{
    [Theory]
    [InlineData(0, 3, 712, 0)]
    [InlineData(1, 3, 712, 712.0 / 3)]
    [InlineData(2, 3, 712, 712.0 / 3 * 2)]
    [InlineData(1, 2, 400, 200)]
    [InlineData(-1, 3, 712, 0)]     // nothing selected
    [InlineData(0, 0, 712, 0)]      // no tabs
    public void The_highlight_lands_on_the_selected_tab(int index, int count, double width, double x) =>
        Assert.Equal(x, SegmentPill.TargetX(index, count, width), 6);
}
