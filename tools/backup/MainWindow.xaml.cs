using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Backup;

public partial class MainWindow : FluentWindow
{
    const int LogLines = 2000;

    readonly Settings? cfg;
    readonly Watcher? watcher;
    string cfgError = "";
    CheckBox[]? boxes;
    readonly bool[] remembered;
    readonly string keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "game-backup", "claim.key");
    readonly string langPath; // the chosen language, next to the claim key
    readonly DispatcherTimer watchTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer runTimer = new() { Interval = TimeSpan.FromMilliseconds(250) }; // log and progress, only while a backup runs
    readonly Queue<string> pending = new(), lines = new();
    readonly Stopwatch clock = new();
    Engine? eng;
    CancellationTokenSource? cts;
    string lastStatus = "", resultPath = "";
    Func<(string Title, string Msg)>? resultText; // so the result bar can be worded again when the language changes
    bool logDirty;
    double grownBy;
    volatile int doneNow, totalNow;
    readonly Queue<(double Secs, int Done)> pace = new(); // progress samples of the last minute and a half: the remaining time follows the pace now, not the average since the start

    public MainWindow()
    {
        langPath = Path.Combine(Path.GetDirectoryName(keyPath)!, "language.txt");
        Loc.Init(langPath);
        InitializeComponent();
        OutDir.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "game-backup");
        remembered = [.. Engine.Groups.Select(g => g.On)];
        try { cfg = Settings.Load(); watcher = new(cfg.ClientExe); }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Text.Json.JsonException) { cfgError = e.Message; }

        foreach (var l in Loc.Languages) LangBox.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l.Code });
        LangBox.SelectedIndex = Array.FindIndex(Loc.Languages, l => l.Code == Loc.Code);
        LangBox.SelectionChanged += (_, _) =>
        {
            if (LangBox.SelectedItem is not ComboBoxItem { Tag: string code } || code == Loc.Code) return;
            Loc.Set(code);
            try { Directory.CreateDirectory(Path.GetDirectoryName(langPath)!); File.WriteAllText(langPath, code); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // not remembered, still switched
            ApplyLanguage();
            FitHeight();
        };
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
        ApplyLanguage();
        ApplyMode();
        Loaded += (_, _) => FitHeight();
        Closed += (_, _) => { watchTimer.Stop(); runTimer.Stop(); cts?.Cancel(); };
    }

    /// <summary>Longer languages need taller cards, and a result bar takes room too: the window is as tall as its content (never taller than the screen). The log, when open, keeps the room GrowFor gave it.</summary>
    void FitHeight()
    {
        var chrome = ActualHeight - Body.ActualHeight;
        Body.Measure(new Size(Body.ActualWidth, double.PositiveInfinity));
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Math.Max(MinHeight, chrome + Body.DesiredSize.Height + grownBy), area.Height);
        Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
    }

    // ---------- language ----------

    /// <summary>Sets every text from the resources of the current language. Ticks, typed text and the result are kept; only words change.</summary>
    void ApplyLanguage()
    {
        Title = TitleBarMain.Title = $"{Loc.S("App_Title")} {Engine.Version}";
        System.Windows.Automation.AutomationProperties.SetName(LangBox, Loc.S("Lang_Name"));
        OwnRadio.Content = Loc.S("Own_Radio");
        PublicRadio.Content = Loc.S("Public_Radio");
        Who.PlaceholderText = Loc.S("Who_Placeholder");
        Named(Who, "Who_Name");
        GroupsTitle.Text = Loc.S("WhatToSave");
        OutDir.PlaceholderText = Loc.S("OutDir_Placeholder");
        Named(OutDir, "OutDir_Placeholder");
        Browse.Content = Loc.S("Browse");
        Named(Browse, "Browse_Name");
        Zip.Content = Wrapped(Loc.S("Zip"));
        Skip.Content = Wrapped(Loc.S("Skip"));
        Upload.Content = Wrapped(Loc.S("Upload"));
        Older.Content = Wrapped(Loc.S("Older"));
        Named(Upload, "Upload_Name");
        if (cts == null) Start.Content = Loc.S("Start");
        Named(Bar, "Bar_Name");
        OpenBtn.Content = Loc.S("Open_Folder");
        Named(OpenBtn, "Open_Name");
        Details.Header = Loc.S("Details");
        Named(LogBox, "Log_Name");
        NotCoveredLink.Content = Loc.S("NotCovered_Link");
        NotCoveredText.Text = Engine.NotCovered;
        BuildRows();
        if (cfg == null) // the settings error is worded in the language too
            try { Settings.Load(); } catch (Exception e) when (e is InvalidOperationException or IOException or System.Text.Json.JsonException) { cfgError = e.Message; }
        if (resultText != null) (Result.Title, Result.Message) = resultText();
        lastStatus = "";
        Tick();
    }

    static void Named(DependencyObject o, string key) => System.Windows.Automation.AutomationProperties.SetName(o, Loc.S(key));

    static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    // ---------- groups ----------

    /// <summary>The group rows are built again for a new language and take over the ticks and enabled state of the old ones.</summary>
    void BuildRows()
    {
        var old = boxes;
        boxes = [.. Engine.Groups.Select(Row)];
        Groups.Children.Clear();
        for (int i = 0; i < boxes.Length; i++)
        {
            if (old != null) (boxes[i].IsChecked, boxes[i].IsEnabled) = (old[i].IsChecked, old[i].IsEnabled);
            if (!boxes[i].IsEnabled) boxes[i].ToolTip = Loc.S("Tip_Public");
            Groups.Children.Add(boxes[i]);
        }
        boxes[HistoryAt].Checked += (_, _) => SyncOlder(); // not Click: a keyboard, screen-reader or code change of the tick counts too
        boxes[HistoryAt].Unchecked += (_, _) => SyncOlder();
        SyncOlder();
    }

    static readonly int HistoryAt = Array.FindIndex(Engine.Groups, g => g.Key == "history");

    /// <summary>The older-editions switch belongs to the history group: it is only on offer while that group is ticked.</summary>
    void SyncOlder()
    {
        Older.IsEnabled = boxes![HistoryAt].IsChecked == true;
        if (!Older.IsEnabled) Older.IsChecked = false;
    }

    CheckBox Row((string Key, bool On, Engine.Session Needs) g)
    {
        string label = Loc.S("Group_" + g.Key), descText = Loc.S("GroupDesc_" + g.Key);
        var title = new WrapPanel();
        title.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        if (g.Needs != Engine.Session.None)
        {
            var tag = new Border
            {
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 1), CornerRadius = new CornerRadius(8),
                ToolTip = Loc.S(g.Needs == Engine.Session.All ? "Tip_All" : "Tip_Part"),
                Child = new TextBlock { Text = Loc.S(g.Needs == Engine.Session.All ? "Tag_All" : "Tag_Part"), FontSize = 11 },
            };
            tag.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush"); // by reference, so a theme switch restyles it
            title.Children.Add(tag);
        }
        var text = new StackPanel();
        text.Children.Add(title);
        var desc = new TextBlock { Text = descText, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        desc.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.Children.Add(desc);
        var box = new CheckBox { Content = text, IsChecked = g.On, MinWidth = 0, Margin = new Thickness(0, 0, 8, 2) };
        System.Windows.Automation.AutomationProperties.SetName(box, $"{label}: {descText}");
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
            boxes[i].ToolTip = own ? null : Loc.S("Tip_Public");
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
            if (PublicRadio.IsChecked == true) (text, icon) = (Loc.S("Status_Public"), SymbolRegular.Person24);
            else
                (text, icon) = watcher.Token == null
                    ? (Loc.S(watcher.GameSeen ? "Status_NoHunt" : "Status_Waiting"), SymbolRegular.Hourglass24)
                    : watcher.Stale ? (Loc.S("Status_Stale"), SymbolRegular.Warning24)
                    : (Loc.F("Status_Found", watcher.At.ToString("HH:mm")), SymbolRegular.CheckmarkCircle24);
        }
        if (text == lastStatus) return; // nothing changed: no layout work
        lastStatus = text;
        StatusText.Text = text;
        StatusIcon.Symbol = icon;
    }

    // ---------- run ----------

    async void OnStart(object sender, RoutedEventArgs e)
    {
        if (cts != null) { cts.Cancel(); Start.IsEnabled = false; Start.Content = Loc.S("Cancelling"); return; }
        if (cfg == null) { Show(InfoBarSeverity.Error, () => (Loc.S("Msg_SettingsMissing"), cfgError), null); return; }
        bool own = OwnRadio.IsChecked == true;
        var who = own ? "" : Who.Text.Trim();
        if (!own && who.Length == 0) { Show(InfoBarSeverity.Warning, () => (Loc.S("Msg_NoProfile"), Loc.S("Msg_NoProfileText")), null); Who.Focus(); return; }
        if (own && watcher?.Token == null) { Show(InfoBarSeverity.Warning, () => (Loc.S("Msg_NoSession"), Loc.S("Msg_NoSessionText")), null); return; }
        var keys = Engine.Groups.Where((_, i) => boxes![i].IsChecked == true).Select(g => g.Key).ToHashSet();
        if (keys.Count == 0) { Show(InfoBarSeverity.Warning, () => (Loc.S("Msg_Nothing"), Loc.S("Msg_NothingText")), null); return; }

        cts = new();
        Result.IsOpen = false;
        OpenBtn.Visibility = Visibility.Collapsed;
        Inputs.IsEnabled = false;
        Start.Content = Loc.S("Cancel");
        Bar.IsIndeterminate = true;
        ProgressText.Text = Loc.S("Progress_Starting");
        (doneNow, totalNow) = (0, 0);
        pace.Clear();
        clock.Restart();
        runTimer.Start();
        using var http = new HttpClient();
        var run = eng = new Engine(cfg, http, Log, (d, t) => (doneNow, totalNow) = (d, t), keyPath);
        run.OlderEditions = Older.IsChecked == true;
        string outDir = OutDir.Text;
        bool resume = Skip.IsChecked == true, zip = Zip.IsChecked == true;
        var ct = cts.Token;
        InfoBarSeverity sev; Func<(string Title, string Msg)> text; string? path = null;
        try
        {
            path = await Task.Run(() => run.Run(outDir, who, own ? () => watcher!.Token : null, keys, resume, zip, ct));
            var (saved, skipped, failed, stale) = (run.Saved, run.Skipped, run.Failed, run.TokenStale);
            var gone = run.Dead;
            var killsOff = !keys.Contains("kills") || !keys.Contains("photos");
            var opened = run.MessagesOpened;
            var (setupItems, setupMaps, setupCheck) = (run.SetupItems, run.SetupReserves, run.SetupChecks);
            sev = failed == 0 && !setupCheck ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            text = () => (Loc.S(failed == 0 ? "Msg_Saved" : "Msg_SavedGaps"),
                Loc.F("Msg_Counts", saved, skipped, failed) + (stale ? " " + Loc.S("Msg_Stale") : failed > 0 ? $" {run.FailureSummary} {Loc.S("Msg_Retry")}" : "")
                + (gone > 0 ? " " + Loc.F("Msg_Dead", gone) : "")
                + (killsOff ? " " + Loc.S("Msg_KillsOff") : "")
                + (opened > 0 ? " " + Loc.F("Msg_MessagesOpened", opened) : "")
                + (setupItems is { } n ? " " + (setupCheck ? Loc.F("Msg_SetupsCheck", n, setupMaps) : n == 0 ? Loc.S("Msg_SetupsNone") : Loc.F("Msg_Setups", n, setupMaps)) : ""));
        }
        catch (HostSilentException ex)
        {
            sev = InfoBarSeverity.Warning;
            var opened = run.MessagesOpened;
            text = () => (Loc.S("Msg_Stopped"), ex.Message + (opened > 0 ? " " + Loc.F("Msg_MessagesOpened", opened) : ""));
            path = run.Folder;
        }
        catch (OperationCanceledException)
        {
            var (saved, skipped) = (run.Saved, run.Skipped);
            sev = InfoBarSeverity.Warning;
            var opened = run.MessagesOpened;
            text = () => (Loc.S("Msg_Cancelled"), Loc.F("Msg_CancelledText", saved, skipped) + (opened > 0 ? " " + Loc.F("Msg_MessagesOpened", opened) : ""));
            path = run.Folder;
        }
        catch (Exception ex)
        {
            sev = InfoBarSeverity.Error;
            text = () => (Loc.S("Msg_Failed"), ex.Message);
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
        Start.Content = Loc.S("Start");
        Bar.IsIndeterminate = false;
        ProgressText.Text = "";
        Bar.Value = Bar.Maximum;
        Show(sev, text, path);
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
        pace.Enqueue((secs, d));
        while (pace.Count > 1 && secs - pace.Peek().Secs > 90) pace.Dequeue();
        var from = pace.Peek();
        if (r.TotalKnown && Engine.SecondsLeft(from.Secs, from.Done, secs, d, t) is { } left)
        {
            int min = (int)Math.Ceiling(left / 60);
            eta = " · " + (left < 45 ? Loc.S("Eta_Less") : min < 90 ? Loc.F("Eta_Min", min) : Loc.F("Eta_Hours", min / 60, min % 60));
        }
        ProgressText.Text = $"{d} / {t}{(r.TotalKnown ? "" : "+")} · {(r.Group.Length > 0 ? Loc.S("Group_" + r.Group) : Loc.S("Progress_Starting"))}{eta}";
    }

    void Show(InfoBarSeverity sev, Func<(string Title, string Msg)> text, string? path)
    {
        resultPath = path ?? "";
        resultText = text;
        Result.Severity = sev;
        (Result.Title, Result.Message) = text();
        OpenBtn.Visibility = resultPath.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Result.IsOpen = true;
        FitHeight();
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
