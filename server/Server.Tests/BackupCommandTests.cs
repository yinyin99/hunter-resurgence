using System.Globalization;
using System.Text.Json.Nodes;
using Resurgence.Server;

namespace Resurgence.Server.Tests;

public class BackupCommandTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("dbbackup").FullName;
    readonly string data, bak;
    readonly Store store;

    public BackupCommandTests()
    {
        (data, bak) = (Path.Combine(tmp, "data"), Path.Combine(tmp, "bak"));
        Directory.CreateDirectory(data);
        store = new Store(Path.Combine(data, "state.db"));
        store.CreateAccount(1, "one", new JsonObject { ["user_id"] = 1, ["handle"] = "one" });
        _ = store.JwtKey;  // state.db.jwtkey beside the database
    }

    public void Dispose() => Fixtures.DeleteDir(tmp);

    static string Today => DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    [Fact]
    public void TheCopyHoldsTheAccountAndNothingElseIsCopied()
    {
        DbBackup.Run(data, bak, 7);
        var copy = Path.Combine(bak, $"state-{Today}.db");
        Assert.Equal([copy], Directory.GetFiles(bak));  // no jwtkey, no WAL files
        Assert.True(new Store(copy).AccountExists(1));
    }

    [Fact]
    public void OnlyTheNewestCopiesStayAndARepeatTheSameDayIsFine()
    {
        Directory.CreateDirectory(bak);
        foreach (var day in new[] { "20200101", "20200102", "20200103" })
            File.WriteAllText(Path.Combine(bak, $"state-{day}.db"), "old");
        DbBackup.Run(data, bak, 2);
        Assert.Equal([$"state-20200103.db", $"state-{Today}.db"], Directory.GetFiles(bak).Select(Path.GetFileName).Order());
        DbBackup.Run(data, bak, 2);
        Assert.Equal(2, Directory.GetFiles(bak).Length);
    }
}
