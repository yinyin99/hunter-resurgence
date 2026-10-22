using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Backup;

/// <summary>Finds the access token on the running client's command line. Same-user, no admin, no memory read:
/// NtQueryInformationProcess(ProcessCommandLineInformation) with PROCESS_QUERY_LIMITED_INFORMATION. The token stays in memory.</summary>
sealed partial class Watcher(string exe)
{
    [GeneratedRegex(@"/connect:hunt(?:beta|dev)?://[^/\s""]+/[^/\s""]+/lang/[^/\s""]+/access_token/(?<a>[0-9a-fA-F]{40})/refresh_token/[0-9a-fA-F]{40}(?![0-9a-fA-F])")]
    private static partial Regex Connect();

    /// <summary>The access token of a `/connect:hunt://…` argument (scheme hunt, huntbeta or huntdev), else null. The refresh token is never kept.</summary>
    public static string? ParseToken(string cmd) => Connect().Match(cmd) is { Success: true } m ? m.Groups["a"].Value : null;

    public string? Token;
    public DateTime At;
    /// <summary>The API refused the held token (errorCode 5 or 11); cleared by the next token found.</summary>
    public bool Stale;
    /// <summary>A client process was found on the last scan, with or without a token.</summary>
    public bool GameSeen;
    string? key;
    readonly string stem = Path.GetFileNameWithoutExtension(exe);

    /// <summary>True when a new client process or command line replaced the held token.</summary>
    public bool Scan()
    {
        string? best = null, bestKey = null;
        var bestStart = DateTime.MinValue;
        GameSeen = false;
        foreach (var p in Process.GetProcessesByName(stem))
            using (p)
            {
                GameSeen = true;
                if (ParseToken(CommandLine(p.Id) ?? "") is not { } t) continue;
                DateTime start;
                try { start = p.StartTime; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { start = DateTime.MinValue; }
                if (best == null || start > bestStart) (best, bestKey, bestStart) = (t, $"{p.Id}:{t}", start);
            }
        if (best == null || bestKey == key) return false;
        (Token, key, At, Stale) = (best, bestKey, DateTime.Now, false);
        return true;
    }

    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int need);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    /// <summary>Command line of a process of this user, or null (other user, elevated, gone).</summary>
    public static string? CommandLine(int pid)
    {
        var h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            for (int len = 1024; ;)
            {
                var buf = Marshal.AllocHGlobal(len);
                try
                {
                    int st = NtQueryInformationProcess(h, 60, buf, len, out int need); // 60 = ProcessCommandLineInformation: a UNICODE_STRING
                    if (st == 0) return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, IntPtr.Size), (ushort)Marshal.ReadInt16(buf) / 2);
                    if ((uint)st is not (0xC0000004 or 0x80000005 or 0xC0000023) || need <= len) return null; // not a too-small buffer
                    len = need;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { CloseHandle(h); }
    }
}
