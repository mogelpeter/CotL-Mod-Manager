using System.Runtime.InteropServices;

namespace CotLModManager;

/// <summary>Dark colour scheme + helpers to apply it to WinForms controls.</summary>
static class Theme
{
    public static readonly Color Back = Color.FromArgb(24, 24, 28);
    public static readonly Color Panel = Color.FromArgb(32, 32, 38);
    public static readonly Color ListBack = Color.FromArgb(20, 20, 24);
    public static readonly Color RowAlt = Color.FromArgb(27, 27, 32);
    public static readonly Color Selection = Color.FromArgb(70, 38, 44);
    public static readonly Color Header = Color.FromArgb(38, 38, 45);
    public static readonly Color Border = Color.FromArgb(58, 58, 68);
    public static readonly Color Hover = Color.FromArgb(52, 52, 62);
    public static readonly Color Fore = Color.FromArgb(232, 232, 236);
    public static readonly Color Dim = Color.FromArgb(135, 135, 148);
    public static readonly Color Accent = Color.FromArgb(178, 34, 52);       // cult red
    public static readonly Color AccentHover = Color.FromArgb(206, 48, 68);
    public static readonly Color Good = Color.FromArgb(96, 200, 128);
    public static readonly Color Bad = Color.FromArgb(255, 110, 110);
    public static readonly Color Warn = Color.FromArgb(240, 190, 90);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    /// <summary>Dark title bar (Windows 10 1809+ / 11).</summary>
    public static void DarkTitleBar(Form f)
    {
        int on = 1;
        if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
    }

    /// <summary>Dark scrollbars / checkboxes for native controls.</summary>
    public static void DarkNative(Control c)
    {
        if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        else c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
    }

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>Dark native header for a details ListView (covers the area right of the last column).</summary>
    public static void DarkHeader(ListView lv)
    {
        var header = SendMessage(lv.Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
        if (header != IntPtr.Zero) SetWindowTheme(header, "DarkMode_ItemsView", null);
    }

    public static Button Button(string text, EventHandler click, bool primary = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Padding = new Padding(8, 3, 8, 3), Margin = new Padding(3),
            BackColor = primary ? Accent : Panel, ForeColor = Fore, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Border;
        b.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Hover;
        b.FlatAppearance.MouseDownBackColor = primary ? Accent : Border;
        if (primary) b.Font = new Font(b.Font, FontStyle.Bold);
        b.Click += click;
        return b;
    }

    public static ToolStripProfessionalRenderer Renderer => new(new DarkColors()) { RoundedEdges = false };

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color StatusStripGradientBegin => Panel;
        public override Color StatusStripGradientEnd => Panel;
        public override Color ToolStripBorder => Panel;
    }
}

/// <summary>Dark replacement for MessageBox.</summary>
sealed class DarkDialog : Form
{
    DarkDialog(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Back; ForeColor = Theme.Fore;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var (glyph, color) = icon switch
        {
            MessageBoxIcon.Error => ("✖", Theme.Bad),
            MessageBoxIcon.Warning => ("⚠", Theme.Warn),
            MessageBoxIcon.Question => ("?", Theme.Accent),
            _ => ("ℹ", Theme.Good),
        };

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 2, Dock = DockStyle.Fill };
        grid.Controls.Add(new Label
        {
            Text = glyph, ForeColor = color, AutoSize = true, Font = new Font("Segoe UI Symbol", 22f),
            Margin = new Padding(0, 0, 14, 0),
        }, 0, 0);
        grid.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(620, 0), Margin = new Padding(0, 6, 0, 14) }, 1, 0);

        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0) };
        void Add(string t, DialogResult r, bool primary)
        {
            var b = Theme.Button(t, (_, _) => { DialogResult = r; Close(); }, primary);
            b.MinimumSize = new Size(90, 0);
            row.Controls.Add(b);
            if (primary) AcceptButton = b;
            if (r is DialogResult.No or DialogResult.Cancel || buttons == MessageBoxButtons.OK) CancelButton = b;
        }
        // RightToLeft flow: add the rightmost button first.
        switch (buttons)
        {
            case MessageBoxButtons.YesNo: Add("No", DialogResult.No, false); Add("Yes", DialogResult.Yes, true); break;
            case MessageBoxButtons.OKCancel: Add("Cancel", DialogResult.Cancel, false); Add("OK", DialogResult.OK, true); break;
            default: Add("OK", DialogResult.OK, true); break;
        }
        grid.Controls.Add(row, 0, 1);
        grid.SetColumnSpan(row, 2);
        Controls.Add(grid);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }

    public static DialogResult Show(IWin32Window owner, string text, string title,
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.Information)
    {
        using var d = new DarkDialog(text, title, buttons, icon);
        return d.ShowDialog(owner);
    }
}
