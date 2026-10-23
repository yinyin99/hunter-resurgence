using Microsoft.AspNetCore.Localization;
using Resurgence.Ui;
using Resurgence.Ui.Modals;
using Resurgence.Web;
using Resurgence.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddUiLocalization(Path.Combine(builder.Environment.ContentRootPath, "settings.json"));
builder.Services.AddMockServices();
builder.Services.AddScoped<HostMode>();
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
// development only: ?mode=launcher|web picks the shell and is remembered in a cookie, so the launcher screens can be opened in a browser
if (app.Environment.IsDevelopment())
    app.Use((ctx, next) =>
    {
        var m = ctx.Request.Query["mode"].ToString();
        if (m is "launcher" or "web") ctx.Response.Cookies.Append("mode", m);
        else m = ctx.Request.Cookies["mode"];
        var host = ctx.RequestServices.GetRequiredService<HostMode>();
        (host.Launcher, host.Dev) = (m == "launcher", true);
        return next();
    });
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().AddAdditionalAssemblies(typeof(Resurgence.Ui.Strings).Assembly);

app.Run();
