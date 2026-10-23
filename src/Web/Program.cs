using Resurgence.Ui;
using Resurgence.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// static SSR only; add AddInteractiveServerComponents() when a page needs interactivity
builder.Services.AddRazorComponents();
builder.Services.AddUiLocalization(Path.Combine(builder.Environment.ContentRootPath, "settings.json"));
builder.Services.AddMockServices();

var app = builder.Build();
app.UseRequestLocalization(o => o.SetDefaultCulture("en").AddSupportedCultures("en", "fr").AddSupportedUICultures("en", "fr"));
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddAdditionalAssemblies(typeof(Resurgence.Ui.Strings).Assembly);

app.Run();
