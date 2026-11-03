using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Modals;

namespace Resurgence.Tests.WebTests;

/// <summary>A bUnit context with the services the pages need, backed by the mocks. A test replaces one registration before its first Render.</summary>
public abstract class UiContext : BunitContext
{
    protected UiContext()
    {
        Services.AddUiLocalization(new Branding());
        Services.AddMockServices();
        Services.AddSingleton(new HostMode());
        Services.AddSingleton<ModalService>();
        Services.AddSingleton(new AssetImages(new Dictionary<string, string>()));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }
}
