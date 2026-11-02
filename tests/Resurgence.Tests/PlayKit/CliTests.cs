using Play;

namespace Resurgence.Tests.PlayKit;

public class CliTests
{
    [Fact]
    public async Task AnUnknownCommandPrintsTheUsageAndFails() => Assert.Equal(2, await Cli.Run(["nonsense"]));
}
