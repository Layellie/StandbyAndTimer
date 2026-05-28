using System.Diagnostics;
using System.IO;
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
    private readonly IAutoStartService    _autoStartService;
    private readonly ILocalizationService _localization;
    private readonly ISettingsService     _settingsService;
    private readonly IUpdateService       _updateService;
    private readonly IThemeService        _themeService;

    private bool _isInitializing;

    [ObservableProperty] private bool     _isOpen;
    [ObservableProperty] private bool     _autoStartEnabled;
    [ObservableProperty] private bool     _updateCheckEnabled = true;
    [ObservableProperty] private Language _selectedLanguage = Language.English;
    [ObservableProperty] private Theme    _selectedTheme    = Theme.Dark;
    [ObservableProperty] private string   _updateStatus = string.Empty;

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

    public Language[] LanguageOptions { get; } = [Language.English, Language.Turkish];
    public Theme[]    ThemeOptions    { get; } = [Theme.Dark, Theme.Light, Theme.System];

    // Set by MainViewModel after Initialize so Export/Import can serialize a snapshot.
    internal Func<AppSettings>? SnapshotProvider { get; set; }

    public SettingsViewModel(
        IAutoStartService    autoStartService,
        ILocalizationService localization,
        ISettingsService     settingsService,
        IUpdateService       updateService,
        IThemeService        themeService)
    {
        _autoStartService = autoStartService;
        _localization     = localization;
        _settingsService  = settingsService;
        _updateService    = updateService;
        _themeService     = themeService;
    }

    public void Initialize(bool autoStartEnabled, Language language, bool updateCheckEnabled, Theme theme)
    {
        _isInitializing = true;
        try
        {
            AutoStartEnabled    = autoStartEnabled;
            SelectedLanguage    = language;
            UpdateCheckEnabled  = updateCheckEnabled;
            SelectedTheme       = theme;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

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

        if (enable)
            await _autoStartService.EnableAsync(exePath).ConfigureAwait(false);
        else
            await _autoStartService.DisableAsync().ConfigureAwait(false);
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
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true
            });
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

            _settingsService.Save(imported);
            UpdateStatus = _localization.GetString("Str_Settings_ImportOk");
            Logger.Info($"Settings imported from {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Logger.Error("ImportSettings", ex);
            UpdateStatus = _localization.GetString("Str_Settings_ImportFail") + ": " + ex.Message;
        }
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
                $"StandbyAndTimer_Setup_{_pendingVersion ?? "latest"}.exe");

            var progress = new Progress<double>(p =>
                UpdateStatus = string.Format(
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
    private void OpenLogFolder()
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
