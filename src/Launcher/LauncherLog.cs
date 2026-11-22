using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Resurgence.Launcher;

/// <summary>
/// The launcher's own log, <c>launcher.log</c> next to launcher.json (docs/launcher.md, "The launcher's log"): what the launcher did and the errors it did not handle, one entry per write, read by the problem report.
/// It is also the launcher's <see cref="ILoggerProvider"/>: what the launcher's own classes (the <c>Resurgence.Launcher</c> namespace) log at Information or above lands here, nothing else does.
/// A file that an entry would take over <see cref="MaxBytes"/> becomes <c>launcher.1.log</c> (the older one is dropped), so the launcher keeps at most about 2 MiB; one entry is cut at <see cref="MaxEntryChars"/> characters.
/// Every entry goes through the report's scrub (<see cref="Diagnostics.Scrub(string)"/>) before it is written: no credential line and no Windows user name reaches the disk. A log that cannot be written is silently skipped: it never takes the launcher down.
/// </summary>
public sealed class LauncherLog(string path) : ILoggerProvider
{
    public const long MaxBytes = 1024 * 1024;
    public const int MaxEntryChars = 64 * 1024;

    public static LauncherLog Default { get; } = new(Path.Combine(Path.GetDirectoryName(Prefs.DefaultPath)!, "launcher.log"));

    public string FilePath => path;

    /// <summary>The file the log rotates to: <c>launcher.log</c> becomes <c>launcher.1.log</c>.</summary>
    public static string OlderOf(string path) => Path.ChangeExtension(path, ".1.log");

    readonly object gate = new();

    /// <summary>
    /// Appends one entry (a time in UTC, the level, the text, then the exceptions with their stacks); the file is closed again at once, so the report can read it while the launcher runs. The text and the exception messages go through the
    /// report's line scrub (<see cref="Diagnostics.Scrub(string)"/>); the stack frames are written from the frames' own metadata (type, method, parameters, file, line; see <see cref="Frames"/>), never from the exception's text, and lose the Windows
    /// user name. The cut at <see cref="MaxEntryChars"/> comes after the scrub: a credential is never cut in two.
    /// </summary>
    public void Write(LogLevel level, string text, Exception? error = null)
    {
        var entry = Diagnostics.Scrub($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {level} {text}") + (error is null ? "" : "\n" + Describe(error));
        if (entry.Length > MaxEntryChars) entry = entry[..MaxEntryChars] + "\n[cut]";
        var bytes = Encoding.UTF8.GetBytes(entry + "\n");
        lock (gate)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (new FileInfo(path) is { Exists: true, Length: > 0 } f && f.Length + bytes.Length > MaxBytes) File.Move(path, OlderOf(path), true);
                using var o = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                o.Write(bytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>What <c>Exception.ToString()</c> says, with the messages scrubbed as free text: the type and message of the exception, its frames, then each inner exception.</summary>
    static string Describe(Exception error)
    {
        var sb = new StringBuilder();
        void Add(Exception e, string label)
        {
            sb.Append(label).Append(e.GetType().FullName).Append(": ").Append(Diagnostics.Scrub(e.Message)).Append('\n').Append(Frames(e));
            if (e is AggregateException all) foreach (var inner in all.InnerExceptions) Add(inner, "caused by ");
            else if (e.InnerException is { } one) Add(one, "caused by ");
        }
        Add(error, "");
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// The frames of the stack the runtime captured, as <c>   at Type.Method(ParameterType name) in file:line n</c>, built from each frame's metadata. <c>Exception.StackTrace</c> is text (a type may override it and a caller may set a remote stack
    /// text), which could hold a credential, so it is not used; code names and parameter types carry none, so a method with a Token parameter stays.
    /// </summary>
    static string Frames(Exception e)
    {
        var sb = new StringBuilder();
        foreach (var f in new StackTrace(e, true).GetFrames() ?? [])
            if (f.GetMethod() is { } m)
            {
                sb.Append("   at ").Append(m.DeclaringType?.FullName).Append('.').Append(m.Name).Append('(').AppendJoin(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")).Append(')');
                if (f.GetFileName() is { } file) sb.Append(" in ").Append(file).Append(":line ").Append(f.GetFileLineNumber());
                sb.Append('\n');
            }
        return Diagnostics.ScrubNames(sb.ToString());
    }

    /// <summary>
    /// Logs the exceptions that no thread handled, with their stack: any thread's (<c>AppDomain.UnhandledException</c>, after which the launcher closes) and a task's that nobody awaited
    /// (<c>TaskScheduler.UnobservedTaskException</c>). It only logs: the launcher closes or goes on as it would without. Started first thing in <c>Main</c>, so that a failure in the start-up is in the log too.
    /// </summary>
    /// <returns>Stops the logging (only a test does).</returns>
    public IDisposable WatchProcess()
    {
        UnhandledExceptionEventHandler domain = (_, e) => Write(LogLevel.Critical, $"Unhandled exception{(e.IsTerminating ? ", the launcher is closing" : "")}", e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()));
        EventHandler<UnobservedTaskExceptionEventArgs> task = (_, e) => Write(LogLevel.Error, "Unobserved exception of a task", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += domain;
        TaskScheduler.UnobservedTaskException += task;
        return new Stop(() =>
        {
            AppDomain.CurrentDomain.UnhandledException -= domain;
            TaskScheduler.UnobservedTaskException -= task;
        });
    }

    /// <summary>Logs the exceptions of the UI thread that nothing handled: the dispatcher's <c>UnhandledException</c>, the event <c>Application.DispatcherUnhandledException</c> raises. It only logs.</summary>
    public IDisposable WatchUi(Dispatcher dispatcher)
    {
        DispatcherUnhandledExceptionEventHandler ui = (_, e) => Write(LogLevel.Error, "Unhandled exception on the UI thread", e.Exception);
        dispatcher.UnhandledException += ui;
        return new Stop(() => dispatcher.UnhandledException -= ui);
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
