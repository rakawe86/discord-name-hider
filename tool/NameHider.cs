// Discord Name Hider - one-file Windows tool.
// Installs a bundled Vencord build (with the HideUsers plugin) into Discord
// and lets you set which people to hide. No Node/Git needed on the target PC.

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

static class Hider
{
    static readonly string LocalApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string RoamingApp = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static readonly string DistDir = Path.Combine(LocalApp, "DiscordNameHider", "dist");
    static readonly string SettingsFile = Path.Combine(RoamingApp, "Vencord", "settings", "settings.json");
    static readonly string[] Flavors = { "Discord", "DiscordPTB", "DiscordCanary" };
    static readonly string[] DistFiles = { "patcher.js", "preload.js", "renderer.js", "renderer.css" };

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

    public static bool CloseDiscord()
    {
        bool wasRunning = false;
        foreach (var f in Flavors)
            foreach (var p in Process.GetProcessesByName(f))
            {
                wasRunning = true;
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
        if (wasRunning) Thread.Sleep(1500);
        return wasRunning;
    }

    public static void StartDiscord(string flavor)
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

    static Dictionary<string, object> ReadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var obj = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(SettingsFile)) as Dictionary<string, object>;
                if (obj != null) return obj;
            }
        }
        catch { }
        return new Dictionary<string, object>();
    }

    public static string ReadHiddenUsers()
    {
        object plugins, hu, users;
        var s = ReadSettings();
        if (s.TryGetValue("plugins", out plugins) && plugins is Dictionary<string, object>
            && ((Dictionary<string, object>)plugins).TryGetValue("HideUsers", out hu) && hu is Dictionary<string, object>
            && ((Dictionary<string, object>)hu).TryGetValue("users", out users) && users is string)
            return string.Join(Environment.NewLine, ((string)users).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
        return "";
    }

    static void WriteSettings(string users, bool enabled)
    {
        var s = ReadSettings();
        object pluginsObj;
        var plugins = s.TryGetValue("plugins", out pluginsObj) && pluginsObj is Dictionary<string, object>
            ? (Dictionary<string, object>)pluginsObj : new Dictionary<string, object>();
        plugins["HideUsers"] = new Dictionary<string, object> { { "enabled", enabled }, { "users", users } };
        s["plugins"] = plugins;
        // Keep the bundled build: Vencord's own updater would replace it and drop HideUsers.
        s["autoUpdate"] = false;
        s["autoUpdateNotification"] = false;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
        File.WriteAllText(SettingsFile, new JavaScriptSerializer().Serialize(s));
    }

    public static string Apply(string userText)
    {
        var users = string.Join(", ", userText
            .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0));

        var flavors = InstalledDiscords();
        if (flavors.Count == 0) throw new Exception("Discord desktop app was not found on this PC.");

        CloseDiscord();
        ExtractDist();
        WriteSettings(users, true);

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
        return users;
    }

    public static void Remove()
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
        try { WriteSettings("", false); } catch { }
        try { Directory.Delete(Path.GetDirectoryName(DistDir), true); } catch { }
        foreach (var f in InstalledDiscords()) StartDiscord(f);
    }
}

class MainForm : Form
{
    readonly TextBox box;
    readonly Label status;
    static readonly Color Bg = Color.FromArgb(49, 51, 56);
    static readonly Color Panel = Color.FromArgb(30, 31, 34);
    static readonly Color Blurple = Color.FromArgb(88, 101, 242);

    public MainForm()
    {
        Text = "Discord Name Hider";
        ClientSize = new Size(440, 430);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var title = new Label { Text = "Hide people in Discord", Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(18, 14) };
        var help = new Label
        {
            Text = "One per line: username or User ID.\nUser ID is best (Discord > Settings > Advanced > Developer Mode,\nthen right-click a person > Copy User ID).",
            ForeColor = Color.FromArgb(181, 186, 193), AutoSize = true, Location = new Point(20, 50), Font = new Font("Segoe UI", 9f)
        };
        box = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(20, 112), Size = new Size(400, 180),
            BackColor = Panel, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 11f),
            Text = Hider.ReadHiddenUsers()
        };
        var hide = MakeButton("Hide them", Blurple, new Point(20, 306), new Size(400, 42));
        hide.Click += (s, e) => Run(() =>
        {
            var u = Hider.Apply(box.Text);
            return u.Length == 0 ? "Done. Nobody is hidden (list is empty)." : "Done! Discord restarted. Hidden: " + u;
        });
        var remove = MakeButton("Remove tool / show everyone", Color.FromArgb(78, 80, 88), new Point(20, 356), new Size(400, 32));
        remove.Click += (s, e) =>
        {
            if (MessageBox.Show("Restore Discord to normal?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            Run(() => { Hider.Remove(); return "Removed. Discord is back to normal."; });
        };
        status = new Label { AutoSize = false, Location = new Point(20, 396), Size = new Size(400, 30), ForeColor = Color.FromArgb(181, 186, 193), Font = new Font("Segoe UI", 9f) };

        if (Hider.InstalledDiscords().Count == 0) status.Text = "Discord desktop app not found.";

        Controls.AddRange(new Control[] { title, help, box, hide, remove, status });
    }

    Button MakeButton(string text, Color color, Point loc, Size size)
    {
        var b = new Button { Text = text, Location = loc, Size = size, FlatStyle = FlatStyle.Flat, BackColor = color, ForeColor = Color.White, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    void Run(Func<string> action)
    {
        UseWaitCursor = true;
        Enabled = false;
        status.Text = "Working... (Discord will close and reopen)";
        Refresh();
        try { status.Text = action(); status.ForeColor = Color.FromArgb(35, 165, 90); }
        catch (Exception ex) { status.Text = "Error: " + ex.Message; status.ForeColor = Color.FromArgb(242, 63, 67); }
        finally { Enabled = true; UseWaitCursor = false; }
    }
}
