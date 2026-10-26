namespace Resurgence.Tests.LauncherApp;

/// <summary>The mock account service the signup screens run on: where the start goes for each kind of session, and the rules the screens rely on.</summary>
public class SignupTests
{
    [Fact]
    public async Task TheMockSessionAndSignupServicesFollowTheRulesOfTheScreens()
    {
        var s = new Resurgence.Services.Mocks.MockSessionService();
        await s.SignOutAsync();
        Assert.True(await s.StartAsync() == Resurgence.Services.StartOutcome.LoggedOut, "start without a session goes to the sign-in");
        var shortCode = await s.SignInAsync("a@b.test", "12");
        var noAt = await s.RequestCodeAsync("nope");
        Assert.True(shortCode.Error == "Validation.Code.Invalid" && noAt.Error == "Validation.Email.Invalid", "a short code and an address without @ are refused");
        await s.SignInAsync("a@b.test", "123456", register: true);
        Assert.True(await s.StartAsync() == Resurgence.Services.StartOutcome.NeedsSignup, "a new account without a username goes to the username page");
        await s.SetHandleAsync("Name");
        Assert.True(await s.StartAsync() == Resurgence.Services.StartOutcome.LoggedIn, "an account with a username is logged in");
        var u = new Resurgence.Services.Mocks.MockSignupService();
        var takenName = await u.UsernameAvailableAsync("Taken_1");
        var freeName = await u.UsernameAvailableAsync("Free");
        var takenEmail = await u.ChangeEmailAsync("taken@x.test");
        Assert.True(!takenName && freeName && takenEmail.Error == "Dialog.EmailTaken", "taken names and addresses are refused");
    }
}
