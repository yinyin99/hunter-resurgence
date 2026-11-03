using Bunit;
using Microsoft.JSInterop;
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
}
