using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Pages.Signup;

namespace Resurgence.Tests.WebTests;

public sealed class SignupPage1Tests : UiContext
{
    /// <summary>Offers one name, free in the check; the session service then decides.</summary>
    sealed class OneName(string name) : ISignupService
    {
        readonly MockSignupService inner = new();
        public Task<bool> UsernameAvailableAsync(string n) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> AvatarsAsync() => inner.AvatarsAsync();
        public Task<IReadOnlyList<string>> SuggestNamesAsync() => Task.FromResult<IReadOnlyList<string>>([name]);
        public Task<string> EmailAsync() => inner.EmailAsync();
        public Task<bool> EmailConfirmedAsync() => inner.EmailConfirmedAsync();
        public Task<Result> ResendEmailAsync() => inner.ResendEmailAsync();
        public Task<Result> ChangeEmailAsync(string email) => inner.ChangeEmailAsync(email);
        public Task<Result> AutoLoginAsync(string id) => inner.AutoLoginAsync(id);
        public Task<Result> ConfirmEmailAsync(string kind, string? code, string? uuid, string? src) => inner.ConfirmEmailAsync(kind, code, uuid, src);
        public Task<Result> SharedDataAsync(string? code) => inner.SharedDataAsync(code);
    }

    IRenderedComponent<Page1> Submit(string name)
    {
        Services.AddSingleton<ISignupService>(new OneName(name));
        var cut = Render<Page1>();
        cut.Find(".su-dice").Click();
        cut.Find(".su-names button").Click();
        cut.Find("form").Submit();
        return cut;
    }

    [Fact]
    public void ANameTakenBeforeItIsSavedKeepsThePage()
    {
        var cut = Submit("race1");

        var taken = Services.GetRequiredService<IStringLocalizer<Strings>>()["Signup.P1.Taken"].Value;
        Assert.Equal(taken, cut.Find(".su-bubble").TextContent);
        Assert.EndsWith("/", Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().Uri);
    }

    [Fact]
    public void AFreeNameGoesToPage2()
    {
        Submit("free1");

        Assert.EndsWith("/signup/page2", Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().Uri);
    }
}
