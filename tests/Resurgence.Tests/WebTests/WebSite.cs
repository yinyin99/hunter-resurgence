using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace Resurgence.Tests.WebTests;

/// <summary>The real website in memory, in the Development environment, with no server and no operator pictures.</summary>
public sealed class WebSite : WebApplicationFactory<Resurgence.Web.WebLanguageSwitcher>
{
    protected override void ConfigureWebHost(IWebHostBuilder b) =>
        b.UseEnvironment("Development").UseSetting("SERVER_URL", "").UseSetting("IMAGES_DIR", "");
}
