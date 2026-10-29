using System.Text.Json;
using Resurgence.Restore;

namespace Resurgence.Restore.Cli;

/// <summary>Stand-in for the server's "import backup &lt;zip&gt;" verb: prints the report and writes the model as JSON. Remove it when that verb exists.</summary>
public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>0 = read, 1 = refused or unreadable, 2 = wrong arguments.</summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? cataloguePath = null;
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
            if (args[i] != "--catalogue") paths.Add(args[i]);
            else if (++i < args.Length) cataloguePath = args[i];
            else paths.Clear(); // --catalogue without a file
        if (paths.Count is < 1 or > 2)
        {
            stderr.WriteLine("usage: restore <backup.zip> [model.json] [--catalogue <application.json>]   (model default: <backup>.model.json beside the zip)");
            return 2;
        }
        try
        {
            IItemCatalogue? catalogue = null;
            if (cataloguePath is not null)
                using (var c = File.OpenRead(cataloguePath)) catalogue = ApplicationCatalogue.Read(c);
            var r = Importer.Read(paths[0], catalogue);
            stdout.Write(r.Report.ToText());
            var outPath = paths.Count > 1 ? paths[1] : Path.ChangeExtension(paths[0], ".model.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(r.Model, Importer.Json));
            stdout.WriteLine($"model: {outPath}");
            return 0;
        }
        catch (IOException e)
        {
            stderr.WriteLine($"cannot read: {e.Message}");
            return 1;
        }
        catch (RestoreException e)
        {
            stderr.WriteLine($"refused: {e.Message}");
            return 1;
        }
        catch (Exception e) // a bad catalogue or a fault of the reader: its type says which, never a crash
        {
            stderr.WriteLine($"refused: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }
}
