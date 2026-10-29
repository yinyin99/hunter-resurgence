using System.Text.Json;
using Resurgence.Restore;

// Stand-in for the server's "import backup <zip>" verb: prints the report and writes the model as JSON. Remove it when that verb exists.
if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: restore <backup.zip> [model.json]   (default: <backup>.model.json beside the zip)");
    return 2;
}
try
{
    var r = Importer.Read(args[0]);
    Console.Write(r.Report.ToText());
    var outPath = args.Length > 1 ? args[1] : Path.ChangeExtension(args[0], ".model.json");
    File.WriteAllText(outPath, JsonSerializer.Serialize(r.Model, Importer.Json));
    Console.WriteLine($"model: {outPath}");
    return 0;
}
catch (RestoreException e)
{
    Console.Error.WriteLine($"refused: {e.Message}");
    return 1;
}
catch (IOException e)
{
    Console.Error.WriteLine($"cannot read: {e.Message}");
    return 1;
}
