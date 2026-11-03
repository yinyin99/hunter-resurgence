using Bunit;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class MissionsPageTests : UiContext
{
    [Fact]
    public void ClickingAPackExpandsIt()
    {
        var cut = Render<Missions>();
        var pack = cut.Find("button.row.pack");
        Assert.Equal("false", pack.GetAttribute("aria-expanded"));

        pack.Click();

        Assert.Equal("true", cut.Find("button.row.pack").GetAttribute("aria-expanded"));
        Assert.NotEmpty(cut.FindAll("li[data-pack] .more ul.rows li"));
    }

    [Fact]
    public void ADeepLinkOpensItsPackAndScrollsToIt()
    {
        var shell = JSInterop.SetupModule("./_content/Resurgence.Ui/shell.js");
        shell.SetupVoid("scrollToPack", 3);

        var cut = Render<Missions>(p => p.Add(m => m.Group, 3));

        cut.WaitForAssertion(() => shell.VerifyInvoke("scrollToPack"));
        Assert.Equal("true", cut.Find("li[data-pack='3'] button.row.pack").GetAttribute("aria-expanded"));
        Assert.Equal("false", cut.Find("li[data-pack='1'] button.row.pack").GetAttribute("aria-expanded"));
    }
}
