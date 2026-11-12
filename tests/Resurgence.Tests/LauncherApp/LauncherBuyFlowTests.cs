using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Tests.WebTests;
using Resurgence.Ui;
using Resurgence.Ui.Modals;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.LauncherApp;

/// <summary>The launcher's store page as a player uses it (launcher and live mode, the server's services): the buy button opens the dialog, the em$ or the gm$ button sends the purchase.</summary>
public sealed class LauncherBuyFlowTests : UiContext
{
    sealed class Server(int ems = 100, int gms = 472) : HttpMessageHandler
    {
        public readonly List<string> Seen = [], Forms = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Seen) Seen.Add($"{request.Method} {path}");
            if (request.Content is not null) lock (Forms) Forms.Add(request.Content.ReadAsStringAsync().Result);
            var body = path switch
            {
                "/catalog/store" => """{"categories": [{"key": "0", "name": "Gear", "children": [{"key": "0", "name": "Hats"}]}], "items": [{"id": 7, "name": "Hat", "variant": "", "description": "", "category": "0", "ems": 30, "was": null, "gms": 0, "buyable": true}, {"id": 8, "name": "Cap", "variant": "", "description": "", "category": "0", "ems": 0, "was": null, "gms": 50, "buyable": true}, {"id": 9, "name": "Boot", "variant": "", "description": "", "category": "0", "ems": 30, "was": null, "gms": 40, "buyable": true}], "bundles": []}""",
                "/auth/me" => $$"""{"userId": 1, "handle": "Fox", "ems": {{ems}}, "gms": {{gms}}}""",
                "/account/buy" => """{"status": "bought", "ems": 70}""",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    Server Launcher(int ems, int gms = 472)
    {
        var server = new Server(ems, gms);
        var http = new HttpClient(server) { BaseAddress = new Uri("http://server.test") };
        var cookie = new ServerCookie { Value = "secret" };
        Services.AddSingleton<ISessionService>(new ServerSessionService(http, cookie, NullLogger<ServerSessionService>.Instance));
        Services.AddSingleton<IWalletService>(new ServerWalletService(http, cookie, new Resurgence.Services.Mocks.MockWalletService(new Resurgence.Services.Mocks.MockWalletState()), NullLogger<ServerWalletService>.Instance));
        Services.AddSingleton<IStoreService>(new ServerStoreService(http, cookie, new ServerCache<ServerStore>(), new NoItems(), NullLogger<ServerStoreService>.Instance));
        var mode = Services.GetRequiredService<HostMode>();
        (mode.Launcher, mode.Live) = (true, true);
        return server;
    }

    string Text(string key, params object[] args) => Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Strings>>()[key, args].Value;

    [Fact]
    public void TheBuyButtonOpensTheDialogAndTheEmsButtonSendsThePurchase()
    {
        var server = Launcher(ems: 100);

        var host = Render<ModalHost>();
        var store = Render<Store>(p => p.Add(s => s.Key, "0"));
        store.WaitForElement(".card button.btn-primary").Click();
        host.WaitForElement(".buy .buttons button:not([disabled])").Click();

        host.WaitForAssertion(() => Assert.Contains("POST /account/buy", server.Seen));
    }

    // too few em$: the button stays off and the dialog says what the player has and what it costs
    [Fact]
    public void TooFewEmsKeepTheButtonOffAndSayWhy()
    {
        var server = Launcher(ems: 4);

        var host = Render<ModalHost>();
        var store = Render<Store>(p => p.Add(s => s.Key, "0"));
        store.WaitForElement(".card button.btn-primary").Click();

        host.WaitForAssertion(() => Assert.Equal(Text("Store.NotEnoughEms", 4, 30), host.Find(".buy p.line:last-of-type").TextContent));
        Assert.True(host.Find(".buy .buttons button").HasAttribute("disabled"));
        Assert.DoesNotContain("POST /account/buy", server.Seen);
    }

    // an item with a gm$ price alone: its buy button opens a dialog with the gm$ button only, which sends the purchase with the SILVER wallet
    [Fact]
    public void AnItemForGmsAloneIsBoughtWithGms()
    {
        var server = Launcher(ems: 0);

        var host = Render<ModalHost>();
        var store = Render<Store>(p => p.Add(s => s.Key, "0"));
        store.WaitForAssertion(() => Assert.Contains("50 gm$", store.FindAll(".card").First(c => c.TextContent.Contains("Cap")).QuerySelector("p.price")!.TextContent));
        store.FindAll(".card").First(c => c.TextContent.Contains("Cap")).QuerySelector("button.btn-primary")!.Click();
        var button = host.WaitForElement(".buy .buttons button");
        Assert.Single(host.FindAll(".buy .buttons button"));
        Assert.Equal("1 x Cap - 50 gm$", host.Find(".buy p.line").TextContent);  // the dialog names the price it charges
        Assert.Equal(Text("Store.BuyGms") + " (50 gm$)", button.TextContent);
        button.Click();

        host.WaitForAssertion(() => Assert.Contains("POST /account/buy", server.Seen));
        Assert.Equal("id=8&count=1&bundle=0&wallet=SILVER", Assert.Single(server.Forms));
    }

    // too few gm$: the button stays off and the dialog says what the player has and what it costs; the em$ button of the same item is still on
    [Fact]
    public void TooFewGmsKeepTheirButtonOffAndSayWhy()
    {
        var server = Launcher(ems: 100, gms: 10);

        var host = Render<ModalHost>();
        var store = Render<Store>(p => p.Add(s => s.Key, "0"));
        store.FindAll(".card").First(c => c.TextContent.Contains("Boot")).QuerySelector("button.btn-primary")!.Click();

        host.WaitForAssertion(() => Assert.Equal(Text("Store.NotEnoughGmsHave", 10, 40), host.Find(".buy p.line:last-of-type").TextContent));
        var buttons = host.FindAll(".buy .buttons button");
        Assert.Equal([false, true], buttons.Select(b => b.HasAttribute("disabled")));
        Assert.DoesNotContain("POST /account/buy", server.Seen);
        buttons[0].Click();
        host.WaitForAssertion(() => Assert.Equal("id=9&count=1&bundle=0&wallet=GOLD", Assert.Single(server.Forms)));
    }
}
