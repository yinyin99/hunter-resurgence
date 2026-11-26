using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using static Resurgence.Launcher.LogLock;

namespace Resurgence.Launcher;

/// <summary>
/// The game's log file writer has no lock of its own: a line written by one thread while another thread writes or flushes the log can crash the game.
/// At each start of the known exe the launcher starts the game suspended and wraps the writer's write and flush entries in the game's own memory lock:
/// two small functions in a page of the game's memory take the lock, call the original entry and release the lock, and the writer's table of entries
/// points at them. Every value is checked before anything is written; on any other exe or any other value nothing is written and the game starts as it is.
/// Nothing on disk changes.
/// </summary>
/// <param name="ExeSha256">The SHA-256 of the exe this applies to (lowercase hex).</param>
/// <param name="Table">Offset from the image base of the writer's table: three entries, the cleanup, the write and the flush.</param>
/// <param name="Cleanup">Offset of the cleanup entry the table holds (checked, never changed).</param>
/// <param name="Write">Offset of the write entry (two arguments on the stack, the writer in ecx).</param>
/// <param name="Flush">Offset of the flush entry (no argument, the writer in ecx).</param>
/// <param name="Lock">Offset of the game's memory lock, a Windows critical section the game creates before its first log line.</param>
/// <param name="Enter">Offset of the exe's import of <c>EnterCriticalSection</c>.</param>
/// <param name="Leave">Offset of the exe's import of <c>LeaveCriticalSection</c>.</param>
public sealed record LogLock(string ExeSha256, uint Table, uint Cleanup, uint Write, uint Flush, uint Lock, uint Enter, uint Leave)
{
    /// <summary>The exe Steam serves since November 2026 (its SHA-256 written as bytes).</summary>
    public static readonly LogLock Known = new(Convert.ToHexStringLower([38, 250, 59, 103, 124, 212, 15, 46, 213, 173, 25, 87, 221, 136, 153, 61, 48, 190, 35, 47, 217, 235, 142, 213, 114, 82, 247, 150, 121, 111, 45, 58]),
        Table: 10880620, Cleanup: 1698912, Write: 1700352, Flush: 1699728, Lock: 14214368, Enter: 9806144, Leave: 9806160);

    /// <summary>The flush function starts this many bytes into the page, after the write function.</summary>
    public const int FlushAt = 44;

    /// <summary>The exe's SHA-256 (lowercase hex); null when it cannot be read.</summary>
    public static string? Sha256(string exe)
    {
        try
        {
            using var f = File.OpenRead(exe);
            return Convert.ToHexStringLower(SHA256.HashData(f));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The two functions for an image loaded at <paramref name="image"/>: the write one, then the flush one at <see cref="FlushAt"/>. They use absolute addresses only, so they run at any address.</summary>
    public byte[] Code(uint image)
    {
        var (lk, enter, leave) = (D(image + Lock), D(image + Enter), D(image + Leave));
        return [
            // write: push ecx, push lock, call [Enter], pop ecx; push the two arguments again; mov eax, Write; call eax
            .. H("5168"), .. lk, .. H("FF15"), .. enter, .. H("59 FF742408 FF742408 B8"), .. D(image + Write), .. H("FFD0"),
            // keep the result: push eax, push lock, call [Leave], pop eax, ret 8
            .. H("5068"), .. lk, .. H("FF15"), .. leave, .. H("58 C20800"),
            // flush: push ecx, push lock, call [Enter], pop ecx, mov eax, Flush; call eax; push lock, call [Leave], ret
            .. H("5168"), .. lk, .. H("FF15"), .. enter, .. H("59 B8"), .. D(image + Flush), .. H("FFD0 68"), .. lk, .. H("FF15"), .. leave, .. H("C3"),
        ];
    }

    /// <summary>Checks the table, then writes the two functions and points the table at them; nothing is written unless every check passed. Null when applied, else why not.</summary>
    public string? Apply(ISuspendedGame game)
    {
        var image = game.Image;
        if (image == 0 || game.Read(image + Table, 12) is not { } table) return "the game's memory could not be read";
        byte[] expected = [.. D(image + Cleanup), .. D(image + Write), .. D(image + Flush)];
        if (!table.AsSpan().SequenceEqual(expected)) return "the log writer's table holds other values";
        var code = Code(image);
        var page = game.Allocate(code.Length);
        if (page == 0) return "no memory could be allocated in the game";
        if (!game.Write(page, code)) return "the lock's functions could not be written";
        return game.Write(image + Table + 4, [.. D(page), .. D(page + FlushAt)]) ? null : "the log writer's table could not be written";
    }

    /// <summary>Applies the lock to a game started suspended, logs the outcome, then resumes the game in every case; false when it could not be resumed.</summary>
    public bool ApplyAndResume(ISuspendedGame game, ILogger? log)
    {
        if (Apply(game) is { } why) log?.LogWarning("The game's log lock is not applied, the game starts without it: {Why}", why);
        else log?.LogInformation("The game's log lock is applied");
        return game.Resume();
    }

    internal static byte[] D(uint value) => BitConverter.GetBytes(value);
    internal static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
}

/// <summary>
/// In the current game the Snow Goose's call animation raises an event of its own instead of the one the other birds' call animations raise, so its call is heard but never registers as a clue.
/// At each start of the known exe (the one of <see cref="LogLock.Known"/>), while the game is suspended, the launcher points one call of the game at a small function in a page of the game's memory.
/// The game makes that call for each event of an animation it loads; the function looks at the event's name: when it is exactly the 28 characters <c>snd_snow_goose_call_sequence</c>,
/// it hands the game the name <c>play_call_sound</c> instead (kept after the function in the game's own text form: the 15 characters, their count and the capacity), then jumps to the game's own
/// registration function. Every other name passes through unchanged. Every value is checked before anything is written; on any other exe or any other value nothing is written and the game starts as it is.
/// Nothing on disk changes.
/// </summary>
/// <param name="ExeSha256">The SHA-256 of the exe this applies to (lowercase hex).</param>
/// <param name="Site">Offset from the image base of the call that registers one event of an animation: a call of <paramref name="Register"/>, pointed at the function.</param>
/// <param name="Args">Offset of the 13 bytes before that call that push its arguments (checked, never changed).</param>
/// <param name="CategoryPush">Offset of the push of the events' category before them (checked, never changed).</param>
/// <param name="Category">Offset of the category's text, <c>animation_track_event</c> (checked, never changed).</param>
/// <param name="Register">Offset of the game's registration function (its first 12 bytes checked, never changed).</param>
public sealed record GooseCall(string ExeSha256, uint Site, uint Args, uint CategoryPush, uint Category, uint Register)
{
    /// <summary>The same exe as the log lock.</summary>
    public static readonly GooseCall Known = new(LogLock.Known.ExeSha256, Site: 5041552, Args: 5041539, CategoryPush: 5041502, Category: 11006028, Register: 6946480);

    /// <summary>The new name starts this many bytes into the page, after the function; <see cref="Size"/> is the whole.</summary>
    public const int NameAt = 96, Size = 124;

    /// <summary>The function for an image loaded at <paramref name="image"/>, written at <paramref name="page"/> (where its new name is).</summary>
    public byte[] Code(uint image, uint page) => [
        // mov eax, [esp+4] (the name); cmp [eax+16], 28 (its count); jne to the jump; mov eax, [eax] (its characters)
        .. H("8B442404 8378101C 7548 8B00"),
        // the 28 characters four at a time ("snd_", "snow", "_goo", "se_c", "all_", "sequ", "ence"), each followed by jne to the jump
        .. H("8138736E645F 753E 817804736E6F77 7535 8178085F676F6F 752C 81780C73655F63 7523 817810616C6C5F 751A 81781473657175 7511 817818656E6365 7508"),
        // the same name: mov [esp+4], the new name
        .. H("C7442404"), .. D(page + NameAt),
        // the jump: mov eax, Register; jmp eax; then filler up to the name
        .. H("B8"), .. D(image + Register), .. H("FFE0 CCCCCCCCCCCCCC"),
        // the new name as the game keeps text: 16 bytes of characters, the count, the capacity, 4 unused bytes
        .. "play_call_sound\0"u8, .. D(15), .. D(15), .. D(0),
    ];

    /// <summary>The call instruction at <see cref="Site"/> of an image loaded at <paramref name="image"/> when it calls <paramref name="target"/>.</summary>
    byte[] Call(uint image, uint target) => [.. H("E8"), .. D(unchecked(target - (image + Site + 5)))];

    /// <summary>Checks the five places, then writes the function and points the call at it; nothing is written unless every check passed. Null when applied, else why not.</summary>
    public string? Apply(ISuspendedGame game)
    {
        var image = game.Image;
        if (image == 0) return "the game's memory could not be read";
        (uint At, byte[] Expected, string What)[] checks = [
            (Site, Call(image, image + Register), "the registration call"),
            (Args, H("55 8D4C244C 51 56 8BCF 896C247C"), "the registration call's arguments"),
            (CategoryPush, [.. H("6A15 68"), .. D(image + Category)], "the category's push"),
            (Category, [.. "animation_track_event\0"u8], "the category's text"),
            (Register, H("55 8BEC 83E4F8 81EC1C040000"), "the registration function"),
        ];
        foreach (var (at, expected, what) in checks)
        {
            if (game.Read(image + at, expected.Length) is not { } found) return "the game's memory could not be read";
            if (!found.AsSpan().SequenceEqual(expected)) return $"other values in {what}";
        }
        var page = game.Allocate(Size);
        if (page == 0) return "no memory could be allocated in the game";
        if (!game.Write(page, Code(image, page))) return "the function could not be written";
        return game.Write(image + Site, Call(image, page)) ? null : "the registration call could not be written";
    }

    /// <summary>Applies the patch to a game started suspended and logs the outcome; the game is resumed by <see cref="LogLock.ApplyAndResume"/>.</summary>
    public void ApplyAndLog(ISuspendedGame game, ILogger? log)
    {
        if (Apply(game) is { } why) log?.LogWarning("The game's goose call patch is not applied, the game starts without it: {Why}", why);
        else log?.LogInformation("The game's goose call patch is applied");
    }
}

/// <summary>The game started suspended, as the log lock and the goose call patch read and write it: the seam a test replaces with a byte array.</summary>
public interface ISuspendedGame
{
    /// <summary>Where the exe is loaded; 0 when not known.</summary>
    uint Image { get; }
    /// <summary>The bytes, or null when they cannot all be read.</summary>
    byte[]? Read(uint address, int count);
    /// <summary>Writes over any protection, code included (put back afterwards, the instruction cache flushed); false when it fails.</summary>
    bool Write(uint address, byte[] bytes);
    /// <summary>A new executable page of at least <paramref name="size"/> bytes; 0 when none.</summary>
    uint Allocate(int size);
    /// <summary>Lets the game run; false when it fails.</summary>
    bool Resume();
}

/// <summary>A game process created suspended (<c>CreateProcess</c>), with the same exe, arguments, folder and environment as <see cref="Process.Start(ProcessStartInfo)"/> would use.</summary>
sealed class SuspendedProcess : ISuspendedGame
{
    readonly nint process, thread;
    readonly uint pid;

    SuspendedProcess(ProcessInformation p)
    {
        (process, thread, pid) = (p.Process, p.Thread, p.ProcessId);
        // the 32-bit PEB's ImageBaseAddress: set by Windows when it maps the exe, before the first instruction runs
        if (NtQueryInformationProcess(process, Wow64Information, out var peb, nint.Size, 0) == 0 && peb != 0 && ReadAt(peb + 8, 4) is { } b) Image = BitConverter.ToUInt32(b);
    }

    public uint Image { get; }

    /// <summary>Null when Windows does not start it.</summary>
    public static SuspendedProcess? Create(ProcessStartInfo info)
    {
        var env = new StringBuilder();
        foreach (var (k, v) in info.Environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)) env.Append(k).Append('=').Append(v).Append('\0');
        var si = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        return CreateProcessW(info.FileName, new StringBuilder($"\"{info.FileName}\" {info.Arguments}"), 0, 0, false, Suspended | UnicodeEnvironment, env.Append('\0').ToString(),
            string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory, ref si, out var p) ? new(p) : null;
    }

    public byte[]? Read(uint address, int count) => ReadAt((nint)address, count);

    byte[]? ReadAt(nint address, int count)
    {
        var b = new byte[count];
        return ReadProcessMemory(process, address, b, count, out var n) && n == count ? b : null;
    }

    public bool Write(uint address, byte[] bytes)
    {
        if (!VirtualProtectEx(process, (nint)address, bytes.Length, ReadWrite, out var old)) return false;
        var ok = WriteProcessMemory(process, (nint)address, bytes, bytes.Length, out var n) && n == bytes.Length;
        VirtualProtectEx(process, (nint)address, bytes.Length, old, out _);
        if (ok) FlushInstructionCache(process, (nint)address, bytes.Length);
        return ok;
    }

    public uint Allocate(int size)
    {
        var a = VirtualAllocEx(process, 0, size, Commit | Reserve, ExecuteRead);
        // the game is 32-bit: a page it cannot address is no use (the lowest free one is taken, so this does not happen in practice)
        if ((ulong)a + (ulong)size <= uint.MaxValue) return (uint)a;
        if (a != 0) VirtualFreeEx(process, a, 0, Release);
        return 0;
    }

    public bool Resume() => ResumeThread(thread) != uint.MaxValue;

    /// <summary>The running game, which owns the process handle from now on.</summary>
    public GameProcess Started()
    {
        CloseHandle(thread);
        var (h, id) = (process, pid);
        int? code() => GetExitCodeProcess(h, out var c) && c != StillActive ? unchecked((int)c) : null;
        return new(() => code() is null && GameProcess.HasVisibleWindow(id), code, () => CloseHandle(h)) { Id = (int)id };
    }

    /// <summary>Ends a game that could not be resumed.</summary>
    public void Kill()
    {
        TerminateProcess(process, 1);
        CloseHandle(thread);
        CloseHandle(process);
    }

    const uint Suspended = 4, UnicodeEnvironment = 1024, Commit = 4096, Reserve = 8192, Release = 32768, ExecuteRead = 32, ReadWrite = 4, StillActive = 259;
    const int Wow64Information = 26;

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public short Show, Reserved2Size;
        public nint Reserved2, StdIn, StdOut, StdErr;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string app, StringBuilder commandLine, nint processAttributes, nint threadAttributes, bool inherit, uint flags, string environment, string? directory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(nint process, int kind, out nint value, int size, nint returned);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, int size, out nint read);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, int size, out nint written);
    [DllImport("kernel32.dll")] static extern bool VirtualProtectEx(nint process, nint address, nint size, uint protect, out uint old);
    [DllImport("kernel32.dll")] static extern nint VirtualAllocEx(nint process, nint address, nint size, uint type, uint protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(nint process, nint address, nint size, uint type);
    [DllImport("kernel32.dll")] static extern bool FlushInstructionCache(nint process, nint address, nint size);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
}
