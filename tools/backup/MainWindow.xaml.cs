using System.Diagnostics;
using System.Runtime.InteropServices;
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
    Func<bool>? validation; // while a "you need to do something" bar is open: is its cause still there? (a run result never sets it)
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
    // window height: until the user resizes it by hand it follows the content both ways (fitted at load); after that it is max(the user's height, what the content needs). userSized: has a hand resize happened? knownHeight/placedTop: what the app left, to tell a user's resize or move from its own
    const double MinViewport = 160; // after a hand resize the window only grows to keep this much of the scrolling cards beside Start, the result bar and Details (ponytail: a constant, about the cards' first block; a measured value if a language ever needs more)
    double userHeight, knownHeight, placedTop;
    double? displacedTop; // where the window was before the app moved it up to grow; it goes back there when it shrinks, unless the user moved it meanwhile
    bool userSized, fitting, fitDone;

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
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Text.Json.JsonException) { Log($"settings.json: {e.Message}"); } // the status line says what to do; the detail is for the Details log
        Start.IsEnabled = cfg != null;

        foreach (var l in Loc.Languages) LangBox.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l.Code });
        LangBox.SelectedIndex = Array.FindIndex(Loc.Languages, l => l.Code == Loc.Code);
        var langTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) }; // arrow keys on the closed box browse the list: the language is applied once the choice has settled
        langTimer.Tick += (_, _) => { langTimer.Stop(); ApplyChosenLanguage(); };
        LangBox.SelectionChanged += (_, _) => { if (!LangBox.IsDropDownOpen) { langTimer.Stop(); langTimer.Start(); } }; // an open list applies when it closes
        LangBox.DropDownClosed += (_, _) => { langTimer.Stop(); ApplyChosenLanguage(); };
        foreach (var r in new[] { OwnRadio, PublicRadio })
        {
            r.GotKeyboardFocus += (s, _) => ((RadioButton)s).IsChecked = true; // arrow keys move and select; Tab reaches only the checked one
            r.KeyDown += (s, e) => { if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down) { (s == OwnRadio ? PublicRadio : OwnRadio).Focus(); e.Handled = true; } }; // Left and Right come from DirectionalNavigation; a Windows radio group takes all four arrows
        }
        OwnRadio.Checked += (_, _) => ApplyMode();
        PublicRadio.Checked += (_, _) => ApplyMode();
        Browse.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { InitialDirectory = OutDir.Text };
            if (d.ShowDialog(this) == true) OutDir.Text = d.FolderName;
        };
        Start.Click += OnStart;
        foreach (var f in new[] { Who, OutDir }) f.KeyDown += (_, e) => { if (e.Key != System.Windows.Input.Key.Enter) return; e.Handled = true; if (!e.IsRepeat) OnStart(this, e); }; // Enter starts a backup only from the two fields (Start is not the window's default button, so Enter on a switch or a tick does nothing)
        Start.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && e.IsRepeat) e.Handled = true; }; // a held Enter that started a run from a field moves on to Start, which is Cancel by then: only a fresh press may use it
        OpenBtn.Click += (_, _) => OpenResult();
        NotCoveredLink.Click += async (_, _) => await ShowNotCovered();
        HowLink.Click += async (_, _) => await ShowHow();
        SelectAll.Click += (_, _) => SelectGroups(true);
        SelectNone.Click += (_, _) => SelectGroups(false);
        Who.TextChanged += (_, _) => Recheck();
        Groups.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Recheck()));
        Groups.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => Recheck()));
        Details.Expanded += (_, _) => { RenderLog(); Dispatcher.BeginInvoke(FitHeight, DispatcherPriority.Background); }; // the Expander shows or hides its content after the event
        Details.SizeChanged += (_, e) => { if (IsLoaded && !Details.IsExpanded && e.HeightChanged) Dispatcher.BeginInvoke(FitHeight, DispatcherPriority.Background); }; // it hides the log after the Collapsed event, when its height drops
        StateChanged += (_, _) => { NameTitleButtons(this); if (WindowState == WindowState.Normal) Dispatcher.BeginInvoke(FitHeight, DispatcherPriority.Background); }; // content may have changed while it was maximized or minimized
        SizeChanged += (_, e) => // a height the app did not set is the user's (drag, keyboard size, snap): it becomes the height to keep
        {
            if (!e.HeightChanged || !fitDone || fitting || WindowState != WindowState.Normal || Math.Abs(ActualHeight - knownHeight) < 0.5) return; // a restore brings back the known height
            (userHeight, userSized, knownHeight) = (ActualHeight, true, ActualHeight);
        };
        watchTimer.Tick += (_, _) => { Tick(); Recheck(); };
        runTimer.Tick += (_, _) => { FlushLog(); ShowProgress(); };
        watchTimer.Start();
        ApplyLanguage();
        ApplyMode();
        SourceInitialized += (_, _) => AddSystemMenu(this);
        ContentRendered += (_, _) => SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush"); // FluentWindow resets it to its own dark grey when it applies the backdrop
        Loaded += (_, _) => { NameTitleButtons(this); HideGlyphs(); FitHeight(); };
        Closed += (_, _) => { watchTimer.Stop(); runTimer.Stop(); langTimer.Stop(); cts?.Cancel(); };
    }

    /// <summary>Until the user resizes the window by hand its height is the content's, up to the work area: fitted at load, taller or shorter again when a language, the result bar or the log change what the content needs. A hand resize (drag, keyboard Size, snap) sets a floor, the height the user gave it, and from then on "the content" is only what cannot scroll: Start, the result bar and Details, with MinViewport of the cards; the window grows to that and goes back to the floor, never below it. Top stays unless the window would leave the work area; the app moves it up only as far as needed and back when it shrinks. Nothing is touched while maximized or minimized, so Restore returns the old bounds.</summary>
    void FitHeight()
    {
        UpdateLayout(); // settle pending changes first: the manual Measure below left them half-measured (Start kept the width of "Cancelling…" and its label sat off-centre)
        if (WindowState != WindowState.Normal) return; // StateChanged fits again on Restore
        var m = Body.Margin; // Measure takes the size of the slot, margins included, and DesiredSize comes back with them: ActualWidth/Height are the inside
        var chrome = ActualHeight - (Body.ActualHeight + m.Top + m.Bottom);
        Body.Measure(new Size(Body.ActualWidth + m.Left + m.Right, double.PositiveInfinity));
        double full = chrome + Body.DesiredSize.Height, scroll = Scroller.DesiredSize.Height;
        Body.InvalidateMeasure(); // the infinite measure was only a question
        var area = SystemParameters.WorkArea;
        double need = userSized ? full - scroll + Math.Min(scroll, MinViewport) : full;
        double h = Math.Min(Math.Max(userSized ? Math.Max(userHeight, need) : need, MinHeight), area.Height), top = Top;
        if (Math.Abs(h - ActualHeight) >= 0.5)
        {
            double wanted = displacedTop is { } d && Math.Abs(Top - placedTop) < 1 ? d : Top; // the user moved it: that is the place now
            top = Math.Max(area.Top, Math.Min(wanted, area.Bottom - h));
            displacedTop = top < wanted - 0.5 ? wanted : null;
        }
        fitting = true;
        (Height, Top) = (h, top);
        UpdateLayout();
        fitting = false;
        (knownHeight, placedTop) = (ActualHeight, Top);
        fitDone = true;
    }

    /// <summary>Only changes enabled rows: in Public mode the token-only groups stay off. The older-editions switch follows through SyncOlder.</summary>
    void SelectGroups(bool on)
    {
        foreach (var b in boxes!) if (b.IsEnabled) b.IsChecked = on;
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);

    /// <summary>The custom title bar leaves the window without WS_SYSMENU, so Alt+Space and a right click on the bar open nothing and there is no keyboard Move or Size. Also used for the dialog's window.</summary>
    static void AddSystemMenu(Window w)
    {
        const int GwlStyle = -16, WsSysMenu = 0x80000;
        var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        SetWindowLong(h, GwlStyle, GetWindowLong(h, GwlStyle) | WsSysMenu);
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
        Zip.Content = Wrapped(Loc.S("Zip"));
        Skip.Content = Wrapped(Loc.S("Skip"));
        Upload.Content = Wrapped(Loc.S("Upload"));
        BuildOlder();
        if (cts == null) Start.Content = Loc.S("Start");
        Named(Bar, "Bar_Name");
        OpenBtn.Content = Loc.S("Open_Folder");
        Details.Header = Loc.S("Details");
        Named(LogBox, "Log_Name");
        NotCoveredLink.Content = Loc.S("NotCovered_Link");
        HowLink.Content = Loc.S("How_Link");
        SelectAll.Content = Loc.S("Groups_All");
        SelectNone.Content = Loc.S("Groups_None");
        NameTitleButtons(this);
        BuildRows();
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
        ColLeft.Children.Clear();
        ColRight.Children.Clear();
        for (int i = 0; i < boxes.Length; i++)
        {
            if (old != null) (boxes[i].IsChecked, boxes[i].IsEnabled) = (old[i].IsChecked, old[i].IsEnabled);
            Describe(i);
            var column = i < (boxes.Length + 1) / 2 ? ColLeft : ColRight; // group order runs down the left column, then down the right one: Tab and screen readers follow it
            column.Children.Add(boxes[i]);
            if (i == HistoryAt) column.Children.Add(Older); // the older-editions switch sits right under the group it depends on
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
        PaintOlder();
    }

    TextBlock? olderNote;

    /// <summary>The older-editions switch: its label and, in the same style as a group row's cost line, what it costs. The label dims by itself when the switch is off; the note takes the same disabled brush from the app (as an off row does, see Describe).</summary>
    void BuildOlder()
    {
        var label = Loc.S("Older");
        olderNote = new TextBlock { Text = Loc.S("Slow_older"), FontSize = 12, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        Older.Content = new StackPanel { Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, olderNote } };
        System.Windows.Automation.AutomationProperties.SetName(Older, label);
        System.Windows.Automation.AutomationProperties.SetHelpText(Older, Loc.S("Slow_older"));
        PaintOlder();
    }

    void PaintOlder()
    {
        if (olderNote == null) return;
        if (Older.ReadLocalValue(IsEnabledProperty) is false) olderNote.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFillColorDisabledBrush");
        else olderNote.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
    }

    /// <summary>The options that make a backup take a long time: a "slow" tag and a cost line on the row, and a confirmation at Start. The older-editions switch is the fourth (see SlowPicked). The cost lines are the resources Slow_&lt;key&gt;.</summary>
    static bool IsSlow(string key) => key is "kills" or "photos" or "history";

    CheckBox Row((string Key, bool On, Engine.Session Needs) g)
    {
        string label = Loc.S("Group_" + g.Key), descText = Loc.S("GroupDesc_" + g.Key);
        string tagText = g.Needs == Engine.Session.None ? "" : Loc.S(g.Needs == Engine.Session.All ? "Tag_All" : "Tag_Part");
        var title = new WrapPanel();
        title.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        static Border Pill(string text, string? tip) => new()
        {
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(8),
            ToolTip = tip,
            Child = new TextBlock { Text = text, FontSize = 11 },
        };
        if (g.Needs != Engine.Session.None) title.Children.Add(Pill(tagText, Tip(g.Needs)));
        string slowText = IsSlow(g.Key) ? Loc.S("Tag_Slow") : "";
        if (slowText.Length > 0) title.Children.Add(Pill(slowText, Loc.S("Tip_Slow")));
        var text = new StackPanel();
        text.Children.Add(title);
        var desc = new TextBlock { Text = descText, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        text.Children.Add(desc);
        var note = new TextBlock { FontSize = 12, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed }; // why the row is off, or its cost when it is slow; filled by Describe
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.Children.Add(note);
        var box = new QuietCheckBox { Content = text, IsChecked = g.On, MinWidth = 0, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 8, 4) };
        var tags = string.Join(", ", new[] { label, tagText, slowText }.Where(t => t.Length > 0));
        System.Windows.Automation.AutomationProperties.SetName(box, tags);
        ToolTipService.SetShowOnDisabled(box, true);
        return box;
    }

    /// <summary>The row's checkbox says label and tag in its Name and the description in its HelpText; its inner texts and tick glyph would be read a second time, so it exposes no children.</summary>
    sealed class QuietCheckBox : CheckBox
    {
        public QuietCheckBox() => SetResourceReference(StyleProperty, typeof(CheckBox)); // an implicit style does not reach a subclass

        /// <summary>The template's BulletDecorator centres the tick on the whole row (a VerticalAlignment on the tick does not move it); with a description and a cost line it sat far below the title. It is lifted to the top, level with the title's first line, whatever the row's height. The whole row stays the click target: only the drawing moves.</summary>
        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (GetTemplateChild("ControlBorderIconPresenter") is not FrameworkElement { Parent: not null } tick) return;
            SizeChanged += (_, _) => tick.RenderTransform = new System.Windows.Media.TranslateTransform(0, -Math.Max(0, ((FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(tick)).ActualHeight - tick.ActualHeight) / 2);
        }
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
        bool off = why != null; // the disabled brush alone dims the row; the old Opacity on top of it left title, description and tag under 3:1
        var title = (WrapPanel)text.Children[0];
        // an off row names its brush itself, resolved here from the app: while a backup runs the cards map the disabled keys to readable ones and an off row must not follow
        void Paint(TextBlock t, string? on) { if (off) t.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFillColorDisabledBrush"); else if (on == null) t.ClearValue(TextBlock.ForegroundProperty); else t.SetResourceReference(TextBlock.ForegroundProperty, on); }
        Paint((TextBlock)title.Children[0], null);
        Paint((TextBlock)text.Children[1], "TextFillColorSecondaryBrush");
        foreach (var pill in title.Children.OfType<Border>()) // the tags: on their fill the disabled text is under 3:1, so an off row shows them as plain text
        {
            Paint((TextBlock)pill.Child, null);
            if (off) pill.Background = System.Windows.Media.Brushes.Transparent;
            else pill.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush"); // by reference, so a theme switch restyles it
        }
        var key = Engine.Groups[i].Key;
        var cost = IsSlow(key) ? Loc.S("Slow_" + key) : null;
        var note = (TextBlock)text.Children[2];
        var shown = why ?? cost;
        (note.Text, note.Visibility) = (shown ?? "", shown == null ? Visibility.Collapsed : Visibility.Visible);
        // the pills' explanations are mouse tooltips only: they go in the help text too
        var parts = new[] { Loc.S("GroupDesc_" + key), why ?? Tip(Engine.Groups[i].Needs), cost, IsSlow(key) ? Loc.S("Tip_Slow") : null }.OfType<string>().Select(p => p.TrimEnd('.')).ToList();
        System.Windows.Automation.AutomationProperties.SetHelpText(box, string.Join(". ", parts) + (parts.Count > 1 ? "." : ""));
    }

    static string? Tip(Engine.Session needs) => needs == Engine.Session.None ? null : Loc.S(needs == Engine.Session.All ? "Tip_All" : "Tip_Part");

    /// <summary>WPF does not raise LiveRegionChanged itself when a live TextBlock's text changes; a screen reader only hears the change when this does.</summary>
    static void Announce(TextBlock t, string text)
    {
        if (t.Text == text) return;
        t.Text = text;
        System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(t)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    /// <summary>A window-level announcement that can be said again (the live regions above only speak when their text changes). Posted after the focus and status changes of this turn.</summary>
    void Say(string text, string id, System.Windows.Automation.AutomationNotificationKind kind) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseNotificationEvent(
            kind, System.Windows.Automation.AutomationNotificationProcessing.ImportantMostRecent, text, id)));

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
        Recheck();
        if (IsLoaded) FitHeight(); // the notes of the disabled rows change their height
    }

    // ---------- watcher ----------

    void Tick()
    {
        string text, tone = "SystemFillColorSolidNeutralBrush"; SymbolRegular icon; // icon and words say the state; the colour only backs them up
        if (watcher == null) (text, icon, tone) = (Loc.S("Status_Settings"), SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush");
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
        if (cfg == null) { Say(Loc.S("Status_Settings"), "SettingsMissing", System.Windows.Automation.AutomationNotificationKind.ActionAborted); return; } // Start is disabled then, so only Enter in a field gets here: the status line already says it, a screen reader is told again
        bool Blocked()
        {
            if (Problem() is not { } p) return false;
            Show(InfoBarSeverity.Informational, () => (Loc.S("Msg_" + p), Loc.S("Msg_" + p + "Text")), null, () => Problem() == p);
            if (p == "NoProfile") Who.Focus();
            return true;
        }
        if (Blocked()) return;
        if (SlowPicked() is { Count: > 0 } slow && !await ConfirmSlow(slow)) return;
        if (Blocked()) return; // the session may have gone while the dialog was open
        bool own = OwnRadio.IsChecked == true;
        var who = own ? "" : Who.Text.Trim();
        var keys = Engine.Groups.Where((_, i) => boxes![i].IsChecked == true).Select(g => g.Key).ToHashSet();

        cts = new();
        Start.Appearance = ControlAppearance.Secondary; // Cancel is not the primary action
        SetBusy(true);
        Result.IsOpen = false;
        validation = null;
        OpenBtn.Visibility = Visibility.Collapsed;
        Start.Focus(); // Enter in a field started this run: the cards are about to be locked, keep the keyboard on Start (now Cancel)
        LockInputs(true);
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
            var (huntsSaved, huntsListed, huntsListFailed) = (run.HuntsSaved, run.HuntsListed, run.HuntsListFailed);
            var huntsShort = huntsListed != null;
            var (setupItems, setupMaps, setupCheck) = (run.SetupItems, run.SetupReserves, run.SetupChecks);
            sev = failed == 0 && !setupCheck && !huntsShort ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            var finished = DateTime.Now;
            text = () => (Loc.S(failed == 0 ? "Msg_Saved" : "Msg_SavedGaps"),
                Loc.F("Msg_Counts", saved, skipped, failed) + (stale ? " " + Loc.S("Msg_Stale") : failed > 0 ? $" {run.FailureSummary} {Loc.S("Msg_Retry")}" : "")
                + (gone > 0 ? " " + Loc.F("Msg_Dead", gone) : "")
                + (killsOff ? " " + Loc.S("Msg_KillsOff") : "")
                + (opened > 0 ? " " + Loc.F("Msg_MessagesOpened", opened) : "")
                + (huntsListFailed ? " " + Loc.F("Msg_HuntsListFailed", huntsSaved!) : huntsShort ? " " + Loc.F("Msg_HuntsShort", huntsSaved!, huntsListed!) : "")
                + (setupItems is { } n ? " " + (setupCheck ? Loc.F("Msg_SetupsCheck", n, setupMaps) : n == 0 ? Loc.S("Msg_SetupsNone") : Loc.F("Msg_Setups", n, setupMaps)) : "")
                + (failed == 0 ? "\n" + Loc.F("Msg_Finished", finished) : ""));
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
        LockInputs(false);
        Start.IsEnabled = true;
        Start.Content = Loc.S("Start");
        Start.Appearance = ControlAppearance.Primary;
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

    // while a backup runs the cards are disabled (a screen reader sees them as such) but keep what is being saved readable: each disabled brush the controls use is mapped to the brush of the enabled control
    static readonly (string Disabled, string Normal)[] RunLook =
    [
        ("TextFillColorDisabledBrush", "TextFillColorPrimaryBrush"), // texts and the tick of a disabled box
        ("CheckBoxForegroundUncheckedDisabled", "CheckBoxForeground"), ("CheckBoxCheckBackgroundFillUncheckedDisabled", "TextControlBackground"), ("CheckBoxCheckBackgroundStrokeUncheckedDisabled", "TextControlElevationBorderBrush"), // the box of an unticked group (a ticked one is set in LockInputs)
        ("RadioButtonForegroundDisabled", "RadioButtonForeground"), ("RadioButtonOuterEllipseFillDisabled", "RadioButtonOuterEllipseFill"), ("RadioButtonOuterEllipseStrokeDisabled", "RadioButtonOuterEllipseStroke"), ("RadioButtonOuterEllipseCheckedStrokeDisabled", "RadioButtonOuterEllipseCheckedStroke"),
        ("ToggleSwitchContentForegroundDisabled", "ToggleSwitchContentForeground"), ("ToggleSwitchFillOnDisabled", "ToggleSwitchFillOn"), ("ToggleSwitchStrokeOnDisabled", "ToggleSwitchStrokeOn"), ("ToggleSwitchKnobFillOnDisabled", "ToggleSwitchKnobFillOn"),
        ("ToggleSwitchFillOffDisabled", "ToggleSwitchFillOff"), ("ToggleSwitchStrokeOffDisabled", "ToggleSwitchStrokeOff"), ("ToggleSwitchKnobFillOffDisabled", "ToggleSwitchKnobFillOff"),
        ("TextControlForegroundDisabled", "TextControlForeground"), ("ComboBoxForegroundDisabled", "ComboBoxForeground"),
    ];

    /// <summary>The controls cannot be used while a backup runs. Disabled, so a screen reader cannot toggle or invoke them either; the scroll region around them stays enabled for the wheel, and the status line and the heading are not locked, so they keep reading as enabled.
    /// A control that is off for its own reason (Public mode's token groups, the older-editions switch without its group, Upload) keeps its idle disabled look; only the ones this method locks get the readable one.</summary>
    void LockInputs(bool locked)
    {
        foreach (var c in new FrameworkElement[] { LangBox, SourceRow, SelectAll, SelectNone, Groups, Options }) c.IsEnabled = !locked;
        var look = Inputs.Resources.MergedDictionaries;
        look.Clear();
        foreach (var c in (Control[])[Older, Upload, .. boxes ?? []])
        {
            c.Resources.Clear();
            if (!locked) continue;
            if (c.ReadLocalValue(IsEnabledProperty) is false) // off for its own reason: the same keys, from the app, so the run look above does not reach it
                foreach (var (disabled, _) in RunLook) { if (TryFindResource(disabled) is { } idle) c.Resources[disabled] = idle; }
            else if (c is CheckBox { IsChecked: true }) // a ticked group keeps its orange box, like the Zip and Skip switches that are on: "on" reads the same way everywhere (the template has one disabled fill for ticked and unticked boxes)
                c.Resources["CheckBoxCheckBackgroundFillUncheckedDisabled"] = c.Resources["CheckBoxCheckBackgroundStrokeUncheckedDisabled"] = TryFindResource("CheckBoxCheckBackgroundFillChecked");
        }
        foreach (var r in new[] { OwnRadio, PublicRadio }) // the template dims a disabled, selected radio to 70% by a trigger on its own parts; a local value beats it
            foreach (var part in new[] { "CheckOuterEllipse", "CheckGlyph" })
                if (r.Template.FindName(part, r) is UIElement e) { if (locked && r.IsChecked == true) e.Opacity = 1; else e.ClearValue(OpacityProperty); }
        if (!locked) return;
        var d = new ResourceDictionary();
        foreach (var (disabled, normal) in RunLook) if (TryFindResource(normal) is { } brush) d[disabled] = brush;
        look.Add(d);
    }

    /// <summary>The Details chevron sits inside the Expander's template, which WPF cannot take out of the control view without a new template: its private-use character gets a blank name and is marked off screen, so a screen reader skips it.</summary>
    void HideGlyphs()
    {
        foreach (var t in Descendants<TextBlock>(Details))
            if (t.Text.Length == 1 && t.Text[0] >= '\uE000')
            {
                System.Windows.Automation.AutomationProperties.SetName(t, " ");
                System.Windows.Automation.AutomationProperties.SetIsOffscreenBehavior(t, System.Windows.Automation.IsOffscreenBehavior.Offscreen);
            }
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

    /// <summary>What stops a run now, the cheapest fix first: tick a group, type a handle, and only then a game session, which needs the game. The key is the end of the Msg_ names.</summary>
    string? Problem()
    {
        if (boxes!.All(b => b.IsChecked != true)) return "Nothing";
        if (PublicRadio.IsChecked == true) return Who.Text.Trim().Length == 0 ? "NoProfile" : null;
        return watcher?.Token == null ? "NoSession" : null;
    }

    /// <summary>A bar that asks the user to do something goes as soon as it is done. Run results stay until the next run.</summary>
    void Recheck()
    {
        if (validation is not { } stillApplies || stillApplies()) return;
        (validation, resultText) = (null, null);
        Result.IsOpen = false;
        OpenBtn.Visibility = Visibility.Collapsed;
        if (IsLoaded) FitHeight();
    }

    void Show(InfoBarSeverity sev, Func<(string Title, string Msg)> text, string? path, Func<bool>? stillApplies = null)
    {
        validation = stillApplies;
        resultPath = path ?? "";
        resultText = text;
        Result.Severity = sev;
        (Result.Title, Result.Message) = text();
        OpenBtn.Visibility = resultPath.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Result.IsOpen = true;
        Say($"{Result.Title}. {Result.Message}", "BackupResult", System.Windows.Automation.AutomationNotificationKind.ActionCompleted);
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
        var body = new StackPanel { MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var item in Engine.NotCovered)
        {
            var row = new Grid { Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new TextBlock { Text = "•" };
            var text = new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(text, 1);
            row.Children.Add(dot);
            row.Children.Add(text);
            body.Children.Add(row);
        }
        var box = new Wpf.Ui.Controls.MessageBox { Title = Loc.S("NotCovered_Link"), Owner = this, CloseButtonText = Loc.S("Close"), Content = Scrolled(body) };
        await Ask(box, string.Join(" ", Engine.NotCovered));
    }

    /// <summary>The short version of the safety and snapshot facts of docs/backup.md ("How it works, in short"): five headings, each with one to three short paragraphs.</summary>
    async Task ShowHow()
    {
        var body = new StackPanel { MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left }; // below the dialog width (Ask): measured wider it would be sized for fewer lines than it shows
        var help = new List<string>();
        foreach (var section in new[] { "1ab", "2ab", "3abc", "4ab", "5ab" }) // How_<n>H is the heading, How_<n>a.. the paragraphs
        {
            var head = new TextBlock { Text = Loc.S($"How_{section[0]}H"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 14, 0, 2) };
            System.Windows.Automation.AutomationProperties.SetHeadingLevel(head, System.Windows.Automation.AutomationHeadingLevel.Level1);
            body.Children.Add(head);
            help.Add(head.Text + ".");
            foreach (var p in section[1..])
            {
                var t = Loc.S($"How_{section[0]}{p}");
                body.Children.Add(new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
                help.Add(t);
            }
        }
        var box = new Wpf.Ui.Controls.MessageBox { Title = Loc.S("How_Link"), Owner = this, CloseButtonText = Loc.S("Close"), Content = Scrolled(body) };
        await Ask(box, string.Join(" ", help));
    }

    /// <summary>The ticked slow options with their cost lines: the four of IsSlow and the older-editions switch.</summary>
    List<(string Name, string Cost)> SlowPicked() =>
    [
        .. Engine.Groups.Where((g, i) => IsSlow(g.Key) && boxes![i].IsChecked == true).Select(g => (Loc.S("Group_" + g.Key), Loc.S("Slow_" + g.Key))),
        .. Older.IsChecked == true ? [(Loc.S("Older"), Loc.S("Slow_older"))] : Array.Empty<(string, string)>(),
    ];

    /// <summary>Asked at Start when a slow option is ticked. Back is the focused button and Esc; only "Start backup" lets the run begin.</summary>
    async Task<bool> ConfirmSlow(List<(string Name, string Cost)> slow)
    {
        var body = new StackPanel { MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        var intro = Loc.S("Slow_Intro");
        body.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap });
        var help = new List<string> { intro };
        foreach (var (name, cost) in slow)
        {
            var item = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            item.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var c = new TextBlock { Text = cost, TextWrapping = TextWrapping.Wrap };
            c.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            item.Children.Add(c);
            body.Children.Add(item);
            help.Add($"{name}. {cost}");
        }
        var outro = Loc.S("Slow_Outro");
        body.Children.Add(new TextBlock { Text = outro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) });
        help.Add(outro);
        var box = new Wpf.Ui.Controls.MessageBox { Title = Loc.S("Slow_Title"), Owner = this, PrimaryButtonText = Loc.S("Slow_Start"), CloseButtonText = Loc.S("Slow_Back"), Content = Scrolled(body) };
        return await Ask(box, string.Join(" ", help)) == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    /// <summary>The dialog body in a scroll region of its own, as tall as the work area allows. It is only a Tab stop (and so scrolls with the arrow and page keys) when the text does not fit, see Ask.</summary>
    static UIElement Scrolled(UIElement content) => new ScrollViewer { Content = content, Tag = "body", VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(160, SystemParameters.WorkArea.Height - 240) };

    /// <summary>Shows a dialog over this window. Its title names the window (else the whole body does); its buttons carry the body as help text, because focus lands on a button and a screen reader does not read a WPF dialog's body by itself. Focus starts on the close button (the safe one: Enter closes it, Esc still does) and goes back to where it was.</summary>
    async Task<Wpf.Ui.Controls.MessageBoxResult> Ask(Wpf.Ui.Controls.MessageBox box, string help)
    {
        var back = System.Windows.Input.Keyboard.FocusedElement;
        box.MaxWidth = 600; // WPF UI stops a dialog at 450, which made the longer texts tall
        System.Windows.Automation.AutomationProperties.SetName(box, box.Title);
        box.SourceInitialized += (_, _) => AddSystemMenu(box);
        box.ContentRendered += (_, _) =>
        {
            NameTitleButtons(box); // this window's title-bar buttons have no names either
            var buttons = Descendants<System.Windows.Controls.Button>(box).Where(b => b.Content is string s && (s == box.PrimaryButtonText || s == box.CloseButtonText)).ToList();
            foreach (var b in buttons) System.Windows.Automation.AutomationProperties.SetHelpText(b, help);
            buttons.FirstOrDefault(b => b.Content as string == box.CloseButtonText)?.Focus();
            foreach (var sv in Descendants<ScrollViewer>(box).Where(s => s.Tag as string == "body"))
            {
                if (sv.ScrollableHeight < 1) { sv.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; continue; } // the dialog is sized in whole pixels: a fraction of one would show a bar for text that fits
                sv.Focusable = sv.IsTabStop = true; // a keyboard user can reach the rest of the text
                System.Windows.Automation.AutomationProperties.SetName(sv, box.Title);
            }
        };
        var result = await box.ShowDialogAsync();
        back?.Focus();
        return result;
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

/// <summary>An icon that is only decoration: a screen reader does not see it or its private-use glyph.</summary>
sealed class DecorIcon : SymbolIcon
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed class Peer(DecorIcon icon) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(icon)
    {
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
        protected override List<System.Windows.Automation.Peers.AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>The scroll region of the cards. A plain ScrollViewer takes the arrow keys of a control inside it and moves focus by its own guess, which broke the arrow keys of the two radios; here they go to the controls (focus moving scrolls it into view), and the other keys still scroll.</summary>
sealed class CardScroll : ScrollViewer
{
    public CardScroll() => SetResourceReference(StyleProperty, typeof(ScrollViewer)); // an implicit style does not reach a subclass

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.OriginalSource != this && e.Key is System.Windows.Input.Key.Left or System.Windows.Input.Key.Right or System.Windows.Input.Key.Up or System.Windows.Input.Key.Down) return;
        base.OnKeyDown(e);
    }
}
