// Discord Name Hider - one-file Windows tool.
// Installs a bundled Vencord build (with the HideUsers plugin) into Discord once,
// then manages who is hidden. Changes apply live: Discord re-reads
// %APPDATA%\DiscordNameHider\state.json every second, no restart needed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Discord Name Hider")]
[assembly: AssemblyProduct("Discord Name Hider")]
[assembly: AssemblyVersion(Hider.Version + ".0")]

static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        SetProcessDPIAware(); // sharp text instead of a blurry stretched window
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
    public const string Version = "1.4.1"; // bump for every release; the GitHub tag must be "v" + this
    const string Repo = "rakawe86/discord-name-hider";
    public const string DownloadPage = "https://github.com/" + Repo + "/releases/latest";

    public static bool IsNewer(string a, string b)
    {
        Version x, y;
        return System.Version.TryParse(a, out x) && System.Version.TryParse(b, out y) && x > y;
    }

    // The newer version on GitHub, or null (offline, GitHub down, nothing new).
    public static string LatestRelease()
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = "DiscordNameHider";
                var release = Json().DeserializeObject(web.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest")) as Dictionary<string, object>;
                object tag;
                var latest = release != null && release.TryGetValue("tag_name", out tag) ? Convert.ToString(tag).TrimStart('v') : "";
                return IsNewer(latest, Version) ? latest : null;
            }
        }
        catch { return null; }
    }

    static readonly string LocalApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string RoamingApp = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static readonly string DistDir = Path.Combine(LocalApp, "DiscordNameHider", "dist");
    public static readonly string StateFile = Path.Combine(RoamingApp, "DiscordNameHider", "state.json");
    static readonly string VencordSettings = Path.Combine(RoamingApp, "Vencord", "settings", "settings.json");
    static readonly string[] Flavors = { "Discord", "DiscordPTB", "DiscordCanary" };
    static readonly string[] DistFiles = { "patcher.js", "preload.js", "renderer.js", "renderer.css" };

    /* ---------- state file ---------- */

    static JavaScriptSerializer Json()
    {
        return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    }

    // null = unreadable right now (half-written, locked or corrupt).
    public static HiderState TryLoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return new HiderState();
            var s = Json().Deserialize<HiderState>(File.ReadAllText(StateFile));
            if (s == null) return null;
            if (s.users == null) s.users = new List<Person>();
            s.users.RemoveAll(u => u == null || string.IsNullOrWhiteSpace(u.value));
            return s;
        }
        catch { return null; }
    }

    // App start: a truly corrupt file is kept as state.json.bad, never silently overwritten.
    public static HiderState LoadState()
    {
        for (int i = 0; i < 5; i++)
        {
            var s = TryLoadState();
            if (s != null) return s;
            Thread.Sleep(100);
        }
        try { File.Copy(StateFile, StateFile + ".bad", true); } catch { }
        return new HiderState();
    }

    public static void SaveState(HiderState s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
        var tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, Json().Serialize(s));
        // Discord polls this file and antivirus scans it, so the swap can briefly be refused.
        for (int i = 0; ; i++)
        {
            try
            {
                if (File.Exists(StateFile)) File.Replace(tmp, StateFile, null);
                else File.Move(tmp, StateFile);
                return;
            }
            catch (Exception)
            {
                if (i >= 200) throw;
                Thread.Sleep(5);
            }
        }
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

    // The newest version is the one Discord runs; an older patched folder doesn't count.
    static string LatestResources(string flavor)
    {
        return ResourceDirs(flavor).OrderByDescending(res =>
        {
            System.Version v;
            return System.Version.TryParse(Path.GetFileName(Path.GetDirectoryName(res)).Substring(4), out v) ? v : new System.Version();
        }).FirstOrDefault();
    }

    public static bool IsInstalled()
    {
        if (!File.Exists(Path.Combine(DistDir, "patcher.js"))) return false;
        bool any = false;
        foreach (var f in InstalledDiscords())
        {
            var res = LatestResources(f);
            if (res == null) continue;
            var app = Path.Combine(res, "app.asar");
            if (!File.Exists(Path.Combine(res, "_app.asar")) || !File.Exists(app) || new FileInfo(app).Length >= 4096
                || !File.ReadAllText(app).Contains("DiscordNameHider"))
                return false;
            any = true;
        }
        return any;
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
        File.WriteAllText(Path.Combine(DistDir, "version.txt"), Version); // read by the plugin's update check
    }

    // Writes a minimal asar archive whose index.js loads our patcher.
    static void WriteLoaderAsar(string path)
    {
        var patcher = Path.Combine(DistDir, "patcher.js");
        var index = Encoding.UTF8.GetBytes("require(" + Json().Serialize(patcher) + ");");
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
                s = Json().DeserializeObject(File.ReadAllText(VencordSettings)) as Dictionary<string, object>;
        }
        catch { }
        if (s == null)
        {
            // Unreadable settings are the user's data: keep a copy before starting fresh.
            if (File.Exists(VencordSettings)) File.Copy(VencordSettings, VencordSettings + ".bad", true);
            s = new Dictionary<string, object>();
        }

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
        File.WriteAllText(VencordSettings, Json().Serialize(s));
    }

    public static void Install()
    {
        var flavors = InstalledDiscords();
        if (flavors.Count == 0) throw new Exception("Discord desktop app was not found on this PC.");

        CloseDiscord();
        try
        {
            ExtractDist();
            WriteVencordSettings(true);
            if (!File.Exists(StateFile)) SaveState(new HiderState());

            int patched = 0;
            foreach (var f in flavors)
                foreach (var res in ResourceDirs(f))
                    if (PatchVersion(res)) patched++;
            if (patched == 0) throw new Exception("Could not find Discord's app files to patch.");
        }
        finally
        {
            foreach (var f in flavors) StartDiscord(f);
        }
    }

    // Whatever fails (antivirus, locks), Discord must be left with a working app.asar.
    static bool PatchVersion(string res)
    {
        var app = Path.Combine(res, "app.asar");
        var orig = Path.Combine(res, "_app.asar");
        if (!File.Exists(app) && !File.Exists(orig)) return false;

        var loader = app + ".new";
        WriteLoaderAsar(loader);
        try
        {
            if (!File.Exists(orig)) File.Move(app, orig);
            if (File.Exists(app)) File.Replace(loader, app, null);
            else File.Move(loader, app);
            return true;
        }
        catch
        {
            if (!File.Exists(app) && File.Exists(orig)) File.Move(orig, app);
            if (File.Exists(loader)) File.Delete(loader);
            throw;
        }
    }

    public static void Uninstall()
    {
        CloseDiscord();
        try
        {
            foreach (var f in InstalledDiscords())
                foreach (var res in ResourceDirs(f))
                {
                    var app = Path.Combine(res, "app.asar");
                    var orig = Path.Combine(res, "_app.asar");
                    if (!File.Exists(orig)) continue;
                    if (File.Exists(app)) File.Replace(orig, app, null);
                    else File.Move(orig, app);
                }
            try { WriteVencordSettings(false); } catch { }
            try { Directory.Delete(DistDir, true); } catch { }
        }
        finally
        {
            foreach (var f in InstalledDiscords()) StartDiscord(f);
        }
    }
}

/* ======================= UI ======================= */

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(17, 18, 20);
    public static readonly Color Card = Color.FromArgb(28, 29, 33);
    public static readonly Color CardHover = Color.FromArgb(37, 38, 44);
    public static readonly Color Input = Color.FromArgb(28, 29, 33);
    public static readonly Color Border = Color.FromArgb(46, 48, 54);
    public static readonly Color Text = Color.FromArgb(242, 243, 245);
    public static readonly Color Muted = Color.FromArgb(148, 155, 164);
    public static readonly Color Accent = Color.FromArgb(88, 101, 242);
    public static readonly Color AccentHover = Color.FromArgb(108, 120, 248);
    public static readonly Color Green = Color.FromArgb(35, 165, 90);
    public static readonly Color Red = Color.FromArgb(242, 63, 66);
    public static readonly Color Amber = Color.FromArgb(240, 178, 50);
    public static readonly Color Off = Color.FromArgb(70, 72, 80);
    static readonly Color[] Avatars =
    {
        Color.FromArgb(88, 101, 242), Color.FromArgb(235, 69, 158), Color.FromArgb(35, 165, 90),
        Color.FromArgb(240, 140, 50), Color.FromArgb(155, 89, 182), Color.FromArgb(26, 188, 156)
    };

    public static Color AvatarFor(string name)
    {
        int h = 0;
        foreach (var c in name) h = unchecked(h * 31 + c);
        return Avatars[(h & int.MaxValue) % Avatars.Length];
    }

    // Segoe UI Variable is the Windows 11 font; plain Segoe UI on Windows 10.
    static readonly string Family = new System.Drawing.Text.InstalledFontCollection().Families.Any(f => f.Name == "Segoe UI Variable Text")
        ? "Segoe UI Variable Text" : "Segoe UI";
    public static Font F(float size, bool bold = false)
    {
        return new Font(Family, size, bold ? FontStyle.Bold : FontStyle.Regular);
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, RectangleF r, float radius, Color fill, Color? border = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Round(r, radius))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            if (border.HasValue) using (var pen = new Pen(border.Value)) g.DrawPath(pen, p);
        }
    }

    // progress: 0 = off, 1 = on.
    public static void DrawSwitch(Graphics g, RectangleF r, float progress, Color onColor)
    {
        Color track = Color.FromArgb(
            (int)(Off.R + (onColor.R - Off.R) * progress),
            (int)(Off.G + (onColor.G - Off.G) * progress),
            (int)(Off.B + (onColor.B - Off.B) * progress));
        FillRound(g, r, r.Height / 2, track);
        float pad = r.Height * 0.14f, knob = r.Height - pad * 2;
        float x = r.X + pad + (r.Width - knob - pad * 2) * progress;
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x, r.Y + pad, knob, knob);
    }
}

class ToggleSwitch : Control
{
    bool on;
    float progress;
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
    public event EventHandler Toggled;

    public bool On
    {
        get { return on; }
        set { if (on != value) { on = value; anim.Start(); } }
    }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        anim.Tick += (s, e) =>
        {
            float target = on ? 1 : 0;
            progress += Math.Sign(target - progress) * 0.15f;
            if (Math.Abs(target - progress) < 0.15f) { progress = target; anim.Stop(); }
            Invalidate();
        };
    }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        On = !On;
        if (Toggled != null) Toggled(this, EventArgs.Empty);
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        Theme.DrawSwitch(e.Graphics, new RectangleF(1, 1, Width - 3, Height - 3), progress, Theme.Green);
        if (Focused) using (var p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f))
            using (var pen = new Pen(Theme.Accent)) e.Graphics.DrawPath(pen, p);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) anim.Dispose();
        base.Dispose(disposing);
    }
}

// Subclass of Button: keeps keyboard, focus and screen-reader behaviour, only the look changes.
class ModernButton : Button
{
    public Color Fill = Theme.Accent, FillHover = Theme.AccentHover, FillBorder = Color.Empty, TextColor = Color.White;
    public float Radius = 9;
    bool hover;

    public ModernButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = Theme.F(9.5f, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent.BackColor);
        Color? border = Focused ? Theme.Accent : FillBorder.IsEmpty ? (Color?)null : FillBorder;
        Theme.FillRound(g, new RectangleF(0, 0, Width - 1, Height - 1), Radius, hover ? FillHover : Fill, border);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? TextColor : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

class Card : Panel
{
    public float Radius = 12;
    public Color BorderColor = Theme.Border;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent.BackColor);
        Theme.FillRound(e.Graphics, new RectangleF(0, 0, Width - 1, Height - 1), Radius, BackColor, BorderColor);
    }
}

// Own-drawn people list. Only visible rows are drawn, so huge lists stay instant.
// shortcut: screen readers see the list as one control, not each row; upgrade with a custom AccessibleObject if asked.
class PeopleList : Control
{
    public event Action<Person> PersonToggled, PersonRemoved;

    List<Person> items = new List<Person>();
    bool dimmed, dragging;
    int offset, hover = -1, focusRow;
    readonly int rowH, pad, thumbW;
    readonly Font nameFont = Theme.F(10.5f, true), subFont = Theme.F(8.75f), avatarFont = Theme.F(11f, true), bigFont = Theme.F(11f, true);

    public PeopleList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "People. Up and Down to move, Space to hide or show, Delete to remove.";
        BackColor = Theme.Card;
        float s;
        using (var g = CreateGraphics()) s = g.DpiX / 96f;
        rowH = (int)(58 * s); pad = (int)(8 * s); thumbW = (int)(5 * s);
    }

    int TotalHeight { get { return items.Count * rowH + pad * 2; } }
    int MaxOffset { get { return Math.Max(0, TotalHeight - Height); } }

    public void SetItems(List<Person> people, bool enabled)
    {
        items = people;
        dimmed = !enabled;
        offset = Math.Min(offset, MaxOffset);
        focusRow = Math.Max(0, Math.Min(focusRow, items.Count - 1));
        Invalidate();
    }

    Rectangle RowRect(int i) { return new Rectangle(pad, pad + i * rowH - offset, Width - pad * 2 - thumbW, rowH - 2); }

    public Rectangle ToggleRect(int i)
    {
        var r = RowRect(i);
        int w = (int)(rowH * 0.72), h = (int)(rowH * 0.4);
        return new Rectangle(r.Right - w - pad * 2, r.Y + (r.Height - h) / 2, w, h);
    }

    public Rectangle RemoveRect(int i)
    {
        var t = ToggleRect(i);
        int size = t.Height;
        return new Rectangle(t.X - size - pad, t.Y, size, size);
    }

    int RowAt(Point p)
    {
        int i = (p.Y + offset - pad) / rowH;
        return p.Y + offset < pad || i < 0 || i >= items.Count ? -1 : i;
    }

    protected override bool IsInputKey(Keys k)
    {
        return k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (items.Count == 0) return;
        if (e.KeyCode == Keys.Up) focusRow = Math.Max(0, focusRow - 1);
        else if (e.KeyCode == Keys.Down) focusRow = Math.Min(items.Count - 1, focusRow + 1);
        else if (e.KeyCode == Keys.Space) { if (PersonToggled != null) PersonToggled(items[focusRow]); return; }
        else if (e.KeyCode == Keys.Delete) { if (PersonRemoved != null) PersonRemoved(items[focusRow]); return; }
        else return;
        var r = RowRect(focusRow);
        if (r.Y < 0) ScrollTo(offset + r.Y - pad);
        else if (r.Bottom > Height) ScrollTo(offset + r.Bottom - Height + pad);
        Invalidate();
    }

    void ScrollTo(int value)
    {
        offset = Math.Max(0, Math.Min(MaxOffset, value));
        Invalidate();
    }

    void DragThumb(int y)
    {
        ScrollTo(MaxOffset == 0 ? 0 : (int)((long)y * MaxOffset / Math.Max(1, Height)));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollTo(offset - Math.Sign(e.Delta) * rowH * 2);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (MaxOffset > 0 && e.X >= Width - thumbW * 3) { dragging = true; DragThumb(e.Y); return; }
        int i = RowAt(e.Location);
        if (i < 0) return;
        focusRow = i;
        if (ToggleRect(i).Contains(e.Location)) { if (PersonToggled != null) PersonToggled(items[i]); }
        else if (RemoveRect(i).Contains(e.Location)) { hover = -1; if (PersonRemoved != null) PersonRemoved(items[i]); }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) { dragging = false; base.OnMouseUp(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) { DragThumb(e.Y); return; }
        int i = RowAt(e.Location);
        Cursor = i >= 0 && (ToggleRect(i).Contains(e.Location) || RemoveRect(i).Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        if (i != hover) { hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (items.Count == 0)
        {
            var mid = Height / 2;
            TextRenderer.DrawText(g, "Nobody on your list yet", bigFont, new Rectangle(0, mid - rowH / 2, Width, rowH / 2), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
            TextRenderer.DrawText(g, "Add a username or User ID below", subFont, new Rectangle(0, mid + 2, Width, rowH / 2), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
            return;
        }

        int first = Math.Max(0, (offset - pad) / rowH), last = Math.Min(items.Count - 1, (offset + Height) / rowH);
        for (int i = first; i <= last; i++) PaintRow(g, i);

        if (MaxOffset > 0)
        {
            int h = Math.Max(thumbW * 6, (int)((long)Height * Height / TotalHeight));
            int y = (int)((long)(Height - h) * offset / MaxOffset);
            Theme.FillRound(g, new RectangleF(Width - thumbW - 2, y + 2, thumbW, h - 4), thumbW / 2f, Theme.Off);
        }
    }

    void PaintRow(Graphics g, int i)
    {
        var p = items[i];
        var r = RowRect(i);
        if (i == hover) Theme.FillRound(g, r, 9, Theme.CardHover);
        if (Focused && i == focusRow) Theme.FillRound(g, r, 9, i == hover ? Theme.CardHover : BackColor, Theme.Accent);

        // Avatar with first letter
        int a = (int)(rowH * 0.64), ax = r.X + pad, ay = r.Y + (r.Height - a) / 2;
        var name = p.value.Length > 120 ? p.value.Substring(0, 120) : p.value;
        var color = dimmed || !p.hidden ? Theme.Off : Theme.AvatarFor(p.value);
        using (var b = new SolidBrush(color)) g.FillEllipse(b, ax, ay, a, a);
        char c = name.TrimStart('@').FirstOrDefault();
        string letter = char.IsDigit(c) ? "#" : char.IsLetter(c) ? char.ToUpper(c).ToString() : "?";
        TextRenderer.DrawText(g, letter, avatarFont, new Rectangle(ax, ay, a, a), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // Name + status line
        var sw = ToggleRect(i);
        int tx = ax + a + pad + pad / 2, tw = (i == hover ? RemoveRect(i).X : sw.X) - tx - pad;
        var status = p.hidden && !dimmed ? "Hidden" : dimmed && p.hidden ? "Paused" : "Visible";
        var sub = string.IsNullOrEmpty(p.label) ? status : status + "  ·  " + p.label;
        int lineH = r.Height / 2;
        TextRenderer.DrawText(g, name, nameFont, new Rectangle(tx, r.Y + lineH / 6, tw, lineH), dimmed ? Theme.Muted : Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, sub, subFont, new Rectangle(tx, r.Y + lineH + lineH / 6, tw, lineH), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        Theme.DrawSwitch(g, sw, p.hidden ? 1 : 0, dimmed ? Color.FromArgb(44, 100, 70) : Theme.Green);

        if (i == hover)
        {
            var x = RemoveRect(i);
            int m = x.Width / 3;
            using (var pen = new Pen(Theme.Muted, 1.6f))
            {
                g.DrawLine(pen, x.X + m, x.Y + m, x.Right - m, x.Bottom - m);
                g.DrawLine(pen, x.Right - m, x.Y + m, x.X + m, x.Bottom - m);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { nameFont.Dispose(); subFont.Dispose(); avatarFont.Dispose(); bigFont.Dispose(); }
        base.Dispose(disposing);
    }
}

class MainForm : Form
{
    HiderState state;
    DateTime lastWrite;
    bool needsUpdate, recheck = true, confirmUninstall;

    readonly ToggleSwitch master;
    readonly Card masterCard;
    readonly Label masterText, masterSub, installBadge, footer, countLabel;
    readonly PeopleList people;
    readonly TextBox addValue, addLabel;
    readonly ModernButton installBtn;
    readonly System.Windows.Forms.Timer watch;
    readonly Image logo;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    public MainForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        Text = "Discord Name Hider";
        ClientSize = new Size(500, 704);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.F(9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.icon.png"))
            if (s != null) logo = new Bitmap(s);

        // Header: the logo is painted in OnPaint
        Controls.Add(new Label { Text = "Discord Name Hider", Font = Theme.F(16f, true), AutoSize = true, Location = new Point(82, 20), BackColor = Theme.Bg });
        installBadge = new Label { AutoSize = true, Location = new Point(84, 52), Font = Theme.F(9f), BackColor = Theme.Bg };
        Controls.Add(installBadge);
        var updateLink = new ModernButton { Location = new Point(332, 28), Size = new Size(144, 34), Visible = false, Font = Theme.F(9f, true) };
        updateLink.Click += (s, e) => Process.Start(Hider.DownloadPage);
        Controls.Add(updateLink);
        Task.Factory.StartNew(() => Hider.LatestRelease()).ContinueWith(t =>
        {
            if (t.Result == null || IsDisposed) return;
            updateLink.Text = "Get v" + t.Result;
            updateLink.Visible = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());

        // Hide everyone
        masterCard = new Card { Location = new Point(24, 88), Size = new Size(452, 78) };
        masterText = new Label { Text = "Hide everyone", Font = Theme.F(12.5f, true), AutoSize = true, Location = new Point(18, 14), BackColor = Theme.Card };
        masterSub = new Label { ForeColor = Theme.Muted, AutoSize = false, Location = new Point(19, 42), Size = new Size(340, 22), Font = Theme.F(9f), BackColor = Theme.Card };
        master = new ToggleSwitch { Location = new Point(378, 24), Size = new Size(56, 30), BackColor = Theme.Card, AccessibleName = "Hide everyone" };
        master.Toggled += (s, e) => { state.enabled = master.On; Save(); };
        masterCard.Controls.AddRange(new Control[] { masterText, masterSub, master });
        Controls.Add(masterCard);

        // People heading
        Controls.Add(new Label { Text = "People", Font = Theme.F(11f, true), AutoSize = true, Location = new Point(26, 182), BackColor = Theme.Bg });
        countLabel = new Label { ForeColor = Theme.Muted, AutoSize = true, Location = new Point(84, 185), Font = Theme.F(9f), BackColor = Theme.Bg };
        Controls.Add(countLabel);
        var allOn = Ghost("Hide all", new Point(324, 178), new Size(74, 28));
        allOn.Click += (s, e) => SetAll(true);
        var allOff = Ghost("Show all", new Point(402, 178), new Size(74, 28));
        allOff.Click += (s, e) => SetAll(false);
        Controls.AddRange(new Control[] { allOn, allOff });

        // People list
        var listCard = new Card { Location = new Point(24, 214), Size = new Size(452, 278) };
        people = new PeopleList { Location = new Point(3, 3), Size = new Size(446, 272) };
        people.PersonToggled += p => { p.hidden = !p.hidden; Save(); };
        people.PersonRemoved += RemovePerson;
        listCard.Controls.Add(people);
        Controls.Add(listCard);

        // Add row
        addValue = MakeInput(new Point(24, 504), 216, "Username or User ID");
        addLabel = MakeInput(new Point(248, 504), 136, "Note (optional)");
        var addBtn = new ModernButton { Text = "Add", Location = new Point(392, 504), Size = new Size(84, 44) };
        addBtn.Click += (s, e) => AddPerson();
        addValue.KeyDown += EnterAdds;
        addLabel.KeyDown += EnterAdds;
        Controls.Add(addBtn);

        Controls.Add(new Label
        {
            Text = "User IDs work best: Discord > Settings > Advanced > Developer Mode, then right-click a person > Copy User ID.  Ctrl+Shift+H in Discord switches hiding on or off.",
            ForeColor = Theme.Muted, Font = Theme.F(8.5f), Location = new Point(26, 560), Size = new Size(450, 52), BackColor = Theme.Bg
        });

        // Footer
        installBtn = new ModernButton { Location = new Point(24, 628), Size = new Size(190, 44) };
        installBtn.Click += (s, e) => InstallOrUninstall();
        installBtn.MouseLeave += (s, e) => { if (confirmUninstall) { confirmUninstall = false; RefreshStatus(); } };
        footer = new Label { ForeColor = Theme.Muted, Location = new Point(226, 628), Size = new Size(250, 44), TextAlign = ContentAlignment.MiddleLeft, Font = Theme.F(9f), BackColor = Theme.Bg };
        Controls.AddRange(new Control[] { installBtn, footer });
        ResumeLayout(false);
        ActiveControl = addValue;

        state = Hider.LoadState();
        if (File.Exists(Hider.StateFile)) lastWrite = File.GetLastWriteTimeUtc(Hider.StateFile);
        RefreshAll();

        // Pick up changes made from inside Discord (Ctrl+Shift+H).
        watch = new System.Windows.Forms.Timer { Interval = 1000 };
        watch.Tick += (s, e) =>
        {
            try
            {
                if (MouseButtons != MouseButtons.None || !File.Exists(Hider.StateFile)) return; // not mid-click
                var written = File.GetLastWriteTimeUtc(Hider.StateFile);
                if (written == lastWrite) return;
                var fresh = Hider.TryLoadState();
                if (fresh == null) return; // half-written: keep what we show, retry next tick
                lastWrite = written;
                state = fresh;
                RefreshAll();
            }
            catch { }
        };
        watch.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark title bar matching the window (Windows ignores what it doesn't support).
        int dark = 1, caption = ColorTranslator.ToWin32(Theme.Bg);
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (logo == null) return;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float s = e.Graphics.DpiX / 96f;
        e.Graphics.DrawImage(logo, 24 * s, 22 * s, 48 * s, 48 * s);
    }

    void EnterAdds(object sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        AddPerson();
        e.SuppressKeyPress = true;
    }

    ModernButton Ghost(string text, Point loc, Size size)
    {
        return new ModernButton
        {
            Text = text, Location = loc, Size = size, Fill = Theme.Bg, FillHover = Theme.CardHover, FillBorder = Theme.Border,
            TextColor = Theme.Text, Radius = 8, Font = Theme.F(8.75f, true)
        };
    }

    TextBox MakeInput(Point loc, int width, string placeholder)
    {
        var host = new Card { Location = loc, Size = new Size(width, 44), Radius = 9, BackColor = Theme.Input };
        var tb = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.F(10.5f), Location = new Point(12, 12), Width = width - 24, AccessibleName = placeholder };
        host.Controls.Add(tb);
        host.Click += (s, e) => tb.Focus();
        tb.Enter += (s, e) => { host.BorderColor = Theme.Accent; host.Invalidate(); };
        tb.Leave += (s, e) => { host.BorderColor = Theme.Border; host.Invalidate(); };
        tb.HandleCreated += (s, e) => SendMessage(tb.Handle, 0x1501, (IntPtr)1, placeholder); // EM_SETCUEBANNER
        Controls.Add(host);
        return tb;
    }

    void RefreshAll()
    {
        people.SetItems(state.users, state.enabled);
        RefreshStatus();
    }

    void RefreshStatus()
    {
        master.On = state.enabled;
        masterCard.BorderColor = state.enabled ? Theme.Green : Theme.Border;
        masterCard.Invalidate();
        masterText.ForeColor = state.enabled ? Theme.Text : Theme.Muted;
        var count = state.users.Count(u => u.hidden);
        masterSub.Text = state.enabled
            ? count + " of " + state.users.Count + " people hidden right now"
            : "Paused: everyone is visible";
        countLabel.Text = state.users.Count.ToString();

        bool installed = Hider.IsInstalled();
        bool hasDiscord = Hider.InstalledDiscords().Count > 0;
        if (recheck) { needsUpdate = installed && Hider.NeedsUpdate(); recheck = false; }
        installBadge.Text = !hasDiscord ? "●  Discord app not found"
            : needsUpdate ? "●  New version ready: click Update"
            : installed ? "●  Active in Discord" : "●  Not installed in Discord yet";
        installBadge.ForeColor = !hasDiscord ? Theme.Red : installed && !needsUpdate ? Theme.Green : Theme.Amber;

        bool primary = !installed || needsUpdate;
        installBtn.Text = confirmUninstall ? "Click again to uninstall" : needsUpdate ? "Update" : installed ? "Uninstall from Discord" : "Install into Discord";
        installBtn.Fill = confirmUninstall ? Theme.Red : primary ? Theme.Accent : Theme.Bg;
        installBtn.FillHover = confirmUninstall ? Theme.Red : primary ? Theme.AccentHover : Theme.CardHover;
        installBtn.FillBorder = primary || confirmUninstall ? Color.Empty : Theme.Border;
        installBtn.Invalidate();
        installBtn.Enabled = hasDiscord;
        if (!installed && hasDiscord && footer.Text.Length == 0) footer.Text = "One-time setup. Discord restarts once.";
    }

    void Save()
    {
        try
        {
            Hider.SaveState(state);
            lastWrite = File.GetLastWriteTimeUtc(Hider.StateFile);
            footer.Text = "";
            footer.ForeColor = Theme.Muted;
            RefreshAll();
        }
        catch (Exception ex) { footer.Text = "Could not save: " + ex.Message; footer.ForeColor = Theme.Red; }
    }

    void AddPerson()
    {
        string duplicate = null;
        bool added = false;
        foreach (var raw in addValue.Text.Split(','))
        {
            var v = raw.Trim();
            var mention = Regex.Match(v, @"^<@!?(\d+)>$"); // pasted from Discord
            v = (mention.Success ? mention.Groups[1].Value : v).TrimStart('@').Trim();
            if (v.Length == 0) continue;
            if (state.users.Any(u => string.Equals(u.value, v, StringComparison.OrdinalIgnoreCase))) { duplicate = v; continue; }
            state.users.Add(new Person { value = v, label = addLabel.Text.Trim(), hidden = true });
            added = true;
        }
        if (added)
        {
            addValue.Clear(); addLabel.Clear(); addValue.Focus();
            Save();
        }
        if (duplicate != null) { footer.Text = duplicate + " is already in the list."; footer.ForeColor = Theme.Muted; }
    }

    void RemovePerson(Person p)
    {
        state.users.Remove(p);
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
        if (installed && !confirmUninstall)
        {
            confirmUninstall = true;
            RefreshStatus();
            return;
        }
        confirmUninstall = false;

        UseWaitCursor = true; Enabled = false;
        footer.ForeColor = Theme.Muted;
        footer.Text = "Working... Discord will close and reopen.";
        Refresh();
        try
        {
            if (installed) Hider.Uninstall(); else Hider.Install();
            footer.ForeColor = Theme.Green;
            footer.Text = installed ? "Removed. Discord is back to normal." : "Done! Discord is restarting.";
        }
        catch (Exception ex) { footer.ForeColor = Theme.Red; footer.Text = "Error: " + ex.Message; }
        finally { Enabled = true; UseWaitCursor = false; recheck = true; RefreshAll(); }
    }
}
