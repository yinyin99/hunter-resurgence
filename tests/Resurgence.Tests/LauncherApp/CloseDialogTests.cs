using System.Globalization;
using System.Diagnostics;
using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

public class CloseDialogTests
{
    [Fact]
    public void TheDecisionFollowsTheGameTheShutdownAndTheAnswer()
    {
        Func<bool> no = () => false, yes = () => true;
        Func<bool> unasked = () => throw new InvalidOperationException("nothing to ask");
        Assert.Equal(MainWindow.CloseChoice.Exit, MainWindow.Decide(false, no, unasked));
        Assert.Equal(MainWindow.CloseChoice.Exit, MainWindow.Decide(true, no, unasked));
        Assert.Equal(MainWindow.CloseChoice.Exit, MainWindow.Decide(true, yes, unasked)); // a shutdown or sign-out is never held up
        Assert.Equal(MainWindow.CloseChoice.CloseGame, MainWindow.Decide(false, yes, yes));
        Assert.Equal(MainWindow.CloseChoice.Minimize, MainWindow.Decide(false, yes, no));
    }

    [Fact]
    public void YesCountsDownFromThreeAndIsNotEnabledBeforeThreeSeconds()
    {
        var now = TimeSpan.Zero;
        Sta(() =>
        {
            var d = new CloseDialog(() => now);
            Assert.False(d.YesEnabled);
            Assert.Equal("Yes (3)", d.YesLabel);

            foreach (var (ms, label) in new[] { (900, "Yes (3)"), (1000, "Yes (2)"), (1999, "Yes (2)"), (2000, "Yes (1)"), (2999, "Yes (1)") })
            {
                now = TimeSpan.FromMilliseconds(ms);
                d.Tick();
                Assert.False(d.YesEnabled);
                Assert.Equal(label, d.YesLabel);
            }

            now = CloseDialog.Delay;
            d.Tick();
            Assert.True(d.YesEnabled);
            Assert.Equal("Yes", d.YesLabel);
        });
    }

    [Fact]
    public async Task AGameThatStaysIsStoppedAfterTheGraceTime()
    {
        // a console program has no window to close, so only the stop ends it
        var p = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { CreateNoWindow = true })!;
        var id = p.Id;
        await GameFiles.StopAsync([p], TimeSpan.FromMilliseconds(300));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(id)); // gone
    }

    static void Sta(Action body)
    {
        Exception? failed = null;
        var t = new Thread(() => { try { Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en"); body(); } catch (Exception e) { failed = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failed is not null) throw new Exception("on the STA thread", failed);
    }
}
