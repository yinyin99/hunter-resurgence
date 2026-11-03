using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Pages.Browser;

namespace Resurgence.Tests.WebTests;

public sealed class LogoutPageTests : UiContext
{
    sealed class FakeSessions(string? signInUrl) : ISessionService
    {
        readonly MockSessionService inner = new();
        public int SignOuts;
        public event Action? Changed { add { } remove { } }
        public string? SignInUrl(string next) => signInUrl;
        public Task<Session?> CurrentAsync() => inner.CurrentAsync();
        public Task<Result> RequestCodeAsync(string email) => inner.RequestCodeAsync(email);
        public Task<Result> SignInAsync(string email, string code, bool register = false) => inner.SignInAsync(email, code, register);
        public Task<Result> SetHandleAsync(string handle) => inner.SetHandleAsync(handle);
        public Task<StartOutcome> StartAsync() => inner.StartAsync();
        public Task SignOutAsync() { SignOuts++; return Task.CompletedTask; }
        public Task<bool> LicenseAcceptedAsync() => inner.LicenseAcceptedAsync();
        public Task AcceptLicenseAsync() => inner.AcceptLicenseAsync();
    }

    [Fact]
    public void OpeningTheAddressDoesNotSignOutOnTheCommunityServerButTheButtonDoes()
    {
        var s = new FakeSessions("/auth/login");
        Services.AddSingleton<ISessionService>(s);
        var cut = Render<Logout>();
        Assert.Equal(0, s.SignOuts);
        var button = cut.Find("button");
        Assert.Equal(Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Strings>>()["Tip.SignOut"].Value, button.TextContent);

        button.Click();

        Assert.Equal(1, s.SignOuts);
        Assert.EndsWith("/feed", Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().Uri);
    }

    [Fact]
    public void WithoutASignInUrlTheMockSignsOutAtOnceAndShowsNoButton()
    {
        var s = new FakeSessions(null);
        Services.AddSingleton<ISessionService>(s);
        var cut = Render<Logout>();
        cut.WaitForAssertion(() => Assert.Equal(1, s.SignOuts));
        Assert.Empty(cut.FindAll("button"));
    }
}
