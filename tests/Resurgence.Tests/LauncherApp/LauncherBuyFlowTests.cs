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

/// <summary>The launcher's store page as a player uses it (launcher and live mode, the server's services): the buy button opens the dialog, the em$ button sends the purchase.</summary>
public sealed class LauncherBuyFlowTests : UiContext
{
    sealed class Server(int ems = 100) : HttpMessageHandler
    {
        public readonly List<string> Seen = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Seen) Seen.Add($"{request.Method} {path}");
            var body = path switch
            {
                "/catalog/store" => """{"categories": [{"key": "0", "name": "Gear", "children": [{"key": "0", "name": "Hats"}]}], "items": [{"id": 7, "name": "Hat", "variant": "", "description": "", "category": "0", "ems": 30, "was": null, "gms": 0, "buyable": true}, {"id": 8, "name": "Cap", "variant": "", "description": "", "category": "0", "ems": 0, "was": null, "gms": 50, "buyable": false}], "bundles": []}""",
                "/auth/me" => $$"""{"userId": 1, "handle": "Fox", "ems": {{ems}}, "gms": 472}""",
                "/account/buy" => """{"status": "bought", "ems": 70}""",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    Server Launcher(int ems)
    {
        var server = new Server(ems);
        var http = new HttpClient(server) { BaseAddress = new Uri("http://server.test") };
        var cookie = new ServerCookie { Value = "secret" };
        Services.AddSingleton<ISessionService>(new ServerSessionService(http, cookie, NullLogger<ServerSessionService>.Instance));
        Services.AddSingleton<IWalletService>(new ServerWalletService(http, cookie, new Resurgence.Services.Mocks.MockWalletService(new Resurgence.Services.Mocks.MockWalletState()), NullLogger<ServerWalletService>.Instance));
        Services.AddSingleton<IStoreService>(new ServerStoreService(http, cookie, new ServerCache<ServerStore>(), NullLogger<ServerStoreService>.Instance));
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

    // the server sells for em$ only: an item with a gm$ price alone says so, on the list and on its page
    [Fact]
    public void AnItemForGmsAloneSaysTheyAreNotAvailable()
    {
        Launcher(ems: 100);

        var store = Render<Store>(p => p.Add(s => s.Key, "0"));
        store.WaitForAssertion(() => Assert.Equal(Text("Store.GmsUnavailable"), store.Find(".card p.own").TextContent));
        Assert.Single(store.FindAll(".card p.own"));

        var item = Render<ItemDetail>(p => p.Add(i => i.Id, 8));
        item.WaitForAssertion(() => Assert.Equal(Text("Store.GmsUnavailable"), item.Find("p.own").TextContent));
    }
}
