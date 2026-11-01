using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StandbyAndTimer.Core.Models;

// INotifyPropertyChanged so the Game Mode list row refreshes its "custom"
// marker when the per-game switches change. Plain BCL, no toolkit, to keep
// Core free of UI dependencies.
public sealed class GameEntry : INotifyPropertyChanged
{
    private GamePriority _priority = GamePriority.High;
    private bool _allowAutoPurge = true;
    private bool _purgeOnStart;
    private bool _purgeOnExit;
    private bool _timerInGame    = true;

    public string DisplayName    { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>True when fullscreen detection added it (shown with an "AUTO" tag); false for user-added.</summary>
    public bool   AutoAdded      { get; init; }

    /// <summary>
    /// High or AboveNormal: that priority class, all cores and the EcoQoS
    /// opt-out while it runs. Unchanged: the app never touches this process
    /// (e.g. Process Lasso owns it).
    /// </summary>
    public GamePriority Priority
    {
        get => _priority;
        set
        {
            if (_priority == value) return;
            _priority = value;
            Raise();
            Raise(nameof(BoostPriority));
            Raise(nameof(HasCustomRules));
        }
    }

    /// <summary>
    /// Whether the app boosts this game at all (Priority is not Unchanged).
    /// The switch from before the priority menu; true picks High.
    /// </summary>
    public bool BoostPriority
    {
        get => _priority != GamePriority.Unchanged;
        set
        {
            if (value != BoostPriority) Priority = value ? GamePriority.High : GamePriority.Unchanged;
        }
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

    /// <summary>
    /// True: one purge (in the chosen mode) when this game closes, so the cache
    /// it left behind is freed. Works without AUTO PURGE. Off by default.
    /// </summary>
    public bool PurgeOnExit
    {
        get => _purgeOnExit;
        set => Set(ref _purgeOnExit, value);
    }

    /// <summary>
    /// True: the IN GAME timer lock turns the timer on while this game runs.
    /// False: it leaves the timer alone for this game (another running game
    /// that wants it still turns it on). On by default.
    /// </summary>
    public bool TimerInGame
    {
        get => _timerInGame;
        set => Set(ref _timerInGame, value);
    }

    /// <summary>True when a per-game switch is not at its default; drives the list row marker.</summary>
    public bool HasCustomRules =>
        _priority != GamePriority.High || !_allowAutoPurge || _purgeOnStart || _purgeOnExit || !_timerInGame;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        Raise(name);
        Raise(nameof(HasCustomRules));
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => DisplayName;
}
