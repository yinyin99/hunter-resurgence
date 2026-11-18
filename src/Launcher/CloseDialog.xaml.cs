using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace Resurgence.Launcher;

/// <summary>"Are you sure you want to close the launcher and the game?": Yes stays disabled for <see cref="Delay"/> with its countdown in the label, No is the default, Escape and the × answer No. The clock is the seam: a test hands in its own.</summary>
public partial class CloseDialog : Window
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    readonly Func<TimeSpan> elapsed;
    readonly DispatcherTimer timer;

    /// <param name="elapsed">Time since the dialog opened; null: a stopwatch that starts now.</param>
    public CloseDialog(Func<TimeSpan>? elapsed = null)
    {
        var watch = Stopwatch.StartNew();
        this.elapsed = elapsed ?? (() => watch.Elapsed);
        InitializeComponent();
        Title = Kit.S("App.Name");
        Question.Text = Kit.S("Launcher.CloseQuestion");
        No.Content = Kit.S("Alert.No");
        timer = new(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        Closed += (_, _) => timer.Stop();
        Tick();
    }

    /// <summary>Seconds still to wait, rounded up: 3, 2, 1, then 0.</summary>
    public static int Remaining(TimeSpan elapsed) => Math.Max(0, (int)Math.Ceiling((Delay - elapsed).TotalSeconds));

    /// <summary>Updates Yes ("Yes (3)" … "Yes") and enables it once the delay is over.</summary>
    public void Tick()
    {
        var left = Remaining(elapsed());
        Yes.Content = left > 0 ? $"{Kit.S("Alert.Yes")} ({left})" : Kit.S("Alert.Yes");
        Yes.IsEnabled = left == 0;
        if (left == 0) timer.Stop();
    }

    /// <summary>Whether Yes can be pressed (mouse, Enter and Space all need it enabled).</summary>
    public bool YesEnabled => Yes.IsEnabled;

    /// <summary>The Yes button's label.</summary>
    public string YesLabel => (string)Yes.Content;

    void OnYes(object sender, RoutedEventArgs e) => DialogResult = true;

    void OnNo(object sender, RoutedEventArgs e) => DialogResult = false;
}
