using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace StandbyAndTimer.Views.Overlays;

// Hosts the two-button "Run in Background / Exit" dialog. MainWindow.OnClosing
// cancels the close and calls Show(); the buttons raise events back to the
// host which decides what to do (hide to tray vs. real shutdown).
public partial class CloseConfirmationOverlay : UserControl
{
    public event EventHandler? MinimizeRequested;
    public event EventHandler? ExitRequested;

    public CloseConfirmationOverlay() => InitializeComponent();

    public void Show()
    {
        Visibility = Visibility.Visible;
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    public void Hide() => Visibility = Visibility.Collapsed;

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        Hide();
        MinimizeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Hide();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
