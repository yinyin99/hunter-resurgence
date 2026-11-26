using Microsoft.Web.WebView2.Core;
using Resurgence.Launcher;
using static Resurgence.Launcher.WebViewStart;

namespace Resurgence.Tests.LauncherApp;

public class WebViewStartTests
{
    [Fact]
    public void ARuntimeThatIsNotThereIsMissingWhetherTheCallThrowsOrAnswersNothing()
    {
        Assert.True(RuntimeMissing(() => throw new WebView2RuntimeNotFoundException()));
        Assert.True(RuntimeMissing(() => null));
        Assert.True(RuntimeMissing(() => ""));
        Assert.False(RuntimeMissing(() => "130.0.2849.80"));
    }

    [Fact]
    public void OnlyTheMissingRuntimeIsToldApartFromAFailedStart()
    {
        Assert.Equal(Problem.RuntimeMissing, Classify(new WebView2RuntimeNotFoundException()));
        Assert.Equal(Problem.StartFailed, Classify(new InvalidOperationException()));
        Assert.Equal(Problem.StartFailed, Classify(null));
        Assert.Equal(Problem.StartFailed, Classify(new System.Runtime.InteropServices.COMException("x", -2147467259)));  // E_FAIL
    }

    [Fact]
    public void AnOlderBrowserStillHoldingTheUserDataIsBusyNeverMissing()
    {
        Assert.Equal(5023, InvalidState & 0xFFFF);  // ERROR_INVALID_STATE
        Assert.Equal(Problem.Busy, Classify(new System.Runtime.InteropServices.COMException("x", InvalidState)));
        Assert.Equal(Problem.Busy, Classify(new InvalidOperationException("wrapped", new System.Runtime.InteropServices.COMException("x", InvalidState))));
        Assert.Equal(Notice.Last, Decide(Problem.Busy, false));
        Assert.Equal(Notice.Last, Decide(Problem.Busy, true));
        foreach (var yes in new[] { true, false }) Assert.Equal(Next.Wait, Answer(Notice.Last, yes));
    }

    [Theory]
    [InlineData(Problem.RuntimeMissing, false, Notice.Install)]
    [InlineData(Problem.RuntimeMissing, true, Notice.Install)]
    [InlineData(Problem.StartFailed, false, Notice.RestartOrClose)]
    [InlineData(Problem.StartFailed, true, Notice.Install)] // restarting without hardware acceleration was tried: the Runtime itself is broken
    [InlineData(Problem.NotLoaded, false, Notice.RestartOrWait)]
    [InlineData(Problem.NotLoaded, true, Notice.Last)] // no second restart: no loop
    [InlineData(Problem.Busy, false, Notice.Last)] // built once more already: the plain final message, never the download page
    [InlineData(Problem.Busy, true, Notice.Last)]
    public void TheMessageDependsOnTheProblemAndOnWhetherHardwareAccelerationIsAlreadyOff(Problem problem, bool gpuOff, Notice expected) =>
        Assert.Equal(expected, Decide(problem, gpuOff));

    [Fact]
    public void OnlyTheRestartAnswersRestartAndNothingRestartsWithoutHardwareAccelerationTwice()
    {
        Assert.Equal(Next.Restart, Answer(Notice.RestartOrClose, true));
        Assert.Equal(Next.Close, Answer(Notice.RestartOrClose, false));
        Assert.Equal(Next.Restart, Answer(Notice.RestartOrWait, true));
        Assert.Equal(Next.Wait, Answer(Notice.RestartOrWait, false));
        Assert.Equal(Next.Close, Answer(Notice.Install, true));
        Assert.Equal(Next.Close, Answer(Notice.Install, false));
        Assert.Equal(Next.Wait, Answer(Notice.Last, true));
        foreach (var problem in Enum.GetValues<Problem>())
            foreach (var yes in new[] { true, false })
                Assert.NotEqual(Next.Restart, Answer(Decide(problem, true), yes));
    }

    [Theory]
    [InlineData("--wait-for=1234", 1234)]
    [InlineData("--wait-for=x", null)]
    [InlineData("--wait-for=", null)]
    [InlineData("--route=/store", null)]
    public void TheProcessToWaitForIsReadFromTheArguments(string argument, int? expected) =>
        Assert.Equal(expected, ParseWaitFor(["--route=/store", argument]));

    [Fact]
    public void ARestartKeepsTheArgumentsAndNamesOnlyTheCurrentProcess()
    {
        Assert.Equal(["--route=/store", "hunter-resurgence://open", "--wait-for=42"], RestartArgs(["--route=/store", "--wait-for=7", "hunter-resurgence://open"], 42));
        Assert.Equal(["--route=/store"], ForwardedArgs(["--wait-for=7", "--route=/store"]));
        Assert.Equal(["--wait-for=42"], RestartArgs([], 42));
    }

    [Fact]
    public void EveryMessageHasItsText()
    {
        foreach (var notice in Enum.GetValues<Notice>())
            Assert.False(string.IsNullOrWhiteSpace(Kit.S(Key(notice))), notice.ToString());
    }
}
