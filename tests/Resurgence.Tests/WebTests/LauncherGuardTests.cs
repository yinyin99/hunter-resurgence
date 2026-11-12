using Resurgence.Ui;

namespace Resurgence.Tests.WebTests;

public class LauncherGuardTests
{
    [Theory]
    [InlineData("dashboard", true)]
    [InlineData("/launch-map/1?x=1", true)]
    [InlineData("Servers/", true)]
    [InlineData("tutorials#a", true)]
    [InlineData("dashboardx", false)]
    [InlineData("feed", false)]
    [InlineData("", false)]
    public void BlocksTheLauncherOnlyRoutes(string path, bool blocked) => Assert.Equal(blocked, LauncherGuard.Blocks(path));

    [Theory]
    [InlineData("wallet/ems", true)]
    [InlineData("/Messages", true)]
    [InlineData("new-message", true)]
    [InlineData("bank/ems", true)]
    [InlineData("walletx", false)]
    [InlineData("store", false)]
    [InlineData("profile/x", false)]
    public void NeedsASessionForTheSignedInRoutes(string path, bool needs) => Assert.Equal(needs, LauncherGuard.NeedsSession(path));

    [Theory]
    [InlineData("community_league", true)]
    [InlineData("community_league/details/3", true)]
    [InlineData("steam_callback/abc", true)]
    [InlineData("wallet/ems", false)]                // the server's: the owner's history
    [InlineData("/Messages", false)]                 // the server's: the restored messages, read only
    [InlineData("new-message", true)]
    [InlineData("bank/ems", true)]
    [InlineData("inventory", false)]                 // the server's: the owner's items
    [InlineData("missions", false)]                  // the server's: the owner's missions
    [InlineData("/Missions/group/3", false)]
    [InlineData("missions/daily", false)]            // the server's: the owner's streak
    [InlineData("competitions", true)]               // no source on the live site: the recorded ones ended weeks ago
    [InlineData("competitions/details/3/history", true)]
    [InlineData("restore", false)]
    [InlineData("store", false)]
    [InlineData("profile/x", false)]
    [InlineData("leaderboards", false)]
    public void TheMockOnlyRoutesAreTheSignedInOnesExceptTheServersAndTheCompetitions(string path, bool mock) => Assert.Equal(mock, LauncherGuard.MockOnly(path));
}
