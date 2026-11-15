using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Resurgence.Ui;
using Resurgence.Ui.Layouts;

namespace Resurgence.Tests.WebTests;

public sealed class ShellLayoutTests : UiContext
{
    [Fact]
    public async Task DisposingAfterAReloadDoesNotThrowWhenTheModuleIsGone()
    {
        var shell = JSInterop.SetupModule("./_content/Resurgence.Ui/shell.js");
        shell.SetupVoid("watchScroll", _ => true);
        shell.SetupVoid("unwatchScroll").SetException(new JSException("gone"));
        var cut = Render<ShellLayout>();
        shell.VerifyInvoke("watchScroll");

        await cut.Instance.DisposeAsync();
        shell.VerifyInvoke("unwatchScroll");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]  // the launcher's Hunt now is a drop-down: nothing to watch
    public void TheWebsiteWatchesTheHuntNowLinkAndTheLauncherDoesNot(bool launcher)
    {
        Services.GetRequiredService<HostMode>().Launcher = launcher;
        var shell = JSInterop.SetupModule("./_content/Resurgence.Ui/shell.js");
        shell.SetupVoid("watchScroll", _ => true).SetVoidResult();
        shell.SetupVoid("watchKeys", _ => true).SetVoidResult();
        shell.SetupVoid("watchLaunch").SetVoidResult();
        var cut = Render<ShellLayout>();
        cut.WaitForAssertion(() => shell.VerifyInvoke("watchScroll"));
        if (launcher) shell.VerifyNotInvoke("watchLaunch");
        else cut.WaitForAssertion(() => shell.VerifyInvoke("watchLaunch"));
    }
}
