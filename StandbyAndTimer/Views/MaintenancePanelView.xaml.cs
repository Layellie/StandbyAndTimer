using System.Windows.Controls;

namespace StandbyAndTimer.Views;

// The Maintenance tab page. Built by MainWindow's TabItem.ContentTemplate
// only when the tab is selected (so it costs nothing at start-up).
public partial class MaintenancePanelView : UserControl
{
    public MaintenancePanelView() => InitializeComponent();
}
