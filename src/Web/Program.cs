using Resurgence.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// static SSR only; add AddInteractiveServerComponents() when a page needs interactivity
builder.Services.AddRazorComponents();
builder.Services.AddLocalization();

var app = builder.Build();
app.UseRequestLocalization("en"); // English only until other Strings.<culture>.resx exist
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddAdditionalAssemblies(typeof(Resurgence.Ui.Strings).Assembly);

app.Run();
