using System.Globalization;
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Components;

namespace Resurgence.Tests.WebTests;

/// <summary>The server sends the typical and the non-typical deer under their full names; the pages translate each.</summary>
public sealed class SpeciesFullNameTests : UiContext
{
    [Fact]
    public void TheStatisticsTabTranslatesTheTypicalAndTheNonTypicalDeer()
    {
        HuntingStatisticsTests.Stats.Answer = new([], [new("Mule Deer (Typical)", 4, 3, 2), new("Mule Deer (Non-Typical)", 2, 1, 1)], [], 0, 0);
        Services.AddSingleton(DispatchProxy.Create<IProfileService, HuntingStatisticsTests.Stats>());
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            var english = Render<StatisticsTab>(p => p.Add(t => t.Handle, "h").Add(t => t.Sub, "lifetime"));
            Assert.Equal(["Mule Deer (Typical)", "Mule Deer (Non-Typical)"], english.FindAll("table.tbl-c td:first-child").Select(e => e.TextContent));

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
            var german = Render<StatisticsTab>(p => p.Add(t => t.Handle, "h").Add(t => t.Sub, "lifetime"));
            Assert.Equal(["Maultierhirsch (typisch)", "Maultierhirsch (atypisch)"], german.FindAll("table.tbl-c td:first-child").Select(e => e.TextContent));
        }
        finally { CultureInfo.CurrentUICulture = ui; }
    }
}
