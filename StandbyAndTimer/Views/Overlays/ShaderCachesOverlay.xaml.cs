using System.Windows.Controls;
using System.Windows.Input;

namespace StandbyAndTimer.Views.Overlays;

public partial class ShaderCachesOverlay : UserControl
{
    public ShaderCachesOverlay() => InitializeComponent();

    // Clicks inside the dialog must not reach the overlay's close binding.
    private void DialogMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
