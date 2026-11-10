using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class MissionsPageTests : UiContext
{
    [Fact]
    public void ClickingAPackExpandsIt()
    {
        var cut = Render<Missions>();
        var pack = cut.Find("button.row.pack");
        Assert.Equal("false", pack.GetAttribute("aria-expanded"));

        pack.Click();

        Assert.Equal("true", cut.Find("button.row.pack").GetAttribute("aria-expanded"));
        Assert.NotEmpty(cut.FindAll("li[data-pack] .more ul.rows li"));
    }

    [Fact]
    public void ADeepLinkOpensItsPackAndScrollsToIt()
    {
        var shell = JSInterop.SetupModule("./_content/Resurgence.Ui/shell.js");
        shell.SetupVoid("scrollToPack", 3);

        var cut = Render<Missions>(p => p.Add(m => m.Group, 3));

        cut.WaitForAssertion(() => shell.VerifyInvoke("scrollToPack"));
        Assert.Equal("true", cut.Find("li[data-pack='3'] button.row.pack").GetAttribute("aria-expanded"));
        Assert.Equal("false", cut.Find("li[data-pack='1'] button.row.pack").GetAttribute("aria-expanded"));
    }

    sealed class Answer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    // made-up: the active mission names a reserve and a species and no weapon, the available one a weapon only
    const string Server = """
        {"missions": [
          {"id": 1, "title": "Test One", "desc": "", "state": "active", "objectives": [], "reward": "", "reserve": "Test Reserve", "species": "Test Deer", "weapon": ""},
          {"id": 2, "title": "Test Two", "desc": "", "state": "available", "objectives": [], "reward": "", "reserve": "", "species": "", "weapon": "Test Rifle"}],
         "groups": [{"id": 10, "title": "Test Pack", "desc": "", "favourite": false, "missions": [1, 2]}],
         "completed": 0, "auto_mission_activate": false, "activate_only_favourite": false, "hide_completed_favourite": false}
        """;

    [Fact]
    public void TheLiveSiteHasNoDailyTabNorHelpAndNoFilterOptionForNone()
    {
        Services.AddSingleton(new HostMode { Live = true });
        Services.AddSingleton<IMissionsService>(new ServerMissionsService(new HttpClient(new Answer(Server)) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie { Value = "abc" }, new MockMissionsService(), NullLogger<ServerMissionsService>.Instance));

        var cut = Render<Missions>();

        Assert.Empty(cut.FindAll("a[href='/missions/daily']"));
        Assert.Empty(cut.FindAll("a.help"));
        // the select-all entry and the one name each of the three filters has
        Assert.Equal([2, 2, 2], cut.FindAll("ul.pop").Select(u => u.QuerySelectorAll("li").Length));
    }
}
