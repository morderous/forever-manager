// Forever Manager: keeps WoW Forever's addons up to date from the Forever
// pack, whose pack.json lists them: its own releases (from the friends'
// server, see Friends), public GitHub releases and CurseForge files. A
// variant of ForeverUI Manager for a few friends: the friends' server key
// and the CurseForge key come inside the exe (masked by the build, see
// make_secrets.ps1; no GitHub token since 1.0.8). Every download is checked
// against the hash its source gives before it goes in AddOns, and only the
// folders this app put in AddOns are ever touched.
//
// Built with the C# compiler that comes with Windows (build.bat). C# 5 only:
// no $"", no ?., no => members.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Forever Manager")]
[assembly: AssemblyProduct("Forever Manager")]
[assembly: AssemblyDescription("Keeps WoW Forever addons up to date from the Forever pack")]
[assembly: AssemblyCompany("morderous")]
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]

namespace ForeverManager
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Native.SetProcessDPIAware();
            bool hidden = Array.IndexOf(args, "--tray") >= 0;
            // Started again by an update: the one before is let go first.
            int after = Array.IndexOf(args, "--after-update");
            if (after >= 0 && after + 1 < args.Length) SelfUpdate.WaitFor(args[after + 1]);
            bool owned;
            using (Mutex single = new Mutex(true, Core.TestMode ? "ForeverManager.Single.Test" : "ForeverManager.Single", out owned))
            {
                if (!owned)
                {
                    // Already running: the one open shows itself.
                    Native.PostMessage((IntPtr)0xffff, Native.ShowMe, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                // A new version downloaded before: swapped in now, and started instead.
                if (SelfUpdate.ApplyAtStart(args)) return 0;
                SelfUpdate.CleanOld();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(hidden));
                // "Restart to update": the new version swapped in and started, now.
                if (SelfUpdate.RestartAsked) SelfUpdate.ApplyAtStart(args);
                return 0;
            }
        }
    }

    static class Native
    {
        public static readonly int ShowMe = RegisterWindowMessage(Core.TestMode ? "ForeverManager.ShowMe.Test" : "ForeverManager.ShowMe");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Windows' dark scroll bars for a list ("DarkMode_Explorer").
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        // The shell's delete with undo (to the Recycle Bin), one layout for
        // 64-bit and one packed for 32-bit.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct FileOp64
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom, pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        public struct FileOp32
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom, pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        public static extern int FileOperation64(ref FileOp64 op);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        public static extern int FileOperation32(ref FileOp32 op);
    }

    // Settings, the log and the app's own folder. FUM_HOME (tests) puts the
    // folder elsewhere and keeps the app off the Windows Run key.
    static class Core
    {
        public const string Version = "1.1.1";
        public const string Author = "morderous";
        public const string AuthorUrl = "https://github.com/morderous";
        public const string KofiUrl = "https://ko-fi.com/morderous";
        public const string Name = "Forever Manager";
        public const string RunName = "ForeverManager";
        public static readonly bool TestMode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FUM_HOME"));
        static string home;
        static Dictionary<string, string> settings;
        static readonly object logLock = new object();

        public static string Env(string name)
        {
            return Environment.GetEnvironmentVariable(name) ?? "";
        }

        public static string Home()
        {
            if (home != null) return home;
            string given = Env("FUM_HOME");
            home = given != "" ? given : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForeverManager");
            Directory.CreateDirectory(home);
            return home;
        }

        public static string Setting(string key, string fallback)
        {
            LoadSettings();
            string value;
            return settings.TryGetValue(key, out value) ? value : fallback;
        }

        public static void SetSetting(string key, string value)
        {
            LoadSettings();
            if (value == null) settings.Remove(key);
            else settings[key] = value;
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, string> each in settings) lines.Add(each.Key + "=" + each.Value);
            lines.Sort(StringComparer.Ordinal);
            WriteAll(Path.Combine(Home(), "settings.ini"), string.Join("\r\n", lines.ToArray()) + "\r\n");
        }

        static void LoadSettings()
        {
            if (settings != null) return;
            settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(Home(), "settings.ini");
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) settings[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }

        // Written beside, then swapped in: a crash never leaves half a file.
        public static void WriteAll(string path, string text)
        {
            WriteAll(path, new UTF8Encoding(false).GetBytes(text));
        }

        public static void WriteAll(string path, byte[] bytes)
        {
            string fresh = path + ".new";
            File.WriteAllBytes(fresh, bytes);
            if (File.Exists(path)) File.Replace(fresh, path, null);
            else File.Move(fresh, path);
        }

        public static void Log(string line)
        {
            try
            {
                lock (logLock)
                {
                    string path = Path.Combine(Home(), "log.txt");
                    if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    {
                        File.Copy(path, path + ".old", true);
                        File.WriteAllText(path, "");
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + "\r\n");
                }
            }
            catch { }
        }

        // Start with Windows: our own value in the user's Run key, pointing at
        // this exe wherever it is now. Never in a test. Off unless the user
        // ticks the box: the key is only read until there is a change to make.
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // What the Run key holds for us now; null when nothing (or in a test).
        static string RunKeyValue()
        {
            if (TestMode) return null;
            try
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunPath)) return run == null ? null : run.GetValue(RunName) as string;
            }
            catch { return null; }
        }

        // Start with Windows when nothing was ever chosen: off, unless an
        // earlier version (on by default until 1.0.8) left its value there.
        public static bool StartWithWindows()
        {
            return Setting("startWithWindows", RunKeyValue() != null ? "1" : "0") == "1";
        }

        public static void SetRunKey(bool on)
        {
            if (TestMode) return;
            try
            {
                string have = RunKeyValue();
                string want = "\"" + Application.ExecutablePath + "\" --tray";
                if (on ? string.Equals(have, want, StringComparison.OrdinalIgnoreCase) : have == null) return;
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunPath, true))
                {
                    if (run == null) return;
                    if (on) run.SetValue(RunName, want);
                    else run.DeleteValue(RunName, false);
                }
            }
            catch (Exception why) { Log("Run key: " + why.Message); }
        }

        public static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static string Sha256Of(string path)
        {
            using (FileStream input = File.OpenRead(path))
            using (SHA256 hash = SHA256.Create())
                return Hex(hash.ComputeHash(input));
        }

        public static string Sha256Of(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
                return Hex(hash.ComputeHash(bytes));
        }

        public static string Bytes(long bytes)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (bytes >= 1073741824) return (bytes / 1073741824.0).ToString("0.00", c) + " GB";
            return bytes >= 1048576 ? (bytes / 1048576.0).ToString("0.0", c) + " MB" : (bytes / 1024.0).ToString("0.0", c) + " KB";
        }

        // "0.3.1" against "0.2.5": the numbers of each part in turn.
        public static int CompareVersions(string a, string b)
        {
            string[] x = Regex.Split((a ?? "").TrimStart('v', 'V'), @"[.\-+ ]"), y = Regex.Split((b ?? "").TrimStart('v', 'V'), @"[.\-+ ]");
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                long m = 0, n = 0;
                if (i < x.Length) long.TryParse(Regex.Match(x[i], @"^\d+").Value, out m);
                if (i < y.Length) long.TryParse(Regex.Match(y[i], @"^\d+").Value, out n);
                if (m != n) return m < n ? -1 : 1;
            }
            return 0;
        }
    }

    // The main window's place in settings.ini: "window=x,y,width,height" (its
    // size and place when not maximized) and "maximized=1". Put back on a
    // screen it is on, never off every screen nor bigger than the screen.
    static class WindowPlace
    {
        public static string Format(Rectangle r)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return r.X.ToString(c) + "," + r.Y.ToString(c) + "," + r.Width.ToString(c) + "," + r.Height.ToString(c);
        }

        // Null when it is not four whole numbers, or the size is not one.
        public static Rectangle? Parse(string text)
        {
            string[] parts = (text ?? "").Split(',');
            if (parts.Length != 4) return null;
            int[] n = new int[4];
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return null;
            if (n[2] <= 0 || n[3] <= 0 || n[2] > 100000 || n[3] > 100000) return null;
            return new Rectangle(n[0], n[1], n[2], n[3]);
        }

        // The saved place on the screen it overlaps most (none: the first, the
        // main one, centred there): no smaller than min, no bigger than that
        // screen's working area, and moved in until it is all inside.
        public static Rectangle Fit(Rectangle saved, Rectangle[] screens, Size min)
        {
            if (screens == null || screens.Length == 0) return saved;
            Rectangle area = screens[0];
            long most = 0;
            foreach (Rectangle s in screens)
            {
                Rectangle both = Rectangle.Intersect(s, saved);
                long room = both.IsEmpty ? 0 : (long)both.Width * both.Height;
                if (room > most) { most = room; area = s; }
            }
            int w = Math.Min(Math.Max(saved.Width, min.Width), area.Width);
            int h = Math.Min(Math.Max(saved.Height, min.Height), area.Height);
            int x = saved.X, y = saved.Y;
            if (most == 0)
            {
                x = area.X + (area.Width - w) / 2;
                y = area.Y + (area.Height - h) / 2;
            }
            x = Math.Max(area.X, Math.Min(x, area.Right - w));
            y = Math.Max(area.Y, Math.Min(y, area.Bottom - h));
            return new Rectangle(x, y, w, h);
        }
    }

    static class Json
    {
        static JavaScriptSerializer Serializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            return serializer;
        }

        public static object Parse(string text) { return Serializer().DeserializeObject(text); }
        public static string Write(object value) { return Serializer().Serialize(value); }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object value;
            return d != null && d.TryGetValue(key, out value) && value != null && !(value is Dictionary<string, object>) && !(value is object[])
                ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        public static bool Bool(Dictionary<string, object> d, string key)
        {
            object value;
            return d != null && d.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        public static long Long(Dictionary<string, object> d, string key)
        {
            object value;
            if (d != null && d.TryGetValue(key, out value) && value != null && !(value is string))
                try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); } catch { }
            return -1;
        }

        public static object[] Arr(Dictionary<string, object> d, string key)
        {
            object value;
            if (d == null || !d.TryGetValue(key, out value)) return new object[0];
            if (value is object[]) return (object[])value;
            if (value is System.Collections.ArrayList) return ((System.Collections.ArrayList)value).ToArray();
            return new object[0];
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object value;
            return d != null && d.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }
    }
    // The secrets the build put in (make_secrets.ps1 writes the other half of
    // this class): the Forever pack's read-only GitHub token and the
    // CurseForge key, each masked with a key of its own made at each build,
    // so neither is in the exe in the clear. Masked is not hidden: whoever
    // has the exe can take them out. Never written to the log.
    static partial class Secrets
    {
        static string Unmask(byte[] masked, byte[] key)
        {
            byte[] plain = new byte[masked.Length];
            for (int i = 0; i < masked.Length; i++) plain[i] = (byte)(masked[i] ^ key[i % key.Length]);
            return Encoding.UTF8.GetString(plain);
        }

        public static string GitHubToken() { return Unmask(TokenMasked, TokenKey); }
        public static string CurseForgeKey() { return Unmask(CurseMasked, CurseKey); }
        public static string FriendsKey() { return Unmask(KeyMasked, KeyKey); }

        // A build without the file had a fake one put in: said, never sent.
        public static bool HasToken { get { return TokenGiven; } }
        public static bool HasCurseForge { get { return CurseGiven; } }
        public static bool HasFriendsKey { get { return KeyGiven; } }
    }

    // One entry of the Forever pack's pack.json: a release series of the
    // pack's own repository, a public GitHub repository, or a CurseForge project.
    class KnownRepo
    {
        public string Repo, Title, Series, Zip;
        public bool Required;           // always on the list, cannot be taken off
        public bool Ticked = true;      // comes ticked (a big one does not)
        public bool Recommended;        // said in its details; ticked or not as "ticked" says
        public bool Private;           // from the pack's own repository: read with the token
        public bool GuidesOnly;         // its zip may hold guide folders only (ForeverGuidesPack_, PolestarPack_)
        public string[] Replaces;       // repositories it takes the place of: they stop being required while it is on
        public long CurseId = -1;       // a CurseForge project ("curseforge:<id>" as its Repo)

        public bool IsCurse { get { return CurseId > 0; } }

        public string Key { get { return TrackedAddon.KeyOf(Repo, Series); } }

        // The addons offered now: the pack's, once it has been read.
        public static KnownRepo[] Addons()
        {
            return Pack.Addons ?? new KnownRepo[0];
        }

        public static KnownRepo Of(string repo)
        {
            return Addons().FirstOrDefault(k => string.Equals(k.Repo, repo, StringComparison.OrdinalIgnoreCase));
        }
    }

    // The Forever pack: pack.json at the root of morderous/forever-addons'
    // default branch, read through GitHub's API with the token the build put
    // in (a private repository has no other way in). The copy last read is
    // kept in the app's folder for when GitHub does not answer. FUM_PACK
    // (tests): a pack.json file read instead; "off" reads nothing new.
    static class Pack
    {
        public const string Repo = "morderous/forever-addons";
        public static KnownRepo[] Addons;   // null: never read
        public static string Text;          // the pack.json in use

        static string HeldPath() { return Path.Combine(Core.Home(), "pack.json"); }

        // The token goes to this repository and nowhere else.
        public static bool Owns(string repo)
        {
            return repo != null && string.Equals(repo, Repo, StringComparison.OrdinalIgnoreCase);
        }

        // At start: the copy kept, if any.
        public static void LoadHeld()
        {
            string path = HeldPath();
            if (!File.Exists(path)) return;
            try { Use(File.ReadAllText(path, Encoding.UTF8), false); }
            catch (Exception why) { Core.Log("pack.json kept: " + why.Message); }
        }

        // pack.json from the repository, as text. Throws, saying why.
        public static string Download()
        {
            string given = Core.Env("FUM_PACK");
            if (given == "off") throw new Exception("not read in this test");
            if (given != "") return File.ReadAllText(given, Encoding.UTF8);
            string served = Friends.PackText();
            if (served != null) return served;
            try { return GitHub.PrivateApi("repos/" + Repo + "/contents/pack.json", "application/vnd.github.raw+json"); }
            catch (PrivateMissing) { throw new Exception("GitHub shows this build's token no pack.json in " + Repo); }
        }

        // A pack.json read (now or at start) put in use, and kept when new.
        // True when it changed what the pack lists.
        public static bool Use(string text, bool keep)
        {
            KnownRepo[] read = Parse(text);
            bool changed = Text == null || Text != text;
            Addons = read;
            Text = text;
            if (keep && changed)
                try { Core.WriteAll(HeldPath(), text); }
                catch (Exception why) { Core.Log("pack.json not kept: " + why.Message); }
            return changed;
        }

        // { "format": 1, "addons": [ { "title", "required", "ticked", and
        // one of: "series" + "zip" (a release of the pack's own repository),
        // "repo" (+ "series", "zip": a public GitHub repository), or
        // "curseforge": <project id> } ]. "guidesOnly" and "replaces" as in
        // ForeverUI Manager. An entry that cannot be taken is left out; a
        // pack with no addon at all is refused.
        public static KnownRepo[] Parse(string text)
        {
            Dictionary<string, object> top = Json.Parse(text) as Dictionary<string, object>;
            if (top == null) throw new Exception("pack.json is not a JSON object");
            if (Json.Long(top, "format") != 1) throw new Exception("pack.json is of a format this version does not read (" + Json.Str(top, "format") + ")");
            List<KnownRepo> addons = new List<KnownRepo>();
            foreach (object each in Json.Arr(top, "addons"))
            {
                KnownRepo k = Entry(each as Dictionary<string, object>);
                if (k != null && !addons.Exists(o => TrackedAddon.SameKey(o.Key, k.Key))) addons.Add(k);
            }
            if (addons.Count == 0) throw new Exception("pack.json lists no addon");
            return addons.ToArray();
        }

        static KnownRepo Entry(Dictionary<string, object> d)
        {
            if (d == null) return null;
            string title = Json.Str(d, "title"), series = Json.Str(d, "series"), zip = Json.Str(d, "zip");
            if (title != null) title = Regex.Replace(title, @"[\x00-\x1f]", "").Trim();
            if (title == "" || (title != null && title.Length > 80)) title = null;
            KnownRepo k;
            if (d.ContainsKey("curseforge"))
            {
                long id = Json.Long(d, "curseforge");
                if (id <= 0 || id > int.MaxValue) { Core.Log("pack.json: " + (title ?? "an entry") + "'s CurseForge id is not a number, left out"); return null; }
                k = new KnownRepo { Repo = CurseForge.RepoOf(id), CurseId = id, Title = title };
            }
            else
            {
                string given = Json.Str(d, "repo");
                string repo = given == null ? Repo : GitHub.ParseRepo(given);
                if (repo == null) return null;
                if (series != null)
                    try { new Regex(series); }
                    catch (ArgumentException) { Core.Log("pack.json: " + repo + "'s series is not a pattern: " + series); return null; }
                if (zip != null && (zip.IndexOfAny(new[] { '/', '\\' }) >= 0 || !zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))) return null;
                k = new KnownRepo { Repo = repo, Title = title, Series = series, Zip = zip };
                k.Private = Owns(repo);
                // The repository ships several things, and its release.json or
                // SHA256SUMS cannot be read without the token: series and zip a must.
                if (k.Private && (series == null || zip == null))
                {
                    Core.Log("pack.json: " + (title ?? "an entry") + " of the pack's own repository says no series or no zip, left out");
                    return null;
                }
                List<string> replaces = new List<string>();
                foreach (object r in Json.Arr(d, "replaces"))
                {
                    string other = GitHub.ParseRepo(r as string);
                    if (other != null && !string.Equals(other, repo, StringComparison.OrdinalIgnoreCase)) replaces.Add(other);
                }
                if (replaces.Count > 0) k.Replaces = replaces.ToArray();
            }
            k.Required = Json.Bool(d, "required");
            k.Ticked = k.Required || !d.ContainsKey("ticked") || Json.Bool(d, "ticked");
            k.Recommended = Json.Bool(d, "recommended");
            k.GuidesOnly = Json.Bool(d, "guidesOnly");
            return k;
        }
    }

    // An addon on the list: from GitHub (the pack's own repository, or a
    // public one) or from CurseForge. Kept in addons.json, never sent anywhere.
    class TrackedAddon
    {
        public string Kind = "github";  // or "curseforge"
        public string Repo;             // owner/name; "curseforge:<id>" for a CurseForge project
        public string Series;           // the tags of its releases (a pattern), when the repository ships several things
        public string ZipPattern;       // its zip's name ("ForeverUI-*.zip")
        public string Tag;              // the version installed by us; null when not
        public string Asset;
        public string[] Folders;        // the folders we put in AddOns: the only ones ever touched
        public string InstalledAt;
        public bool Auto = true;        // updates by itself when a new version comes out
        public bool Trusted;            // the warning before the first install was said yes to
        public string Known;            // the newest version seen
        public string Title;
        public bool Betas;              // test versions (pre-releases) too
        public bool Want = true;        // ticked: Install selected puts it in (one not installed)
        public bool Private;            // from the pack's own repository: read with the token
        public bool GuidesOnly;         // its zip may hold guide folders only
        public string[] Replaces;       // repositories it takes the place of, while it is ticked or installed
        public string ReplacedBy;       // another addon takes its place: no longer required
        public long CurseId = -1;       // a CurseForge project's id
        public string Slug, Author;     // a CurseForge project's name in its link, and its author

        // Not kept: this run's look.
        public Release Latest;
        public string Problem;          // why the last check failed
        public string Found;            // its version, when it is in AddOns but not put there by us
        public bool Missing;            // some of its folders are gone from AddOns
        public string Interface;        // its .toc's Interface line
        public string Busy;             // "Downloading 40%"
        public string Source;           // "pack": an addon of the Forever pack; null: added by hand
        public bool Required;           // the pack's word, read again at each start: cannot be taken off
        public bool Recommended;        // the pack's word too: said in its details, nothing more
        public bool Dev;                // in AddOns by hand, matching none of its releases (a "Dev" tag): Install selected leaves it unless ticked
        public int DevCompared;         // how many of its releases it was compared with
        public string[] DevFolders;     // its folders in AddOns
        public bool Marked;             // installed (or a dev copy): its box, ticked for Uninstall selected (Install selected); this run only

        public bool IsCurse { get { return Kind == "curseforge"; } }

        // One entry per repository and series: Spoken and each of its voice
        // packs come from one repository. "owner/name", or "owner/name <series>"
        // with the series escaped (no space or comma in it).
        public static string KeyOf(string repo, string series)
        {
            return series == null ? repo : repo + " " + Uri.EscapeDataString(series);
        }

        public string Key { get { return KeyOf(Repo, Series); } }

        // The same repository (in any case) and the same series (exactly).
        public static bool SameKey(string a, string b)
        {
            if (a == null || b == null) return false;
            int x = a.IndexOf(' '), y = b.IndexOf(' ');
            string ra = x < 0 ? a : a.Substring(0, x), rb = y < 0 ? b : b.Substring(0, y);
            string sa = x < 0 ? null : a.Substring(x + 1), sb = y < 0 ? null : b.Substring(y + 1);
            return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase) && sa == sb;
        }

        public bool Is(KnownRepo k) { return SameKey(Key, k.Key); }

        // An addon of the pack, new on the list: ticked as the pack says; a
        // big one (not ticked) does not update by itself either.
        public static TrackedAddon ForItem(KnownRepo k)
        {
            TrackedAddon a = new TrackedAddon { Repo = k.Repo, Source = "pack" };
            if (k.IsCurse) { a.Kind = "curseforge"; a.CurseId = k.CurseId; }
            a.Take(k);
            a.Want = k.Ticked;
            a.Auto = k.Ticked;
            return a;
        }

        public static TrackedAddon ForCurse(long id)
        {
            return new TrackedAddon { Kind = "curseforge", Repo = CurseForge.RepoOf(id), CurseId = id };
        }

        // The pack's word on an addon on the list. True when something kept changed.
        public bool Take(KnownRepo k)
        {
            return Take(k, false);
        }

        // Replaced: another addon takes its place, so the pack's "required" does not hold.
        public bool Take(KnownRepo k, bool replaced)
        {
            bool changed = Series != k.Series || ZipPattern != k.Zip || Source != "pack" || (k.Title != null && Title != k.Title)
                || Private != k.Private || GuidesOnly != k.GuidesOnly || !SameList(Replaces, k.Replaces);
            Series = k.Series;
            ZipPattern = k.Zip;
            if (k.Title != null) Title = k.Title;
            Source = "pack";
            Private = k.Private;
            GuidesOnly = k.GuidesOnly;
            Replaces = k.Replaces;
            Required = k.Required && !replaced;
            Recommended = k.Recommended;
            if (Required) Want = true;
            return changed;
        }

        static bool SameList(string[] a, string[] b)
        {
            if (a == null || b == null) return a == b;
            return a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
        }

        // The folder a copy put in AddOns by hand would have: the repository's
        // name; a CurseForge project's title, run together ("QuestCompletist").
        public string RepoName
        {
            get
            {
                if (IsCurse) return !string.IsNullOrEmpty(Title) ? Regex.Replace(Title, "[^A-Za-z0-9_]", "") : "curseforge-" + CurseId;
                int slash = Repo.IndexOf('/');
                return slash >= 0 ? Repo.Substring(slash + 1) : Repo;
            }
        }

        public string Name
        {
            get { return !string.IsNullOrEmpty(Title) ? Title : IsCurse ? "CurseForge " + CurseId : RepoName; }
        }

        public string By
        {
            get
            {
                if (IsCurse) return Author ?? "";
                int slash = Repo.IndexOf('/');
                return slash > 0 ? Repo.Substring(0, slash) : "";
            }
        }

        // Where its page is.
        public string PageUrl
        {
            get
            {
                if (IsCurse) return Latest != null && Latest.PageUrl != null ? Latest.PageUrl : Slug != null ? CurseForge.Site + Slug : CurseForge.Site;
                return "https://github.com/" + Repo + "/releases";
            }
        }

        public bool UpdateWaits
        {
            get { return Tag != null && Known != null && Known != Tag; }
        }

        public static TrackedAddon ForRepo(string repo)
        {
            TrackedAddon a = new TrackedAddon { Repo = repo };
            KnownRepo known = KnownRepo.Of(repo);
            if (known != null && !known.IsCurse)
            {
                a.Repo = known.Repo;
                a.Title = known.Title;
                a.Series = known.Series;
                a.ZipPattern = known.Zip;
            }
            return a;
        }
    }

    static class Store
    {
        static string FilePath() { return Path.Combine(Core.Home(), "addons.json"); }

        public static List<TrackedAddon> Load()
        {
            List<TrackedAddon> list = new List<TrackedAddon>();
            string path = FilePath();
            if (File.Exists(path))
            {
                try
                {
                    Dictionary<string, object> top = Json.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
                    foreach (object each in Json.Arr(top, "addons"))
                    {
                        TrackedAddon a = Row(each as Dictionary<string, object>);
                        if (a != null) list.Add(a);
                    }
                }
                catch (Exception why)
                {
                    // A list that cannot be read is kept as it is, never written over.
                    Core.Log("addons.json unreadable: " + why.Message);
                    try { File.Copy(path, path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch { }
                    return list;
                }
            }
            bool fresh = !File.Exists(path);
            if (Seed(list).Count == 0 && fresh) Save(list);
            return list;
        }

        // The pack's addons (none before it has been read) on the list, each
        // offered once: one taken off stays off; a
        // required one always comes back. The ones the list has take the
        // pack's word (series, zip, title, required). Returns the ones added.
        public static List<TrackedAddon> Seed(List<TrackedAddon> list)
        {
            List<TrackedAddon> added = new List<TrackedAddon>();
            if (Core.Env("FUM_NO_SEED") == "1") return added;
            List<string> offered = OfferedKeys();
            bool changed = false, offeredMore = false;
            KnownRepo[] items = KnownRepo.Addons();
            // Required and Recommended are the pack's word, read again each time:
            // one the pack no longer requires (EllesmereUI in 1.0.0) is optional.
            foreach (TrackedAddon a in list) { a.Required = false; a.Recommended = false; }
            Dictionary<string, TrackedAddon> replaced = Replaced(list);
            // The pack's "ticked": false, taken once per item by a row not
            // installed (one ticked by an older pack's word, or by being
            // required): Install selected leaves it out. A tick given by hand after
            // that is kept; an installed one stays installed, never taken out.
            List<string> unticked = UntickedKeys();
            bool untickedMore = false;
            foreach (KnownRepo k in items)
            {
                TrackedAddon have = Match(list, k);
                bool off = !k.Private && replaced.ContainsKey(k.Repo);
                if (have != null)
                {
                    changed |= have.Take(k, off);
                    if (!k.Ticked && !unticked.Exists(o => TrackedAddon.SameKey(o, k.Key)) && have.Tag == null && !have.Required && have.Want)
                    {
                        have.Want = false;
                        have.Auto = false;
                        changed = true;
                    }
                }
                else if ((k.Required && !off) || !Offered(offered, k))
                {
                    TrackedAddon a = TrackedAddon.ForItem(k);
                    list.Add(a);
                    added.Add(a);
                }
                if (!Offered(offered, k)) { offered.Add(k.Key); offeredMore = true; }
                bool seen = unticked.Exists(o => TrackedAddon.SameKey(o, k.Key));
                if (!k.Ticked && !seen) { unticked.Add(k.Key); untickedMore = true; }
                else if (k.Ticked && seen) { unticked.RemoveAll(o => TrackedAddon.SameKey(o, k.Key)); untickedMore = true; }
            }
            // In the pack's order (ForeverUI first), the others after, as they were.
            List<TrackedAddon> before = new List<TrackedAddon>(list);
            Func<TrackedAddon, int> place = a => { int i = Array.FindIndex(items, k => a.Is(k)); return i >= 0 ? i : items.Length + before.IndexOf(a); };
            List<TrackedAddon> ordered = list.OrderBy(place).ToList();
            if (!ordered.SequenceEqual(before)) { list.Clear(); list.AddRange(ordered); changed = true; }
            changed |= Replace(list);
            if (changed || added.Count > 0) Save(list);
            if (offeredMore) Core.SetSetting("starters", string.Join(",", offered.ToArray()));
            if (untickedMore) Core.SetSetting("unticked", string.Join(",", unticked.ToArray()));
            return added;
        }

        // The pack items whose "ticked": false the list has taken already.
        public static List<string> UntickedKeys()
        {
            return Core.Setting("unticked", "").Split(',').Where(s => s != "").ToList();
        }

        // The repositories an addon on the list takes the place of (ForeverGuides
        // of Polestar's, ForeverMap of OpenMap's), while it is ticked or installed:
        // each with the addon taking its place.
        public static Dictionary<string, TrackedAddon> Replaced(List<TrackedAddon> list)
        {
            Dictionary<string, TrackedAddon> replaced = new Dictionary<string, TrackedAddon>(StringComparer.OrdinalIgnoreCase);
            foreach (TrackedAddon r in list)
                if (r.Replaces != null && (r.Want || r.Tag != null))
                    foreach (string repo in r.Replaces)
                        if (!replaced.ContainsKey(repo)) replaced[repo] = r;
            return replaced;
        }

        // An addon another takes the place of is no longer required. The first
        // time, one not installed is unticked, so Install selected leaves it out (a
        // tick given again by hand is kept); one installed stays installed and
        // updated, never put in again once uninstalled. When nothing takes its
        // place any more, the pack's word holds again. True when something changed.
        public static bool Replace(List<TrackedAddon> list)
        {
            Dictionary<string, TrackedAddon> replaced = Replaced(list);
            bool changed = false;
            foreach (TrackedAddon a in list)
            {
                TrackedAddon by;
                if (!a.Private && replaced.TryGetValue(a.Repo, out by) && by != a)
                {
                    a.Required = false;
                    if (a.ReplacedBy == null)
                    {
                        a.ReplacedBy = by.Name;
                        if (a.Tag == null) a.Want = false;
                        changed = true;
                    }
                    else if (a.ReplacedBy != by.Name) { a.ReplacedBy = by.Name; changed = true; }
                }
                else if (a.ReplacedBy != null)
                {
                    a.ReplacedBy = null;
                    KnownRepo k = KnownRepo.Addons().FirstOrDefault(o => a.Is(o));
                    if (k != null && k.Required) { a.Required = true; a.Want = true; }
                    changed = true;
                }
            }
            return changed;
        }

        // The entry on the list for a pack item: the same repository and
        // series; else one of the same repository with no series, which
        // installed a zip of the item's name (Spoken, added by hand before
        // the pack named its series).
        static TrackedAddon Match(List<TrackedAddon> list, KnownRepo k)
        {
            TrackedAddon same = list.Find(a => a.Is(k));
            if (same != null || k.Series == null) return same;
            return list.Find(a => !a.IsCurse && a.Series == null && string.Equals(a.Repo, k.Repo, StringComparison.OrdinalIgnoreCase)
                && (k.Zip == null || a.Asset == null || GitHub.Like(a.Asset, k.Zip))
                && !KnownRepo.Addons().Any(o => o.Series == null && a.Is(o)));
        }

        public static List<string> OfferedKeys()
        {
            return Core.Setting("starters", "").Split(',').Where(s => s != "").ToList();
        }

        public static bool Offered(List<string> offered, KnownRepo k)
        {
            return offered.Exists(o => TrackedAddon.SameKey(o, k.Key));
        }

        static TrackedAddon Row(Dictionary<string, object> d)
        {
            TrackedAddon a;
            if (Json.Str(d, "kind") == "curseforge")
            {
                long id = Json.Long(d, "curseforge");
                if (id <= 0 || id > int.MaxValue) return null;
                a = TrackedAddon.ForCurse(id);
                a.Slug = CurseForge.ValidSlug(Json.Str(d, "slug")) ? Json.Str(d, "slug") : null;
                a.Author = Json.Str(d, "author");
            }
            else if (Json.Str(d, "kind") != null && Json.Str(d, "kind") != "github") return null;   // a polestar.guide guide of ForeverUI Manager's: not here
            else
            {
                string repo = GitHub.ParseRepo(Json.Str(d, "repo"));
                if (repo == null) return null;
                a = TrackedAddon.ForRepo(repo);
                if (Json.Str(d, "series") != null) a.Series = Json.Str(d, "series");
                if (Json.Str(d, "zip") != null) a.ZipPattern = Json.Str(d, "zip");
                if (d.ContainsKey("betas")) a.Betas = Json.Bool(d, "betas");
                a.Private = Json.Bool(d, "private");
                List<string> replaces = new List<string>();
                foreach (object r in Json.Arr(d, "replaces"))
                    if (GitHub.ParseRepo(r as string) != null) replaces.Add(GitHub.ParseRepo((string)r));
                a.Replaces = replaces.Count > 0 ? replaces.ToArray() : null;
                a.ReplacedBy = Json.Str(d, "replacedBy");
            }
            if (Json.Str(d, "source") == "pack") a.Source = "pack";
            a.GuidesOnly = Json.Bool(d, "guidesOnly");
            a.Tag = Json.Str(d, "tag");
            a.Asset = Json.Str(d, "asset");
            a.InstalledAt = Json.Str(d, "installedAt");
            a.Known = Json.Str(d, "known");
            if (Json.Str(d, "title") != null) a.Title = Json.Str(d, "title");
            a.Auto = !d.ContainsKey("auto") || Json.Bool(d, "auto");
            a.Want = !d.ContainsKey("want") || Json.Bool(d, "want");
            a.Trusted = Json.Bool(d, "trusted");
            List<string> folders = new List<string>();
            foreach (object f in Json.Arr(d, "folders"))
                if (f is string && Installer.SafeFolder((string)f)) folders.Add((string)f);
            a.Folders = folders.Count > 0 ? folders.ToArray() : null;
            if (a.Folders == null) a.Tag = null;
            return a;
        }

        public static void Save(List<TrackedAddon> list)
        {
            List<object> rows = new List<object>();
            foreach (TrackedAddon a in list)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                if (a.IsCurse)
                {
                    d["kind"] = "curseforge";
                    d["curseforge"] = a.CurseId;
                    if (a.Slug != null) d["slug"] = a.Slug;
                    if (a.Author != null) d["author"] = a.Author;
                }
                else
                {
                    d["repo"] = a.Repo;
                    if (a.Series != null) d["series"] = a.Series;
                    if (a.ZipPattern != null) d["zip"] = a.ZipPattern;
                    if (a.Betas) d["betas"] = true;
                    if (a.Private) d["private"] = true;
                    if (a.Replaces != null) d["replaces"] = a.Replaces;
                    if (a.ReplacedBy != null) d["replacedBy"] = a.ReplacedBy;
                }
                if (a.Source != null) d["source"] = a.Source;
                if (a.GuidesOnly) d["guidesOnly"] = true;
                if (a.Tag != null) d["tag"] = a.Tag;
                if (a.Asset != null) d["asset"] = a.Asset;
                if (a.Folders != null) d["folders"] = a.Folders;
                if (a.InstalledAt != null) d["installedAt"] = a.InstalledAt;
                if (a.Known != null) d["known"] = a.Known;
                if (!string.IsNullOrEmpty(a.Title)) d["title"] = a.Title;
                d["auto"] = a.Auto;
                if (!a.Want) d["want"] = false;
                d["trusted"] = a.Trusted;
                rows.Add(d);
            }
            Dictionary<string, object> top = new Dictionary<string, object>();
            top["format"] = 1;
            top["addons"] = rows;
            try { Core.WriteAll(FilePath(), Json.Write(top)); }
            catch (Exception why) { Core.Log("addons.json not saved: " + why.Message); }
        }
    }

    // WoW Forever on this computer: Battle.net puts it beside retail, in
    // World of Warcraft\_forever_, marked "wowf" in its .flavor.info.
    static class Game
    {
        public static string Folder()
        {
            string given = Core.Env("FUM_GAME");
            if (given != "") return given;
            string picked = Core.Setting("game", "");
            if (IsForever(picked)) return picked;
            foreach (string wow in WowFolders())
            {
                string forever = Path.Combine(wow, "_forever_");
                if (IsForever(forever)) return forever;
            }
            return null;
        }

        public static bool IsForever(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || !File.Exists(Path.Combine(folder, "Wow.exe"))) return false;
            string flavor = Path.Combine(folder, ".flavor.info");
            if (File.Exists(flavor))
            {
                try { return Regex.IsMatch(File.ReadAllText(flavor), @"(^|\s)wowf(\s|$)"); } catch { }
            }
            return string.Equals(Path.GetFileName(folder.TrimEnd('\\')), "_forever_", StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<string> WowFolders()
        {
            List<string> found = new List<string>();
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\World of Warcraft"))
                    if (key != null && key.GetValue("InstallLocation") is string) found.Add((string)key.GetValue("InstallLocation"));
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft"))
                    if (key != null && key.GetValue("InstallPath") is string)
                    {
                        string path = ((string)key.GetValue("InstallPath")).TrimEnd('\\');
                        found.Add(Path.GetFileName(path).StartsWith("_") ? Path.GetDirectoryName(path) : path);
                    }
            }
            catch { }
            found.Add(@"C:\Program Files (x86)\World of Warcraft");
            found.Add(@"C:\Program Files\World of Warcraft");
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                found.Add(Path.Combine(drive.Name, "World of Warcraft"));
                found.Add(Path.Combine(drive.Name, @"Games\World of Warcraft"));
                found.Add(Path.Combine(drive.Name, @"Program Files (x86)\World of Warcraft"));
            }
            return found.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public static string AddOns(string game)
        {
            return game == null ? null : Path.Combine(game, @"Interface\AddOns");
        }

        // Its version, from Battle.net's .build.info one folder up.
        public static string Version(string game)
        {
            string given = Core.Env("FUM_GAME_VERSION");
            if (given != "") return given;
            try
            {
                string info = Path.Combine(Path.GetDirectoryName(game.TrimEnd('\\')), ".build.info");
                if (!File.Exists(info)) return "";
                string[] lines = File.ReadAllLines(info);
                if (lines.Length < 2) return "";
                string[] heads = lines[0].Split('|');
                int product = -1, version = -1;
                for (int i = 0; i < heads.Length; i++)
                {
                    if (heads[i].StartsWith("Product!")) product = i;
                    if (heads[i].StartsWith("Version!")) version = i;
                }
                if (product < 0 || version < 0) return "";
                for (int n = 1; n < lines.Length; n++)
                {
                    string[] cells = lines[n].Split('|');
                    if (cells.Length > Math.Max(product, version) && cells[product] == "wowf") return cells[version];
                }
            }
            catch { }
            return "";
        }

        // Its version as CurseForge names game versions: "1.60.1" (of
        // "1.60.1.65432"); "" when unknown.
        public static string CurseVersion(string game)
        {
            Match v = Regex.Match(game == null ? Core.Env("FUM_GAME_VERSION") : Version(game), @"^\d+\.\d+\.\d+");
            return v.Success ? v.Value : "";
        }

        // Its Interface number, as a .toc says it: 1.60.1 is 16001. 0 when unknown.
        public static int Interface(string game)
        {
            Match v = Regex.Match(Version(game), @"^(\d+)\.(\d+)\.(\d+)");
            return v.Success ? int.Parse(v.Groups[1].Value) * 10000 + int.Parse(v.Groups[2].Value) * 100 + int.Parse(v.Groups[3].Value) : 0;
        }

        // Is WoW Forever open? A Wow.exe started from its folder. One whose
        // path cannot be read counts as open, to be safe.
        public static bool Running(string game)
        {
            if (Core.Env("FUM_TEST_RUNNING") == "1") return true;
            if (game == null) return false;
            string inside = Path.GetFullPath(game).TrimEnd('\\') + "\\";
            foreach (Process process in Process.GetProcessesByName("Wow"))
            {
                using (process)
                {
                    string path = ImagePath(process.Id);
                    if (path == null || path.StartsWith(inside, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        static string ImagePath(int pid)
        {
            IntPtr handle = Native.OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle == IntPtr.Zero) return null;
            try
            {
                StringBuilder name = new StringBuilder(1024);
                int size = name.Capacity;
                return Native.QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString(0, size) : null;
            }
            finally { Native.CloseHandle(handle); }
        }
    }

    class Release
    {
        public string Repo, Tag, Published, Notes, PageUrl;
        public string ZipName, ZipUrl, Sha256, PickedBy;
        public string Sha1;             // CurseForge's: a sha1 instead of a sha256
        public long Size = -1;
        public bool Test;              // a pre-release (on CurseForge: a beta or alpha file)
        public bool Private;            // from the pack's own repository: its zip comes through the API, with the token
        public long FileId = -1;        // CurseForge's file id
        // A newer release left out because it cannot be installed (no zip,
        // most often its author's slip): its tag, why ("its release v0.7.3
        // has no zip") and that in a few words for the row ("has no zip").
        public string Skipped, SkippedWhy, SkippedShort;

        public void Skip(Unusable why)
        {
            Skipped = why.Tag;
            SkippedWhy = why.Message;
            SkippedShort = why.Short;
        }
    }

    class Asset
    {
        public string Name, Url, Sha256;
        public long Size = -1;
    }

    // GitHub's API is out for now (its limit of 60 an hour, or no answer):
    // its web pages are asked instead.
    class ApiOut : Exception { public ApiOut(string message) : base(message) { } }

    // An answer for good: no repository, no release, no build for Forever.
    class Final : Exception { public Final(string message) : base(message) { } }

    // A release that cannot be installed as it is: no zip, or none that can
    // be told for WoW Forever. The check takes the newest one before it that
    // can, and says so on the row (in gold, not as a failed check).
    class Unusable : Final
    {
        public readonly string Short;   // for the row: "has no zip"
        public readonly string Tag;     // the release's
        public Unusable(string tag, string message, string brief) : base(message) { Tag = tag; Short = brief; }
    }

    // The pack's repository (or file) is not there for this token.
    class PrivateMissing : Exception { public PrivateMissing(string message) : base(message) { } }

    // Folders a zip would put in AddOns where folders not put there by this
    // app already are: never moved without a yes.
    class InTheWay : Exception
    {
        public readonly string[] Folders;
        public InTheWay(string[] folders)
            : base("AddOns already has " + string.Join(", ", folders) + ", not put there by " + Core.Name + "; it is left as it is")
        {
            Folders = folders;
        }
    }

    static class Net
    {
        public static string Agent { get { return "Forever-Manager/" + Core.Version; } }

        public static HttpWebRequest Make(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = Agent;
            request.Timeout = 20000;
            request.ReadWriteTimeout = 60000;
            request.AllowAutoRedirect = true;
            return request;
        }

        public static byte[] ReadCapped(Stream input, long cap)
        {
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[16384];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    if (output.Length > cap) throw new Exception("the answer is bigger than " + (cap / 1024) + " KB");
                }
                return output.ToArray();
            }
        }

        // A small file (release.json, SHA256SUMS) as text. 404: Final.
        public static string Text(string url)
        {
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)Make(url).GetResponse())
                using (Stream input = response.GetResponseStream())
                    return Encoding.UTF8.GetString(ReadCapped(input, 1024 * 1024));
            }
            catch (WebException error)
            {
                if (IsMissing(error, url)) throw new Final("not found: " + url);
                throw;
            }
        }

        // Where an address ends after its redirects.
        public static string Landing(string url)
        {
            HttpWebRequest request = Make(url);
            request.Method = "HEAD";
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    return response.ResponseUri.AbsoluteUri;
            }
            catch (WebException error)
            {
                if (IsMissing(error, null)) throw new Final("GitHub has no repository by that name");
                throw;
            }
        }

        // GitHub sometimes closes the connection in a missing page's body, so
        // .NET sees no response: its head alone tells the 404.
        public static bool IsMissing(WebException error, string url)
        {
            HttpWebResponse response = error.Response as HttpWebResponse;
            if (response != null)
                using (response)
                    return response.StatusCode == HttpStatusCode.NotFound;
            if (url == null) return false;
            HttpWebRequest head = Make(url);
            head.Method = "HEAD";
            try { head.GetResponse().Close(); return false; }
            catch (WebException again)
            {
                HttpWebResponse second = again.Response as HttpWebResponse;
                if (second == null) return false;
                using (second) return second.StatusCode == HttpStatusCode.NotFound;
            }
        }

        public static bool SameOrigin(string url, string origin)
        {
            Uri a, b;
            return Uri.TryCreate(url, UriKind.Absolute, out a) && Uri.TryCreate(origin, UriKind.Absolute, out b)
                && a.Scheme == b.Scheme && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
        }

        // A download to a file. With an origin, every redirect must stay on
        // it; with
        // a cap, nothing bigger is taken.
        public static void Download(string url, string path, Action<long, long> progress, string origin, long cap)
        {
            Download(url, path, progress, origin, cap, false);
        }

        // Resume: what path holds already is kept and only the rest asked
        // for (a Range); a download that stops is taken up again where it
        // stopped, three times, a few seconds apart.
        public static void Download(string url, string path, Action<long, long> progress, string origin, long cap, bool resume)
        {
            Download(url, path, progress, origin, cap, resume, null);
        }

        // Token: a private repository's asset, asked of GitHub's API with the
        // token; the API sends it on to another host, which gets no token.
        public static void Download(string url, string path, Action<long, long> progress, string origin, long cap, bool resume, string token)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { Once(url, path, progress, origin, cap, resume, token); return; }
                catch (Exception why)
                {
                    if (why is Final || !resume || attempt >= 3 || why.Message.Contains("bigger than")) throw;
                    Core.Log("download stopped (" + why.Message + "), taken up again at " + (File.Exists(path) ? new FileInfo(path).Length : 0) + " bytes");
                    Thread.Sleep(Core.TestMode ? 200 : 3000 * (attempt + 1));
                }
            }
        }

        // An address without its query (a signed one says nothing in the log).
        public static string Bare(string url)
        {
            int q = (url ?? "").IndexOf('?');
            return q < 0 ? url : url.Substring(0, q);
        }

        public static bool IsApi(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) && u.Scheme == "https" && u.IsDefaultPort && string.Equals(u.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);
        }

        // A piece of a file: bytes from..to, or (from < 0) its last -from
        // bytes. Start: where the piece begins in the file; total: the file's
        // length (-1 unknown). The token as in Download: the API's first hop
        // only. A host that sends the whole file is read up to 8 MB.
        public static byte[] Range(string url, string token, long from, long to, out long start, out long total)
        {
            if (token != null && !Friends.Owns(url) && !url.StartsWith("https://api.github.com/repos/" + Pack.Repo + "/releases/assets/", StringComparison.OrdinalIgnoreCase))
                throw new Final("the token goes only to " + Pack.Repo + "'s own downloads");
            // Where it lands, asked without a Range (CurseForge's edge host
            // answers a Range with a 404 before its redirect); then the piece there.
            string at = url;
            for (int hop = 0; ; hop++)
            {
                if (hop > 5) throw new Exception("its download goes round in redirects");
                if (hop > 0 && !at.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new Final("its download moved off https");
                HttpWebRequest head = Make(at);
                head.Method = "HEAD";
                head.AllowAutoRedirect = false;
                Bearer(head, token, hop == 0);
                HttpWebResponse answered;
                // A host that takes no HEAD (GitHub's file host says 501): the piece is asked of it as it is.
                try { answered = (HttpWebResponse)head.GetResponse(); }
                catch (WebException) { break; }
                using (HttpWebResponse response = answered)
                {
                    int code = (int)response.StatusCode;
                    if (code < 300 || code >= 400) break;
                    string location = response.Headers["Location"];
                    if (string.IsNullOrEmpty(location)) throw new Exception("its download moved, to no address");
                    at = new Uri(new Uri(at), location).AbsoluteUri;
                }
            }
            {
                HttpWebRequest request = Make(at);
                request.AllowAutoRedirect = false;
                Bearer(request, token, at == url);
                if (from < 0) request.AddRange(from);
                else request.AddRange(from, to);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    if (code >= 300) throw new Exception("its host answered " + code + " for a piece of it");
                    using (Stream input = response.GetResponseStream())
                    {
                        if (code == 206)
                        {
                            Match range = Regex.Match(response.Headers["Content-Range"] ?? "", @"bytes (\d+)-(\d+)/(\d+|\*)");
                            if (!range.Success) throw new Exception("its host gave a piece without saying which");
                            start = long.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
                            total = range.Groups[3].Value == "*" ? -1 : long.Parse(range.Groups[3].Value, CultureInfo.InvariantCulture);
                            return ReadCapped(input, 33L * 1048576);
                        }
                        byte[] whole = ReadCapped(input, 8L * 1048576);
                        start = 0;
                        total = whole.Length;
                        if (from < 0) return whole;
                        if (from >= whole.Length) return new byte[0];
                        long end = Math.Min(to, whole.Length - 1);
                        byte[] piece = new byte[end - from + 1];
                        Array.Copy(whole, from, piece, 0, piece.Length);
                        start = from;
                        return piece;
                    }
                }
            }
        }

        // The token, to GitHub's API only (the first hop); to the friends'
        // server, its own key instead (the token never goes there).
        static void Bearer(HttpWebRequest request, string token, bool api)
        {
            if (token == null || !api) return;
            if (Friends.Owns(request.RequestUri.AbsoluteUri)) { Friends.Sign(request); return; }
            request.Headers["Authorization"] = "Bearer " + token;
            request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            request.Accept = "application/octet-stream";
        }

        static void Once(string url, string path, Action<long, long> progress, string origin, long cap, bool resume, string token)
        {
            long have = resume && File.Exists(path) ? new FileInfo(path).Length : 0;
            string at = url;
            bool friends = token != null && Friends.Owns(url);
            if (token != null && !friends && !IsApi(url)) throw new Final("a private download goes only through GitHub's API or the friends' server");
            if (token != null && !friends && !url.StartsWith("https://api.github.com/repos/" + Pack.Repo + "/releases/assets/", StringComparison.OrdinalIgnoreCase))
                throw new Final("the token goes only to " + Pack.Repo + "'s own downloads");
            for (int hop = 0; ; hop++)
            {
                if (origin != null && !SameOrigin(at, origin)) throw new Final("its download is not on " + origin);
                if (hop > 5) throw new Exception("its download goes round in redirects");
                if (token != null && hop > 0 && !at.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new Final("its download moved off https");
                if (friends && hop > 0) throw new Final("the friends' server sent its download elsewhere");
                HttpWebRequest request = Make(at);
                request.AllowAutoRedirect = origin == null && token == null;
                // The token for the API alone, on the first hop; the host it sends us on to gets none.
                // The friends' server gets its own key, never the token.
                if (friends) Friends.Sign(request);
                else if (token != null && hop == 0)
                {
                    request.Headers["Authorization"] = "Bearer " + token;
                    request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
                    request.Accept = "application/octet-stream";
                }
                if (have > 0) request.AddRange(have);
                try
                {
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        int code = (int)response.StatusCode;
                        if (code >= 300 && code < 400)
                        {
                            string location = response.Headers["Location"];
                            if (string.IsNullOrEmpty(location)) throw new Exception("its download moved without saying where");
                            at = new Uri(new Uri(at), location).AbsoluteUri;
                            continue;
                        }
                        // 206: the rest, after what is held; 200: all of it again.
                        bool rest = have > 0 && code == 206;
                        if (rest)
                        {
                            Match from = Regex.Match(response.Headers["Content-Range"] ?? "", @"^bytes (\d+)-");
                            if (!from.Success || long.Parse(from.Groups[1].Value) != have) throw new Exception("its download came back from the wrong place");
                        }
                        long start = rest ? have : 0;
                        long total = response.ContentLength >= 0 ? start + response.ContentLength : -1, done = start;
                        if (cap > 0 && total > cap) throw new Exception("its download is bigger than " + (cap / 1048576) + " MB");
                        using (Stream input = response.GetResponseStream())
                        using (FileStream output = rest ? new FileStream(path, FileMode.Append, FileAccess.Write) : File.Create(path))
                        {
                            byte[] buffer = new byte[81920];
                            int read;
                            DateTime shown = DateTime.MinValue;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                output.Write(buffer, 0, read);
                                done += read;
                                if (cap > 0 && done > cap) throw new Exception("its download is bigger than " + (cap / 1048576) + " MB");
                                if (progress != null && (DateTime.Now - shown).TotalMilliseconds > 150)
                                {
                                    shown = DateTime.Now;
                                    progress(done, total);
                                }
                            }
                        }
                        if (progress != null) progress(done, total);
                        if (total > 0 && done != total) throw new Exception("the download stopped early");
                        return;
                    }
                }
                catch (WebException error)
                {
                    // 416: nothing after what is held. Whole already when the
                    // server says it is that big; else started again.
                    HttpWebResponse said = error.Response as HttpWebResponse;
                    if (have > 0 && said != null && (int)said.StatusCode == 416)
                    {
                        Match whole = Regex.Match(said.Headers["Content-Range"] ?? "", @"/(\d+)$");
                        said.Close();
                        if (whole.Success && long.Parse(whole.Groups[1].Value) == have) return;
                        File.Delete(path);
                        throw new Exception("its download did not fit what was held, started again");
                    }
                    if (token != null)
                    {
                        HttpWebResponse r = error.Response as HttpWebResponse;
                        if (r == null) throw new Exception("its download did not answer (" + error.Message + ")");
                        int status = (int)r.StatusCode;
                        r.Close();
                        if (friends && status == 404) throw new Final("the friends' server does not give its download to this build (404: gone, or the key is not the server's)");
                        if (friends) throw new Exception("the friends' server answered " + status);
                        if (status == 401) throw new Final("GitHub refused this build's token (401): it may have expired; a new Forever Manager is needed");
                        if (status == 403 || status == 404) throw new Final("GitHub would not give its download to this token (" + status + ")");
                        throw new Exception("its download answered " + status);
                    }
                    if (IsMissing(error, at)) throw new Final("its download is gone: " + Bare(at));
                    throw;
                }
            }
        }
    }

    static class GitHub
    {
        static DateTime wait = DateTime.MinValue;

        // "https://github.com/owner/name/...", "github.com/owner/name.git",
        // "git@github.com:owner/name" or "owner/name": "owner/name"; else null.
        public static string ParseRepo(string given)
        {
            string text = (given ?? "").Trim().Trim('"', '\'', '<', '>');
            Match link = Regex.Match(text, @"^(?:git@github\.com:|(?:https?://)?(?:www\.)?github\.com/)([^/?#\s]+)/([^/?#\s]+)", RegexOptions.IgnoreCase);
            string owner, name;
            if (link.Success)
            {
                owner = link.Groups[1].Value;
                name = link.Groups[2].Value;
            }
            else
            {
                Match plain = Regex.Match(text, @"^([^/\s]+)/([^/\s]+?)/?$");
                if (!plain.Success || plain.Groups[1].Value.Contains(".")) return null;
                owner = plain.Groups[1].Value;
                name = plain.Groups[2].Value;
            }
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            if (!Regex.IsMatch(owner, @"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")) return null;
            if (!Regex.IsMatch(name, @"^[A-Za-z0-9._-]{1,100}$") || name == "." || name == "..") return null;
            return owner + "/" + name;
        }

        static string Limit()
        {
            return "GitHub's limit of 60 checks an hour is used up until " + wait.ToString("HH:mm");
        }

        // Tests: the API's answers made up, and the calls counted.
        public static Func<string, string> FakeApi = null;
        public static int Calls;

        public static string Api(string path)
        {
            if (FakeApi != null) { Calls++; return FakeApi(path); }
            // A public repository's releases: the friends' server's copy (kept
            // there 10 minutes, counted in no limit of this computer) first.
            string served = Friends.Proxy(path);
            if (served != null) return served;
            if (Core.Env("FUM_NO_API") == "1") throw new ApiOut("the API is off for this test");
            if (DateTime.Now < wait) throw new ApiOut(Limit());
            Calls++;
            HttpWebRequest request = Net.Make("https://api.github.com/" + path);
            request.Accept = "application/vnd.github+json";
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                if (response == null) throw new ApiOut("GitHub's API did not answer (" + error.Message + ")");
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound) throw new Final("GitHub has no repository by that name");
                    long reset;
                    if (response.Headers["X-RateLimit-Remaining"] == "0" && long.TryParse(response.Headers["X-RateLimit-Reset"], out reset))
                    {
                        wait = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(reset).ToLocalTime();
                        throw new ApiOut(Limit());
                    }
                    throw new Exception("GitHub answered " + (int)response.StatusCode + " " + response.StatusDescription);
                }
            }
        }

        // The newest stable release of the addon and its zip for WoW
        // Forever: a CurseForge project's from CurseForge; the pack's own
        // repository's through the API with the token; a public one's by
        // the API first, and with the API out, GitHub's pages (counted in no limit).
        public static Release Latest(TrackedAddon a)
        {
            if (a.IsCurse) return CurseForge.Latest(a.CurseId);
            if (a.Private || Pack.Owns(a.Repo)) return PrivateLatest(a.Repo, a.Series, a.ZipPattern, a.Betas);
            return Latest(a.Repo, a.Series, a.ZipPattern, a.Betas);
        }

        public static Release Latest(KnownRepo k)
        {
            if (k.IsCurse) return CurseForge.Latest(k.CurseId);
            if (k.Private) return PrivateLatest(k.Repo, k.Series, k.Zip, false);
            return Latest(k.Repo, k.Series, k.Zip, false);
        }

        // Tests: the private API's answers made up (path, token, accept).
        public static Func<string, string, string, string> FakePrivate = null;

        // GitHub's API with the token the build put in: only for the pack's
        // own repository, never in the log, never after a redirect. Its
        // limit is the token's own (5000 an hour), not the 60 of this
        // computer. A 404: PrivateMissing. FUM_PRIVATE_RELEASES (tests): a
        // file answering for the repository's list of releases.
        public static string PrivateApi(string path, string accept)
        {
            if (path != "repos/" + Pack.Repo && !path.StartsWith("repos/" + Pack.Repo + "/", StringComparison.OrdinalIgnoreCase))
                throw new Final("the token goes only to " + Pack.Repo);
            string token = Secrets.GitHubToken();
            if (FakePrivate != null) { Calls++; return FakePrivate(path, token, accept); }
            string given = Core.Env("FUM_PRIVATE_RELEASES");
            if (given != "" && path.StartsWith("repos/" + Pack.Repo + "/releases?", StringComparison.OrdinalIgnoreCase)) return File.ReadAllText(given, Encoding.UTF8);
            if (Core.Env("FUM_NO_API") == "1") throw new ApiOut("the API is off for this test");
            // Since 1.0.8 no build has the token: the friends' server is the one way in.
            if (!Secrets.HasToken) throw new Exception("the friends' server did not answer" + (Friends.LastWhy != null ? " (" + Friends.LastWhy + ")" : "") + "; checked again in two minutes");
            Calls++;
            HttpWebRequest request = Net.Make("https://api.github.com/" + path);
            request.Accept = accept ?? "application/vnd.github+json";
            request.Headers["Authorization"] = "Bearer " + token;
            request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            request.AllowAutoRedirect = false;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    if (code >= 300) throw new Exception("GitHub answered " + code + " (the repository may have moved)");
                    using (Stream input = response.GetResponseStream())
                        return Encoding.UTF8.GetString(Net.ReadCapped(input, 16 * 1024 * 1024));
                }
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                if (response == null) throw new ApiOut("GitHub's API did not answer (" + error.Message + ")");
                using (response)
                {
                    int code = (int)response.StatusCode;
                    if (code == 404) throw new PrivateMissing("GitHub shows this build's token no such repository or file (404)");
                    if (code == 401) throw new Exception("GitHub refused this build's token (401): it may have expired; a new Forever Manager is needed");
                    if (code == 403 || code == 429)
                    {
                        if (response.Headers["X-RateLimit-Remaining"] == "0") throw new Exception("this build's token has used up its GitHub limit for this hour");
                        throw new Exception("GitHub refused this build's token (" + code + "): it may not read " + Pack.Repo + "'s Contents");
                    }
                    throw new Exception("GitHub answered " + code + " " + response.StatusDescription);
                }
            }
        }

        // The newest release of a series of the pack's own repository,
        // through the API with the token (there is no other way to it). Its
        // zip by its name, checked against the sha256 GitHub's API gives (one
        // without is not taken), downloaded through the API too.
        public static Release PrivateLatest(string repo, string series, string zipPattern, bool betas)
        {
            if (!Pack.Owns(repo)) throw new Exception("the token is only for " + Pack.Repo);
            try
            {
                return ByApi(repo, series, zipPattern, betas, delegate(string path) { return PrivateApi(path, null); }, true);
            }
            catch (PrivateMissing) { throw new Exception("GitHub shows this build's token no repository " + repo); }
            catch (ApiOut why) { throw new Exception(why.Message + "; a private repository has no other way in"); }
        }

        // The pack's own repository's list of releases, newest first (the
        // same page the addons' checks read, kept two minutes): for the
        // Manager's own new versions.
        public static object[] PrivateReleases()
        {
            return Releases(Pack.Repo, 100, 1, delegate(string path) { return PrivateApi(path, null); }, true);
        }

        public static Release Latest(string repo, string series, string zipPattern)
        {
            return Latest(repo, series, zipPattern, false);
        }

        // Betas: the newest release, test versions (pre-releases) too.
        public static Release Latest(string repo, string series, string zipPattern, bool betas)
        {
            try { return ByApi(repo, series, zipPattern, betas); }
            catch (ApiOut why)
            {
                try { return ByWeb(repo, series, zipPattern, betas); }
                catch (Final) { throw; }
                catch (Exception web) { throw new Exception(why.Message + "; the release page did not answer either (" + web.Message + ")"); }
            }
        }

        // Its version tags: "v0.3.1", "1.2"; not "lib-v1.2" (another thing
        // the same repository ships).
        static bool PlainTag(string tag)
        {
            return Regex.IsMatch(tag ?? "", @"^[vV]?\d");
        }

        public const string NoStable = "it has no stable release of this addon";

        // A page of a repository's releases, newest first. A page read in
        // the last two minutes is not asked again: Spoken and its four voice
        // packs share one repository, and one call.
        static readonly Dictionary<string, KeyValuePair<DateTime, object[]>> pages = new Dictionary<string, KeyValuePair<DateTime, object[]>>(StringComparer.OrdinalIgnoreCase);

        static object[] Releases(string repo, int per, int page, Func<string, string> api, bool priv)
        {
            // The pack's own releases: the friends' server's manifest (all of
            // them on one page) while it answers; GitHub with the token else.
            if (priv && Pack.Owns(repo))
            {
                object[] served = Friends.Releases();
                if (served != null) return page == 1 ? served : new object[0];
            }
            string path = "repos/" + repo + "/releases?per_page=" + per + (page > 1 ? "&page=" + page : "");
            string held_key = (priv ? "private " : "") + path;
            KeyValuePair<DateTime, object[]> held;
            lock (pages)
                if (pages.TryGetValue(held_key, out held) && DateTime.Now - held.Key < TimeSpan.FromMinutes(2)) return held.Value;
            object[] list = Json.Parse(api(path)) as object[];
            if (list == null) throw new Exception("GitHub's answer could not be read");
            lock (pages) pages[held_key] = new KeyValuePair<DateTime, object[]>(DateTime.Now, list);
            return list;
        }

        public static void Forget() { lock (pages) pages.Clear(); Friends.Forget(); }

        // A repository that ships several things may have the newest release
        // of a series far down its list (Spoken's has 140 releases of a dozen
        // things): pages of 100 are read until one of the series turns up,
        // three at most.
        static Release ByApi(string repo, string series, string zipPattern, bool betas)
        {
            return ByApi(repo, series, zipPattern, betas, Api, false);
        }

        // Priv: the pack's own repository's; each asset by its API address, its sha256 GitHub's.
        static Release ByApi(string repo, string series, string zipPattern, bool betas, Func<string, string> api, bool priv)
        {
            int per = series == null ? 30 : 100, most = series == null ? 1 : 3, seen = 0;
            List<Dictionary<string, object>> stable = new List<Dictionary<string, object>>();
            for (int page = 1; page <= most && stable.Count == 0; page++)
            {
                object[] list = Releases(repo, per, page, api, priv);
                seen += list.Length;
                foreach (object each in list)
                {
                    Dictionary<string, object> release = each as Dictionary<string, object>;
                    if (release == null || Json.Bool(release, "draft") || (!betas && Json.Bool(release, "prerelease"))) continue;
                    if (series != null && !Regex.IsMatch(Json.Str(release, "tag_name") ?? "", series)) continue;
                    stable.Add(release);
                }
                if (list.Length < per) break;
            }
            if (stable.Count == 0) throw new Final(seen == 0 ? "it has no release yet" : NoStable);
            Dictionary<string, object> pick = series == null ? (stable.FirstOrDefault(r => PlainTag(Json.Str(r, "tag_name"))) ?? stable[0]) : stable[0];
            try { return FromApi(pick, repo, zipPattern, priv); }
            catch (Unusable newest)
            {
                // Its newest release cannot be installed (its author forgot
                // the zip, most often): the newest before it that can, of
                // the next five, said on the row. None: the newest's own word.
                foreach (Dictionary<string, object> older in stable.Where(r => r != pick && (series != null || PlainTag(Json.Str(r, "tag_name")))).Take(5))
                {
                    Release found;
                    try { found = FromApi(older, repo, zipPattern, priv); }
                    catch (Final why) { Core.Log("older release " + Json.Str(older, "tag_name") + " of " + repo + ": " + why.Message); continue; }
                    found.Skip(newest);
                    Core.Log(repo + ": " + newest.Message + "; " + found.Tag + " offered instead");
                    return found;
                }
                throw;
            }
        }

        // Older stable releases of a series than the newest (skip), newest
        // first, from the page the check just read (no call more): to tell an
        // older version put in AddOns by hand. One that cannot be taken is left out.
        public static IEnumerable<Release> Older(TrackedAddon a, string skip, int most)
        {
            bool priv = a.Private || Pack.Owns(a.Repo);
            Func<string, string> api = priv ? (Func<string, string>)(path => PrivateApi(path, null)) : (Func<string, string>)Api;
            object[] list;
            try { list = Releases(a.Repo, a.Series == null ? 30 : 100, 1, api, priv); }
            catch (Exception why) { Core.Log("older versions of " + a.Repo + ": " + why.Message); yield break; }
            int given = 0;
            foreach (object each in list)
            {
                if (given >= most) yield break;
                Dictionary<string, object> release = each as Dictionary<string, object>;
                if (release == null || Json.Bool(release, "draft") || (!a.Betas && Json.Bool(release, "prerelease"))) continue;
                string tag = Json.Str(release, "tag_name") ?? "";
                if (a.Series != null ? !Regex.IsMatch(tag, a.Series) : !PlainTag(tag)) continue;
                if (tag == skip) continue;
                Release r = null;
                try { r = FromApi(release, a.Repo, a.ZipPattern, priv); }
                catch (Exception why) { Core.Log("older version " + tag + " of " + a.Repo + ": " + why.Message); }
                if (r == null) continue;
                given++;
                yield return r;
            }
        }

        static Release FromApi(Dictionary<string, object> pick, string repo, string zipPattern, bool priv)
        {
            Release found = new Release();
            found.Repo = repo;
            found.Tag = Json.Str(pick, "tag_name");
            found.Test = Json.Bool(pick, "prerelease");
            found.Published = Json.Str(pick, "published_at");
            found.Notes = Json.Str(pick, "body");
            found.PageUrl = Json.Str(pick, "html_url");
            List<Asset> assets = new List<Asset>();
            string own = "https://api.github.com/repos/" + repo + "/releases/assets/";
            foreach (object a in Json.Arr(pick, "assets"))
            {
                Dictionary<string, object> asset = a as Dictionary<string, object>;
                if (asset == null) continue;
                string url = Json.Str(asset, priv ? "url" : "browser_download_url");
                if (priv && !Friends.Owns(url) && (url == null || !url.StartsWith(own, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(url.Substring(own.Length), @"^\d+$"))) url = null;
                assets.Add(new Asset
                {
                    Name = Json.Str(asset, "name") ?? "",
                    Url = url,
                    Size = Json.Long(asset, "size"),
                    Sha256 = Digest(Json.Str(asset, "digest"))
                });
            }
            if (priv)
            {
                // Its zip by its name only: a release.json or SHA256SUMS of a private repository cannot be read without the token.
                if (zipPattern == null) throw new Final("an addon of the pack's own repository must say its zip's name");
                PickZip(found, assets, zipPattern, RepoName(repo));
                if (found.ZipUrl == null) throw new Final("its release " + found.Tag + " gives no API address for " + found.ZipName);
                if (found.Sha256 == null) throw new Final("its release " + found.Tag + " gives no sha256 for " + found.ZipName + " (GitHub gives one for files uploaded since 2025)");
                found.Private = true;
                found.PickedBy += Friends.Owns(found.ZipUrl) ? ", from the friends' server, checked against the pack's sha256" : ", checked against GitHub's sha256";
                return found;
            }
            PickZip(found, assets, zipPattern, RepoName(repo));
            Sums(found, assets);
            return found;
        }

        static string RepoName(string repo)
        {
            return repo.Substring(repo.IndexOf('/') + 1);
        }

        // "sha256:ab12..." as GitHub gives it: "ab12..."; else null.
        public static string Digest(string digest)
        {
            if (digest == null) return null;
            Match sha = Regex.Match(digest, "^sha256:([0-9a-fA-F]{64})$");
            return sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : null;
        }

        public static bool Like(string name, string pattern)
        {
            return Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
        }

        // Which zip is the one for WoW Forever: the one its known name says;
        // else the release.json addon packagers put beside their zips (flavor
        // "forever", or an Interface of 1.6x); else the release's only zip,
        // the one named for Forever, the one named for the repository, or
        // the one not named for another game.
        public static void PickZip(Release release, List<Asset> assets, string zipPattern, string repoName)
        {
            List<Asset> zips = assets.FindAll(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (zipPattern != null)
            {
                List<Asset> named = zips.FindAll(a => Like(a.Name, zipPattern));
                if (named.Count != 1) throw new Unusable(release.Tag, "its release " + release.Tag + " has " + (named.Count == 0 ? "no zip" : "several zips") + " named like " + zipPattern,
                    named.Count == 0 ? "has no zip" : "has several zips");
                Take(release, named[0], "its zip by name");
                return;
            }
            Asset meta = assets.Find(a => string.Equals(a.Name, "release.json", StringComparison.OrdinalIgnoreCase));
            if (meta != null && meta.Url != null)
            {
                string chosen = ForeverIn(Net.Text(meta.Url));
                if (chosen == null) throw new Unusable(release.Tag, "its release " + release.Tag + " has no build for WoW Forever (its release.json lists none)", "not for Forever");
                Asset zip = assets.Find(a => string.Equals(a.Name, chosen, StringComparison.OrdinalIgnoreCase));
                if (zip == null) throw new Unusable(release.Tag, "its release.json names " + chosen + ", which is not in release " + release.Tag, "has no zip");
                Take(release, zip, "the release.json picks it for WoW Forever");
                return;
            }
            string chosenName = ZipByName(zips.ConvertAll(a => a.Name), repoName);
            Asset one = chosenName == null ? null : zips.Find(a => a.Name == chosenName);
            if (one == null)
                throw new Unusable(release.Tag, zips.Count == 0 ? "its release " + release.Tag + " has no zip"
                    : "its release " + release.Tag + " has several zips (" + string.Join(", ", zips.ConvertAll(a => a.Name).ToArray()) + ") and nothing saying which is for WoW Forever",
                    zips.Count == 0 ? "has no zip" : "has several zips");
            Take(release, one, zips.Count == 1 ? "the release's only zip" : "picked by its name");
        }

        // Of a release's zip names, the one for WoW Forever without a
        // release.json; null when that cannot be told.
        public static string ZipByName(List<string> zips, string repoName)
        {
            if (zips.Count == 1) return zips[0];
            string forever = zips.Find(n => Regex.IsMatch(n, "forever|camelot", RegexOptions.IgnoreCase));
            if (forever != null) return forever;
            List<string> own = zips.FindAll(n => Regex.IsMatch(n, "^" + Regex.Escape(repoName) + @"[-_ ]?[vV]?\d", RegexOptions.IgnoreCase));
            if (own.Count == 1) return own[0];
            List<string> plain = zips.FindAll(n => !Regex.IsMatch(n, @"(^|[-_. ])(classic|vanilla|era|sod|bcc|tbc|wrath|wotlk|cata|mists|mop|nolib)([-_. ]|$)", RegexOptions.IgnoreCase));
            return plain.Count == 1 ? plain[0] : null;
        }

        static void Take(Release release, Asset zip, string why)
        {
            release.ZipName = zip.Name;
            release.ZipUrl = zip.Url;
            release.Size = zip.Size;
            release.Sha256 = zip.Sha256;
            release.PickedBy = why;
        }

        static readonly string[] SumsNames = { "SHA256SUMS", "SHA256SUMS.txt", "sha256sums.txt", "checksums.txt", "checksums.sha256" };

        // The author's own list of sha256s beside the zips, when there is
        // one: it must agree with GitHub's.
        static void Sums(Release release, List<Asset> assets)
        {
            Asset sums = assets.Find(a => SumsNames.Any(n => string.Equals(n, a.Name, StringComparison.OrdinalIgnoreCase)));
            if (sums == null || sums.Url == null) return;
            Dictionary<string, string> listed = ParseSums(Net.Text(sums.Url));
            string sha;
            if (!listed.TryGetValue(release.ZipName, out sha)) return;
            if (release.Sha256 != null && release.Sha256 != sha)
                throw new Final("GitHub's sha256 for " + release.ZipName + " and the release's " + sums.Name + " disagree");
            release.Sha256 = sha;
            release.PickedBy += ", checked against its " + sums.Name;
        }

        // "<sha256>  <file>" lines (a "*" before the file for binary mode).
        public static Dictionary<string, string> ParseSums(string text)
        {
            Dictionary<string, string> listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in text.Split('\n'))
            {
                Match m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                if (m.Success) listed[m.Groups[2].Value.Trim()] = m.Groups[1].Value.ToLowerInvariant();
            }
            return listed;
        }

        // The zip release.json lists for WoW Forever, the full one before a
        // "nolib" one; null when it lists none.
        public static string ForeverIn(string releaseJson)
        {
            Dictionary<string, object> top = Json.Parse(releaseJson) as Dictionary<string, object>;
            string best = null;
            foreach (object each in Json.Arr(top, "releases"))
            {
                Dictionary<string, object> entry = each as Dictionary<string, object>;
                string file = Json.Str(entry, "filename");
                if (file == null || file.IndexOfAny(new[] { '/', '\\' }) >= 0) continue;
                bool forever = false;
                foreach (object m in Json.Arr(entry, "metadata"))
                {
                    Dictionary<string, object> meta = m as Dictionary<string, object>;
                    string flavor = (Json.Str(meta, "flavor") ?? "").ToLowerInvariant();
                    long iface = Json.Long(meta, "interface");
                    if (flavor == "forever" || flavor == "camelot" || (iface >= 16000 && iface < 17000)) forever = true;
                }
                if (!forever) continue;
                if (!Json.Bool(entry, "nolib")) return file;
                if (best == null) best = file;
            }
            return best;
        }

        // The web's way: releases/latest lands on the newest stable tag; the
        // release's release.json, or its SHA256SUMS, names the zip. No size
        // this way, and a sha256 only from SHA256SUMS. A newest release that
        // cannot be installed (no zip): the newest before it that can, from
        // git's list of tags, said on the row.
        static Release ByWeb(string repo, string series, string zipPattern, bool betas)
        {
            try { return ByLanding(repo, series, zipPattern, betas); }
            catch (Unusable newest)
            {
                Release found;
                try { found = BySeriesWeb(repo, series ?? @"^[vV]?\d", zipPattern, betas, newest.Tag); }
                catch (Final) { throw newest; }
                found.Skip(newest);
                Core.Log(repo + ": " + newest.Message + "; " + found.Tag + " offered instead (read from GitHub's pages)");
                return found;
            }
        }

        static Release ByLanding(string repo, string series, string zipPattern, bool betas)
        {
            string landed = Net.Landing("https://github.com/" + repo + "/releases/latest");
            Match tag = Regex.Match(landed, @"/releases/tag/([^?#]+)$");
            if (!tag.Success && series == null) throw new Final("it has no stable release");
            string tagName = tag.Success ? Uri.UnescapeDataString(tag.Groups[1].Value) : "";
            // Another of the repository's things is its newest: the series'
            // newest tag from git's own list instead.
            if (series != null && !Regex.IsMatch(tagName, series)) return BySeriesWeb(repo, series, zipPattern, betas);
            if (series == null && !PlainTag(tagName))
                throw new Exception("its newest release on GitHub's page (" + tagName + ") is another of the repository's things; checked again later");
            string downloads = "https://github.com/" + repo + "/releases/download/" + TagPath(tagName) + "/";
            Release release = new Release();
            release.Repo = repo;
            release.Tag = tagName;
            release.PageUrl = landed;
            Dictionary<string, string> sums = null;
            foreach (string name in SumsNames)
            {
                try { sums = ParseSums(Net.Text(downloads + name)); break; }
                catch (Final) { }
            }
            string chosen = null;
            if (zipPattern != null)
            {
                if (sums != null) chosen = sums.Keys.FirstOrDefault(n => Like(n, zipPattern));
                release.PickedBy = "its zip by name (read from GitHub's page)";
            }
            else
            {
                try
                {
                    chosen = ForeverIn(Net.Text(downloads + "release.json"));
                    if (chosen == null) throw new Unusable(tagName, "its release " + tagName + " has no build for WoW Forever (its release.json lists none)", "not for Forever");
                    release.PickedBy = "the release.json picks it for WoW Forever (read from GitHub's page)";
                }
                catch (Final why)
                {
                    if (!why.Message.StartsWith("not found")) throw;
                    if (sums != null) chosen = ZipByName(sums.Keys.Where(n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList(), RepoName(repo));
                    release.PickedBy = "picked by its name (read from GitHub's page)";
                }
            }
            if (chosen == null)
            {
                // No SHA256SUMS or release.json to name it (Spoken's, OpenMap's): the release's own list of files.
                List<Asset> listed = WebAssets(repo, tagName);
                if (listed == null) throw new Exception("without GitHub's API its zip cannot be told; checked again later");
                PickZip(release, listed, zipPattern, RepoName(repo));
                release.PickedBy += " (read from GitHub's page)";
                Sums(release, listed);
                if (release.Sha256 != null && !release.PickedBy.Contains("checked against")) release.PickedBy += ", checked against GitHub's sha256";
                return release;
            }
            if (chosen == null) throw new Exception("without GitHub's API its zip cannot be told; checked again later");
            release.ZipName = chosen;
            release.ZipUrl = downloads + Uri.EscapeDataString(chosen);
            string sha;
            if (sums != null && sums.TryGetValue(chosen, out sha)) { release.Sha256 = sha; release.PickedBy += ", checked against its SHA256SUMS"; }
            else
            {
                // GitHub's own sha256, from the release's page.
                List<Asset> listed = null;
                try { listed = WebAssets(repo, tagName); } catch (Exception) { }
                Asset same = listed == null ? null : listed.Find(x => x.Name == chosen && x.Sha256 != null);
                if (same != null) { release.Sha256 = same.Sha256; release.PickedBy += ", checked against GitHub's sha256"; }
            }
            return release;
        }

        // A tag in an address: each part escaped, "quests-audio%2Fv2.5.0"
        // as GitHub writes it.
        static string TagPath(string tag)
        {
            return Uri.EscapeDataString(tag);
        }

        // Without the API, the newest release of a series: the repository's
        // tags from git's own list (github.com/<repo>.git/info/refs, counted
        // in no limit), the newest of the series by its version, then the
        // release's files and sha256s from its page. A tag with no release,
        // a test version, or a release that cannot be installed (no zip),
        // gives way to the one before (three at most). Skip: a tag already
        // read and left out.
        static Release BySeriesWeb(string repo, string series, string zipPattern, bool betas, string skip = null)
        {
            string refs;
            HttpWebRequest request = Net.Make("https://github.com/" + repo + ".git/info/refs?service=git-upload-pack");
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream input = response.GetResponseStream())
                    refs = Encoding.UTF8.GetString(Net.ReadCapped(input, 8 * 1024 * 1024));
            }
            catch (WebException error)
            {
                if (Net.IsMissing(error, null)) throw new Final("GitHub has no repository by that name");
                throw;
            }
            List<string> tags = NewestFirst(TagsIn(refs), series);
            if (skip != null) tags.Remove(skip);
            if (tags.Count == 0) throw new Final(NoStable);
            Unusable first = null;
            foreach (string tagName in tags.Take(3))
            {
                List<Asset> assets = WebAssets(repo, tagName);
                if (assets == null) continue;
                if (!betas && TestVersion(repo, tagName)) continue;
                Release release = new Release { Repo = repo, Tag = tagName, PageUrl = "https://github.com/" + repo + "/releases/tag/" + TagPath(tagName) };
                try { PickZip(release, assets, zipPattern, RepoName(repo)); }
                catch (Unusable why) { if (first == null) first = why; continue; }
                release.PickedBy += " (read from GitHub's pages)";
                Sums(release, assets);
                if (release.Sha256 != null && !release.PickedBy.Contains("checked against")) release.PickedBy += ", checked against GitHub's sha256";
                if (first != null) release.Skip(first);
                return release;
            }
            if (first != null) throw first;
            throw new Exception("without GitHub's API its newest release (" + tags[0] + ") cannot be told; checked again later");
        }

        // The tags in git's list of refs ("<sha> refs/tags/<name>" lines).
        public static List<string> TagsIn(string refs)
        {
            List<string> tags = new List<string>();
            foreach (Match m in Regex.Matches(refs, @"refs/tags/([^\s\^\x00]+)"))
                if (!tags.Contains(m.Groups[1].Value)) tags.Add(m.Groups[1].Value);
            return tags;
        }

        // The tags of a series, the newest version first ("quests-audio/v2.10.0"
        // before "quests-audio/v2.9.1": the version is what follows the last /).
        public static List<string> NewestFirst(List<string> tags, string series)
        {
            List<string> mine = tags.Where(t => Regex.IsMatch(t, series)).ToList();
            mine.Sort((a, b) => Core.CompareVersions(b.Substring(b.LastIndexOf('/') + 1), a.Substring(a.LastIndexOf('/') + 1)));
            return mine;
        }

        // A release's files from its page (releases/expanded_assets/<tag>):
        // each one's name, address and GitHub's sha256; none when the
        // release has no files of its own (its author forgot them). Null
        // when the tag has no release.
        static List<Asset> WebAssets(string repo, string tag)
        {
            string page;
            try { page = Net.Text("https://github.com/" + repo + "/releases/expanded_assets/" + TagPath(tag)); }
            catch (Final) { return null; }
            List<Asset> assets = ParseAssets(page, repo);
            // None of its own: only when the page shows the release (its
            // "Source code" links), not a page this app no longer reads.
            if (assets.Count == 0 && page.IndexOf("/archive/refs/tags/", StringComparison.OrdinalIgnoreCase) < 0) return null;
            return assets;
        }

        public static List<Asset> ParseAssets(string page, string repo)
        {
            List<Asset> assets = new List<Asset>();
            string own = "/" + repo + "/releases/download/";
            foreach (string item in page.Split(new[] { "<li" }, StringSplitOptions.None))
            {
                Match link = Regex.Match(item, "href=\"(" + Regex.Escape(own) + "[^\"/]+/([^\"/]+))\"", RegexOptions.IgnoreCase);
                if (!link.Success) continue;
                string name = Uri.UnescapeDataString(link.Groups[2].Value);
                if (assets.Exists(a => a.Name == name)) continue;
                Match sha = Regex.Match(item, @"sha256:([0-9a-f]{64})");
                assets.Add(new Asset { Name = name, Url = "https://github.com" + System.Net.WebUtility.HtmlDecode(link.Groups[1].Value), Sha256 = sha.Success ? sha.Groups[1].Value : null });
            }
            return assets;
        }

        // A release page marked as a test version (pre-release).
        static bool TestVersion(string repo, string tag)
        {
            try { return Regex.IsMatch(Net.Text("https://github.com/" + repo + "/releases/tag/" + TagPath(tag)), @">\s*Pre-release\s*<"); }
            catch (Final) { return false; }
        }
    }

    // The friends' server (since 1.0.7): a Cloudflare Worker in front of the
    // pack's files on R2. It gives manifest.json (the pack's own releases:
    // tag, notes, and each file's path, size and sha256), pack.json and the
    // files (with Range), to this build's key alone (X-Forever-Key; the
    // GitHub token never goes there). The manifest stands for the private
    // repository's list of releases, in the same shape, so the checks, the
    // older versions and the app's own updates read it as they read GitHub's.
    // While it does not answer (not up yet, down, a key it does not know),
    // GitHub with the token is asked as before, and it is tried again two
    // minutes later. FUM_FRIENDS (tests only): another address
    // (http://127.0.0.1:<port>/ too), or "off".
    static class Friends
    {
        public const string Home = "https://friends.survivorui.com/";
        static readonly Regex PathOk = new Regex(@"^[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*$");
        static readonly object gate = new object();
        static object[] held;
        static string heldPack;
        static DateTime heldAt = DateTime.MinValue, packAt = DateTime.MinValue, downUntil = DateTime.MinValue;
        public static string LastWhy;       // why it was last left for GitHub, for the log

        // Tests: answers made up (the path asked: its text; null: no answer).
        public static Func<string, string> Fake = null;

        // The server's address; null when this build has no key for it (it is never asked then).
        public static string Base
        {
            get
            {
                if (Core.TestMode)
                {
                    string given = Core.Env("FUM_FRIENDS");
                    if (given == "off") return null;
                    if (given != "") return given.TrimEnd('/') + "/";
                    if (Fake != null) return Home;
                }
                return Secrets.HasFriendsKey ? Home : null;
            }
        }

        public static bool Owns(string url)
        {
            string at = Base;
            return at != null && url != null && url.StartsWith(at + "f/", StringComparison.OrdinalIgnoreCase);
        }

        // In use now: the last manifest asked came.
        public static bool Live { get { lock (gate) return held != null && DateTime.Now - heldAt < TimeSpan.FromMinutes(2); } }

        public static void Sign(HttpWebRequest request)
        {
            request.Headers["X-Forever-Key"] = Secrets.FriendsKey();
        }

        public static void Forget()
        {
            lock (gate) { held = null; heldPack = null; heldAt = packAt = downUntil = proxyDownUntil = DateTime.MinValue; }
        }

        // A small file of the server's, as text. Throws, saying why.
        static string Get(string path)
        {
            if (Fake != null)
            {
                string made = Fake(path);
                if (made == null) throw new Exception("no answer (test)");
                return made;
            }
            HttpWebRequest request = Net.Make(Base + path);
            request.AllowAutoRedirect = false;
            request.Timeout = 15000;
            Sign(request);
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    if (code != 200) throw new Exception("it answered " + code);
                    using (Stream input = response.GetResponseStream())
                        return Encoding.UTF8.GetString(Net.ReadCapped(input, 16 * 1024 * 1024));
                }
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                if (response == null) throw new Exception("it did not answer (" + error.Message + ")");
                using (response) throw new Exception("it answered " + (int)response.StatusCode);
            }
        }

        static void Down(string why)
        {
            LastWhy = why;
            downUntil = DateTime.Now.AddMinutes(2);
            Core.Log("friends' server: " + why + "; GitHub instead");
        }

        // The pack's releases from the manifest, newest first, in the shape of
        // GitHub's list; null when the server is not to be had now.
        public static object[] Releases()
        {
            if (Base == null) return null;
            lock (gate)
            {
                if (held != null && DateTime.Now - heldAt < TimeSpan.FromMinutes(2)) return held;
                if (DateTime.Now < downUntil) return null;
                try
                {
                    held = Shape(Get("manifest.json"), Base);
                    heldAt = DateTime.Now;
                    return held;
                }
                catch (Exception why) { held = null; Down("manifest.json: " + why.Message); return null; }
            }
        }

        static DateTime proxyDownUntil = DateTime.MinValue;

        // GitHub's list of releases of a public repository the pack lists,
        // through the server (/gh/, kept there 10 minutes): its text, or null
        // (not a list of releases, a repository the pack does not list, or the
        // server not to be had now), and GitHub is asked as before.
        public static string Proxy(string path)
        {
            if (Base == null) return null;
            Match m = Regex.Match(path ?? "", @"^repos/([A-Za-z0-9-]{1,39})/([A-Za-z0-9._-]{1,100})/releases\?per_page=(\d{1,3})(?:&page=(\d{1,2}))?$");
            if (!m.Success || Pack.Owns(m.Groups[1].Value + "/" + m.Groups[2].Value)) return null;
            lock (gate) if (DateTime.Now < proxyDownUntil) return null;
            try { return Get("gh/" + m.Groups[1].Value + "/" + m.Groups[2].Value + "/releases?per_page=" + m.Groups[3].Value + (m.Groups[4].Success ? "&page=" + m.Groups[4].Value : "")); }
            catch (Exception why)
            {
                // A repository not in the server's pack.json says 404: GitHub for it, the others still through the server.
                if (!why.Message.EndsWith(" 404")) lock (gate) proxyDownUntil = DateTime.Now.AddMinutes(2);
                Core.Log("friends' server, releases of " + m.Groups[1].Value + "/" + m.Groups[2].Value + ": " + why.Message + "; GitHub instead");
                return null;
            }
        }

        // pack.json from the server; null when it is not to be had now.
        public static string PackText()
        {
            if (Base == null) return null;
            lock (gate)
            {
                if (heldPack != null && DateTime.Now - packAt < TimeSpan.FromMinutes(2)) return heldPack;
                if (DateTime.Now < downUntil) return null;
                try
                {
                    string text = Get("pack.json");
                    Pack.Parse(text);
                    heldPack = text;
                    packAt = DateTime.Now;
                    return text;
                }
                catch (Exception why) { Down("pack.json: " + why.Message); return null; }
            }
        }

        // { "format": 1, "releases": [ { "tag", "name", "notes", "published",
        // "prerelease", "assets": [ { "name", "path", "size", "sha256" } ] } ],
        // "latest": {...} (the Discord links, not read here) } as GitHub's
        // list: each file's address on the server, its sha256 as a digest. A
        // release with a file that cannot be taken is left out whole.
        public static object[] Shape(string text, string at)
        {
            Dictionary<string, object> top = Json.Parse(text) as Dictionary<string, object>;
            if (top == null) throw new Exception("the manifest is not a JSON object");
            if (Json.Long(top, "format") != 1) throw new Exception("the manifest is of a format this version does not read");
            List<object> list = new List<object>();
            foreach (object each in Json.Arr(top, "releases"))
            {
                Dictionary<string, object> r = each as Dictionary<string, object>;
                string tag = Json.Str(r, "tag");
                if (r == null || tag == null || !Regex.IsMatch(tag, @"^[A-Za-z0-9._-]+/v[0-9A-Za-z._-]+$")) continue;
                List<object> assets = new List<object>();
                bool whole = true;
                foreach (object x in Json.Arr(r, "assets"))
                {
                    Dictionary<string, object> a = x as Dictionary<string, object>;
                    string name = Json.Str(a, "name"), path = Json.Str(a, "path"), sha = Json.Str(a, "sha256");
                    long size = Json.Long(a, "size");
                    if (name == null || path == null || !PathOk.IsMatch(path) || path.Contains("..") || sha == null
                        || !Regex.IsMatch(sha, "^[0-9a-f]{64}$") || size <= 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0) { whole = false; break; }
                    Dictionary<string, object> shaped = new Dictionary<string, object>();
                    shaped["name"] = name;
                    shaped["url"] = at + "f/" + path;
                    shaped["size"] = size;
                    shaped["digest"] = "sha256:" + sha;
                    assets.Add(shaped);
                }
                if (!whole) { Core.Log("manifest: " + tag + " has a file that cannot be taken, left out"); continue; }
                Dictionary<string, object> g = new Dictionary<string, object>();
                g["tag_name"] = tag;
                g["name"] = Json.Str(r, "name") ?? tag;
                g["body"] = Json.Str(r, "notes") ?? "";
                g["published_at"] = Json.Str(r, "published");
                g["html_url"] = null;
                g["prerelease"] = Json.Bool(r, "prerelease");
                g["draft"] = false;
                g["assets"] = assets.ToArray();
                list.Add(g);
            }
            if (list.Count == 0) throw new Exception("the manifest lists no release");
            return list.ToArray();
        }
    }

    // CurseForge's own API (api.curseforge.com, its key in the x-api-key
    // header; WoW is its game 1), for the projects that are only there. A
    // project's file is the newest one CurseForge lists for the game's own
    // version (from its .build.info, "1.60.1"), a release before a beta and a
    // beta before an alpha; its zip is checked against CurseForge's sha1 and
    // may hold addon folders only. The key goes to api.curseforge.com and
    // nowhere else; the download link it gives (CurseForge's file host)
    // gets no key. Answers are kept 15 minutes, and the last one on disk for
    // when CurseForge does not answer. FUM_CURSEFORGE (tests): a folder of
    // answers read instead, one file per question.
    static class CurseForge
    {
        public const string Api = "https://api.curseforge.com";
        public const string Site = "https://www.curseforge.com/wow/addons/";
        public const int Game = 1;
        public const string NoAppDownload = "its author does not let apps download it from CurseForge (no download link): get it on curseforge.com";
        static readonly TimeSpan Keep = TimeSpan.FromMinutes(15);
        static readonly Dictionary<string, KeyValuePair<DateTime, string>> held = new Dictionary<string, KeyValuePair<DateTime, string>>();

        // The game version files are picked for; set from the game's folder.
        public static string GameVersion = "";

        // Tests: CurseForge's answers made up (path to answer), and the calls counted.
        public static Func<string, string> Fake = null;
        public static int Calls;

        public static bool On { get { return Secrets.HasCurseForge || Core.Env("FUM_CURSEFORGE") != "" || Fake != null; } }
        public const string Off = "CurseForge is off in this build (it was built without a CurseForge key)";

        public static string RepoOf(long id) { return "curseforge:" + id.ToString(CultureInfo.InvariantCulture); }

        public static bool ValidSlug(string slug)
        {
            return slug != null && Regex.IsMatch(slug, "^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$");
        }

        // "https://www.curseforge.com/wow/addons/quest-completist" (and its
        // pages under it), "curseforge.com/wow/addons/quest-completist" or a
        // project id ("27328"): the slug, or the id. False when it is neither.
        public static bool ParseLink(string given, out long id, out string slug)
        {
            id = -1;
            slug = null;
            string text = (given ?? "").Trim().Trim('"', '\'', '<', '>');
            Match number = Regex.Match(text, @"^\d{1,10}$");
            if (number.Success)
            {
                long n;
                if (!long.TryParse(text, out n) || n <= 0 || n > int.MaxValue) return false;
                id = n;
                return true;
            }
            Match link = Regex.Match(text, @"^(?:https?://)?(?:www\.)?curseforge\.com/wow/addons/([^/?#\s]+)(?:[/?#].*)?$", RegexOptions.IgnoreCase);
            if (!link.Success || !ValidSlug(link.Groups[1].Value)) return false;
            slug = link.Groups[1].Value.ToLowerInvariant();
            return true;
        }

        static string FileName(string path)
        {
            return Regex.Replace(path.TrimStart('/'), "[^A-Za-z0-9]+", "-").Trim('-') + ".json";
        }

        // An answer of CurseForge's API, as text. 404: Final.
        public static string Get(string path)
        {
            if (Fake != null) { Calls++; return Fake(path); }
            string given = Core.Env("FUM_CURSEFORGE");
            if (given != "")
            {
                string file = Path.Combine(given, FileName(path));
                if (!File.Exists(file)) throw new Final("CurseForge has no such project (" + FileName(path) + " is not among this test's answers)");
                return File.ReadAllText(file, Encoding.UTF8);
            }
            if (!Secrets.HasCurseForge) throw new Final(Off);
            KeyValuePair<DateTime, string> kept;
            lock (held)
                if (held.TryGetValue(path, out kept) && DateTime.Now - kept.Key < Keep) return kept.Value;
            string disk = Path.Combine(Core.Home(), "curseforge", FileName(path));
            string text;
            try { text = Ask(path); }
            catch (Final) { throw; }
            catch (Exception why)
            {
                if (!File.Exists(disk)) throw;
                Core.Log("CurseForge did not answer (" + why.Message + "): its last answer used for " + path);
                return File.ReadAllText(disk, Encoding.UTF8);
            }
            lock (held) held[path] = new KeyValuePair<DateTime, string>(DateTime.Now, text);
            try { Directory.CreateDirectory(Path.GetDirectoryName(disk)); Core.WriteAll(disk, text); } catch { }
            return text;
        }

        static string Ask(string path)
        {
            Calls++;
            HttpWebRequest request = Net.Make(Api + path);
            request.Accept = "application/json";
            request.Headers["x-api-key"] = Secrets.CurseForgeKey();
            request.AllowAutoRedirect = false;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    if (code >= 300) throw new Exception("CurseForge answered " + code);
                    using (Stream input = response.GetResponseStream())
                        return Encoding.UTF8.GetString(Net.ReadCapped(input, 8 * 1024 * 1024));
                }
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                if (response == null) throw new ApiOut("CurseForge did not answer (" + error.Message + ")");
                using (response)
                {
                    int code = (int)response.StatusCode;
                    if (code == 404) throw new Final("CurseForge has no such project");
                    if (code == 401 || code == 403) throw new Exception("CurseForge refused this build's key (" + code + ")");
                    throw new Exception("CurseForge answered " + code + " " + response.StatusDescription);
                }
            }
        }

        static Dictionary<string, object> Data(string text)
        {
            Dictionary<string, object> top = Json.Parse(text) as Dictionary<string, object>;
            if (top == null) throw new Exception("CurseForge's answer could not be read");
            return top;
        }

        // A project of WoW's, by its id: its data (name, slug, authors, links).
        public static Dictionary<string, object> Mod(long id)
        {
            Dictionary<string, object> mod = Json.Obj(Data(Get("/v1/mods/" + id.ToString(CultureInfo.InvariantCulture))), "data");
            if (mod == null || Json.Long(mod, "id") != id) throw new Exception("CurseForge's answer could not be read");
            if (Json.Long(mod, "gameId") != Game) throw new Final("CurseForge project " + id + " is not a World of Warcraft addon");
            return mod;
        }

        // A project of WoW's, by the name in its link.
        public static Dictionary<string, object> BySlug(string slug)
        {
            object[] found = Json.Arr(Data(Get("/v1/mods/search?gameId=" + Game + "&slug=" + Uri.EscapeDataString(slug))), "data");
            foreach (object each in found)
            {
                Dictionary<string, object> mod = each as Dictionary<string, object>;
                if (mod != null && Json.Long(mod, "gameId") == Game && string.Equals(Json.Str(mod, "slug"), slug, StringComparison.OrdinalIgnoreCase) && Json.Long(mod, "id") > 0)
                    return mod;
            }
            throw new Final("CurseForge has no World of Warcraft addon at " + Site + slug);
        }

        // A project's name, slug and first author, for the list.
        public static void Describe(TrackedAddon a, Dictionary<string, object> mod)
        {
            string name = Json.Str(mod, "name");
            if (name != null) name = Regex.Replace(name, @"[\x00-\x1f]", "").Trim();
            // A pack's project keeps the pack's name.
            if (!string.IsNullOrEmpty(name) && name.Length <= 80 && (a.Source != "pack" || string.IsNullOrEmpty(a.Title))) a.Title = name;
            if (ValidSlug(Json.Str(mod, "slug"))) a.Slug = Json.Str(mod, "slug");
            object[] authors = Json.Arr(mod, "authors");
            if (authors.Length > 0) a.Author = Json.Str(authors[0] as Dictionary<string, object>, "name");
        }

        // The file for the game's version, by one question: the project's
        // newest files (50), the right one picked here.
        public static Release Latest(long id)
        {
            string text = Get("/v1/mods/" + id.ToString(CultureInfo.InvariantCulture) + "/files?pageSize=50");
            return Pick(Json.Arr(Data(text), "data"), GameVersion, id);
        }

        static int Rank(long releaseType) { return releaseType == 1 ? 0 : releaseType == 2 ? 1 : releaseType == 3 ? 2 : 3; }

        static string Kind(long releaseType) { return releaseType == 1 ? "release" : releaseType == 2 ? "beta" : "alpha"; }

        // Of a project's files, the one for this game version: listed for it
        // in gameVersions, a release before a beta before an alpha, the
        // newest first. Throws (Final) saying why there is none, or why it
        // cannot be taken (no download link, no sha1, a link elsewhere).
        public static Release Pick(object[] files, string version, long id)
        {
            if (string.IsNullOrEmpty(version)) throw new Final("the game's version is not known (no .build.info), so its CurseForge file cannot be told");
            List<Dictionary<string, object>> fit = new List<Dictionary<string, object>>();
            foreach (object each in files)
            {
                Dictionary<string, object> f = each as Dictionary<string, object>;
                if (f == null || (f.ContainsKey("isAvailable") && !Json.Bool(f, "isAvailable"))) continue;
                if (!Json.Arr(f, "gameVersions").Any(v => string.Equals(v as string, version, StringComparison.OrdinalIgnoreCase))) continue;
                long type = Json.Long(f, "releaseType");
                if (type < 1 || type > 3) continue;
                fit.Add(f);
            }
            if (fit.Count == 0) throw new Final("CurseForge has no file of it for WoW Forever " + version);
            Dictionary<string, object> pick = fit.OrderBy(f => Rank(Json.Long(f, "releaseType")))
                .ThenByDescending(f => Json.Str(f, "fileDate") ?? "", StringComparer.Ordinal)
                .ThenByDescending(f => Json.Long(f, "id")).First();
            return Build(pick, version, id);
        }

        // Its other files for this game version, newest first, from the
        // answer the check just read (kept 15 minutes: no call more). One
        // that cannot be taken is left out.
        public static IEnumerable<Release> Older(long id, long skipFile, int most)
        {
            string version = GameVersion;
            object[] files;
            try { files = Json.Arr(Data(Get("/v1/mods/" + id.ToString(CultureInfo.InvariantCulture) + "/files?pageSize=50")), "data"); }
            catch (Exception why) { Core.Log("older files of CurseForge " + id + ": " + why.Message); yield break; }
            int given = 0;
            foreach (Dictionary<string, object> f in files.OfType<Dictionary<string, object>>()
                .Where(f => Json.Arr(f, "gameVersions").Any(v => string.Equals(v as string, version, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => Json.Str(f, "fileDate") ?? "", StringComparer.Ordinal))
            {
                if (given >= most) yield break;
                long type = Json.Long(f, "releaseType");
                if (type < 1 || type > 3 || Json.Long(f, "id") == skipFile) continue;
                Release r = null;
                try { r = Build(f, version, id); }
                catch (Exception why) { Core.Log("older file " + Json.Long(f, "id") + " of CurseForge " + id + ": " + why.Message); }
                if (r == null) continue;
                given++;
                yield return r;
            }
        }

        static Release Build(Dictionary<string, object> pick, string version, long id)
        {
            long kind = Json.Long(pick, "releaseType");
            Release r = new Release();
            r.Repo = RepoOf(id);
            r.FileId = Json.Long(pick, "id");
            string shown = Json.Str(pick, "displayName");
            r.ZipName = Json.Str(pick, "fileName") ?? "";
            r.Tag = !string.IsNullOrEmpty(shown) ? Regex.Replace(shown, @"[\x00-\x1f]", "").Trim() : r.ZipName;
            r.Published = Json.Str(pick, "fileDate");
            r.Size = Json.Long(pick, "fileLength");
            r.Test = kind != 1;
            if (!r.ZipName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || r.ZipName.IndexOfAny(new[] { '/', '\\' }) >= 0)
                throw new Final("its CurseForge file " + r.ZipName + " is not a zip");
            foreach (object h in Json.Arr(pick, "hashes"))
            {
                Dictionary<string, object> hash = h as Dictionary<string, object>;
                string value = Json.Str(hash, "value");
                if (Json.Long(hash, "algo") == 1 && value != null && Regex.IsMatch(value, "^[0-9a-fA-F]{40}$")) r.Sha1 = value.ToLowerInvariant();
            }
            string url = Json.Str(pick, "downloadUrl");
            if (string.IsNullOrEmpty(url)) throw new Final(NoAppDownload);
            Uri at;
            if (!Uri.TryCreate(url, UriKind.Absolute, out at) || at.Scheme != "https" || !(at.Host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase)))
                throw new Final("its CurseForge download is not on CurseForge's file host (" + Net.Bare(url) + ")");
            if (r.Sha1 == null) throw new Final("CurseForge gives no sha1 for its file " + r.ZipName);
            r.ZipUrl = url;
            r.PickedBy = "CurseForge's newest " + Kind(kind) + " file for " + version + ", checked against its sha1";
            return r;
        }
    }

    // The Manager's own new versions: releases of the pack's repository in
    // the series manager/v<version>, each with ForeverManager-<version>.exe,
    // read with the same call as the addons' (no call more). A newer one is
    // downloaded beside the exe, checked against GitHub's sha256, kept as
    // <exe>.new, and swapped in when the Manager starts again (or at once with
    // Restart to update): the running exe renamed to <exe>.old (Windows lets
    // a running exe be renamed, not written over), the new one put in its
    // place and started; the old one deleted by the next start. Never in a
    // test unless FUM_SELF_EXE names a copy to update.
    static class SelfUpdate
    {
        public const string Series = @"^manager/v\d";
        public const string ExePattern = "ForeverManager-*.exe";
        const long Cap = 64L * 1048576;
        public static bool RestartAsked;

        public class Offer
        {
            public string Tag, Version, Name, Url, Sha256;
            public long Size = -1;
        }

        // The exe to update: this one; in a test only the copy FUM_SELF_EXE names.
        public static string ExePath()
        {
            string given = Core.Env("FUM_SELF_EXE");
            if (given != "") return given;
            return Core.TestMode ? null : Application.ExecutablePath;
        }

        static string NewPath(string exe) { return exe + ".new"; }
        static string NotePath(string exe) { return exe + ".new.sha256"; }
        static string OldPath(string exe) { return exe + ".old"; }
        static string PartPath(string exe) { return exe + ".download"; }

        // "manager/v1.2.0": "1.2.0".
        public static string VersionOf(string tag)
        {
            string last = (tag ?? "").Substring((tag ?? "").LastIndexOf('/') + 1);
            return last.TrimStart('v', 'V');
        }

        // Of the repository's releases, the newest of the Manager's series
        // when it is newer than current; null when none is. Throws (Final)
        // when it cannot be taken (no exe, several, no sha256).
        public static Offer Pick(object[] releases, string current)
        {
            Dictionary<string, object> best = null;
            foreach (object each in releases)
            {
                Dictionary<string, object> r = each as Dictionary<string, object>;
                if (r == null || Json.Bool(r, "draft") || Json.Bool(r, "prerelease")) continue;
                string tag = Json.Str(r, "tag_name") ?? "";
                if (!Regex.IsMatch(tag, Series)) continue;
                if (best == null || Core.CompareVersions(VersionOf(tag), VersionOf(Json.Str(best, "tag_name"))) > 0) best = r;
            }
            if (best == null) return null;
            string bestTag = Json.Str(best, "tag_name");
            if (Core.CompareVersions(VersionOf(bestTag), current) <= 0) return null;
            string own = "https://api.github.com/repos/" + Pack.Repo + "/releases/assets/";
            List<Dictionary<string, object>> exes = Json.Arr(best, "assets").OfType<Dictionary<string, object>>()
                .Where(a => GitHub.Like(Json.Str(a, "name") ?? "", ExePattern)).ToList();
            if (exes.Count != 1) throw new Final("its release " + bestTag + " has " + (exes.Count == 0 ? "no" : "several") + " files named like " + ExePattern);
            Dictionary<string, object> asset = exes[0];
            string url = Json.Str(asset, "url");
            if (!Friends.Owns(url) && (url == null || !url.StartsWith(own, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(url.Substring(own.Length), @"^\d+$")))
                throw new Final("its release " + bestTag + " gives no API address for its exe");
            string sha = GitHub.Digest(Json.Str(asset, "digest"));
            if (sha == null) throw new Final("its release " + bestTag + " gives no sha256 for its exe");
            return new Offer { Tag = bestTag, Version = VersionOf(bestTag), Name = Json.Str(asset, "name"), Url = url, Sha256 = sha, Size = Json.Long(asset, "size") };
        }

        public static Offer Check()
        {
            return Pick(GitHub.PrivateReleases(), Core.Version);
        }

        // The version ready to go in (downloaded and still whole); null when none.
        public static string Ready(string exe)
        {
            if (exe == null || !File.Exists(NewPath(exe)) || !File.Exists(NotePath(exe))) return null;
            try
            {
                string[] note = File.ReadAllText(NotePath(exe)).Trim().Split(' ');
                if (note.Length == 2 && Regex.IsMatch(note[0], "^[0-9a-f]{64}$") && Core.Sha256Of(NewPath(exe)) == note[0]) return note[1];
            }
            catch (Exception why) { Core.Log("update: " + why.Message); }
            return null;
        }

        // The offer downloaded beside the exe (through the API, with the
        // token) and checked; ready for the next start. Download (tests):
        // instead of GitHub, how the file comes.
        public static void Fetch(Offer o, string exe, Action<string, string> download)
        {
            if (exe == null) throw new Exception("not in a test");
            if (Ready(exe) == o.Version) return;
            string part = PartPath(exe);
            if (download != null) download(o.Url, part);
            else Net.Download(o.Url, part, null, null, Cap, true, Secrets.GitHubToken());
            try
            {
                if (o.Size > 0 && new FileInfo(part).Length != o.Size) throw new Exception("the new version's download is " + new FileInfo(part).Length + " bytes and GitHub says " + o.Size);
                if (Core.Sha256Of(part) != o.Sha256) throw new Exception("the new version's download does not match GitHub's sha256");
            }
            catch
            {
                try { File.Delete(part); } catch { }
                throw;
            }
            if (File.Exists(NewPath(exe))) File.Delete(NewPath(exe));
            File.Move(part, NewPath(exe));
            Core.WriteAll(NotePath(exe), o.Sha256 + " " + o.Version);
            Core.Log("update: " + Core.Name + " " + o.Version + " downloaded, it goes in at the next start");
        }

        // The new one in the exe's place, the running one kept as .old. True when done.
        public static bool Swap(string exe)
        {
            string ready = Ready(exe);
            if (ready == null) return false;
            string old = OldPath(exe);
            try
            {
                if (File.Exists(old))
                {
                    try { File.Delete(old); }
                    catch (Exception) { old = exe + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".old"; }
                }
                File.Move(exe, old);
                try { File.Move(NewPath(exe), exe); }
                catch
                {
                    File.Move(old, exe);
                    throw;
                }
                try { File.Delete(NotePath(exe)); } catch { }
                Core.Log("update: " + Core.Name + " " + ready + " swapped in");
                return true;
            }
            catch (Exception why)
            {
                Core.Log("update: could not swap in " + ready + ": " + why.Message);
                return false;
            }
        }

        // At start: a version ready swapped in and started; true when this one should stop.
        public static bool ApplyAtStart(string[] args)
        {
            string exe = ExePath();
            if (exe == null || !Swap(exe)) return false;
            if (Core.TestMode) return false;   // a test's copy: swapped, never started
            List<string> keep = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--after-update") { i++; continue; }
                keep.Add(args[i]);
            }
            keep.Add("--after-update");
            keep.Add(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            try
            {
                Process.Start(new ProcessStartInfo(exe, string.Join(" ", keep.Select(a => "\"" + a + "\"").ToArray())) { UseShellExecute = false });
                return true;
            }
            catch (Exception why)
            {
                Core.Log("update: the new version did not start: " + why.Message);
                return false;
            }
        }

        // The exe before the last update, once it has stopped.
        public static void CleanOld()
        {
            string exe = ExePath();
            if (exe == null) return;
            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(exe)), name = Path.GetFileName(exe);
                foreach (string f in Directory.GetFiles(folder, name + "*.old")) try { File.Delete(f); } catch { }
            }
            catch { }
        }

        public static void WaitFor(string pid)
        {
            int id;
            if (!int.TryParse(pid, out id)) return;
            try { using (Process p = Process.GetProcessById(id)) p.WaitForExit(15000); }
            catch { }
        }
    }

    class Toc
    {
        public string Title, Version, Interface;
    }

    class Placed
    {
        public string[] Folders;
        public int Skipped;
        public string Left;     // the old copy, when the Recycle Bin refused it
        public string[] Kept;   // folders of the zip's names already in AddOns, not ours: left as they were
    }

    // A file a zip holds, as its central directory lists it.
    class ZipItem
    {
        public string Name;
        public uint Crc;
        public long Size;
    }

    // An addon in AddOns that this app did not put there, told apart: the
    // same files as one of its releases (the newest, else up to Recognize.Most
    // older ones), file by file (size and CRC-32, from each zip's central
    // directory, read with two small Range requests: no zip is downloaded);
    // or none of them (a dev copy).
    static class Recognize
    {
        public const int Most = 5;

        public enum Kind { Absent, Same, Differs }

        public class Result
        {
            public Kind Kind = Kind.Absent;
            public Release Release;     // Same: the release it matches
            public string[] Folders;    // its folders in AddOns
            public int Compared;        // releases compared
        }

        // Tests: a zip's index made up (a Release's ZipUrl, its index).
        public static Func<Release, List<ZipItem>> FakeIndex = null;

        static readonly Dictionary<string, List<ZipItem>> held = new Dictionary<string, List<ZipItem>>();

        // Run in the check's background work. Null when there is nothing to tell
        // (no folder of it in AddOns, its zip cannot be read).
        public static Result Look(string addons, TrackedAddon a, Release latest)
        {
            if (addons == null || latest == null || !Directory.Exists(addons)) return null;
            Result result = new Result();
            IEnumerable<Release> older = a.IsCurse ? CurseForge.Older(a.CurseId, latest.FileId, Most) : GitHub.Older(a, latest.Tag, Most);
            foreach (Release r in new[] { latest }.Concat(older))
            {
                List<ZipItem> index;
                try { index = Index(r); }
                catch (Exception why) { Core.Log("recognize " + a.Repo + " " + r.Tag + ": " + why.Message); index = null; }
                if (index == null) { if (result.Compared == 0) return null; continue; }
                result.Compared++;
                string[] folders;
                Kind kind = Compare(addons, index, out folders);
                // Its folders are not in AddOns (by the newest zip's names): nothing to tell.
                if (kind == Kind.Absent) { if (result.Compared == 1) return null; continue; }
                if (result.Folders == null) result.Folders = folders;
                if (kind == Kind.Same)
                {
                    result.Kind = Kind.Same;
                    result.Release = r;
                    result.Folders = folders;
                    return result;
                }
                result.Kind = Kind.Differs;
            }
            return result.Kind == Kind.Absent ? null : result;
        }

        // A zip's files from its central directory (held for this run).
        public static List<ZipItem> Index(Release r)
        {
            if (FakeIndex != null) return FakeIndex(r);
            // Tests (FUM_RECOGNIZE_ZIPS): a folder of zips by their names stands for the downloads.
            string given = Core.Env("FUM_RECOGNIZE_ZIPS");
            if (given != "")
            {
                string local = Path.Combine(given, r.ZipName ?? "");
                if (r.ZipName == null || !File.Exists(local)) return null;
                return FromTail(File.ReadAllBytes(local), 0, null);
            }
            if (r.ZipUrl == null) return null;
            List<ZipItem> found;
            lock (held) if (held.TryGetValue(r.ZipUrl, out found)) return found;
            string token = r.Private ? Secrets.GitHubToken() : null;
            long start, total;
            // Its last 64 KB by their place when its size is known (GitHub's file host takes no "last n bytes").
            byte[] tail = r.Size > 0 ? Net.Range(r.ZipUrl, token, Math.Max(0, r.Size - 65557), r.Size - 1, out start, out total)
                : Net.Range(r.ZipUrl, token, -65557, -1, out start, out total);
            found = FromTail(tail, start, delegate(long from, long to)
            {
                long at, all;
                byte[] piece = Net.Range(r.ZipUrl, token, from, to, out at, out all);
                if (at != from) throw new Exception("its zip's list of files did not come whole");
                return piece;
            });
            lock (held) held[r.ZipUrl] = found;
            return found;
        }

        // A zip's files from its last bytes (tail, starting at start in the
        // zip); more(from, to) brings the list of files when the tail does not hold it.
        public static List<ZipItem> FromTail(byte[] tail, long start, Func<long, long, byte[]> more)
        {
            int end = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
                if (U32(tail, i) == 0x06054b50) { end = i; break; }
            if (end < 0) throw new Exception("its zip has no end record in its last 64 KB");
            long count = U16(tail, end + 10), size = U32(tail, end + 12), offset = U32(tail, end + 16);
            if (count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF) throw new Exception("its zip is a ZIP64, not read");
            byte[] dir;
            if (offset >= start && offset + size <= start + tail.Length)
            {
                dir = new byte[size];
                Array.Copy(tail, offset - start, dir, 0, size);
            }
            else
            {
                if (size > 32L * 1048576) throw new Exception("its zip's list of files is too big");
                dir = more(offset, offset + size - 1);
                if (dir.Length != size) throw new Exception("its zip's list of files did not come whole");
            }
            return Parse(dir, count);
        }

        static uint U32(byte[] b, long i) { return (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24); }
        static int U16(byte[] b, long i) { return b[i] | b[i + 1] << 8; }

        // A central directory's entries.
        public static List<ZipItem> Parse(byte[] dir, long count)
        {
            List<ZipItem> items = new List<ZipItem>();
            int i = 0;
            for (long n = 0; n < count; n++)
            {
                if (i + 46 > dir.Length || U32(dir, i) != 0x02014b50) throw new Exception("its zip's list of files could not be read");
                int flags = U16(dir, i + 8), nameLen = U16(dir, i + 28), extra = U16(dir, i + 30), comment = U16(dir, i + 32);
                if (i + 46 + nameLen > dir.Length) throw new Exception("its zip's list of files is cut short");
                Encoding text = (flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.GetEncoding(437);
                items.Add(new ZipItem
                {
                    Name = text.GetString(dir, i + 46, nameLen).Replace('\\', '/'),
                    Crc = U32(dir, i + 16),
                    Size = U32(dir, i + 24)
                });
                i += 46 + nameLen + extra + comment;
            }
            return items;
        }

        // AddOns against a zip's files, as Installer.Put would place them:
        // Absent when none of its folders is there; Same when every file of
        // it is there with the same size and CRC-32 (files of their own
        // beside them do not count); else Differs.
        public static Kind Compare(string addons, List<ZipItem> index, out string[] folders)
        {
            List<KeyValuePair<string, string>> roots = Installer.FoldersIn(index.Select(e => e.Name));
            folders = roots.Select(f => f.Value).ToArray();
            if (roots.Count == 0 || roots.Any(f => !Installer.SafeFolder(f.Value))) return Kind.Absent;
            int there = roots.Count(f => Directory.Exists(Path.Combine(addons, f.Value)));
            if (there == 0) return Kind.Absent;
            if (there < roots.Count) return Kind.Differs;
            List<KeyValuePair<string, ZipItem>> files = new List<KeyValuePair<string, ZipItem>>();
            foreach (KeyValuePair<string, string> f in roots)
            {
                string target = Path.GetFullPath(Path.Combine(addons, f.Value));
                foreach (ZipItem e in index)
                {
                    if (e.Name.EndsWith("/") || !e.Name.StartsWith(f.Key, StringComparison.OrdinalIgnoreCase) || !Installer.Takes(e.Name)) continue;
                    string path;
                    try { path = Path.GetFullPath(Path.Combine(target, e.Name.Substring(f.Key.Length))); }
                    catch { continue; }
                    if (!path.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                    files.Add(new KeyValuePair<string, ZipItem>(path, e));
                }
            }
            // Sizes first (cheap), then the files read.
            foreach (KeyValuePair<string, ZipItem> f in files)
            {
                FileInfo info = new FileInfo(f.Key);
                if (!info.Exists || info.Length != f.Value.Size) return Kind.Differs;
            }
            foreach (KeyValuePair<string, ZipItem> f in files)
                if (Crc32.Of(f.Key) != f.Value.Crc) return Kind.Differs;
            return Kind.Same;
        }
    }

    static class Crc32
    {
        static readonly uint[] table = Make();

        static uint[] Make()
        {
            uint[] t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Of(byte[] data, int count)
        {
            uint c = 0xFFFFFFFF;
            for (int i = 0; i < count; i++) c = table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }

        public static uint Of(string path)
        {
            uint c = 0xFFFFFFFF;
            byte[] buffer = new byte[65536];
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    for (int i = 0; i < read; i++) c = table[(c ^ buffer[i]) & 0xFF] ^ (c >> 8);
            }
            return c ^ 0xFFFFFFFF;
        }
    }

    static class Installer
    {
        // A zip's file that Put places (not one of the Refused kinds).
        public static bool Takes(string name)
        {
            return Array.IndexOf(Refused, Path.GetExtension(name).ToLowerInvariant()) < 0;
        }

        // Never put in AddOns: the game runs none of these, and nothing else should.
        static readonly string[] Refused = { ".exe", ".dll", ".bat", ".cmd", ".com", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse",
            ".wsf", ".wsh", ".msi", ".msp", ".scr", ".lnk", ".url", ".hta", ".cpl", ".jar", ".reg", ".sys", ".pif", ".inf", ".ocx",
            ".application", ".appref-ms", ".gadget", ".sh" };

        public static bool SafeFolder(string folder)
        {
            return !string.IsNullOrEmpty(folder) && folder.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && folder.Trim() == folder
                && !folder.StartsWith(".") && !folder.EndsWith(".") && !folder.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase);
        }

        // A .toc names its folder: Folder.toc, or with a game's tag,
        // Folder_Camelot.toc, Folder-Mainline.toc.
        static bool TocOf(string file, string folder)
        {
            if (string.Equals(file, folder, StringComparison.OrdinalIgnoreCase)) return true;
            if (file.Length <= folder.Length + 1 || !file.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;
            char mark = file[folder.Length];
            return (mark == '_' || mark == '-') && Regex.IsMatch(file.Substring(folder.Length + 1), "^[A-Za-z]+$");
        }

        // The addons in a zip, as (its root in the zip, its folder): each
        // folder holding its own .toc, wherever, none inside another; else a
        // .toc at the top of the zip (or of the one folder holding it all, as
        // a repository's own archive has it), named by the .toc.
        public static List<KeyValuePair<string, string>> FoldersIn(IEnumerable<string> entries)
        {
            List<string> names = entries.Select(n => n.Replace('\\', '/')).ToList();
            List<KeyValuePair<string, string>> found = new List<KeyValuePair<string, string>>();
            foreach (string name in names)
            {
                if (name.EndsWith("/") || !name.EndsWith(".toc", StringComparison.OrdinalIgnoreCase)) continue;
                int slash = name.LastIndexOf('/');
                if (slash < 0) continue;
                string prefix = name.Substring(0, slash + 1);
                string file = name.Substring(slash + 1, name.Length - slash - 5);
                string folder = prefix.TrimEnd('/');
                folder = folder.Substring(folder.LastIndexOf('/') + 1);
                if (TocOf(file, folder)) found.Add(new KeyValuePair<string, string>(prefix, folder));
            }
            if (found.Count == 0)
            {
                string top = null;
                foreach (string name in names)
                {
                    int slash = name.IndexOf('/');
                    string first = slash > 0 ? name.Substring(0, slash + 1) : "";
                    if (top == null) top = first;
                    else if (!string.Equals(top, first, StringComparison.OrdinalIgnoreCase)) top = "";
                }
                top = top ?? "";
                List<string> atTop = new List<string>();
                foreach (string name in names)
                {
                    if (!name.EndsWith(".toc", StringComparison.OrdinalIgnoreCase)) continue;
                    int slash = name.LastIndexOf('/');
                    if (!string.Equals(name.Substring(0, slash + 1), top, StringComparison.OrdinalIgnoreCase)) continue;
                    string file = name.Substring(slash + 1, name.Length - slash - 5);
                    atTop.Add(Regex.Replace(file, "[_-][A-Za-z]+$", ""));
                }
                if (atTop.Count > 0)
                {
                    atTop.Sort((a, b) => a.Length != b.Length ? a.Length - b.Length : string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
                    found.Add(new KeyValuePair<string, string>(top, atTop[0]));
                }
            }
            found.Sort((a, b) => a.Key.Length != b.Key.Length ? a.Key.Length - b.Key.Length : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            List<KeyValuePair<string, string>> kept = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> each in found)
                if (!kept.Exists(outer => outer.Key.Length > 0 && each.Key.StartsWith(outer.Key, StringComparison.OrdinalIgnoreCase))
                    && !kept.Exists(other => string.Equals(other.Value, each.Value, StringComparison.OrdinalIgnoreCase)))
                    kept.Add(each);
            return kept;
        }

        // Guide packs (guidesOnly): the zip holds guide folders (ForeverGuides'
        // ForeverGuidesPack_, Polestar's PolestarPack_) and nothing else,
        // so it cannot put another addon in AddOns.
        public static readonly string[] GuideFolders = { "PolestarPack_", "ForeverGuidesPack_" };

        public static bool GuideFolder(string folder)
        {
            return GuideFolders.Any(p => folder.StartsWith(p, StringComparison.Ordinal) && folder.Length > p.Length);
        }

        public static void OnlyGuides(string zip)
        {
            using (ZipArchive archive = ZipFile.OpenRead(zip))
            {
                List<KeyValuePair<string, string>> folders = FoldersIn(archive.Entries.Select(e => e.FullName));
                if (folders.Count == 0) throw new Final("its zip holds no guide");
                foreach (KeyValuePair<string, string> f in folders)
                    if (!GuideFolder(f.Value))
                        throw new Final("its zip holds more than guides (" + f.Value + ")");
            }
        }

        // Room to work, kept free on each drive besides what is needed.
        const long Spare = 200L * 1048576;

        static string Root(string folder)
        {
            return Path.GetPathRoot(Path.GetFullPath(folder)).ToUpperInvariant();
        }

        // The free room on a folder's drive; FUM_TEST_FREE (tests) says it instead.
        public static long Free(string folder)
        {
            long given;
            if (long.TryParse(Core.Env("FUM_TEST_FREE"), out given)) return given;
            try { return new DriveInfo(Root(folder)).AvailableFreeSpace; }
            catch { return long.MaxValue; }
        }

        static void Need(string folder, long bytes)
        {
            long free = Free(folder);
            if (free < bytes + Spare)
                throw new Exception("there is not room enough on " + Root(folder).TrimEnd('\\') + ": it needs " + Core.Bytes(bytes + Spare) + " free and has " + Core.Bytes(free));
        }

        // Before a download: room for the zip in %TEMP%, and, when AddOns is
        // on the same drive, for what it unpacks to (about the same, for
        // sound, which a zip barely makes smaller). Have: what is held of it.
        public static void RoomToDownload(string temp, string addons, long size, long have)
        {
            if (size <= 0) return;
            long rest = Math.Max(0, size - have);
            if (Root(temp) == Root(addons)) Need(temp, rest + size);
            else { Need(temp, rest); Need(addons, size); }
        }

        // Before unpacking: room beside AddOns for all the zip holds (moved
        // into AddOns after, not copied; the old version goes to the Recycle
        // Bin, on the same drive).
        public static void RoomToUnpack(string addons, string zip)
        {
            long unpacked = 0;
            using (ZipArchive archive = ZipFile.OpenRead(zip))
                foreach (ZipArchiveEntry entry in archive.Entries) unpacked += entry.Length;
            Need(addons, unpacked);
        }

        // Where a GitHub download is held while it comes: one name per file,
        // so one that stopped is taken up again at the next try.
        public static string PartPath(string temp, Release r)
        {
            string id = r.Sha256 != null ? r.Sha256.Substring(0, 16) : "u" + Core.Sha256Of(Encoding.UTF8.GetBytes(r.ZipUrl ?? "")).Substring(0, 15);
            return Path.Combine(temp, id + ".part");
        }

        // Leftovers in the app's temp folder: parts a week old, zips a day old.
        public static void CleanTemp(string temp)
        {
            try
            {
                foreach (string f in Directory.GetFiles(temp))
                {
                    TimeSpan age = DateTime.Now - File.GetLastWriteTime(f);
                    if ((f.EndsWith(".part") && age.TotalDays > 7) || (f.EndsWith(".zip") && age.TotalDays > 1)) File.Delete(f);
                }
            }
            catch { }
        }

        public static void Verify(string zip, Release release)
        {
            long size = new FileInfo(zip).Length;
            if (release.Size > 0 && size != release.Size)
                throw new Exception("the download is " + size + " bytes and its source says " + release.Size);
            if (release.Sha256 != null && !string.Equals(Core.Sha256Of(zip), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new Exception("the download does not match the sha256 its source gives");
            if (release.Sha1 != null && !string.Equals(Sha1Of(zip), release.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new Exception("the download does not match the sha1 CurseForge gives");
        }

        public static string Sha1Of(string path)
        {
            using (FileStream input = File.OpenRead(path))
            using (SHA1 hash = SHA1.Create())
                return Core.Hex(hash.ComputeHash(input));
        }

        // A CurseForge file: addon folders only, each with its own .toc, at
        // the top of the zip; no file beside or outside them.
        public static void OnlyAddonFolders(string zip)
        {
            using (ZipArchive archive = ZipFile.OpenRead(zip))
            {
                List<KeyValuePair<string, string>> folders = FoldersIn(archive.Entries.Select(e => e.FullName));
                if (folders.Count == 0) throw new Final("its zip holds no addon folder with its own .toc");
                foreach (KeyValuePair<string, string> f in folders)
                    if (!string.Equals(f.Key, f.Value + "/", StringComparison.OrdinalIgnoreCase))
                        throw new Final("its zip's addon " + f.Value + " is not a folder at the top of the zip");
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (!folders.Exists(f => name.StartsWith(f.Key, StringComparison.OrdinalIgnoreCase)))
                        throw new Final("its zip holds more than addon folders (" + name + ")");
                }
            }
        }

        static string Stage(string addons)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(addons).TrimEnd('\\'));
            return Path.Combine(parent, "_fm-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        static string Plain(string label)
        {
            foreach (char bad in Path.GetInvalidFileNameChars()) label = label.Replace(bad, '-');
            return label.Trim().TrimEnd('.');
        }

        static Exception InUse(string folder, Exception why)
        {
            return new Exception("AddOns\\" + folder + " could not be moved (" + why.Message.TrimEnd('.') + "); is a file of it open?");
        }

        // What to do with a folder of the zip's name that is in AddOns and was
        // not put there by this app (not among before): Refuse (InTheWay, and
        // nothing changes), Skip (it stays as it is and the zip's copy is
        // left out: guide packs, each folder on its own), Replace (the user
        // said yes: it goes to the Recycle Bin like an old version).
        public enum Foreign { Refuse, Skip, Replace }

        public static Placed Put(string addons, string zip, string[] before, string label)
        {
            return Put(addons, zip, before, label, Foreign.Refuse);
        }

        // A zip's addons into AddOns: unpacked beside it first; then each
        // folder it replaces, and each one the last version had and this one
        // has not, moved out; the new ones in; the old ones together to the
        // Recycle Bin. A folder in use puts everything back as it was. Only
        // the folders of before (put there by this app) are ever moved
        // without a yes; any other is as foreign says.
        public static Placed Put(string addons, string zip, string[] before, string label, Foreign foreign)
        {
            Directory.CreateDirectory(addons);
            string stage = Stage(addons), fresh = Path.Combine(stage, "new"), old = Path.Combine(stage, Plain(label + " (replaced)"));
            Placed placed = new Placed();
            List<string> movedOut = new List<string>(), movedIn = new List<string>();
            try
            {
                List<KeyValuePair<string, string>> folders;
                using (ZipArchive archive = ZipFile.OpenRead(zip))
                {
                    folders = FoldersIn(archive.Entries.Select(e => e.FullName));
                    if (folders.Count == 0) throw new Exception("its zip holds no addon (no folder with its own .toc)");
                    foreach (KeyValuePair<string, string> f in folders)
                        if (!SafeFolder(f.Value)) throw new Exception("its zip has a folder named " + f.Value + ", which is not taken");
                    foreach (KeyValuePair<string, string> f in folders)
                    {
                        string target = Path.GetFullPath(Path.Combine(fresh, f.Value));
                        Directory.CreateDirectory(target);
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            string name = entry.FullName.Replace('\\', '/');
                            if (entry.Name == "" || !name.StartsWith(f.Key, StringComparison.OrdinalIgnoreCase)) continue;
                            string path;
                            try { path = Path.GetFullPath(Path.Combine(target, name.Substring(f.Key.Length))); }
                            catch { placed.Skipped++; continue; }
                            if (!path.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase)
                                || Array.IndexOf(Refused, Path.GetExtension(entry.Name).ToLowerInvariant()) >= 0)
                            {
                                placed.Skipped++;
                                continue;
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            entry.ExtractToFile(path, true);
                        }
                    }
                }
                List<string> now = folders.ConvertAll(f => f.Value);
                List<string> theirs = now.FindAll(f => Directory.Exists(Path.Combine(addons, f))
                    && (before == null || !before.Any(b => string.Equals(b, f, StringComparison.OrdinalIgnoreCase))));
                if (theirs.Count > 0 && foreign == Foreign.Refuse) throw new InTheWay(theirs.ToArray());
                if (theirs.Count > 0 && foreign == Foreign.Skip)
                {
                    now.RemoveAll(f => theirs.Contains(f));
                    placed.Kept = theirs.ToArray();
                    if (now.Count == 0) throw new InTheWay(theirs.ToArray());
                }
                List<string> outgoing = new List<string>(now);
                if (before != null)
                    foreach (string b in before)
                        if (SafeFolder(b) && !outgoing.Exists(o => string.Equals(o, b, StringComparison.OrdinalIgnoreCase))) outgoing.Add(b);
                Directory.CreateDirectory(old);
                foreach (string f in outgoing)
                {
                    string there = Path.Combine(addons, f);
                    if (!Directory.Exists(there)) continue;
                    try { Directory.Move(there, Path.Combine(old, f)); }
                    catch (Exception why) { throw InUse(f, why); }
                    movedOut.Add(f);
                }
                foreach (string f in now)
                {
                    Directory.Move(Path.Combine(fresh, f), Path.Combine(addons, f));
                    movedIn.Add(f);
                }
                placed.Folders = now.ToArray();
            }
            catch
            {
                foreach (string f in movedIn) try { Directory.Move(Path.Combine(addons, f), Path.Combine(fresh, f)); } catch { }
                foreach (string f in movedOut) try { Directory.Move(Path.Combine(old, f), Path.Combine(addons, f)); } catch { }
                CleanStage(stage, old);
                throw;
            }
            if (movedOut.Count > 0 && !ToBin(old)) placed.Left = old;
            CleanStage(stage, old);
            return placed;
        }

        // Its folders out of AddOns, together to the Recycle Bin. Null when
        // done, else where they are.
        public static string Remove(string addons, string[] folders, string label)
        {
            string stage = Stage(addons), old = Path.Combine(stage, Plain(label + " (uninstalled)"));
            Directory.CreateDirectory(old);
            List<string> moved = new List<string>();
            try
            {
                foreach (string f in folders ?? new string[0])
                {
                    if (!SafeFolder(f)) continue;
                    string there = Path.Combine(addons, f);
                    if (!Directory.Exists(there)) continue;
                    try { Directory.Move(there, Path.Combine(old, f)); }
                    catch (Exception why) { throw InUse(f, why); }
                    moved.Add(f);
                }
            }
            catch
            {
                foreach (string f in moved) try { Directory.Move(Path.Combine(old, f), Path.Combine(addons, f)); } catch { }
                CleanStage(stage, old);
                throw;
            }
            string left = null;
            if (moved.Count > 0 && !ToBin(old)) left = old;
            CleanStage(stage, old);
            return left;
        }

        // The unpacked new files are ours to delete; what came out of AddOns
        // is never deleted here, only sent to the Recycle Bin.
        static void CleanStage(string stage, string old)
        {
            try { string fresh = Path.Combine(stage, "new"); if (Directory.Exists(fresh)) Directory.Delete(fresh, true); } catch { }
            try { if (Directory.Exists(old) && Directory.GetFileSystemEntries(old).Length == 0) Directory.Delete(old); } catch { }
            try { if (Directory.Exists(stage) && Directory.GetFileSystemEntries(stage).Length == 0) Directory.Delete(stage); } catch { }
        }

        // To the Recycle Bin by the shell's delete with undo, no questions
        // (Windows still warns before something too big for the bin would be
        // deleted outright). FUM_TEST_BIN: a folder standing in for the bin.
        public static bool ToBin(string path)
        {
            string bin = Core.Env("FUM_TEST_BIN");
            if (bin != "")
            {
                Directory.CreateDirectory(bin);
                Directory.Move(path, Path.Combine(bin, Guid.NewGuid().ToString("N").Substring(0, 6) + " " + Path.GetFileName(path)));
                return true;
            }
            const uint Delete = 3;
            const ushort Flags = 0x40 | 0x10 | 0x4 | 0x400 | 0x4000;
            string from = Path.GetFullPath(path) + "\0";
            int result;
            bool aborted;
            if (IntPtr.Size == 8)
            {
                Native.FileOp64 op = new Native.FileOp64 { wFunc = Delete, pFrom = from, fFlags = Flags };
                result = Native.FileOperation64(ref op);
                aborted = op.fAnyOperationsAborted != 0;
            }
            else
            {
                Native.FileOp32 op = new Native.FileOp32 { wFunc = Delete, pFrom = from, fFlags = Flags };
                result = Native.FileOperation32(ref op);
                aborted = op.fAnyOperationsAborted != 0;
            }
            return result == 0 && !aborted && !Directory.Exists(path);
        }

        // A folder's .toc as WoW Forever reads it: Folder_Camelot.toc first,
        // then Folder.toc, then any Folder_<game>.toc.
        public static Toc ReadToc(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return null;
            string folder = Path.GetFileName(folderPath.TrimEnd('\\'));
            string[] tries = { folder + "_Camelot.toc", folder + "-Camelot.toc", folder + ".toc" };
            string file = null;
            foreach (string each in tries)
                if (File.Exists(Path.Combine(folderPath, each))) { file = Path.Combine(folderPath, each); break; }
            if (file == null)
                foreach (string each in Directory.GetFiles(folderPath, "*.toc"))
                    if (TocOf(Path.GetFileNameWithoutExtension(each), folder)) { file = each; break; }
            if (file == null) return null;
            Toc toc = new Toc();
            try
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    Match m = Regex.Match(line, @"^##\s*(Title|Version|Interface)\s*:\s*(.*)$");
                    if (!m.Success) continue;
                    string value = m.Groups[2].Value.Trim();
                    if (m.Groups[1].Value == "Title" && toc.Title == null) toc.Title = Regex.Replace(value, @"\|c[0-9A-Fa-f]{8}|\|r", "").Trim();
                    if (m.Groups[1].Value == "Version" && toc.Version == null) toc.Version = value;
                    if (m.Groups[1].Value == "Interface" && toc.Interface == null) toc.Interface = value;
                }
            }
            catch { return null; }
            return toc;
        }

        // The Interface number for WoW Forever (1.6x: 16000 to 16999) in a
        // .toc's line, or null.
        public static string ForeverInterface(string line)
        {
            if (line == null) return null;
            foreach (Match m in Regex.Matches(line, @"\d+"))
            {
                long n;
                if (long.TryParse(m.Value, out n) && n >= 16000 && n < 17000) return m.Value;
            }
            return null;
        }
    }

    class Bar : Control
    {
        float value = -1;
        public Color Fill = Color.Teal;
        public Bar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
        public float Value { get { return value; } set { this.value = value; Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (value >= 0)
                using (SolidBrush brush = new SolidBrush(Fill))
                    e.Graphics.FillRectangle(brush, 0, 0, Width * Math.Min(1f, value), Height);
        }
    }

    // A tick drawn like the list's Auto update one: the flat CheckBox's own
    // check is barely seen on a dark window.
    class DarkTick : CheckBox
    {
        public Color Well, Rim, Mark, Words;
        public float K = 1;

        public DarkTick()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int size = (int)Math.Round(14 * K);
            Rectangle box = new Rectangle(0, (Height - size) / 2, size, size);
            using (SolidBrush well = new SolidBrush(Well)) e.Graphics.FillRectangle(well, box);
            using (Pen rim = new Pen(Checked ? Mark : Rim)) e.Graphics.DrawRectangle(rim, box);
            if (Checked)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen mark = new Pen(Mark, Math.Max(1.5f, 2f * K)))
                    e.Graphics.DrawLines(mark, new[] { new PointF(box.X + size * 0.22f, box.Y + size * 0.52f), new PointF(box.X + size * 0.42f, box.Y + size * 0.72f), new PointF(box.X + size * 0.8f, box.Y + size * 0.28f) });
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(size + (int)(7 * K), 0, Width, Height), Words,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }

    // The list: below its last row, Windows' theme draws the columns' lines
    // into the empty room a big window gives; that room is painted plain.
    class DarkList : ListView
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // WM_PAINT; WM_PRINT and WM_PRINTCLIENT (a picture of the window).
            if (m.Msg == 0x000F) using (Graphics g = CreateGraphics()) Blank(g);
            else if ((m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero) using (Graphics g = Graphics.FromHdc(m.WParam)) Blank(g);
        }

        void Blank(Graphics g)
        {
            int below = Items.Count == 0 ? 0 : GetItemRect(Items.Count - 1).Bottom;
            if (below < 0) below = 0;
            if (below >= ClientSize.Height) return;
            using (SolidBrush back = new SolidBrush(BackColor)) g.FillRectangle(back, 0, below, ClientSize.Width, ClientSize.Height - below);
        }
    }

    // A window that can come up without taking the focus (tests' pictures).
    class QuietForm : Form
    {
        public bool Quiet;
        protected override bool ShowWithoutActivation { get { return Quiet; } }
    }

    class MainForm : Form
    {
        static readonly Color CBack = Color.FromArgb(17, 19, 23);
        static readonly Color CPanel = Color.FromArgb(25, 28, 34);
        static readonly Color CRow = Color.FromArgb(29, 33, 40);
        static readonly Color CPick = Color.FromArgb(31, 54, 56);
        static readonly Color CLine = Color.FromArgb(50, 55, 64);
        static readonly Color CText = Color.FromArgb(228, 230, 234);
        static readonly Color CDim = Color.FromArgb(136, 142, 152);
        static readonly Color CAccent = Color.FromArgb(70, 200, 170);
        static readonly Color CGold = Color.FromArgb(236, 192, 92);
        static readonly Color CRed = Color.FromArgb(238, 104, 92);
        static readonly Color CGreen = Color.FromArgb(100, 212, 124);
        static readonly Color CDev = Color.FromArgb(178, 144, 255);

        static readonly int[] Widths = { 270, 92, 96, 96, 152, 84, 90, 34 };
        static readonly string[] Titles = { "Addon", "Source", "Installed", "Latest", "State", "Updated", "Auto update", "" };
        const int ColSource = 1, ColInstalled = 2, ColLatest = 3, ColState = 4, ColUpdated = 5, ColAuto = 6, ColCross = 7;
        // The columns whose title sorts the list when clicked, and the name each goes by in settings.ini ("sort").
        static readonly string[] SortKeys = { "name", "source", null, null, "state", "updated", "auto", null };
        static readonly Color CGitHub = Color.FromArgb(200, 204, 212), CCurse = Color.FromArgb(240, 140, 70);

        // Where an addon comes from, as its Source column says it and in that column's order:
        // the Forever pack's own server, GitHub, CurseForge.
        public static int SourceRank(TrackedAddon a)
        {
            return a.IsCurse ? 2 : a.Private ? 0 : 1;
        }

        public static string SourceWord(TrackedAddon a)
        {
            return a.IsCurse ? "CurseForge" : a.Private ? "Forever" : "GitHub";
        }

        // Under the line at the end of the list, whatever its order: not
        // installed by the Manager, no dev copy and no copy put in AddOns by hand.
        public static bool Below(TrackedAddon a)
        {
            return a.Tag == null && !a.Dev && a.Found == null;
        }

        // The State column's order: what asks for something first, what is fine after.
        public static int StateRank(TrackedAddon a)
        {
            if (Blocked(a) != null) return 7;
            if (a.Problem != null) return 0;
            if (a.Tag != null && a.Missing) return 1;
            if (a.Tag != null && a.UpdateWaits) return 2;
            if (a.Tag != null && a.Latest != null && a.Latest.Skipped != null) return 3;
            if (a.Tag == null && a.Dev) return 4;
            if (a.Tag == null && a.Found != null) return 5;
            return 6;
        }

        // The list in the order of a column ("name", "source", "state", "updated", "auto";
        // anything else: as kept), then the ones Below after all the others.
        // As first clicked: names A to Z, what asks for something first, the
        // last updated first, Auto update on first; flip turns it round. The
        // ones with no date stay last either way; equals go by name.
        public static List<TrackedAddon> Order(List<TrackedAddon> all, string by, bool flip)
        {
            Func<TrackedAddon, string> name = a => a.Name ?? "";
            StringComparer plain = StringComparer.OrdinalIgnoreCase;
            IEnumerable<TrackedAddon> sorted = all;
            if (by == "source") sorted = (flip ? all.OrderByDescending(a => SourceRank(a)) : all.OrderBy(a => SourceRank(a))).ThenBy(name, plain);
            else if (by == "name") sorted = flip ? all.OrderByDescending(name, plain) : all.OrderBy(name, plain);
            else if (by == "state") sorted = (flip ? all.OrderByDescending(a => StateRank(a)) : all.OrderBy(a => StateRank(a))).ThenBy(name, plain);
            else if (by == "auto") sorted = (flip ? all.OrderBy(a => a.Auto ? 1 : 0) : all.OrderBy(a => a.Auto ? 0 : 1)).ThenBy(name, plain);
            else if (by == "updated")
            {
                Func<TrackedAddon, string> when = a => a.Tag != null ? a.InstalledAt ?? "" : "";
                IOrderedEnumerable<TrackedAddon> dated = all.OrderBy(a => when(a) == "" ? 1 : 0);
                sorted = (flip ? dated.ThenBy(when, StringComparer.Ordinal) : dated.ThenByDescending(when, StringComparer.Ordinal)).ThenBy(name, plain);
            }
            List<TrackedAddon> kept = sorted.ToList();
            List<TrackedAddon> ordered = kept.FindAll(a => !Below(a));
            ordered.AddRange(kept.FindAll(a => Below(a)));
            return ordered;
        }

        // The Updated column: how long ago it was installed or updated, from
        // InstalledAt ("yyyy-MM-dd HH:mm"), by the days of the calendar.
        public static string Ago(string at, DateTime now)
        {
            DateTime when;
            if (at == null || !DateTime.TryParseExact(at, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out when)) return "-";
            int days = (int)(now.Date - when.Date).TotalDays;
            if (days <= 0) return "today";
            if (days == 1) return "yesterday";
            if (days < 60) return days + " d ago";
            if (days < 730) return days / 30 + " mo ago";
            return days / 365 + " y ago";
        }
        // How often it checks by itself: the addons' new versions, the pack's
        // list and the Manager's own new version, all in one round. Check for updates
        // and the tray's Check for updates go at once.
        public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(4);

        // A check is due once CheckEvery has gone by since the last one began.
        public static bool Due(DateTime last, DateTime now)
        {
            return now - last >= CheckEvery;
        }

        // The smallest inside of the window (at 96 dpi): the row of buttons whole, a few rows of the list.
        const int MinWidth = 900, MinHeight = 450;

        readonly bool startHidden, shotMode;
        bool placed;                        // put where settings.ini said: from now on its moves are kept
        Rectangle normalBounds;             // where it is when neither maximized nor minimized
        DateTime checkedAt = DateTime.MinValue;
        readonly float k;
        readonly Font bold, small;
        List<TrackedAddon> addons;
        DateTime packAt = DateTime.MinValue;
        string packProblem;
        string game;
        bool busy, quitting, toldTray, shotTaken, toldUpdate;
        string selfReady;                   // a new version of the Manager, downloaded and ready
        readonly List<TrackedAddon> waiting = new List<TrackedAddon>();
        DateTime closedSince = DateTime.MinValue;
        // The row's buttons: the × clicked once waits for a second click (4 s);
        // the ones under the mouse, drawn lit.
        TrackedAddon armed, hoverCross, hoverUpdate;
        string armedSaid;
        string sortBy;                      // "name", "state", "updated", "auto"; "": the order kept in addons.json
        bool sortFlip;
        bool hideBelow, hoverLine;          // the not installed folded away under their line; the mouse on that line
        int belowCount;
        string tipSaid;
        readonly ToolTip tips = new ToolTip();
        readonly System.Windows.Forms.Timer disarm = new System.Windows.Forms.Timer { Interval = 4000 };

        readonly ListView list = new DarkList();
        readonly Panel heads = new Panel(), detailBox = new Panel();
        readonly Label gameLine = new Label(), status = new Label(), details = new Label(), curseNote = new Label(), autoNote = new Label();
        readonly Button topUpdate = new Button();
        readonly LinkLabel repoLink = new LinkLabel(), changeLink = new LinkLabel(), openLink = new LinkLabel(), updateLink = new LinkLabel();
        readonly Button checkButton = new Button(), addButton = new Button(), curseButton = new Button(), removeButton = new Button(),
            installSel = new Button(), uninstallSel = new Button();
        readonly DarkTick startBox = new DarkTick(), trayBox = new DarkTick();
        readonly Bar bar = new Bar();
        readonly NotifyIcon tray = new NotifyIcon();
        ToolStripMenuItem restartItem;
        readonly System.Windows.Forms.Timer first = new System.Windows.Forms.Timer(), every = new System.Windows.Forms.Timer(), watch = new System.Windows.Forms.Timer();

        int S(float v) { return (int)Math.Round(v * k); }

        public MainForm(bool hidden)
        {
            startHidden = hidden;
            shotMode = Core.Env("FUM_SHOT") != "";
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) k = g.DpiX / 96f;
            Text = Core.Name;
            Font = new Font("Segoe UI", 9f);
            bold = new Font("Segoe UI Semibold", 9.5f);
            small = new Font("Segoe UI", 8.25f);
            BackColor = CBack;
            ForeColor = CText;
            // Sizable and maximizable: the list grows, the rest keeps its anchors.
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(S(780), S(542));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = AppIcon();

            Build();
            // Everything in one column (body): the whole window, or its middle when maximized.
            body.Bounds = ClientRectangle;
            body.BackColor = CBack;
            Control[] all = Controls.Cast<Control>().ToArray();
            Controls.Clear();
            body.Controls.AddRange(all);
            Controls.Add(body);
            // Laid out at 780 wide (Build's own numbers), then given the room of the Source and Updated columns: the anchors take it.
            ClientSize = new Size(S(MinWidth), ClientSize.Height);
            MinimumSize = SizeFromClientSize(new Size(S(MinWidth), S(MinHeight)));
            if (shotMode)
            {
                // Tests: off screen, never placed from settings.ini nor saved there.
                // FUM_SHOT_SIZE=<width>x<height>: the inside of the window at that size.
                // FUM_SHOT_MAXIMIZED=1: laid out as when maximized (it stays off screen).
                simMaximized = Core.Env("FUM_SHOT_MAXIMIZED") == "1";
                StartPosition = FormStartPosition.Manual;
                Match given = Regex.Match(Core.Env("FUM_SHOT_SIZE"), @"^(\d+)x(\d+)$");
                if (given.Success) ClientSize = new Size(Math.Max(S(MinWidth), int.Parse(given.Groups[1].Value)), Math.Max(S(MinHeight), int.Parse(given.Groups[2].Value)));
                Location = new Point(-S(3000) - Width, S(40));
                ShowInTaskbar = false;
            }
            else PlaceFromSettings();
            placed = true;
            PlaceBody();
            Pack.LoadHeld();
            addons = Store.Load();
            game = Game.Folder();
            ShowGame();
            sortBy = Core.Setting("sort", "");
            if (sortBy == "" || Array.IndexOf(SortKeys, sortBy) < 0) sortBy = "";
            sortFlip = Core.Setting("sortFlip", "0") == "1";
            hideBelow = Core.Setting("hideBelow", "0") == "1";
            Fill();
            SetUpTray();
            selfReady = SelfUpdate.Ready(SelfUpdate.ExePath());
            ShowSelf();
            startBox.Checked = Core.StartWithWindows();
            trayBox.Checked = Core.Setting("closeToTray", "1") == "1";
            Core.SetRunKey(startBox.Checked);
            startBox.CheckedChanged += delegate { Core.SetSetting("startWithWindows", startBox.Checked ? "1" : "0"); Core.SetRunKey(startBox.Checked); };
            trayBox.CheckedChanged += delegate { Core.SetSetting("closeToTray", trayBox.Checked ? "1" : "0"); };

            first.Interval = shotMode ? 300 : 1500;
            first.Tick += delegate { first.Stop(); CheckAll(false); };
            first.Start();
            // Looks once a minute whether CheckEvery has gone by (a sleep or a
            // busy moment does not push the next check a whole round away).
            every.Interval = 60 * 1000;
            every.Tick += delegate { if (!busy && Due(checkedAt, DateTime.Now)) CheckAll(false); };
            every.Start();
            watch.Interval = 5000;
            watch.Tick += delegate { WatchGame(); };
            Status(game == null ? "WoW Forever was not found: use Change... to pick its folder." : addons.Count == 0 ? "Reading the Forever pack's list..." : "Ready.");
        }

        // Where it was last time (settings.ini "window" and "maximized"), kept on a screen.
        void PlaceFromSettings()
        {
            Rectangle? saved = WindowPlace.Parse(Core.Setting("window", ""));
            if (saved != null)
            {
                Rectangle[] screens = Screen.AllScreens.Select(s => s.WorkingArea).ToArray();
                StartPosition = FormStartPosition.Manual;
                Bounds = WindowPlace.Fit(saved.Value, screens, MinimumSize);
            }
            normalBounds = Bounds;
            if (Core.Setting("maximized", "0") == "1") WindowState = FormWindowState.Maximized;
        }

        void SavePlace()
        {
            if (!placed || shotMode) return;
            if (WindowState == FormWindowState.Normal) normalBounds = Bounds;
            if (normalBounds.Width <= 0) return;
            string where = WindowPlace.Format(normalBounds), max = WindowState == FormWindowState.Maximized ? "1" : "0";
            // A minimized window keeps the word it had.
            if (WindowState == FormWindowState.Minimized) max = Core.Setting("maximized", "0");
            if (Core.Setting("window", "") == where && Core.Setting("maximized", "0") == max) return;
            try
            {
                Core.SetSetting("window", where);
                Core.SetSetting("maximized", max);
            }
            catch (Exception why) { Core.Log("settings.ini: " + why.Message); }
        }

        FormWindowState lastState = FormWindowState.Normal;
        readonly Panel body = new Panel();
        bool simMaximized;                  // tests: laid out as maximized, off screen

        // Where the column of everything goes: the whole inside of the window;
        // maximized, the middle 60% (20% of the width on each side), never
        // narrower than min (the margins give way first).
        public static Rectangle Column(Size client, bool maximized, int min)
        {
            if (!maximized) return new Rectangle(Point.Empty, client);
            int width = Math.Min(client.Width, Math.Max(min, (int)Math.Round(client.Width * 0.6)));
            return new Rectangle((client.Width - width) / 2, 0, width, client.Height);
        }

        void PlaceBody()
        {
            if (WindowState == FormWindowState.Minimized || ClientSize.Width <= 0) return;
            Rectangle column = Column(ClientSize, WindowState == FormWindowState.Maximized || simMaximized, S(MinWidth));
            if (body.Bounds != column) body.Bounds = column;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PlaceBody();
            if (WindowState == FormWindowState.Normal && placed) normalBounds = Bounds;
            if (WindowState != lastState)
            {
                lastState = WindowState;
                SavePlace();
            }
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (WindowState == FormWindowState.Normal && placed) normalBounds = Bounds;
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            SavePlace();
        }

        // The Addon column takes the room the window gives; the others keep theirs.
        void FitColumns()
        {
            if (list.Columns.Count != Widths.Length) return;
            PlaceTop();
            int others = 0;
            for (int i = 1; i < Widths.Length; i++) others += list.Columns[i].Width;
            // As at the start: the room of a scroll bar given up, so a long list never scrolls sideways.
            if (list.Width <= 0) return;
            int first = Math.Max(S(160), list.Width - SystemInformation.VerticalScrollBarWidth - others);
            if (list.Columns[0].Width != first) list.Columns[0].Width = first;
            heads.Invalidate();
            list.Invalidate();
        }

        // Started by Windows (--tray): the window waits in the tray.
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && !IsHandleCreated)
            {
                CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override bool ShowWithoutActivation { get { return shotMode; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1;
            try { Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.ShowMe) ShowMe();
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SavePlace();
            if (!quitting && e.CloseReason == CloseReason.UserClosing && trayBox.Checked)
            {
                e.Cancel = true;
                Hide();
                if (!toldTray)
                {
                    toldTray = true;
                    tray.ShowBalloonTip(4000, Core.Name, "Still here in the tray, keeping your addons up to date. Right-click for Exit.", ToolTipIcon.Info);
                }
                return;
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        void ShowMe()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        static Icon appIcon, trayIcon;

        // The icon built into the exe (build.bat: /win32icon for Explorer and
        // the taskbar, /resource "app.ico" for here), every size of it; drawn
        // here, the same look, by a build without it (the tests).
        static Icon AppIcon()
        {
            if (appIcon != null) return appIcon;
            try
            {
                using (Stream built = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    if (built != null) return appIcon = new Icon(built);
            }
            catch (Exception why) { Core.Log("icon: " + why.Message); }
            using (Bitmap picture = new Bitmap(64, 64))
            {
                using (Graphics g = Graphics.FromImage(picture))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (GraphicsPath shape = Rounded(new RectangleF(2, 2, 60, 60), 14))
                    using (LinearGradientBrush fill = new LinearGradientBrush(new PointF(0, 0), new PointF(64, 64), Color.FromArgb(236, 192, 92), Color.FromArgb(150, 96, 30)))
                        g.FillPath(fill, shape);
                    using (Font font = new Font("Segoe UI", 38, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (StringFormat centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("F", font, Brushes.White, new RectangleF(0, 2, 64, 62), centre);
                }
                IntPtr handle = picture.GetHicon();
                appIcon = (Icon)Icon.FromHandle(handle).Clone();
                Native.DestroyIcon(handle);
            }
            return appIcon;
        }

        // The tray's: the icon's own small size, not the big one shrunk.
        static Icon TrayIcon()
        {
            if (trayIcon != null) return trayIcon;
            try { trayIcon = new Icon(AppIcon(), SystemInformation.SmallIconSize); }
            catch (Exception) { trayIcon = AppIcon(); }
            return trayIcon;
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        void Build()
        {
            Label title = new Label { Text = Core.Name, Font = new Font("Segoe UI Semibold", 15f), ForeColor = CText, AutoSize = true, Location = new Point(S(17), S(10)) };
            Controls.Add(title);
            Label tagline = new Label { Text = "v" + Core.Version + "  ·  the Forever pack's WoW Forever addons, kept up to date", Font = small, ForeColor = CDim, AutoSize = true, Location = new Point(S(20), S(44)) };
            Controls.Add(tagline);
            // Credits, quiet, after the subtitle: the name goes to its GitHub page.
            LinkLabel credit = new LinkLabel { Text = "·  Made by morderous", Font = small, ForeColor = CDim, AutoSize = true };
            credit.Location = new Point(tagline.Left + tagline.PreferredWidth + S(2), tagline.Top);
            credit.LinkArea = new LinkArea(credit.Text.IndexOf("morderous"), "morderous".Length);
            credit.LinkColor = credit.ActiveLinkColor = credit.VisitedLinkColor = CAccent;
            credit.LinkBehavior = LinkBehavior.HoverUnderline;
            credit.LinkClicked += delegate { Process.Start(Core.AuthorUrl); };
            Controls.Add(credit);
            // Laid out at the first size (780 x 542); each anchor says what follows a bigger window.
            Style(checkButton, "Check for updates", 620, 16, 140);
            checkButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            checkButton.Click += delegate { CheckAll(true); };
            // Donations: Ko-fi's own coral, filled, beside Check for updates, so it shows.
            Button kofi = new Button();
            Style(kofi, "♥  Support me on Ko-fi", 442, 14, 166);
            kofi.Height = S(28);
            kofi.Font = new Font("Segoe UI Semibold", 9.5f);
            kofi.BackColor = Color.FromArgb(255, 94, 91);
            kofi.ForeColor = Color.White;
            kofi.FlatAppearance.BorderColor = Color.FromArgb(255, 94, 91);
            kofi.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 122, 119);
            kofi.FlatAppearance.MouseDownBackColor = Color.FromArgb(224, 72, 70);
            kofi.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            kofi.Click += delegate { Process.Start(Core.KofiUrl); };

            gameLine.Location = new Point(S(20), S(72));
            gameLine.Size = new Size(S(560), S(18));
            gameLine.AutoEllipsis = true;
            gameLine.ForeColor = CDim;
            gameLine.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(gameLine);
            autoNote.AutoEllipsis = true;
            autoNote.ForeColor = CDim;
            autoNote.Font = small;
            autoNote.TextAlign = ContentAlignment.MiddleRight;
            autoNote.Text = AutoWords();
            Controls.Add(autoNote);
            Style(topUpdate, "Update all", 0, 0, 112);
            topUpdate.Font = small;
            topUpdate.Click += delegate { Disarm(); UpdateOnly(); };
            Style(installSel, "Install selected", 0, 0, 150);
            installSel.Font = small;
            installSel.Click += delegate { Disarm(); InstallSelected(); };
            Style(uninstallSel, "Uninstall selected", 0, 0, 150);
            uninstallSel.Font = small;
            uninstallSel.Click += delegate { Disarm(); UninstallSelected(); };
            Link(changeLink, "Change...", 610, 72, delegate { PickGame(); });
            changeLink.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Link(openLink, "Open AddOns", 678, 72, delegate
            {
                string folder = AddOns();
                if (folder != null && Directory.Exists(folder)) Process.Start("explorer.exe", "\"" + folder + "\"");
            });
            openLink.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            heads.Location = new Point(S(20), S(130));
            heads.Size = new Size(S(740), S(26));
            heads.BackColor = CPanel;
            heads.Paint += PaintHeads;
            heads.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            heads.Resize += delegate { heads.Invalidate(); };
            heads.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) SortBy(HeadAt(e.X)); };
            heads.MouseMove += delegate(object sender, MouseEventArgs e) { int at = HeadAt(e.X); heads.Cursor = at >= 0 && SortKeys[at] != null ? Cursors.Hand : Cursors.Default; };
            Controls.Add(heads);

            list.Location = new Point(S(20), S(156));
            list.Size = new Size(S(740), S(186));
            list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            list.Resize += delegate { FitColumns(); };
            list.View = View.Details;
            list.HeaderStyle = ColumnHeaderStyle.None;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = CPanel;
            list.ForeColor = CText;
            list.OwnerDraw = true;
            list.SmallImageList = new ImageList { ImageSize = new Size(1, S(30)) };
            // The first column gives up the room of a scroll bar, so a long list never scrolls sideways.
            for (int i = 0; i < Widths.Length; i++) list.Columns.Add(Titles[i], S(Widths[i]) - (i == 0 ? SystemInformation.VerticalScrollBarWidth : 0));
            list.HandleCreated += delegate { try { Native.SetWindowTheme(list.Handle, "DarkMode_Explorer", null); } catch { } };
            list.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = false; };
            list.DrawItem += delegate(object sender, DrawListViewItemEventArgs e) { e.DrawDefault = false; };
            list.DrawSubItem += DrawCell;
            // The line between the two groups is a row of its own (no addon): never picked.
            list.SelectedIndexChanged += delegate
            {
                foreach (ListViewItem item in list.SelectedItems.Cast<ListViewItem>().ToList()) if (item.Tag == null) item.Selected = false;
                ShowPicked();
                Buttons();
            };
            list.MouseClick += ClickList;
            list.MouseMove += delegate(object sender, MouseEventArgs e) { Hover(e.Location); };
            list.MouseLeave += delegate { Hover(new Point(-1, -1)); };
            disarm.Tick += delegate { Disarm(); };
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(list, true, null);
            Controls.Add(list);

            detailBox.Location = new Point(S(20), S(350));
            detailBox.Size = new Size(S(740), S(78));
            detailBox.BackColor = CPanel;
            detailBox.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(detailBox);
            repoLink.Location = new Point(S(12), S(8));
            repoLink.AutoSize = true;
            repoLink.Font = bold;
            repoLink.LinkColor = repoLink.ActiveLinkColor = repoLink.VisitedLinkColor = CAccent;
            repoLink.DisabledLinkColor = CText;
            repoLink.LinkBehavior = LinkBehavior.HoverUnderline;
            repoLink.LinkClicked += delegate
            {
                TrackedAddon a = Picked();
                // The pack's own repository is private: its page would be a 404 for whoever has the exe.
                if (a == null || a.Private) return;
                Process.Start(a.PageUrl);
            };
            detailBox.Controls.Add(repoLink);
            details.Location = new Point(S(12), S(30));
            details.Size = new Size(S(716), S(46));
            details.ForeColor = CDim;
            details.Font = small;
            details.UseMnemonic = false;
            details.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            detailBox.Controls.Add(details);

            Style(addButton, "Add from GitHub...", 20, 438, 130);
            addButton.Click += delegate { AddFromGitHub(); };
            Style(curseButton, "Add from CurseForge...", 156, 438, 156);
            curseButton.Click += delegate { AddFromCurseForge(); };
            Style(removeButton, "Remove from list", 636, 438, 124);
            removeButton.Click += delegate { RemovePicked(); };
            foreach (Button b in new[] { addButton, curseButton })
                b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            removeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            bar.Location = new Point(S(20), S(478));
            bar.Size = new Size(S(740), Math.Max(2, S(3)));
            bar.BackColor = CBack;
            bar.Fill = CAccent;
            bar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(bar);
            status.Location = new Point(S(20), S(486));
            status.Size = new Size(S(740), S(20));
            status.AutoEllipsis = true;
            status.UseMnemonic = false;
            status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(status);

            Tick(startBox, "Start with Windows", 20, 512);
            Tick(trayBox, "Close to the tray", 190, 512);
            startBox.Anchor = trayBox.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            curseNote.Text = CurseForge.On ? "" : "CurseForge is off in this build";
            curseNote.Font = small;
            curseNote.ForeColor = CDim;
            curseNote.TextAlign = ContentAlignment.MiddleRight;
            curseNote.Location = new Point(S(400), S(514));
            curseNote.Size = new Size(S(360), S(18));
            curseNote.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Controls.Add(curseNote);
            Link(updateLink, "Restart to update", 600, 514, delegate { RestartToUpdate(); });
            updateLink.Font = bold;
            updateLink.Visible = false;
            updateLink.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        }

        void Style(Button b, string text, int x, int y, int w)
        {
            Style(b, text, x, y, w, this);
        }

        void Style(Button b, string text, int x, int y, int w, Control into)
        {
            b.Text = text;
            b.Location = new Point(S(x), S(y));
            b.Size = new Size(S(w), S(30));
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = CLine;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 46, 54);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(30, 64, 60);
            b.BackColor = CPanel;
            b.ForeColor = CText;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            // A flat button turned off draws its words in Windows' own dark grey,
            // unreadable on this background: drawn again over it, dim but legible.
            b.Paint += delegate(object sender, PaintEventArgs e)
            {
                if (b.Enabled) return;
                Rectangle all = b.ClientRectangle;
                using (SolidBrush back = new SolidBrush(b.BackColor)) e.Graphics.FillRectangle(back, all);
                using (Pen rim = new Pen(CLine)) e.Graphics.DrawRectangle(rim, 0, 0, all.Width - 1, all.Height - 1);
                TextRenderer.DrawText(e.Graphics, b.Text, b.Font, all, CDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            };
            into.Controls.Add(b);
        }

        void Link(LinkLabel link, string text, int x, int y, Action click)
        {
            link.Text = text;
            link.AutoSize = true;
            link.Location = new Point(S(x), S(y));
            link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = CAccent;
            link.DisabledLinkColor = CDim;
            link.LinkBehavior = LinkBehavior.HoverUnderline;
            link.LinkClicked += delegate { click(); };
            Controls.Add(link);
        }

        void Tick(DarkTick box, string text, int x, int y)
        {
            box.Text = text;
            box.AutoSize = false;
            box.K = k;
            box.Location = new Point(S(x), S(y));
            box.Size = new Size(S(21) + TextRenderer.MeasureText(text, Font).Width, S(22));
            box.Well = CBack;
            box.Rim = CLine;
            box.Mark = CAccent;
            box.Words = CDim;
            box.BackColor = CBack;
            box.Cursor = Cursors.Hand;
            Controls.Add(box);
        }

        void PaintHeads(object sender, PaintEventArgs e)
        {
            int x = 0;
            for (int i = 0; i < list.Columns.Count; i++)
            {
                bool sorted = sortBy != "" && SortKeys[i] == sortBy;
                TextRenderer.DrawText(e.Graphics, Titles[i], small, new Rectangle(x + S(10), 0, list.Columns[i].Width - S(12), heads.Height), sorted ? CAccent : CDim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                if (sorted)
                {
                    // An arrow after the title: up for A to Z and the like, down for the newest first and the orders turned round.
                    bool down = sortFlip != (sortBy == "updated");
                    float left = x + S(10) + TextRenderer.MeasureText(e.Graphics, Titles[i], small).Width + S(1), w = S(7), h = S(4), top = (heads.Height - h) / 2f;
                    PointF[] arrow = down ? new[] { new PointF(left, top), new PointF(left + w, top), new PointF(left + w / 2, top + h) }
                        : new[] { new PointF(left, top + h), new PointF(left + w, top + h), new PointF(left + w / 2, top) };
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush fill = new SolidBrush(CAccent)) e.Graphics.FillPolygon(fill, arrow);
                    e.Graphics.SmoothingMode = SmoothingMode.None;
                }
                x += list.Columns[i].Width;
            }
            using (Pen line = new Pen(CLine)) e.Graphics.DrawLine(line, 0, heads.Height - 1, heads.Width, heads.Height - 1);
        }

        // The column whose title is at x on the header; -1 past the last.
        int HeadAt(int x)
        {
            int left = 0;
            for (int i = 0; i < list.Columns.Count; i++)
            {
                left += list.Columns[i].Width;
                if (x < left) return i;
            }
            return -1;
        }

        // A click on a column's title: the list in its order; again, turned
        // round; a third time, back to the order kept (the pack's). Kept in settings.ini.
        void SortBy(int column)
        {
            if (column < 0 || SortKeys[column] == null) return;
            if (sortBy != SortKeys[column]) { sortBy = SortKeys[column]; sortFlip = false; }
            else if (!sortFlip) sortFlip = true;
            else { sortBy = ""; sortFlip = false; }
            try
            {
                Core.SetSetting("sort", sortBy);
                Core.SetSetting("sortFlip", sortFlip ? "1" : "0");
            }
            catch { }
            Disarm();
            Fill();
            heads.Invalidate();
        }

        // The line's own button: the not installed folded away, or shown again. Kept in settings.ini.
        void FoldBelow()
        {
            hideBelow = !hideBelow;
            try { Core.SetSetting("hideBelow", hideBelow ? "1" : "0"); } catch { }
            Disarm();
            Fill();
        }

        // What the line's button says: "Hide", or how many it folds away.
        string LineWords()
        {
            return hideBelow ? "Show " + belowCount + " not installed" : "Hide";
        }

        // What the Update all above the State column puts in: the updates that wait (none to install).
        List<TrackedAddon> Updatable()
        {
            return addons == null ? new List<TrackedAddon>() : addons.FindAll(a => a.UpdateWaits && !a.Missing && Blocked(a) == null);
        }

        // The row of buttons above the list: Install selected over the Addon
        // column, Update all over State (how often the Manager checks before
        // it), Uninstall selected at the end, over Auto update and the ×.
        void PlaceTop()
        {
            if (list.Columns.Count < Widths.Length) return;
            int row = heads.Top - S(32), h = S(26);
            int state = heads.Left, end = heads.Left;
            for (int i = 0; i < ColState; i++) state += list.Columns[i].Width;
            for (int i = 0; i < list.Columns.Count; i++) end += list.Columns[i].Width;
            installSel.SetBounds(heads.Left, row, Fit(installSel), h);
            topUpdate.SetBounds(state + S(4), row, Math.Max(S(112), Fit(topUpdate)), h);
            int wide = Fit(uninstallSel);
            uninstallSel.SetBounds(Math.Max(topUpdate.Right + S(10), end - wide), row, wide, h);
            int noteLeft = installSel.Right + S(14);
            autoNote.SetBounds(noteLeft, row, Math.Max(0, topUpdate.Left - S(14) - noteLeft), h);
        }

        int Fit(Button b)
        {
            return TextRenderer.MeasureText(b.Text, b.Font).Width + S(28);
        }

        // A button's colour: its words and rim; dim when there is nothing for it.
        void Tone(Button b, Color colour)
        {
            b.ForeColor = colour;
            b.FlatAppearance.BorderColor = colour == CDim ? CLine : Color.FromArgb(150, colour);
        }

        // How often the Manager checks, and when next (CheckEvery after the last check began).
        string AutoWords()
        {
            string every = CheckEvery.TotalHours >= 1 ? CheckEvery.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " h" : CheckEvery.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min";
            return "Auto update: checks every " + every + (checkedAt == DateTime.MinValue ? "" : ", next at " + (checkedAt + CheckEvery).ToString("HH:mm"));
        }

        // The updates that wait, all of them: the button above the State column.
        void UpdateOnly()
        {
            List<TrackedAddon> due = Updatable();
            if (busy || game == null || due.Count == 0) return;
            if (!GameOpenOk("Update")) return;
            RunQueue(due, false);
        }

        // A cell of a row, from the columns' widths (column 0's own bounds span the whole row).
        Rectangle CellOf(ListViewItem item, int column)
        {
            int x = 0;
            for (int i = 0; i < column; i++) x += list.Columns[i].Width;
            Rectangle row = item.Bounds;
            return new Rectangle(row.X + x, row.Y, list.Columns[column].Width, row.Height);
        }

        Size Measure(string words)
        {
            return TextRenderer.MeasureText(words, small, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        // An update waits and nothing else is said on the row: its State is an Update button.
        bool RowUpdates(TrackedAddon a)
        {
            return a.Busy == null && Blocked(a) == null && a.Problem == null && a.Tag != null && !a.Missing && a.UpdateWaits && !waiting.Contains(a) && armed != a;
        }

        // Installed: an × at the end of its row uninstalls it (a second click within 4 s).
        bool RowCrosses(TrackedAddon a)
        {
            return a.Tag != null && a.Busy == null && game != null;
        }

        // Folders gone from AddOns: its State is a Reinstall button.
        bool RowReinstalls(TrackedAddon a)
        {
            return a.Busy == null && Blocked(a) == null && a.Problem == null && a.Tag != null && a.Missing && a.Latest != null && armed != a;
        }

        // The row's button in its State: "Update", "Reinstall", or none.
        string RowAction(TrackedAddon a)
        {
            return RowUpdates(a) ? "Update" : RowReinstalls(a) ? "Reinstall" : null;
        }

        Rectangle RowChip(Rectangle cell, string words)
        {
            Size size = Measure(words);
            int h = size.Height + S(6);
            return new Rectangle(cell.X + S(8), cell.Y + (cell.Height - h) / 2, size.Width + S(22), h);
        }

        Rectangle CrossBox(Rectangle cell)
        {
            int size = S(20);
            return new Rectangle(cell.X + (cell.Width - size) / 2, cell.Y + (cell.Height - size) / 2, size, size);
        }

        // A button drawn on the list or its header: brighter under the mouse, dim when it cannot be used now.
        void DrawButton(Graphics g, string words, Rectangle box, Color colour, bool on, bool lit)
        {
            Color tone = on ? colour : CDim;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath shape = Rounded(new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1), S(4)))
            {
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(on && lit ? 84 : 44, tone))) g.FillPath(fill, shape);
                using (Pen rim = new Pen(Color.FromArgb(on ? 220 : 120, tone))) g.DrawPath(rim, shape);
            }
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, words, small, box, tone, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        // The ×: dim; lit under the mouse; red and filled once clicked (it waits for the second click).
        void DrawCross(Graphics g, Rectangle box, bool lit, bool primed)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color tone = primed ? CRed : lit ? CText : CDim;
            if (primed || lit)
                using (GraphicsPath shape = Rounded(new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1), S(4)))
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(primed ? 90 : 40, tone)))
                    g.FillPath(fill, shape);
            float pad = box.Width * 0.32f;
            using (Pen mark = new Pen(tone, Math.Max(1.5f, 1.6f * k)))
            {
                g.DrawLine(mark, box.X + pad, box.Y + pad, box.Right - pad, box.Bottom - pad);
                g.DrawLine(mark, box.Right - pad, box.Y + pad, box.X + pad, box.Bottom - pad);
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        void Hover(Point p)
        {
            ListViewItem item = p.X < 0 ? null : list.GetItemAt(p.X, p.Y);
            TrackedAddon a = item == null ? null : (TrackedAddon)item.Tag;
            TrackedAddon cross = a != null && RowCrosses(a) && CrossBox(CellOf(item, ColCross)).Contains(p) ? a : null;
            string act = a == null ? null : RowAction(a);
            TrackedAddon update = act != null && RowChip(CellOf(item, ColState), act).Contains(p) ? a : null;
            bool line = item != null && a == null;
            list.Cursor = ((cross != null || update != null) && !busy) || line ? Cursors.Hand : Cursors.Default;
            // Said at the mouse: the day and hour behind "5 d ago"; what a click on the line does.
            string tip = line ? (hideBelow ? "Click to show the addons that are not installed" : "Click to hide the addons that are not installed")
                : a != null && a.Tag != null && a.InstalledAt != null && CellOf(item, ColUpdated).Contains(p) ? "Installed or updated on " + a.InstalledAt : null;
            if (tip != tipSaid)
            {
                tipSaid = tip;
                tips.SetToolTip(list, tip ?? "");
            }
            if (cross != hoverCross || update != hoverUpdate || line != hoverLine)
            {
                hoverCross = cross;
                hoverUpdate = update;
                hoverLine = line;
                list.Invalidate();
            }
        }

        // The first click on a row's ×: it turns red and waits 4 s for the second.
        void Arm(TrackedAddon a)
        {
            armed = a;
            disarm.Stop();
            disarm.Start();
            armedSaid = "Click the × again to uninstall " + a.Name + ": its folders go to the Recycle Bin.";
            Status(armedSaid, CGold);
            list.Invalidate();
        }

        void Disarm()
        {
            disarm.Stop();
            if (armed == null) return;
            armed = null;
            if (status.Text == armedSaid) Status("");
            list.Invalidate();
        }

        void DrawCell(object sender, DrawListViewSubItemEventArgs e)
        {
            TrackedAddon a = (TrackedAddon)e.Item.Tag;
            int x = 0;
            for (int i = 0; i < e.ColumnIndex; i++) x += list.Columns[i].Width;
            // Column 0's bounds span the whole row: each cell from the columns' widths.
            Rectangle cell = new Rectangle(e.Item.Bounds.X + x, e.Bounds.Y, list.Columns[e.ColumnIndex].Width, e.Bounds.Height);
            if (a == null)
            {
                // The line between the installed and the not installed, across the row.
                using (SolidBrush back = new SolidBrush(CPanel)) e.Graphics.FillRectangle(back, cell);
                int thick = Math.Max(2, S(2)), from = cell.X;
                if (e.ColumnIndex == 0)
                {
                    // Its button at the start: "Hide", or "Show 7 not installed" once folded away.
                    Rectangle chip = RowChip(cell, LineWords());
                    DrawButton(e.Graphics, LineWords(), chip, hideBelow ? CText : CDim, true, hoverLine);
                    from = chip.Right + S(8);
                }
                if (from < cell.Right)
                    using (SolidBrush line = new SolidBrush(CLine)) e.Graphics.FillRectangle(line, from, cell.Y + (cell.Height - thick) / 2, cell.Right - from, thick);
                return;
            }
            using (SolidBrush back = new SolidBrush(e.Item.Selected ? CPick : (e.ItemIndex % 2 == 0 ? CPanel : CRow)))
                e.Graphics.FillRectangle(back, cell);
            Rectangle text = new Rectangle(cell.X + S(10), cell.Y, cell.Width - S(14), cell.Height);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            switch (e.ColumnIndex)
            {
                case 0:
                    {
                        // Ticked: Install selected puts it in (installed: Uninstall selected takes it out). A required one not installed stays ticked.
                        int size = S(14);
                        Rectangle box = new Rectangle(text.X, cell.Y + (cell.Height - size) / 2, size, size);
                        bool on = Ticked(a), locked = a.Tag == null && !a.Dev && a.Required;
                        Color tone = on ? (locked ? CDim : CAccent) : CLine;
                        using (SolidBrush well = new SolidBrush(CBack)) e.Graphics.FillRectangle(well, box);
                        using (Pen rim = new Pen(tone)) e.Graphics.DrawRectangle(rim, box);
                        if (on) Check(e.Graphics, box, tone);
                        text = new Rectangle(box.Right + S(8), text.Y, Math.Max(0, text.Right - box.Right - S(8)), text.Height);
                        Size name = TextRenderer.MeasureText(e.Graphics, a.Name, bold, text.Size, flags);
                        TextRenderer.DrawText(e.Graphics, a.Name, bold, text, CText, flags);
                        int after = name.Width + S(2);
                        // A dev copy in AddOns (none of its releases): a tag after the name.
                        if (a.Dev && a.Tag == null)
                        {
                            Rectangle tag = TagBox(e.Graphics, "Dev", text.X + after + S(4), cell);
                            if (tag.Right <= text.Right)
                            {
                                DrawTag(e.Graphics, "Dev", tag, CDev);
                                after = tag.Right - text.X + S(2);
                            }
                        }
                        Rectangle by = new Rectangle(text.X + after, text.Y, Math.Max(0, text.Width - after), text.Height);
                        // Its author alone: where it comes from has a column of its own.
                        string who = a.By != "" && !a.Private ? "by " + a.By : "";
                        if (who != "" && by.Width > S(30)) TextRenderer.DrawText(e.Graphics, who, small, by, CDim, flags);
                        break;
                    }
                case ColSource:
                    {
                        string from = SourceWord(a);
                        DrawTag(e.Graphics, from, TagBox(e.Graphics, from, text.X, cell), a.IsCurse ? CCurse : a.Private ? CGold : CGitHub);
                        break;
                    }
                case ColInstalled:
                    TextRenderer.DrawText(e.Graphics, a.Tag != null ? VersionText(a, a.Tag) : a.Dev ? (a.Found != null && a.Found != "?" ? a.Found + " (dev)" : "dev copy") : (a.Found != null ? a.Found + " (by hand)" : "-"), Font, text, a.Tag != null ? CText : a.Dev ? CDev : CDim, flags);
                    break;
                case ColLatest:
                    TextRenderer.DrawText(e.Graphics, a.Known != null ? VersionText(a, a.Known) : "-", Font, text, CText, flags);
                    break;
                case ColState:
                    {
                        if (armed == a && a.Tag != null)
                        {
                            TextRenderer.DrawText(e.Graphics, "Uninstall? Click ×", Font, text, CRed, flags);
                            break;
                        }
                        string act = RowAction(a);
                        if (act != null)
                        {
                            DrawButton(e.Graphics, act, RowChip(cell, act), act == "Update" ? CGold : CRed, !busy, hoverUpdate == a);
                            break;
                        }
                        Color colour;
                        string said = StateOf(a, out colour);
                        TextRenderer.DrawText(e.Graphics, said, Font, text, colour, flags);
                        break;
                    }
                case ColUpdated:
                    {
                        // Just changed (today, yesterday): bright; the rest dim.
                        string ago = a.Tag != null ? Ago(a.InstalledAt, DateTime.Now) : "-";
                        TextRenderer.DrawText(e.Graphics, ago, Font, text, ago == "today" ? CAccent : ago == "yesterday" ? CText : CDim, flags);
                        break;
                    }
                case ColAuto:
                    {
                        int size = S(14);
                        Rectangle box = new Rectangle(text.X, cell.Y + (cell.Height - size) / 2, size, size);
                        using (SolidBrush well = new SolidBrush(CBack)) e.Graphics.FillRectangle(well, box);
                        using (Pen rim = new Pen(a.Auto ? CAccent : CLine)) e.Graphics.DrawRectangle(rim, box);
                        if (a.Auto) Check(e.Graphics, box, CAccent);
                        TextRenderer.DrawText(e.Graphics, a.Auto ? "On" : "Off", small, new Rectangle(box.Right + S(6), cell.Y, cell.Width, cell.Height), a.Auto ? CText : CDim, flags);
                        break;
                    }
                case ColCross:
                    if (RowCrosses(a)) DrawCross(e.Graphics, CrossBox(cell), hoverCross == a && !busy, armed == a);
                    break;
            }
        }

        // A small tag on a row ("Dev", its Source): its box, centred on the row's height.
        Rectangle TagBox(Graphics g, string words, int x, Rectangle cell)
        {
            Size size = TextRenderer.MeasureText(g, words, small, new Size(int.MaxValue, cell.Height), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int h = size.Height + S(4);
            return new Rectangle(x, cell.Y + (cell.Height - h) / 2, size.Width + S(12), h);
        }

        void DrawTag(Graphics g, string words, Rectangle box, Color colour)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath shape = Rounded(new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1), Math.Max(2f, (box.Height - 1) / 2f)))
            {
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(36, colour))) g.FillPath(fill, shape);
                using (Pen rim = new Pen(Color.FromArgb(150, colour))) g.DrawPath(rim, shape);
            }
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, words, small, box, colour, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        void Check(Graphics g, Rectangle box, Color colour)
        {
            float size = box.Width;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen mark = new Pen(colour, Math.Max(1.5f, 2f * k)))
                g.DrawLines(mark, new[] { new PointF(box.X + size * 0.22f, box.Y + size * 0.52f), new PointF(box.X + size * 0.42f, box.Y + size * 0.72f), new PointF(box.X + size * 0.8f, box.Y + size * 0.28f) });
            g.SmoothingMode = SmoothingMode.None;
        }

        // The box: not installed, Install selected puts it in (kept, as the pack
        // says at first); installed or a dev copy, picked for this run only
        // (Uninstall selected, Install selected), unticked to begin with and after.
        static bool Ticked(TrackedAddon a) { return a.Tag != null || a.Dev ? a.Marked : a.Want || a.Required; }

        // A series' tag in a column: its version alone ("quests-audio/v2.5.0": "v2.5.0").
        static string Short(string tag) { return tag.Substring(tag.LastIndexOf('/') + 1); }

        // A version in a column: a CurseForge file's name ("QuestCompletist 3.8") by its version alone ("3.8").
        static string VersionText(TrackedAddon a, string tag)
        {
            if (!a.IsCurse) return Short(tag);
            MatchCollection all = Regex.Matches(tag, @"[vV]?\d+(?:\.\d+)+[0-9A-Za-z.\-]*");
            return all.Count > 0 ? all[all.Count - 1].Value.TrimEnd('.', '-') : tag;
        }

        // Ticked and not in AddOns yet: what Install selected puts in.
        List<TrackedAddon> ToInstall()
        {
            return addons.FindAll(a => a.Tag == null && Ticked(a) && Blocked(a) == null);
        }

        // Why it cannot go in now; null when it can.
        static string Blocked(TrackedAddon a)
        {
            return a.IsCurse && !CurseForge.On ? CurseForge.Off : null;
        }

        string StateOf(TrackedAddon a, out Color colour)
        {
            if (a.Busy != null) { colour = CAccent; return a.Busy; }
            if (Blocked(a) != null) { colour = CDim; return "CurseForge off"; }
            if (a.Problem != null) { colour = CRed; return "Check failed"; }
            if (a.Tag == null && a.Dev) { colour = CDev; return "Dev build in AddOns"; }
            if (a.Tag == null)
            {
                colour = a.Found != null ? CGold : CDim;
                // Its download's size, before it goes in (a voice pack is 1.4 GB).
                return a.Found != null ? "In AddOns by hand" : "Not installed" + (a.Latest != null && a.Latest.Size > 0 ? ", " + Bytes(a.Latest.Size) : "");
            }
            if (a.Missing) { colour = CRed; return "Folders missing"; }
            if (a.UpdateWaits)
            {
                colour = CGold;
                return waiting.Contains(a) ? "Waits for the game" : "Update available";
            }
            // Up to date with the newest that can be installed; a newer one has no zip.
            if (a.Latest != null && a.Latest.Skipped != null)
            {
                colour = CGold;
                return VersionText(a, a.Latest.Skipped) + " " + a.Latest.SkippedShort;
            }
            colour = CGreen;
            return a.Known == null ? "Installed" : "Up to date";
        }

        void ClickList(object sender, MouseEventArgs e)
        {
            ListViewHitTestInfo hit = list.HitTest(e.Location);
            TrackedAddon row = hit.Item == null ? null : (TrackedAddon)hit.Item.Tag;
            bool cross = row != null && e.Button == MouseButtons.Left && RowCrosses(row) && CrossBox(CellOf(hit.Item, ColCross)).Contains(e.Location);
            // Any other click lets go of a waiting ×.
            if (!(cross && armed == row)) Disarm();
            if (cross)
            {
                if (busy) return;
                if (armed == row) { Disarm(); Uninstall(row, true, null); }
                else Arm(row);
                return;
            }
            string act = row == null ? null : RowAction(row);
            if (act != null && e.Button == MouseButtons.Left && RowChip(CellOf(hit.Item, ColState), act).Contains(e.Location))
            {
                if (busy || game == null) return;
                if (Trust(row) && GameOpenOk(act)) Install(row, false, null);
                return;
            }
            if (hit.Item != null && row == null)
            {
                if (e.Button == MouseButtons.Left) FoldBelow();
                return;
            }
            if (hit.Item == null || hit.SubItem == null) return;
            int column = hit.Item.SubItems.IndexOf(hit.SubItem);
            if (column == 0 && e.X - hit.Item.Bounds.X < S(32))
            {
                Toggle((TrackedAddon)hit.Item.Tag);
                return;
            }
            if (column != ColAuto) return;
            TrackedAddon a = (TrackedAddon)hit.Item.Tag;
            a.Auto = !a.Auto;
            if (addons.Contains(a)) Store.Save(addons);
            list.Invalidate();
            Status(a.Name + (a.Auto ? ": updates by itself when a new version comes out." : ": updates only when you click Update."));
        }

        void Toggle(TrackedAddon a)
        {
            Disarm();
            if (a.Tag != null || a.Dev)
            {
                a.Marked = !a.Marked;
                list.Invalidate();
                Buttons();
                Status(!a.Marked ? a.Name + " is not selected." : a.Tag != null ? a.Name + " is selected: Uninstall selected takes it out of AddOns."
                    : a.Name + " is selected: Install selected puts it in, after asking before it replaces the copy in AddOns.");
                return;
            }
            if (a.Required) { Status(a.Name + " is part of the Forever pack: it cannot be left out."); return; }
            a.Want = !a.Want;
            Store.Replace(addons);
            Store.Save(addons);
            list.Invalidate();
            Buttons();
            long size = a.Latest != null ? a.Latest.Size : -1;
            Status(a.Want ? a.Name + " is selected: Install selected puts it in" + (size > 0 ? " (" + Bytes(size) + ")." : ".") : a.Name + " is not selected: Install selected skips it.");
        }

        string AddOns() { return Game.AddOns(game); }

        TrackedAddon Picked()
        {
            return list.SelectedItems.Count > 0 ? (TrackedAddon)list.SelectedItems[0].Tag : null;
        }

        void Pick(TrackedAddon a)
        {
            foreach (ListViewItem item in list.Items) item.Selected = a != null && item.Tag == a;
            ListViewItem picked = a == null ? null : list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag == a);
            if (picked != null) picked.EnsureVisible();
        }

        void ShowGame()
        {
            CurseForge.GameVersion = Game.CurseVersion(game);
            if (game == null)
            {
                gameLine.Text = "WoW Forever not found. Use Change... to pick its folder (_forever_, with Wow.exe).";
                gameLine.ForeColor = CRed;
                return;
            }
            string version = Game.Version(game);
            gameLine.Text = "WoW Forever" + (version != "" ? " " + version : "") + "  ·  " + AddOns();
            gameLine.ForeColor = CDim;
        }

        // What AddOns holds of each addon: its folders still there, its
        // .toc's title and Interface, a copy put there by hand.
        void Probe(TrackedAddon a)
        {
            a.Found = null;
            a.Missing = false;
            a.Interface = null;
            string folder = AddOns();
            if (folder == null) return;
            string main = a.RepoName;
            if (a.Folders != null && a.Folders.Length > 0)
            {
                main = a.Folders.FirstOrDefault(f => string.Equals(f, a.RepoName, StringComparison.OrdinalIgnoreCase))
                    ?? a.Folders.OrderBy(f => f.Length).First();
                foreach (string f in a.Folders)
                    if (!Directory.Exists(Path.Combine(folder, f))) a.Missing = true;
            }
            if (main == "" || !Installer.SafeFolder(main)) return;
            Toc toc = Installer.ReadToc(Path.Combine(folder, main));
            if (toc == null) return;
            if (a.Tag == null) a.Found = toc.Version ?? "?";
            // A pack's addon keeps the pack's name ("Spoken Bosses Audio", not its .toc's "Spoken Audio: Bosses").
            if (a.Source != "pack" && !a.IsCurse && !string.IsNullOrEmpty(toc.Title)) a.Title = toc.Title;
            a.Interface = toc.Interface;
        }

        void Fill()
        {
            if (addons != null && Store.Replace(addons)) Store.Save(addons);
            TrackedAddon keep = Picked();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (TrackedAddon a in addons) Probe(a);
            // In the order of the column picked; the not installed after a line of their own.
            bool lined = false;
            belowCount = addons.Count(a => Below(a));
            foreach (TrackedAddon a in Order(addons, sortBy, sortFlip))
            {
                if (Below(a) && !lined && list.Items.Count > 0)
                {
                    list.Items.Add(new ListViewItem(new[] { "", "", "", "", "", "", "", "" }));
                    lined = true;
                }
                // Folded away under their line (never when there is no line to bring them back).
                if (Below(a) && lined && hideBelow) continue;
                ListViewItem item = new ListViewItem(new[] { a.Repo, "", "", "", "", "", "", "" });
                item.Tag = a;
                list.Items.Add(item);
            }
            list.EndUpdate();
            ListViewItem again = keep == null ? null : list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag == keep);
            if (again == null && list.Items.Count > 0) again = list.Items[0];
            if (again != null) again.Selected = true;
            ShowPicked();
            Buttons();
        }

        static string Day(string iso)
        {
            DateTime when;
            return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when)
                ? when.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : iso;
        }

        static string Bytes(long bytes) { return Core.Bytes(bytes); }

        void ShowPicked()
        {
            TrackedAddon a = Picked();
            if (a == null)
            {
                repoLink.Text = "";
                details.Text = packProblem != null ? "The Forever pack's list did not come: " + packProblem + "." : "The Forever pack's list is not here yet: Check for updates reads it.";
                return;
            }
            List<string> lines = new List<string>();
            if (a.IsCurse) repoLink.Text = "curseforge.com/wow/addons/" + (a.Slug ?? a.CurseId.ToString(CultureInfo.InvariantCulture));
            else if (a.Private) repoLink.Text = "Forever pack  ·  " + (a.ZipPattern ?? a.Repo);
            else repoLink.Text = "github.com/" + a.Repo;
            repoLink.Enabled = !a.Private;
            string why = Blocked(a);
            if (why != null) lines.Add(why + ": its project stays on the list, not checked.");
            if (a.Problem != null) lines.Add("Last check failed: " + a.Problem + ".");
            Release r = a.Latest;
            if (r != null)
                lines.Add("Latest " + r.Tag + (r.Test ? (a.IsCurse ? " (a beta or alpha file)" : " (test version)") : "") + (r.Published != null ? ", out " + Day(r.Published) : "") + "  ·  " + r.ZipName
                    + (r.Size > 0 ? " (" + Bytes(r.Size) + ")" : "") + "  ·  " + r.PickedBy);
            if (r != null && r.Skipped != null)
                lines.Add("Left out: " + r.SkippedWhy + ", most likely a slip of its author's. " + VersionText(a, r.Tag) + " is the newest that can be installed; each check looks again.");
            if (a.Tag != null)
            {
                string iface = Installer.ForeverInterface(a.Interface);
                lines.Add("Installed " + a.Tag + (a.InstalledAt != null ? " on " + a.InstalledAt : "") + "  ·  " + a.Folders.Length + " folder(s) in AddOns"
                    + (a.Missing ? ", some gone (Reinstall puts them back)" : "")
                    + (a.Interface == null ? "" : iface != null ? "  ·  its .toc is for WoW Forever (" + iface + ")" : "  ·  its .toc lists no WoW Forever Interface"));
            }
            else if (a.Dev)
                lines.Add("Dev build: " + string.Join(", ", a.DevFolders ?? new string[0]) + " in AddOns match" + ((a.DevFolders ?? new string[0]).Length == 1 ? "es" : "")
                    + " none of its releases (the newest" + (a.DevCompared > 1 ? " and " + (a.DevCompared - 1) + " older" : "") + ", file by file). Left alone unless you tick it: then Install selected asks before it replaces it.");
            else if (a.Found != null) lines.Add("In AddOns (version " + a.Found + "), put there by hand or by another app. Install selected asks before it replaces that copy.");
            else lines.Add(Ticked(a) ? "Not installed yet: ticked, so Install selected puts it in." : "Not installed: not ticked, so Install selected leaves it out (tick its box to have it).");
            if (lines.Count > 0)
                lines[lines.Count - 1] += a.Source != "pack" ? "  ·  Added by you." : a.ReplacedBy != null ? "  ·  Optional: " + a.ReplacedBy + " takes its place."
                    : a.Required ? "  ·  Part of the Forever pack." : a.Recommended ? "  ·  An extra of the Forever pack, recommended." : "  ·  An extra of the Forever pack.";
            details.Text = string.Join("\n", lines.ToArray());
        }

        void Buttons()
        {
            TrackedAddon a = Picked();
            bool can = !busy && game != null;
            // Off the list: anything but a required one.
            removeButton.Enabled = !busy && a != null && addons.Contains(a) && !a.Required;
            int fresh = ToInstall().Count, gone = ToUninstall().Count;
            installSel.Text = fresh > 0 ? "Install selected (" + fresh + ")" : "Install selected";
            installSel.Enabled = can && fresh > 0;
            Tone(installSel, fresh > 0 ? CAccent : CDim);
            uninstallSel.Text = gone > 0 ? "Uninstall selected (" + gone + ")" : "Uninstall selected";
            uninstallSel.Enabled = can && gone > 0;
            Tone(uninstallSel, gone > 0 ? CRed : CDim);
            checkButton.Enabled = !busy;
            addButton.Enabled = !busy;
            curseButton.Enabled = !busy && CurseForge.On;
            changeLink.Enabled = !busy;
            updateLink.Enabled = !busy;
            int due = Updatable().Count;
            topUpdate.Text = due > 0 ? "Update all (" + due + ")" : "Update all";
            topUpdate.Enabled = !busy && game != null && due > 0;
            Tone(topUpdate, due > 0 ? CGold : CDim);
            autoNote.Text = AutoWords();
            PlaceTop();
            heads.Invalidate();
            list.Invalidate();
        }

        void Status(string text) { Status(text, CDim); }

        void Status(string text, Color colour)
        {
            status.Text = text;
            status.ForeColor = colour;
        }

        // One job at a time, off the window's thread.
        void Work(string what, Func<object> job, Action<object> done, Action<Exception> failed)
        {
            busy = true;
            Buttons();
            Status(what, CText);
            ThreadPool.QueueUserWorkItem(delegate
            {
                object result = null;
                Exception error = null;
                try { result = job(); }
                catch (Exception why) { error = why; }
                BeginInvoke((Action)delegate
                {
                    busy = false;
                    bar.Value = -1;
                    try
                    {
                        if (error == null) done(result);
                        else if (failed != null) failed(error);
                        else { Status(error.Message, CRed); Core.Log(error.ToString()); }
                    }
                    finally
                    {
                        Buttons();
                        list.Invalidate();
                    }
                });
            });
        }

        void Say(TrackedAddon a, string text, float progress)
        {
            BeginInvoke((Action)delegate
            {
                a.Busy = text;
                bar.Value = progress;
                list.Invalidate();
            });
        }

        // The Forever pack's list (at the first check, every 4 hours, or when
        // asked), every addon's newest version, and the Manager's own.
        void CheckAll(bool manual)
        {
            if (busy) return;
            DateTime started = DateTime.Now;
            checkedAt = started;
            List<TrackedAddon> repos = addons.FindAll(a => Blocked(a) == null);
            foreach (TrackedAddon a in addons) if (Blocked(a) != null) { a.Problem = null; a.Latest = null; }
            Dictionary<TrackedAddon, object> answers = new Dictionary<TrackedAddon, object>();
            Dictionary<TrackedAddon, Dictionary<string, object>> described = new Dictionary<TrackedAddon, Dictionary<string, object>>();
            bool wantPack = manual || Due(packAt, started) || Pack.Addons == null;
            List<string> onList = addons.Select(a => a.Key).ToList(), offered = Store.OfferedKeys();
            string packText = null, packFail = null, selfFail = null;
            KnownRepo[] packRead = null;
            Dictionary<string, object> freshAnswers = new Dictionary<string, object>();
            Dictionary<string, Dictionary<string, object>> freshDescribed = new Dictionary<string, Dictionary<string, object>>();
            string selfExe = SelfUpdate.ExePath();
            string addonsFolder = AddOns();
            Dictionary<TrackedAddon, Recognize.Result> recognized = new Dictionary<TrackedAddon, Recognize.Result>();
            Dictionary<string, Recognize.Result> freshRecognized = new Dictionary<string, Recognize.Result>();
            CurseForge.GameVersion = Game.CurseVersion(game);
            Work("Checking for new versions...", delegate
            {
                if (wantPack)
                {
                    try { packText = Pack.Download(); packRead = Pack.Parse(packText); }
                    catch (Exception why) { packFail = why.Message; Core.Log("pack.json: " + why.Message); }
                    // What it adds to the list, checked in the same round.
                    if (packRead != null)
                        foreach (KnownRepo k in packRead)
                            if (!onList.Exists(key => TrackedAddon.SameKey(key, k.Key)) && (k.Required || !Store.Offered(offered, k)) && !freshAnswers.ContainsKey(k.Key))
                            {
                                if (k.IsCurse && !CurseForge.On) continue;
                                try { freshAnswers[k.Key] = GitHub.Latest(k); }
                                catch (Exception why) { freshAnswers[k.Key] = why.Message; Core.Log("check " + k.Key + ": " + why.Message); }
                                if (k.IsCurse && freshAnswers[k.Key] is Release)
                                    try { freshDescribed[k.Key] = CurseForge.Mod(k.CurseId); }
                                    catch (Exception why) { Core.Log("CurseForge project " + k.CurseId + ": " + why.Message); }
                            }
                }
                foreach (TrackedAddon a in repos)
                {
                    try { answers[a] = GitHub.Latest(a); }
                    catch (Exception why) { answers[a] = why.Message; Core.Log("check " + a.Repo + ": " + why.Message); }
                }
                // Not installed by us but in AddOns: one of its releases (taken as installed), or a dev copy.
                foreach (TrackedAddon a in repos.Where(x => x.Tag == null && answers[x] is Release))
                    try { recognized[a] = Recognize.Look(addonsFolder, a, (Release)answers[a]); }
                    catch (Exception why) { Core.Log("recognize " + a.Repo + ": " + why.Message); }
                if (packRead != null)
                foreach (KeyValuePair<string, object> fresh in freshAnswers.Where(f => f.Value is Release))
                {
                    KnownRepo k = packRead.FirstOrDefault(x => x.Key == fresh.Key);
                    if (k == null) continue;
                    try { freshRecognized[fresh.Key] = Recognize.Look(addonsFolder, TrackedAddon.ForItem(k), (Release)fresh.Value); }
                    catch (Exception why) { Core.Log("recognize " + k.Key + ": " + why.Message); }
                }
                // A CurseForge project of the pack's: its link and author, once.
                foreach (TrackedAddon a in repos.Where(x => x.IsCurse && x.Slug == null && answers[x] is Release))
                    try { described[a] = CurseForge.Mod(a.CurseId); }
                    catch (Exception why) { Core.Log("CurseForge project " + a.CurseId + ": " + why.Message); }
                // The Manager's own new version: the releases page read above, no call more.
                try
                {
                    SelfUpdate.Offer offer = SelfUpdate.Check();
                    if (offer != null && selfExe != null) SelfUpdate.Fetch(offer, selfExe, null);
                    else if (offer != null) Core.Log("update: " + Core.Name + " " + offer.Version + " is out (not taken in a test)");
                }
                catch (Exception why) { selfFail = why.Message; Core.Log("update: " + why.Message); }
                return null;
            }, delegate
            {
                foreach (KeyValuePair<TrackedAddon, object> answer in answers)
                {
                    Release r = answer.Value as Release;
                    answer.Key.Latest = r;
                    answer.Key.Problem = r == null ? (string)answer.Value : null;
                    if (r != null) answer.Key.Known = r.Tag;
                    if (answer.Key.Tag == null && r != null)
                    {
                        Recognize.Result seen;
                        Recognized(answer.Key, recognized.TryGetValue(answer.Key, out seen) ? seen : null);
                    }
                }
                foreach (KeyValuePair<TrackedAddon, Dictionary<string, object>> said in described) CurseForge.Describe(said.Key, said.Value);
                List<TrackedAddon> joined = new List<TrackedAddon>();
                if (wantPack) packProblem = packFail;
                if (packRead != null)
                {
                    try
                    {
                        if (Pack.Use(packText, true)) Core.Log("pack.json read: " + Pack.Addons.Length + " addons");
                        packAt = started;
                        joined = Store.Seed(addons);
                        foreach (TrackedAddon a in joined)
                        {
                            object said;
                            Dictionary<string, object> mod;
                            if (freshDescribed.TryGetValue(a.Key, out mod)) CurseForge.Describe(a, mod);
                            if (!freshAnswers.TryGetValue(a.Key, out said)) continue;
                            a.Latest = said as Release;
                            a.Problem = a.Latest == null ? (string)said : null;
                            if (a.Latest != null) a.Known = a.Latest.Tag;
                            Recognize.Result seen;
                            if (a.Tag == null && a.Latest != null) Recognized(a, freshRecognized.TryGetValue(a.Key, out seen) ? seen : null);
                        }
                    }
                    catch (Exception why) { packProblem = why.Message; Core.Log("pack.json not used: " + why.Message); }
                }
                Store.Save(addons);
                Fill();
                string ready = SelfUpdate.Ready(selfExe);
                if (ready != null && ready != selfReady) { selfReady = ready; toldUpdate = false; }
                ShowSelf();
                int fails = addons.Count(a => a.Problem != null), updates = addons.Count(a => a.UpdateWaits);
                List<TrackedAddon> skipped = addons.FindAll(a => a.Tag != null && a.Latest != null && a.Latest.Skipped != null);
                if (packProblem != null && Pack.Addons == null) Status("The Forever pack's list did not come: " + packProblem + ".", CRed);
                else if (fails > 0) Status(fails == 1 ? "The check failed for " + addons.First(a => a.Problem != null).Name + ": " + addons.First(a => a.Problem != null).Problem + "." : fails + " checks failed (see each one).", CRed);
                else if (packProblem != null && manual) Status("The Forever pack's list did not come: " + packProblem + " (the copy kept is in use).", CRed);
                else if (joined.Count > 0 && addons.Exists(a => a.Tag != null))
                    Status("New in the Forever pack: " + string.Join(", ", joined.Select(a => a.Name).ToArray()) + "." + (joined.Exists(Ticked) ? " Install selected puts in the ticked ones." : ""), CGold);
                else if (updates > 0) Status(updates == 1 ? "An update is out." : updates + " updates are out.", CGold);
                else if (skipped.Count > 0)
                    Status("Checked at " + DateTime.Now.ToString("HH:mm") + ". " + (skipped.Count == 1
                        ? skipped[0].Name + ": " + skipped[0].Latest.SkippedWhy + "; " + VersionText(skipped[0], skipped[0].Latest.Tag) + " is the newest that can be installed."
                        : skipped.Count + " addons have a newest release that cannot be installed (see each one)."), CGold);
                else if (!addons.Exists(a => a.Tag != null))
                {
                    List<TrackedAddon> ticked = ToInstall();
                    long size = ticked.Sum(a => a.Latest != null && a.Latest.Size > 0 ? a.Latest.Size : 0);
                    Status("Checked at " + DateTime.Now.ToString("HH:mm") + ". Nothing installed yet: " + (ticked.Count > 0
                        ? "Install selected puts in the " + ticked.Count + " ticked addons" + (size > 0 ? " (" + Bytes(size) + ")" : "") + "."
                        : "tick an addon and click Install selected."), CDim);
                }
                else Status("Checked at " + DateTime.Now.ToString("HH:mm") + ": everything installed is up to date.", CDim);
                if (!AutoUpdates()) ShotIfAsked();
            }, null);
        }

        // What the check found of an addon not installed by us: the same
        // files as one of its releases, taken as installed (ours from now
        // on, its updates as any other's); else a dev copy, left alone.
        void Recognized(TrackedAddon a, Recognize.Result seen)
        {
            a.Dev = false;
            a.DevFolders = null;
            a.DevCompared = 0;
            if (seen == null) return;
            if (seen.Kind == Recognize.Kind.Same && seen.Release != null)
            {
                a.Tag = seen.Release.Tag;
                a.Asset = seen.Release.ZipName;
                a.Folders = seen.Folders;
                a.InstalledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                a.Want = true;
                Core.Log("recognize " + a.Repo + ": AddOns has " + seen.Release.Tag + " (" + string.Join(", ", seen.Folders) + "), file by file; taken as installed");
                return;
            }
            a.Dev = true;
            a.DevFolders = seen.Folders;
            a.DevCompared = seen.Compared;
            Core.Log("recognize " + a.Repo + ": AddOns has " + string.Join(", ", seen.Folders ?? new string[0]) + ", matching none of " + seen.Compared + " release(s): a dev copy, left as it is");
        }

        // A new version of the Manager, ready: said under the list, once in the tray.
        void ShowSelf()
        {
            bool newer = selfReady != null && Core.CompareVersions(selfReady, Core.Version) > 0;
            updateLink.Visible = newer;
            curseNote.Visible = !newer;
            if (restartItem != null) restartItem.Visible = newer;
            if (!newer) return;
            updateLink.Text = "Restart to update to v" + selfReady;
            updateLink.Location = new Point(curseNote.Right - updateLink.PreferredWidth, curseNote.Top);
            if (!toldUpdate && !Visible && !shotMode)
            {
                toldUpdate = true;
                tray.ShowBalloonTip(5000, Core.Name, Core.Name + " " + selfReady + " is ready: right-click, Restart to update.", ToolTipIcon.Info);
            }
        }

        void RestartToUpdate()
        {
            if (busy) { Status("Wait for the work under way to end, then restart.", CGold); return; }
            if (SelfUpdate.Ready(SelfUpdate.ExePath()) == null) { selfReady = null; ShowSelf(); Status("The new version is no longer there: it is downloaded again at the next check.", CRed); return; }
            SelfUpdate.RestartAsked = true;
            quitting = true;
            Close();
        }

        // Updates by themselves (Auto update on): now with the game closed,
        // else once it has been closed for 8 s. True when some started.
        bool AutoUpdates()
        {
            if (game == null) return false;
            List<TrackedAddon> due = addons.FindAll(a => a.Auto && a.UpdateWaits && !a.Missing && a.Problem == null && a.Latest != null && Blocked(a) == null);
            if (Core.Env("FUM_SHOT_INSTALL") == "1")
                // Tests: what Install selected would put in (a ticked one), and nothing else.
                due.AddRange(addons.FindAll(a => a.Tag == null && !a.Dev && a.Latest != null && Ticked(a) && Blocked(a) == null && !due.Contains(a)));
            if (due.Count == 0) return false;
            if (Game.Running(game))
            {
                foreach (TrackedAddon a in due) if (!waiting.Contains(a)) waiting.Add(a);
                closedSince = DateTime.MinValue;
                watch.Start();
                Status(due.Count == 1 ? due[0].Name + " " + VersionText(due[0], due[0].Known) + " waits for WoW Forever to close." : due.Count + " updates wait for WoW Forever to close.", CGold);
                list.Invalidate();
                return false;
            }
            RunQueue(due, true);
            return true;
        }

        void WatchGame()
        {
            if (waiting.Count == 0) { watch.Stop(); return; }
            if (Game.Running(game)) { closedSince = DateTime.MinValue; return; }
            if (closedSince == DateTime.MinValue) { closedSince = DateTime.Now; return; }
            if ((DateTime.Now - closedSince).TotalSeconds < 8 || busy) return;
            watch.Stop();
            List<TrackedAddon> due = new List<TrackedAddon>(waiting);
            waiting.Clear();
            RunQueue(due, true);
        }

        void RunQueue(List<TrackedAddon> queue, bool quiet)
        {
            if (queue.Count == 0) { ShotIfAsked(); return; }
            TrackedAddon a = queue[0];
            queue.RemoveAt(0);
            Install(a, quiet, delegate { RunQueue(queue, quiet); });
        }

        // Where it comes from, said before it goes in.
        static string Where(TrackedAddon a)
        {
            if (a.IsCurse) return "CurseForge (" + CurseForge.Site + (a.Slug ?? a.CurseId.ToString(CultureInfo.InvariantCulture)) + "), checked against CurseForge's sha1, and only as addon folders";
            if (a.Private) return Friends.Live ? "the Forever pack's own releases, from the friends' server, checked against the pack's sha256" : "the Forever pack's own releases, checked against GitHub's sha256";
            return "its GitHub releases (github.com/" + a.Repo + "), checked against GitHub's sha256";
        }

        const string OnlyOurs = "Forever Manager never touches a folder it did not put in AddOns: if AddOns already has folders of the same names, it asks first.";

        bool Trust(TrackedAddon a)
        {
            if (a.Trusted) return true;
            string text = "Addons run inside WoW Forever with your character and account.\n\n" +
                "Install " + a.Name + " only if you trust its author. It comes straight from " + Where(a) + ", with nothing in between.\n\n" + OnlyOurs;
            if (MessageBox.Show(this, text, "Install " + a.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return false;
            a.Trusted = true;
            if (addons.Contains(a)) Store.Save(addons);
            return true;
        }

        bool GameOpenOk(string doing)
        {
            if (!Game.Running(game)) return true;
            return MessageBox.Show(this, "WoW Forever is open. " + doing + " now anyway?\n\nThe game reads the new files at your next /reload or login.",
                "WoW Forever is open", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
        }

        // The ticked ones not installed yet; the updates that wait are the Update all above the list.
        void InstallSelected()
        {
            List<TrackedAddon> fresh = ToInstall();
            if (busy || fresh.Count == 0 || game == null) return;
            if (!TrustAll(fresh) || !GameOpenOk("Install")) return;
            RunQueue(fresh, false);
        }

        // One yes for all the new ones Install selected puts in, with what they are and how big.
        bool TrustAll(List<TrackedAddon> fresh)
        {
            List<TrackedAddon> asked = fresh.FindAll(a => !a.Trusted);
            if (asked.Count == 0) return true;
            if (asked.Count == 1) return Trust(asked[0]);
            long total = 0;
            bool allSized = true;
            List<string> lines = new List<string>();
            foreach (TrackedAddon a in asked)
            {
                long size = a.Latest != null ? a.Latest.Size : -1;
                if (size > 0) total += size; else allSized = false;
                lines.Add("  " + a.Name + "  (" + (a.IsCurse ? "CurseForge" : a.Private ? "Forever pack" : "github.com/" + a.Repo) + (size > 0 ? ", " + Bytes(size) : "") + ")");
            }
            string text = "Install these " + asked.Count + " addons?\n\n" + string.Join("\n", lines.ToArray()) + "\n\n" +
                "Addons run inside WoW Forever with your character and account. Each comes straight from its source (the Forever pack's releases, " +
                "its author's GitHub releases or CurseForge), checked against the hash its source gives, with nothing in between.\n\n" + OnlyOurs +
                (total > 0 ? "\n\nAbout " + Bytes(total) + (allSized ? "" : " or more") + " to download." : "");
            if (MessageBox.Show(this, text, "Install selected", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return false;
            foreach (TrackedAddon a in asked) a.Trusted = true;
            Store.Save(addons);
            return true;
        }

        void Install(TrackedAddon a, bool quiet, Action then)
        {
            Install(a, quiet, then, a.GuidesOnly && !a.Dev ? Installer.Foreign.Skip : Installer.Foreign.Refuse);
        }

        // Foreign: what to do with folders of the zip's names that this app
        // did not put in AddOns (guide packs: left as they are; an addon:
        // asked, unless it updates by itself, then left as they are).
        void Install(TrackedAddon a, bool quiet, Action then, Installer.Foreign foreign)
        {
            string addonsFolder = AddOns();
            Release known = a.Latest;
            string[] before = a.Folders;
            string label = a.RepoName + " " + (a.Tag ?? "by hand");
            Work("Getting " + a.Name + "...", delegate
            {
                Release r = known ?? GitHub.Latest(a);
                string temp = Path.Combine(Path.GetTempPath(), "ForeverManager");
                Directory.CreateDirectory(temp);
                Installer.CleanTemp(temp);
                // A download is taken up where it stopped, even at the next try (a voice pack is 1.5 GB).
                const bool resume = true;
                string zip = Installer.PartPath(temp, r);
                bool keep = false, whole = false;
                try
                {
                    long have = resume && File.Exists(zip) ? new FileInfo(zip).Length : 0;
                    Installer.RoomToDownload(temp, addonsFolder, r.Size, have);
                    Say(a, "Downloading...", 0);
                    if (!(have > 0 && have == r.Size))
                        Net.Download(r.ZipUrl, zip, delegate(long done, long total)
                        {
                            long all = total > 0 ? total : r.Size;
                            float part = all > 0 ? (float)done / all : -1;
                            Say(a, all > 0 ? "Downloading " + (int)(part * 100) + "%" + (all >= 100L * 1048576 ? " of " + Bytes(all) : "") : "Downloading " + Bytes(done), part);
                        }, null, 0, resume, r.Private ? Secrets.GitHubToken() : null);
                    whole = true;
                    Say(a, "Checking...", 1);
                    Installer.Verify(zip, r);
                    if (a.GuidesOnly) Installer.OnlyGuides(zip);
                    if (a.IsCurse) Installer.OnlyAddonFolders(zip);
                    Installer.RoomToUnpack(addonsFolder, zip);
                    // Checked whole: kept if it cannot go in now (a folder in use, or in the way), not downloaded again.
                    keep = resume;
                    Say(a, "Installing...", 1);
                    Placed placed = Installer.Put(addonsFolder, zip, before, label, foreign);
                    keep = false;
                    return new object[] { r, placed };
                }
                catch
                {
                    // Half a big download is kept for the next try.
                    if (resume && !whole && File.Exists(zip) && new FileInfo(zip).Length > 8L * 1048576) keep = true;
                    throw;
                }
                finally
                {
                    try { if (!keep && File.Exists(zip)) File.Delete(zip); } catch { }
                }
            }, delegate(object result)
            {
                object[] pair = (object[])result;
                Release r = (Release)pair[0];
                Placed placed = (Placed)pair[1];
                bool updated = a.Tag != null;
                a.Busy = null;
                a.Latest = r;
                a.Known = r.Tag;
                a.Tag = r.Tag;
                a.Asset = r.ZipName;
                a.Folders = placed.Folders;
                a.Want = true;
                a.Marked = false;
                a.Problem = null;
                a.InstalledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                if (!addons.Contains(a)) addons.Add(a);
                Store.Save(addons);
                Fill();
                string said = a.Name + " " + VersionText(a, r.Tag) + (updated ? " is in: " : " installed: ") + placed.Folders.Length + " folder(s) in AddOns."
                    + (placed.Kept != null ? " " + placed.Kept.Length + " folder(s) already in AddOns and not put there by " + Core.Name + " were left as they are (" + string.Join(", ", placed.Kept) + ")." : "")
                    + (placed.Skipped > 0 ? " " + placed.Skipped + " file(s) that are not addon files were left out." : "")
                    + (placed.Left != null ? " The old copy could not go to the Recycle Bin; it is in " + placed.Left + "." : "")
                    + (r.Sha256 == null && r.Sha1 == null ? " (No hash from its source this time: only the download itself was checked.)" : "");
                Status(said, CGreen);
                Core.Log(said);
                if (quiet && !Visible) tray.ShowBalloonTip(5000, Core.Name, a.Name + " " + VersionText(a, r.Tag) + (updated ? " is in." : " installed."), ToolTipIcon.Info);
                if (then != null) then();
            }, delegate(Exception why)
            {
                a.Busy = null;
                InTheWay inTheWay = why as InTheWay;
                // Folders that are not ours: moved only with a yes, never by an update that runs by itself.
                if (inTheWay != null && !quiet && foreign == Installer.Foreign.Refuse)
                {
                    list.Invalidate();
                    if (MessageBox.Show(this, "AddOns already has " + string.Join(", ", inTheWay.Folders) + ", which " + Core.Name + " did not put there (a copy by hand, or another app's).\n\n" +
                        "Move " + (inTheWay.Folders.Length == 1 ? "it" : "them") + " to the Recycle Bin and put " + a.Name + " in " + (inTheWay.Folders.Length == 1 ? "its" : "their") + " place?",
                        "Install " + a.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    {
                        Install(a, quiet, then, Installer.Foreign.Replace);
                        return;
                    }
                    Status(a.Name + " not installed: " + string.Join(", ", inTheWay.Folders) + " left as " + (inTheWay.Folders.Length == 1 ? "it was" : "they were") + ".", CGold);
                    if (then != null) then();
                    return;
                }
                Status("Could not install " + a.Name + ": " + why.Message, CRed);
                Core.Log("install " + a.Repo + ": " + why.Message);
                list.Invalidate();
                if (then != null) then();
            });
        }

        // Installed and ticked: what Uninstall selected takes out.
        List<TrackedAddon> ToUninstall()
        {
            return addons == null ? new List<TrackedAddon>() : addons.FindAll(a => a.Tag != null && a.Marked && a.Busy == null);
        }

        // One question for all of them, then one after another.
        void UninstallSelected()
        {
            List<TrackedAddon> due = ToUninstall();
            if (busy || game == null || due.Count == 0) return;
            string names = string.Join("\n", due.Select(a => "  " + a.Name).ToArray());
            if (MessageBox.Show(this, "Move the folders of " + (due.Count == 1 ? due[0].Name : "these " + due.Count + " addons") + " from AddOns to the Recycle Bin?\n\n" + names
                + "\n\nTheir settings (in WTF) stay, for when you install them again.", "Uninstall selected", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            if (!GameOpenOk("Uninstall")) return;
            UninstallQueue(due, 0);
        }

        void UninstallQueue(List<TrackedAddon> queue, int done)
        {
            if (queue.Count == 0)
            {
                if (done > 1) Status(done + " addons uninstalled: their folders are in the Recycle Bin.", CGreen);
                return;
            }
            TrackedAddon a = queue[0];
            queue.RemoveAt(0);
            Uninstall(a, false, delegate { UninstallQueue(queue, done + 1); });
        }

        // AskGame: whether to ask first when WoW Forever is open (the queue asked once already).
        // Then: called once it is out of AddOns.
        void Uninstall(TrackedAddon a, bool askGame, Action then)
        {
            if (a == null || a.Tag == null || game == null || busy) return;
            if (askGame && !GameOpenOk("Uninstall")) return;
            string addonsFolder = AddOns();
            string[] folders = a.Folders;
            string label = a.RepoName + " " + a.Tag;
            Work("Uninstalling " + a.Name + "...", delegate { return Installer.Remove(addonsFolder, folders, label); }, delegate(object result)
            {
                string left = result as string;
                a.Tag = null;
                a.Folders = null;
                a.Asset = null;
                a.InstalledAt = null;
                // Not selected any more: Install selected leaves it out (a required one stays ticked).
                a.Want = a.Required;
                a.Marked = false;
                Store.Save(addons);
                Fill();
                Status(left == null ? a.Name + " uninstalled: its folders are in the Recycle Bin." : a.Name + " uninstalled; the Recycle Bin refused its folders, which are in " + left + ".", CGreen);
                Core.Log("uninstalled " + a.Repo + (left != null ? ", left in " + left : ""));
                if (then != null) then();
            }, null);
        }

        void RemovePicked()
        {
            TrackedAddon a = Picked();
            if (a == null || a.Required) return;
            string text = a.Tag != null
                ? "Take " + a.Name + " off the list?\n\nIts folders stay in AddOns and it stops being updated. (The × at the end of its row removes the folders.)"
                : "Take " + a.Name + " off the list?";
            if (a.Source == "pack") text += "\n\nIt came with the Forever pack: it does not come back by itself. To keep it on the list without installing it, untick it instead.";
            if (MessageBox.Show(this, text, "Remove " + a.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            addons.Remove(a);
            waiting.Remove(a);
            Store.Save(addons);
            Fill();
            Status(a.Name + " is off the list." + (a.Source == "pack" ? " It came with the Forever pack and does not come back by itself." : ""));
        }

        void AddFromGitHub()
        {
            string given = Ask("Add from GitHub", "The addon's GitHub link (github.com/owner/name) or owner/name:",
                "It is checked against its own releases on GitHub: nothing in between.", delegate(string clip) { return GitHub.ParseRepo(clip) != null; }, null, null);
            if (given == null) return;
            string repo = GitHub.ParseRepo(given);
            if (repo == null) { Status("That is not a GitHub link (github.com/owner/name).", CRed); return; }
            if (Pack.Owns(repo)) { Status("That is the Forever pack's own repository: its addons come on the list by themselves.", CDim); return; }
            TrackedAddon same = addons.Find(a => !a.IsCurse && string.Equals(a.Repo, repo, StringComparison.OrdinalIgnoreCase));
            if (same != null)
            {
                Pick(same);
                Status(same.Name + " is on the list already.");
                return;
            }
            AddRepo(TrackedAddon.ForRepo(repo));
        }

        void AddRepo(TrackedAddon fresh)
        {
            string repo = fresh.Repo;
            Work("Looking up " + repo + " on GitHub...", delegate { return GitHub.Latest(fresh); }, delegate(object result)
            {
                Release r = (Release)result;
                fresh.Latest = r;
                fresh.Known = r.Tag;
                addons.Add(fresh);
                Store.Save(addons);
                Fill();
                Pick(fresh);
                Status(fresh.Name + " " + r.Tag + (r.Test ? " (a test version)" : "") + " is on the list. Tick it and click Install selected to put it in AddOns.", CGreen);
            }, delegate(Exception why)
            {
                // Only test versions: taken only with a yes.
                if (why is Final && why.Message == GitHub.NoStable && !fresh.Betas)
                {
                    if (MessageBox.Show(this, repo + " has only test versions (pre-releases) on GitHub.\n\nAdd it with its test versions?", "Add " + fresh.Name,
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    {
                        fresh.Betas = true;
                        AddRepo(fresh);
                    }
                    else Status("Not added: " + why.Message + ".", CDim);
                    return;
                }
                if (why is Final) { Status("Not added: " + why.Message + ".", CRed); return; }
                // GitHub out for now: on the list all the same, checked again later.
                fresh.Problem = why.Message;
                addons.Add(fresh);
                Store.Save(addons);
                Fill();
                Pick(fresh);
                Status(repo + " is on the list, but GitHub did not answer: " + why.Message + ".", CGold);
            });
        }

        void AddFromCurseForge()
        {
            if (!CurseForge.On) { Status(CurseForge.Off + ".", CRed); return; }
            string given = Ask("Add from CurseForge", "The addon's CurseForge link (curseforge.com/wow/addons/name) or its project id:",
                "Its file for your WoW Forever version comes from CurseForge, checked against its sha1.", delegate(string clip) { long i; string s; return CurseForge.ParseLink(clip, out i, out s); }, null, null);
            if (given != null) AddCurse(given, null);
        }

        // A CurseForge project on the list, from its link or id: its project
        // (by id, or by the name in its link), then its file for the game's
        // version. Done (tests): called once it is over, either way.
        void AddCurse(string given, Action done)
        {
            long id;
            string slug;
            if (!CurseForge.ParseLink(given, out id, out slug))
            {
                Status("That is not a CurseForge addon link (curseforge.com/wow/addons/name) or a project id.", CRed);
                if (done != null) done();
                return;
            }
            TrackedAddon same = addons.Find(a => a.IsCurse && (a.CurseId == id || (slug != null && string.Equals(a.Slug, slug, StringComparison.OrdinalIgnoreCase))));
            if (same != null)
            {
                Pick(same);
                Status(same.Name + " is on the list already.");
                if (done != null) done();
                return;
            }
            TrackedAddon fresh = null;
            string fileProblem = null;
            CurseForge.GameVersion = Game.CurseVersion(game);
            Work("Looking up " + (slug ?? "project " + id) + " on CurseForge...", delegate
            {
                Dictionary<string, object> mod = id > 0 ? CurseForge.Mod(id) : CurseForge.BySlug(slug);
                fresh = TrackedAddon.ForCurse(Json.Long(mod, "id"));
                CurseForge.Describe(fresh, mod);
                try { fresh.Latest = CurseForge.Latest(fresh.CurseId); }
                catch (Final why) { fileProblem = why.Message; }
                return null;
            }, delegate
            {
                TrackedAddon twin = addons.Find(a => a.IsCurse && a.CurseId == fresh.CurseId);
                if (twin != null) { Pick(twin); Status(twin.Name + " is on the list already."); }
                else if (fileProblem == CurseForge.NoAppDownload) Status("Not added: " + fresh.Name + ": " + fileProblem + ".", CRed);
                else
                {
                    if (fresh.Latest != null) fresh.Known = fresh.Latest.Tag;
                    fresh.Problem = fileProblem;
                    addons.Add(fresh);
                    Store.Save(addons);
                    Fill();
                    Pick(fresh);
                    Status(fileProblem == null
                        ? fresh.Name + " " + VersionText(fresh, fresh.Latest.Tag) + " is on the list, from CurseForge. Tick it and click Install selected to put it in AddOns."
                        : fresh.Name + " is on the list, but " + fileProblem + "; it is checked again later.", fileProblem == null ? CGreen : CGold);
                    Core.Log("added from CurseForge: " + fresh.CurseId + " " + fresh.Slug);
                }
                if (done != null) done();
            }, delegate(Exception why)
            {
                Status("Not added: " + why.Message + ".", CRed);
                if (done != null) done();
            });
        }

        // A one-line question. Shot (tests): the window, filled with given,
        // drawn into that file and closed; then is called with what it says.
        string Ask(string head, string say, string note, Func<string, bool> fromClipboard, string shot, Action<string> then)
        {
            QuietForm ask = new QuietForm { Quiet = shot != null };
            ask.Text = head;
            ask.FormBorderStyle = FormBorderStyle.FixedDialog;
            ask.StartPosition = FormStartPosition.CenterParent;
            ask.MinimizeBox = ask.MaximizeBox = false;
            ask.ShowInTaskbar = false;
            ask.BackColor = CBack;
            ask.ForeColor = CText;
            ask.Font = Font;
            ask.AutoScaleMode = AutoScaleMode.None;
            ask.ClientSize = new Size(S(520), S(150));
            if (shot != null)
            {
                ask.StartPosition = FormStartPosition.Manual;
                ask.Location = new Point(-S(3000), S(40));
            }
            ask.Controls.Add(new Label { Text = say, AutoSize = true, Location = new Point(S(16), S(16)) });
            TextBox box = new TextBox { Location = new Point(S(16), S(44)), Size = new Size(S(488), S(24)), BackColor = CPanel, ForeColor = CText, BorderStyle = BorderStyle.FixedSingle };
            if (shot != null) box.Text = Core.Env("FUM_SHOT_CURSE_TEXT");
            else
                try
                {
                    string clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
                    if (clip.Length < 300 && fromClipboard(clip)) box.Text = clip;
                }
                catch { }
            ask.Controls.Add(box);
            ask.Controls.Add(new Label { Text = note, AutoSize = true, Font = small, ForeColor = CDim, Location = new Point(S(16), S(74)) });
            Button ok = new Button(), cancel = new Button();
            Style(ok, "Add", 294, 106, 100, ask);
            Style(cancel, "Cancel", 404, 106, 100, ask);
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            ask.AcceptButton = ok;
            ask.CancelButton = cancel;
            ask.HandleCreated += delegate { int dark = 1; try { Native.DwmSetWindowAttribute(ask.Handle, 20, ref dark, 4); } catch { } };
            if (shot != null)
            {
                System.Windows.Forms.Timer later = new System.Windows.Forms.Timer { Interval = 500 };
                ask.Shown += delegate { later.Start(); };
                later.Tick += delegate
                {
                    later.Stop();
                    Draw(ask, shot);
                    string text = box.Text.Trim();
                    ask.Close();
                    ask.Dispose();
                    then(text);
                };
                // Shown without taking the focus: never in front of what the user is doing.
                ask.Show();
                return null;
            }
            using (ask)
                return ask.ShowDialog(this) == DialogResult.OK && box.Text.Trim() != "" ? box.Text.Trim() : null;
        }

        void PickGame()
        {
            using (FolderBrowserDialog pick = new FolderBrowserDialog())
            {
                pick.Description = "Pick WoW Forever's folder (_forever_, the one with Wow.exe), or World of Warcraft's folder.";
                if (game != null) pick.SelectedPath = game;
                if (pick.ShowDialog(this) != DialogResult.OK) return;
                string chosen = pick.SelectedPath;
                if (Game.IsForever(Path.Combine(chosen, "_forever_"))) chosen = Path.Combine(chosen, "_forever_");
                if (!Game.IsForever(chosen)) { Status("That is not WoW Forever's folder: no Wow.exe of WoW Forever in " + chosen + ".", CRed); return; }
                Core.SetSetting("game", chosen);
                game = chosen;
                ShowGame();
                Fill();
                Status("WoW Forever is in " + chosen + ".");
            }
        }

        void SetUpTray()
        {
            tray.Icon = TrayIcon();
            // No About in the menu: the credits go in the tray's tooltip (63 characters at most).
            tray.Text = Core.Name + " v" + Core.Version + " · made by " + Core.Author;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, delegate { ShowMe(); });
            menu.Items.Add("Check for updates", null, delegate { CheckAll(true); });
            restartItem = new ToolStripMenuItem("Restart to update", null, delegate { RestartToUpdate(); }) { Visible = false };
            menu.Items.Add(restartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { quitting = true; Close(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowMe(); };
            tray.BalloonTipClicked += delegate { ShowMe(); };
            tray.Visible = !shotMode;
        }

        // A window drawn straight into a picture: off screen, Windows never
        // composes some of it, so a print of the screen comes out white.
        static void Draw(Form form, string path)
        {
            using (Bitmap picture = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                using (Bitmap whole = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(whole, new Rectangle(0, 0, form.Width, form.Height));
                    Point corner = form.PointToScreen(Point.Empty);
                    using (Graphics g = Graphics.FromImage(picture))
                        g.DrawImage(whole, -(corner.X - form.Left), -(corner.Y - form.Top));
                }
                picture.Save(path, ImageFormat.Png);
            }
        }

        // Tests (FUM_SHOT=<png>): the window drawn into a file once the work
        // is done, then closed. FUM_SHOT_CURSE=<png>: then Add from CurseForge
        // too, filled with FUM_SHOT_CURSE_TEXT and drawn; what it adds, then the
        // window again into FUM_SHOT_CURSE_DONE.
        void ShotIfAsked()
        {
            string path = Core.Env("FUM_SHOT");
            if (path == "" || shotTaken) return;
            shotTaken = true;
            // FUM_SHOT_PICK: the row of that name picked and in view.
            ListViewItem pick = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag != null && ((TrackedAddon)i.Tag).Name == Core.Env("FUM_SHOT_PICK"));
            if (pick != null) { pick.Selected = true; pick.EnsureVisible(); }
            // FUM_SHOT_ARM: the row of that name with its × clicked once.
            TrackedAddon primed = addons.FirstOrDefault(a => a.Name == Core.Env("FUM_SHOT_ARM") && a.Tag != null);
            if (primed != null) Arm(primed);
            // FUM_SHOT_CLICK: "<name>|update" clicks that row's Update button,
            // "-|all" the Update all above the State column, "-|installsel" and
            // "-|uninstallsel" Install and Uninstall selected (no question),
            // "<name>|cross" its × twice, "<name>|cross1" once, through the
            // list's own click (the picture waits for the work it starts).
            // FUM_SHOT_MARK: those rows' boxes clicked ("A,B").
            foreach (string name in Core.Env("FUM_SHOT_MARK").Split(','))
            {
                TrackedAddon marked = addons.FirstOrDefault(x => x.Name == name);
                if (marked != null) Toggle(marked);
            }
            string[] click = Core.Env("FUM_SHOT_CLICK").Split('|');
            ListViewItem target = click.Length == 2 ? list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag != null && ((TrackedAddon)i.Tag).Name == click[0]) : null;
            if (click.Length == 2 && click[1] == "all") topUpdate.PerformClick();
            // Without their questions: what they put in and take out.
            if (click.Length == 2 && click[1] == "installsel") RunQueue(ToInstall(), false);
            if (click.Length == 2 && click[1] == "uninstallsel") UninstallQueue(ToUninstall(), 0);
            bool bulk = click.Length == 2 && (click[1] == "all" || click[1] == "installsel" || click[1] == "uninstallsel");
            if (target != null && !bulk)
            {
                Rectangle spot = click[1] == "update" ? RowChip(CellOf(target, ColState), RowAction((TrackedAddon)target.Tag) ?? "Update") : CrossBox(CellOf(target, ColCross));
                MouseEventArgs at = new MouseEventArgs(MouseButtons.Left, 1, spot.X + spot.Width / 2, spot.Y + spot.Height / 2, 0);
                ClickList(list, at);
                if (click[1] == "cross") ClickList(list, at);
            }
            System.Windows.Forms.Timer later = new System.Windows.Forms.Timer { Interval = target != null && click[1] != "cross1" ? 4000 : 800 };
            if (bulk) later.Interval = 8000;
            later.Tick += delegate
            {
                later.Stop();
                Draw(this, path);
                if (Core.Env("FUM_SHOT_CURSE") != "")
                {
                    Ask("Add from CurseForge", "The addon's CurseForge link (curseforge.com/wow/addons/name) or its project id:",
                        "Its file for your WoW Forever version comes from CurseForge, checked against its sha1.", delegate(string clip) { return false; },
                        Core.Env("FUM_SHOT_CURSE"), delegate(string text)
                        {
                            AddCurse(text, delegate
                            {
                                System.Windows.Forms.Timer after = new System.Windows.Forms.Timer { Interval = 800 };
                                after.Tick += delegate
                                {
                                    after.Stop();
                                    if (Core.Env("FUM_SHOT_CURSE_DONE") != "") Draw(this, Core.Env("FUM_SHOT_CURSE_DONE"));
                                    quitting = true;
                                    Close();
                                };
                                after.Start();
                            });
                        });
                    return;
                }
                quitting = true;
                Close();
            };
            later.Start();
        }
    }

}
