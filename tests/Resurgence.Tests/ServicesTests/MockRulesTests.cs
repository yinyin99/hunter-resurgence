using Resurgence.Services.Mocks;

namespace Resurgence.Tests.ServicesTests;

/// <summary>PL26: the rules the mocks keep (they are the website's behaviour without a server).</summary>
public class MockRulesTests
{
    [Fact]
    public async Task ANameThatStartsWithRaceIsTaken() => Assert.Equal("Signup.P1.Taken", (await new MockSessionService().SetHandleAsync("racer")).Error);

    [Fact]
    public async Task MessagingAnUnknownHunterFails() => Assert.Equal("Msg.NoUser", (await new MockMessagesService().SendAsync("nobody1", "x")).Error);

    [Fact]
    public async Task TurningOneAutomaticSettingOnTurnsTheOtherOff()
    {
        var missions = new MockMissionsService();
        await missions.SetAutomaticAsync("auto", true);
        await missions.SetAutomaticAsync("only", true);
        var r = await missions.RegularAsync();
        Assert.Equal((false, true), (r.Auto, r.OnlyFavourite));
    }

    [Fact]
    public async Task JoiningTwiceEnrolsOnceAndLeavingRemoves()
    {
        var c = new MockCompetitionsService();
        await c.JoinAsync(180100);
        await c.JoinAsync(180100);
        Assert.Equal(1, (await c.EnrolledAsync()).Count(e => e.Competition.Id == 180100));
        await c.LeaveAsync(180100);
        Assert.DoesNotContain(await c.EnrolledAsync(), e => e.Competition.Id == 180100);
    }

    [Fact]
    public async Task AnExpeditionIdOutsideTheHistoryShowsTheNearestEntry() => Assert.NotNull(await new MockProfileService().ExpeditionAsync("h", long.MaxValue));
}
