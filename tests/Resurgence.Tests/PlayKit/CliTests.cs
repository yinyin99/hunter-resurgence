using Play;

namespace Resurgence.Tests.PlayKit;

public class CliTests
{
    [Theory]
    [InlineData("2026-11-10T23:59:59Z", false, true)]   // the last second of the official servers
    [InlineData("2026-11-10T23:59:59Z", true, false)]   // forced
    [InlineData("2026-11-11T00:00:00Z", false, false)]  // the day they close
    [InlineData("2027-01-01T00:00:00Z", false, false)]
    public void SetupRefusesBeforeTheShutdownUnlessForced(string now, bool force, bool refused) =>
        Assert.Equal(refused, Cli.Refuses(DateTimeOffset.Parse(now), force));

    [Fact]
    public async Task AnUnknownCommandPrintsTheUsageAndFails() => Assert.Equal(2, await Cli.Run(["nonsense"]));
}
