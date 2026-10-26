namespace Resurgence.Tests.LauncherApp;

/// <summary>Writing the small test files of the launcher tests (always below a temp folder).</summary>
static class Files
{
    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static void Touch(string path) => Write(path, "x");

    public static string Bytes(string path) => System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));

    public static string Also(this string path, Action<string> act)
    {
        act(path);
        return path;
    }
}
