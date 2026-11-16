using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    // The timer: switching it on and off, the lock on the Timer card (always
    // on / while a game runs), and the rules that pause it (idle, on battery).

    // ── Rules that pause the timer: idle, on battery ────────────────────────

    /// <summary>
    /// "Turn the timer off when idle": no input for the chosen minutes turns
    /// a running timer off, which also lets Windows sleep again; the next
    /// input turns it back on. Never while a listed game runs: gamepad input
    /// does not count as input for Windows' idle time.
    /// </summary>
    internal async Task ApplyIdleStateAsync(TimeSpan idleFor, bool anyGameRunning)
    {
        int  minutes = Settings.TimerIdleOffMinutes;
        bool idle    = Settings.TimerIdleOffEnabled && !anyGameRunning
                    && idleFor >= TimeSpan.FromMinutes(minutes);

        if (idle && TimerActive && !_timerPausedForIdle)
        {
            DeactivateTimer(notify: false);
            _timerPausedForIdle = true;
            StatusMessage = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Status_TimerIdleOff"), minutes);
            Logger.Info($"Idle {minutes} min: timer OFF");
        }
        else if (!idle && _timerPausedForIdle)
        {
            _timerPausedForIdle = false;
            if (TimerActive) return;   // already back on (a game start, a lock change)
            await ActivateTimerInternalAsync(silent: true).ConfigureAwait(true);
            StatusMessage = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Status_TimerIdleBack"), ActualTimerMs);
            Logger.Info("Input after idle: timer ON");
        }
    }

    // Tests set a fixed power source; the app asks Windows.
    internal Func<bool> ReadOnBattery { get; set; } = PowerSource.IsOnBattery;

    /// <summary>
    /// "Turn the timer off on battery": unplugged turns a running timer off,
    /// which lets the CPU sleep and saves the battery; plugged in turns it back
    /// on. Not while a listed game runs (like the idle switch-off: a game is
    /// played on purpose). A TIMER press on battery stays on until the power
    /// source changes.
    /// </summary>
    internal async Task ApplyBatteryStateAsync(bool onBattery, bool anyGameRunning)
    {
        _onBattery = onBattery;
        bool off = Settings.TimerOffOnBattery && onBattery && !anyGameRunning;

        if (!off)
        {
            _timerOnByUserOnBattery = false;
            if (!_timerPausedForBattery) return;
            _timerPausedForBattery = false;
            if (TimerActive) return;   // already back on (a game start, a lock change)
            await ActivateTimerInternalAsync(silent: true).ConfigureAwait(true);
            StatusMessage = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Status_TimerBatteryBack"), ActualTimerMs);
            Logger.Info("Power back: timer ON");
        }
        else if (TimerActive && !_timerPausedForBattery && !_timerOnByUserOnBattery)
        {
            DeactivateTimer(notify: false);
            _timerPausedForBattery = true;
            StatusMessage = _localization.GetString("Str_Status_TimerBatteryOff");
            Logger.Info("On battery: timer OFF");
        }
    }

    // True while the battery rule would keep the timer off (for the user's own switch-ons).
    private bool BatteryRuleApplies() =>
        Settings.TimerOffOnBattery && _onBattery && !(_lastSnapshot?.AnyGameRunning ?? false);

    // Raised on the SystemEvents thread: apply on the UI thread.
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.StatusChange) return;
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            try { await ApplyBatteryStateAsync(ReadOnBattery(), _lastSnapshot?.AnyGameRunning ?? false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warn($"Power change handling: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    // ── The IN GAME lock ────────────────────────────────────────────────────

    /// <summary>
    /// In-game lock: a listed game starting turns the timer on, the last one
    /// exiting turns it off again, but only if a game start had turned it
    /// on. Acts on transitions only, so a TIMER press stays in charge until
    /// the next game starts. No-op in the other lock modes.
    /// </summary>
    internal async Task ApplyGameStateAsync(bool anyGameRunning)
    {
        bool started = anyGameRunning && !_anyGameRunning;
        bool stopped = !anyGameRunning && _anyGameRunning;
        _anyGameRunning = anyGameRunning;
        if (TimerLock != TimerLockMode.InGame) return;

        if (started && !TimerActive)
        {
            await ActivateTimerInternalAsync(silent: true).ConfigureAwait(true);
            _timerStartedForGame = true;
            StatusMessage = _localization.GetString("Str_Status_TimerGameOn");
            Logger.Info("In-game lock: game started, timer ON");
        }
        else if (stopped && TimerActive && _timerStartedForGame)
        {
            DeactivateTimer(notify: false);
            StatusMessage = _localization.GetString("Str_Status_TimerGameOff");
            Logger.Info("In-game lock: last game exited, timer OFF");
        }
    }

    private void OnTimerResolutionMeasured(object? sender, double measuredMs) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!TimerActive) return;
            ActualTimerMs = measuredMs;
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetString("Str_Status_TimerActive"), measuredMs);
        });

    // ── The TIMER button and the lock on the card ───────────────────────────

    [RelayCommand]
    private async Task ToggleTimerResolutionAsync()
    {
        _timerStartedForGame   = false;   // the user is in charge now
        _timerPausedForIdle    = false;
        _timerPausedForBattery = false;
        if (TimerActive)
        {
            DeactivateTimer(notify: true);
            _timerOnByUserOnBattery = false;
            StatusMessage = _localization.GetString("Str_Status_TimerOff");
            Logger.Info("User toggled timer OFF");
        }
        else
        {
            await ActivateTimerInternalAsync(silent: false).ConfigureAwait(true);
            _timerOnByUserOnBattery = BatteryRuleApplies();
        }
        PersistSettings();
    }

    // Timer card lock, cycling Unlocked -> Locked -> InGame -> Unlocked.
    // Locked switches the timer on now ("always on"). InGame hands it to
    // the games: on now if one runs, and from then on it follows games
    // starting and exiting; a timer already on stays on. Unlocked leaves
    // the running session alone and only affects the next start. App
    // auto-start is a separate setting.
    [RelayCommand]
    private async Task ToggleTimerLockAsync()
    {
        TimerLock = TimerLock switch
        {
            TimerLockMode.Unlocked => TimerLockMode.Locked,
            TimerLockMode.Locked   => TimerLockMode.InGame,
            _                      => TimerLockMode.Unlocked,
        };
        _timerStartedForGame = false;

        switch (TimerLock)
        {
            case TimerLockMode.Locked when !TimerActive:
                await ActivateTimerInternalAsync(silent: false).ConfigureAwait(true);
                _timerOnByUserOnBattery = BatteryRuleApplies();
                break;
            case TimerLockMode.InGame when _anyGameRunning && !TimerActive:
                await ActivateTimerInternalAsync(silent: true).ConfigureAwait(true);
                _timerStartedForGame = true;
                break;
            // InGame with no game running leaves a running timer alone:
            // the cycle passes through InGame on the way from Locked to
            // Unlocked, and unlocking must not switch the timer off.
        }

        StatusMessage = _localization.GetString(TimerLock switch
        {
            TimerLockMode.Locked => "Str_Status_TimerLockedOn",
            TimerLockMode.InGame => "Str_Status_TimerLockedInGame",
            _                    => "Str_Status_TimerLockedOff",
        });
        Logger.Info($"Timer lock {TimerLock} (timer active={TimerActive})");
        PersistSettings();
    }

    // notify: tray balloon. Only a TIMER press notifies; the in-game lock
    // switches quietly (its activation is silent too) and says so in the
    // status line instead.
    private void DeactivateTimer(bool notify)
    {
        _timerService.Deactivate();
        TimerActive          = false;
        _timerStartedForGame = false;
        ShowWindowsResolution();
        if (notify)
            TimerToggledNotification?.Invoke(this, new TimerToggledArgs(false, 0));
    }

    // With the app's timer off the card shows what Windows really runs at.
    // ActualTimerMs used to change only while the timer ran: it read 0.00 from
    // a start with the timer off (the in-game lock without a game) and kept a
    // stale 0.50 after the timer went off.
    private void ShowWindowsResolution()
    {
        if (!TimerActive) ActualTimerMs = _timerService.QueryCurrentMs();
    }

    // Idle status line: the live timer reading while the timer runs, else "Ready".
    private void SetIdleStatus() =>
        StatusMessage = TimerActive
            ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Str_Status_TimerActive"), ActualTimerMs)
            : _localization.GetString("Str_Status_Ready");

    private async Task ActivateTimerInternalAsync(bool silent)
    {
        double ms = await _timerService.ActivateAsync(_appCts.Token).ConfigureAwait(true);
        ActualTimerMs = ms;
        TimerActive   = true;
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString("Str_Status_TimerActive"), ms);
        if (!silent)
            TimerToggledNotification?.Invoke(this, new TimerToggledArgs(true, ms));
        Logger.Info(FormattableString.Invariant($"Timer activated (silent={silent}, actual={ms:F3} ms)"));
    }

    // ── Refresh rate (timer card) ───────────────────────────────────────────

    private readonly IDisplayInfoService? _display;

    /// <summary>"144 Hz" for the primary display, "" when unknown.</summary>
    [ObservableProperty] private string _refreshRateText = "";

    internal void UpdateRefreshRate()
    {
        int hz = _display?.GetRefreshRate() ?? 0;
        RefreshRateText = hz > 0 ? hz.ToString(CultureInfo.InvariantCulture) + " Hz" : "";
    }

    // Raised on the SystemEvents thread: read again on the UI thread.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.InvokeAsync(UpdateRefreshRate);
}
