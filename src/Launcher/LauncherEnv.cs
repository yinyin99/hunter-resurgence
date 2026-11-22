using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Resurgence.Services;
using Microsoft.Win32;

namespace Resurgence.Launcher;

/// <summary>What this PC says: settings.json, the player's saved choices, the detected game and the Steam user. One instance for the whole app.</summary>
public sealed class LauncherEnv
{
    public Settings Settings { get; init; } = Settings.Load();
    public string PrefsPath { get; }
    public Prefs Prefs { get; }
    public GameSearch Search { get; private set; } = new(null, null, []);
    public GameInstall? Install => Search.Install;
    public string? SteamPath { get; private set; }
    /// <summary>SteamID64 of the Steam user signed in on this PC, null when Steam is not running or nobody is signed in.</summary>
    public string? SteamId64 { get; private set; }
    public bool SaveFailed { get; private set; }
    /// <summary>The launcher's version (x.y.z, without the build metadata).</summary>
    public static string AppVersion { get; } =
        (typeof(LauncherEnv).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
    public string Version => AppVersion;

    public LauncherEnv(string? prefsPath = null)
    {
        PrefsPath = prefsPath ?? Prefs.DefaultPath;
        Prefs = Prefs.Load(PrefsPath);
        Detect();
    }

    public void Detect()
    {
        SteamPath = Steam.InstallPath();
        var account = Steam.ActiveAccount();
        SteamId64 = account == 0 ? null : Steam.SteamId64(account);
        Search = GameFinder.Search(Settings, Prefs.GameFolder, Environment.GetCommandLineArgs().Skip(1), SteamPath);
    }

    /// <summary>The PC's clock against the server's, measured by the install check's request (<see cref="Checks"/>).</summary>
    public ServerClock Clock { get; } = new();

    InstallChecks? checks;
    /// <summary>The install check of this PC (one instance: it remembers the server's build for a few minutes).</summary>
    public InstallChecks Checks => checks ??= InstallChecks.For(this);

    public GameInstallInfo GameInstallInfo => new(Install?.Folder, Search.Source, Search.Others, Search.Gone);

    /// <summary>The picked folder (the install root or the exe's folder) is saved as the manual folder and the game is found again; cancelled (null) changes nothing.</summary>
    public Result ChooseGameFolder(Func<string?> pick)
    {
        if (pick() is not { } picked) return Result.Success;
        if (GameFinder.FromDir(Settings, picked) is not { } g) return Result.Fail("Settings.FolderNoGame");
        Prefs.GameFolder = g.Folder;
        Save();
        Detect();
        return Result.Success;
    }

    /// <summary>Forgets the manual folder; detection goes back to Steam.</summary>
    public void UseAutomaticGameFolder()
    {
        Prefs.GameFolder = null;
        Save();
        Detect();
    }

    /// <summary>Folder of the client exe: the game's logs live next to it.</summary>
    public string? ClientFolder => Install is null ? null : Path.GetDirectoryName(Install.ClientExe);

    public void Save()
    {
        try { Prefs.Save(PrefsPath); SaveFailed = false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SaveFailed = true; }
    }

    /// <summary>The folder the player picked, or null when cancelled; whether it holds the game is up to the caller.</summary>
    public string? PickFolder()
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            var d = new OpenFolderDialog { InitialDirectory = Install?.Folder ?? "" };
            return d.ShowDialog(Application.Current.MainWindow) == true ? d.FolderName : null;
        });
    }

    public static string? PickZip()
    {
        var d = new OpenFileDialog { Filter = "Zip|*.zip", CheckFileExists = true };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null;
    }

    /// <summary>Open a folder in Explorer.</summary>
    public static void OpenFolder(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Open Explorer with a file selected; a file name cannot hold a quote, so the quoted path is safe.</summary>
    public static void ShowFile(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
}
