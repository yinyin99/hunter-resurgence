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
    public string Version { get; } =
        (typeof(LauncherEnv).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

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

    public GameInstallInfo GameInstallInfo => new(Install?.Folder, Search.Source, Search.Others);

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
        // the bridge is called off the window's thread: the dialog belongs to it
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
}
