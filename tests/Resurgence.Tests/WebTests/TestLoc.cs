using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Resurgence.Ui;

namespace Resurgence.Tests.WebTests;

/// <summary>The localizer the pages inject, on its own: it reads the thread's UI culture.</summary>
static class TestLoc
{
    public static Loc Make(Branding? branding = null) =>
        new(new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance), branding ?? new Branding());
}
