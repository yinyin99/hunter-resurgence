using System.Globalization;

namespace Resurgence.Launcher;

public partial class App : System.Windows.Application
{
    public App()
    {
        // English-only UI until other Strings.<culture>.resx exist: keep dates and numbers English too
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture =
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en");
    }
}
