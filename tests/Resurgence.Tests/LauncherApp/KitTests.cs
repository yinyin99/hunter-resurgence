using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

public class KitTests
{
    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public void HostsOnAndOffAreTheElevatedCopy(string word, bool on) => Assert.Equal(on, Kit.HostsArgument(["--hosts", word]));

    // only exactly `--hosts on|off` may reach the elevated hosts edit; every other command line (Steam passes the game's path, Velopack its own flags) starts the launcher
    [Theory]
    [InlineData]
    [InlineData("--hosts")]
    [InlineData("--hosts", "sideways")]
    [InlineData("--hosts", "on", "off")]
    [InlineData("hosts", "on")]
    [InlineData("--HOSTS", "on")]
    [InlineData("--route=/store")]
    [InlineData("C:\\game\\client.exe")]
    public void EveryOtherCommandLineIsNotTheHostsEdit(params string[] args) => Assert.Null(Kit.HostsArgument(args));
}
