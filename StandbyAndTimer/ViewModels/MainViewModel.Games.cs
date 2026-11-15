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
    // The Game Mode list: which listed games run right now and what that
    // triggers, what the app found out about a game (refused the priority,
    // not on disk), the per-game rules, and adding / removing / starting games.

    // ── What the app finds out about a game while it runs ───────────────────

    // Which listed games refused the priority change at this tick (anti-cheat
    // protected ones do). Said once in the status line when a game starts
    // refusing; the list row's tooltip and the "game started" notification
    // read the mark on the game.
    private bool _anyBoostRefused;

    private void ApplyBoostRefusals(IReadOnlyCollection<string> refused)
    {
        if (refused.Count == 0 && !_anyBoostRefused) return;   // the usual tick
        _anyBoostRefused = refused.Count > 0;
        foreach (var game in Games)
        {
            bool refusedNow = refused.Contains(game.ExecutablePath, StringComparer.OrdinalIgnoreCase);
            if (refusedNow && !game.BoostRefused)
                StatusMessage = string.Format(CultureInfo.CurrentCulture,
                    _localization.GetString("Str_Status_BoostRefused"), game.DisplayName);
            game.BoostRefused = refusedNow;
        }
    }

    // Tests set what exists; the app asks the file system.
    internal Func<string, bool> GameFileExists { get; set; } = File.Exists;

    /// <summary>
    /// Looks again which listed games are there (the window calls it when it
    /// is shown: a drive may have been plugged in since). Off the UI thread:
    /// asking a sleeping disk can take a moment. Nothing checks on a timer.
    /// </summary>
    public async Task RefreshMissingGamesAsync()
    {
        var games  = Games.ToList();
        var exists = GameFileExists;
        try
        {
            bool[] found = await Task.Run(() => games.Select(g => exists(g.ExecutablePath)).ToArray()).ConfigureAwait(true);
            for (int i = 0; i < games.Count; i++) games[i].IsMissing = !found[i];
        }
        catch (Exception ex)
        {
            Logger.Warn($"Looking for the listed games failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Launch / open folder found out by trying.
    private bool IsThere(GameEntry game)
    {
        bool there = GameFileExists(game.ExecutablePath);
        game.IsMissing = !there;
        return there;
    }

    // ── Listed games starting and exiting (every monitor tick) ──────────────

    /// <summary>
    /// Per tick: lets the in-game lock act, then announces each listed game
    /// that was not running at the last tick.
    /// </summary>
    internal async Task HandleRunningGamesAsync(IReadOnlyList<string> running)
    {
        // The usual tick: no listed game runs and none did a second ago.
        if (running.Count == 0 && _runningGames.Count == 0)
        {
            await ApplyGameStateAsync(false).ConfigureAwait(true);
            return;
        }

        var started = running.Where(p => !_runningGames.Contains(p)).ToList();
        var exited  = _runningGames.Where(p => !running.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        _runningGames.Clear();
        _runningGames.UnionWith(running);

        // The IN GAME lock follows only the games that want the timer
        // ("Timer on while it runs"); a game removed from the list meanwhile counts as one.
        bool timerGameRunning = running.Any(p =>
            Games.FirstOrDefault(g => g.ExecutablePath.Equals(p, StringComparison.OrdinalIgnoreCase))?.TimerInGame != false);
        await ApplyGameStateAsync(timerGameRunning).ConfigureAwait(true);

        bool purgeNow = false;
        foreach (string path in started)
        {
            var game = Games.FirstOrDefault(g => g.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (game is null) continue;   // removed from the list meanwhile
            game.IsMissing = false;       // it runs, so it is there
            purgeNow |= game.PurgeOnStart;
            GameStartedNotification?.Invoke(this, new GameStartedArgs(game.DisplayName, DescribeGameStart(game)));
            Logger.Info($"Game started: {game.DisplayName}");
        }

        // "Purge once when it starts": one purge for the tick even if two such
        // games start together, in the chosen mode, without AUTO PURGE.
        if (purgeNow)
            await _purgeService.PurgeAsync(PurgeTrigger.GameStart).ConfigureAwait(true);

        // "Purge once: at exit": frees the cache the game left behind; one
        // purge for the tick even if two such games close together.
        bool purgeAtExit = false;
        foreach (string path in exited)
        {
            var game = Games.FirstOrDefault(g => g.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (game is null) continue;   // removed from the list meanwhile
            purgeAtExit |= game.PurgeOnExit;
            Logger.Info($"Game exited: {game.DisplayName}");
        }
        if (purgeAtExit)
            await _purgeService.PurgeAsync(PurgeTrigger.GameExit).ConfigureAwait(true);
    }

    // "High priority · Timer on (0.50 ms) · Auto purge paused": only the
    // features that are switched on, as they apply to this game right now.
    private string DescribeGameStart(GameEntry game)
    {
        var parts = new List<string>(3);
        if (GameModeEnabled)
            parts.Add(_localization.GetString(game.BoostPriority && game.BoostRefused
                ? "Str_GameNotify_BoostRefused"
                : game.Priority switch
                {
                    GamePriority.High        => "Str_GameNotify_Boost",
                    GamePriority.AboveNormal => "Str_GameNotify_AboveNormal",
                    _                        => "Str_GameNotify_NoBoost",
                }));
        parts.Add(TimerActive
            ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Str_GameNotify_TimerOn"), ActualTimerMs)
            : _localization.GetString("Str_GameNotify_TimerOff"));
        if (AutoPurgeEnabled)
            parts.Add(_localization.GetString(game.AllowAutoPurge ? "Str_GameNotify_PurgeOn" : "Str_GameNotify_PurgePaused"));
        if (game.PurgeOnStart)
            parts.Add(_localization.GetString("Str_GameNotify_PurgeAtStart"));
        return string.Join(" · ", parts);
    }

    // ── Fullscreen detection adds a game ────────────────────────────────────

    // Detection already filtered out known non-games and momentary overlays
    // (two consecutive fullscreen polls); what reaches here is added to the
    // list, tagged AUTO, and persisted. Removing it later dismisses it for good.
    private void OnGameDetected(object? sender, string exePath) =>
        Application.Current.Dispatcher.InvokeAsync(() => AutoAddDetectedGame(exePath));

    internal void AutoAddDetectedGame(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;
        if (IsGameAlreadyListed(exePath, includeIgnored: true)) return;

        string name = Path.GetFileNameWithoutExtension(exePath);
        Games.Add(new GameEntry { DisplayName = name, ExecutablePath = exePath, AutoAdded = true });
        SyncMonitorGames();
        PersistSettings();
        StatusMessage = string.Format(CultureInfo.CurrentCulture,
            _localization.GetString("Str_Status_GameAutoAdded"), name);
        Logger.Info($"Game auto-added: {exePath}");

        // App.xaml.cs owns the tray icon and shows the "added" balloon.
        GameAutoDetected?.Invoke(this, exePath);
    }

    public event EventHandler<string>? GameAutoDetected;

    // ── Per-game rules of the selected game ─────────────────────────────────

    // The strip under the Game Mode list. Each change re-syncs the monitor
    // and persists.

    partial void OnSelectedGameChanged(GameEntry? value)
    {
        RemoveGameCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedGamePriority));
        OnPropertyChanged(nameof(SelectedGameAutoPurge));
        OnPropertyChanged(nameof(SelectedGamePurgeOnce));
        OnPropertyChanged(nameof(SelectedGameTimerInGame));
    }

    /// <summary>The Priority menu's choices, in menu order.</summary>
    public IReadOnlyList<GamePriority> GamePriorities { get; } =
        [GamePriority.High, GamePriority.AboveNormal, GamePriority.Unchanged];

    /// <summary>Applied the next time the game starts (a running, boosted game keeps its class).</summary>
    public GamePriority SelectedGamePriority
    {
        get => SelectedGame?.Priority ?? GamePriority.High;
        set => SetSelectedGameRule(g => g.Priority = value);
    }

    public bool SelectedGameAutoPurge
    {
        get => SelectedGame?.AllowAutoPurge ?? true;
        set => SetSelectedGameRule(g => g.AllowAutoPurge = value);
    }

    /// <summary>The Purge once menu's choices, in menu order.</summary>
    public IReadOnlyList<PurgeOnceOption> PurgeOnceOptions { get; } =
        [PurgeOnceOption.Off, PurgeOnceOption.AtStart, PurgeOnceOption.AtExit, PurgeOnceOption.Both];

    /// <summary>One menu over the game's two switches: purge once at start and / or at exit.</summary>
    public PurgeOnceOption SelectedGamePurgeOnce
    {
        get => SelectedGame is not { } g ? PurgeOnceOption.Off
             : (g.PurgeOnStart, g.PurgeOnExit) switch
               {
                   (true, true)  => PurgeOnceOption.Both,
                   (true, false) => PurgeOnceOption.AtStart,
                   (false, true) => PurgeOnceOption.AtExit,
                   _             => PurgeOnceOption.Off,
               };
        set => SetSelectedGameRule(g =>
        {
            g.PurgeOnStart = value is PurgeOnceOption.AtStart or PurgeOnceOption.Both;
            g.PurgeOnExit  = value is PurgeOnceOption.AtExit  or PurgeOnceOption.Both;
        });
    }

    /// <summary>The IN GAME lock turns the timer on while this game runs (applied at the next monitor tick).</summary>
    public bool SelectedGameTimerInGame
    {
        get => SelectedGame?.TimerInGame ?? true;
        set => SetSelectedGameRule(g => g.TimerInGame = value);
    }

    private void SetSelectedGameRule(Action<GameEntry> apply)
    {
        if (SelectedGame is null) return;
        apply(SelectedGame);
        OnPropertyChanged(nameof(SelectedGamePriority));
        OnPropertyChanged(nameof(SelectedGameAutoPurge));
        OnPropertyChanged(nameof(SelectedGamePurgeOnce));
        OnPropertyChanged(nameof(SelectedGameTimerInGame));
        SyncMonitorGames();
        PersistSettings();
    }

    // ── Adding, starting and removing games ─────────────────────────────────

    [RelayCommand]
    private void AddGame()
    {
        var dlg = new OpenFileDialog
        {
            Title  = _localization.GetString("Str_Game_FileDialogTitle"),
            Filter = _localization.GetString("Str_Game_FileDialogFilter")
        };
        if (dlg.ShowDialog() != true) return;
        AddGameFromPath(dlg.FileName);
    }

    // Tests swap in a fake; the app starts games through the shell.
    internal Func<string, bool> StartGame { get; set; } = GameLauncher.StartUnelevated;
    internal Func<string, bool> RevealGameInExplorer { get; set; } = DownloadsViewModel.OpenExplorerSelect;
    internal Func<CancellationToken, Task<IReadOnlyList<InstalledGameCandidate>>> ScanInstalledGames { get; set; } = InstalledGamesScanner.ScanAsync;

    /// <summary>Double-click on a listed game: start it, not elevated (see GameLauncher).</summary>
    public void LaunchGame(GameEntry game)
    {
        if (!IsThere(game))
        {
            StatusMessage = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Status_GameMissing"), game.DisplayName);
            return;
        }
        bool ok = StartGame(game.ExecutablePath);
        StatusMessage = string.Format(CultureInfo.CurrentCulture,
            _localization.GetString(ok ? "Str_Status_GameLaunching" : "Str_Status_GameLaunchFailed"), game.DisplayName);
    }

    [RelayCommand]
    private void LaunchSelectedGame(GameEntry? game)
    {
        if ((game ?? SelectedGame) is { } target)
            LaunchGame(target);
    }

    [RelayCommand]
    private void OpenGameFolder(GameEntry? game)
    {
        if ((game ?? SelectedGame) is not { } target) return;
        if (!IsThere(target))
        {
            StatusMessage = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Status_GameMissing"), target.DisplayName);
            return;
        }
        RevealGameInExplorer(target.ExecutablePath);
    }

    [RelayCommand]
    private void RemoveSpecificGame(GameEntry? game)
    {
        if ((game ?? SelectedGame) is not { } target) return;
        SelectedGame = target;
        RemoveGame();
    }

    [RelayCommand]
    private async Task OpenInstalledGamesAsync()
    {
        var dialog = new InstalledGamesViewModel(
            ScanInstalledGames,
            c => IsGameAlreadyListed(c.ExecutablePath, c.InstallDirectory, includeIgnored: true),
            _localization,
            OnInstalledGamesChosen);
        InstalledGamesDialog = dialog;
        await dialog.LoadAsync();
    }

    [RelayCommand]
    private void CloseInstalledGames()
    {
        InstalledGamesDialog?.Dispose();
        InstalledGamesDialog = null;
    }

    private void OnInstalledGamesChosen(IReadOnlyList<InstalledGameCandidate> candidates)
    {
        int added = 0;
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.ExecutablePath) || !File.Exists(c.ExecutablePath)) continue;
            if (IsGameAlreadyListed(c.ExecutablePath, c.InstallDirectory)) continue;

            Games.Add(new GameEntry
            {
                DisplayName    = Path.GetFileNameWithoutExtension(c.ExecutablePath),
                ExecutablePath = c.ExecutablePath
            });
            _ignoredGamePaths.Remove(c.ExecutablePath);
            added++;
        }

        CloseInstalledGames();
        if (added > 0)
        {
            SyncMonitorGames();
            PersistSettings();
            StatusMessage = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Status_GamesScannedAdded"), added);
            Logger.Info($"Added {added} game(s) from library scan");
        }
    }

    public void AddGameFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;

        if (IsGameAlreadyListed(path))
        {
            StatusMessage = _localization.GetString("Str_Status_GameExists");
            return;
        }

        Games.Add(new GameEntry
        {
            DisplayName    = Path.GetFileNameWithoutExtension(path),
            ExecutablePath = path
        });
        // An explicit add overrides an earlier dismissal of the same exe.
        _ignoredGamePaths.Remove(path);
        SyncMonitorGames();
        PersistSettings();
        StatusMessage = _localization.GetString("Str_Status_GameAdded");
        Logger.Info($"Game added: {path}");
    }

    // ── Is this game already in the list? ───────────────────────────────────

    /// <summary>
    /// True when <paramref name="exePath"/> matches a game already in <see cref="Games"/>
    /// (including AUTO-added games) by exact path, executable filename, or shared
    /// Steam/Epic install root directory.
    /// </summary>
    internal bool IsGameAlreadyListed(string exePath, string? installDir = null, bool includeIgnored = false)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        if (includeIgnored && _ignoredGamePaths.Any(ig => AreSameGameExecutable(ig, exePath, installDir)))
            return true;

        return Games.Any(g => AreSameGameExecutable(g.ExecutablePath, exePath, installDir));
    }

    internal static bool AreSameGameExecutable(string existingPath, string candidatePath, string? candidateInstallDir = null)
    {
        if (string.IsNullOrWhiteSpace(existingPath) || string.IsNullOrWhiteSpace(candidatePath))
            return false;

        if (existingPath.Equals(candidatePath, StringComparison.OrdinalIgnoreCase))
            return true;

        string existingFile = Path.GetFileName(existingPath);
        string candidateFile = Path.GetFileName(candidatePath);
        if (!string.IsNullOrEmpty(existingFile) && existingFile.Equals(candidateFile, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(candidateInstallDir) && IsPathUnderDirectory(existingPath, candidateInstallDir))
            return true;

        string? rootA = TryGetLibraryGameRoot(existingPath);
        string? rootB = TryGetLibraryGameRoot(candidatePath);
        if (rootA is not null && rootB is not null && rootA.Equals(rootB, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsPathUnderDirectory(string filePath, string dirPath)
    {
        string normDir = dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        string normFile = filePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normFile.StartsWith(normDir, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetLibraryGameRoot(string exePath)
    {
        string norm = exePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        foreach (string marker in new[] { @"\steamapps\common\", @"\Epic Games\" })
        {
            int idx = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            int folderStart = idx + marker.Length;
            int nextSep = norm.IndexOf(Path.DirectorySeparatorChar, folderStart);
            if (nextSep > folderStart)
                return norm[..nextSep];
        }
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveGame))]
    private void RemoveGame()
    {
        if (SelectedGame is null) return;
        // Removing an auto-added game means "this is not a game": remember it
        // so detection never adds it again. User-added games just go.
        if (SelectedGame.AutoAdded)
            _ignoredGamePaths.Add(SelectedGame.ExecutablePath);
        Games.Remove(SelectedGame);
        SelectedGame  = null;
        SyncMonitorGames();
        PersistSettings();
        StatusMessage = _localization.GetString("Str_Status_GameRemoved");
    }

    private bool CanRemoveGame() => SelectedGame is not null;
}
