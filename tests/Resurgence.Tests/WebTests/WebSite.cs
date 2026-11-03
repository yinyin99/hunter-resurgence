using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Resurgence.Tests.WebTests;

/// <summary>The real website in memory, in the Development environment, with no server and no operator pictures.</summary>
public sealed class WebSite : WebApplicationFactory<Resurgence.Web.WebLanguageSwitcher>
{
    protected override void ConfigureWebHost(IWebHostBuilder b) =>
        b.UseEnvironment("Development").UseSetting("SERVER_URL", "").UseSetting("IMAGES_DIR", "")
            // the key ring lives in memory: the default would write a key file under the PC's profile
            .ConfigureServices(s => s.AddDataProtection().Services.Configure<KeyManagementOptions>(o => o.XmlRepository = new MemoryKeys()));

    sealed class MemoryKeys : IXmlRepository
    {
        readonly List<XElement> keys = [];
        public IReadOnlyCollection<XElement> GetAllElements() { lock (keys) return [.. keys]; }
        public void StoreElement(XElement element, string friendlyName) { lock (keys) keys.Add(element); }
    }
}
