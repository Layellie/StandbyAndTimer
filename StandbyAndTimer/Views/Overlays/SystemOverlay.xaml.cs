using System.Windows.Controls;
using System.Windows.Input;

namespace StandbyAndTimer.Views.Overlays;

public partial class SystemOverlay : UserControl
{
    public SystemOverlay() => InitializeComponent();

    // Clicks inside the panel must not reach the overlay's close binding.
    private void DialogMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
