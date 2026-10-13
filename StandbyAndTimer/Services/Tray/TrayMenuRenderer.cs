using System.Drawing;
using WinForms = System.Windows.Forms;

namespace StandbyAndTimer.Services.Tray;

// Renders the WinForms ContextMenuStrip in one of our palettes (dark or
// light, following the app theme). The default ProfessionalRenderer ignores
// BackColor/ForeColor on the menu and uses a stock Office-style grey theme,
// which clashes with the app's UI; this subclass overrides the color table
// + draws text with our foreground.
internal sealed class TrayMenuRenderer : WinForms.ToolStripProfessionalRenderer
{
    private readonly TrayMenuPalette _palette;

    public TrayMenuRenderer(TrayMenuPalette palette) : base(new TrayColorTable(palette))
    {
        _palette     = palette;
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _palette.AppFg : _palette.DisabledFg;
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
        using var brush = new SolidBrush(e.Item.Pressed ? _palette.PressedBg : _palette.HoverBg);
        e.Graphics.FillRectangle(brush, bounds);
    }

    private sealed class TrayColorTable(TrayMenuPalette p) : WinForms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground       => p.SurfaceBg;
        public override Color ImageMarginGradientBegin          => p.SurfaceBg;
        public override Color ImageMarginGradientMiddle         => p.SurfaceBg;
        public override Color ImageMarginGradientEnd            => p.SurfaceBg;
        public override Color MenuBorder                        => p.BorderColor;
        // Hover = flat tint, no outline (matches the WPF HoverBg rows).
        public override Color MenuItemBorder                    => p.HoverBg;
        public override Color MenuItemSelected                  => p.HoverBg;
        public override Color MenuItemSelectedGradientBegin     => p.HoverBg;
        public override Color MenuItemSelectedGradientEnd       => p.HoverBg;
        public override Color MenuItemPressedGradientBegin      => p.PressedBg;
        public override Color MenuItemPressedGradientEnd        => p.PressedBg;
        public override Color SeparatorDark                     => p.BorderColor;
        public override Color SeparatorLight                    => p.BorderColor;
    }
}
