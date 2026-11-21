using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Components;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

/// <summary>A hunt in progress: its page says so, shows what it has made so far and is read again by itself; the launcher's start screen shows the current hunt.</summary>
public sealed class HuntInProgressTests : UiContext
{
    static readonly DateTime Start = new(2026, 10, 22, 11, 16, 0);

    static Expedition Hunt(DateTime? end, int kills = 1) => new(7, "Reserve A", "Lodge B", Start, end,
        [new("Rifle A", "Ammo Type 1", 2, 50, 1, 100, null)], [new("Mule Deer", 1, 0, 1)], [new Pickup("Common Mushroom", 12.5, Start.AddMinutes(10))], [], 0, 1260, [],
        [.. Enumerable.Range(0, kills).Select(i => new Kill(100 + i, "Mule Deer", "Rifle A", "Ammo Type 1", 120, 100, 85, Start.AddMinutes(20)))]);

    /// <summary>An IProfileService that answers only ExpeditionAsync, with what the test sets: the hunt of each read, the last one repeating.</summary>
    public class Hunts : DispatchProxy
    {
        public static Queue<Expedition?> Answers = [];
        protected override object? Invoke(MethodInfo? m, object?[]? args) =>
            m!.Name != nameof(IProfileService.ExpeditionAsync) ? throw new NotSupportedException(m.Name) : Task.FromResult(Answers.Count > 1 ? Answers.Dequeue() : Answers.Peek());
    }

    void Serve(params Expedition?[] answers)
    {
        Hunts.Answers = new(answers);
        Services.AddSingleton(DispatchProxy.Create<IProfileService, Hunts>());
    }

    [Fact]
    public void AHuntInProgressSaysSoAndShowsWhatItHasMadeAndAHuntThatEndedDoesNot()
    {
        var open = Render<HuntStats>(p => p.Add(x => x.Hunt, Hunt(null)).Add(x => x.Handle, "h"));
        Assert.EndsWith("In progress", open.Find(".when").TextContent);
        Assert.Contains("Rifle A", open.Markup);
        Assert.Contains("Mule Deer", open.Markup);
        Assert.Contains("Common Mushroom", open.Markup);
        Assert.Equal("-", open.Find(".misc tr").LastElementChild!.TextContent);  // the distance is known when the hunt ends
        Assert.DoesNotContain("Statistics will be updated", open.Markup);

        var closed = Render<HuntStats>(p => p.Add(x => x.Hunt, Hunt(Start.AddMinutes(30))).Add(x => x.Handle, "h"));
        Assert.DoesNotContain("In progress", closed.Find(".when").TextContent);
        Assert.NotEqual("-", closed.Find(".misc tr").LastElementChild!.TextContent);
    }

    [Fact]
    public void AHuntInProgressAsksForItselfAgainAndAHuntThatEndedNever()
    {
        var reloads = 0;
        Render<HuntStats>(p => p.Add(x => x.Hunt, Hunt(null)).Add(x => x.Handle, "h").Add(x => x.Every, TimeSpan.FromMilliseconds(40))
            .Add(x => x.Reload, EventCallback.Factory.Create(this, () => reloads++)));
        Render<HuntStats>(p => p.Add(x => x.Hunt, Hunt(Start.AddMinutes(30))).Add(x => x.Handle, "h").Add(x => x.Every, TimeSpan.FromMilliseconds(40))
            .Add(x => x.Reload, EventCallback.Factory.Create(this, () => throw new InvalidOperationException("an ended hunt is not read again"))));
        Assert.True(SpinWait.SpinUntil(() => reloads >= 2, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TheHuntPageReadsTheHuntAgainWhenItsStatisticsAskFor()
    {
        Serve(Hunt(null, kills: 1), Hunt(Start.AddMinutes(30), kills: 2));
        var cut = Render<ExpeditionTab>(p => p.Add(x => x.Handle, "h").Add(x => x.Id, 7L));
        Assert.EndsWith("In progress", cut.Find(".when").TextContent);
        Assert.Single(cut.FindAll(".kills tbody tr"));

        var stats = cut.FindComponent<HuntStats>();
        await cut.InvokeAsync(() => stats.Instance.Reload.InvokeAsync());

        Assert.Equal(2, cut.FindAll(".kills tbody tr").Count);
        Assert.DoesNotContain("In progress", cut.Find(".when").TextContent);
    }

    [Fact]
    public void TheCurrentHuntPanelShowsReserveTimeAndCountsAndLinksToThePage()
    {
        Serve(Hunt(null, kills: 2));
        var cut = Render<CurrentHunt>();

        cut.WaitForAssertion(() => Assert.Equal("Hunt in progress", cut.Find("#current-title").TextContent));
        Assert.Equal("Reserve A - Lodge B", cut.Find(".where").TextContent);
        var values = cut.FindAll("dd").Select(d => d.TextContent).ToArray();
        Assert.Contains("21m", values[0]);  // the time since the start
        Assert.Equal(["2", "1"], values[1..]);  // kills and collectables so far
        Assert.Equal("/profile/Hunter01/expedition/7", cut.Find("a").GetAttribute("href"));
    }

    [Fact]
    public void TheCurrentHuntPanelIsReadAgainAndGoesWhenTheHuntEnds()
    {
        Serve(Hunt(null, kills: 1), Hunt(null, kills: 3), Hunt(Start.AddMinutes(30)));
        var cut = Render<CurrentHunt>(p => p.Add(x => x.Every, TimeSpan.FromMilliseconds(60)));
        cut.WaitForAssertion(() => Assert.Equal("1", cut.FindAll("dd")[1].TextContent));
        cut.WaitForAssertion(() => Assert.Equal("3", cut.FindAll("dd")[1].TextContent));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#current-title")));  // the last answer has an end
    }

    [Fact]
    public void WithoutAHuntThereIsNoPanel()
    {
        Serve((Expedition?)null);
        Assert.Empty(Render<CurrentHunt>().FindAll("#current-title"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]  // the website's player is not in the game
    public void OnlyTheLaunchersStartScreenShowsTheCurrentHunt(bool launcher, bool shown)
    {
        Services.AddSingleton<IProfileService>(new MockProfileService("profile:ongoing"));
        Services.GetRequiredService<HostMode>().Launcher = launcher;

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".dash")));
        cut.WaitForAssertion(() => Assert.Equal(shown, cut.FindAll("#current-title").Count == 1));
    }
}
