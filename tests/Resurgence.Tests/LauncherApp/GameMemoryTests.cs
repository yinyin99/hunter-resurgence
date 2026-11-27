using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Launcher;
using Resurgence.Services.Mocks;

namespace Resurgence.Tests.LauncherApp;

/// <summary>#974: the launcher logs the game's memory while it runs and once when it exits.</summary>
public class GameMemoryTests
{
    static readonly MockLauncherBridge Rest = new(NullLogger<MockLauncherBridge>.Instance);
    const long Mb = 1024 * 1024;

    [Fact]
    public void TheLineTellsWholeMegabytesInTheInvariantCultureAtTheExitOnlyThePeaks()
    {
        var m = new GameMemory(2345 * Mb + 5, 1800 * Mb, 2400 * Mb, 1850 * Mb);
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            Assert.Equal("Game memory: virtual 2345 MB, private 1800 MB, peak virtual 2400 MB, peak private 1850 MB", m.Line(false));
            Assert.Equal("Game memory at exit: peak virtual 2400 MB, peak private 1850 MB", m.Line(true));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void TheLineSurvivesTheLogsScrubAndLandsInTheFile()
    {
        using var temp = new TempDir("resurgence-test-");
        var log = new LauncherLog(Path.Combine(temp.Path, "launcher.log"));
        log.Write(LogLevel.Information, new GameMemory(2345 * Mb, 1800 * Mb, 2400 * Mb, 1850 * Mb).Line(false));
        Assert.EndsWith(" Information Game memory: virtual 2345 MB, private 1800 MB, peak virtual 2400 MB, peak private 1850 MB\n", File.ReadAllText(log.FilePath).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ARunningProcessIsReadAndAnExitedOneStillAnswersWithItsPeaks()
    {
        using var me = Process.GetCurrentProcess();
        var now = GameMemory.Read(me.Handle)!.Value;
        Assert.InRange(now.Virtual, me.VirtualMemorySize64 / 2, me.VirtualMemorySize64 * 2);
        Assert.InRange(now.Private, 1, now.PeakPrivate);
        Assert.True(now.PeakVirtual >= now.Virtual);

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        child.WaitForExit();
        var gone = GameMemory.Read(child.Handle)!.Value;
        Assert.True(gone.PeakVirtual > 0 && gone.PeakPrivate > 0);
    }

    /// <summary>A game that runs for <paramref name="ticks"/> memory readings and then exits (the exit code is known after them).</summary>
    static (LauncherBridge Bridge, GooseCallTests.Lines Log, Func<int> Reads) Watch(Func<GameMemory?> memory, int ticks, TimeSpan? every = null, Action? dispose = null)
    {
        var log = new GooseCallTests.Lines();
        var reads = 0;
        var polls = 0;
        var game = new GameProcess(() => true, () => reads > ticks || every is not null && ++polls > ticks ? 0 : null, dispose) { Memory = () => { reads++; return memory(); } };
        var b = new LauncherBridge(() => new("dir", "client.exe"), Rest, _ => true, _ => game, LauncherFakes.Signed, null, poll: TimeSpan.FromMilliseconds(1), find: _ => game, log: log, memoryEvery: every ?? TimeSpan.FromMilliseconds(5));
        return (b, log, () => reads);
    }

    [Fact]
    public async Task TheWatchLogsWhileTheGameRunsAndOnceMoreWhenItHasExited()
    {
        var (b, log, _) = Watch(() => new GameMemory(2 * Mb, Mb, 3 * Mb, 2 * Mb), 3);
        await b.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var lines = log.Seen.Where(l => l.StartsWith("Game memory")).ToList();
        Assert.True(lines.Count >= 2);
        Assert.All(lines[..^1], l => Assert.Equal("Game memory: virtual 2 MB, private 1 MB, peak virtual 3 MB, peak private 2 MB", l));
        Assert.Equal("Game memory at exit: peak virtual 3 MB, peak private 2 MB", lines[^1]);
        Assert.Single(log.Seen, l => l.StartsWith("Game exited"));
    }

    [Fact]
    public async Task TheLoggingStopsWithTheGame()
    {
        var (b, _, reads) = Watch(() => new GameMemory(Mb, Mb, Mb, Mb), 2);
        await b.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var after = reads();
        await Task.Delay(50);
        Assert.Equal(after, reads());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AProcessThatEndedBetweenTicksDoesNotThrowIntoTheWatchAndLogsNothing(int kind)
    {
        Exception error = kind switch { 0 => new InvalidOperationException("Process has exited"), 1 => new ObjectDisposedException("Process"), _ => new System.ComponentModel.Win32Exception(5) };
        var (b, log, _) = Watch(() => throw error, 3);
        Assert.NotNull(await b.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(log.Seen, l => l.StartsWith("Game memory"));
        Assert.Single(log.Seen, l => l.StartsWith("Game exited"));

        var (silent, quiet, _) = Watch(() => null, 3);
        Assert.NotNull(await silent.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(quiet.Seen, l => l.StartsWith("Game memory"));
    }

    [Fact]
    public async Task TheCadenceIs30SecondsAndNothingIsLoggedBeforeItIsUp()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), LauncherBridge.MemoryEvery);
        var (b, log, reads) = Watch(() => new GameMemory(Mb, Mb, Mb, Mb), 20, every: TimeSpan.FromHours(1));
        await b.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["Game memory at exit: peak virtual 1 MB, peak private 1 MB"], log.Seen.Where(l => l.StartsWith("Game memory")));  // 20 ticks, no running line
    }

    [Fact]
    public async Task TheExitLineIsReadBeforeTheProcessIsDisposed()
    {
        var disposed = false;
        var (b, log, _) = Watch(() => disposed ? null : new GameMemory(Mb, Mb, Mb, Mb), 2, every: TimeSpan.FromHours(1), dispose: () => disposed = true);
        await b.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Game memory at exit: peak virtual 1 MB, peak private 1 MB", log.Seen);
    }

    /// <summary>A copy of cmd.exe under a name of its own that waits a few seconds: a process <see cref="GameProcess"/> can look up by name.</summary>
    sealed class Probe : IDisposable
    {
        readonly string dir = Directory.CreateTempSubdirectory("resurgence-test-").FullName;
        public readonly string Exe, Name = "memprobe" + Environment.ProcessId + Random.Shared.Next(1000);
        public Probe()
        {
            Exe = Path.Combine(dir, Name + ".exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Exe);
        }
        public ProcessStartInfo Info => new(Exe, "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true };
        public void Dispose()
        {
            foreach (var p in Process.GetProcessesByName(Name)) { try { p.Kill(); p.WaitForExit(5000); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } p.Dispose(); }
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static void AssertReadsMemory(GameProcess game)
    {
        Assert.NotNull(game.Memory);
        var m = game.Memory!()!.Value;
        Assert.True(m.Virtual > 0 && m.Private > 0 && m.PeakVirtual >= m.Virtual);
    }

    [Fact]
    public void EveryFactoryOfTheRealGameProcessSetsTheMemoryReader()
    {
        using var probe = new Probe();
        // GameProcess.Start for an exe that is not the known one (Process.Start), and GameProcess.Find (the handle opened by id)
        using (var started = GameProcess.Start(probe.Info, null)!)
        {
            AssertReadsMemory(started);
            using var found = GameProcess.Find(probe.Exe)!;
            AssertReadsMemory(found);
        }
        // the suspended start of the known exe, whose handle the running game owns (SuspendedProcess.Started)
        var suspended = SuspendedProcess.Create(probe.Info)!;
        using var game = suspended.Started();
        AssertReadsMemory(game);
    }
}
