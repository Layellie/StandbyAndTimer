namespace StandbyAndTimer.Core.Models;

/// <summary>
/// Modifier-flag mask + virtual-key code parsed from a "Ctrl+Alt+P"-style
/// settings string. The mask uses the same bit values as the Win32
/// <c>MOD_*</c> constants so it can be passed straight to <c>RegisterHotKey</c>.
/// </summary>
public sealed record HotkeyBinding(uint Modifiers, uint VirtualKey)
{
    private const uint MOD_ALT     = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT   = 0x0004;
    private const uint MOD_WIN     = 0x0008;

    /// <summary>
    /// Parses "Ctrl+Alt+P" / "Shift+F9" / "Win+Alt+M" into a binding.
    /// Returns null for malformed input (no modifier, missing key, etc.) so
    /// the caller can log + fall back to default behavior instead of throwing.
    /// </summary>
    public static HotkeyBinding? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        uint mods = 0;
        uint vk   = 0;

        foreach (var raw in text.Split('+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL": mods |= MOD_CONTROL; break;
                case "ALT":     mods |= MOD_ALT;     break;
                case "SHIFT":   mods |= MOD_SHIFT;   break;
                case "WIN":
                case "WINDOWS": mods |= MOD_WIN;     break;
                default:
                    if (raw.Length == 1 && char.IsLetterOrDigit(raw[0]))
                    {
                        // VK_A..VK_Z + VK_0..VK_9 happen to match the ASCII
                        // codes of the upper-case characters / digit chars,
                        // so a direct cast does the job without a lookup table.
                        vk = char.ToUpperInvariant(raw[0]);
                    }
                    else if (raw.Length >= 2
                          && (raw[0] == 'F' || raw[0] == 'f')
                          && int.TryParse(raw[1..], out int fn)
                          && fn is >= 1 and <= 24)
                    {
                        // VK_F1 = 0x70, VK_F2 = 0x71, ... VK_F24 = 0x87
                        vk = (uint)(0x70 + fn - 1);
                    }
                    break;
            }
        }

        return (mods != 0 && vk != 0) ? new HotkeyBinding(mods, vk) : null;
    }
}
