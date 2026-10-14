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
    // a time); grows when the process list does.
    private static readonly object Gate = new();
    private static IntPtr _buffer;
    private static int    _bufferSize;

    /// <summary>
    /// One SystemProcessInformation snapshot, reading only image names and
    /// PIDs. Process.GetProcesses, which this replaces, also turns every
    /// thread of every process into objects (measured: 435 KB allocated and
    /// 6.3 ms CPU per call, vs 18 KB and 5.0 ms), and the monitor takes one
    /// every second, plus one per running boosted game for the PIDs.
    /// </summary>
    public static ProcessSnapshot Take()
    {
        var snapshot = new ProcessSnapshot();
        if (IntPtr.Size == 8 && TryRead(snapshot)) return snapshot;

        snapshot = new ProcessSnapshot();   // 32-bit or a failed query: the slower managed path
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { snapshot.Add(p.ProcessName, p.Id); }
                catch { /* exited between enumeration and access */ }
            }
        }
        return snapshot;
    }

    // SYSTEM_PROCESS_INFORMATION (x64, winternl.h): ULONG NextEntryOffset at
    // 0x00; UNICODE_STRING ImageName at 0x38 (USHORT Length in bytes at 0x38,
    // PWSTR Buffer at 0x40); KPRIORITY BasePriority at 0x48; HANDLE
    // UniqueProcessId at 0x50.
    private const int NextEntryOffset = 0x00;
    private const int ImageNameLength = 0x38;
    private const int ImageNameBuffer = 0x40;
    private const int UniqueProcessId = 0x50;

    private static bool TryRead(ProcessSnapshot snapshot)
    {
        lock (Gate)
        {
            try
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (_buffer == IntPtr.Zero) Grow(512 * 1024);
                    int status = NativeMethods.NtQuerySystemInformation(
                        NativeMethods.SystemProcessInformation, _buffer, _bufferSize, out int needed);
                    if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
                    {
                        Grow(Math.Max(needed, _bufferSize) + 64 * 1024);   // processes may start meanwhile
                        continue;
                    }
                    if (status != 0) return false;

                    int offset = 0;
                    while (true)
                    {
                        IntPtr entry = _buffer + offset;
                        int length = Marshal.ReadInt16(entry, ImageNameLength) & 0xFFFF;
                        IntPtr text = Marshal.ReadIntPtr(entry, ImageNameBuffer);
                        if (text != IntPtr.Zero && length > 0)   // the Idle process has no name
                        {
                            string name = ShortName(Marshal.PtrToStringUni(text, length / 2));
                            snapshot.Add(name, (int)Marshal.ReadIntPtr(entry, UniqueProcessId));
                        }
                        int next = Marshal.ReadInt32(entry, NextEntryOffset);
                        if (next <= 0 || offset + next >= _bufferSize) break;
                        offset += next;
                    }
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"RunningGames: process snapshot failed, using Process.GetProcesses ({ex.Message})");
                return false;
            }
        }
    }

    /// <summary>
    /// The name Process.ProcessName gives the same image: only a trailing
    /// ".exe" is removed ("provjobd.exe448854178" stays as it is; a CI runner
    /// has one). Path.GetFileNameWithoutExtension would cut any extension.
    /// </summary>
    internal static string ShortName(string imageName) =>
        imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? imageName[..^4] : imageName;

    private static void Grow(int size)
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer     = Marshal.AllocHGlobal(size);
        _bufferSize = size;
    }

    /// <summary>The paths from <paramref name="gamePaths"/> whose exe name is in <paramref name="runningNames"/>.</summary>
    public static List<string> Match(IEnumerable<string> gamePaths, IReadOnlySet<string> runningNames) =>
        gamePaths.Where(p => runningNames.Contains(Path.GetFileNameWithoutExtension(p))).ToList();
}
