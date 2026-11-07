using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle(Companion.ProjectName)]
[assembly: System.Reflection.AssemblyProduct(Companion.ProjectName)]
[assembly: System.Reflection.AssemblyVersion("0.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.1.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("0.1.0-beta")]

sealed class Companion : Form
{
    internal const string ProjectName = "EPC: Electron Project Companion";
    const string ReleaseLabel = "Beta 0.1";
    internal static readonly Color Muted = Color.FromArgb(180, 190, 174);
    readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill, Margin = Padding.Empty };
    readonly Label status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly ProjectEditor[] editors;
    ProjectEditor ActiveEditor { get { return editors[tabs.SelectedIndex]; } }
    string transferPath;
    bool confirmingClose, closeApproved;
    static readonly string TransferLinkFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Elektron Project Companion", "transfer-path.txt");

    [STAThread]
    public static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 1 && args[0] == "--smoke")
        {
            using (var app = new Companion())
                if (!app.editors.All(editor => editor.CheckEngine())) Environment.ExitCode = 1;
            return;
        }
        Application.Run(new Companion());
    }

    public Companion()
    {
        Text = ProjectName;
        MinimumSize = new Size(920, 650);
        var screenSize = Screen.FromPoint(Cursor.Position).WorkingArea.Size;
        Size = new Size(Math.Min(1420, screenSize.Width), Math.Min(820, screenSize.Height));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 20, 17);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10);
        KeyPreview = true;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 59));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        shell.Controls.Add(new Label { Text = ProjectName, Font = new Font("Segoe UI Semibold", 23),
            ForeColor = Color.FromArgb(204, 244, 129), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        editors = new[] { new ProjectEditor(this, false), new ProjectEditor(this, true) };
        foreach (var editor in editors)
        {
            var page = new TabPage(editor.DeviceName) { BackColor = BackColor, ForeColor = ForeColor, Padding = new Padding(6) };
            page.Controls.Add(editor);
            tabs.TabPages.Add(page);
        }
        tabs.SelectedIndex = 0;
        shell.Controls.Add(tabs, 0, 1);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(new Label { Text = ReleaseLabel, AutoSize = true, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight, ForeColor = Muted }, 1, 0);
        shell.Controls.Add(footer, 0, 2);
        Controls.Add(shell);
        tabs.SelectedIndexChanged += delegate { RefreshStatus(); };
        KeyDown += delegate(object sender, KeyEventArgs args) { if (!confirmingClose) ActiveEditor.HandleShortcut(args); };
        Shown += delegate {
            transferPath = LoadTransferPath(TransferLinkFile);
            if (!IsTransferPath(transferPath)) PickTransfer();
        };
        RefreshStatus();
    }

    internal void RefreshStatus()
    {
        if (tabs.SelectedIndex < 0) return;
        status.Text = ActiveEditor.StatusText;
        status.ForeColor = ActiveEditor.StatusColor;
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || closeApproved) return;
        if (confirmingClose) { e.Cancel = true; return; }
        var working = Array.FindIndex(editors, editor => editor.IsWorking);
        if (working >= 0)
        {
            e.Cancel = true;
            tabs.SelectedIndex = working;
            Tell("Wait for the current operation to finish before closing.", true);
            return;
        }
        if (!editors.Any(editor => editor.HasUnsavedChanges)) return;

        e.Cancel = true;
        confirmingClose = true;
        tabs.Enabled = false;
        try
        {
            for (int i = 0; i < editors.Length; i++)
            {
                var editor = editors[i];
                if (!editor.HasUnsavedChanges) continue;
                tabs.SelectedIndex = i;
                var choice = MessageBox.Show(this, "Save changes to the " + editor.DeviceName
                    + " workspace before closing?\n\nChoose No to discard its unsaved changes.",
                    ProjectName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (choice == DialogResult.Cancel || (choice == DialogResult.Yes && !await editor.SaveWorkspace())) return;
            }
            closeApproved = true;
            BeginInvoke((Action)Close);
        }
        finally
        {
            confirmingClose = false;
            tabs.Enabled = true;
        }
    }

    void Tell(string text, bool error = false) { ActiveEditor.Tell(text, error); }

    static bool IsTransferPath(string file)
    {
        return !String.IsNullOrEmpty(file) && File.Exists(file)
            && String.Equals(Path.GetFileName(file), "Transfer.exe", StringComparison.OrdinalIgnoreCase);
    }

    static string LoadTransferPath(string linkFile)
    {
        try { return File.ReadAllText(linkFile).Trim(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static string InstalledTransferPath()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            string root = Environment.GetFolderPath(folder);
            if (String.IsNullOrEmpty(root)) continue;
            string candidate = Path.Combine(root, @"Elektron Transfer\Transfer.exe");
            if (IsTransferPath(candidate)) return candidate;
        }
        return null;
    }

    internal bool PickTransfer()
    {
        string suggested = IsTransferPath(transferPath) ? transferPath : InstalledTransferPath();
        using (var dialog = new OpenFileDialog {
            Title = "Link Elektron Transfer — select Transfer.exe",
            Filter = "Elektron Transfer (Transfer.exe)|Transfer.exe",
            FileName = "Transfer.exe",
            InitialDirectory = suggested == null ? "" : Path.GetDirectoryName(suggested),
            CheckFileExists = true,
            RestoreDirectory = true
        })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            if (!IsTransferPath(dialog.FileName))
            {
                Tell("Select Transfer.exe from your Elektron Transfer installation.", true);
                return false;
            }
            transferPath = dialog.FileName;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TransferLinkFile));
            File.WriteAllText(TransferLinkFile, transferPath);
            Tell("Elektron Transfer linked. Use Project → Open Transfer to launch it.");
        }
        catch (IOException error)
        {
            Tell("Transfer is linked for this session. Could not save the link. " + error.Message, true);
        }
        catch (UnauthorizedAccessException error)
        {
            Tell("Transfer is linked for this session. Could not save the link. " + error.Message, true);
        }
        return true;
    }

    internal void OpenTransfer()
    {
        if (!IsTransferPath(transferPath) && !PickTransfer())
        {
            Tell("Link Transfer.exe using Project → Link Elektron Transfer to open it from the companion.", true);
            return;
        }
        try
        {
            using (Process.Start(new ProcessStartInfo(transferPath) {
                WorkingDirectory = Path.GetDirectoryName(transferPath), UseShellExecute = true
            })) { }
            Tell("Opened Elektron Transfer.");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            Tell("Could not open Transfer. Use Project → Link Elektron Transfer to check the path. " + error.Message, true);
        }
    }
}

sealed class ProjectEditor : UserControl
{
    readonly Companion host;
    readonly bool digitakt;
    string BackupFilter { get { return digitakt ? "Digitakt II projects|*.dt2prj" : "Analog Rytm MKII backups|*.arprj;*.syx"; } }
    internal string DeviceName { get { return digitakt ? "Digitakt II" : "Analog Rytm MKII"; } }
    internal string StatusText { get; private set; }
    internal Color StatusColor { get; private set; }
    internal bool IsWorking { get { return busy; } }
    internal bool HasUnsavedChanges
    {
        get { return workspaceSettingsChanged || placements.Where((p, i) => !SamePlacement(p, savedPlacements[i])).Any(); }
    }
    readonly Color surface = Color.FromArgb(29, 32, 28);
    readonly Color muted = Companion.Muted;
    readonly Color accent = Color.FromArgb(204, 244, 129);
    readonly Font keyLabelFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
    readonly Font keyNameFont = new Font("Segoe UI", 8f);
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    readonly ComboBox projects = new ComboBox();
    readonly ListView library = new ListView();
    readonly DataGridView grid = new DataGridView();
    readonly Label templateText = new Label();
    readonly Label info = new Label();
    readonly CheckBox all = new CheckBox();
    readonly List<Dictionary<string, object>> sources = new List<Dictionary<string, object>>();
    readonly Stack<Placement[]> undo = new Stack<Placement[]>();
    readonly List<Button> actions = new List<Button>();
    readonly ContextMenuStrip projectMenu = new ContextMenuStrip();
    Placement[] placements = new Placement[128];
    Placement[] savedPlacements = new Placement[128];
    bool workspaceSettingsChanged;
    Dictionary<string, object> template;
    Dictionary<string, object> firmwareProfile;
    bool hasFirmwareLock;
    Placement selectedSource;
    int selectedSlot = -1, dragSlot = -1;
    Point dragStart;
    Process service;
    Button lockButton;
    ToolStripMenuItem exportItem;
    bool busy, refreshing;
    static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;

    sealed class Placement
    {
        public int project { get; set; }
        public int slot { get; set; }
    }

    static bool SamePlacement(Placement a, Placement b)
    {
        return a == b || (a != null && b != null && a.project == b.project && a.slot == b.slot);
    }

    internal ProjectEditor(Companion host, bool digitakt)
    {
        this.host = host;
        this.digitakt = digitakt;
        Dock = DockStyle.Fill;
        StatusText = digitakt ? "Import .dt2prj backups. Select or drag patterns into the destination grid."
            : "Import .arprj or .syx backups. Select or drag patterns into the destination grid.";
        StatusColor = muted;
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        var projectButton = Add(bar, "&Project ▾", () => projectMenu.Show(
            bar.Controls[0], new Point(0, bar.Controls[0].Height)));
        projectButton.AccessibleDescription = "Open the project actions menu.";
        AddProjectAction("Import backups…", ImportFiles);
        AddProjectAction("Open backup folder…", ImportFolder);
        AddProjectAction("Blank template…", async () => { await PickTemplate(); });
        projectMenu.Items.Add(new ToolStripSeparator());
        AddProjectAction("Open workspace…", OpenWorkspace);
        AddProjectAction("Save workspace…", async () => { await SaveWorkspace(); });
        exportItem = AddProjectAction("Export project…", Export);
        AddProjectAction("Firmware compatibility…", async () => { await ConfigureFirmware(); });
        if (!digitakt) AddProjectAction("Find Rytm lockfile…", async () => { await FindLock(); });
        projectMenu.Items.Add(new ToolStripSeparator());
        AddProjectAction("Open Transfer", host.OpenTransfer);
        AddProjectAction("Link Elektron Transfer…", () => host.PickTransfer());
        shell.Controls.Add(bar, 0, 0);
        templateText.Dock = DockStyle.Fill;
        templateText.TextAlign = ContentAlignment.MiddleLeft;
        templateText.ForeColor = muted;
        shell.Controls.Add(templateText, 0, 1);

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
            Tell("Selected " + LabelFor(selectedSource.slot) + ". Click an empty slot or drag to replace an occupied one.");
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
        lockButton = Add(sourceActions, "Find Rytm lockfile", async () => { await FindLock(); });
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
        grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.RowHeadersWidth = 42;
        grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;
        grid.AllowUserToResizeColumns = false;
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
            Name = "slot" + i, HeaderText = (i + 1).ToString("00"), MinimumWidth = 24, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.ClientSizeChanged += delegate { FitColumns(); };
        FitColumns();
        grid.Rows.Add(8);
        for (int i = 0; i < 8; i++) grid.Rows[i].HeaderCell.Value = ((char)('A' + i)).ToString();
        grid.CellPainting += PaintPatternKey;
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
            int slot = cell.RowIndex * 16 + cell.ColumnIndex;
            if (data == "elektron-source" && selectedSource != null) ChooseCell(slot, true);
            else if (data == "elektron-grid" && dragSlot >= 0)
            {
                selectedSource = null;
                selectedSlot = dragSlot;
                ChooseCell(slot);
            }
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
        shell.Controls.Add(layout, 0, 2);
        Controls.Add(shell);
        Render();
    }

    internal void HandleShortcut(KeyEventArgs e)
    {
        if (busy) return;
        if (e.KeyCode == Keys.Delete) Remove();
        if (e.Control && e.KeyCode == Keys.Z) Undo();
        if (e.KeyCode == Keys.Escape) { selectedSource = null; selectedSlot = -1; RenderGrid(); }
    }

    void FitColumns()
    {
        int available = Math.Max(0, grid.ClientSize.Width - grid.RowHeadersWidth - SystemInformation.VerticalScrollBarWidth);
        int width = available / grid.Columns.Count;
        int remainder = available % grid.Columns.Count;
        foreach (DataGridViewColumn column in grid.Columns)
            column.Width = Math.Max(column.MinimumWidth, width + (column.Index < remainder ? 1 : 0));
    }

    static GraphicsPath KeyOutline(Rectangle bounds)
    {
        const int corner = 8;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, corner, corner, 180, 90);
        path.AddArc(bounds.Right - corner, bounds.Y, corner, corner, 270, 90);
        path.AddArc(bounds.Right - corner, bounds.Bottom - corner, corner, corner, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - corner, corner, corner, 90, 90);
        path.CloseFigure();
        return path;
    }

    void PaintPatternKey(object sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || SystemInformation.HighContrast) return;
        int slot = e.RowIndex * 16 + e.ColumnIndex;
        bool occupied = placements[slot] != null;
        bool selected = slot == selectedSlot;
        bool focused = grid.Focused && (e.State & DataGridViewElementStates.Selected) != 0;
        var face = Rectangle.Inflate(e.CellBounds, -2, -3);
        face.Width -= 1;
        face.Height -= 3;
        var shadow = face;
        shadow.Offset(0, 3);
        Color top = selected ? Color.FromArgb(70, 85, 47)
            : occupied ? Color.FromArgb(76, 59, 53) : Color.FromArgb(61, 62, 59);
        Color bottom = selected ? Color.FromArgb(43, 54, 30)
            : occupied ? Color.FromArgb(48, 37, 34) : Color.FromArgb(35, 36, 34);
        Color edge = selected ? accent : focused ? Color.Silver : Color.FromArgb(105, 105, 99);
        Color lamp = occupied ? (digitakt ? Color.FromArgb(255, 172, 70) : Color.FromArgb(255, 113, 94))
            : Color.FromArgb(45, 46, 43);

        var graphics = e.Graphics;
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var panel = new SolidBrush(grid.BackgroundColor)) graphics.FillRectangle(panel, e.CellBounds);
            using (var path = KeyOutline(shadow)) graphics.FillPath(Brushes.Black, path);
            using (var path = KeyOutline(face))
            using (var paint = new LinearGradientBrush(face, top, bottom, LinearGradientMode.Vertical))
            using (var rim = new Pen(edge))
            {
                graphics.FillPath(paint, path);
                graphics.DrawPath(rim, path);
            }
            using (var light = new SolidBrush(lamp)) graphics.FillRectangle(light, face.X + (face.Width - 10) / 2, face.Y + 5, 10, 2);
        }
        finally { graphics.Restore(state); }

        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
            | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;
        var text = new Rectangle(face.X + 1, face.Y + 9, face.Width - 2, occupied ? 16 : face.Height - 12);
        TextRenderer.DrawText(graphics, LabelFor(slot), keyLabelFont, text, Color.WhiteSmoke, flags);
        string display = Convert.ToString(e.FormattedValue);
        int lineBreak = display.IndexOf('\n');
        if (occupied && lineBreak >= 0)
        {
            text.Y += 15;
            TextRenderer.DrawText(graphics, display.Substring(lineBreak + 1), keyNameFont, text, Color.Gainsboro, flags);
        }
        e.Handled = true;
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

    ToolStripMenuItem AddProjectAction(string caption, Action action)
    {
        var item = new ToolStripMenuItem(caption);
        item.Click += delegate { if (!busy) action(); };
        projectMenu.Items.Add(item);
        return item;
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

    internal void Tell(string text, bool error = false)
    {
        StatusText = text;
        StatusColor = error ? Color.LightSalmon : muted;
        host.RefreshStatus();
    }
    void Remember() { undo.Push((Placement[])placements.Clone()); }

    void Render()
    {
        refreshing = true;
        int choice = projects.SelectedIndex;
        projects.Items.Clear();
        foreach (var source in sources) projects.Items.Add(Device(Get(source, "format")) + " · " + Get(source, "name"));
        if (sources.Count > 0) projects.SelectedIndex = Math.Min(Math.Max(choice, 0), sources.Count - 1);
        refreshing = false;
        templateText.Text = template == null ? "No blank template loaded. Choose one from the Project menu or when you export."
            : "TEMPLATE   " + Device(Get(template, "format")) + "  ·  " + Get(template, "name");
        lockButton.Visible = template != null && (bool)template["modStorage"] && Convert.ToUInt32(template["layout"]) != 0;
        if (template != null && (bool)template["modStorage"])
        {
            var layout = Convert.ToUInt32(template["layout"]);
            templateText.Text += (bool)template["emptyModStorage"] ? " · Rytm mod storage is empty"
                : layout == 0 ? " · Unidentified Rytm mod layout"
                : " · Rytm mod layout 0x" + layout.ToString("X8") + (hasFirmwareLock ? " (lockfile matched)" : " (needs lockfile)");
        }
        if (firmwareProfile != null)
            templateText.Text += " · Target: " + FirmwareName(Get(firmwareProfile, "source"));
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
            if ((bool)sources[index]["modStorage"]) info.Text += "   ·   Mod storage";
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
        exportItem.Text = template == null ? "Export project…" : "Export ." + Get(template, "format") + "…";
    }

    bool Fits(Placement p, int replacedSlot = -1)
    {
        string format = Get(sources[p.project], "format");
        if (template != null && format != Get(template, "format")) return false;
        return placements.Where((x, i) => x != null && i != replacedSlot).All(x => Get(sources[x.project], "format") == format);
    }

    void ChooseCell(int slot, bool replace = false)
    {
        if (busy) return;
        bool replaced = false;
        if (selectedSource != null)
        {
            var previous = placements[slot];
            if (previous != null && !replace) { Tell("Drag the source pattern onto the occupied slot to replace it.", true); return; }
            if (!Fits(selectedSource, slot)) { Tell("Use the same project format for the arrangement and template.", true); return; }
            if (SamePlacement(previous, selectedSource))
            {
                selectedSource = null;
                Tell(LabelFor(slot) + " already has that pattern.");
                return;
            }
            Remember();
            placements[slot] = selectedSource;
            selectedSource = null;
            replaced = previous != null;
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
        Tell(replaced ? "Replaced " + LabelFor(slot) + ". Use Undo to restore the previous pattern."
            : placements.Count(x => x != null) + " / 128 slots placed.");
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
        service = Process.Start(new ProcessStartInfo(Node(), "\"" + core + "\" " + (digitakt ? "dt2prj" : "arprj")) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 });
        service.ErrorDataReceived += delegate { };
        service.BeginErrorReadLine();
    }

    internal bool CheckEngine()
    {
        StartService();
        return Request(Data("op", "import", "files", new string[0])).ContainsKey("sources");
    }

    Dictionary<string, object> Request(Dictionary<string, object> message)
    {
        if (service == null || service.HasExited) throw new IOException("The project engine is unavailable. Install Node.js 24 or set ELEKTRON_COMPANION_NODE and reopen the app.");
        // The engine reads UTF-8 regardless of the Windows console code page.
        var bytes = Encoding.UTF8.GetBytes(json.Serialize(message) + "\n");
        var input = service.StandardInput.BaseStream;
        input.Write(bytes, 0, bytes.Length);
        input.Flush();
        string line = service.StandardOutput.ReadLine();
        if (line == null) throw new IOException("The project engine stopped. Reopen the companion.");
        var result = json.Deserialize<Dictionary<string, object>>(line);
        if (!(bool)result["ok"]) throw new IOException(Get(result, "error"));
        return Map(result["value"]);
    }

    async void Call(Dictionary<string, object> message, Action<Dictionary<string, object>> done)
    {
        await RunRequest(message, done);
    }

    async Task<bool> RunRequest(Dictionary<string, object> message, Action<Dictionary<string, object>> done, string failureTitle = null)
    {
        busy = true;
        foreach (var button in actions) button.Enabled = false;
        Tell("Working…");
        try
        {
            if (service == null) StartService();
            var result = await Task.Run(() => Request(message));
            if (IsDisposed) return false;
            done(result);
            return true;
        }
        catch (Exception error)
        {
            if (!IsDisposed) ReportFailure(error.Message, failureTitle);
            return false;
        }
        finally
        {
            busy = false;
            if (!IsDisposed) foreach (var button in actions) button.Enabled = true;
        }
    }

    void ReportFailure(string message, string title)
    {
        Tell(message, true);
        if (title != null) MessageBox.Show(host, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    void SetState(Dictionary<string, object> result)
    {
        sources.Clear();
        sources.AddRange(Items(result["sources"]).Select(Map));
        template = Map(result["template"]);
        firmwareProfile = Map(result["firmwareProfile"]);
        hasFirmwareLock = (bool)result["hasLock"];
        selectedSlot = -1;
        selectedSource = null;
        Render();
    }

    void Import(string[] paths)
    {
        if (paths.Length == 0) { Tell("No project backups found in that folder.", true); return; }
        Call(Data("op", "import", "files", paths), result => {
            SetState(result);
            if (Int(result, "added") > 0) workspaceSettingsChanged = true;
            var errors = Items(result["errors"]).Cast<string>().ToList();
            Tell("Imported " + Int(result, "added") + ". Skipped " + Int(result, "empty") + " unused slots."
                + (errors.Count > 0 ? "  " + errors.Count + " unreadable. " + errors[0] : ""), errors.Count > 0);
        });
    }

    void ImportFiles()
    {
        using (var dialog = new OpenFileDialog { Filter = BackupFilter, Multiselect = true })
            if (dialog.ShowDialog(host) == DialogResult.OK) Import(dialog.FileNames);
    }

    void ImportFolder()
    {
        using (var dialog = new FolderBrowserDialog { Description = "Choose a folder of " + DeviceName + " backups" })
            if (dialog.ShowDialog(host) == DialogResult.OK)
            {
                var extensions = digitakt ? new[] { ".dt2prj" } : new[] { ".arprj", ".syx" };
                Import(Directory.GetFiles(dialog.SelectedPath).Where(f => extensions
                    .Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f).ToArray());
            }
    }

    string FileToOpen(string filter, string title = "")
    {
        using (var dialog = new OpenFileDialog { Filter = filter, Title = title })
            return dialog.ShowDialog(host) == DialogResult.OK ? dialog.FileName : null;
    }

    string FileToSave(string extension, string name, string title = "")
    {
        using (var dialog = new SaveFileDialog { Title = title, FileName = name, Filter = "Save file|*." + extension, DefaultExt = extension, AddExtension = true, OverwritePrompt = false })
            return dialog.ShowDialog(host) == DialogResult.OK ? dialog.FileName : null;
    }

    async Task<bool> PickTemplate()
    {
        var first = placements.FirstOrDefault(p => p != null);
        string extension = first == null ? null : Get(sources[first.project], "format");
        string filter = extension == null ? BackupFilter : "Blank " + DeviceName + " project|*." + extension;
        string file = FileToOpen(filter, "Choose a blank " + DeviceName + " template");
        if (file == null) return false;
        string format = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
        if (placements.Any(x => x != null && Get(sources[x.project], "format") != format))
        {
            ReportFailure("The blank template must use the same project format as your arrangement.", "Template could not be loaded");
            return false;
        }
        return await RunRequest(Data("op", "template", "file", file), result => {
            SetState(result);
            workspaceSettingsChanged = true;
            Tell("Blank template ready.");
        }, "Template could not be loaded");
    }

    async Task<bool> FindLock()
    {
        uint id = template == null ? 0 : Convert.ToUInt32(template["layout"]);
        if (id == 0)
        {
            ReportFailure("Choose a blank Rytm .arprj with a detected mod layout first.", "Rytm lockfile required");
            return false;
        }
        Dictionary<string, object> request;
        using (var dialog = new Form {
            Text = "Find Rytm lockfile", ClientSize = new Size(560, 208), Font = Font,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        })
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
            foreach (int height in new[] { 26, 47, 24, 36, 44 })
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Detected Rytm layout 0x" + id.ToString("X8") }, 0, 0);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill,
                Text = "Leave the link blank to check the companion registry on GitHub. You can also open a lockfile or build manifest from your computer." }, 0, 1);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Public JSON link (optional)" }, 0, 2);
            var url = new TextBox { Dock = DockStyle.Fill, MaxLength = 4096, AccessibleName = "Public JSON link" };
            layout.Controls.Add(url, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var online = new Button { Text = "Check registry", AutoSize = true, DialogResult = DialogResult.OK };
            var local = new Button { Text = "Open local file…", AutoSize = true, DialogResult = DialogResult.Retry };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(online);
            buttons.Controls.Add(local);
            layout.Controls.Add(buttons, 0, 4);
            url.TextChanged += delegate { online.Text = String.IsNullOrWhiteSpace(url.Text) ? "Check registry" : "Load from link"; };
            dialog.AcceptButton = online;
            dialog.CancelButton = cancel;
            dialog.Controls.Add(layout);
            var choice = dialog.ShowDialog(host);
            if (choice == DialogResult.OK) request = Data("op", "lock-online", "url", url.Text);
            else if (choice == DialogResult.Retry)
            {
                string file = FileToOpen("Rytm storage lockfile or build manifest|*.json");
                if (file == null) return false;
                request = Data("op", "lock", "file", file);
            }
            else return false;
        }
        return await RunRequest(request, result => {
            SetState(result);
            workspaceSettingsChanged = true;
            Tell("Rytm storage lockfile matched to the blank template.");
        }, "Lockfile could not be loaded");
    }

    static readonly string[] FirmwareSources = { "stock", "modwerk", "elekloader", "octabam", "other" };
    static readonly string[] FirmwareNames = { "Stock Elektron", "Modwerk", "Elekloader", "Octabam", "Other or unknown mod" };
    static string FirmwareName(string source) { return FirmwareNames[Array.IndexOf(FirmwareSources, source)]; }

    async Task<bool> ConfigureFirmware()
    {
        bool tagged = template != null && (bool)template["modStorage"];
        Dictionary<string, object> profile;
        using (var dialog = new Form {
            Text = "Firmware compatibility", ClientSize = new Size(460, 234), Font = Font,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        })
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
            foreach (int height in new[] { 28, 35, 62, 40, 39 })
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Text = "Which firmware runs on the destination machine?", Dock = DockStyle.Fill }, 0, 0);
            var choice = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Firmware source" };
            choice.Items.AddRange(FirmwareNames);
            layout.Controls.Add(choice, 0, 1);
            var hint = new Label { Dock = DockStyle.Fill };
            layout.Controls.Add(hint, 0, 2);
            var standard = new CheckBox { Text = "My build uses the standard Elektron project layout", Dock = DockStyle.Fill,
                Checked = firmwareProfile != null && (bool)firmwareProfile["standardLayout"] };
            layout.Controls.Add(standard, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "Use profile", AutoSize = true, DialogResult = DialogResult.OK, Enabled = false };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons, 0, 4);
            choice.SelectedIndexChanged += delegate {
                int index = choice.SelectedIndex;
                standard.Visible = !tagged && (index == 1 || index == 2);
                hint.Text = index == 3 ? "Octabam targets Octatrack. The companion cannot export Octatrack projects yet."
                    : index == 4 ? "Export requires a verified project layout."
                    : tagged ? "The Rytm template contains mod storage. Export needs the matching build's lockfile."
                    : index == 0 ? "Export uses the standard project layout."
                    : "The backup does not identify the mod build. Check the build documentation before confirming its project layout.";
                ok.Enabled = true;
            };
            hint.Text = tagged ? "The Rytm template contains mod storage." : "Backups may omit the firmware mod name.";
            standard.Visible = false;
            if (firmwareProfile != null) choice.SelectedIndex = Array.IndexOf(FirmwareSources, Get(firmwareProfile, "source"));
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            dialog.Controls.Add(layout);
            if (dialog.ShowDialog(host) != DialogResult.OK) return false;
            int selected = choice.SelectedIndex;
            profile = Data("source", FirmwareSources[selected],
                "standardLayout", !tagged && (selected == 1 || selected == 2) && standard.Checked);
        }
        return await RunRequest(Data("op", "firmware", "profile", profile), result => {
            var previous = firmwareProfile;
            SetState(result);
            if (previous == null || Get(previous, "source") != Get(firmwareProfile, "source")
                || (bool)previous["standardLayout"] != (bool)firmwareProfile["standardLayout"]) workspaceSettingsChanged = true;
            Tell("Target firmware set to " + FirmwareName(Get(firmwareProfile, "source")) + ".");
        }, "Firmware profile could not be set");
    }

    void MarkWorkspaceSaved()
    {
        savedPlacements = (Placement[])placements.Clone();
        workspaceSettingsChanged = false;
    }

    internal async Task<bool> SaveWorkspace()
    {
        string file = FileToSave("json", DeviceName + " set.json");
        return file != null && await WriteWorkspace(file);
    }

    Task<bool> WriteWorkspace(string file)
    {
        return RunRequest(Data("op", "save", "file", file, "grid", placements), result => {
            MarkWorkspaceSaved();
            Tell("Saved workspace to " + file);
        });
    }

    void OpenWorkspace()
    {
        string file = FileToOpen("Elektron workspace|*.json");
        if (file != null) Call(Data("op", "open", "file", file), result => {
            placements = Items(result["grid"]).Select(x => x == null ? null :
                new Placement { project = Int(Map(x), "project"), slot = Int(Map(x), "slot") }).ToArray();
            undo.Clear();
            SetState(result);
            MarkWorkspaceSaved();
            Tell("Opened " + Path.GetFileName(file));
        });
    }

    async void Export()
    {
        if (placements.All(p => p == null))
        {
            Tell("Drag at least one source pattern into the destination grid before exporting.", true);
            return;
        }
        if (template == null)
        {
            Tell("Select a backup of a newly saved, empty project from your " + DeviceName + ".");
            if (!await PickTemplate()) return;
        }
        string format = Get(template, "format");
        bool tagged = (bool)template["modStorage"];
        bool emptyModStorage = (bool)template["emptyModStorage"];
        bool needsFirmwareProfile = firmwareProfile == null ? !tagged : tagged && Get(firmwareProfile, "source") == "stock";
        if (needsFirmwareProfile && !await ConfigureFirmware()) return;
        if (emptyModStorage && placements.Any(p => p != null && (bool)sources[p.project]["modStorage"]
            && !(bool)sources[p.project]["emptyModStorage"]))
        {
            ReportFailure("Load and save the blank project on the Rytm, then back it up again so EPC can match its mod layout to your source patterns.",
                "Rytm template needs a saved mod layout");
            return;
        }
        if (tagged && !emptyModStorage && !hasFirmwareLock && (firmwareProfile == null
            || new[] { "modwerk", "elekloader" }.Contains(Get(firmwareProfile, "source"))))
        {
            Tell("Choose the storage lockfile from the build used for the blank Rytm template.");
            if (!await FindLock()) return;
        }
        if (IsDisposed) return;
        Tell("Choose where to save the merged project file.");
        string file = FileToSave(format, (digitakt ? "Digitakt II" : "Rytm") + " Merged." + format, "Save merged " + DeviceName + " project");
        if (file == null) { Tell("Export canceled before saving a project file."); return; }
        if (File.Exists(file))
        {
            ReportFailure("A file already exists at that location. Choose a new filename for the merged project.", "Project could not be exported");
            return;
        }
        string sampleNote = "";
        bool saved = await RunRequest(Data("op", "export", "file", file, "grid", placements), result => {
            if (result.ContainsKey("bundled")) sampleNote = "Bundled samples: " + Int(result, "bundled") + ".";
            if (result.ContainsKey("missing") && Int(result, "missing") > 0)
                sampleNote += " " + Int(result, "missing") + " sample identities need audio on the +Drive.";
            if (result.ContainsKey("samplePoolSummary"))
                sampleNote += "\n\nMerged sample pool\n"
                    + string.Join("\n", Items(result["samplePoolSummary"]).Select(item => Convert.ToString(item)).ToArray());
            Tell("Saved " + file);
        }, "Project could not be exported");
        if (saved && !IsDisposed)
            MessageBox.Show(host, "Saved the merged project to:\n\n" + file + "\n\nUse Project → Open Transfer to restore it."
                + (sampleNote.Length > 0 ? "\n\n" + sampleNote.Trim() : ""), "Project exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void Kits()
    {
        if (projects.SelectedIndex < 0) { Tell("Import and select a source device first.", true); return; }
        string device = Get(sources[projects.SelectedIndex], "format") == "dt2prj" ? "dt2prj" : "arprj";
        string file = FileToSave("zip", Device(device) + " Kit Folders.zip");
        if (file != null) Call(Data("op", "kits", "file", file, "device", device), result =>
            Tell("Saved " + Int(result, "count") + " kit records. Extract the ZIP to browse the project folders."));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            projectMenu.Dispose();
            keyLabelFont.Dispose();
            keyNameFont.Dispose();
        }
        if (disposing && service != null)
        {
            try { if (!service.HasExited) service.Kill(); } catch (InvalidOperationException) { }
            service.Dispose();
        }
        base.Dispose(disposing);
    }
}

