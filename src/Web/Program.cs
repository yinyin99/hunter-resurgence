using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Modals;
using Resurgence.Web;
using Resurgence.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
// LINK_BACKUP: set by the deploy from the repository variable of the same name (docs/deploy.md); it wins over link_backup of settings.json
builder.Services.AddUiLocalization(Branding.Load(Path.Combine(builder.Environment.ContentRootPath, "settings.json")).WithLink("backup", builder.Configuration["LINK_BACKUP"]));
builder.Services.AddMockServices();
builder.Services.AddScoped<HostMode>();
// one mock session per browser circuit, so signing in or out in one tab does not change the others
builder.Services.AddScoped<ISessionService>(sp => new MockSessionService(() => sp.GetRequiredService<HostMode>().Mock));
// the development states (?mock=patch:..., ?mock=settings:first) are per browser circuit too
builder.Services.AddScoped<IPatchService>(sp => new MockPatchService(sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<IGameSettingsService>(sp => new MockGameSettingsService(sp.GetRequiredService<ILogger<MockGameSettingsService>>(), sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<INewsService>(sp => new MockNewsService(sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<IMissionsService>(sp => new MockMissionsService(sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<IFriendsService>(sp => new MockFriendsService(sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<IMessagesService>(sp => new MockMessagesService(sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<IProfileService>(sp => new MockProfileService(sp.GetRequiredService<HostMode>().Mock, sp.GetRequiredService<IFriendsService>()));
// one wallet per circuit: what the store and the bank buy shows in the wallet and the status bar
// (Replace, because the singleton mocks of AddMockServices would otherwise be validated against the scoped state)
builder.Services.Replace(ServiceDescriptor.Scoped<MockWalletState, MockWalletState>());
builder.Services.Replace(ServiceDescriptor.Scoped<IWalletService>(sp => new MockWalletService(sp.GetRequiredService<MockWalletState>(), sp.GetRequiredService<HostMode>().Mock)));
builder.Services.Replace(ServiceDescriptor.Scoped<IStoreService>(sp => new MockStoreService(sp.GetRequiredService<MockWalletState>(), sp.GetRequiredService<HostMode>().Mock)));
builder.Services.Replace(ServiceDescriptor.Scoped<IBankService>(sp => new MockBankService(sp.GetRequiredService<MockWalletState>(), sp.GetRequiredService<HostMode>().Mock)));
builder.Services.AddScoped<IServersService>(sp => new MockServersService(sp.GetRequiredService<ILogger<MockServersService>>(), sp.GetRequiredService<ILauncherBridge>(), sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<ICompetitionsService>(sp => new MockCompetitionsService(sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<IAccountService>(sp => new MockAccountService(sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<ISignupService>(sp => new MockSignupService(sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<ModalService>();
builder.Services.AddScoped<ILanguageSwitcher, WebLanguageSwitcher>();

// behind the reverse proxy (deploy/): the client address and https come from FORWARDED_TRUSTED only; unset, nothing is forwarded
builder.Services.Configure<ForwardedHeadersOptions>(o => ForwardedProxy.Configure(o, builder.Configuration["FORWARDED_TRUSTED"]));

var app = builder.Build();
// first: the cookie policy, the language cookie and the antiforgery read the scheme
app.UseForwardedHeaders();
// the antiforgery cookie too; the Appends below set Secure themselves for code scanning (browsers accept it on http://localhost)
app.UseCookiePolicy(new CookiePolicyOptions { Secure = CookieSecurePolicy.Always });
// ?lang=fr picks the language and remembers it in the culture cookie
app.Use((ctx, next) =>
{
    var lang = ctx.Request.Query["lang"].ToString();
    if (Languages.Find(lang) is { } l) ctx.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName, CookieRequestCultureProvider.MakeCookieValue(new(l.Code)), new CookieOptions { Secure = true });
    return next();
});
app.UseRequestLocalization(o =>
{
    var codes = Languages.All.Select(l => l.Code).ToArray();
    o.SetDefaultCulture("en").AddSupportedCultures(codes).AddSupportedUICultures(codes);
    o.RequestCultureProviders.Insert(0, new QueryStringRequestCultureProvider { QueryStringKey = "lang", UIQueryStringKey = "lang" });
    // the browser's languages by preference, any region ("de-AT" is German, "pt" Brazilian Portuguese), replacing the exact-match default
    o.RequestCultureProviders.Remove(o.RequestCultureProviders.OfType<AcceptLanguageHeaderRequestCultureProvider>().Single());
    o.RequestCultureProviders.Add(new CustomRequestCultureProvider(ctx => Task.FromResult(ctx.Request.GetTypedHeaders().AcceptLanguage
        .OrderByDescending(a => a.Quality ?? 1).Select(a => Languages.Match(a.Value.Value)).FirstOrDefault(l => l is not null) is { } l ? new ProviderCultureResult(l.Code) : null)));
});
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// development only, after the status code pages so the re-executed /not-found request (new scope) gets it too:
// ?mode=launcher|web picks the shell and ?mock=session:out the signed-out site; both are remembered in cookies
if (app.Environment.IsDevelopment())
    app.Use((ctx, next) =>
    {
        // a 404 is re-executed as /not-found without its query string: read the original one
        var original = ctx.Features.Get<IStatusCodeReExecuteFeature>()?.OriginalQueryString;
        var query = original is null ? null : QueryHelpers.ParseQuery(original);
        string Q(string key) => (query is null ? ctx.Request.Query[key] : query.GetValueOrDefault(key)).ToString();
        var m = Q("mode");
        if (m is "launcher" or "web") ctx.Response.Cookies.Append("mode", m, new CookieOptions { Secure = true });
        else m = ctx.Request.Cookies["mode"];
        var host = ctx.RequestServices.GetRequiredService<HostMode>();
        (host.Launcher, host.Dev) = (m == "launcher", true);
        var mock = Q("mock");
        if (mock.Length > 0) ctx.Response.Cookies.Append("mock", mock, new CookieOptions { Secure = true }); else mock = ctx.Request.Cookies["mock"] ?? "";
        host.Mock = mock == "none" ? null : mock;
        return next();
    });
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().AddAdditionalAssemblies(typeof(Resurgence.Ui.Strings).Assembly);

app.Run();
