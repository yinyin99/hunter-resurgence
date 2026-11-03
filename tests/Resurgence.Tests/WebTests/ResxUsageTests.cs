using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Resurgence.Services.Mocks;

namespace Resurgence.Tests.WebTests;

/// <summary>The key parity test cannot see a key the code asks for that no file holds (the page then shows the key), or a key built at run time.</summary>
public partial class ResxUsageTests
{
    static HashSet<string> Keys() => new ResourceManager("Resurgence.Ui.Strings", typeof(Resurgence.Ui.Strings).Assembly)
        .GetResourceSet(CultureInfo.InvariantCulture, true, false)!.Cast<DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();

    // a literal that ends the key ("]" or "," after it); "Store.Cat." + x is built at run time
    [GeneratedRegex(@"\bL(?:\[|\.Html\(|\.Frag\()""(?<key>[^""{}]+)""(?=\s*[\],)])")]
    private static partial Regex Literal();

    [Fact]
    public void EveryKeyTheCodeAsksForExists()
    {
        var keys = Keys();
        var missing = new SortedSet<string>();
        var found = 0;
        foreach (var dir in new[] { "Ui", "Web", "Launcher" })
            foreach (var file in Directory.EnumerateFiles(Repo.Path("src", dir), "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".razor") || f.EndsWith(".cs")) && !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
            {
                foreach (Match m in Literal().Matches(File.ReadAllText(file)))
                {
                    found++;
                    if (!keys.Contains(m.Groups["key"].Value)) missing.Add($"{m.Groups["key"].Value} ({Path.GetFileName(file)})");
                }
            }
        Assert.True(found > 500, "the scan found the keys");
        Assert.True(missing.Count == 0, "keys without a text: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task EveryStoreCategoryHasItsText()
    {
        var keys = Keys();
        var categories = await new MockStoreService(new MockWalletState()).CategoriesAsync();
        // the sub-categories of the event items fall back to the name the service gives (StoreFrame.Name)
        var wanted = categories.Select(c => c.Key).Concat(categories.Where(c => c.Key != "57").SelectMany(c => c.Children).Select(c => c.Key)).Distinct();
        Assert.Equal([], wanted.Where(k => !keys.Contains("Store.Cat." + k)).ToArray());
    }
}
