using System.Windows.Controls;
using System.Windows.Input;

namespace StandbyAndTimer.Views.Overlays;

public partial class UpdateNotesOverlay : UserControl
{
    public UpdateNotesOverlay() => InitializeComponent();

    // Clicks inside the dialog must not reach the overlay's "Later" binding.
    private void DialogMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
