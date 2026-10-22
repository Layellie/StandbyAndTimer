using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StandbyAndTimer.Core.Interfaces;

namespace StandbyAndTimer.ViewModels;

/// <summary>
/// The "System settings" panel behind the title bar's Administrator badge:
/// settings that change Windows itself (HKLM) or guard system-level behavior.
/// </summary>
public sealed partial class SystemViewModel : ObservableObject
{
    private readonly IGlobalTimerSetting _globalTimer;
    private bool _isInitializing;

    public const int CooldownMinSeconds = 5, CooldownMaxSeconds = 600, CooldownDefaultSeconds = 60;

    // Persisted through MainViewModel (AppSettings), like SettingsViewModel's.
    internal bool      PromptAnswered        { get; private set; }
    internal bool      GlobalTimerSetByApp   { get; private set; }   // uninstall reverts only then
    internal DateTime? GlobalTimerChangedUtc { get; private set; }
    internal bool      GlobalTimerBootValue  { get; private set; }   // value Windows started with

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isPromptVisible;
    [ObservableProperty] private bool _globalTimerEnabled;
    [ObservableProperty] private bool _restartPending;
    [ObservableProperty] private bool _writeFailed;
    [ObservableProperty] private bool _autoPurgeCooldownEnabled;
    [ObservableProperty] private int  _autoPurgeCooldownSeconds = CooldownDefaultSeconds;
    [ObservableProperty] private bool _closeToTrayDirectly;

    public bool GlobalTimerSupported   => _globalTimer.IsSupported;
    public bool GlobalTimerUnsupported => !_globalTimer.IsSupported;

    public event EventHandler? SettingsChanged;

    public SystemViewModel(IGlobalTimerSetting globalTimer) => _globalTimer = globalTimer;

    // For hosts that do not pass one (tests, UiRender): nothing supported,
    // nothing written.
    internal static SystemViewModel Inert() => new(new InertGlobalTimer());

    private sealed class InertGlobalTimer : IGlobalTimerSetting
    {
        public bool IsSupported => false;
        public bool IsEnabled => false;
        public bool TrySetEnabled(bool enabled) => false;
        public DateTime LastBootUtc => DateTime.UtcNow;
    }

    public void Initialize(bool promptAnswered, bool setByApp, DateTime? changedUtc, bool bootValue,
                           bool cooldownEnabled = false, int cooldownSeconds = CooldownDefaultSeconds,
                           bool closeToTrayDirectly = false)
    {
        using (new InitScope(v => _isInitializing = v))
        {
            PromptAnswered           = promptAnswered;
            GlobalTimerSetByApp      = setByApp;
            GlobalTimerChangedUtc    = changedUtc;
            GlobalTimerEnabled       = _globalTimer.IsEnabled;   // the registry is the truth
            // No change since Windows started: what is set is what runs.
            GlobalTimerBootValue     = ChangedThisBoot ? bootValue : GlobalTimerEnabled;
            RestartPending           = ChangedThisBoot && GlobalTimerEnabled != GlobalTimerBootValue;
            AutoPurgeCooldownEnabled = cooldownEnabled;
            AutoPurgeCooldownSeconds = ClampCooldown(cooldownSeconds);
            CloseToTrayDirectly      = closeToTrayDirectly;
        }
    }

    internal static int ClampCooldown(int seconds) =>
        seconds <= 0 ? CooldownDefaultSeconds : Math.Clamp(seconds, CooldownMinSeconds, CooldownMaxSeconds);

    private bool ChangedThisBoot => GlobalTimerChangedUtc is { } t && t > _globalTimer.LastBootUtc;

    /// <summary>
    /// Start-up question, once: only on Windows 11 and only if the switch is
    /// still off. Already on (set by hand) or unsupported counts as answered.
    /// </summary>
    public void MaybeShowPrompt()
    {
        if (PromptAnswered) return;
        if (GlobalTimerSupported && !GlobalTimerEnabled) { IsPromptVisible = true; return; }
        PromptAnswered = true;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand] private void Open()  => IsOpen = true;
    [RelayCommand] private void Close() => IsOpen = false;

    [RelayCommand]
    private void AcceptPrompt()
    {
        IsPromptVisible = false;
        PromptAnswered  = true;
        GlobalTimerEnabled = true;   // OnGlobalTimerEnabledChanged writes and persists
        if (!GlobalTimerEnabled) SettingsChanged?.Invoke(this, EventArgs.Empty);   // write failed: still answered
    }

    [RelayCommand]
    private void DeclinePrompt()
    {
        IsPromptVisible = false;
        PromptAnswered  = true;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnGlobalTimerEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        if (!_globalTimer.TrySetEnabled(value))
        {
            WriteFailed = true;
            using (new InitScope(v => _isInitializing = v))
                GlobalTimerEnabled = !value;   // show what is really set
            return;
        }
        WriteFailed = false;
        if (!ChangedThisBoot) GlobalTimerBootValue = !value;   // first change since Windows started
        GlobalTimerChangedUtc = DateTime.UtcNow;
        GlobalTimerSetByApp = value;   // removed: nothing left to revert at uninstall
        RestartPending = value != GlobalTimerBootValue;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnAutoPurgeCooldownEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnAutoPurgeCooldownSecondsChanged(int value)
    {
        int clamped = ClampCooldown(value);
        if (clamped != value)
        {
            AutoPurgeCooldownSeconds = clamped;
            return;
        }
        if (_isInitializing) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnCloseToTrayDirectlyChanged(bool value)
    {
        if (_isInitializing) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
