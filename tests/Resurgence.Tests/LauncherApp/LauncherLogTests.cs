using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

public class LauncherLogTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void BoomWithCancellation(CancellationToken cancellationToken) => throw new InvalidOperationException("with a parameter");

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Boom(string message) => throw new InvalidOperationException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void FaultATaskNobodyAwaits()
    {
        var task = Task.Run(() => Boom("task failure"));
        ((IAsyncResult)task).AsyncWaitHandle.WaitOne(); // waits without observing the exception
    }

    [Fact]
    public void AnExceptionOnTheUiThreadAndOneOnATaskAreLoggedWithTheirStack()
    {
        using var temp = new TempDir("resurgence-test-");
        var log = new LauncherLog(Path.Combine(temp.Path, "launcher.log"));
        var ui = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var watch = log.WatchUi(dispatcher);
            dispatcher.UnhandledException += (_, e) => e.Handled = true; // after the log's handler: the test's dispatcher goes on
            dispatcher.BeginInvoke(new Action(() => Boom("ui failure"))); // InvokeAsync keeps the exception in its operation instead of reporting it
            dispatcher.BeginInvoke(new Action(dispatcher.InvokeShutdown));
            Dispatcher.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();

        using (log.WatchProcess())
        {
            FaultATaskNobodyAwaits();
            // the exception is reported when the finalizer of the faulted task runs
            for (var i = 0; i < 50 && !File.ReadAllText(log.FilePath).Contains("task failure"); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(20);
            }
        }

        var text = File.ReadAllText(log.FilePath);
        Assert.Contains("Error Unhandled exception on the UI thread", text);
        Assert.Contains("ui failure", text);
        Assert.Contains("Error Unobserved exception of a task", text);
        Assert.Contains("task failure", text);
        Assert.Equal(2, text.Split("at Resurgence.Tests.LauncherApp.LauncherLogTests.Boom").Length - 1); // each with its stack
    }

    [Fact]
    public void OnlyTheLaunchersOwnClassesLogAndANewFileStartsWhenOneIsFull()
    {
        using var temp = new TempDir("resurgence-test-");
        var path = Path.Combine(temp.Path, "launcher.log");
        var log = new LauncherLog(path);
        log.CreateLogger("Resurgence.Launcher.MainWindow").LogWarning("WebView2 process failed: {Kind}, {Reason}, exit code {ExitCode}", "RenderProcessExited", "Crashed", 3);
        log.CreateLogger("Resurgence.Launcher.MainWindow").LogDebug("too detailed");
        log.CreateLogger("Resurgence.Services.ServerStoreService").LogWarning("another namespace");
        var text = File.ReadAllText(path);
        Assert.Contains("Warning WebView2 process failed: RenderProcessExited, Crashed, exit code 3", text);
        Assert.DoesNotContain("too detailed", text);
        Assert.DoesNotContain("another namespace", text);

        var line = new string('x', 1000);
        for (var i = 0; i < LauncherLog.MaxBytes / 1000 + 10; i++) log.Write(LogLevel.Information, line);
        log.Write(LogLevel.Information, "newest");
        Assert.True(File.Exists(LauncherLog.OlderOf(path)));
        Assert.Contains("WebView2 process failed", File.ReadAllText(LauncherLog.OlderOf(path)));
        Assert.Contains("newest", File.ReadAllText(path));
        Assert.InRange(new FileInfo(path).Length, 1, LauncherLog.MaxBytes);
    }

    [Fact]
    public void AnEntryIsScrubbedBeforeItIsWrittenAndKeepsItsStackFrames()
    {
        using var temp = new TempDir("resurgence-test-");
        var path = Path.Combine(temp.Path, "launcher.log");
        var log = new LauncherLog(path);
        Exception? caught = null;
        try { Boom(@"cannot open C:\Users\Jane Doe\AppData\x.cfg"); }
        catch (Exception e) { caught = e; }
        log.Write(LogLevel.Warning, "sign-in failed, token abcdef was sent", caught);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("token abcdef", text); // a credential line is replaced as a whole
        Assert.Contains("[line removed: it may hold a credential]", text);
        Assert.DoesNotContain("Jane Doe", text);
        Assert.Contains(@"C:\Users\<user>\AppData\x.cfg", text);
        Assert.Contains("at Resurgence.Tests.LauncherApp.LauncherLogTests.Boom", text); // the frame stays
        // a frame with a CancellationToken parameter stays in the log and in the report (the word is written Tk there); the line scrub itself stays strict
        try { BoomWithCancellation(CancellationToken.None); }
        catch (Exception e) { log.Write(LogLevel.Error, "with a parameter", e); }
        Assert.Contains("BoomWithCancellation(CancellationTk cancellationTk)", File.ReadAllText(path));
        var zip = new Diagnostics(() => null, [], "1", () => [], Path.Combine(temp.Path, "out"), () => [], () => [], () => [], _ => [], launcherLog: path).Build("manual").Path;
        using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
            Assert.Contains("BoomWithCancellation(CancellationTk cancellationTk)", new StreamReader(z.GetEntry("launcher.log")!.Open()).ReadToEnd());
        Assert.Equal("[line removed: it may hold a credential]", Diagnostics.Scrub("   at X.Y() in C:\\x token=abc"));
        Assert.Equal("[line removed: it may hold a credential]", Diagnostics.Scrub("   at X.Y(string token)"));
    }

    [Fact]
    public void AStackTextThatACallerSuppliedIsNotWrittenAndTheZipScrubsBothFilesStrictly()
    {
        using var temp = new TempDir("resurgence-test-");
        var path = Path.Combine(temp.Path, "launcher.log");
        var log = new LauncherLog(path);
        var remote = new InvalidOperationException("remote failure");
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.SetRemoteStackTrace(remote, "   at Evil.Method() token=fictional-secret");
        try { throw remote; }
        catch (Exception e) { log.Write(LogLevel.Error, "remote", e); }
        var text = File.ReadAllText(path);
        Assert.Contains("remote failure", text);
        Assert.DoesNotContain("fictional-secret", text);

        // lines that are in either file when the zip is built (written by something else, or by an older version) go through the strict line scrub
        File.AppendAllText(path, "   at Other.Method() Authorization: fictional-bearer\n");
        File.WriteAllText(LauncherLog.OlderOf(path), "older line\n   at Other.Method() cookie=fictional-cookie\n");
        var zip = new Diagnostics(() => null, [], "1", () => [], Path.Combine(temp.Path, "out"), () => [], () => [], () => [], _ => [], launcherLog: path).Build("manual").Path;
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        var both = new StreamReader(z.GetEntry("launcher.log")!.Open()).ReadToEnd() + new StreamReader(z.GetEntry("launcher.1.log")!.Open()).ReadToEnd();
        Assert.Contains("older line", both);
        Assert.DoesNotContain("fictional", both);
    }

    [Fact]
    public void OneHugeEntryIsCutAndNeitherFileGrowsPastTheLimit()
    {
        using var temp = new TempDir("resurgence-test-");
        var path = Path.Combine(temp.Path, "launcher.log");
        var log = new LauncherLog(path);
        for (var i = 0; i < 40; i++) log.Write(LogLevel.Error, new string('y', 3 * 1024 * 1024)); // entries of 3 MiB each, cut to 64 K characters: the file rotates and neither grows past the limit
        log.Write(LogLevel.Information, "after");
        foreach (var f in new[] { path, LauncherLog.OlderOf(path) }) Assert.InRange(new FileInfo(f).Length, 1, LauncherLog.MaxBytes);
        Assert.Contains("[cut]", File.ReadAllText(path));
    }

    [Fact]
    public void ACredentialThatCrossesTheCutIsRemovedNotSplit()
    {
        using var temp = new TempDir("resurgence-test-");
        var path = Path.Combine(temp.Path, "launcher.log");
        new LauncherLog(path).Write(LogLevel.Information, new string('x', LauncherLog.MaxEntryChars - 50) + " 0123456789abcdef0123456789abcdef0123"); // a fictional session id, cut by the limit if clipped first
        Assert.DoesNotContain("0123456789", File.ReadAllText(path));
    }
}
