using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StandbyAndTimer.Core.Models;

// INotifyPropertyChanged so the Game Mode list row refreshes its "custom"
// marker when the per-game switches change. Plain BCL, no toolkit, to keep
// Core free of UI dependencies.
public sealed class GameEntry : INotifyPropertyChanged
{
    private bool _boostPriority  = true;
    private bool _allowAutoPurge = true;
    private bool _purgeOnStart;

    public string DisplayName    { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>True when fullscreen detection added it (shown with an "AUTO" tag); false for user-added.</summary>
    public bool   AutoAdded      { get; init; }

    /// <summary>
    /// True: High priority, all cores and the EcoQoS opt-out while it runs.
    /// False: the app never touches this process (e.g. Process Lasso owns it).
    /// </summary>
    public bool BoostPriority
    {
        get => _boostPriority;
        set => Set(ref _boostPriority, value);
    }

    /// <summary>False: automatic standby purges are paused while this game runs (manual purges still work).</summary>
    public bool AllowAutoPurge
    {
        get => _allowAutoPurge;
        set => Set(ref _allowAutoPurge, value);
    }

    /// <summary>
    /// True: one purge (in the chosen mode) as soon as this game starts, before
    /// it fills RAM. Works without AUTO PURGE. Off by default.
    /// </summary>
    public bool PurgeOnStart
    {
        get => _purgeOnStart;
        set => Set(ref _purgeOnStart, value);
    }

    /// <summary>True when a per-game switch is not at its default; drives the list row marker.</summary>
    public bool HasCustomRules => !_boostPriority || !_allowAutoPurge || _purgeOnStart;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCustomRules)));
    }

    public override string ToString() => DisplayName;
}
