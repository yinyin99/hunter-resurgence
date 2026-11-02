using Resurgence.Ui;

namespace Resurgence.Tests.WebTests;

public class AssetImagesTests
{
    [Fact]
    public void PicturesAreFoundByNormalisedSlotName()
    {
        using var t = new TempDir("asset-images-");
        foreach (var f in new[] { "Logo Large.PNG", "logo-large.jpg", "x.svg", "notes.txt" })
            File.WriteAllText(Path.Combine(t.Path, f), "");
        var images = new AssetImages(t.Path);
        Assert.StartsWith("/images/Logo%20Large.PNG?v=", images.Url("logo-large")); // first in ordinal order wins
        Assert.Null(images.Url("x"));     // no SVG: it can run script
        Assert.Null(images.Url("notes")); // not a picture
    }

    [Theory]
    [InlineData("species-Alpine Ibex", "species-alpine-ibex")]
    [InlineData("news-news:featured-post", "news-news-featured-post")]
    public void KeyNormalises(string name, string key) => Assert.Equal(key, AssetImages.Key(name));

    [Fact]
    public void NoFolderMeansNoPictures()
    {
        Assert.Null(new AssetImages(null).Url("logo-large"));
        Assert.Null(new AssetImages(Path.Combine(Path.GetTempPath(), "asset-images-missing-" + Guid.NewGuid().ToString("N"))).Url("logo-large"));
    }
}
