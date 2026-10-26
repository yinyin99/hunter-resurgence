namespace Resurgence.Tests;

/// <summary>A folder of its own under the temp folder for one test (tests run in parallel); removed with the test.</summary>
sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}
