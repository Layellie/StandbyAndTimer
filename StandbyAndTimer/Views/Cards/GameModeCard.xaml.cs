using System.Windows;
using System.Windows.Controls;

namespace StandbyAndTimer.Views.Cards;

// Files dropped on the ListBox bubble up via the GameDropped event so the
// MainWindow host can forward each path to MainViewModel.AddGameFromPath.
// This card stays free of any direct ViewModel coupling beyond the DataContext
// inherited from the parent.
public partial class GameModeCard : UserControl
{
    public event EventHandler<string>? GameDropped;

    public GameModeCard() => InitializeComponent();

    private void GamesList_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void GamesList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var f in files)
            GameDropped?.Invoke(this, f);
        e.Handled = true;
    }
}
