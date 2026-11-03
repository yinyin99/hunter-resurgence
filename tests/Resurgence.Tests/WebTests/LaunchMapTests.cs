using Bunit;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class LaunchMapTests : UiContext
{
    [Fact]
    public void EveryReserveButtonSaysTrueOrFalseNeverAnEmptyAttribute()
    {
        var cut = Render<LaunchMap>(p => p.Add(m => m.Kind, 1));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[data-reserve]")));
        var states = cut.FindAll("button[data-reserve]").Select(b => b.GetAttribute("aria-pressed")).ToList();
        Assert.All(states, s => Assert.True(s is "true" or "false", s));
        Assert.Single(states, "true");
    }
}
