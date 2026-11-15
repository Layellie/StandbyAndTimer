using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

/// <summary>Running processes at one moment: exe name (no extension) → PIDs.</summary>
internal sealed class ProcessSnapshot
{
    private readonly Dictionary<string, List<int>> _byName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> Names => _names;
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int>    _pids  = [];

    internal void Add(string name, int pid)
    {
        if (!_byName.TryGetValue(name, out var pids)) { _byName[name] = pids = []; _names.Add(name); }
        _pids.Add(pid);
        pids.Add(pid);
    }

    public IReadOnlyList<int> PidsOf(string name) => _byName.TryGetValue(name, out var p) ? p : [];

    public bool Contains(int pid) => _pids.Contains(pid);
}

// Which of the listed games are running right now. Matches by exe name
// (file name without extension), the same way ProcessOptimizationService
// finds them: reading MainModule.FileName for every process needs access
// rights that anti-cheat protected games deny.
internal static class RunningGames
{
    // Reused between calls (the monitor loop is the only caller, one tick at
    // a time); grows when the process list does. A pinned managed array, so
    // the kernel can fill it and the names in it can be compared as spans,
    // in place and bounds-checked.
    private static readonly object Gate = new();
    private static byte[] _buffer = [];
    private static IntPtr _bufferAddress;

    /// <summary>Where one NtQuerySystemInformation class keeps the image name and the PID of an entry (x64).</summary>
    private readonly record struct Layout(int InfoClass, int NameLength, int NameBuffer, int Pid, int FirstBuffer);

    // SYSTEM_PROCESS_INFORMATION (winternl.h): ULONG NextEntryOffset at 0x00;
    // UNICODE_STRING ImageName at 0x38 (USHORT Length in bytes at 0x38, PWSTR
    // Buffer at 0x40); HANDLE UniqueProcessId at 0x50. Carries every thread
    // of every process, which is what makes it slow.
    private static readonly Layout Full = new(NativeMethods.SystemProcessInformation, 0x38, 0x40, 0x50, 512 * 1024);

    // SYSTEM_BASIC_PROCESS_INFORMATION (Windows 11 24H2 and later): ULONG
    // NextEntryOffset at 0x00; HANDLE UniqueProcessId at 0x08; parent PID at
    // 0x10; sequence number at 0x18; UNICODE_STRING ImageName at 0x20 (Length
    // at 0x20, Buffer at 0x28). Names and PIDs only, no threads: measured
    // 14 µs against 1340 µs for the full class (236 processes), and 19 KB of
    // buffer instead of 500 KB.
    private static readonly Layout Basic = new(NativeMethods.SystemBasicProcessInformation, 0x20, 0x28, 0x08, 64 * 1024);

    // 0 = not tried yet, 1 = works here, -1 = not on this Windows (or its
    // answer did not check out): the full class from then on.
    private static int _basicState;

    /// <summary>
    /// One snapshot of the running processes, image names and PIDs only.
    /// Process.GetProcesses, which this replaces, also turns every thread of
    /// every process into objects (measured: 435 KB allocated and 6.3 ms CPU
    /// per call), and the monitor takes one every second.
    /// </summary>
    /// <param name="only">
    /// The exe names to look for (a set with StringComparer.OrdinalIgnoreCase);
    /// the snapshot then holds these alone and no other name becomes a string
    /// (measured with 240 processes: 63 KB allocated per call without it).
    /// Null = every process.
    /// </param>
    public static ProcessSnapshot Take(HashSet<string>? only = null)
    {
        if (TakeBasic(only) is { } basic) return basic;
        if (TakeFull(only) is { } full) return full;

        var snapshot = new ProcessSnapshot();   // 32-bit or a failed query: the slower managed path
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    string name = p.ProcessName;
                    if (only is null || only.Contains(name)) snapshot.Add(name, p.Id);
                }
                catch { /* exited between enumeration and access */ }
            }
        }
        return snapshot;
    }

    /// <summary>
    /// The short query, or null where it cannot be used. Its layout is not in
    /// the public headers, so its first answer must show this very process
    /// under its own name; otherwise it is never asked again.
    /// </summary>
    internal static ProcessSnapshot? TakeBasic(HashSet<string>? only = null)
    {
        if (IntPtr.Size != 8) return null;
        int state = Volatile.Read(ref _basicState);
        if (state == 0)
        {
            var all = new ProcessSnapshot();
            bool ok = TryRead(all, Basic, only: null);
            if (ok)
            {
                using var self = Process.GetCurrentProcess();
                ok = all.PidsOf(self.ProcessName).Contains(Environment.ProcessId);
            }
            Volatile.Write(ref _basicState, state = ok ? 1 : -1);
            Logger.Info(ok ? "Process list: short query (names and PIDs only)"
                           : "Process list: short query not available on this Windows, using the full one");
            if (ok && only is null) return all;
        }
        if (state < 0) return null;

        var snapshot = new ProcessSnapshot();
        return TryRead(snapshot, Basic, only) ? snapshot : null;   // one failed read: the full class for this tick
    }

    /// <summary>The full process class (every Windows version), or null when the query fails.</summary>
    internal static ProcessSnapshot? TakeFull(HashSet<string>? only = null)
    {
        if (IntPtr.Size != 8) return null;
        var snapshot = new ProcessSnapshot();
        return TryRead(snapshot, Full, only) ? snapshot : null;
    }

    private const int NextEntryOffset = 0x00;

    private static bool TryRead(ProcessSnapshot snapshot, Layout layout, HashSet<string>? only)
    {
        lock (Gate)
        {
            try
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (_buffer.Length == 0) Grow(layout.FirstBuffer);
                    int status = NativeMethods.NtQuerySystemInformation(
                        layout.InfoClass, _bufferAddress, _buffer.Length, out int needed);
                    if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
                    {
                        Grow(Math.Max(needed, _buffer.Length) + 64 * 1024);   // processes may start meanwhile
                        continue;
                    }
                    if (status != 0) return false;

                    Parse(snapshot, layout, only);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"RunningGames: process snapshot failed ({ex.Message})");
                return false;
            }
        }
    }

    // Every read goes through a span of the buffer: an entry or a name that
    // does not lie inside it throws (caught by TryRead) instead of reading
    // foreign memory.
    private static void Parse(ProcessSnapshot snapshot, Layout layout, HashSet<string>? only)
    {
        ReadOnlySpan<byte> buffer = _buffer;
        var watched = only?.GetAlternateLookup<ReadOnlySpan<char>>();
        int offset = 0;
        while (true)
        {
            ReadOnlySpan<byte> entry = buffer[offset..];
            int  length = MemoryMarshal.Read<ushort>(entry[layout.NameLength..]);
            long text   = MemoryMarshal.Read<long>(entry[layout.NameBuffer..]);
            if (text != 0 && length > 0)   // the Idle process has no name
            {
                // The name lies in the same buffer, behind its entry.
                int at = checked((int)(text - (long)_bufferAddress));
                ReadOnlySpan<char> name = ShortName(MemoryMarshal.Cast<byte, char>(buffer.Slice(at, length)));
                int pid = (int)MemoryMarshal.Read<long>(entry[layout.Pid..]);
                if (watched is null) snapshot.Add(name.ToString(), pid);
                else if (watched.Value.TryGetValue(name, out string? listed)) snapshot.Add(listed, pid);
            }
            int next = MemoryMarshal.Read<int>(entry[NextEntryOffset..]);
            if (next <= 0) break;
            offset += next;
        }
    }

    /// <summary>
    /// The name Process.ProcessName gives the same image: only a trailing
    /// ".exe" is removed ("provjobd.exe448854178" stays as it is; a CI runner
    /// has one). Path.GetFileNameWithoutExtension would cut any extension.
    /// </summary>
    internal static string ShortName(string imageName) =>
        imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? imageName[..^4] : imageName;

    private static ReadOnlySpan<char> ShortName(ReadOnlySpan<char> imageName) =>
        imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? imageName[..^4] : imageName;

    private static void Grow(int size)
    {
        _buffer        = GC.AllocateUninitializedArray<byte>(size, pinned: true);
        _bufferAddress = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0);
    }

    /// <summary>The paths from <paramref name="gamePaths"/> whose exe name is in <paramref name="runningNames"/>.</summary>
    public static List<string> Match(IEnumerable<string> gamePaths, IReadOnlySet<string> runningNames) =>
        gamePaths.Where(p => runningNames.Contains(Path.GetFileNameWithoutExtension(p))).ToList();
}
