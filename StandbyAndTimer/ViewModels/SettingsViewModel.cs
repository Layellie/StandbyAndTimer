using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    // Cached so we don't allocate a fresh options instance on every export.
    private static readonly JsonSerializerOptions ExportJsonOptions =
        new() { WriteIndented = true };

    // Number of log lines surfaced in the in-panel viewer. 100 is enough
    // context to triage a hiccup without making the box itself a scroll-fest;
    // the full log lives on disk and Open Log Folder still surfaces it.
    private const int LogTailLines = 100;

    private readonly IAutoStartService    _autoStartService;
    private readonly ILocalizationService _localization;
    private readonly ISettingsService     _settingsService;
    private readonly IUpdateService       _updateService;
    private readonly IThemeService        _themeService;
    private readonly ILogTailReader       _logTailReader;

    // Monotonically increases on every LoadLogTailAsync entry. After the
    // async read returns, the result is published to RecentLogs only if
    // the captured ticket still matches — this guarantees a slow read
    // that started first can never clobber a faster, newer one. Using a
    // counter (rather than a CancellationTokenSource field) keeps this
    // type Disposable-free, which the SettingsViewModel singleton
    // lifetime makes appropriate.
    private int _logReadTicket;

    private bool _isInitializing;

    [ObservableProperty] private bool     _isOpen;
    [ObservableProperty] private bool     _autoStartEnabled;
    [ObservableProperty] private bool     _updateCheckEnabled = true;
    [ObservableProperty] private Language _selectedLanguage = Language.English;
    [ObservableProperty] private Theme    _selectedTheme    = Theme.Dark;
    [ObservableProperty] private string   _updateStatus = string.Empty;
    [ObservableProperty] private string   _recentLogs   = string.Empty;
    [ObservableProperty] private bool     _notifyOnPurge        = true;
    [ObservableProperty] private bool     _notifyOnTimerToggle  = true;
    [ObservableProperty] private bool     _notifyOnGameDetected = true;
    [ObservableProperty] private string   _purgeHotkeyText      = "Ctrl+Alt+P";
    [ObservableProperty] private string   _timerHotkeyText      = "Ctrl+Alt+T";
    [ObservableProperty] private string   _autoPurgeHotkeyText  = "Ctrl+Alt+O";

    // Hotkey rebinding. RecordingHotkeyId is the id being recorded (one of
    // HotkeyIds) or null; HotkeyError is shown under the Hotkeys group and
    // cleared when a new recording starts.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingPurge), nameof(IsRecordingTimer), nameof(IsRecordingAutoPurge))]
    private string? _recordingHotkeyId;

    [ObservableProperty] private string   _hotkeyError          = string.Empty;

    public const string HotkeyIdPurge     = "purge";
    public const string HotkeyIdTimer     = "timer";
    public const string HotkeyIdAutoPurge = "autopurge";

    private static readonly string[] HotkeyIds = [HotkeyIdPurge, HotkeyIdTimer, HotkeyIdAutoPurge];

    public bool IsRecordingPurge     => RecordingHotkeyId == HotkeyIdPurge;
    public bool IsRecordingTimer     => RecordingHotkeyId == HotkeyIdTimer;
    public bool IsRecordingAutoPurge => RecordingHotkeyId == HotkeyIdAutoPurge;

    private string HotkeyText(string id) => id switch
    {
        HotkeyIdPurge     => PurgeHotkeyText,
        HotkeyIdTimer     => TimerHotkeyText,
        HotkeyIdAutoPurge => AutoPurgeHotkeyText,
        _                 => throw new ArgumentOutOfRangeException(nameof(id), id, null)
    };

    private void SetHotkeyText(string id, string text)
    {
        switch (id)
        {
            case HotkeyIdPurge:     PurgeHotkeyText     = text; break;
            case HotkeyIdTimer:     TimerHotkeyText     = text; break;
            case HotkeyIdAutoPurge: AutoPurgeHotkeyText = text; break;
            default: throw new ArgumentOutOfRangeException(nameof(id), id, null);
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadAndInstallCommand))]
    private bool _updateAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadAndInstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdateCommand))]
    private bool _isDownloading;

    private string? _pendingAssetUrl;
    private string? _pendingVersion;
    private string? _pendingSha256;

    public event EventHandler? SettingsChanged;

    // Raised after a successful ImportSettings — payload is the freshly loaded
    // AppSettings. MainViewModel subscribes to re-apply values to the running
    // session so the user doesn't have to restart for the import to take effect.
    public event EventHandler<AppSettings>? SettingsImported;

    public Language[] LanguageOptions { get; } = [Language.English, Language.Turkish];
    public Theme[]    ThemeOptions    { get; } = [Theme.Dark, Theme.Light, Theme.System];

    // Shown in the Settings panel footer. The release pipeline passes
    // -p:Version=X.Y.Z to dotnet publish so this lines up with the GitHub
    // release tag; dev / CI builds fall back to the csproj baseline. We
    // trim a trailing ".0" so "2.0.5.0" renders as "2.0.5" — matches the
    // way the version is communicated everywhere else (tags, installer).
    public string CurrentVersion { get; } = FormatVersion(
        Assembly.GetExecutingAssembly().GetName().Version);

    private static string FormatVersion(Version? v)
    {
        if (v is null) return "?";
        return v.Revision == 0
            ? FormattableString.Invariant($"{v.Major}.{v.Minor}.{v.Build}")
            : FormattableString.Invariant($"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
    }

    // Set by MainViewModel after Initialize so Export/Import can serialize a snapshot.
    internal Func<AppSettings>? SnapshotProvider { get; set; }

    // Set by App once the hotkey service is attached to the window. App owns
    // the registrations (it knows which command each id fires); the view
    // model only decides WHAT to register. RegisterHotkey returns false when
    // Win32 refuses the combo (taken by another app, unparsable text).
    internal Func<string, string, bool>? RegisterHotkey   { get; set; }
    internal Action<string>?             UnregisterHotkey { get; set; }

    public SettingsViewModel(
        IAutoStartService    autoStartService,
        ILocalizationService localization,
        ISettingsService     settingsService,
        IUpdateService       updateService,
        IThemeService        themeService,
        ILogTailReader       logTailReader)
    {
        _autoStartService = autoStartService;
        _localization     = localization;
        _settingsService  = settingsService;
        _updateService    = updateService;
        _themeService     = themeService;
        _logTailReader    = logTailReader;
    }

    public void Initialize(
        bool autoStartEnabled, Language language, bool updateCheckEnabled, Theme theme,
        bool notifyOnPurge, bool notifyOnTimerToggle, bool notifyOnGameDetected,
        string purgeHotkey, string timerHotkey, string autoPurgeHotkey)
    {
        using (new InitScope(v => _isInitializing = v))
        {
            AutoStartEnabled     = autoStartEnabled;
            SelectedLanguage     = language;
            UpdateCheckEnabled   = updateCheckEnabled;
            SelectedTheme        = theme;
            NotifyOnPurge        = notifyOnPurge;
            NotifyOnTimerToggle  = notifyOnTimerToggle;
            NotifyOnGameDetected = notifyOnGameDetected;
            PurgeHotkeyText      = purgeHotkey;
            TimerHotkeyText      = timerHotkey;
            AutoPurgeHotkeyText  = autoPurgeHotkey;
        }
    }

    [RelayCommand]
    private Task RefreshLogsAsync() => LoadLogTailAsync();

    // IsOpen = "the Settings tab is on screen" (bound to the main TabControl).
    // Auto-refresh whenever the tab is shown so the user always sees current
    // logs without an extra click; bumping the ticket on leave ensures any
    // still-in-flight read that returns later won't overwrite RecentLogs.
    partial void OnIsOpenChanged(bool value)
    {
        if (value) _ = LoadLogTailAsync();
        else       System.Threading.Interlocked.Increment(ref _logReadTicket);

        // Leaving the Settings tab must never strand the hotkeys suspended.
        if (!value) CancelHotkeyRecording();
    }

    // ── Hotkey rebinding ──────────────────────────────────────────────────────

    // All global hotkeys are released while recording: RegisterHotKey
    // swallows the combo before the window sees the key, so pressing a
    // current shortcut would fire its action instead of being recorded.
    [RelayCommand]
    private void StartHotkeyRecording(string? id)
    {
        if (id is null || Array.IndexOf(HotkeyIds, id) < 0) return;
        if (RegisterHotkey is null || UnregisterHotkey is null) return;

        if (RecordingHotkeyId is not null) CancelHotkeyRecording();

        HotkeyError       = string.Empty;
        RecordingHotkeyId = id;
        foreach (string hid in HotkeyIds) UnregisterHotkey(hid);
    }

    /// <summary>Ends a recording without changes and re-registers all hotkeys.</summary>
    public void CancelHotkeyRecording()
    {
        if (RecordingHotkeyId is null) return;
        RecordingHotkeyId = null;
        RegisterAll();
    }

    /// <summary>
    /// Called by the view with the pressed combo (Win32 MOD_* mask + virtual
    /// key). Invalid combos keep recording and show why; a valid one is
    /// registered, persisted and ends the recording. If Win32 refuses it the
    /// previous binding is restored.
    /// </summary>
    public void CompleteHotkeyRecording(uint modifiers, uint virtualKey)
    {
        string? id = RecordingHotkeyId;
        if (id is null || RegisterHotkey is null) return;

        var candidate = new HotkeyBinding(modifiers, virtualKey);
        if (modifiers == 0)
        {
            HotkeyError = _localization.GetString("Str_Settings_HotkeyNeedsModifier");
            return;
        }
        string? text = candidate.Format();
        if (text is null)
        {
            HotkeyError = _localization.GetString("Str_Settings_HotkeyUnsupported");
            return;
        }

        if (HotkeyIds.Any(other => other != id && HotkeyBinding.Parse(HotkeyText(other)) == candidate))
        {
            HotkeyError = _localization.GetString("Str_Settings_HotkeyDuplicate");
            return;
        }

        RecordingHotkeyId = null;
        if (RegisterHotkey(id, text))
        {
            // Property change → OnXHotkeyTextChanged → SettingsChanged →
            // MainViewModel.PersistSettings writes it to the registry.
            SetHotkeyText(id, text);
            HotkeyError = string.Empty;
            Logger.Info($"Hotkey '{id}' rebound to {text}");
        }
        else
        {
            HotkeyError = _localization.GetString("Str_Settings_HotkeyTaken");
            Logger.Warn($"Hotkey '{id}': {text} refused by RegisterHotKey, keeping previous binding");
        }
        RegisterAll();
    }

    // Re-registers whatever the texts currently hold. Register replaces an
    // existing registration for the same id, so this is safe to call after
    // a successful rebind or an import too. Returns false if any failed
    // (true when no registrar is attached yet: nothing to re-apply).
    private bool RegisterAll()
    {
        if (RegisterHotkey is null) return true;
        bool ok = true;
        foreach (string id in HotkeyIds)
        {
            string text = HotkeyText(id);
            if (RegisterHotkey(id, text)) continue;
            ok = false;
            Logger.Warn($"Hotkey '{id}': could not register {text}");
        }
        return ok;
    }

    partial void OnPurgeHotkeyTextChanged(string value)
    {
        if (_isInitializing) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnTimerHotkeyTextChanged(string value)
    {
        if (_isInitializing) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnAutoPurgeHotkeyTextChanged(string value)
    {
        if (_isInitializing) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadLogTailAsync()
    {
        int myTicket = System.Threading.Interlocked.Increment(ref _logReadTicket);

        string tail = await _logTailReader
            .ReadTailAsync(LogTailLines)
            .ConfigureAwait(true);

        // Superseded by a newer LoadLogTailAsync (or by panel close) while
        // we were awaiting the disk read — drop the result silently.
        if (myTicket != System.Threading.Volatile.Read(ref _logReadTicket)) return;

        RecentLogs = string.IsNullOrWhiteSpace(tail)
            ? _localization.GetString("Str_Settings_LogsEmpty")
            : tail;
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        _ = HandleAutoStartAsync(value);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedLanguageChanged(Language value)
    {
        if (_isInitializing) return;
        _localization.SetLanguage(value);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnUpdateCheckEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnNotifyOnPurgeChanged(bool value)
    {
        if (_isInitializing) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnNotifyOnTimerToggleChanged(bool value)
    {
        if (_isInitializing) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnNotifyOnGameDetectedChanged(bool value)
    {
        if (_isInitializing) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedThemeChanged(Theme value)
    {
        if (_isInitializing) return;
        _themeService.SetTheme(value);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task HandleAutoStartAsync(bool enable)
    {
        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine executable path.");

        bool ok = enable
            ? await _autoStartService.EnableAsync(exePath).ConfigureAwait(true)
            : await _autoStartService.DisableAsync().ConfigureAwait(true);

        if (!ok)
        {
            // Surface the failure so the user sees the toggle didn't actually
            // take effect. Without this, schtasks errors (UAC denial, sandboxed
            // env, malformed path) were swallowed and the checkbox happily
            // showed ON with no scheduled task behind it.
            UpdateStatus = _localization.GetString(
                enable ? "Str_Settings_AutoStartFailedOn"
                       : "Str_Settings_AutoStartFailedOff");
        }
    }

    // ── Export / Import ───────────────────────────────────────────────────────

    [RelayCommand]
    private void ExportSettings()
    {
        if (SnapshotProvider is null) return;

        var dlg = new SaveFileDialog
        {
            FileName = $"StandbyAndTimer_settings_{DateTime.Now:yyyyMMdd}.json",
            Filter   = "JSON (*.json)|*.json"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var snapshot = SnapshotProvider();
            var json = JsonSerializer.Serialize(snapshot, ExportJsonOptions);
            File.WriteAllText(dlg.FileName, json);
            UpdateStatus = _localization.GetString("Str_Settings_ExportOk");
            Logger.Info($"Settings exported to {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Logger.Error("ExportSettings", ex);
            UpdateStatus = ex.Message;
        }
    }

    [RelayCommand]
    private void ImportSettings()
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var imported = JsonSerializer.Deserialize<AppSettings>(json);
            if (imported is null) throw new InvalidDataException("Empty settings file");

            ApplyImportedSettings(imported);
            Logger.Info($"Settings imported from {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Logger.Error("ImportSettings", ex);
            UpdateStatus = _localization.GetString("Str_Settings_ImportFail") + ": " + ex.Message;
        }
    }

    // Everything after the file was read: persist, re-apply to the running
    // session, re-register the hotkeys. Split from ImportSettings so it can
    // be exercised without the file dialog.
    internal void ApplyImportedSettings(AppSettings imported)
    {
        _settingsService.Save(imported);
        SettingsImported?.Invoke(this, imported);

        // The import set the hotkey texts through Initialize (no change
        // events), so the global registrations still held the old combos.
        HotkeyError = RegisterAll()
            ? string.Empty
            : _localization.GetString("Str_Settings_HotkeyTaken");

        UpdateStatus = _localization.GetString("Str_Settings_ImportOk");
    }

    // ── Update check ──────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanCheckUpdate))]
    private async Task CheckUpdateAsync()
    {
        UpdateAvailable = false;
        _pendingAssetUrl = null;
        _pendingVersion  = null;
        _pendingSha256   = null;

        UpdateStatus = _localization.GetString("Str_Settings_UpdateChecking");
        var result = await _updateService.CheckAsync().ConfigureAwait(true);

        if (!result.IsNewer)
        {
            UpdateStatus = _localization.GetString("Str_Settings_UpdateNone");
            return;
        }

        UpdateStatus = string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString("Str_Settings_UpdateAvailable"),
            result.LatestVersion);

        if (!string.IsNullOrWhiteSpace(result.AssetDownloadUrl))
        {
            _pendingAssetUrl = result.AssetDownloadUrl;
            _pendingVersion  = result.LatestVersion;
            _pendingSha256   = result.ExpectedSha256;
            UpdateAvailable  = true;
        }
        else if (!string.IsNullOrWhiteSpace(result.ReleaseUrl))
        {
            // Release found but no .exe asset attached — fall back to opening the
            // release page so the user can grab the binary manually.
            TryOpenUrl(result.ReleaseUrl);
        }
    }

    private bool CanCheckUpdate() => !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDownloadAndInstall))]
    private async Task DownloadAndInstallAsync()
    {
        if (string.IsNullOrWhiteSpace(_pendingAssetUrl)) return;

        IsDownloading = true;
        try
        {
            string targetPath = Path.Combine(
                Path.GetTempPath(),
                $"{UpdateService.DownloadedInstallerPrefix}{_pendingVersion ?? "latest"}.exe");

            var progress = new Progress<double>(p =>
                UpdateStatus = string.Format(
                    CultureInfo.CurrentCulture,
                    _localization.GetString("Str_Settings_Downloading"),
                    (int)p));

            await _updateService
                .DownloadAsync(_pendingAssetUrl!, targetPath, _pendingSha256 ?? string.Empty, progress)
                .ConfigureAwait(true);

            UpdateStatus = _localization.GetString("Str_Settings_LaunchingInstaller");

            Process.Start(new ProcessStartInfo
            {
                FileName        = targetPath,
                UseShellExecute = true
            });

            // Give the user a beat to read the status, then exit so the installer
            // can overwrite the running executable.
            await Task.Delay(800).ConfigureAwait(true);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Logger.Error("DownloadAndInstall", ex);
            UpdateStatus = _localization.GetString("Str_Settings_DownloadFailed") + ": " + ex.Message;
            IsDownloading = false;
        }
    }

    private bool CanDownloadAndInstall() => UpdateAvailable && !IsDownloading;

    private static void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to open URL '{url}': {ex.Message}");
        }
    }

    [RelayCommand]
    private static void OpenLogFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(Logger.LogFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"OpenLogFolder failed: {ex.Message}");
        }
    }
}
