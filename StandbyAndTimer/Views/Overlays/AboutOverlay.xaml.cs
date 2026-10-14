using System.Windows.Controls;
using System.Windows.Input;

namespace StandbyAndTimer.Views.Overlays;

public partial class AboutOverlay : UserControl
{
    public AboutOverlay() => InitializeComponent();

    // The overlay's LeftClick binding closes the dialog; stop clicks inside
    // the dialog from bubbling up to it, so reading, selecting or dragging
    // the guide's scrollbar never closes it. The Close button still works:
    // a Button handles its own click before this.
    private void DialogMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
