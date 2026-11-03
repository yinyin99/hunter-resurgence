namespace Resurgence.Tests;

/// <summary>The repository's root folder (the one holding hunter-resurgence.slnx), found from the test's output folder.</summary>
static class Repo
{
    public static string Root { get; } = Find();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(System.IO.Path.Combine(dir.FullName, "hunter-resurgence.slnx"))) dir = dir.Parent!;
        return dir.FullName;
    }
}
