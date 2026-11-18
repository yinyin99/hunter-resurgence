using Resurgence.Services;
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
    [Fact]
    public async Task ATaxidermyIsPaidOnceFromTheWalletAndShowsItsBadge()
    {
        var wallet = new MockWalletState();
        var profile = new MockProfileService(null, null, wallet);
        const long kill = 100000201;  // the own hunt 1 back: allowed, with a skull
        Assert.Equal("Taxidermy.NotEnoughGms", (await profile.TaxidermizeAsync(kill, "taxidermy", "gms")).Error);  // 1,000 gm$ against 2,000
        Assert.Equal(new TaxidermyDone("skull", 90, 1000), (await profile.TaxidermizeAsync(kill, "skull", "ems")).Value);
        Assert.Equal("Taxidermy.Already", (await profile.TaxidermizeAsync(kill, "taxidermy", "ems")).Error);
        Assert.Equal((90, "skull"), (wallet.Ems, (await profile.ExpeditionAsync("Hunter01", 100000099))!.Kills.Single().Taxidermy!.Done));
    }

    [Fact]
    public async Task OnlyTheOwnHuntsOfTheLast72HoursOfferATaxidermy()
    {
        var profile = new MockProfileService();
        foreach (var id in new long[] { 100000099, 100000098, 100000097, 100000096 }) Assert.NotNull((await profile.ExpeditionAsync("Hunter01", id))!.Kills.Single().Taxidermy);
        Assert.Null((await profile.ExpeditionAsync("Hunter01", 100000094))!.Kills.Single().Taxidermy);
        Assert.Null((await profile.ExpeditionAsync("Hunter05", 100000099))!.Kills.Single().Taxidermy);
        Assert.Equal("Taxidermy.TooLate", (await profile.TaxidermizeAsync(100000205, "taxidermy", "ems")).Error);
    }
}
