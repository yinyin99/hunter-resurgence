using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Ui;
using Resurgence.Ui.Shell;

namespace Resurgence.Tests.WebTests;

public sealed class StatusBarTests : UiContext
{
    [Fact]
    public void TheLauncherLabelIsNeverTranslated()
    {
        Services.GetRequiredService<HostMode>().Launcher = true;
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new("fr");
            var cut = Render<StatusBar>(p => p.Add(b => b.Minimal, true));
            Assert.Equal("Launcher", cut.Find(".launcher-label").TextContent);
        }
        finally { CultureInfo.CurrentUICulture = ui; }
    }
}
