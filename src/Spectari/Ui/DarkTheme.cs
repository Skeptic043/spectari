using System.Runtime.InteropServices;
using static Spectari.Ui.Palette;

namespace Spectari.Ui;

/// <summary>Applies the app's dark look to windows and their controls.</summary>
internal static class DarkTheme
{
    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Gives the window a dark title bar. Call from OnHandleCreated or after the handle exists.</summary>
    internal static void UseDarkTitleBar(Form form)
    {
        int on = 1;
        _ = DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref on, sizeof(int));
    }

    /// <summary>Styles a dialog and its controls, including a dark title bar once the handle exists.</summary>
    internal static void ApplyToDialog(Form dialog)
    {
        dialog.BackColor = Bg;
        dialog.ForeColor = Fg;
        dialog.HandleCreated += (_, _) => UseDarkTitleBar(dialog);
        Apply(dialog);
    }

    internal static void Style(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.BackColor = Card;
        button.ForeColor = Fg;
    }

    internal static void Apply(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case Button b:
                    Style(b);
                    break;
                case ComboBox cb:
                    cb.BackColor = Card;
                    cb.ForeColor = Fg;
                    break;
                case NumericUpDown n:
                    n.BackColor = Card;
                    n.ForeColor = Fg;
                    n.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case CheckBox or RadioButton:
                    c.ForeColor = Fg;
                    break;
                case TextBox t:
                    t.BackColor = Card;
                    t.ForeColor = Fg;
                    break;
            }
            if (c.HasChildren) Apply(c);
        }
    }
}
