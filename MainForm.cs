using System.Diagnostics;
using System.Text.Json;

namespace CotLModManager;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Headless test mode: CotLModManager.exe --test <gameDir> <reportFile> [sources...]
        if (args.Length >= 3 && args[0] == "--test") return SelfTest(args[1], args[2], args.Skip(3).ToArray());

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    static int SelfTest(string game, string report, string[] sources)
    {
        var store = new ModStore(game);
        using var w = new StreamWriter(report);
        foreach (var s in sources)
        {
            try { w.WriteLine($"INSTALL {Path.GetFileName(s)} -> {string.Join(", ", store.Install(s, _ => true))}"); }
            catch (Exception e) { w.WriteLine($"FAIL {Path.GetFileName(s)}: {e.Message.Replace('\n', ' ')}"); }
        }
        foreach (var m in store.Load())
            w.WriteLine($"{(m.Enabled ? "[x]" : "[ ]")} {m.DisplayName} | {m.VersionText} | {m.FileName} | {m.Status}");
        return 0;
    }
}

class Settings
{
    public string GamePath { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CotLModManager", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

class MainForm : Form
{
    readonly Settings settings = Settings.Load();
    ModStore store;
    List<ModEntry> mods = new();
    bool suppressCheck;

    readonly ListView list;
    readonly Label pathLabel;
    readonly ToolStripStatusLabel statusLeft, statusRight;
    readonly ColumnHeader colName, colVer, colFile, colStatus;

    public MainForm()
    {
        Text = "Cult of the Lamb Mod Manager";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1040, 580);
        MinimumSize = new Size(760, 380);
        AllowDrop = true;
        BackColor = Theme.Back; ForeColor = Theme.Fore;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // --- top: title + game folder ---
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(12, 10, 12, 8), BackColor = Theme.Panel };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new Label { Text = "🐑  Cult of the Lamb · Mods", AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), ForeColor = Theme.Fore, Margin = new Padding(0, 0, 0, 6) };
        top.Controls.Add(title, 0, 0);
        top.SetColumnSpan(title, 3);
        top.Controls.Add(new Label { Text = "Game folder", AutoSize = true, ForeColor = Theme.Dim, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) }, 0, 1);
        pathLabel = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Fore };
        top.Controls.Add(pathLabel, 1, 1);
        top.Controls.Add(Theme.Button("Change…", (_, _) => PickGameFolder()), 2, 1);
        var accentLine = new Panel { Dock = DockStyle.Top, Height = 2, BackColor = Theme.Accent };

        // --- list ---
        list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true,
            HideSelection = false, MultiSelect = true, AllowDrop = true, OwnerDraw = true,
            BorderStyle = BorderStyle.None, BackColor = Theme.ListBack, ForeColor = Theme.Fore,
        };
        colName = list.Columns.Add("Mod", 260);
        colVer = list.Columns.Add("Version", 80);
        colFile = list.Columns.Add("File / folder", 230);
        colStatus = list.Columns.Add("Status", 300);
        list.DrawColumnHeader += DrawHeader;
        list.DrawItem += (_, _) => { };
        list.DrawSubItem += DrawCell;
        list.MouseMove += (_, _) => { };
        list.ItemCheck += OnItemCheck;
        list.Resize += (_, _) => FitColumns();
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) RemoveSelected();
            else if (e.KeyCode == Keys.F5) RefreshList();
        };
        Theme.DarkNative(list);
        list.HandleCreated += (_, _) => Theme.DarkHeader(list);
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 4), BackColor = Theme.Back };
        listHost.Controls.Add(list);

        var menu = new ContextMenuStrip { Renderer = Theme.Renderer, BackColor = Theme.Panel, ForeColor = Theme.Fore, ShowImageMargin = false };
        menu.Items.Add("Enable", null, (_, _) => SetSelected(true));
        menu.Items.Add("Disable", null, (_, _) => SetSelected(false));
        menu.Items.Add("Show in Explorer", null, (_, _) => ShowSelectedInExplorer());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove…", null, (_, _) => RemoveSelected());
        foreach (ToolStripItem i in menu.Items) i.ForeColor = Theme.Fore;
        list.ContextMenuStrip = menu;

        // --- bottom buttons ---
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(9, 4, 9, 8), BackColor = Theme.Back };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        left.Controls.Add(Theme.Button("＋  Install mod (zip / 7z / rar / dll)…", (_, _) => AddFiles(), primary: true));
        left.Controls.Add(Theme.Button("Install from folder…", (_, _) => AddFolder()));
        left.Controls.Add(Theme.Button("Remove", (_, _) => RemoveSelected()));
        left.Controls.Add(Theme.Button("Plugins folder", (_, _) => OpenFolder(store?.PluginsDir)));
        left.Controls.Add(Theme.Button("Config folder", (_, _) => OpenFolder(store == null ? null : Path.Combine(store.BepInExDir, "config"))));
        left.Controls.Add(Theme.Button("⟳ Refresh", (_, _) => RefreshList()));
        bottom.Controls.Add(left, 0, 0);
        bottom.Controls.Add(Theme.Button("▶  Launch game", (_, _) => Launch(), primary: true), 1, 0);

        // --- status bar ---
        var strip = new StatusStrip { Renderer = Theme.Renderer, BackColor = Theme.Panel, SizingGrip = false };
        statusLeft = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Fore };
        statusRight = new ToolStripStatusLabel("Tip: drop archives, DLLs or folders onto this window") { ForeColor = Theme.Dim };
        strip.Items.AddRange(new ToolStripItem[] { statusLeft, statusRight });

        Controls.Add(listHost);
        Controls.Add(accentLine);
        Controls.Add(top);
        Controls.Add(bottom);
        Controls.Add(strip);
        listHost.BringToFront(); // fill the remaining space

        DragEnter += OnDragEnter; list.DragEnter += OnDragEnter;
        DragDrop += OnDragDrop; list.DragDrop += OnDragDrop;

        Shown += (_, _) => InitGame();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }

    void DrawCell(object sender, DrawListViewSubItemEventArgs e)
    {
        var g = e.Graphics;
        var m = (ModEntry)e.Item.Tag;
        bool sel = e.Item.Selected;
        var back = sel ? Theme.Selection : e.SubItem.BackColor;
        using (var b = new SolidBrush(back)) g.FillRectangle(b, e.Bounds);

        var textRect = e.Bounds;
        if (e.ColumnIndex == 0)
        {
            // checkbox, drawn where the native state image sits so clicks still toggle it
            var box = new Rectangle(e.Bounds.X + 5, e.Bounds.Y + (e.Bounds.Height - 14) / 2, 14, 14);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (e.Item.Checked)
            {
                using var fill = new SolidBrush(Theme.Accent);
                g.FillRectangle(fill, box);
                using var pen = new Pen(Color.White, 2f);
                g.DrawLines(pen, new[] { new Point(box.X + 3, box.Y + 7), new Point(box.X + 6, box.Y + 10), new Point(box.X + 11, box.Y + 4) });
            }
            else
            {
                using var pen = new Pen(Theme.Dim, 1.5f);
                g.DrawRectangle(pen, box);
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
            textRect = new Rectangle(e.Bounds.X + 26, e.Bounds.Y, e.Bounds.Width - 26, e.Bounds.Height);
        }
        else textRect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);

        var fore = e.SubItem.ForeColor;
        if (sel && m.Enabled && fore == Theme.Fore) fore = Color.White;
        var font = e.ColumnIndex == 0 && m.Enabled ? boldFont ??= new Font(list.Font, FontStyle.Bold) : list.Font;
        TextRenderer.DrawText(g, e.SubItem.Text, font, textRect, fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    Font boldFont;

    void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var b = new SolidBrush(Theme.Header)) e.Graphics.FillRectangle(b, e.Bounds);
        using (var p = new Pen(Theme.Border))
        {
            e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
        }
        var r = new Rectangle(e.Bounds.X + (e.ColumnIndex == 0 ? 26 : 6), e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.Header.Text, list.Font, r, Theme.Dim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    // ---------- game folder ----------

    void InitGame()
    {
        var path = settings.GamePath;
        if (!ModStore.IsGameDir(path)) path = ModStore.DetectGame();
        if (!ModStore.IsGameDir(path))
        {
            DarkDialog.Show(this, "Couldn't find Cult of the Lamb automatically.\nPlease select the game folder (the one containing \"Cult Of The Lamb.exe\").",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!PickGameFolder()) { Close(); return; }
            return;
        }
        SetGame(path);
    }

    bool PickGameFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select the Cult of the Lamb game folder", UseDescriptionForTitle = true, SelectedPath = store?.Game ?? "" };
        while (dlg.ShowDialog(this) == DialogResult.OK)
        {
            if (ModStore.IsGameDir(dlg.SelectedPath)) { SetGame(dlg.SelectedPath); return true; }
            DarkDialog.Show(this, $"\"{ModStore.GameExe}\" was not found in that folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return store != null;
    }

    void SetGame(string path)
    {
        store = new ModStore(path);
        settings.GamePath = path;
        settings.Save();
        pathLabel.Text = path;
        try { Icon = Icon.ExtractAssociatedIcon(Path.Combine(path, ModStore.GameExe)); } catch { }
        RefreshList();
    }

    // ---------- list ----------

    void RefreshList()
    {
        if (store == null) return;
        var selected = new HashSet<string>(list.SelectedItems.Cast<ListViewItem>().Select(i => ((ModEntry)i.Tag).FileName), StringComparer.OrdinalIgnoreCase);
        mods = store.Load();

        suppressCheck = true;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var m in mods)
        {
            var it = new ListViewItem(m.DisplayName) { Tag = m, Checked = m.Enabled };
            it.SubItems.Add(m.VersionText);
            it.SubItems.Add((m.IsFolder ? "📁 " : "") + m.FileName);
            it.SubItems.Add(m.Status);
            it.UseItemStyleForSubItems = false;
            var color = !m.Enabled ? Theme.Dim : m.Problem ? Theme.Bad : Theme.Fore;
            var back = list.Items.Count % 2 == 0 ? Theme.ListBack : Theme.RowAlt;
            foreach (ListViewItem.ListViewSubItem s in it.SubItems) { s.ForeColor = color; s.BackColor = back; }
            if (m.Enabled && !m.Problem && m.Status == "OK") it.SubItems[3].ForeColor = Theme.Good;
            it.Selected = selected.Contains(m.FileName);
            list.Items.Add(it);
        }
        list.EndUpdate();
        suppressCheck = false;
        FitColumns();

        var bep = store.BepInExVersion();
        int on = mods.Count(m => m.Enabled), problems = mods.Count(m => m.Problem);
        statusLeft.Text = (bep == null ? "⚠ BepInEx not installed!" : $"BepInEx {bep}") +
                          $"   ·   {on} of {mods.Count} mods enabled" +
                          (problems > 0 ? $"   ·   ⚠ {problems} with problems" : "") +
                          (ModStore.GameRunning() ? "   ·   Game is running (changes locked)" : "");
        statusLeft.ForeColor = bep == null || problems > 0 ? Theme.Bad : Theme.Fore;
    }

    void FitColumns()
    {
        int rest = list.ClientSize.Width - colName.Width - colVer.Width - colFile.Width;
        if (rest > 120) colStatus.Width = rest;
    }

    bool EnsureGameClosed()
    {
        if (!ModStore.GameRunning()) return true;
        DarkDialog.Show(this, "Cult of the Lamb is running. Close the game first, then try again.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    void OnItemCheck(object sender, ItemCheckEventArgs e)
    {
        if (suppressCheck) return;
        var m = (ModEntry)list.Items[e.Index].Tag;
        bool enable = e.NewValue == CheckState.Checked;
        if (!TryToggle(m, enable)) e.NewValue = e.CurrentValue;
        BeginInvoke(RefreshList);
    }

    bool TryToggle(ModEntry m, bool enable)
    {
        if (m.Enabled == enable) return true;
        if (!EnsureGameClosed()) return false;
        if (!enable)
        {
            var deps = ModStore.Dependents(mods, m);
            if (deps.Count > 0 && DarkDialog.Show(this,
                    $"{m.DisplayName} is required by:\n\n{string.Join("\n", deps.Select(d => "  • " + d.DisplayName))}\n\nThose mods won't load without it. Disable anyway?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return false;
        }
        try { store.SetEnabled(m, enable); return true; }
        catch (Exception ex) { DarkDialog.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
    }

    List<ModEntry> Selected() => list.SelectedItems.Cast<ListViewItem>().Select(i => (ModEntry)i.Tag).ToList();

    void SetSelected(bool enable)
    {
        foreach (var m in Selected()) if (!TryToggle(m, enable)) break;
        RefreshList();
    }

    void ShowSelectedInExplorer()
    {
        var m = Selected().FirstOrDefault();
        if (m != null) Process.Start("explorer.exe", $"/select,\"{m.Path}\"");
    }

    void RemoveSelected()
    {
        var sel = Selected();
        if (sel.Count == 0 || !EnsureGameClosed()) return;
        var msg = $"Move to the Recycle Bin?\n\n{string.Join("\n", sel.Select(m => $"  • {m.DisplayName}  [{m.FileName}]"))}\n\n(Config files in BepInEx\\config are kept.)";
        if (DarkDialog.Show(this, msg, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var m in sel)
            try { ModStore.Recycle(m.Path); }
            catch (Exception ex) { DarkDialog.Show(this, $"{m.FileName}: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        RefreshList();
    }

    // ---------- adding mods ----------

    void AddFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select mod archive(s) or DLL(s)",
            Filter = "Mods (*.zip;*.7z;*.rar;*.dll)|*.zip;*.7z;*.rar;*.dll|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) InstallMany(dlg.FileNames);
    }

    void AddFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select an extracted mod folder", UseDescriptionForTitle = true,
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) InstallMany(new[] { dlg.SelectedPath });
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void OnDragDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) BeginInvoke(() => InstallMany(paths));
    }

    void InstallMany(IEnumerable<string> sources)
    {
        if (store == null || !EnsureGameClosed()) return;
        var ok = new List<string>();
        var errors = new List<string>();
        Cursor = Cursors.WaitCursor;
        try
        {
            foreach (var s in sources)
            {
                if (!ModStore.IsInstallable(s)) { errors.Add($"{Path.GetFileName(s)}: unsupported file type"); continue; }
                try
                {
                    var installed = store.Install(s, msg => DarkDialog.Show(this, msg, "Replace existing mod?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);
                    ok.AddRange(installed);
                }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(s)}:\n{ex.Message}"); }
            }
        }
        finally { Cursor = Cursors.Default; }

        RefreshList();
        if (ok.Count == 0 && errors.Count == 0) return;

        var lines = new List<string>();
        if (ok.Count > 0)
        {
            lines.Add("Installed:");
            foreach (var name in ok)
            {
                var m = mods.FirstOrDefault(x => x.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
                lines.Add($"  • {(m?.DisplayName ?? name)} {m?.VersionText}");
                if (m != null && m.Problem) lines.Add($"      ⚠ {m.Status}");
            }
        }
        if (errors.Count > 0) { lines.Add(""); lines.Add("Failed:"); lines.AddRange(errors.Select(x => "  • " + x)); }
        DarkDialog.Show(this, string.Join("\n", lines), Text, MessageBoxButtons.OK,
            errors.Count > 0 || mods.Any(m => m.Problem) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    // ---------- misc ----------

    static void OpenFolder(string path)
    {
        if (path == null) return;
        Directory.CreateDirectory(path);
        Process.Start("explorer.exe", $"\"{path}\"");
    }

    void Launch()
    {
        if (ModStore.GameRunning()) { DarkDialog.Show(this, "The game is already running.", Text); return; }
        Process.Start(new ProcessStartInfo($"steam://rungameid/{ModStore.SteamAppId}") { UseShellExecute = true });
    }
}
