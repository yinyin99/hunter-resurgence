using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Backup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // WPF UI derives its accent shades from one colour and lightens them in dark mode; the theme orange is used as is (see App.xaml)
        var accent = (Color)ColorConverter.ConvertFromString("#F9560D");
        ApplicationAccentColorManager.Apply(accent, accent, accent, accent);
        Resources["SystemFillColorAttentionBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CC2FF")); // Apply sets it to the accent; the informational InfoBar keeps WPF UI's blue
        base.OnStartup(e);
    }
}
