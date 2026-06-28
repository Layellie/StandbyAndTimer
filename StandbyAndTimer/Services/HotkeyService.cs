using System.Runtime.InteropServices;
using System.Windows.Interop;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

internal sealed class HotkeyService : IHotkeyService
{
    private readonly Dictionary<int, (string id, Action handler)> _byHotkeyId = new();
    private readonly Dictionary<string, int>                       _idLookup   = new(StringComparer.Ordinal);
    private int         _nextHotkeyId;
    private IntPtr      _hwnd;
    private HwndSource? _source;
    private bool        _disposed;

    public void AttachTo(IntPtr hwnd)
    {
        if (_hwnd == hwnd) return;
        Detach();
        _hwnd   = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    public bool Register(string id, HotkeyBinding binding, Action handler)
    {
        if (_hwnd == IntPtr.Zero)
        {
            Logger.Warn($"HotkeyService.Register('{id}') called before AttachTo");
            return false;
        }

        // Replace any prior binding for the same id so re-binding via Settings
        // (when we add that UI in v2.2) doesn't leak the previous registration.
        Unregister(id);

        int hotkeyId = System.Threading.Interlocked.Increment(ref _nextHotkeyId);
        uint mods    = binding.Modifiers | NativeMethods.MOD_NOREPEAT;

        if (!NativeMethods.RegisterHotKey(_hwnd, hotkeyId, mods, binding.VirtualKey))
        {
            Logger.Warn($"RegisterHotKey failed for '{id}' (mods=0x{binding.Modifiers:X}, vk=0x{binding.VirtualKey:X}): " +
                        Marshal.GetLastWin32Error());
            return false;
        }

        _byHotkeyId[hotkeyId] = (id, handler);
        _idLookup[id]         = hotkeyId;
        return true;
    }

    public void Unregister(string id)
    {
        if (!_idLookup.TryGetValue(id, out int hotkeyId)) return;
        NativeMethods.UnregisterHotKey(_hwnd, hotkeyId);
        _byHotkeyId.Remove(hotkeyId);
        _idLookup.Remove(id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;

        int hotkeyId = wParam.ToInt32();
        if (_byHotkeyId.TryGetValue(hotkeyId, out var slot))
        {
            try { slot.handler(); }
            catch (Exception ex) { Logger.Warn($"Hotkey handler '{slot.id}' threw: {ex.Message}"); }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Detach()
    {
        if (_source is null) return;
        foreach (int hotkeyId in _byHotkeyId.Keys)
            NativeMethods.UnregisterHotKey(_hwnd, hotkeyId);
        _byHotkeyId.Clear();
        _idLookup.Clear();
        _source.RemoveHook(WndProc);
        _source = null;
        _hwnd   = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }
}
