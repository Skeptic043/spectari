namespace Spectari.Ui;

/// <summary>The main window's menu bar, drawn in the window's dark palette.</summary>
internal static class AppMenuBar
{
    private static readonly Color Bg = Color.FromArgb(31, 33, 39);
    private static readonly Color Card = Color.FromArgb(42, 45, 53);
    private static readonly Color Border = Color.FromArgb(60, 65, 74);
    private static readonly Color Fg = Color.FromArgb(212, 216, 222);
    private static readonly Color Highlight = Color.FromArgb(48, 92, 146);

    internal static MenuStrip Create(Action checkForUpdates)
    {
        var checkItem = new ToolStripMenuItem("Check for updates") { ForeColor = Fg };
        checkItem.Click += (_, _) => checkForUpdates();

        var help = new ToolStripMenuItem("Help") { ForeColor = Fg };
        help.DropDownItems.Add(checkItem);

        var menu = new MenuStrip
        {
            Dock = DockStyle.Top,
            BackColor = Bg,
            ForeColor = Fg,
            Renderer = new ToolStripProfessionalRenderer(new DarkColors()),
        };
        menu.Items.Add(help);
        return menu;
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => Bg;
        public override Color MenuStripGradientEnd => Bg;
        public override Color ToolStripDropDownBackground => Card;
        public override Color ImageMarginGradientBegin => Card;
        public override Color ImageMarginGradientMiddle => Card;
        public override Color ImageMarginGradientEnd => Card;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Highlight;
        public override Color MenuItemSelected => Highlight;
        public override Color MenuItemSelectedGradientBegin => Highlight;
        public override Color MenuItemSelectedGradientEnd => Highlight;
        public override Color MenuItemPressedGradientBegin => Card;
        public override Color MenuItemPressedGradientMiddle => Card;
        public override Color MenuItemPressedGradientEnd => Card;
    }
}
