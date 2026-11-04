using static Spectari.Ui.Palette;

namespace Spectari.Ui;

/// <summary>The main window's menu bar, drawn in the window's dark palette.</summary>
internal static class AppMenuBar
{
    internal static MenuStrip Create(Action openSettings, Action checkForUpdates, Action showReleaseNotes, Action sendFeedback)
    {
        ToolStripMenuItem settings = Item("Settings", openSettings);

        var help = new ToolStripMenuItem("Help") { ForeColor = Fg };
        help.DropDownItems.Add(Item("Check for updates", checkForUpdates));
        help.DropDownItems.Add(Item("Release notes", showReleaseNotes));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(Item("Send feedback on GitHub...", sendFeedback));

        var menu = new MenuStrip
        {
            Dock = DockStyle.Top,
            BackColor = Bg,
            ForeColor = Fg,
            Renderer = new ToolStripProfessionalRenderer(new DarkColors()),
        };
        menu.Items.Add(settings);
        menu.Items.Add(help);
        return menu;
    }

    private static ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { ForeColor = Fg };
        item.Click += (_, _) => onClick();
        return item;
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
        public override Color MenuItemBorder => AccentDark;
        public override Color MenuItemSelected => AccentDark;
        public override Color MenuItemSelectedGradientBegin => AccentDark;
        public override Color MenuItemSelectedGradientEnd => AccentDark;
        public override Color MenuItemPressedGradientBegin => Card;
        public override Color MenuItemPressedGradientMiddle => Card;
        public override Color MenuItemPressedGradientEnd => Card;
    }
}
