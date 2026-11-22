using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

/// <summary>#810: the borderless display mode keeps the game's window borderless while it runs; a fake stands in for the window.</summary>
public class BorderlessTests
{
    sealed class FakeWindow : IGameWindow
    {
        public nint Window; // 0 until the game's window appears
        public bool Frame = true;
        public int Unframed, Pid;
        public nint Find(int pid) { Pid = pid; return Window; }
        public bool Framed(nint window) => window == Window && Frame;
        public void Unframe(nint window) { Unframed++; Frame = false; }
    }

    [Fact]
    public async Task TheWindowIsMadeBorderlessWhenItAppearsAndAgainWhenItGetsItsFrameBack()
    {
        var w = new FakeWindow();
        var checks = 0;
        // one answer per check: the game runs without a window, its window appears, it stays borderless, the game puts its frame back, then it exits
        await Borderless.KeepAsync(w, 42, () => ++checks switch
        {
            1 => true,
            2 => (w.Window = 7) != 0,
            3 => w.Unframed == 1,
            4 => w.Unframed == 1 && (w.Frame = true),
            5 => w.Unframed == 2,
            _ => false,
        }, TimeSpan.Zero);
        Assert.Equal((2, 42, 6), (w.Unframed, w.Pid, checks));
    }
}
