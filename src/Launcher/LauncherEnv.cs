using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace Resurgence.Launcher;

/// <summary>What this PC says: settings.json, the player's saved choices, the detected game and the Steam user. One instance for the whole app.</summary>
public sealed class LauncherEnv
{
    public Settings Settings { get; } = Settings.Load();
    public Prefs Prefs { get; } = Prefs.Load(Prefs.DefaultPath);
    public GameInstall? Install { get; private set; }
    public string? SteamPath { get; private set; }
    /// <summary>SteamID64 of the Steam user signed in on this PC, null when Steam is not running or nobody is signed in.</summary>
    public string? SteamId64 { get; private set; }
    public bool SaveFailed { get; private set; }
    public string Version { get; } =
        (typeof(LauncherEnv).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    uint account;

    public LauncherEnv() => Detect();

    /// <summary>The hand-chosen folder is in use (it still contains the client).</summary>
    public bool ChosenFolderInUse => GameFinder.Check(Settings, Prefs.GameFolder) is not null;

    public void Detect()
    {
        SteamPath = Steam.InstallPath();
        account = Steam.ActiveAccount();
        SteamId64 = account == 0 ? null : Steam.SteamId64(account);
        Install = GameFinder.Find(Settings, Prefs.GameFolder, Environment.GetCommandLineArgs().Skip(1), SteamPath);
    }

    /// <summary>Folder of the client exe: the game's logs live next to it.</summary>
    public string? ClientFolder => Install is null ? null : Path.GetDirectoryName(Install.ClientExe);

    /// <summary>Steam's own screenshot folder of this game for the signed-in user (generic Steam layout).</summary>
    public string? ScreenshotsFolder => SteamPath is null || account == 0 || Settings.SteamAppId is null ? null
        : Path.Combine(SteamPath, "userdata", account.ToString(), "760", "remote", Settings.SteamAppId, "screenshots");

    public void Save()
    {
        try { Prefs.Save(Prefs.DefaultPath); SaveFailed = false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SaveFailed = true; }
    }

    /// <summary>A folder with the client exe in it, or null (cancelled / wrong folder is up to the caller to tell apart).</summary>
    public string? PickFolder()
    {
        var d = new OpenFolderDialog { InitialDirectory = Install?.Folder ?? "" };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FolderName : null;
    }

    public static string? PickZip()
    {
        var d = new OpenFileDialog { Filter = "Zip|*.zip", CheckFileExists = true };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null;
    }

    /// <summary>Open a folder in Explorer; does nothing when it does not exist.</summary>
    public static void OpenFolder(string? path)
    {
        if (path is not null && Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
