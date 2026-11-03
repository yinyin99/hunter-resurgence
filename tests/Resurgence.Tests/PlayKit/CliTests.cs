using Play;

namespace Resurgence.Tests.PlayKit;

public class CliTests
{
    [Fact]
    public async Task AnUnknownCommandPrintsTheUsageAndFails() => Assert.Equal(2, await Cli.Run(["nonsense"]));

    // only exactly `hosts on|off` may reach the elevated hosts edit; every other shape ends in the usage (these would touch the PC if the guard let them through)
    [Theory]
    [InlineData]
    [InlineData("setup", "--x")]
    [InlineData("setup", "--remove", "x")]
    [InlineData("run", "x")]
    [InlineData("hosts")]
    [InlineData("hosts", "sideways")]
    [InlineData("hosts", "on", "off")]
    public async Task EveryOtherShapeOfTheCommandLineIsTheUsage(params string[] args) => Assert.Equal(2, await Cli.Run(args));
}
