using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Backup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (SystemParameters.HighContrast)
            ApplicationThemeManager.Apply(ApplicationTheme.HighContrast, Wpf.Ui.Controls.WindowBackdropType.None, updateAccent: false); // WPF UI picks the system's HC1, HC2, Black or White palette; no tokens, no accent. ponytail: read once at startup, a switch while the app runs needs a restart
        else
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Tokens.xaml", UriKind.Relative) });
            // WPF UI derives its accent shades from one colour and lightens them in dark mode; the theme orange is used as is (see Tokens.xaml)
            var accent = (Color)ColorConverter.ConvertFromString("#F9560D");
            ApplicationAccentColorManager.Apply(accent, accent, accent, accent);
            Resources["SystemFillColorAttentionBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CC2FF")); // Apply sets it to the accent; the informational InfoBar keeps WPF UI's blue
        }
        base.OnStartup(e);
    }
}
