using Resurgence.Services.Mocks;

namespace Resurgence.Tests.ServicesTests;

public class MockAccountServiceTests
{
    /// <summary>The Settings time-zone list holds IANA ids (Linux is checked in the container by CI).</summary>
    [Fact]
    public async Task TimeZonesAreIanaIds()
    {
        var zones = (await new MockAccountService().LocaleChoicesAsync()).TimeZones;
        Assert.Contains("Europe/Paris", zones);
    }
}
