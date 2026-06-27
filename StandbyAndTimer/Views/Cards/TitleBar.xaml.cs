using System.Windows;
using System.Windows.Controls;

namespace StandbyAndTimer.Views.Cards;

// Window-chrome buttons that need access to the parent Window (Minimize, Close)
// raise events the host listens for. Keeping the actual WindowState change in
// MainWindow.xaml.cs means this control doesn't have to walk the visual tree
// up to find its hosting Window — and it stays trivially reusable in tests.
public partial class TitleBar : UserControl
{
    public event EventHandler? MinimizeRequested;
    public event EventHandler? CloseRequested;

    public TitleBar() => InitializeComponent();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        MinimizeRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
