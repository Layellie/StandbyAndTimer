using System.Drawing;
using WinForms = System.Windows.Forms;

namespace StandbyAndTimer.Services.Tray;

// Renders the WinForms ContextMenuStrip in our dark palette. The default
// ProfessionalRenderer ignores BackColor/ForeColor on the menu and uses a
// stock Office-style grey theme, which clashes with the app's dark UI;
// this subclass overrides the color table + draws text with our foreground.
internal sealed class TrayDarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
{
    public TrayDarkMenuRenderer() : base(new TrayDarkColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? TrayDarkPalette.AppFg : TrayDarkPalette.DisabledFg;
        base.OnRenderItemText(e);
    }

    private sealed class TrayDarkColorTable : WinForms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground       => TrayDarkPalette.SurfaceBg;
        public override Color ImageMarginGradientBegin          => TrayDarkPalette.SurfaceBg;
        public override Color ImageMarginGradientMiddle         => TrayDarkPalette.SurfaceBg;
        public override Color ImageMarginGradientEnd            => TrayDarkPalette.SurfaceBg;
        public override Color MenuBorder                        => TrayDarkPalette.BorderColor;
        public override Color MenuItemBorder                    => TrayDarkPalette.AccentPrimary;
        public override Color MenuItemSelected                  => TrayDarkPalette.SelectionBg;
        public override Color MenuItemSelectedGradientBegin     => TrayDarkPalette.SelectionBg;
        public override Color MenuItemSelectedGradientEnd       => TrayDarkPalette.SelectionBg;
        public override Color MenuItemPressedGradientBegin      => TrayDarkPalette.AccentPressed;
        public override Color MenuItemPressedGradientEnd        => TrayDarkPalette.AccentPressed;
        public override Color SeparatorDark                     => TrayDarkPalette.BorderColor;
        public override Color SeparatorLight                    => TrayDarkPalette.BorderColor;
    }
}
