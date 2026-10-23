using Resurgence.Ui;
using Resurgence.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// static SSR only; add AddInteractiveServerComponents() when a page needs interactivity
builder.Services.AddRazorComponents();
builder.Services.AddUiLocalization(Path.Combine(builder.Environment.ContentRootPath, "settings.json"));
builder.Services.AddMockServices();
builder.Services.AddScoped<HostMode>();

var app = builder.Build();
app.UseRequestLocalization(o => o.SetDefaultCulture("en").AddSupportedCultures("en", "fr").AddSupportedUICultures("en", "fr"));
// development only: ?mode=launcher|web picks the shell and is remembered in a cookie, so the launcher screens can be opened in a browser
if (app.Environment.IsDevelopment())
    app.Use((ctx, next) =>
    {
        var m = ctx.Request.Query["mode"].ToString();
        if (m is "launcher" or "web") ctx.Response.Cookies.Append("mode", m);
        else m = ctx.Request.Cookies["mode"];
        ctx.RequestServices.GetRequiredService<HostMode>().Launcher = m == "launcher";
        return next();
    });
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddAdditionalAssemblies(typeof(Resurgence.Ui.Strings).Assembly);

app.Run();
