using StandbyAndTimer.Core.Models;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests;

public class HotkeyBindingTests
{
    [Theory]
    [InlineData("Ctrl+Alt+P")]
    [InlineData("Shift+F9")]
    [InlineData("Ctrl+Shift+Win+0")]
    [InlineData("Alt+F24")]
    public void Format_round_trips_through_Parse(string text) =>
        Assert.Equal(text, HotkeyBinding.Parse(text)!.Format());

    [Fact]
    public void Format_without_modifier_is_null() =>
        Assert.Null(new HotkeyBinding(0, 'P').Format());

    [Fact]
    public void Format_of_unsupported_key_is_null() =>
        Assert.Null(new HotkeyBinding(2, 0x09).Format()); // Ctrl+Tab
}

public class HotkeyRebindTests
{
    private const uint Ctrl = 2, Alt = 1, Shift = 4;

    private readonly Harness _h = new();
    private readonly List<string> _calls = [];
    private readonly HashSet<string> _taken = ["Ctrl+Alt+K"];

    public HotkeyRebindTests()
    {
        _h.Settings.RegisterHotkey   = (id, text) => { _calls.Add($"reg {id}={text}"); return !_taken.Contains(text); };
        _h.Settings.UnregisterHotkey = id => _calls.Add($"unreg {id}");
    }

    private SettingsViewModel S => _h.Settings;

    [Fact]
    public void Starting_a_recording_suspends_every_hotkey()
    {
        S.StartHotkeyRecordingCommand.Execute("purge");

        Assert.True(S.IsRecordingPurge);
        Assert.Equal(["unreg purge", "unreg timer", "unreg autopurge"], _calls);
    }

    [Theory]
    [InlineData(0u, (uint)'P', "Str_Settings_HotkeyNeedsModifier")]
    [InlineData(Ctrl | Alt, 0x09u, "Str_Settings_HotkeyUnsupported")]
    [InlineData(Ctrl | Alt, (uint)'T', "Str_Settings_HotkeyDuplicate")]   // = timer
    [InlineData(Ctrl | Alt, (uint)'O', "Str_Settings_HotkeyDuplicate")]   // = auto purge
    public void Invalid_combo_keeps_recording_and_registers_nothing(uint mods, uint vk, string error)
    {
        S.StartHotkeyRecordingCommand.Execute("purge");
        _calls.Clear();

        S.CompleteHotkeyRecording(mods, vk);

        Assert.True(S.IsRecordingPurge);
        Assert.Equal(error, S.HotkeyError);
        Assert.Empty(_calls);
        Assert.Empty(_h.Store.Saved);
    }

    [Fact]
    public void Valid_combo_is_registered_persisted_and_ends_recording()
    {
        S.StartHotkeyRecordingCommand.Execute("purge");
        S.CompleteHotkeyRecording(Ctrl | Shift, 0x74); // F5

        Assert.Null(S.RecordingHotkeyId);
        Assert.Equal("Ctrl+Shift+F5", S.PurgeHotkeyText);
        Assert.Equal("Ctrl+Shift+F5", _h.LastSaved.PurgeHotkey);
        Assert.Contains("reg timer=Ctrl+Alt+T", _calls);
        Assert.Contains("reg autopurge=Ctrl+Alt+O", _calls);
    }

    [Fact]
    public void Combo_refused_by_windows_keeps_the_old_binding()
    {
        S.StartHotkeyRecordingCommand.Execute("timer");
        S.CompleteHotkeyRecording(Ctrl | Alt, 'K');

        Assert.Equal("Ctrl+Alt+T", S.TimerHotkeyText);
        Assert.Equal("Str_Settings_HotkeyTaken", S.HotkeyError);
        Assert.Empty(_h.Store.Saved);
        Assert.True(_calls.LastIndexOf("reg timer=Ctrl+Alt+T") > _calls.IndexOf("reg timer=Ctrl+Alt+K"));
    }

    [Fact]
    public void Leaving_the_settings_tab_cancels_and_re_registers()
    {
        S.IsOpen = true;
        S.StartHotkeyRecordingCommand.Execute("timer");
        _calls.Clear();

        S.IsOpen = false;

        Assert.Null(S.RecordingHotkeyId);
        Assert.Equal(["reg purge=Ctrl+Alt+P", "reg timer=Ctrl+Alt+T", "reg autopurge=Ctrl+Alt+O"], _calls);
    }

    [Fact]
    public void Import_re_registers_hotkeys_and_reports_a_taken_one()
    {
        S.ApplyImportedSettings(new AppSettings { PurgeHotkey = "Ctrl+Alt+F9", TimerHotkey = "Ctrl+Alt+K" });

        Assert.Contains("reg purge=Ctrl+Alt+F9", _calls);
        Assert.Contains("reg timer=Ctrl+Alt+K", _calls);
        Assert.Equal("Str_Settings_HotkeyTaken", S.HotkeyError);
    }

    [Fact]
    public void Auto_purge_hotkey_command_flips_the_switch()
    {
        bool before = _h.Main.AutoPurgeEnabled;

        _h.Main.ToggleAutoPurgeCommand.Execute(null);
        Assert.Equal(!before, _h.Main.AutoPurgeEnabled);

        _h.Main.ToggleAutoPurgeCommand.Execute(null);
        Assert.Equal(before, _h.Main.AutoPurgeEnabled);
    }
}
