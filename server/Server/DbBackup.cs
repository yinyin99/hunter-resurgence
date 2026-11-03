using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Resurgence.Server;

public static class DbBackup
{
    // A consistent copy of every database of data_dir (VACUUM INTO, not a file copy: ADR 0001) as <name>-<yyyyMMdd>.db in
    // <folder>; only the newest <keep> copies of each database stay. Safe while the server runs.
    public static void Run(string dataDir, string folder, int keep)
    {
        Directory.CreateDirectory(folder);
        var day = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        foreach (var db in Directory.GetFiles(dataDir, "*.db"))
        {
            var name = Path.GetFileNameWithoutExtension(db);
            var copy = Path.Combine(folder, $"{name}-{day}.db");
            File.Delete(copy);
            using (var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False"))
            {
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "VACUUM INTO $to";
                cmd.Parameters.AddWithValue("$to", copy);
                cmd.ExecuteNonQuery();
            }
            foreach (var old in Directory.GetFiles(folder, name + "-????????.db").OrderDescending().Skip(keep)) File.Delete(old);
            Console.WriteLine($"backup: {copy}");
        }
    }
}
