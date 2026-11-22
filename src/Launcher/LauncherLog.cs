using System.Text;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Resurgence.Launcher;

/// <summary>
/// The launcher's own log, <c>launcher.log</c> next to launcher.json (docs/launcher.md, "The launcher's log"): what the launcher did and the errors it did not handle, one entry per write, read by the problem report.
/// It is also the launcher's <see cref="ILoggerProvider"/>: what the launcher's own classes (the <c>Resurgence.Launcher</c> namespace) log at Information or above lands here, nothing else does.
/// A file over <see cref="MaxBytes"/> becomes <c>launcher.1.log</c> (the older one is dropped), so the launcher keeps at most about 2 MiB. A log that cannot be written is silently skipped: it never takes the launcher down.
/// </summary>
public sealed class LauncherLog(string path) : ILoggerProvider
{
    public const long MaxBytes = 1024 * 1024;

    public static LauncherLog Default { get; } = new(Path.Combine(Path.GetDirectoryName(Prefs.DefaultPath)!, "launcher.log"));

    public string FilePath => path;

    /// <summary>The file the log rotates to: <c>launcher.log</c> becomes <c>launcher.1.log</c>.</summary>
    public static string OlderOf(string path) => Path.ChangeExtension(path, ".1.log");

    readonly object gate = new();

    /// <summary>Appends one entry (a time in UTC, the level, the text, then the exception with its stack and inner exceptions); the file is closed again at once, so the report can read it while the launcher runs.</summary>
    public void Write(LogLevel level, string text, Exception? error = null)
    {
        var entry = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {level} {text}{(error is null ? "" : "\n" + error)}\n";
        lock (gate)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (new FileInfo(path) is { Exists: true, Length: >= MaxBytes }) File.Move(path, OlderOf(path), true);
                using var f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                f.Write(Encoding.UTF8.GetBytes(entry));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Logs the exceptions nothing handled, with their stack: the UI thread's (the dispatcher's <c>UnhandledException</c>, the event <c>Application.DispatcherUnhandledException</c> raises), any thread's
    /// (<c>AppDomain.UnhandledException</c>, after which the launcher closes) and a task's that nobody awaited (<c>TaskScheduler.UnobservedTaskException</c>). It only logs: the launcher closes or goes on as it would without.
    /// </summary>
    /// <returns>Stops the logging (only a test does).</returns>
    public IDisposable Watch(Dispatcher dispatcher)
    {
        DispatcherUnhandledExceptionEventHandler ui = (_, e) => Write(LogLevel.Error, "Unhandled exception on the UI thread", e.Exception);
        UnhandledExceptionEventHandler domain = (_, e) => Write(LogLevel.Critical, $"Unhandled exception{(e.IsTerminating ? ", the launcher is closing" : "")}", e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()));
        EventHandler<UnobservedTaskExceptionEventArgs> task = (_, e) => Write(LogLevel.Error, "Unobserved exception of a task", e.Exception);
        dispatcher.UnhandledException += ui;
        AppDomain.CurrentDomain.UnhandledException += domain;
        TaskScheduler.UnobservedTaskException += task;
        return new Stop(() =>
        {
            dispatcher.UnhandledException -= ui;
            AppDomain.CurrentDomain.UnhandledException -= domain;
            TaskScheduler.UnobservedTaskException -= task;
        });
    }

    sealed class Stop(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }

    public ILogger CreateLogger(string category) => category.StartsWith("Resurgence.Launcher", StringComparison.Ordinal) ? new Logger(this) : NullLogger.Instance;

    public void Dispose() { }

    sealed class Logger(LauncherLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level is >= LogLevel.Information and not LogLevel.None;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level)) log.Write(level, formatter(state, error), error);
        }
    }
}
