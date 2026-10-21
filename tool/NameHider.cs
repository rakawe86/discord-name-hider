// Discord Name Hider - one-file Windows tool.
// Installs a bundled Vencord build (with the HideUsers plugin) into Discord once,
// then manages who is hidden. Changes apply live: Discord re-reads
// %APPDATA%\DiscordNameHider\state.json every second, no restart needed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Discord Name Hider")]
[assembly: AssemblyProduct("Discord Name Hider")]
[assembly: AssemblyVersion("1.3.0.0")]

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

class Person
{
    public string value;
    public string label;
    public bool hidden;
}

class HiderState
{
    public bool enabled = true;
    public List<Person> users = new List<Person>();
}

static class Hider
{
    static readonly string LocalApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string RoamingApp = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static readonly string DistDir = Path.Combine(LocalApp, "DiscordNameHider", "dist");
    public static readonly string StateFile = Path.Combine(RoamingApp, "DiscordNameHider", "state.json");
    static readonly string VencordSettings = Path.Combine(RoamingApp, "Vencord", "settings", "settings.json");
    static readonly string[] Flavors = { "Discord", "DiscordPTB", "DiscordCanary" };
    static readonly string[] DistFiles = { "patcher.js", "preload.js", "renderer.js", "renderer.css" };

    /* ---------- state file ---------- */

    public static HiderState LoadState()
    {
        try
        {
            if (File.Exists(StateFile))
                return new JavaScriptSerializer().Deserialize<HiderState>(File.ReadAllText(StateFile)) ?? new HiderState();
        }
        catch { }
        return new HiderState();
    }

    public static void SaveState(HiderState s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
        var tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(s));
        if (File.Exists(StateFile)) File.Replace(tmp, StateFile, null);
        else File.Move(tmp, StateFile);
    }

    /* ---------- Discord install ---------- */

    public static List<string> InstalledDiscords()
    {
        return Flavors.Where(f => Directory.Exists(Path.Combine(LocalApp, f))).ToList();
    }

    static IEnumerable<string> ResourceDirs(string flavor)
    {
        var root = Path.Combine(LocalApp, flavor);
        if (!Directory.Exists(root)) yield break;
        foreach (var app in Directory.GetDirectories(root, "app-*"))
        {
            var res = Path.Combine(app, "resources");
            if (Directory.Exists(res)) yield return res;
        }
    }

    public static bool IsInstalled()
    {
        if (!File.Exists(Path.Combine(DistDir, "patcher.js"))) return false;
        foreach (var f in InstalledDiscords())
            foreach (var res in ResourceDirs(f))
            {
                var app = Path.Combine(res, "app.asar");
                if (File.Exists(Path.Combine(res, "_app.asar")) && File.Exists(app) && new FileInfo(app).Length < 4096
                    && File.ReadAllText(app).Contains("DiscordNameHider"))
                    return true;
            }
        return false;
    }

    // True when the installed files differ from the ones bundled in this exe.
    public static bool NeedsUpdate()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in DistFiles)
        {
            var path = Path.Combine(DistDir, name);
            if (!File.Exists(path)) return true;
            using (var s = asm.GetManifestResourceStream("vc." + name))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                if (!ms.ToArray().SequenceEqual(File.ReadAllBytes(path))) return true;
            }
        }
        return false;
    }

    static void CloseDiscord()
    {
        bool wasRunning = false;
        foreach (var f in Flavors)
            foreach (var p in Process.GetProcessesByName(f))
            {
                wasRunning = true;
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
        if (wasRunning) Thread.Sleep(1500);
    }

    static void StartDiscord(string flavor)
    {
        var updater = Path.Combine(LocalApp, flavor, "Update.exe");
        if (File.Exists(updater))
            Process.Start(new ProcessStartInfo(updater, "--processStart " + flavor + ".exe") { UseShellExecute = false });
    }

    static void ExtractDist()
    {
        Directory.CreateDirectory(DistDir);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in DistFiles)
        {
            using (var s = asm.GetManifestResourceStream("vc." + name))
            using (var o = File.Create(Path.Combine(DistDir, name)))
                s.CopyTo(o);
        }
    }

    // Writes a minimal asar archive whose index.js loads our patcher.
    static void WriteLoaderAsar(string path)
    {
        var patcher = Path.Combine(DistDir, "patcher.js");
        var index = Encoding.UTF8.GetBytes("require(" + new JavaScriptSerializer().Serialize(patcher) + ");");
        var pkg = Encoding.UTF8.GetBytes("{\"name\":\"discord\",\"main\":\"index.js\"}");
        var header = Encoding.UTF8.GetBytes(
            "{\"files\":{\"index.js\":{\"size\":" + index.Length + ",\"offset\":\"0\"}," +
            "\"package.json\":{\"size\":" + pkg.Length + ",\"offset\":\"" + index.Length + "\"}}}");
        int pad = (4 - header.Length % 4) % 4;
        int headerPickle = 8 + header.Length + pad;

        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((uint)4);
            w.Write((uint)headerPickle);
            w.Write((uint)(headerPickle - 4));
            w.Write((uint)header.Length);
            w.Write(header);
            w.Write(new byte[pad]);
            w.Write(index);
            w.Write(pkg);
        }
    }

    static void WriteVencordSettings(bool enabled)
    {
        Dictionary<string, object> s = null;
        try
        {
            if (File.Exists(VencordSettings))
                s = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(VencordSettings)) as Dictionary<string, object>;
        }
        catch { }
        if (s == null) s = new Dictionary<string, object>();

        object pluginsObj;
        var plugins = s.TryGetValue("plugins", out pluginsObj) && pluginsObj is Dictionary<string, object>
            ? (Dictionary<string, object>)pluginsObj : new Dictionary<string, object>();
        plugins["HideUsers"] = new Dictionary<string, object> { { "enabled", enabled } };
        // Safety: keep Discord's analytics, metrics and Sentry crash reports blocked,
        // so nothing about the client mod is reported back to Discord.
        plugins["NoTrack"] = new Dictionary<string, object> { { "enabled", true }, { "disableAnalytics", true } };
        s["plugins"] = plugins;
        // Keep the bundled build: Vencord's own updater would replace it and drop HideUsers.
        s["autoUpdate"] = false;
        s["autoUpdateNotification"] = false;
        Directory.CreateDirectory(Path.GetDirectoryName(VencordSettings));
        File.WriteAllText(VencordSettings, new JavaScriptSerializer().Serialize(s));
    }

    public static void Install()
    {
        var flavors = InstalledDiscords();
        if (flavors.Count == 0) throw new Exception("Discord desktop app was not found on this PC.");

        CloseDiscord();
        ExtractDist();
        WriteVencordSettings(true);
        if (!File.Exists(StateFile)) SaveState(new HiderState());

        int patched = 0;
        foreach (var f in flavors)
            foreach (var res in ResourceDirs(f))
            {
                var app = Path.Combine(res, "app.asar");
                var orig = Path.Combine(res, "_app.asar");
                if (!File.Exists(orig))
                {
                    if (!File.Exists(app)) continue;
                    File.Move(app, orig);
                }
                WriteLoaderAsar(app);
                patched++;
            }
        if (patched == 0) throw new Exception("Could not find Discord's app files to patch.");

        foreach (var f in flavors) StartDiscord(f);
    }

    public static void Uninstall()
    {
        CloseDiscord();
        foreach (var f in InstalledDiscords())
            foreach (var res in ResourceDirs(f))
            {
                var app = Path.Combine(res, "app.asar");
                var orig = Path.Combine(res, "_app.asar");
                if (!File.Exists(orig)) continue;
                if (File.Exists(app)) File.Delete(app);
                File.Move(orig, app);
            }
        try { WriteVencordSettings(false); } catch { }
        try { Directory.Delete(DistDir, true); } catch { }
        foreach (var f in InstalledDiscords()) StartDiscord(f);
    }
}

/* ======================= UI ======================= */

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(43, 45, 49);
    public static readonly Color Card = Color.FromArgb(30, 31, 34);
    public static readonly Color Input = Color.FromArgb(56, 58, 64);
    public static readonly Color Text = Color.FromArgb(242, 243, 245);
    public static readonly Color Muted = Color.FromArgb(148, 155, 164);
    public static readonly Color Blurple = Color.FromArgb(88, 101, 242);
    public static readonly Color Green = Color.FromArgb(35, 165, 90);
    public static readonly Color Red = Color.FromArgb(218, 55, 60);
    public static readonly Color Grey = Color.FromArgb(78, 80, 88);
}

// Flat iOS-style switch.
class ToggleSwitch : Control
{
    bool on;
    public event EventHandler Toggled;
    public bool On { get { return on; } set { on = value; Invalidate(); } }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(52, 28);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
    }

    protected override void OnClick(EventArgs e)
    {
        On = !On;
        if (Toggled != null) Toggled(this, EventArgs.Empty);
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int h = Height - 1, w = Width - 1;
        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            path.AddArc(0, 0, h, h, 90, 180);
            path.AddArc(w - h, 0, h, h, 270, 180);
            path.CloseFigure();
            using (var b = new SolidBrush(on ? Theme.Green : Theme.Grey)) g.FillPath(b, path);
        }
        int knob = h - 6;
        int x = on ? w - knob - 3 : 3;
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x, 3, knob, knob);
    }
}

class MainForm : Form
{
    HiderState state;
    DateTime lastWrite;
    bool loading, needsUpdate, recheck = true;

    readonly ToggleSwitch master;
    readonly Label masterText, masterSub, installBadge, footer;
    readonly ListView list;
    readonly TextBox addValue, addLabel;
    readonly Button installBtn;
    readonly System.Windows.Forms.Timer watch;

    public MainForm()
    {
        Text = "Discord Name Hider";
        ClientSize = new Size(520, 616);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // Header
        Controls.Add(new Label { Text = "Discord Name Hider", Font = new Font("Segoe UI Semibold", 16f), AutoSize = true, Location = new Point(20, 16) });
        installBadge = new Label { AutoSize = true, Location = new Point(23, 52), Font = new Font("Segoe UI", 9f) };
        Controls.Add(installBadge);

        // Master switch card
        var card = new Panel { Location = new Point(20, 80), Size = new Size(480, 70), BackColor = Theme.Card };
        masterText = new Label { Font = new Font("Segoe UI Semibold", 12f), AutoSize = true, Location = new Point(16, 12) };
        masterSub = new Label { ForeColor = Theme.Muted, AutoSize = true, Location = new Point(17, 40), Font = new Font("Segoe UI", 8.75f) };
        master = new ToggleSwitch { Location = new Point(410, 21) };
        master.Toggled += (s, e) => { state.enabled = master.On; Save(); };
        card.Controls.AddRange(new Control[] { masterText, masterSub, master });
        Controls.Add(card);

        // People list
        Controls.Add(new Label { Text = "PEOPLE  -  tick = hidden, untick = visible", ForeColor = Theme.Muted, Font = new Font("Segoe UI Semibold", 8.5f), AutoSize = true, Location = new Point(20, 166) });
        list = new ListView
        {
            Location = new Point(20, 188), Size = new Size(480, 230), View = View.Details, CheckBoxes = true,
            FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None,
            BackColor = Theme.Card, ForeColor = Theme.Text, Font = new Font("Segoe UI", 10f), MultiSelect = true
        };
        list.Columns.Add("Username or User ID", 260);
        list.Columns.Add("Note", 216);
        // Dark column header; rows keep the default drawing (incl. checkboxes).
        list.OwnerDraw = true;
        list.DrawColumnHeader += (s, e) =>
        {
            using (var bg = new SolidBrush(Theme.Input)) e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, new Font("Segoe UI Semibold", 9f), new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height),
                Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
        list.DrawItem += (s, e) => { e.DrawDefault = true; };
        list.DrawSubItem += (s, e) => { e.DrawDefault = true; };
        list.ItemChecked += (s, e) =>
        {
            if (loading) return;
            var p = (Person)e.Item.Tag;
            p.hidden = e.Item.Checked;
            Save();
        };
        list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
        Controls.Add(list);

        // Add row
        addValue = MakeInput(new Point(20, 430), 210, "Username or User ID");
        addLabel = MakeInput(new Point(238, 430), 160, "Note (optional)");
        var addBtn = MakeButton("Add", Theme.Blurple, new Point(406, 429), new Size(94, 30));
        addBtn.Click += (s, e) => AddPerson();
        addValue.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddPerson(); e.SuppressKeyPress = true; } };
        addLabel.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddPerson(); e.SuppressKeyPress = true; } };
        Controls.AddRange(new Control[] { addValue, addLabel, addBtn });

        var removeBtn = MakeButton("Remove selected", Theme.Grey, new Point(20, 468), new Size(140, 30));
        removeBtn.Click += (s, e) => RemoveSelected();
        var allOn = MakeButton("Hide all", Theme.Grey, new Point(168, 468), new Size(90, 30));
        allOn.Click += (s, e) => SetAll(true);
        var allOff = MakeButton("Show all", Theme.Grey, new Point(266, 468), new Size(90, 30));
        allOff.Click += (s, e) => SetAll(false);
        Controls.AddRange(new Control[] { removeBtn, allOn, allOff });

        Controls.Add(new Label
        {
            Text = "Tip: get a User ID in Discord > Settings > Advanced > Developer Mode, then right-click a person > Copy User ID.\nIn Discord, press Ctrl+Shift+H to switch hiding on/off.",
            ForeColor = Theme.Muted, Font = new Font("Segoe UI", 8.5f), Location = new Point(20, 508), Size = new Size(480, 52)
        });

        // Footer
        installBtn = MakeButton("", Theme.Blurple, new Point(20, 568), new Size(200, 32));
        installBtn.Click += (s, e) => InstallOrUninstall();
        footer = new Label { ForeColor = Theme.Muted, Location = new Point(230, 568), Size = new Size(270, 32), TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 8.75f) };
        Controls.AddRange(new Control[] { installBtn, footer });

        state = Hider.LoadState();
        RefreshAll();

        // Pick up changes made from inside Discord (Ctrl+Shift+H).
        watch = new System.Windows.Forms.Timer { Interval = 1000 };
        watch.Tick += (s, e) =>
        {
            try
            {
                if (File.Exists(Hider.StateFile) && File.GetLastWriteTimeUtc(Hider.StateFile) > lastWrite.AddMilliseconds(200))
                {
                    lastWrite = File.GetLastWriteTimeUtc(Hider.StateFile);
                    state = Hider.LoadState();
                    RefreshAll();
                }
            }
            catch { }
        };
        watch.Start();
    }

    TextBox MakeInput(Point loc, int width, string placeholder)
    {
        var tb = new TextBox { Location = loc, Width = width, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10.5f) };
        SetCue(tb, placeholder);
        return tb;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    static void SetCue(TextBox tb, string text)
    {
        tb.HandleCreated += (s, e) => SendMessage(tb.Handle, 0x1501, (IntPtr)1, text); // EM_SETCUEBANNER
    }

    Button MakeButton(string text, Color color, Point loc, Size size)
    {
        var b = new Button { Text = text, Location = loc, Size = size, FlatStyle = FlatStyle.Flat, BackColor = color, ForeColor = Color.White, Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 9.5f) };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    void RefreshAll()
    {
        loading = true;
        master.On = state.enabled;
        masterText.Text = state.enabled ? "Hide everyone  -  ON" : "Hide everyone  -  OFF";
        masterText.ForeColor = state.enabled ? Theme.Text : Theme.Muted;
        var count = state.users.Count(u => u.hidden);
        masterSub.Text = state.enabled
            ? count + " of " + state.users.Count + " people on your list are hidden right now."
            : "Paused: everyone on your list is visible. Switch on to hide them again.";

        list.BeginUpdate();
        list.Items.Clear();
        foreach (var p in state.users)
        {
            var item = new ListViewItem(new[] { p.value, p.label ?? "" }) { Checked = p.hidden, Tag = p };
            if (!state.enabled) item.ForeColor = Theme.Muted;
            list.Items.Add(item);
        }
        list.EndUpdate();
        loading = false;

        bool installed = Hider.IsInstalled();
        bool hasDiscord = Hider.InstalledDiscords().Count > 0;
        if (recheck) { needsUpdate = installed && Hider.NeedsUpdate(); recheck = false; }
        installBadge.Text = !hasDiscord ? "●  Discord desktop app not found"
            : needsUpdate ? "●  New version available - click Update"
            : installed ? "●  Active in Discord - changes apply instantly" : "●  Not installed in Discord yet";
        installBadge.ForeColor = !hasDiscord ? Theme.Red : installed && !needsUpdate ? Theme.Green : Color.FromArgb(240, 178, 50);
        installBtn.Text = needsUpdate ? "Update (Discord restarts)" : installed ? "Uninstall from Discord" : "Install into Discord";
        installBtn.BackColor = installed && !needsUpdate ? Theme.Grey : Theme.Blurple;
        installBtn.Enabled = hasDiscord;
        if (!installed && hasDiscord && footer.Text.Length == 0) footer.Text = "One-time setup: Discord will restart once.";
    }

    void Save()
    {
        try
        {
            Hider.SaveState(state);
            lastWrite = File.GetLastWriteTimeUtc(Hider.StateFile);
            RefreshAll();
        }
        catch (Exception ex) { footer.Text = "Could not save: " + ex.Message; footer.ForeColor = Theme.Red; }
    }

    void AddPerson()
    {
        var v = addValue.Text.Trim().TrimStart('@');
        if (v.Length == 0) return;
        if (state.users.Any(u => string.Equals(u.value, v, StringComparison.OrdinalIgnoreCase)))
        {
            footer.Text = v + " is already in the list."; footer.ForeColor = Theme.Muted;
            return;
        }
        state.users.Add(new Person { value = v, label = addLabel.Text.Trim(), hidden = true });
        addValue.Clear(); addLabel.Clear(); addValue.Focus();
        Save();
    }

    void RemoveSelected()
    {
        if (list.SelectedItems.Count == 0) return;
        foreach (ListViewItem it in list.SelectedItems) state.users.Remove((Person)it.Tag);
        Save();
    }

    void SetAll(bool hidden)
    {
        foreach (var p in state.users) p.hidden = hidden;
        Save();
    }

    void InstallOrUninstall()
    {
        bool installed = Hider.IsInstalled() && !needsUpdate;
        if (installed && MessageBox.Show("Remove the tool from Discord and show everyone?\nYour list is kept.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        UseWaitCursor = true; Enabled = false;
        footer.ForeColor = Theme.Muted;
        footer.Text = "Working... Discord will close and reopen.";
        Refresh();
        try
        {
            if (installed) Hider.Uninstall(); else Hider.Install();
            footer.ForeColor = Theme.Green;
            footer.Text = installed ? "Removed. Discord is back to normal." : "Installed! Discord is restarting.";
        }
        catch (Exception ex) { footer.ForeColor = Theme.Red; footer.Text = "Error: " + ex.Message; }
        finally { Enabled = true; UseWaitCursor = false; recheck = true; RefreshAll(); }
    }
}
