using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.ViewModels;

public sealed partial class InstalledGameRow : ObservableObject
{
    public InstalledGameRow(InstalledGameCandidate candidate, bool alreadyAdded, string statusBadge)
    {
        Candidate    = candidate;
        AlreadyAdded = alreadyAdded;
        StatusBadge  = statusBadge;
        _isChecked   = !alreadyAdded;
    }

    public InstalledGameCandidate Candidate    { get; }
    public string                 Name         => Candidate.Name;
    public string                 ExecutablePath => Candidate.ExecutablePath;
    public string                 Source       => Candidate.Source;
    public bool                   AlreadyAdded { get; }
    public bool                   CanSelect    => !AlreadyAdded;
    public string                 StatusBadge  { get; }

    [ObservableProperty] private bool _isChecked;
}

/// <summary>
/// Modal picker that scans Steam and Epic libraries for installed games and
/// adds the ticked ones to the Game Mode list.
/// </summary>
public sealed partial class InstalledGamesViewModel : ObservableObject, IDisposable
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<InstalledGameCandidate>>> _scanner;
    private readonly Func<InstalledGameCandidate, bool> _isAlreadyListed;
    private readonly ILocalizationService _loc;
    private readonly Action<IReadOnlyList<InstalledGameCandidate>> _onAdd;
    private readonly CancellationTokenSource _cts = new();

    internal InstalledGamesViewModel(
        Func<CancellationToken, Task<IReadOnlyList<InstalledGameCandidate>>> scanner,
        Func<InstalledGameCandidate, bool> isAlreadyListed,
        ILocalizationService loc,
        Action<IReadOnlyList<InstalledGameCandidate>> onAdd)
    {
        _scanner         = scanner;
        _isAlreadyListed = isAlreadyListed;
        _loc             = loc;
        _onAdd           = onAdd;
    }

    internal InstalledGamesViewModel(
        Func<CancellationToken, Task<IReadOnlyList<InstalledGameCandidate>>> scanner,
        IReadOnlySet<string> existingPaths,
        ILocalizationService loc,
        Action<IReadOnlyList<InstalledGameCandidate>> onAdd)
        : this(scanner, c => existingPaths.Contains(c.ExecutablePath), loc, onAdd)
    {
    }

    public ObservableCollection<InstalledGameRow> Items { get; } = [];

    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _selectedText = "";

    internal async Task LoadAsync()
    {
        IsLoading = true;
        Message   = _loc.GetString("Str_Scan_Loading");
        foreach (var r in Items) r.PropertyChanged -= OnRowChanged;
        Items.Clear();
        try
        {
            var found = await _scanner(_cts.Token);
            foreach (var c in found)
            {
                if (_isAlreadyListed(c)) continue;
                var row = new InstalledGameRow(c, alreadyAdded: false, c.Source);
                row.PropertyChanged += OnRowChanged;
                Items.Add(row);
            }
            Message = Items.Count == 0 ? _loc.GetString("Str_Scan_Empty") : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warn($"InstalledGamesViewModel.LoadAsync: {ex.Message}");
            Message = _loc.GetString("Str_Scan_Empty");
        }
        finally
        {
            IsLoading = false;
            UpdateSelected();
        }
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledGameRow.IsChecked))
            UpdateSelected();
    }

    private void UpdateSelected()
    {
        int count = Items.Count(i => i.CanSelect && i.IsChecked);
        SelectedText = string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Scan_Selected"), count);
        AddSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in Items)
            if (i.CanSelect) i.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in Items)
            if (i.CanSelect) i.IsChecked = false;
    }

    private bool CanAddSelected() => Items.Any(i => i.CanSelect && i.IsChecked);

    [RelayCommand(CanExecute = nameof(CanAddSelected))]
    private void AddSelected()
    {
        var chosen = Items.Where(i => i.CanSelect && i.IsChecked).Select(i => i.Candidate).ToList();
        _onAdd(chosen);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
