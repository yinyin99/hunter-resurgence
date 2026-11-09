using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using Backup;
using Wpf.Ui.Controls;
using CheckBox = System.Windows.Controls.CheckBox;

namespace Resurgence.Tests.BackupTool;

/// <summary>The decisions of the backup window, as the static functions it calls: no window is built. Expected text comes from Loc, not from English literals.</summary>
public class WindowLogicTests
{
    /// <summary>CheckBox needs an STA thread.</summary>
    static void Sta(Action a)
    {
        Exception? ex = null;
        var th = new Thread(() => { try { a(); } catch (Exception e) { ex = e; } });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join();
        if (ex != null) ExceptionDispatchInfo.Capture(ex).Throw();
    }

    static readonly DateTime Finished = new(2026, 1, 2, 3, 4, 0);
    static MainWindow.RunSummary Whole => new(true, false, 5, 2, 0, 0, [], false, 0, null, null, false, null, 0, false, () => "FS");
    static string Msg(MainWindow.RunSummary s) => MainWindow.Words(s, Finished).Msg;

    [Fact]
    public void SeverityIsSuccessOnlyForAWholeRunWithNoChecks()
    {
        Assert.Equal(InfoBarSeverity.Success, MainWindow.Severity(Whole));
        Assert.Equal(InfoBarSeverity.Warning, MainWindow.Severity(Whole with { Whole = false }));
        Assert.Equal(InfoBarSeverity.Warning, MainWindow.Severity(Whole with { SetupCheck = true }));
        Assert.Equal(InfoBarSeverity.Warning, MainWindow.Severity(Whole with { HuntsListed = 3, HuntsSaved = 2 }));
    }

    [Fact]
    public void TitleAndMessageOfAWholeRun()
    {
        var (title, msg) = MainWindow.Words(Whole, Finished);
        Assert.Equal(Loc.S("Msg_Saved"), title);
        Assert.Equal(Loc.F("Msg_Counts", 5, 2, 0) + "\n" + Loc.F("Msg_Finished", Finished), msg);
        Assert.Equal(Loc.S("Msg_SavedGaps"), MainWindow.Words(Whole with { Whole = false }, Finished).Title);
    }

    [Fact]
    public void AnIncompleteRunSaysWhyAndWhatToDo()
    {
        var s = Whole with { Whole = false, Failed = 3 };
        Assert.Equal(Loc.F("Msg_Counts", 5, 2, 3) + $" FS {Loc.S("Msg_Retry")}", Msg(s)); // no "finished" line
        Assert.Equal(Loc.F("Msg_Counts", 5, 2, 3) + " " + Loc.S("Msg_Stale"), Msg(s with { Stale = true })); // a refused session replaces the summary
    }

    [Fact]
    public void EachPieceAppearsOnlyWhenItApplies()
    {
        var silent = Msg(Whole with { Silent = [("a.test", 3), ("b.test", 4)] });
        Assert.Contains(Loc.F("Msg_HostSilent", "a.test", 3) + " " + Loc.F("Msg_HostSilent", "b.test", 4), silent);
        Assert.Contains(" " + Loc.F("Msg_Dead", 2), Msg(Whole with { Gone = 2 }));
        Assert.DoesNotContain(Loc.F("Msg_Dead", 0), Msg(Whole));
        Assert.Contains(Loc.S("Msg_KillsOff"), Msg(Whole with { KillsOff = true }));
        Assert.Contains(Loc.F("Msg_MessagesOpened", 4), Msg(Whole with { Opened = 4 }));
    }

    [Fact]
    public void PicturesAreOnByDefaultAndMessagesAndHistoryAreOff()
    {
        Assert.All(new[] { "kills", "photos" }, k => Assert.True(Engine.Groups.Single(g => g.Key == k).On));
        Assert.All(new[] { "messages", "history" }, k => Assert.False(Engine.Groups.Single(g => g.Key == k).On));
        Assert.DoesNotContain(Loc.S("Msg_KillsOff"), Msg(Whole));
    }

    [Fact]
    public void NotCoveredNoLongerClaimsAchievementDates()
    {
        Assert.DoesNotContain(Engine.NotCovered, s => s.Contains("chievement", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ACutHuntListWinsOverAShortOne()
    {
        var s = Whole with { HuntsSaved = 2, HuntsListed = 3 };
        Assert.Contains(Loc.F("Msg_HuntsShort", 2, 3), Msg(s));
        s = s with { HuntsListFailed = true };
        Assert.Contains(Loc.F("Msg_HuntsListFailed", 2), Msg(s));
        Assert.DoesNotContain(Loc.F("Msg_HuntsShort", 2, 3), Msg(s));
    }

    [Fact]
    public void MapSetupsAreWordedByTheirCount()
    {
        Assert.Contains(Loc.S("Msg_SetupsNone"), Msg(Whole with { SetupItems = 0 }));
        Assert.Contains(Loc.F("Msg_Setups", 4, 2), Msg(Whole with { SetupItems = 4, SetupMaps = 2 }));
        Assert.Contains(Loc.F("Msg_SetupsCheck", 4, 2), Msg(Whole with { SetupItems = 4, SetupMaps = 2, SetupCheck = true }));
    }

    [Theory]
    [InlineData(false, true, "", false, "Nothing")]
    [InlineData(true, true, "  ", true, "NoProfile")]
    [InlineData(true, true, " a ", false, null)]
    [InlineData(true, false, "x", false, "NoSession")]
    [InlineData(true, false, "", true, null)]
    public void ProblemNamesWhatStopsARun(bool ticked, bool isPublic, string handle, bool token, string? expected) =>
        Assert.Equal(expected, MainWindow.Problem(ticked, isPublic, handle, token));

    static CheckBox[] Boxes() => [.. Engine.Groups.Select(g => new CheckBox { IsChecked = true })];

    [Fact]
    public void PublicModeSwitchesOffTheSessionOnlyGroupsAndMyAccountBringsBackTheTicks()
    {
        Sta(() =>
        {
            var boxes = Boxes();
            var remembered = new bool[boxes.Length];
            Array.Fill(remembered, true);
            MainWindow.ApplyMode(false, boxes, remembered);
            for (int i = 0; i < boxes.Length; i++)
            {
                var all = Engine.Groups[i].Needs == Engine.Session.All;
                Assert.Equal(!all, boxes[i].IsEnabled);
                Assert.True(boxes[i].IsChecked == !all);
            }
            MainWindow.ApplyMode(false, boxes, remembered); // twice: the disabled rows are not remembered as unticked
            Assert.All(Enumerable.Range(0, boxes.Length).Where(i => Engine.Groups[i].Needs == Engine.Session.All), i => Assert.True(remembered[i]));

            var one = Array.FindIndex(Engine.Groups, g => g.Needs == Engine.Session.All);
            remembered[one] = false;
            MainWindow.ApplyMode(true, boxes, remembered);
            Assert.True(boxes.All(b => b.IsEnabled));
            Assert.Equal(Engine.Groups.Select((_, i) => i != one), boxes.Select(b => b.IsChecked == true));
        });
    }

    [Fact]
    public void SelectAllTicksOnlyTheEnabledRows() => Sta(() =>
    {
        CheckBox[] boxes = [new() { IsChecked = false }, new() { IsChecked = false, IsEnabled = false }];
        MainWindow.SelectGroups(boxes, true);
        Assert.Equal([true, false], boxes.Select(b => b.IsChecked == true));
        MainWindow.SelectGroups(boxes, false);
        Assert.False(boxes[0].IsChecked);
    });

    [Theory]
    [InlineData(false, false, false, false, false, false, "Status_Settings", "SystemFillColorCriticalBrush")]
    [InlineData(true, true, true, false, false, false, "Status_Running", "SystemFillColorSolidNeutralBrush")] // running beats everything
    [InlineData(true, false, true, false, false, false, "Status_Public", "SystemFillColorSolidNeutralBrush")] // public beats "no session"
    [InlineData(true, false, false, false, true, false, "Status_NoHunt", "SystemFillColorSolidNeutralBrush")]
    [InlineData(true, false, false, false, false, false, "Status_Waiting", "SystemFillColorSolidNeutralBrush")]
    [InlineData(true, false, false, true, true, true, "Status_Stale", "SystemFillColorCautionBrush")]
    [InlineData(true, false, false, true, true, false, "Status_Found", "SystemFillColorSuccessBrush")]
    public void StatusFollowsTheState(bool watcher, bool running, bool isPublic, bool token, bool seen, bool stale, string key, string tone)
    {
        var s = MainWindow.Status(watcher, running, isPublic, token, seen, stale);
        Assert.Equal((key, tone), (s.Key, s.Tone));
    }

    [Fact]
    public void StatusIconsDifferPerState()
    {
        var icons = new[]
        {
            MainWindow.Status(false, false, false, false, false, false), MainWindow.Status(true, true, false, false, false, false), MainWindow.Status(true, false, true, false, false, false),
            MainWindow.Status(true, false, false, false, true, false), MainWindow.Status(true, false, false, true, true, true), MainWindow.Status(true, false, false, true, true, false),
        }.Select(s => s.Icon);
        Assert.Equal([SymbolRegular.ErrorCircle24, SymbolRegular.ArrowSync24, SymbolRegular.Person24, SymbolRegular.Hourglass24, SymbolRegular.Warning24, SymbolRegular.CheckmarkCircle24], icons);
    }

    [Fact]
    public void EtaBoundaries()
    {
        Assert.Equal(Loc.S("Eta_Less"), MainWindow.Eta(44));
        Assert.Equal(Loc.F("Eta_Min", 1), MainWindow.Eta(45));
        Assert.Equal(Loc.F("Eta_Min", 89), MainWindow.Eta(89 * 60));
        Assert.Equal(Loc.F("Eta_Hours", 1, 30), MainWindow.Eta(90 * 60));
    }
}
