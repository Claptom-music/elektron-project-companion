using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

sealed class Companion : Form
{
    const string Filter = "Elektron project backups|*.arprj;*.dt2prj;*.syx";
    readonly Color surface = Color.FromArgb(29, 32, 28);
    readonly Color muted = Color.FromArgb(180, 190, 174);
    readonly Color accent = Color.FromArgb(204, 244, 129);
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    readonly ComboBox projects = new ComboBox();
    readonly ListView library = new ListView();
    readonly DataGridView grid = new DataGridView();
    readonly Label status = new Label();
    readonly Label templateText = new Label();
    readonly Label info = new Label();
    readonly CheckBox all = new CheckBox();
    readonly List<Dictionary<string, object>> sources = new List<Dictionary<string, object>>();
    readonly Stack<Placement[]> undo = new Stack<Placement[]>();
    readonly List<Button> actions = new List<Button>();
    Placement[] placements = new Placement[128];
    Dictionary<string, object> template;
    Placement selectedSource;
    int selectedSlot = -1, dragSlot = -1;
    Point dragStart;
    Process service;
    Button lockButton, exportButton;
    bool busy, refreshing;
    static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;

    sealed class Placement
    {
        public int project { get; set; }
        public int slot { get; set; }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 1 && args[0] == "--smoke")
        {
            using (var app = new Companion())
            {
                app.StartService();
                if (app.Request(Data("op", "import", "files", new string[0])).ContainsKey("sources") == false)
                    Environment.ExitCode = 1;
            }
            return;
        }
        Application.Run(new Companion());
    }

    public Companion()
    {
        Text = "Elektron Project Companion";
        MinimumSize = new Size(920, 650);
        Size = new Size(1420, 820);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 20, 17);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10);
        KeyPreview = true;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 59));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        var title = new Label { Text = "Elektron Project Companion", Font = new Font("Segoe UI Semibold", 23),
            ForeColor = accent, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        shell.Controls.Add(title, 0, 0);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        Add(bar, "Import backups", ImportFiles);
        Add(bar, "Open backup folder", ImportFolder);
        Add(bar, "Blank template", PickTemplate);
        Add(bar, "Open workspace", OpenWorkspace);
        Add(bar, "Save workspace", SaveWorkspace);
        exportButton = Add(bar, "Export project", Export);
        exportButton.BackColor = accent;
        exportButton.ForeColor = Color.FromArgb(20, 30, 10);
        Add(bar, "Open Transfer", OpenTransfer);
        shell.Controls.Add(bar, 0, 1);
        templateText.Dock = DockStyle.Fill;
        templateText.TextAlign = ContentAlignment.MiddleLeft;
        templateText.ForeColor = muted;
        shell.Controls.Add(templateText, 0, 2);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 295));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(9), BackColor = surface,
            ColumnCount = 1, RowCount = 6, Margin = new Padding(0, 0, 11, 0) };
        foreach (int h in new[] { 26, 31, 31, 28, -1, 86 })
            left.RowStyles.Add(h < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, h));
        left.Controls.Add(new Label { Text = "SOURCE PROJECTS", ForeColor = accent, Dock = DockStyle.Fill }, 0, 0);
        projects.Dock = DockStyle.Fill;
        projects.DropDownStyle = ComboBoxStyle.DropDownList;
        projects.SelectedIndexChanged += delegate { if (!refreshing) { selectedSource = null; RenderLibrary(); } };
        left.Controls.Add(projects, 0, 1);
        info.ForeColor = muted;
        info.Dock = DockStyle.Fill;
        left.Controls.Add(info, 0, 2);
        all.Text = "Show all 128 slots";
        all.Dock = DockStyle.Fill;
        all.CheckedChanged += delegate { RenderLibrary(); };
        left.Controls.Add(all, 0, 3);
        library.Dock = DockStyle.Fill;
        library.BackColor = Color.FromArgb(21, 23, 20);
        library.ForeColor = Color.WhiteSmoke;
        library.View = View.Details;
        library.FullRowSelect = true;
        library.MultiSelect = false;
        library.HideSelection = false;
        library.Columns.Add("Slot", 52);
        library.Columns.Add("Pattern / kit", 155);
        library.Columns.Add("Trigs", 52);
        library.SelectedIndexChanged += delegate {
            if (refreshing || library.SelectedItems.Count == 0) return;
            selectedSource = (Placement)library.SelectedItems[0].Tag;
            selectedSlot = -1;
            Tell("Selected " + LabelFor(selectedSource.slot) + ". Click an empty destination.");
        };
        library.ItemDrag += delegate(object s, ItemDragEventArgs e) {
            if (busy) return;
            selectedSource = (Placement)((ListViewItem)e.Item).Tag;
            library.DoDragDrop("elektron-source", DragDropEffects.Copy);
        };
        left.Controls.Add(library, 0, 4);
        var sourceActions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        Add(sourceActions, "Add active", AddActive);
        Add(sourceActions, "Save kit folders", Kits);
        lockButton = Add(sourceActions, "Load Rytm lockfile", PickLock);
        left.Controls.Add(sourceActions, 0, 5);
        layout.Controls.Add(left, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = surface, Padding = new Padding(9) };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var gridBar = new FlowLayoutPanel { Dock = DockStyle.Fill };
        gridBar.Controls.Add(new Label { Text = "DESTINATION BANKS  A–H", ForeColor = accent, AutoSize = true,
            Margin = new Padding(4, 11, 14, 4) });
        Add(gridBar, "Undo", Undo);
        Add(gridBar, "Remove selected", Remove);
        right.Controls.Add(gridBar, 0, 0);
        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = surface;
        grid.BorderStyle = BorderStyle.None;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.RowHeadersWidth = 42;
        grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;
        grid.ColumnHeadersHeight = 28;
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        grid.RowTemplate.Height = 51;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = grid.RowHeadersDefaultCellStyle.BackColor = surface;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = grid.RowHeadersDefaultCellStyle.ForeColor = muted;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(69, 88, 44);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        for (int i = 0; i < 16; i++) grid.Columns.Add(new DataGridViewTextBoxColumn {
            Name = "slot" + i, HeaderText = (i + 1).ToString("00"), Width = 65, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Rows.Add(8);
        for (int i = 0; i < 8; i++) grid.Rows[i].HeaderCell.Value = ((char)('A' + i)).ToString();
        grid.CellClick += delegate(object s, DataGridViewCellEventArgs e) {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0) ChooseCell(e.RowIndex * 16 + e.ColumnIndex);
        };
        grid.AllowDrop = true;
        grid.DragOver += delegate(object s, DragEventArgs e) {
            string data = e.Data.GetData(DataFormats.Text) as string;
            e.Effect = data == "elektron-source" ? DragDropEffects.Copy : data == "elektron-grid" ? DragDropEffects.Move : DragDropEffects.None;
        };
        grid.DragDrop += delegate(object s, DragEventArgs e) {
            var point = grid.PointToClient(new Point(e.X, e.Y));
            var cell = grid.HitTest(point.X, point.Y);
            if (cell.RowIndex < 0 || cell.ColumnIndex < 0) return;
            string data = e.Data.GetData(DataFormats.Text) as string;
            if (data == "elektron-grid" && dragSlot >= 0) { selectedSource = null; selectedSlot = dragSlot; }
            if (data == "elektron-source" || data == "elektron-grid") ChooseCell(cell.RowIndex * 16 + cell.ColumnIndex);
        };
        grid.MouseDown += delegate(object s, MouseEventArgs e) {
            var cell = grid.HitTest(e.X, e.Y);
            dragSlot = cell.RowIndex >= 0 && cell.ColumnIndex >= 0 ? cell.RowIndex * 16 + cell.ColumnIndex : -1;
            dragStart = e.Location;
        };
        grid.MouseMove += delegate(object s, MouseEventArgs e) {
            if (busy || dragSlot < 0 || placements[dragSlot] == null || e.Button != MouseButtons.Left) return;
            if (Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y) > 9)
            {
                grid.DoDragDrop("elektron-grid", DragDropEffects.Move);
                dragSlot = -1;
            }
        };
        right.Controls.Add(grid, 0, 1);
        layout.Controls.Add(right, 1, 0);
        shell.Controls.Add(layout, 0, 3);
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        shell.Controls.Add(status, 0, 4);
        Controls.Add(shell);
        KeyDown += delegate(object s, KeyEventArgs e) {
            if (e.KeyCode == Keys.Delete) Remove();
            if (e.Control && e.KeyCode == Keys.Z) Undo();
            if (e.KeyCode == Keys.Escape) { selectedSource = null; selectedSlot = -1; RenderGrid(); }
        };
        Shown += delegate {
            try { StartService(); }
            catch (Exception error) { Tell(error.Message, true); }
        };
        Render();
        Tell("Import .arprj or .dt2prj backups. Select or drag patterns into the destination grid.");
    }

    Button Add(FlowLayoutPanel bar, string caption, Action action)
    {
        var button = new Button { Text = caption, AutoSize = true, Height = 33, FlatStyle = FlatStyle.Flat,
            BackColor = surface, ForeColor = Color.WhiteSmoke, Margin = new Padding(3, 3, 4, 3) };
        button.FlatAppearance.BorderColor = Color.FromArgb(65, 73, 61);
        button.Click += delegate { if (!busy) action(); };
        bar.Controls.Add(button);
        actions.Add(button);
        return button;
    }

    static string LabelFor(int slot) { return ((char)('A' + slot / 16)).ToString() + (slot % 16 + 1).ToString("00"); }
    static Dictionary<string, object> Data(params object[] values)
    {
        var result = new Dictionary<string, object>();
        for (int i = 0; i < values.Length; i += 2) result.Add((string)values[i], values[i + 1]);
        return result;
    }
    static string Get(Dictionary<string, object> item, string key) { return Convert.ToString(item[key]); }
    static int Int(Dictionary<string, object> item, string key) { return Convert.ToInt32(item[key]); }
    static IEnumerable<object> Items(object value) { return ((IEnumerable)value).Cast<object>(); }
    static string Device(string type) { return type == "dt2prj" ? "Digitakt II" : "Analog Rytm MKII"; }
    static Dictionary<string, object> Map(object value) { return (Dictionary<string, object>)value; }
    Dictionary<string, object> Pattern(Placement p) { return Map(((IList)sources[p.project]["patterns"])[p.slot]); }

    void Tell(string text, bool error = false) { status.Text = text; status.ForeColor = error ? Color.LightSalmon : muted; }
    void Remember() { undo.Push((Placement[])placements.Clone()); }

    void Render()
    {
        refreshing = true;
        int choice = projects.SelectedIndex;
        projects.Items.Clear();
        foreach (var source in sources) projects.Items.Add(Device(Get(source, "format")) + " · " + Get(source, "name"));
        if (sources.Count > 0) projects.SelectedIndex = Math.Min(Math.Max(choice, 0), sources.Count - 1);
        refreshing = false;
        templateText.Text = template == null ? "Choose a blank project for the device you’re arranging."
            : "TEMPLATE   " + Device(Get(template, "format")) + "  ·  " + Get(template, "name");
        lockButton.Visible = template != null && Get(template, "format") == "arprj" && Int(template, "version") == 9;
        RenderLibrary();
        RenderGrid();
    }

    void RenderLibrary()
    {
        refreshing = true;
        library.BeginUpdate();
        library.Items.Clear();
        int index = projects.SelectedIndex;
        info.Text = "Select a backup to browse its patterns.";
        if (index >= 0)
        {
            int slot = 0, activeCount = 0;
            foreach (var value in Items(sources[index]["patterns"]))
            {
                var p = Map(value);
                bool active = (bool)p["active"];
                if (active) activeCount++;
                if (active || all.Checked)
                {
                    var row = new ListViewItem(new[] { LabelFor(slot), Get(p, "name"), Get(p, "trigs") });
                    row.Tag = new Placement { project = index, slot = slot };
                    library.Items.Add(row);
                }
                slot++;
            }
            info.Text = activeCount + " active patterns   ·   " + Get(sources[index], "firmware");
        }
        library.EndUpdate();
        refreshing = false;
    }

    void RenderGrid()
    {
        for (int i = 0; i < placements.Length; i++)
        {
            var cell = grid.Rows[i / 16].Cells[i % 16];
            var p = placements[i];
            cell.Value = p == null ? LabelFor(i) : LabelFor(i) + "\n" + Get(Pattern(p), "name");
            cell.ToolTipText = p == null ? "Empty " + LabelFor(i) :
                Get(sources[p.project], "name") + " / " + LabelFor(p.slot) + " / " + Get(Pattern(p), "name");
            cell.Style.BackColor = i == selectedSlot ? Color.FromArgb(70, 91, 45) :
                p == null ? Color.FromArgb(23, 25, 22) : Color.FromArgb(44, 59, 33);
            cell.Style.ForeColor = p == null ? muted : accent;
        }
        exportButton.Text = template == null ? "Export project" : "Export ." + Get(template, "format");
    }

    bool Fits(Placement p)
    {
        string format = Get(sources[p.project], "format");
        if (template != null && format != Get(template, "format")) return false;
        return placements.Where(x => x != null).All(x => Get(sources[x.project], "format") == format);
    }

    void ChooseCell(int slot)
    {
        if (busy) return;
        if (selectedSource != null)
        {
            if (placements[slot] != null) { Tell("Choose an empty destination.", true); return; }
            if (!Fits(selectedSource)) { Tell("Use the same project format for the arrangement and template.", true); return; }
            Remember();
            placements[slot] = selectedSource;
            selectedSource = null;
        }
        else if (selectedSlot >= 0 && selectedSlot != slot)
        {
            Remember();
            var other = placements[slot];
            placements[slot] = placements[selectedSlot];
            placements[selectedSlot] = other;
            selectedSlot = -1;
        }
        else
        {
            selectedSlot = placements[slot] != null && selectedSlot != slot ? slot : -1;
            if (selectedSlot >= 0) Tell("Selected " + LabelFor(slot) + ". Click another slot to move or swap.");
            RenderGrid();
            return;
        }
        RenderGrid();
        Tell(placements.Count(x => x != null) + " / 128 slots placed.");
    }

    void AddActive()
    {
        int source = projects.SelectedIndex;
        if (source < 0) return;
        if (!Fits(new Placement { project = source, slot = 0 })) { Tell("The source and arrangement must use the same project format.", true); return; }
        var used = new HashSet<int>(placements.Where(p => p != null && p.project == source).Select(p => p.slot));
        var candidates = Items(sources[source]["patterns"]).Select((p, i) => new { p = Map(p), i })
            .Where(x => (bool)x.p["active"] && !used.Contains(x.i)).Select(x => x.i).ToList();
        var free = Enumerable.Range(0, 128).Where(i => placements[i] == null).ToList();
        int count = Math.Min(free.Count, candidates.Count);
        if (count > 0) Remember();
        for (int i = 0; i < count; i++) placements[free[i]] = new Placement { project = source, slot = candidates[i] };
        selectedSource = null;
        selectedSlot = -1;
        RenderGrid();
        Tell("Added " + count + " active patterns.");
    }

    void Undo()
    {
        if (busy || undo.Count == 0) return;
        placements = undo.Pop();
        selectedSlot = -1;
        selectedSource = null;
        RenderGrid();
        Tell("Undid the last arrangement edit.");
    }

    void Remove()
    {
        if (busy || selectedSlot < 0 || placements[selectedSlot] == null) return;
        Remember();
        placements[selectedSlot] = null;
        selectedSlot = -1;
        RenderGrid();
        Tell("Removed the selected pattern from the arrangement.");
    }

    static string Node()
    {
        string overridePath = Environment.GetEnvironmentVariable("ELEKTRON_COMPANION_NODE");
        if (!String.IsNullOrEmpty(overridePath)) return overridePath;
        string local = Path.Combine(Root, "node.exe");
        if (File.Exists(local)) return local;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { string found = Path.Combine(dir.Trim('"'), "node.exe"); if (File.Exists(found)) return found; }
            catch (ArgumentException) { }
        }
        try
        {
            foreach (string dir in Directory.GetDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
                "OpenAI.CodexPrimaryRuntime.*").OrderByDescending(s => s))
            {
                string found = Path.Combine(dir, @"dependencies\node\bin\node.exe");
                if (File.Exists(found)) return found;
            }
        }
        catch (UnauthorizedAccessException) { }
        return "node.exe";
    }

    void StartService()
    {
        string core = Path.Combine(Root, "project-core.cjs");
        if (!File.Exists(core)) throw new IOException("Keep project-core.cjs beside the companion executable.");
        service = Process.Start(new ProcessStartInfo(Node(), "\"" + core + "\"") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 });
        service.ErrorDataReceived += delegate { };
        service.BeginErrorReadLine();
    }

    Dictionary<string, object> Request(Dictionary<string, object> message)
    {
        if (service == null || service.HasExited) throw new IOException("The project engine is unavailable. Install Node.js 24 or set ELEKTRON_COMPANION_NODE and reopen the app.");
        service.StandardInput.WriteLine(json.Serialize(message));
        service.StandardInput.Flush();
        string line = service.StandardOutput.ReadLine();
        if (line == null) throw new IOException("The project engine stopped. Reopen the companion.");
        var result = json.Deserialize<Dictionary<string, object>>(line);
        if (!(bool)result["ok"]) throw new IOException(Get(result, "error"));
        return Map(result["value"]);
    }

    async void Call(Dictionary<string, object> message, Action<Dictionary<string, object>> done)
    {
        busy = true;
        foreach (var button in actions) button.Enabled = false;
        Tell("Working…");
        try
        {
            var result = await Task.Run(() => Request(message));
            if (!IsDisposed) done(result);
        }
        catch (Exception error) { if (!IsDisposed) Tell(error.Message, true); }
        finally
        {
            busy = false;
            if (!IsDisposed) foreach (var button in actions) button.Enabled = true;
        }
    }

    void SetState(Dictionary<string, object> result)
    {
        sources.Clear();
        sources.AddRange(Items(result["sources"]).Select(Map));
        template = Map(result["template"]);
        selectedSlot = -1;
        selectedSource = null;
        Render();
    }

    void Import(string[] paths)
    {
        if (paths.Length == 0) { Tell("No project backups found in that folder.", true); return; }
        Call(Data("op", "import", "files", paths), result => {
            SetState(result);
            var errors = Items(result["errors"]).Cast<string>().ToList();
            Tell("Imported " + Int(result, "added") + ". Skipped " + Int(result, "empty") + " unused slots."
                + (errors.Count > 0 ? "  " + errors.Count + " unreadable. " + errors[0] : ""), errors.Count > 0);
        });
    }

    void ImportFiles()
    {
        using (var dialog = new OpenFileDialog { Filter = Filter, Multiselect = true })
            if (dialog.ShowDialog(this) == DialogResult.OK) Import(dialog.FileNames);
    }

    void ImportFolder()
    {
        using (var dialog = new FolderBrowserDialog { Description = "Choose a folder of local Elektron backups" })
            if (dialog.ShowDialog(this) == DialogResult.OK)
                Import(Directory.GetFiles(dialog.SelectedPath).Where(f => new[] { ".arprj", ".dt2prj", ".syx" }
                    .Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f).ToArray());
    }

    string FileToOpen(string filter)
    {
        using (var dialog = new OpenFileDialog { Filter = filter })
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    string FileToSave(string extension, string name)
    {
        using (var dialog = new SaveFileDialog { FileName = name, Filter = "Save file|*." + extension, DefaultExt = extension, AddExtension = true, OverwritePrompt = false })
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    void PickTemplate()
    {
        string file = FileToOpen(Filter);
        if (file == null) return;
        string format = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
        if (placements.Any(x => x != null && Get(sources[x.project], "format") != format))
        {
            Tell("The blank template must use the same project format as your arrangement.", true);
            return;
        }
        Call(Data("op", "template", "file", file), result => { SetState(result); Tell("Blank template ready."); });
    }

    void PickLock()
    {
        string file = FileToOpen("Elekloader storage lockfile|*.json");
        if (file != null) Call(Data("op", "lock", "file", file), result => { SetState(result); Tell("Rytm storage lockfile loaded."); });
    }

    void SaveWorkspace()
    {
        string file = FileToSave("json", "Elektron set.json");
        if (file != null) Call(Data("op", "save", "file", file, "grid", placements), result => Tell("Saved workspace to " + file));
    }

    void OpenWorkspace()
    {
        string file = FileToOpen("Elektron workspace|*.json");
        if (file != null) Call(Data("op", "open", "file", file), result => {
            placements = Items(result["grid"]).Select(x => x == null ? null :
                new Placement { project = Int(Map(x), "project"), slot = Int(Map(x), "slot") }).ToArray();
            undo.Clear();
            SetState(result);
            Tell("Opened " + Path.GetFileName(file));
        });
    }

    void Export()
    {
        if (template == null || placements.All(p => p == null)) { Tell("Choose a blank template and place at least one pattern.", true); return; }
        string format = Get(template, "format");
        string file = FileToSave(format, (format == "dt2prj" ? "Digitakt II" : "Rytm") + " Merged." + format);
        if (file != null) Call(Data("op", "export", "file", file, "grid", placements), result =>
            Tell("Saved " + Path.GetFileName(file) + (result.ContainsKey("bundled") ? " · " + Int(result, "bundled") + " bundled samples" : "")
            + (result.ContainsKey("missing") && Int(result, "missing") > 0 ? " · " + Int(result, "missing") + " sample identities need audio on the +Drive" : "")
            + ". Open Transfer to restore."));
    }

    void Kits()
    {
        if (projects.SelectedIndex < 0) { Tell("Import and select a source device first.", true); return; }
        string device = Get(sources[projects.SelectedIndex], "format") == "dt2prj" ? "dt2prj" : "arprj";
        string file = FileToSave("zip", Device(device) + " Kit Folders.zip");
        if (file != null) Call(Data("op", "kits", "file", file, "device", device), result =>
            Tell("Saved " + Int(result, "count") + " kit records. Extract the ZIP to browse the project folders."));
    }

    void OpenTransfer()
    {
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            string app = Path.Combine(root, @"Elektron Transfer\Transfer.exe");
            if (File.Exists(app)) { try { Process.Start(app); } catch (Exception e) { Tell(e.Message, true); } return; }
        }
        Tell("Elektron Transfer was not found in its standard installation folder.", true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && service != null)
        {
            try { if (!service.HasExited) service.Kill(); } catch (InvalidOperationException) { }
            service.Dispose();
        }
        base.Dispose(disposing);
    }
}

