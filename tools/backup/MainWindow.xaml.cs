using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Backup;

public partial class MainWindow : FluentWindow
{
    const int LogLines = 2000;

    readonly Settings? cfg;
    readonly Watcher? watcher;
    readonly string cfgError = "";
    readonly CheckBox[] boxes;
    readonly bool[] remembered;
    readonly string keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "game-backup", "claim.key");
    readonly DispatcherTimer watchTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer runTimer = new() { Interval = TimeSpan.FromMilliseconds(250) }; // log and progress, only while a backup runs
    readonly Queue<string> pending = new(), lines = new();
    readonly Stopwatch clock = new();
    Engine? eng;
    CancellationTokenSource? cts;
    string lastStatus = "", resultPath = "";
    bool logDirty;
    double grownBy;
    volatile int doneNow, totalNow;

    public MainWindow()
    {
        SystemThemeWatcher.Watch(this); // light/dark follow the system, Mica backdrop
        InitializeComponent();
        OutDir.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "game-backup");
        NotCoveredText.Text = Engine.NotCovered;
        boxes = [.. Engine.Groups.Select(Row)];
        remembered = [.. Engine.Groups.Select(g => g.On)];
        foreach (var b in boxes) Groups.Children.Add(b);
        try { cfg = Settings.Load(); watcher = new(cfg.ClientExe); }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Text.Json.JsonException) { cfgError = e.Message; }

        OwnRadio.Checked += (_, _) => ApplyMode();
        PublicRadio.Checked += (_, _) => ApplyMode();
        Browse.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { InitialDirectory = OutDir.Text };
            if (d.ShowDialog(this) == true) OutDir.Text = d.FolderName;
        };
        Start.Click += OnStart;
        OpenBtn.Click += (_, _) => OpenResult();
        NotCoveredLink.Click += (_, _) => NotCoveredPopup.IsOpen = true;
        Details.Expanded += (_, _) => { GrowFor(true); RenderLog(); };
        Details.Collapsed += (_, _) => GrowFor(false);
        watchTimer.Tick += (_, _) => Tick();
        runTimer.Tick += (_, _) => { FlushLog(); ShowProgress(); };
        watchTimer.Start();
        ApplyMode();
        Tick();
        Closed += (_, _) => { watchTimer.Stop(); runTimer.Stop(); cts?.Cancel(); };
    }

    // ---------- groups ----------

    CheckBox Row((string Key, string Label, string Desc, bool On, Engine.Session Needs) g)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = g.Label, FontWeight = FontWeights.SemiBold });
        if (g.Needs != Engine.Session.None)
        {
            var tag = new Border
            {
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 1), CornerRadius = new CornerRadius(8),
                ToolTip = g.Needs == Engine.Session.All ? "All of this needs the running game." : "Most of this is public; the part that needs the running game is skipped without it.",
                Child = new TextBlock { Text = g.Needs == Engine.Session.All ? "needs game" : "part needs game", FontSize = 11 },
            };
            tag.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush"); // by reference, so a theme switch restyles it
            title.Children.Add(tag);
        }
        var text = new StackPanel();
        text.Children.Add(title);
        var desc = new TextBlock { Text = g.Desc, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        desc.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.Children.Add(desc);
        var box = new CheckBox { Content = text, IsChecked = g.On, MinWidth = 0, Margin = new Thickness(0, 0, 8, 2) };
        System.Windows.Automation.AutomationProperties.SetName(box, $"{g.Label}: {g.Desc}");
        ToolTipService.SetShowOnDisabled(box, true);
        return box;
    }

    /// <summary>Public mode: the groups that are all token-only are switched off and say why; the choice comes back with My account.</summary>
    void ApplyMode()
    {
        if (boxes == null) return;
        bool own = OwnRadio.IsChecked == true;
        Who.IsEnabled = !own;
        Tick();
        for (int i = 0; i < boxes.Length; i++)
        {
            if (Engine.Groups[i].Needs != Engine.Session.All) continue;
            if (!own && boxes[i].IsEnabled) remembered[i] = boxes[i].IsChecked == true;
            boxes[i].IsEnabled = own;
            boxes[i].IsChecked = own ? remembered[i] : false;
            boxes[i].ToolTip = own ? null : "Needs the running game: choose \"My account\".";
        }
    }

    // ---------- watcher ----------

    void Tick()
    {
        string text; SymbolRegular icon;
        if (watcher == null) (text, icon) = (cfgError, SymbolRegular.ErrorCircle24);
        else
        {
            if (watcher.Scan()) Log($"game session found {watcher.At:HH:mm:ss}"); // also in public mode: it is ready when My account is chosen
            if (PublicRadio.IsChecked == true) (text, icon) = ("Public profile: the game is not needed", SymbolRegular.Person24);
            else
                (text, icon) = watcher.Token == null
                    ? (watcher.GameSeen ? "The game is open but no hunt has started: start a hunt." : "Waiting for the game: open it, then start a hunt. This app can be opened before or after.", SymbolRegular.Hourglass24)
                    : watcher.Stale ? ("Session expired: start a hunt to refresh", SymbolRegular.Warning24)
                    : ($"Game session found at {watcher.At:HH:mm}", SymbolRegular.CheckmarkCircle24);
        }
        if (text == lastStatus) return; // nothing changed: no layout work
        lastStatus = text;
        StatusText.Text = text;
        StatusIcon.Symbol = icon;
    }

    // ---------- run ----------

    async void OnStart(object sender, RoutedEventArgs e)
    {
        if (cts != null) { cts.Cancel(); Start.IsEnabled = false; Start.Content = "Cancelling…"; return; }
        if (cfg == null) { Show(InfoBarSeverity.Error, "Settings missing", cfgError, null); return; }
        bool own = OwnRadio.IsChecked == true;
        var who = own ? "" : Who.Text.Trim();
        if (!own && who.Length == 0) { Show(InfoBarSeverity.Warning, "No profile chosen", "Type a handle or id.", null); Who.Focus(); return; }
        if (own && watcher?.Token == null) { Show(InfoBarSeverity.Warning, "No game session yet", "Open the game and start a hunt, or choose Public profile.", null); return; }
        var keys = Engine.Groups.Where((_, i) => boxes[i].IsChecked == true).Select(g => g.Key).ToHashSet();
        if (keys.Count == 0) { Show(InfoBarSeverity.Warning, "Nothing chosen", "Tick at least one group.", null); return; }

        cts = new();
        Result.IsOpen = false;
        OpenBtn.Visibility = Visibility.Collapsed;
        Inputs.IsEnabled = false;
        Start.Content = "Cancel";
        Bar.IsIndeterminate = true;
        ProgressText.Text = "Starting…";
        (doneNow, totalNow) = (0, 0);
        clock.Restart();
        runTimer.Start();
        using var http = new HttpClient();
        var run = eng = new Engine(cfg, http, Log, (d, t) => (doneNow, totalNow) = (d, t), keyPath);
        string outDir = OutDir.Text, token = own ? watcher!.Token! : "";
        bool resume = Skip.IsChecked == true, zip = Zip.IsChecked == true;
        var ct = cts.Token;
        InfoBarSeverity sev; string title, msg; string? path = null;
        try
        {
            path = await Task.Run(() => run.Run(outDir, who, own ? token : null, keys, resume, zip, ct));
            (sev, title) = run.Failed == 0 ? (InfoBarSeverity.Success, "Backup saved") : (InfoBarSeverity.Warning, "Backup saved with gaps");
            msg = $"{run.Saved} saved, {run.Skipped} skipped (already there), {run.Failed} failed."
                + (run.TokenStale ? " Session expired: start a hunt to refresh, then run again." : run.Failed > 0 ? $" {run.FailureSummary} Run again later with resume on." : "");
        }
        catch (OperationCanceledException)
        {
            (sev, title) = (InfoBarSeverity.Warning, "Cancelled");
            msg = $"{run.Saved} saved so far, {run.Skipped} skipped. Start again with resume on to continue.";
            path = run.Folder;
        }
        catch (Exception ex)
        {
            (sev, title, msg) = (InfoBarSeverity.Error, "Backup failed", ex.Message);
            if (run.TokenStale && watcher != null) watcher.Stale = true;
        }
        if (run.TokenStale && watcher != null) watcher.Stale = true;
        runTimer.Stop();
        FlushLog();
        eng = null;
        cts = null;
        clock.Stop();
        Inputs.IsEnabled = true;
        Start.IsEnabled = true;
        Start.Content = "Start";
        Bar.IsIndeterminate = false;
        ProgressText.Text = "";
        Bar.Value = Bar.Maximum;
        Show(sev, title, msg, path);
        lastStatus = "";
        Tick();
    }

    void ShowProgress()
    {
        if (eng is not { } r) return;
        int d = doneNow, t = totalNow;
        Bar.IsIndeterminate = !r.TotalKnown || t == 0;
        if (!Bar.IsIndeterminate) { Bar.Maximum = t; Bar.Value = Math.Min(d, t); }
        string eta = "";
        double secs = clock.Elapsed.TotalSeconds;
        if (r.TotalKnown && r.Sent >= 3 && secs >= 5 && t > d)
        {
            double left = (t - d) / (r.Sent / secs); // requests still to do at the rate so far, both hosts counted
            eta = left < 45 ? " · less than a minute left" : $" · about {Math.Ceiling(left / 60)} min left";
        }
        ProgressText.Text = $"{d} / {t}{(r.TotalKnown ? "" : "+")} · {(r.Group.Length > 0 ? r.Group : "starting")}{eta}";
    }

    void Show(InfoBarSeverity sev, string title, string msg, string? path)
    {
        resultPath = path ?? "";
        Result.Severity = sev;
        Result.Title = title;
        Result.Message = msg;
        OpenBtn.Visibility = resultPath.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Result.IsOpen = true;
    }

    void OpenResult()
    {
        if (resultPath.Length == 0) return;
        var arg = File.Exists(resultPath) ? $"/select,\"{resultPath}\"" : $"\"{resultPath}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = false });
    }

    // ---------- log ----------

    /// <summary>Any thread. Lines wait in a queue; the 250 ms run timer (or one posted flush when idle) moves them to the box.</summary>
    void Log(string line)
    {
        bool idle;
        line = $"{DateTime.Now:HH:mm:ss} {line}";
        lock (pending) { pending.Enqueue(line); idle = !runTimer.IsEnabled; }
        if (idle) Dispatcher.BeginInvoke(FlushLog);
    }

    void FlushLog()
    {
        List<string> fresh;
        lock (pending) { fresh = [.. pending]; pending.Clear(); }
        if (fresh.Count == 0) return;
        foreach (var l in fresh)
        {
            lines.Enqueue(l);
            if (lines.Count > LogLines) { lines.Dequeue(); logDirty = true; } // only the last 2000 lines are kept
        }
        if (!Details.IsExpanded) { logDirty = true; return; }
        if (logDirty) RenderLog();
        else
        {
            LogBox.AppendText(string.Join(Environment.NewLine, fresh) + Environment.NewLine);
            LogBox.ScrollToEnd();
        }
    }

    void RenderLog()
    {
        if (!logDirty && LogBox.Text.Length > 0) return;
        LogBox.Text = lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines) + Environment.NewLine;
        LogBox.ScrollToEnd();
        logDirty = false;
    }

    /// <summary>The log area is the window's last row: opening Details makes the window taller (within the screen) instead of squeezing the cards.</summary>
    void GrowFor(bool open)
    {
        if (open)
        {
            var room = SystemParameters.WorkArea.Bottom - Top - Height;
            var want = Math.Min(180, Math.Max(0, room));
            if (want < 180) { var up = Math.Min(180 - want, Math.Max(0, Top - SystemParameters.WorkArea.Top)); Top -= up; want += up; }
            Height += want;
            grownBy = want;
        }
        else
        {
            Height = Math.Max(MinHeight, Height - grownBy);
            grownBy = 0;
        }
    }
}
