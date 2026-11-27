using System.Runtime.InteropServices;

namespace Resurgence.Launcher;

/// <summary>
/// What Windows counts for the game process, in bytes: the address space in use (virtual size) and the private memory (private bytes), now and at their peak. The game is a 32-bit process (4 GB of address space at most): its virtual size is what a hunt and a big trophy lodge run
/// out of (#974). Read from the process handle with <c>NtQueryInformationProcess</c> (<c>ProcessVmCounters</c>), the kernel's own per-process counters, which the launcher (64-bit) reads for a 32-bit child as they are. <see cref="System.Diagnostics.Process"/> reads the same counters
/// by process id, but fails once the process is gone; the handle the launcher holds still answers then, with the peaks intact (the current values are 0), which is why the exit line is exact.
/// </summary>
public readonly record struct GameMemory(long Virtual, long Private, long PeakVirtual, long PeakPrivate)
{
    /// <summary>The log's text for these figures, in whole MB (1 MB = 1048576 bytes) and the invariant culture; at the exit only the peaks are told (the process is gone: the current values are 0).</summary>
    public string Line(bool exited) => exited
        ? FormattableString.Invariant($"Game memory at exit: peak virtual {Mb(PeakVirtual)} MB, peak private {Mb(PeakPrivate)} MB")
        : FormattableString.Invariant($"Game memory: virtual {Mb(Virtual)} MB, private {Mb(Private)} MB, peak virtual {Mb(PeakVirtual)} MB, peak private {Mb(PeakPrivate)} MB");

    static long Mb(long bytes) => bytes / (1024 * 1024);

    /// <summary>The figures of the process <paramref name="process"/> (a handle with at least query-limited access); null when Windows does not tell.</summary>
    public static GameMemory? Read(nint process) =>
        NtQueryInformationProcess(process, VmCounters, out var c, Marshal.SizeOf<Counters>(), 0) == 0 ? new((long)c.VirtualSize, (long)c.PrivateUsage, (long)c.PeakVirtualSize, (long)c.PeakPagefileUsage) : null;

    const int VmCounters = 3;

    // VM_COUNTERS_EX: the sizes are SIZE_T; PeakPagefileUsage is the peak of the private commit
    [StructLayout(LayoutKind.Sequential)]
    struct Counters
    {
        public nuint PeakVirtualSize, VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(nint process, int kind, out Counters value, int size, nint returned);
}
