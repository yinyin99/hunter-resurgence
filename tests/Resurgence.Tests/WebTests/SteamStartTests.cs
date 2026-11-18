using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services.Mocks;
using Resurgence.Ui.Modals;

namespace Resurgence.Tests.WebTests;

/// <summary>The start's Steam state: "Starting Steam…" with the button while the launcher waits, one line after a start without Steam (docs/website.md#starting-without-steam).</summary>
public sealed class SteamStartTests : UiContext
{
    MockLauncherBridge Use(string? state)
    {
        var bridge = new MockLauncherBridge(NullLogger<MockLauncherBridge>.Instance, state);
        Services.AddSingleton<Resurgence.Services.ILauncherBridge>(bridge);
        return bridge;
    }

    [Fact]
    public async Task WhileWaitingForSteamTheButtonEndsTheWaitAndTheStartGoesOnWithoutSteam()
    {
        var bridge = Use("steam:wait");
        var cut = Render<SteamStart>();
        Assert.Empty(cut.FindAll(".steam-start"));

        var start = Task.Run(() => bridge.StartGame("x"));
        cut.WaitForAssertion(() => Assert.Equal("Starting Steam…", cut.Find(".steam-start .text").TextContent));
        Assert.Equal("Start without Steam", cut.Find("#start-without-steam").TextContent);
        Assert.Empty(cut.FindAll("#no-steam-note")); // not yet: the start has not finished

        cut.Find("#start-without-steam").Click();
        Assert.True((await start).Ok);
        cut.WaitForAssertion(() => Assert.Equal("Started without Steam: Steam achievements are off for this session.", cut.Find("#no-steam-note").TextContent));
        Assert.Empty(cut.FindAll("#start-without-steam"));
        Assert.Equal("nosteam", bridge.LastStartMode);
    }

    [Fact]
    public void WithoutSteamInstalledTheStartDoesNotWaitAndSaysSo()
    {
        var bridge = Use("steam:none");
        var cut = Render<SteamStart>();
        Assert.True(bridge.StartGame("x").Ok);
        Assert.False(bridge.WaitingForSteam);
        cut.WaitForAssertion(() => Assert.Equal("Started without Steam: Steam achievements are off for this session.", cut.Find("#no-steam-note").TextContent));
    }

    [Fact]
    public void AStartWithSteamSaysNothing()
    {
        var bridge = Use(null);
        var cut = Render<SteamStart>();
        Assert.Empty(cut.FindAll(".steam-start"));
        Assert.True(bridge.StartGame("x").Ok);
        Assert.Equal("steam", bridge.LastStartMode);
        Assert.Empty(cut.FindAll(".steam-start"));
    }

    [Fact]
    public void AnOpenDialogShowsTheLineInsideItAndNotOnThePage()
    {
        var bridge = Use("steam:none");
        bridge.StartGame("x");
        var page = Render<SteamStart>();
        var inside = Render<SteamStart>(p => p.Add(a => a.Inside, true));
        Assert.NotEmpty(page.FindAll("#no-steam-note"));
        Assert.Empty(inside.FindAll("#no-steam-note"));

        Services.GetRequiredService<ModalService>().Show<AlertDialog>();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#no-steam-note")));
        inside.WaitForAssertion(() => Assert.NotEmpty(inside.FindAll("#no-steam-note")));
    }

    [Fact]
    public async Task TheNoteGoesAtAClickAndComesBackAtTheNextStartAndAClickDuringTheWaitLeavesTheWaitAlone()
    {
        var bridge = Use("steam:wait");
        var cut = Render<SteamStart>();
        var start = Task.Run(() => bridge.StartGame("x"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#start-without-steam")));
        await cut.InvokeAsync(() => cut.Instance.Dismiss()); // a click elsewhere while it waits: nothing waits to go
        Assert.NotEmpty(cut.FindAll("#start-without-steam"));
        bridge.StartWithoutSteam();
        await start;
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#no-steam-note"))); // the note is still shown after the wait

        await cut.InvokeAsync(() => cut.Instance.Dismiss());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#no-steam-note")));

        Task.Run(() => bridge.StartGame("x")); // the next start: its wait, then its note
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#start-without-steam")));
        bridge.StartWithoutSteam();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#no-steam-note")));
    }
}
