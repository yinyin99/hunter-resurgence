using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.WebUtilities;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Modals;
using Resurgence.Web;
using Resurgence.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddUiLocalization(Path.Combine(builder.Environment.ContentRootPath, "settings.json"));
builder.Services.AddMockServices();
builder.Services.AddScoped<HostMode>();
// one mock session per browser circuit, so signing in or out in one tab does not change the others
builder.Services.AddScoped<ISessionService, MockSessionService>();
// the development states (?mock=patch:..., ?mock=settings:first) are per browser circuit too
builder.Services.AddScoped<IPatchService>(sp => new MockPatchService(sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<IGameSettingsService>(sp => new MockGameSettingsService(sp.GetRequiredService<ILogger<MockGameSettingsService>>(), sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<IAccountService>(sp => new MockAccountService(sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<ISignupService>(sp => new MockSignupService(sp.GetRequiredService<HostMode>().Mock));
builder.Services.AddScoped<ModalService>();
builder.Services.AddScoped<ILanguageSwitcher, WebLanguageSwitcher>();

var app = builder.Build();
// ?lang=fr picks the language and remembers it in the culture cookie
app.Use((ctx, next) =>
{
    var lang = ctx.Request.Query["lang"].ToString();
    if (lang is "en" or "fr") ctx.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName, CookieRequestCultureProvider.MakeCookieValue(new(lang)));
    return next();
});
app.UseRequestLocalization(o =>
{
    o.SetDefaultCulture("en").AddSupportedCultures("en", "fr").AddSupportedUICultures("en", "fr");
    o.RequestCultureProviders.Insert(0, new QueryStringRequestCultureProvider { QueryStringKey = "lang", UIQueryStringKey = "lang" });
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
        if (m is "launcher" or "web") ctx.Response.Cookies.Append("mode", m);
        else m = ctx.Request.Cookies["mode"];
        var host = ctx.RequestServices.GetRequiredService<HostMode>();
        (host.Launcher, host.Dev) = (m == "launcher", true);
        var mock = Q("mock");
        if (mock.Length > 0) ctx.Response.Cookies.Append("mock", mock); else mock = ctx.Request.Cookies["mock"] ?? "";
        host.Mock = mock == "none" ? null : mock;
        return next();
    });
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().AddAdditionalAssemblies(typeof(Resurgence.Ui.Strings).Assembly);

app.Run();
