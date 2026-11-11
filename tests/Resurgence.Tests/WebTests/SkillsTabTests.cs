using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Components;

namespace Resurgence.Tests.WebTests;

public sealed class SkillsTabTests : UiContext
{
    /// <summary>An IProfileService that answers only SkillsAsync.</summary>
    public class SkillsOnly : DispatchProxy
    {
        public static IReadOnlyList<Skill> Skills = [];
        protected override object? Invoke(MethodInfo? m, object?[]? args) =>
            m!.Name == nameof(IProfileService.SkillsAsync) ? Task.FromResult(Skills) : throw new NotSupportedException(m.Name);
    }

    [Fact]
    public void ASkillWithoutAGroupRendersAnEmptyHeading()
    {
        SkillsOnly.Skills = [new("Rifle X", null, [new("Spotting", 1, 10)])];
        Services.AddSingleton(DispatchProxy.Create<IProfileService, SkillsOnly>());

        var cut = Render<SkillsTab>(p => p.Add(t => t.Handle, "h").Add(t => t.Sub, "weapons"));

        Assert.Equal("", cut.Find("h4.group").TextContent);
        Assert.Contains("Rifle X", cut.Find(".weapon").TextContent);
    }

    [Fact]
    public void AWeaponIsACardWithItsItemPictureAndShootingDial()
    {
        SkillsOnly.Skills = [new("Rifle X", "Rifles", [new("Shooting", 3, 40)], null, 5, "Blue")];
        Services.AddSingleton(DispatchProxy.Create<IProfileService, SkillsOnly>());
        Services.GetRequiredService<HostMode>().Live = true;

        var cut = Render<SkillsTab>(p => p.Add(t => t.Handle, "h").Add(t => t.Sub, "weapons"));

        var card = cut.Find(".weapons > .weapon");
        Assert.Equal("Rifle X", card.QuerySelector(".name")!.TextContent);
        Assert.Equal("Blue", card.QuerySelector(".variation")!.TextContent);
        Assert.Equal("store-item-5", card.QuerySelector(".pic .asset-slot")!.GetAttribute("aria-label"));
        Assert.Equal("3", card.QuerySelector(".dial b")!.TextContent);
    }
}
