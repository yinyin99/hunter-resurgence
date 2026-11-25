using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Components;

namespace Resurgence.Tests.WebTests;

public sealed class SheetGuideTests
{
    static ScoreSheet Sheet(string species, string gender) =>
        new("antler", species, [], [], [], [], "", false, "", 0, "", "", "", "", 0, "", 0, 0, "", 1, DateTime.UnixEpoch, 1, 0, null, gender);

    [Theory]
    [InlineData("Whitetail Deer (Typical)", "", "antler-guide-whitetail-deer-typical")]
    [InlineData("Mule Deer (Non-Typical)", "", "antler-guide-mule-deer-non-typical")]
    [InlineData("Blacktail Deer (Typical)", "", "antler-guide-blacktail-deer-typical")]
    [InlineData("Wendigo", "male", "antler-guide-wendigo-male")]
    [InlineData("Wendigo", "female", "antler-guide-wendigo-female")]
    [InlineData("Wendigo", "", "antler-guide-wendigo")]
    [InlineData("Moose (Typical)", "male", "antler-guide-moose")]
    public void TheGuideSlotFollowsTheSpeciesAndGender(string species, string gender, string key) =>
        Assert.Equal(key, AssetImages.Key("antler-guide-" + SheetTab.GuideName(Sheet(species, gender))));
}
