using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Views.Cards;

// Files dropped on the ListBox bubble up via the GameDropped event so the
// MainWindow host can forward each path to MainViewModel.AddGameFromPath.
// This card stays free of any direct ViewModel coupling beyond the DataContext
// inherited from the parent.
public partial class GameModeCard : UserControl
{
    public event EventHandler<string>? GameDropped;

    // Double-click on a row (not the empty list area) = launch that game.
    public event EventHandler<GameEntry>? GameLaunchRequested;

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

    private void GamesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ItemsControl.ContainerFromElement(GamesList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: GameEntry game })
        {
            GameLaunchRequested?.Invoke(this, game);
            e.Handled = true;
        }
    }
}
