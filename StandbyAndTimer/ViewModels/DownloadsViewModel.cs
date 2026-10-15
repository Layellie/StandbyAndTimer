using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.ViewModels;

public sealed partial class DownloadRow(DownloadEntry entry, string sizeText, string dateText) : ObservableObject
{
    public DownloadEntry Entry    { get; } = entry;
    public string        Name     => Entry.Name;
    public string        SizeText { get; } = sizeText;
    public string        DateText { get; } = dateText;
    public string        Icon     => Entry.IsFolder ? "📁" : "";
    [ObservableProperty] private bool _isChecked;
}

/// <summary>
/// The Downloads dialog: largest first, tick what to send to the Recycle Bin.
/// Exists only while the dialog is open (MaintenanceViewModel drops it).
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    private readonly IDownloadsService _service;
    private readonly ILocalizationService _loc;
    private readonly Action<long, int, int> _moved;
    private readonly CancellationTokenSource _cts = new();

    public DownloadsViewModel(IDownloadsService service, ILocalizationService loc, Action<long, int, int> moved)
    {
        _service = service;
        _loc     = loc;
        _moved   = moved;
    }

    public ObservableCollection<DownloadRow> Items { get; } = [];

    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _selectedText = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    private bool _isMoving;

    internal async Task LoadAsync()
    {
        IsLoading = true;
        Message = _loc.GetString("Str_Dl_Loading");
        foreach (var r in Items) r.PropertyChanged -= OnRowChanged;
        Items.Clear();
        try
        {
            if (_service.FolderPath is null) { Message = _loc.GetString("Str_Dl_NotFound"); return; }
            var list = await _service.ListAsync(_cts.Token);
            var culture = CultureInfo.CurrentCulture;
            foreach (var e in list.OrderByDescending(e => e.Bytes))
            {
                var row = new DownloadRow(e, MaintenanceViewModel.SizeText(e.Bytes), e.Modified.ToString("d MMM yyyy", culture));
                row.PropertyChanged += OnRowChanged;
                Items.Add(row);
            }
            Message = Items.Count == 0 ? _loc.GetString("Str_Dl_Empty") : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warn($"Downloads listing failed: {ex.Message}");
            Message = _loc.GetString("Str_Dl_NotFound");
        }
        finally
        {
            IsLoading = false;
            UpdateSelected();
        }
    }

    /// <summary>The dialog closed: stops a listing still running.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadRow.IsChecked)) UpdateSelected();
    }

    private void UpdateSelected()
    {
        var chosen = Items.Where(i => i.IsChecked).ToList();
        SelectedText = string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Dl_Selected"),
                                     chosen.Count, MaintenanceViewModel.SizeText(chosen.Sum(i => i.Entry.Bytes)));
        MoveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private void SelectAll()  { foreach (var i in Items) i.IsChecked = true; }
    [RelayCommand] private void SelectNone() { foreach (var i in Items) i.IsChecked = false; }

    private bool CanMove() => !IsMoving && Items.Any(i => i.IsChecked);

    [RelayCommand(CanExecute = nameof(CanMove))]
    private async Task MoveAsync()
    {
        IsMoving = true;
        try
        {
            var chosen = Items.Where(i => i.IsChecked).Select(i => i.Entry).ToList();
            var failed = await _service.MoveToRecycleBinAsync(chosen);
            var moved = chosen.Except(failed).ToList();
            _moved(moved.Sum(e => e.Bytes), moved.Count, failed.Count);
            await LoadAsync();
            Message = failed.Count == 0
                ? string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Dl_Moved"), MaintenanceViewModel.SizeText(moved.Sum(e => e.Bytes)))
                : string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Dl_Failed"), failed.Count);
        }
        finally { IsMoving = false; }
    }
}
