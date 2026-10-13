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

    // Hover / press fill painted here instead of by the base renderer. The
    // app never enables WinForms visual styles (no comctl32 v6 manifest
    // entry, no EnableVisualStyles), and in that mode the professional
    // renderer ignores the color table for selected items: it fills and
    // outlines them with SystemColors.Highlight, i.e. the user's Windows
    // accent (gold/blue/…) instead of our palette.
    protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed)) return;

        var bounds = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
        using var brush = new SolidBrush(e.Item.Pressed ? TrayDarkPalette.PressedBg : TrayDarkPalette.HoverBg);
        e.Graphics.FillRectangle(brush, bounds);
    }

    private sealed class TrayDarkColorTable : WinForms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground       => TrayDarkPalette.SurfaceBg;
        public override Color ImageMarginGradientBegin          => TrayDarkPalette.SurfaceBg;
        public override Color ImageMarginGradientMiddle         => TrayDarkPalette.SurfaceBg;
        public override Color ImageMarginGradientEnd            => TrayDarkPalette.SurfaceBg;
        public override Color MenuBorder                        => TrayDarkPalette.BorderColor;
        // Hover = flat tint, no outline (matches the WPF HoverBg rows).
        public override Color MenuItemBorder                    => TrayDarkPalette.HoverBg;
        public override Color MenuItemSelected                  => TrayDarkPalette.HoverBg;
        public override Color MenuItemSelectedGradientBegin     => TrayDarkPalette.HoverBg;
        public override Color MenuItemSelectedGradientEnd       => TrayDarkPalette.HoverBg;
        public override Color MenuItemPressedGradientBegin      => TrayDarkPalette.PressedBg;
        public override Color MenuItemPressedGradientEnd        => TrayDarkPalette.PressedBg;
        public override Color SeparatorDark                     => TrayDarkPalette.BorderColor;
        public override Color SeparatorLight                    => TrayDarkPalette.BorderColor;
    }
}
