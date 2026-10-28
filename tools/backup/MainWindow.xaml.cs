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
    string? announcedGroup; // the group the progress line last spoke for: the counts change every tick, only a new group is announced
    volatile int doneNow, totalNow;
    readonly Queue<(double Secs, int Done)> pace = new(); // progress samples of the last minute and a half: the remaining time follows the pace now, not the average since the start

    public MainWindow()
    {
        langPath = Path.Combine(Path.GetDirectoryName(keyPath)!, "language.txt");
        Loc.Init(langPath);
        InitializeComponent();
        var area = SystemParameters.WorkArea; // a small screen or a high scale factor: the window never asks for more than the work area
        (MinWidth, MinHeight, Width, Height) = (Math.Min(MinWidth, area.Width), Math.Min(MinHeight, area.Height), Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        OutDir.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "game-backup");
        remembered = [.. Engine.Groups.Select(g => g.On)];
        try { cfg = Settings.Load(); watcher = new(cfg.ClientExe); }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Text.Json.JsonException) { cfgError = e.Message; }

        foreach (var l in Loc.Languages) LangBox.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l.Code });
        LangBox.SelectedIndex = Array.FindIndex(Loc.Languages, l => l.Code == Loc.Code);
        var langTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) }; // arrow keys on the closed box browse the list: the language is applied once the choice has settled
        langTimer.Tick += (_, _) => { langTimer.Stop(); ApplyChosenLanguage(); };
        LangBox.SelectionChanged += (_, _) => { if (!LangBox.IsDropDownOpen) { langTimer.Stop(); langTimer.Start(); } }; // an open list applies when it closes
        LangBox.DropDownClosed += (_, _) => { langTimer.Stop(); ApplyChosenLanguage(); };
        foreach (var r in new[] { OwnRadio, PublicRadio }) r.GotKeyboardFocus += (s, _) => ((RadioButton)s).IsChecked = true; // arrow keys move and select; Tab reaches only the checked one
        OwnRadio.Checked += (_, _) => ApplyMode();
        PublicRadio.Checked += (_, _) => ApplyMode();
        Browse.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { InitialDirectory = OutDir.Text };
            if (d.ShowDialog(this) == true) OutDir.Text = d.FolderName;
        };
        Start.Click += OnStart;
        OpenBtn.Click += (_, _) => OpenResult();
        NotCoveredLink.Click += async (_, _) => await ShowNotCovered();
        SelectAll.Click += (_, _) => SelectGroups(true);
        SelectNone.Click += (_, _) => SelectGroups(false);
        Details.Expanded += (_, _) => { RenderLog(); Dispatcher.BeginInvoke(FitHeight, DispatcherPriority.Background); }; // the Expander shows or hides its content after the event
        Details.SizeChanged += (_, e) => { if (IsLoaded && !Details.IsExpanded && e.HeightChanged) Dispatcher.BeginInvoke(FitHeight, DispatcherPriority.Background); }; // it hides the log after the Collapsed event, when its height drops
        LayoutUpdated += (_, _) => FitLog();
        StateChanged += (_, _) => NameTitleButtons(this);
        watchTimer.Tick += (_, _) => Tick();
        runTimer.Tick += (_, _) => { FlushLog(); ShowProgress(); };
        watchTimer.Start();
        ApplyLanguage();
        ApplyMode();
        ContentRendered += (_, _) => SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush"); // FluentWindow resets it to its own dark grey when it applies the backdrop
        Loaded += (_, _) => { NameTitleButtons(this); FitHeight(); };
        Closed += (_, _) => { watchTimer.Stop(); runTimer.Stop(); langTimer.Stop(); cts?.Cancel(); };
    }

    /// <summary>Longer languages need taller cards, and a result bar and the open log take room too: the window is as tall as its content (never taller than the screen). The log asks for its full height here; FitLog takes back what a small screen cannot give.</summary>
    void FitHeight()
    {
        UpdateLayout(); // settle pending changes first: the manual Measure below left them half-measured (Start kept the width of "Cancelling…" and its label sat off-centre)
        var chrome = ActualHeight - Body.ActualHeight;
        LogBox.Height = LogHeight;
        Body.Measure(new Size(Body.ActualWidth, double.PositiveInfinity));
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Math.Max(MinHeight, chrome + Body.DesiredSize.Height), area.Height);
        Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
    }

    const double LogHeight = 155, LogMin = 66; // the log is 155 tall when there is room, three lines at least

    /// <summary>The log is the one thing that gives way when the window is short: it takes what is left below the cards, never less than three lines, and never more than LogHeight. The group list only scrolls.</summary>
    void FitLog()
    {
        if (!Details.IsExpanded || Details.ActualHeight == 0) return;
        var rows = Body.RowDefinitions;
        // the window's row, not Body: an arranged Body never gets smaller than its content
        var room = ((Grid)Body.Parent).RowDefinitions[1].ActualHeight - Body.Margin.Top - Body.Margin.Bottom - rows[0].MinHeight - rows[1].ActualHeight - rows[2].ActualHeight - rows[3].ActualHeight - (rows[4].ActualHeight - LogBox.ActualHeight);
        var want = Math.Clamp(Math.Floor(room), LogMin, LogHeight);
        if (Math.Abs(LogBox.Height - want) >= 1) LogBox.Height = want;
    }

    /// <summary>Only changes enabled rows: in Public mode the token-only groups stay off. The older-editions switch follows through SyncOlder.</summary>
    void SelectGroups(bool on)
    {
        foreach (var b in boxes!) if (b.IsEnabled) b.IsChecked = on;
    }

    /// <summary>The title bar's own buttons have no names for a screen reader; they come from the resources, so they follow the language (and Maximize turns into Restore). Also used for the dialog's window.</summary>
    static void NameTitleButtons(Window w)
    {
        foreach (var b in Descendants<TitleBarButton>(w))
            Named(b, b.ButtonType switch
            {
                TitleBarButtonType.Minimize => "Win_Minimize",
                TitleBarButtonType.Close => "Win_Close",
                _ => w.WindowState == WindowState.Maximized ? "Win_Restore" : "Win_Maximize",
            });
    }

    /// <summary>Applies the language picked in the box: remembered in language.txt, words and layout rebuilt once.</summary>
    void ApplyChosenLanguage()
    {
        if (LangBox.SelectedItem is not ComboBoxItem { Tag: string code } || code == Loc.Code) return;
        Loc.Set(code);
        try { Directory.CreateDirectory(Path.GetDirectoryName(langPath)!); File.WriteAllText(langPath, code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // not remembered, still switched
        ApplyLanguage();
        FitHeight();
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
        if (cts == null) Start.Content = Loc.S("Start");
        Named(Bar, "Bar_Name");
        OpenBtn.Content = Loc.S("Open_Folder");
        Named(OpenBtn, "Open_Name");
        Details.Header = Loc.S("Details");
        Named(LogBox, "Log_Name");
        NotCoveredLink.Content = Loc.S("NotCovered_Link");
        SelectAll.Content = Loc.S("Groups_All");
        SelectNone.Content = Loc.S("Groups_None");
        NameTitleButtons(this);
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
        Groups.RowDefinitions.Clear();
        HistoryCell.Children.Clear();
        for (int i = 0; i < boxes.Length; i++)
        {
            if (old != null) (boxes[i].IsChecked, boxes[i].IsEnabled) = (old[i].IsChecked, old[i].IsEnabled);
            Describe(i);
            UIElement cell = boxes[i];
            if (i == HistoryAt) { HistoryCell.Children.Add(boxes[i]); HistoryCell.Children.Add(Older); cell = HistoryCell; } // the older-editions switch sits right under the group it depends on
            if (i % 2 == 0) Groups.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // row pairs: both cells start at the same top
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            Groups.Children.Add(cell);
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
        string tagText = g.Needs == Engine.Session.None ? "" : Loc.S(g.Needs == Engine.Session.All ? "Tag_All" : "Tag_Part");
        var title = new WrapPanel();
        title.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        if (g.Needs != Engine.Session.None)
        {
            var tag = new Border
            {
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 1), CornerRadius = new CornerRadius(8),
                ToolTip = Tip(g.Needs),
                Child = new TextBlock { Text = tagText, FontSize = 11 },
            };
            tag.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush"); // by reference, so a theme switch restyles it
            title.Children.Add(tag);
        }
        var text = new StackPanel();
        text.Children.Add(title);
        var desc = new TextBlock { Text = descText, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        desc.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.Children.Add(desc);
        var note = new TextBlock { FontSize = 12, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed }; // why the row is off, filled by Describe
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.Children.Add(note);
        var box = new QuietCheckBox { Content = text, IsChecked = g.On, MinWidth = 0, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 8, 2) };
        System.Windows.Automation.AutomationProperties.SetName(box, tagText.Length > 0 ? $"{label}, {tagText}" : label);
        ToolTipService.SetShowOnDisabled(box, true);
        return box;
    }

    /// <summary>The row's checkbox says label and tag in its Name and the description in its HelpText; its inner texts and tick glyph would be read a second time, so it exposes no children.</summary>
    sealed class QuietCheckBox : CheckBox
    {
        public QuietCheckBox() => SetResourceReference(StyleProperty, typeof(CheckBox)); // an implicit style does not reach a subclass
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        sealed class Peer(CheckBox box) : System.Windows.Automation.Peers.CheckBoxAutomationPeer(box)
        {
            protected override List<System.Windows.Automation.Peers.AutomationPeer>? GetChildrenCore() => null;
        }
    }

    /// <summary>Tooltip, visible note and screen-reader help of row i: the description, and why the row is off in Public mode (the row's own texts dim, the note stays readable).</summary>
    void Describe(int i)
    {
        var box = boxes![i];
        var text = (StackPanel)box.Content;
        var why = box.IsEnabled ? null : Loc.S("Tip_Public");
        box.ToolTip = why;
        text.Children[0].Opacity = text.Children[1].Opacity = why == null ? 1 : 0.5;
        var note = (TextBlock)text.Children[2];
        (note.Text, note.Visibility) = (why ?? "", why == null ? Visibility.Collapsed : Visibility.Visible);
        var extra = why ?? Tip(Engine.Groups[i].Needs); // the pill's explanation is a mouse tooltip only: it goes in the help text too
        var desc = Loc.S("GroupDesc_" + Engine.Groups[i].Key);
        System.Windows.Automation.AutomationProperties.SetHelpText(box, extra == null ? desc : $"{desc.TrimEnd('.')}. {extra}");
    }

    static string? Tip(Engine.Session needs) => needs == Engine.Session.None ? null : Loc.S(needs == Engine.Session.All ? "Tip_All" : "Tip_Part");

    /// <summary>WPF does not raise LiveRegionChanged itself when a live TextBlock's text changes; a screen reader only hears the change when this does.</summary>
    static void Announce(TextBlock t, string text)
    {
        if (t.Text == text) return;
        t.Text = text;
        System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(t)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
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
            Describe(i);
        }
        if (IsLoaded) FitHeight(); // the notes of the disabled rows change their height
    }

    // ---------- watcher ----------

    void Tick()
    {
        string text, tone = "SystemFillColorSolidNeutralBrush"; SymbolRegular icon; // icon and words say the state; the colour only backs them up
        if (watcher == null) (text, icon, tone) = (cfgError, SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush");
        else
        {
            if (watcher.Scan()) Log($"game session found {watcher.At:HH:mm:ss}"); // also in public mode: it is ready when My account is chosen
            if (cts != null) (text, icon) = (Loc.S("Status_Running"), SymbolRegular.ArrowSync24);
            else if (PublicRadio.IsChecked == true) (text, icon) = (Loc.S("Status_Public"), SymbolRegular.Person24);
            else if (watcher.Token == null) (text, icon) = (Loc.S(watcher.GameSeen ? "Status_NoHunt" : "Status_Waiting"), SymbolRegular.Hourglass24);
            else if (watcher.Stale) (text, icon, tone) = (Loc.S("Status_Stale"), SymbolRegular.Warning24, "SystemFillColorCautionBrush");
            else (text, icon, tone) = (Loc.F("Status_Found", watcher.At.ToString("HH:mm")), SymbolRegular.CheckmarkCircle24, "SystemFillColorSuccessBrush");
        }
        if (text == lastStatus) return; // nothing changed: no layout work
        lastStatus = text;
        Announce(StatusText, text);
        StatusIcon.Symbol = icon;
        StatusIcon.SetResourceReference(Control.ForegroundProperty, tone);
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
        Start.Appearance = ControlAppearance.Secondary; // Cancel is not the primary action
        Start.IsDefault = false; // Enter must never cancel a run
        SetBusy(true);
        Result.IsOpen = false;
        OpenBtn.Visibility = Visibility.Collapsed;
        Start.Focus(); // Enter in a field started this run: the focused field is about to be disabled, keep the keyboard on Start (now Cancel)
        Inputs.IsEnabled = false;
        Start.Content = Loc.S("Cancel");
        lastStatus = "";
        Tick();
        ProgressText.Text = Loc.S("Progress_Starting");
        announcedGroup = null;
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
        bool done = false; // the run came back whole (with or without gaps): full bar; cancelled, stopped or failed: empty
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
            done = true;
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
        Start.Appearance = ControlAppearance.Primary;
        Start.IsDefault = true;
        Bar.IsIndeterminate = false;
        ProgressText.Text = "";
        Bar.Value = done ? Bar.Maximum : 0;
        Bar.Visibility = done ? Visibility.Visible : Visibility.Hidden; // an empty bar would look like a stray line
        lastStatus = ""; // the result is the last thing a screen reader hears: the status line is announced before it
        Tick();
        Show(sev, text, path);
        (OpenBtn.Visibility == Visibility.Visible ? OpenBtn : Start).Focus(); // the focused Cancel button was disabled and focus fell to the window
    }

    /// <summary>The total is not known yet: the bar sweeps back and forth, unless Windows animations are off. Then it stays out and the text alone says what is going on.</summary>
    void SetBusy(bool busy)
    {
        bool moves = SystemParameters.ClientAreaAnimation; // ponytail: read at each call, but the template's own short transitions are left to WPF UI
        Bar.IsIndeterminate = busy && moves;
        Bar.Visibility = busy && !moves ? Visibility.Hidden : Visibility.Visible;
    }

    void ShowProgress()
    {
        if (eng is not { } r) return;
        int d = doneNow, t = totalNow;
        bool busy = !r.TotalKnown || t == 0;
        SetBusy(busy);
        if (!busy) { Bar.Maximum = t; Bar.Value = Math.Min(d, t); }
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
        var text = $"{d} / {t}{(r.TotalKnown ? "" : "+")} · {(r.Group.Length > 0 ? Loc.S("Group_" + r.Group) : Loc.S("Progress_Starting"))}{eta}";
        if (r.Group != announcedGroup) { announcedGroup = r.Group; Announce(ProgressText, text); }
        else ProgressText.Text = text;
    }

    void Show(InfoBarSeverity sev, Func<(string Title, string Msg)> text, string? path)
    {
        resultPath = path ?? "";
        resultText = text;
        Result.Severity = sev;
        (Result.Title, Result.Message) = text();
        OpenBtn.Visibility = resultPath.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Result.IsOpen = true;
        var spoken = $"{Result.Title}. {Result.Message}";
        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseNotificationEvent( // after the focus and status changes of this turn; the InfoBar's own texts are not announced
            System.Windows.Automation.AutomationNotificationKind.ActionCompleted, System.Windows.Automation.AutomationNotificationProcessing.ImportantMostRecent, spoken, "BackupResult")));
        FitHeight();
    }

    void OpenResult()
    {
        if (resultPath.Length == 0) return;
        var arg = File.Exists(resultPath) ? $"/select,\"{resultPath}\"" : $"\"{resultPath}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = false });
    }

    async Task ShowNotCovered()
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = Loc.S("NotCovered_Link"), Owner = this, CloseButtonText = Loc.S("Close"),
            Content = new TextBlock { Text = Engine.NotCovered, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
        };
        System.Windows.Automation.AutomationProperties.SetName(box, box.Title); // else the window is named after its whole body text
        box.ContentRendered += (_, _) =>
        {
            NameTitleButtons(box); // this window's title-bar buttons have no names either
            Descendants<System.Windows.Controls.Button>(box).FirstOrDefault(b => b.Content as string == box.CloseButtonText)?.Focus(); // Enter closes it; Esc still does
        };
        await box.ShowDialogAsync();
        NotCoveredLink.Focus();
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

    static IEnumerable<T> Descendants<T>(DependencyObject o) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(o, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }
}

/// <summary>The result bar for a screen reader: one text with title and message (its own template texts are not in the control view), so the result can be read again after it was announced.</summary>
sealed class ResultBar : InfoBar
{
    public ResultBar() => SetResourceReference(StyleProperty, typeof(InfoBar)); // an implicit style does not reach a subclass
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed class Peer(ResultBar bar) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(bar)
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Text;
        protected override string GetNameCore() => $"{bar.Title}. {bar.Message}";
        protected override List<System.Windows.Automation.Peers.AutomationPeer>? GetChildrenCore() => null;
    }
}
