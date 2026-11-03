using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

public class SteamTests
{
    // the account id is the low 32 bits of the SteamID64
    [Fact]
    public void SteamId64IsTheAccountAddedToTheIndividualBase() => Assert.Equal("76561197960265729", Steam.SteamId64(1));
}
