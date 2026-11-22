using Microsoft.Web.WebView2.Core;
using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

public class WebViewRecoveryTests
{
    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited, "reload")]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive, "reload")]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited, "reload")]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited, "recreate")]
    [InlineData(CoreWebView2ProcessFailedKind.UtilityProcessExited, "")]
    public void AFailedProcessAsksForTheMatchingRecovery(CoreWebView2ProcessFailedKind kind, string expected)
    {
        var asked = "";
        MainWindow.Handle(kind, () => asked += "reload", () => asked += "recreate");
        Assert.Equal(expected, asked);
    }
}
