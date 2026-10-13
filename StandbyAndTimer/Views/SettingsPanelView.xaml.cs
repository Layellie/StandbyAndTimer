using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Views;

public partial class SettingsPanelView : UserControl
{
    public SettingsPanelView()
    {
        InitializeComponent();
        PreviewKeyDown              += OnPreviewKeyDown;
        IsKeyboardFocusWithinChanged += OnKeyboardFocusWithinChanged;
    }

    // Hotkey recording: translate the pressed combo into the Win32 MOD_* mask
    // + virtual key and hand it to the view model, which owns the rules.
    // View-only work (WPF Key → VK), so it lives here, not in the view model.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || vm.RecordingHotkeyId is null) return;

        // Swallow everything while recording, including Tab and Alt (which
        // would otherwise move focus or open the system menu).
        e.Handled = true;

        // Alt combos and F10 arrive as Key.System with the real key in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            vm.CancelHotkeyRecording();
            return;
        }

        // A bare modifier press: wait for the main key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        // ModifierKeys uses the same bit values as Win32 MOD_ALT/CONTROL/SHIFT/WIN.
        uint modifiers  = (uint)Keyboard.Modifiers;
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        vm.CompleteHotkeyRecording(modifiers, virtualKey);
    }

    // Focus leaving the page (alt-tab, click elsewhere, tab switch) ends the
    // recording so the global hotkeys are never left unregistered.
    private void OnKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && DataContext is SettingsViewModel vm)
            vm.CancelHotkeyRecording();
    }
}
