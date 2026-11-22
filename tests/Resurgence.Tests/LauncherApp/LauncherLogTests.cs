using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

public class LauncherLogTests
{
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
            using var watch = log.Watch(dispatcher);
            dispatcher.UnhandledException += (_, e) => e.Handled = true; // after the log's handler: the test's dispatcher goes on
            dispatcher.BeginInvoke(new Action(() => Boom("ui failure"))); // InvokeAsync keeps the exception in its operation instead of reporting it
            dispatcher.BeginInvoke(new Action(dispatcher.InvokeShutdown));
            Dispatcher.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();

        using (log.Watch(Dispatcher.CurrentDispatcher))
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
}
