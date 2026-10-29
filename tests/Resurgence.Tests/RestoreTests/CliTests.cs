using System.Text.Json.Nodes;
using Cli = Resurgence.Restore.Cli.Program; // the website's top-level Program is in the global namespace

namespace Resurgence.Tests.RestoreTests;

/// <summary>The restore command-line stub on synthetic zips in a temp folder.</summary>
public class CliTests
{
    static (int Code, string Out, string Err) Run(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        return (Cli.Run(args, o, e), o.ToString(), e.ToString());
    }

    [Fact]
    public void CatalogueMakesTheUnitsRuleExactAndNothingCrashes()
    {
        using var temp = new TempDir("resurgence-test-");
        var zip = Path.Combine(temp.Path, "b.zip");
        File.WriteAllBytes(zip, SyntheticZip.PublicZip(byHandle: false).Build());
        // item 500 has a count; 610 (placed, dict units 0) has none; ids and names are made up
        var catalogue = Path.Combine(temp.Path, "application.json");
        File.WriteAllText(catalogue, """{"season":1,"items":[{"id":500,"units":{"count":1,"name":"x"}},{"id":610,"units":null},{"id":"bad","units":{}}]}""");
        var model = Path.Combine(temp.Path, "m.json");

        var (code, stdout, _) = Run(zip, model, "--catalogue", catalogue);
        Assert.Equal(0, code);
        Assert.Contains("units rule: catalogue", stdout);
        var items = JsonNode.Parse(File.ReadAllText(model))!["Items"]!.AsArray();
        Assert.Equal([3, null], items.Select(i => (int?)i!["Units"]));
        Assert.Contains("units rule: catalogue", Run("--catalogue", catalogue, zip, model).Out); // the option may come first
        Assert.Contains("units rule: zero-only", Run(zip, model).Out);

        var noItems = Path.Combine(temp.Path, "empty.json");
        File.WriteAllText(noItems, """{"season":1}""");
        var noCatalogue = Run(zip, model, "--catalogue", noItems);
        Assert.Equal((1, "refused: InvalidDataException: not an item catalogue: no items list"), (noCatalogue.Code, noCatalogue.Err.Trim()));
        var notJson = Path.Combine(temp.Path, "broken.json");
        File.WriteAllText(notJson, "{ nope");
        var bad = Run(zip, model, "--catalogue", notJson);
        Assert.Equal(1, bad.Code);
        Assert.StartsWith("refused: JsonReaderException: ", bad.Err);
        var notZip = Path.Combine(temp.Path, "plain.zip");
        File.WriteAllText(notZip, "not a zip");
        var refused = Run(notZip, model);
        Assert.Equal((1, "refused: not a zip"), (refused.Code, refused.Err.Trim())); // a refused zip: the reason only, no type
        Assert.StartsWith("cannot read: ", Run(zip, model, "--catalogue", Path.Combine(temp.Path, "absent.json")).Err);
        Assert.Equal(2, Run(zip, "--catalogue").Code);
        Assert.Equal(2, Run().Code);
    }
}
