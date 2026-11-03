using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Modals;
using Resurgence.Web;
using Resurgence.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
// LINK_BACKUP, GAME_NAME, GAME_FULL: set by the deploy from the repository variables of the same names (docs/deploy.md); each wins over its key in settings.json
var config = builder.Configuration;
builder.Services.AddUiLocalization(Branding.Load(Path.Combine(builder.Environment.ContentRootPath, "settings.json"))
    .With("link_backup", config["LINK_BACKUP"]).With("game_name", config["GAME_NAME"]).With("game_full", config["GAME_FULL"]));
builder.Services.AddMockServices();
builder.Services.AddScoped<HostMode>();
builder.Services.AddScoped<ServerCookie>();
if (builder.Configuration["SERVER_URL"] is { Length: > 0 } serverUrl)
{
    // SERVER_URL (docs/website.md#sign-in): the session is the community server's, reached by its internal address; the player's cookie is sent by hand
    builder.Services.AddHttpClient("server", c => { c.BaseAddress = new Uri(serverUrl); c.Timeout = TimeSpan.FromSeconds(5); })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false });
    builder.Services.AddScoped<ISessionService>(sp => new ServerSessionService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("server"), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ILogger<ServerSessionService>>()));
    // the player's backup and data: an upload takes longer than the sign-in's 5 s
    builder.Services.AddHttpClient("server-upload", c => { c.BaseAddress = new Uri(serverUrl); c.Timeout = TimeSpan.FromMinutes(2); })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false });
    builder.Services.AddScoped<IRestoreService>(sp => new ServerRestoreService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("server-upload"), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ILogger<ServerRestoreService>>()));
}
else
{
    // one mock session per browser circuit, so signing in or out in one tab does not change the others
    builder.Services.AddScoped<ISessionService>(sp => new MockSessionService(() => sp.GetRequiredService<HostMode>().Mock));
}
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
if (builder.Configuration["SERVER_URL"] is { Length: > 0 })
{
    // the hunterscore board and the profile header are the community server's; registered after the mocks they wrap
    builder.Services.AddScoped<ILeaderboardsService>(sp => new ServerLeaderboardsService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("server"), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ILogger<ServerLeaderboardsService>>()));
    builder.Services.AddScoped<IProfileService>(sp => new ServerProfileService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("server"), sp.GetRequiredService<ServerCookie>(), sp.GetRequiredService<ISessionService>(),
        new MockProfileService(sp.GetRequiredService<HostMode>().Mock, sp.GetRequiredService<IFriendsService>()), sp.GetRequiredService<ILogger<ServerProfileService>>()));
}
builder.Services.AddScoped<ModalService>();
builder.Services.AddScoped<ILanguageSwitcher, WebLanguageSwitcher>();
// IMAGES_DIR: the operator's pictures, never committed (docs/images.md); unset or missing: the labelled boxes. /images.json lists them for the launcher;
// /images-missing.json lists the slots drawn without one
var imageIndex = AssetImages.Index(builder.Configuration["IMAGES_DIR"]);
var images = new AssetImages(imageIndex);
builder.Services.AddSingleton(images);

// behind the reverse proxy (deploy/): the client address and https come from FORWARDED_TRUSTED only; unset, nothing is forwarded
builder.Services.Configure<ForwardedHeadersOptions>(o => ForwardedProxy.Configure(o, builder.Configuration["FORWARDED_TRUSTED"]));

var app = builder.Build();
// security headers on every response, first so static files, the 404 page and the Blazor endpoints all carry them (docs/website.md#security-headers)
app.Use((ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.StrictTransportSecurity = "max-age=31536000"; // browsers ignore it over http (dotnet run, the CI smoke test)
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    // not frameable (clickjacking); a wider policy would have to allow the inline import map and the offerwall iframe
    h.ContentSecurityPolicy = "frame-ancestors 'none'";
    return next();
});
// before the cookie policy, the language cookie and the antiforgery, which read the scheme
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
// the operator's pictures at /images/ (docs/images.md); any other file type falls through to the 404 page
if (builder.Configuration["IMAGES_DIR"] is { Length: > 0 } imagesDir && Directory.Exists(imagesDir))
{
    var pictureTypes = new FileExtensionContentTypeProvider();
    foreach (var ext in pictureTypes.Mappings.Keys.Except(AssetImages.Extensions, StringComparer.OrdinalIgnoreCase).ToList())
        pictureTypes.Mappings.Remove(ext);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(Path.GetFullPath(imagesDir)),
        RequestPath = "/images",
        ContentTypeProvider = pictureTypes,
        OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable", // the ?v= of AssetImages changes with the file
    });
}
app.UseAntiforgery();
app.MapStaticAssets();
// the launcher's list of the pictures (docs/images.md): {"<file>":"<version>"}; {} without IMAGES_DIR
app.MapGet("/images.json", () => imageIndex);
// the slots drawn without a picture since the start (docs/images.md): {"<key>":"<first page>"}
app.MapGet("/images-missing.json", () => images.Missing);
// no CSP of Blazor's own (frame-ancestors 'self'): the security headers middleware above sends the only one
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(o => o.ContentSecurityFrameAncestorsPolicy = null).AddAdditionalAssemblies(typeof(Resurgence.Ui.Strings).Assembly);

app.Run();
