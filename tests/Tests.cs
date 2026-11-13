// Checks for Forever Manager, compiled with the app (/main:ForeverManager.Tests)
// and the FAKE secrets of make_secrets.ps1 -Fake (run_tests.sh).
//   Tests.exe                    offline checks only (the pack, GitHub and CurseForge answers from tests/fixtures)
//   Tests.exe live               also asks GitHub about public addons (about 4 API calls, then its pages)
//   Tests.exe zip <file.zip> <n> also installs a real release zip into a test AddOns (n folders expected)
//   TestsLive.exe curseforge-live (run_tests.sh curseforge-live): one real CurseForge question
// Everything happens in a folder under %TEMP%; the real AddOns, settings and
// Run key are never touched (FUM_HOME and FUM_TEST_BIN are set first).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ForeverManager
{
    static class Tests
    {
        static int passed, failed;
        static string root;

        // make_secrets.ps1's fake ones: never real.
        const string FakeToken = "github_pat_FAKE_TEST_TOKEN_0123456789_abcdefghijklmnopqrstuvwxyz";
        const string FakeCurse = "FAKE_TEST_CURSEFORGE_KEY_0123456789_abcdefghijklmnop";
        const string Own = "morderous/forever-addons";

        static void Is(bool ok, string what)
        {
            if (ok) passed++;
            else { failed++; Console.WriteLine("FAIL " + what); }
        }

        static bool Throws(Action act, string containing)
        {
            try { act(); return false; }
            catch (Exception why)
            {
                if (containing != null && !why.Message.Contains(containing)) { Console.WriteLine("  (said: " + why.Message + ")"); return false; }
                return true;
            }
        }

        static string Fixture(params string[] parts)
        {
            return Path.GetFullPath(Path.Combine(new[] { AppDomain.CurrentDomain.BaseDirectory, "..", "fixtures" }.Concat(parts).ToArray()));
        }

        static int Main(string[] args)
        {
            root = Path.Combine(Path.GetTempPath(), "fm-tests-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("FUM_HOME", Path.Combine(root, "home"));
            Environment.SetEnvironmentVariable("FUM_TEST_BIN", Path.Combine(root, "bin"));
            foreach (string name in new[] { "FUM_PACK", "FUM_CURSEFORGE", "FUM_PRIVATE_RELEASES", "FUM_NO_API", "FUM_SELF_EXE", "FUM_GAME_VERSION" })
                Environment.SetEnvironmentVariable(name, null);
            try
            {
                if (args.Length > 0 && args[0] == "curseforge-live") { CurseLive(); }
                else if (args.Length > 2 && args[0] == "recognize-live") { RecognizeLive(args[1], args[2], args.Length > 3 ? long.Parse(args[3]) : -1); }
                else
                {
                    Repos();
                    Recognizing();
                    Masking();
                    Folders();
                    ReleaseJson();
                    Zips();
                    PutAndUpdate();
                    RollBack();
                    NotTheirs();
                    Store();
                    PackList();
                    OwnReleases();
                    FriendsServer();
                    PrivateInstall();
                    CurseLinks();
                    CursePick();
                    CurseFlow();
                    SelfUpdates();
                    Series();
                    NoZip();
                    WebPages();
                    Resume();
                    Room();
                    Window();
                    if (args.Length > 0 && args[0] == "live") Live();
                    if (args.Length > 2 && args[0] == "zip") RealZip(args[1], int.Parse(args[2]));
                }
            }
            catch (Exception why)
            {
                failed++;
                Console.WriteLine("CRASH " + why);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine(passed + " ok, " + failed + " failed");
            return failed;
        }

        // An addon in AddOns by hand told apart: a zip's list of files from
        // its last bytes, CRC-32, and AddOns compared file by file.
        static void Recognizing()
        {
            Is(Crc32.Of(Encoding.ASCII.GetBytes("123456789"), 9) == 0xCBF43926u, "crc32 check value");
            string dir = Path.Combine(root, "recognize");
            string zip = Path.Combine(dir, "Pack-1.zip"), addons = Path.Combine(dir, "AddOns");
            Directory.CreateDirectory(addons);
            using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (string[] f in new[] { new[] { "PackA/PackA.toc", "## Title: A\n" }, new[] { "PackA/data.lua", "local x = 1\n" }, new[] { "PackB/PackB.toc", "## Title: B\n" }, new[] { "PackA/tool.exe", "MZ" } })
                    using (StreamWriter w = new StreamWriter(archive.CreateEntry(f[0]).Open())) w.Write(f[1]);
            }
            byte[] bytes = File.ReadAllBytes(zip);
            List<ZipItem> whole = Recognize.FromTail(bytes, 0, null);
            Is(whole.Count == 4 && whole.Exists(e => e.Name == "PackA/data.lua" && e.Size == 12), "zip's list of files from its whole");
            Is(whole.First(e => e.Name == "PackA/data.lua").Crc == Crc32.Of(Encoding.ASCII.GetBytes("local x = 1\n"), 12), "zip's crc");
            byte[] tail = bytes.Skip(bytes.Length - 22).ToArray();
            int asked = 0;
            List<ZipItem> fromTail = Recognize.FromTail(tail, bytes.Length - 22, delegate(long from, long to)
            {
                asked++;
                return bytes.Skip((int)from).Take((int)(to - from + 1)).ToArray();
            });
            Is(asked == 1 && fromTail.Count == 4, "zip's list of files asked for when the tail lacks it");
            string[] folders;
            Is(Recognize.Compare(addons, whole, out folders) == Recognize.Kind.Absent, "nothing in AddOns: absent");
            ZipFile.ExtractToDirectory(zip, addons);
            File.Delete(Path.Combine(addons, "PackA", "tool.exe"));
            Is(Recognize.Compare(addons, whole, out folders) == Recognize.Kind.Same && folders.Length == 2, "same files: same (an .exe the app never puts is not asked)");
            File.WriteAllText(Path.Combine(addons, "PackA", "notes.txt"), "mine");
            Is(Recognize.Compare(addons, whole, out folders) == Recognize.Kind.Same, "a file of one's own beside them: still same");
            File.WriteAllText(Path.Combine(addons, "PackA", "data.lua"), "local x = 2\n");
            Is(Recognize.Compare(addons, whole, out folders) == Recognize.Kind.Differs, "same size, other bytes: differs");
            File.WriteAllText(Path.Combine(addons, "PackA", "data.lua"), "local x = 1\n");
            Directory.Delete(Path.Combine(addons, "PackB"), true);
            Is(Recognize.Compare(addons, whole, out folders) == Recognize.Kind.Differs, "one folder of two: differs");
        }

        // recognize-live <zip url> <AddOns folder>: a real zip's list by Range, compared.
        static void RecognizeLive(string url, string addons, long size)
        {
            Release r = new Release { ZipUrl = url, Tag = "live", Size = size };
            List<ZipItem> index = Recognize.Index(r);
            string[] folders;
            Recognize.Kind kind = Recognize.Compare(addons, index, out folders);
            Console.WriteLine("index " + index.Count + " files; " + kind + " (" + string.Join(", ", folders) + ")");
            Is(index.Count > 0, "live index");
        }

        static void Repos()
        {
            Is(GitHub.ParseRepo("https://github.com/EllesmereGaming/EllesmereUI") == "EllesmereGaming/EllesmereUI", "link");
            Is(GitHub.ParseRepo("github.com/EllesmereGaming/EllesmereUI/releases/tag/v9.7.3") == "EllesmereGaming/EllesmereUI", "link with a path");
            Is(GitHub.ParseRepo("https://github.com/a-b/c.d.git") == "a-b/c.d", ".git");
            Is(GitHub.ParseRepo("git@github.com:owner/name") == "owner/name", "ssh");
            Is(GitHub.ParseRepo("owner/name") == "owner/name", "owner/name");
            Is(GitHub.ParseRepo("https://gitlab.com/owner/name") == null, "not GitHub");
            Is(GitHub.ParseRepo("hello") == null, "not a link");
            Is(GitHub.ParseRepo("owner/..") == null, "dots");
            Is(GitHub.ParseRepo("curseforge:27328") == null, "a CurseForge key is not a repository");
            Is(GitHub.Digest("sha256:AEBFA8A60EE1185760D5C4E779C1DCEB999E1897295D0CDBA90DC740242FFDF2") == "aebfa8a60ee1185760d5c4e779c1dceb999e1897295d0cdba90dc740242ffdf2", "digest");
            Is(GitHub.Digest("md5:12") == null, "other digest");
            Is(Core.CompareVersions("0.3.1", "0.2.5") > 0 && Core.CompareVersions("0.3.0", "0.3.0") == 0 && Core.CompareVersions("v0.10", "0.9.9") > 0 && Core.CompareVersions("0.2", "0.2.1") < 0, "versions");
            Is(Core.Name == "Forever Manager" && Core.RunName == "ForeverManager" && Core.Version == "1.0.8", "its own name, Run key and version (" + Core.RunName + " " + Core.Version + ")");
            Is(Core.Home() == Path.Combine(root, "home") && Core.TestMode, "a test's folder, in test mode");
            Is(Pack.Owns("Morderous/Forever-Addons") && !Pack.Owns("morderous/foreverui") && !Pack.Owns(null), "the token is for the pack's repository alone");
            Environment.SetEnvironmentVariable("FUM_GAME_VERSION", "1.60.1.65432");
            Is(Game.CurseVersion(null) == "1.60.1", "the game's version as CurseForge names it (" + Game.CurseVersion(null) + ")");
            Environment.SetEnvironmentVariable("FUM_GAME_VERSION", null);
        }

        // The secrets the build put in: the fake ones read back, and in the
        // app's exe in no form a reader would see them (UTF-8 or UTF-16).
        static void Masking()
        {
            Is(Secrets.GitHubToken() == FakeToken && Secrets.CurseForgeKey() == FakeCurse, "the fake token and key unmasked");
            Is(!Secrets.HasToken && !Secrets.HasCurseForge, "a test build says it has no real token or key");
            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ForeverManager.exe");
            Is(File.Exists(exe), "the app's exe built beside the tests");
            if (!File.Exists(exe)) return;
            byte[] bytes = File.ReadAllBytes(exe);
            foreach (string secret in new[] { FakeToken, FakeCurse, "FAKE_TEST_TOKEN", "FAKE_TEST_CURSEFORGE" })
                foreach (Encoding enc in new Encoding[] { new UTF8Encoding(false), Encoding.Unicode })
                    Is(IndexOf(bytes, enc.GetBytes(secret)) < 0, "\"" + secret.Substring(0, 15) + "...\" is not in the exe in the clear (" + enc.WebName + ")");
            Is(IndexOf(bytes, Encoding.UTF8.GetBytes("github_pat_")) < 0, "not even the token's prefix");
        }

        static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
        static string Folders(params string[] names)
        {
            return string.Join(",", Installer.FoldersIn(names).Select(f => f.Key + "=" + f.Value).ToArray());
        }

        static void Folders()
        {
            Is(Folders("EllesmereUI/EllesmereUI.toc", "EllesmereUI/Libs/LibStub/LibStub.toc", "EllesmereUI/core.lua",
                "EllesmereUIForeverEssentials/EllesmereUIForeverEssentials_Camelot.toc", "X/X-Mainline.toc")
                == "X/=X,EllesmereUI/=EllesmereUI,EllesmereUIForeverEssentials/=EllesmereUIForeverEssentials", "release layout, tagged .toc, libs inside");
            Is(Folders("Repo-main/Addon/Addon.toc", "Repo-main/README.md") == "Repo-main/Addon/=Addon", "a repository's archive");
            Is(Folders("Repo-main/MyAddon_Camelot.toc", "Repo-main/core.lua") == "Repo-main/=MyAddon", ".toc at the top of the archive");
            Is(Folders("MyAddon.toc", "core.lua") == "=MyAddon", ".toc at the top of the zip");
            Is(Folders("Docs/readme.md", "src/a.lua") == "", "no .toc");
            Is(Folders("A/A.toc", "A/B/B.toc", "C/C.toc") == "A/=A,C/=C", "nested addon stays inside");
            Is(!Installer.SafeFolder("Blizzard_AuctionUI") && !Installer.SafeFolder("..") && !Installer.SafeFolder("a\\b") && Installer.SafeFolder("EllesmereUI"), "safe folders");
            Is(Installer.ForeverInterface("120000, 120001, 16001") == "16001" && Installer.ForeverInterface("11507") == null, "Forever interface");
        }

        static void ReleaseJson()
        {
            string ellesmere = "{\"releases\":[{\"name\":\"EllesmereUI\",\"version\":\"v9.7.3\",\"filename\":\"EllesmereUI-v9.7.3.zip\",\"nolib\":false,\"metadata\":[{\"flavor\":\"mainline\",\"interface\":120000},{\"flavor\":\"forever\",\"interface\":16001}]}]}";
            Is(GitHub.ForeverIn(ellesmere) == "EllesmereUI-v9.7.3.zip", "release.json with forever");
            string mainline = "{\"releases\":[{\"filename\":\"A.zip\",\"nolib\":false,\"metadata\":[{\"flavor\":\"mainline\",\"interface\":120000}]}]}";
            Is(GitHub.ForeverIn(mainline) == null, "release.json without forever");
            string several = "{\"releases\":[{\"filename\":\"A-nolib.zip\",\"nolib\":true,\"metadata\":[{\"flavor\":\"forever\",\"interface\":16001}]},"
                + "{\"filename\":\"A-classic.zip\",\"nolib\":false,\"metadata\":[{\"flavor\":\"classic\",\"interface\":11507}]},"
                + "{\"filename\":\"A.zip\",\"nolib\":false,\"metadata\":[{\"flavor\":\"mainline\",\"interface\":120000},{\"interface\":16001}]}]}";
            Is(GitHub.ForeverIn(several) == "A.zip", "full zip before nolib, Interface alone");
            Is(GitHub.ForeverIn("{\"releases\":[{\"filename\":\"../x.zip\",\"metadata\":[{\"flavor\":\"forever\"}]}]}") == null, "path in filename refused");
        }

        static void Zips()
        {
            List<string> polestar = new List<string> { "Polestar-0.3.1.zip", "Polestar_Recorder-0.3.1.zip" };
            Is(GitHub.ZipByName(polestar, "polestar") == "Polestar-0.3.1.zip", "Polestar's zip by the repository's name");
            Is(GitHub.Like("Polestar-0.3.1.zip", "Polestar-*.zip") && !GitHub.Like("Polestar_Recorder-0.3.1.zip", "Polestar-*.zip"), "zip pattern");
            Is(GitHub.ZipByName(new List<string> { "A-classic.zip", "A-1.0.zip" }, "Other") == "A-1.0.zip", "the zip not named for another game");
            Is(GitHub.ZipByName(new List<string> { "x.zip", "y.zip" }, "Other") == null, "two zips, no way to tell");
            Dictionary<string, string> sums = GitHub.ParseSums("463c0fb15b7ec69cf38637d8dd60570d6c2a8b6253696f0177ae33e3519d599d  Polestar-0.3.1.zip\n3c6027e6dbb3c64f77185fa8c8c0ce89b19699a3cad8964a6cff6ec37f911d8c *Polestar_Recorder-0.3.1.zip\r\n");
            Is(sums.Count == 2 && sums["Polestar-0.3.1.zip"].StartsWith("463c0f") && sums["Polestar_Recorder-0.3.1.zip"].StartsWith("3c6027"), "SHA256SUMS read");
            Release r = new Release { Tag = "v0.3.1" };
            List<Asset> assets = new List<Asset> { new Asset { Name = "Polestar-0.3.1.zip", Url = "u1" }, new Asset { Name = "Polestar_Recorder-0.3.1.zip", Url = "u2" } };
            GitHub.PickZip(r, assets, "Polestar-*.zip", "polestar");
            Is(r.ZipUrl == "u1", "PickZip by pattern");
            Is(Throws(delegate { GitHub.PickZip(new Release(), assets, "Nope-*.zip", "polestar"); }, "no zip named like"), "PickZip with no match says so");
        }

        static string MakeZip(string name, Dictionary<string, string> files)
        {
            string path = Path.Combine(root, name);
            if (File.Exists(path)) File.Delete(path);
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (KeyValuePair<string, string> f in files)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(f.Key);
                    if (f.Value == null) continue;
                    using (StreamWriter w = new StreamWriter(entry.Open(), new UTF8Encoding(false))) w.Write(f.Value);
                }
            return path;
        }

        static void PutAndUpdate()
        {
            string addons = Path.Combine(root, "game", "Interface", "AddOns");
            Directory.CreateDirectory(Path.Combine(addons, "Keep"));
            File.WriteAllText(Path.Combine(addons, "Keep", "Keep.toc"), "## Title: by hand");
            Directory.CreateDirectory(Path.Combine(addons, "Old"));
            File.WriteAllText(Path.Combine(addons, "Old", "Old.toc"), "## Title: old");

            string v1 = MakeZip("v1.zip", new Dictionary<string, string>
            {
                { "A/A.toc", "## Interface: 16001\n## Title: |cff00ff00A|r\n## Version: 1" },
                { "A/a.lua", "v1" },
                { "A/evil.exe", "MZ" },
                { "A/../../escape.lua", "out" },
                { "B/B_Camelot.toc", "## Interface: 16001" },
            });
            Placed first = Installer.Put(addons, v1, new[] { "Old" }, "Test v0");
            Is(first.Folders.Length == 2 && Directory.Exists(Path.Combine(addons, "A")) && Directory.Exists(Path.Combine(addons, "B")), "v1 in");
            Is(!Directory.Exists(Path.Combine(addons, "Old")), "the old version's leftover folder out");
            Is(Directory.Exists(Path.Combine(addons, "Keep")), "an addon put in by hand stays");
            Is(!File.Exists(Path.Combine(addons, "A", "evil.exe")) && first.Skipped == 2, "exe and path escape left out (" + first.Skipped + ")");
            Is(!File.Exists(Path.Combine(root, "game", "Interface", "escape.lua")) && !File.Exists(Path.Combine(root, "game", "escape.lua")), "nothing outside its folder");
            Toc toc = Installer.ReadToc(Path.Combine(addons, "A"));
            Is(toc != null && toc.Title == "A" && toc.Version == "1" && toc.Interface == "16001", "toc read, colours off");
            Is(Installer.ReadToc(Path.Combine(addons, "B")) != null, "tagged toc read");
            Is(Directory.GetDirectories(Path.Combine(root, "bin")).Length == 1, "old folders to the bin together");
            Is(Directory.GetDirectories(Path.Combine(root, "game", "Interface")).Length == 1, "no staging left beside AddOns");

            string v2 = MakeZip("v2.zip", new Dictionary<string, string>
            {
                { "A/A.toc", "## Interface: 16001\n## Version: 2" },
                { "A/a.lua", "v2" },
            });
            Placed second = Installer.Put(addons, v2, first.Folders, "Test v1");
            Is(second.Folders.Length == 1 && File.ReadAllText(Path.Combine(addons, "A", "a.lua")) == "v2", "v2 replaced v1");
            Is(!Directory.Exists(Path.Combine(addons, "B")), "a folder v2 dropped is gone");
            Is(Directory.Exists(Path.Combine(addons, "Keep")), "by hand still there");

            string left = Installer.Remove(addons, second.Folders, "Test v2");
            Is(left == null && !Directory.Exists(Path.Combine(addons, "A")) && Directory.Exists(Path.Combine(addons, "Keep")), "uninstall");
            Is(Directory.GetDirectories(Path.Combine(root, "bin")).Length == 3, "each step's old copy in the bin");
        }

        static void RollBack()
        {
            string addons = Path.Combine(root, "game2", "Interface", "AddOns");
            Directory.CreateDirectory(Path.Combine(addons, "A"));
            File.WriteAllText(Path.Combine(addons, "A", "A.toc"), "old");
            Directory.CreateDirectory(Path.Combine(addons, "Z"));
            File.WriteAllText(Path.Combine(addons, "Z", "Z.toc"), "old z");
            string zip = MakeZip("v3.zip", new Dictionary<string, string> { { "A/A.toc", "new" }, { "Z/Z.toc", "new z" } });
            bool threw = false;
            using (FileStream held = new FileStream(Path.Combine(addons, "Z", "Z.toc"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { Installer.Put(addons, zip, new[] { "A", "Z" }, "Locked"); }
                catch (Exception why) { threw = why.Message.Contains("could not be moved"); }
            }
            Is(threw, "a file in use stops it");
            Is(File.ReadAllText(Path.Combine(addons, "A", "A.toc")) == "old" && File.ReadAllText(Path.Combine(addons, "Z", "Z.toc")) == "old z", "everything put back as it was");
            Is(Directory.GetDirectories(Path.Combine(root, "game2", "Interface")).Length == 1, "no staging left after a roll back");
        }

        // The rule: only the folders this app put in AddOns are ever touched.
        // Folders already there that did not come from an item of ours stay
        // as they are, when the guide packs go in, when they update, when
        // they are uninstalled; an addon's install never moves one without a yes.
        static void NotTheirs()
        {
            string addons = Path.Combine(root, "game7", "Interface", "AddOns");
            Directory.CreateDirectory(addons);
            Action<string, string> folder = delegate(string name, string toc)
            {
                Directory.CreateDirectory(Path.Combine(addons, name));
                File.WriteAllText(Path.Combine(addons, name, name + ".toc"), toc);
            };
            Func<string, string> read = delegate(string name) { string f = Path.Combine(addons, name, name + ".toc"); return File.Exists(f) ? File.ReadAllText(f) : null; };
            folder("ForeverGuidesPack_Mine", "## Title: my own pack");
            folder("ForeverGuidesPack_Alpha", "## Title: Alpha put in by hand");
            folder("SomeAddon", "## Title: someone else's");
            int bin = Directory.Exists(Path.Combine(root, "bin")) ? Directory.GetDirectories(Path.Combine(root, "bin")).Length : 0;

            string v1 = MakeZip("packs-v1.zip", new Dictionary<string, string>
            {
                { "ForeverGuidesPack_Alpha/ForeverGuidesPack_Alpha.toc", "## Title: Alpha from the pack v1" },
                { "ForeverGuidesPack_Beta/ForeverGuidesPack_Beta.toc", "## Title: Beta v1" },
            });
            Installer.OnlyGuides(v1);
            Placed first = Installer.Put(addons, v1, null, "packs v1", Installer.Foreign.Skip);
            Is(first.Folders.Length == 1 && first.Folders[0] == "ForeverGuidesPack_Beta", "the packs: only the folder that was not there goes in (" + string.Join(",", first.Folders) + ")");
            Is(first.Kept != null && first.Kept.Length == 1 && first.Kept[0] == "ForeverGuidesPack_Alpha", "the one already there is said, and kept");
            Is(read("ForeverGuidesPack_Alpha") == "## Title: Alpha put in by hand", "a ForeverGuidesPack_ folder put there by hand is left as it was");
            Is(read("ForeverGuidesPack_Mine") == "## Title: my own pack" && read("SomeAddon") == "## Title: someone else's", "the others are not touched");

            // An update of the packs: ours replaced, theirs still as they were.
            string v2 = MakeZip("packs-v2.zip", new Dictionary<string, string>
            {
                { "ForeverGuidesPack_Alpha/ForeverGuidesPack_Alpha.toc", "## Title: Alpha from the pack v2" },
                { "ForeverGuidesPack_Beta/ForeverGuidesPack_Beta.toc", "## Title: Beta v2" },
                { "ForeverGuidesPack_New/ForeverGuidesPack_New.toc", "## Title: New v2" },
            });
            Placed second = Installer.Put(addons, v2, first.Folders, "packs v2", Installer.Foreign.Skip);
            Is(second.Folders.Length == 2 && read("ForeverGuidesPack_Beta") == "## Title: Beta v2" && read("ForeverGuidesPack_New") == "## Title: New v2", "the update: ours replaced, a new one in");
            Is(read("ForeverGuidesPack_Alpha") == "## Title: Alpha put in by hand" && read("ForeverGuidesPack_Mine") == "## Title: my own pack", "the update leaves theirs as they were");

            // Uninstalled: ours go, theirs stay.
            Installer.Remove(addons, second.Folders, "packs v2");
            Is(read("ForeverGuidesPack_Beta") == null && read("ForeverGuidesPack_New") == null, "uninstall takes ours");
            Is(read("ForeverGuidesPack_Alpha") != null && read("ForeverGuidesPack_Mine") != null && read("SomeAddon") != null, "uninstall leaves theirs");

            // A pack whose every folder is someone else's: nothing goes in, nothing changes.
            string only = MakeZip("packs-only.zip", new Dictionary<string, string> { { "ForeverGuidesPack_Mine/ForeverGuidesPack_Mine.toc", "## Title: theirs from us" } });
            Is(Throws(delegate { Installer.Put(addons, only, null, "packs only", Installer.Foreign.Skip); }, "not put there by"), "a pack that only has folders already there is refused");
            Is(read("ForeverGuidesPack_Mine") == "## Title: my own pack", "and the folder is as it was");

            // An addon: refused with the folders named, then put in only with a yes.
            string addon = MakeZip("someaddon.zip", new Dictionary<string, string> { { "SomeAddon/SomeAddon.toc", "## Title: from the pack" } });
            InTheWay said = null;
            try { Installer.Put(addons, addon, null, "someaddon"); }
            catch (InTheWay why) { said = why; }
            Is(said != null && said.Folders.Length == 1 && said.Folders[0] == "SomeAddon", "an addon's folder already there: refused, said by name");
            Is(read("SomeAddon") == "## Title: someone else's", "and left as it was");
            Is(Directory.GetDirectories(Path.Combine(root, "game7", "Interface")).Length == 1, "no staging left after a refusal");
            Is((Directory.Exists(Path.Combine(root, "bin")) ? Directory.GetDirectories(Path.Combine(root, "bin")).Length : 0) == bin + 2, "only our own old copies went to the bin so far");
            Placed yes = Installer.Put(addons, addon, null, "someaddon", Installer.Foreign.Replace);
            Is(yes.Folders.Length == 1 && read("SomeAddon") == "## Title: from the pack", "with a yes, it takes its place (to the Recycle Bin)");
            // Its own folder from before: no question.
            Is(!Throws(delegate { Installer.Put(addons, addon, yes.Folders, "someaddon again"); }, null), "an addon's own folder is replaced without a question");
        }

        static void Store()
        {
            Pack.Addons = null; Pack.Text = null;
            FreshList();
            List<TrackedAddon> list = ForeverManager.Store.Load();
            Is(list.Count == 0, "before the pack is read, the list is empty (" + list.Count + ")");
            Pack.Use(PackText(), false);
            list = ForeverManager.Store.Load();
            Is(list.Count == 13, "then it holds the pack's 13 (" + list.Count + ")");
            list[0].Tag = "foreverui/v0.1.0";
            list[0].Folders = new[] { "ForeverUI", "ForeverUI_XPBar" };
            list[0].Auto = false;
            list[0].Trusted = true;
            TrackedAddon quest = list.Find(a => a.IsCurse);
            quest.Tag = "QuestCompletist 3.8";
            quest.Folders = new[] { "QuestCompletist" };
            quest.Slug = "quest-completist";
            quest.Author = "test-author";
            list.RemoveAll(a => a.Title == "Dialogue UI");
            list.Add(new TrackedAddon { Repo = "someone/by-hand", Tag = "v1", Folders = new[] { "ByHand" } });
            ForeverManager.Store.Save(list);
            List<TrackedAddon> again = ForeverManager.Store.Load();
            TrackedAddon ui = again.Find(a => a.Title == "ForeverUI"), q = again.Find(a => a.IsCurse);
            Is(ui != null && ui.Tag == "foreverui/v0.1.0" && ui.Folders.Length == 2 && !ui.Auto && ui.Trusted && ui.Private, "the list kept");
            Is(q != null && q.CurseId == 27328 && q.Repo == "curseforge:27328" && q.Slug == "quest-completist" && q.Author == "test-author" && q.Tag == "QuestCompletist 3.8" && q.Source == "pack",
                "a CurseForge project kept with its id, slug and author");
            Is(!again.Exists(a => a.Title == "Dialogue UI"), "an extra taken off stays off");
            Is(again.Exists(a => a.Repo == "someone/by-hand" && a.Source == null), "one added by hand kept, as added by hand");
            // ForeverUI Manager's polestar.guide guides are not read here.
            Core.WriteAll(Path.Combine(Core.Home(), "addons.json"), "{\"format\":1,\"addons\":[{\"kind\":\"pack\",\"id\":\"lightforge\",\"tag\":\"1.0.0\",\"folders\":[\"PolestarPack_lightforge\"]}]}");
            Is(!ForeverManager.Store.Load().Exists(a => a.Repo != null && a.Repo.StartsWith("pack:")), "a polestar.guide guide row is left out");
            Pack.Addons = null; Pack.Text = null;
            FreshList();
        }

        // The pack's pack.json (a copy of the real one).
        static string PackText()
        {
            return File.ReadAllText(Fixture("pack.json"), Encoding.UTF8);
        }

        static void FreshList()
        {
            string path = Path.Combine(Core.Home(), "addons.json");
            if (File.Exists(path)) File.Delete(path);
            Core.SetSetting("starters", null);
            Core.SetSetting("unticked", null);
        }

        // The window's place kept in settings.ini, and how often it checks.
        static void Window()
        {
            Rectangle r = new Rectangle(120, -8, 1100, 700);
            Is(WindowPlace.Format(r) == "120,-8,1100,700" && WindowPlace.Parse("120,-8,1100,700") == r, "a place written and read back");
            Is(WindowPlace.Parse("") == null && WindowPlace.Parse("1,2,3") == null && WindowPlace.Parse("a,b,c,d") == null && WindowPlace.Parse("1,2,0,5") == null && WindowPlace.Parse("1,2,-4,5") == null,
                "a place that is not one: none");
            Core.SetSetting("window", WindowPlace.Format(r));
            Core.SetSetting("maximized", "1");
            string ini = File.ReadAllText(Path.Combine(Core.Home(), "settings.ini"));
            Is(ini.Contains("window=120,-8,1100,700") && ini.Contains("maximized=1") && WindowPlace.Parse(Core.Setting("window", "")) == r, "kept in settings.ini as window= and maximized=");
            Core.SetSetting("window", null);
            Core.SetSetting("maximized", null);

            Rectangle[] one = { new Rectangle(0, 0, 1920, 1040) };
            Size min = new Size(796, 489);
            Is(WindowPlace.Fit(new Rectangle(100, 80, 1000, 700), one, min) == new Rectangle(100, 80, 1000, 700), "on the screen: as it was");
            Is(WindowPlace.Fit(new Rectangle(-6000, 300, 1000, 700), one, min) == new Rectangle(460, 170, 1000, 700), "off every screen: centred on the main one");
            Is(WindowPlace.Fit(new Rectangle(1500, 900, 1000, 700), one, min) == new Rectangle(920, 340, 1000, 700), "half off the screen: moved in whole");
            Is(WindowPlace.Fit(new Rectangle(-20, -20, 4000, 3000), one, min) == new Rectangle(0, 0, 1920, 1040), "bigger than the screen: the screen's size");
            Is(WindowPlace.Fit(new Rectangle(50, 50, 300, 200), one, min) == new Rectangle(50, 50, 796, 489), "smaller than the least: the least");
            Rectangle[] two = { new Rectangle(0, 0, 1920, 1040), new Rectangle(1920, 0, 1280, 984) };
            Is(WindowPlace.Fit(new Rectangle(2000, 100, 1000, 700), two, min) == new Rectangle(2000, 100, 1000, 700), "on the second screen: stays there");
            Is(WindowPlace.Fit(new Rectangle(1800, 100, 1400, 700), two, min) == new Rectangle(1920, 100, 1280, 700), "mostly on the second screen: fitted to it");
            Rectangle[] gone = { new Rectangle(0, 0, 1920, 1040) };
            Is(WindowPlace.Fit(new Rectangle(2200, 200, 1000, 700), gone, min) == new Rectangle(460, 170, 1000, 700), "its screen unplugged: on the main one");

            // Maximized: everything in the middle 60%, never narrower than the least width.
            Is(MainForm.Column(new Size(1200, 700), false, 780) == new Rectangle(0, 0, 1200, 700), "not maximized: the whole window, as before");
            Is(MainForm.Column(new Size(1920, 1000), true, 780) == new Rectangle(384, 0, 1152, 1000), "maximized: 20% each side, 60% in the middle");
            Is(MainForm.Column(new Size(1000, 700), true, 780) == new Rectangle(110, 0, 780, 700), "60% under the least width: the least width, smaller margins");
            Is(MainForm.Column(new Size(700, 500), true, 780) == new Rectangle(0, 0, 700, 500), "narrower than the least: the whole window");
            Is(Core.Author == "morderous" && Core.AuthorUrl == "https://github.com/morderous" && (Core.Name + " v" + Core.Version + " · made by " + Core.Author).Length <= 63, "credits: morderous, its GitHub page, a tray tooltip that fits");

            Is(MainForm.CheckEvery == TimeSpan.FromHours(4), "it checks every 4 hours (" + MainForm.CheckEvery + ")");
            DateTime t = new DateTime(2026, 11, 7, 12, 0, 0);
            Is(MainForm.Due(DateTime.MinValue, t) && !MainForm.Due(t, t.AddHours(3).AddMinutes(59)) && MainForm.Due(t, t.AddHours(4)) && MainForm.Due(t, t.AddHours(9)) && !MainForm.Due(t, t.AddMinutes(30)),
                "a check is due after 4 hours, not before (not after 30 minutes, nor 6 hours)");
        }

        static void PackList()
        {
            string text = PackText();
            KnownRepo[] items = Pack.Parse(text);
            Is(items.Length == 13, "pack.json: 13 addons (" + items.Length + ")");
            KnownRepo[] own = items.Where(k => k.Private).ToArray();
            Is(own.Length == 4 && own.All(k => k.Repo == Own && k.Series != null && k.Zip != null), "4 from the pack's own repository, each with its series and zip");
            Is(own.Select(k => k.Title).SequenceEqual(new[] { "ForeverUI", "ForeverGuides", "ForeverMap", "ForeverGuides Packs" }), "ForeverUI, ForeverGuides, ForeverMap and the packs, in order");
            Is(own[3].GuidesOnly && !own[0].GuidesOnly, "the packs come as guides only");
            KnownRepo quest = items.FirstOrDefault(k => k.IsCurse);
            Is(quest != null && quest.CurseId == 27328 && quest.Repo == "curseforge:27328" && quest.Title == "Quest Completist" && !quest.Required && !quest.Ticked && quest.Recommended, "Quest Completist from CurseForge, project 27328, recommended and unticked");
            Is(items.Count(k => !k.Private && !k.IsCurse) == 8, "8 public GitHub ones");
            Is(items.Count(k => k.Required) == 4 && items.Where(k => k.Required).All(k => k.Ticked && k.Private), "only the 4 Forever ones required, all ticked");
            KnownRepo elle = items.FirstOrDefault(k => k.Repo == "EllesmereGaming/EllesmereUI");
            Is(elle != null && !elle.Required && !elle.Ticked && !elle.Recommended, "EllesmereUI optional and unticked");
            Is(items.Where(k => !k.Private).All(k => !k.Ticked && !k.Required), "every one but the 4 Forever ones comes unticked");
            Is(items.Where(k => k.Recommended).Select(k => k.Title).SequenceEqual(new[] { "Quest Completist", "Forever Quest Tint" }), "Quest Completist and Forever Quest Tint recommended, nothing else");
            KnownRepo[] voices = items.Where(k => k.Repo == "rusty-key/spoken-wow").ToArray();
            Is(voices.Length == 5 && voices.All(k => k.Series != null && k.Zip != null && !k.Ticked), "Spoken and its 4 voice packs, unticked");
            KnownRepo[] rec = Pack.Parse("{\"format\":1,\"addons\":[{\"repo\":\"a/b\",\"recommended\":true},{\"repo\":\"c/d\",\"recommended\":true,\"ticked\":true},{\"repo\":\"e/f\",\"recommended\":false}]}");
            Is(rec[0].Recommended && !rec[0].Required && rec[0].Ticked && rec[1].Recommended && rec[1].Ticked && !rec[2].Recommended, "recommended read as given, ticked as \"ticked\" says");
            Is(items.Select(k => k.Key).Distinct().Count() == 13 && items.All(k => k.Key.IndexOf(',') < 0), "13 keys, none with a comma");
            Is(TrackedAddon.SameKey("Rusty-Key/Spoken-WoW " + Uri.EscapeDataString(@"^spoken/v\d"), TrackedAddon.KeyOf("rusty-key/spoken-wow", @"^spoken/v\d")), "the same key in another case");

            Is(Throws(delegate { Pack.Parse("{\"format\":2,\"addons\":[{\"repo\":\"a/b\"}]}"); }, "format"), "a pack of another format is refused");
            Is(Throws(delegate { Pack.Parse("{\"format\":1,\"addons\":[{\"repo\":\"not a repo\"}]}"); }, "no addon"), "a pack with no addon is refused");
            KnownRepo[] odd = Pack.Parse("{\"format\":1,\"addons\":[{\"title\":\"No zip\",\"series\":\"^a/v\"},{\"title\":\"No series\",\"zip\":\"A-*.zip\"},{\"curseforge\":\"abc\"},{\"curseforge\":-4},"
                + "{\"repo\":\"someone/public\",\"title\":\"Public\"},{\"title\":\"Ok\",\"series\":\"^a/v\",\"zip\":\"A-*.zip\"},{\"curseforge\":27328},{\"curseforge\":27328,\"title\":\"twin\"},{\"repo\":\"e/f\",\"zip\":\"../x.zip\"}]}");
            Is(odd.Length == 3 && odd[0].Repo == "someone/public" && !odd[0].Private && odd[1].Private && odd[1].Repo == Own && odd[2].IsCurse && odd[2].Title == null,
                "bad entries left out: own ones without series or zip, a CurseForge id that is not one, a twin, a path in a zip (" + odd.Length + ")");

            // A new list from the pack.
            FreshList();
            Pack.Use(text, true);
            Is(File.Exists(Path.Combine(Core.Home(), "pack.json")), "the pack read is kept for offline");
            List<TrackedAddon> list = ForeverManager.Store.Load();
            Is(list.Count == 13 && list.All(a => a.Source == "pack"), "a new list holds the pack's 13 (" + list.Count + ")");
            Is(list[0].Title == "ForeverUI" && list[0].Private && list[0].Required, "ForeverUI first, from the pack's own repository, required");
            TrackedAddon q = list.Find(a => a.IsCurse);
            Is(q != null && !q.Want && !q.Auto && !q.Required && q.Recommended && q.Kind == "curseforge", "Quest Completist on the list, recommended, unticked");
            TrackedAddon tint = list.Find(a => a.Title == "Forever Quest Tint");
            Is(tint != null && tint.Recommended && !tint.Want && !tint.Required, "Forever Quest Tint recommended, unticked");
            Is(list.Count(a => a.Recommended) == 2, "two recommended on the list");
            List<TrackedAddon> unticked = list.FindAll(a => !a.Want);
            Is(unticked.Count == 9 && unticked.All(a => !a.Private && !a.Auto && !a.Required), "the 9 extras: unticked, no update by themselves");
            // What Install all puts in on a first start: ticked (or required) and not installed.
            List<TrackedAddon> firstInstall = list.FindAll(a => a.Tag == null && (a.Want || a.Required));
            Is(firstInstall.Count == 4 && firstInstall.All(a => a.Private && a.Required), "a first start installs only the 4 Forever ones (" + string.Join(",", firstInstall.Select(a => a.Name).ToArray()) + ")");
            TrackedAddon ell = list.Find(a => a.Repo == "EllesmereGaming/EllesmereUI");
            Is(ell != null && !ell.Required && !ell.Want, "EllesmereUI on the list, optional, unticked");

            // Taken off: an extra stays off, a required one comes back.
            list.RemoveAll(a => a.Title == "Forever Quest Tint" || a.Title == "ForeverMap" || a.IsCurse);
            ForeverManager.Store.Save(list);
            List<TrackedAddon> again = ForeverManager.Store.Load();
            Is(again.Count == 11 && !again.Exists(a => a.Title == "Forever Quest Tint") && !again.Exists(a => a.IsCurse), "extras taken off stay off (" + again.Count + ")");
            Is(again.Exists(a => a.Title == "ForeverMap" && a.Required && a.Private), "a required one comes back");

            // The pack changes: a new CurseForge project joins.
            string quest1 = "{\"curseforge\": 27328, \"title\": \"Quest Completist\", \"ticked\": false, \"recommended\": true},";
            string changed = text.Replace(quest1, quest1 + "\n    { \"curseforge\": 900001, \"title\": \"Example Addon\" },");
            Is(changed != text && Pack.Use(changed, true), "a changed pack is taken");
            List<TrackedAddon> joined = ForeverManager.Store.Seed(again);
            Is(joined.Count == 1 && joined[0].CurseId == 900001 && joined[0].Want && !joined[0].Recommended, "a new CurseForge project joins, ticked (no ticked: false)");
            Is(ForeverManager.Store.Seed(again).Count == 0, "nothing joins twice");

            OptionalNow(text);

            // Never read: an empty list.
            Pack.Addons = null; Pack.Text = null;
            Is(KnownRepo.Addons().Length == 0, "never read: nothing offered");
            FreshList();
            File.Delete(Path.Combine(Core.Home(), "pack.json"));
        }

        // pack.json as Forever Manager 1.0.0 had it: EllesmereUI required, the
        // extras ticked but the voice packs.
        const string OldPack = @"{ ""format"": 1, ""addons"": [
    { ""title"": ""ForeverUI"", ""required"": true, ""series"": ""^foreverui/v\\d"", ""zip"": ""ForeverUI-*.zip"" },
    { ""title"": ""ForeverGuides"", ""required"": true, ""series"": ""^foreverguides/v\\d"", ""zip"": ""ForeverGuides-*.zip"" },
    { ""title"": ""ForeverMap"", ""required"": true, ""series"": ""^forevermap/v\\d"", ""zip"": ""ForeverMap-*.zip"" },
    { ""title"": ""ForeverGuides Packs"", ""required"": true, ""series"": ""^packs/v\\d"", ""zip"": ""ForeverGuidesPacks-*.zip"", ""guidesOnly"": true },
    { ""repo"": ""EllesmereGaming/EllesmereUI"", ""title"": ""EllesmereUI"", ""required"": true },
    { ""curseforge"": 27328, ""title"": ""Quest Completist"" },
    { ""repo"": ""alexdia25/forever-quest-tint"", ""title"": ""Forever Quest Tint"" },
    { ""repo"": ""Peterodox/YUI-Dialogue"", ""title"": ""Dialogue UI"" },
    { ""repo"": ""rusty-key/spoken-wow"", ""title"": ""Spoken"", ""series"": ""^spoken/v\\d"", ""zip"": ""Spoken-*.zip"" },
    { ""repo"": ""rusty-key/spoken-wow"", ""title"": ""Spoken Quests Audio"", ""ticked"": false, ""series"": ""^quests-audio/v\\d"", ""zip"": ""SpokenQuestsAudioAll-*.zip"" }
] }";

        static TrackedAddon Elle(List<TrackedAddon> list) { return list.Find(a => a.Repo == "EllesmereGaming/EllesmereUI"); }

        // The pack stops requiring EllesmereUI (1.0.1): optional, never taken out by itself.
        static void OptionalNow(string text)
        {
            string old = OldPack.Replace("\r\n", "\n");
            Is(Pack.Parse(old).Count(k => k.Required) == 5, "the old pack: 5 required");

            // Installed while required: stays installed and updated, now optional.
            FreshList();
            Pack.Use(old, true);
            List<TrackedAddon> list = ForeverManager.Store.Load();
            TrackedAddon e = Elle(list);
            Is(e != null && e.Required && e.Want, "under the old pack EllesmereUI is required");
            e.Tag = "v9.7.3"; e.Asset = "EllesmereUI-v9.7.3.zip"; e.Folders = new[] { "EllesmereUI" }; e.Trusted = true;
            ForeverManager.Store.Save(list);
            Pack.Use(text, true);
            ForeverManager.Store.Seed(list);
            e = Elle(list);
            Is(e != null && !e.Required && e.Tag == "v9.7.3" && e.Folders != null && e.Folders[0] == "EllesmereUI" && e.Auto, "the new pack: EllesmereUI optional, still installed, still updated");
            TrackedAddon q = list.Find(a => a.IsCurse);
            Is(q != null && !q.Want && q.Recommended, "an extra the old pack ticked, not installed: unticked now");
            List<TrackedAddon> back = ForeverManager.Store.Load();
            Is(Elle(back) != null && !Elle(back).Required && Elle(back).Tag == "v9.7.3", "the same after a restart");
            Is(back.FindAll(a => a.Tag == null && (a.Want || a.Required)).All(a => a.Private), "Install all would put in only the Forever ones");
            // Optional: off the list for good, once taken off.
            back.RemoveAll(a => a.Repo == "EllesmereGaming/EllesmereUI");
            ForeverManager.Store.Save(back);
            Is(Elle(ForeverManager.Store.Load()) == null, "EllesmereUI taken off the list stays off");

            // Required but never installed: unticked, so Install all leaves it out.
            FreshList();
            Core.SetSetting("unticked", null);
            Pack.Use(old, true);
            list = ForeverManager.Store.Load();
            Pack.Use(text, true);
            ForeverManager.Store.Seed(list);
            e = Elle(list);
            Is(e != null && !e.Required && !e.Want && e.Tag == null, "required before, not installed: optional and unticked");
            // A tick given by hand after that is kept.
            e.Want = true;
            ForeverManager.Store.Save(list);
            Is(Elle(ForeverManager.Store.Load()).Want, "ticked again by hand: kept");

            // 1.0.0 read the new pack first (required gone, still ticked), then 1.0.1 starts.
            FreshList();
            Core.SetSetting("unticked", null);
            Pack.Use(old, true);
            list = ForeverManager.Store.Load();
            foreach (TrackedAddon a in list) a.Required = false;
            ForeverManager.Store.Save(list);
            Core.SetSetting("unticked", null);
            Pack.Use(text, true);
            list = ForeverManager.Store.Load();
            e = Elle(list);
            Is(e != null && !e.Required && !e.Want, "a 1.0.0 list that kept EllesmereUI ticked: unticked by 1.0.1");
            Is(list.FindAll(a => a.Tag == null && (a.Want || a.Required)).Count == 4, "and Install all puts in the 4 Forever ones only");

            Core.SetSetting("unticked", null);
            FreshList();
        }

        static void OwnReleases()
        {
            List<string> asked = new List<string>();
            bool tokenEach = true, rawPack = false;
            GitHub.Forget();
            try
            {
                string releases = File.ReadAllText(Fixture("releases.json"), Encoding.UTF8);
                GitHub.FakeApi = delegate(string path) { throw new Exception("the public API was asked for " + path); };
                GitHub.FakePrivate = delegate(string path, string token, string accept)
                {
                    asked.Add(path);
                    tokenEach &= token == FakeToken;
                    if (path == "repos/" + Own + "/contents/pack.json") { rawPack = accept == "application/vnd.github.raw+json"; return PackText(); }
                    if (path.StartsWith("repos/" + Own + "/releases?")) return releases;
                    throw new PrivateMissing("404");
                };
                Is(Pack.Download() == PackText() && rawPack && asked[0] == "repos/" + Own + "/contents/pack.json", "pack.json read through the API, raw, with the build's token");
                KnownRepo[] own = Pack.Parse(PackText()).Where(k => k.Private).ToArray();
                Release ui = GitHub.Latest(own[0]);
                Is(ui.Tag == "foreverui/v0.1.0" && ui.ZipName == "ForeverUI-0.1.0.zip" && ui.Private && ui.Sha256 == new string('1', 64) && ui.Size == 480000,
                    "ForeverUI's newest of its series, sha256 from the API (" + ui.Tag + ")");
                Is(ui.ZipUrl == "https://api.github.com/repos/" + Own + "/releases/assets/9001", "its zip by its API address (" + ui.ZipUrl + ")");
                Release fg = GitHub.Latest(own[1]), fm = GitHub.Latest(own[2]), packs = GitHub.Latest(own[3]);
                Is(fg.Tag == "foreverguides/v0.1.0" && fm.Tag == "forevermap/v0.7.5-fm.1" && packs.Tag == "packs/v1" && packs.ZipName == "ForeverGuidesPacks-1.zip", "the others by their own series");
                Is(tokenEach && asked.Count(x => x.Contains("/releases")) == 1, "one call for the four, each with the token (" + asked.Count + ")");
                int calls = GitHub.Calls;
                SelfUpdate.Offer none = SelfUpdate.Check();
                Is(none == null && GitHub.Calls == calls, "the Manager's own release (1.0.0, not newer) read from the same call, no call more");

                // The token goes nowhere else.
                Is(Throws(delegate { GitHub.PrivateApi("repos/someone/else/contents/pack.json", null); }, "only to"), "the token is never sent for another repository");
                Is(Throws(delegate { GitHub.PrivateApi("user/repos", null); }, "only to"), "nor for anything else of GitHub's");
                Is(Throws(delegate { GitHub.PrivateLatest("someone/else", @"^v\d", "A-*.zip", false); }, "only for"), "no private release of another repository");
                Is(Throws(delegate { Net.Download("https://github.com/x/y/releases/download/v1/a.zip", Path.Combine(root, "no.zip"), null, null, 0, false, FakeToken); }, "only through GitHub's API"),
                    "the token is never sent anywhere but GitHub's API");
                Is(Throws(delegate { Net.Download("https://api.github.com/repos/someone/else/releases/assets/1", Path.Combine(root, "no.zip"), null, null, 0, false, FakeToken); }, "own downloads"),
                    "nor to another repository's downloads");
                Is(Net.IsApi("https://api.github.com/repos/a/b/releases/assets/1") && !Net.IsApi("http://api.github.com/x") && !Net.IsApi("https://api.github.com.evil.example/x"), "only https://api.github.com counts as the API");

                // No digest: not taken.
                GitHub.Forget();
                releases = "[{\"tag_name\":\"foreverui/v0.2.0\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"ForeverUI-0.2.0.zip\",\"url\":\"https://api.github.com/repos/" + Own + "/releases/assets/1\",\"size\":10}]}]";
                Is(Throws(delegate { GitHub.Latest(own[0]); }, "no sha256"), "a release of its own without GitHub's sha256 is refused");

                // A fake token (no token.txt at build time): said, never sent.
                GitHub.FakePrivate = null;
                GitHub.Forget();
                Is(Throws(delegate { Pack.Download(); }, "the friends' server did not answer"), "a build without the token (1.0.8 on) says the server did not answer");
                Environment.SetEnvironmentVariable("FUM_NO_API", "1");
                Is(Throws(delegate { GitHub.Latest(own[0]); }, "no other way in"), "with the API off, a release of its own says why");
                Environment.SetEnvironmentVariable("FUM_NO_API", null);
                // FUM_PACK and FUM_PRIVATE_RELEASES (the window's tests): files instead of the API.
                Environment.SetEnvironmentVariable("FUM_PACK", Fixture("pack.json"));
                Environment.SetEnvironmentVariable("FUM_PRIVATE_RELEASES", Fixture("releases.json"));
                Is(Pack.Download() == PackText() && GitHub.Latest(own[2]).Tag == "forevermap/v0.7.5-fm.1", "the window's tests read files instead");
                string log = File.Exists(Path.Combine(Core.Home(), "log.txt")) ? File.ReadAllText(Path.Combine(Core.Home(), "log.txt")) : "";
                Is(!log.Contains(FakeToken) && !log.Contains("FAKE_TEST"), "the token is never in the log");
            }
            finally
            {
                GitHub.FakeApi = null;
                GitHub.FakePrivate = null;
                GitHub.Forget();
                Environment.SetEnvironmentVariable("FUM_NO_API", null);
                Environment.SetEnvironmentVariable("FUM_PACK", null);
                Environment.SetEnvironmentVariable("FUM_PRIVATE_RELEASES", null);
            }
        }
        // The friends' server (1.0.7): its manifest stands for the private
        // repository's releases, GitHub only while it does not answer.
        static string FriendsManifest(string extra)
        {
            Func<string, string, string, long, string, string> rel = delegate(string tag, string name, string path, long size, string published)
            {
                return "{\"tag\":\"" + tag + "\",\"name\":\"" + tag + "\",\"notes\":\"n\",\"published\":\"" + published + "\",\"prerelease\":false,"
                    + "\"assets\":[{\"name\":\"" + name + "\",\"path\":\"" + path + "\",\"size\":" + size + ",\"sha256\":\"" + new string('c', 64) + "\"}]}";
            };
            return "{\"format\":1,\"updated\":\"2026-11-13T00:00:00Z\",\"releases\":["
                + rel("foreverui/v0.1.6", "ForeverUI-0.1.6.zip", "foreverui/ForeverUI-0.1.6.zip", 5000, "2026-11-13T02:00:00Z") + ","
                + rel("manager/v1.0.9", "ForeverManager-1.0.9.exe", "manager/ForeverManager-1.0.9.exe", 300000, "2026-11-13T01:00:00Z") + ","
                + rel("foreverui/v0.1.5", "ForeverUI-0.1.5.zip", "foreverui/ForeverUI-0.1.5.zip", 4000, "2026-11-12T01:00:00Z") + ","
                + rel("forevermap/v0.7.5-fm.3", "ForeverMap-0.7.5-fm.3.zip", "forevermap/../ForeverMap-0.7.5-fm.3.zip", 4000, "2026-11-12T00:00:00Z") + ","
                + rel("foreverguides/v0.1.17", "ForeverGuides-0.1.17.zip", "foreverguides/ForeverGuides-0.1.17.zip", 4000, "2026-11-11T00:00:00Z") + ","
                + rel("packs/v7", "ForeverGuidesPacks-7.zip", "packs/ForeverGuidesPacks-7.zip", 4000, "2026-11-10T00:00:00Z")
                + extra + "],\"latest\":{}}";
        }

        static void FriendsServer()
        {
            List<string> github = new List<string>(), served = new List<string>();
            bool up = true;
            GitHub.Forget();
            try
            {
                string releases = File.ReadAllText(Fixture("releases.json"), Encoding.UTF8);
                GitHub.FakeApi = delegate(string path) { throw new Exception("the public API was asked for " + path); };
                GitHub.FakePrivate = delegate(string path, string token, string accept)
                {
                    github.Add(path);
                    if (path == "repos/" + Own + "/contents/pack.json") return PackText();
                    if (path.StartsWith("repos/" + Own + "/releases?")) return releases;
                    throw new PrivateMissing("404");
                };
                Friends.Fake = delegate(string path)
                {
                    served.Add(path);
                    if (!up) return null;
                    if (path == "pack.json") return PackText();
                    if (path == "manifest.json") return FriendsManifest("");
                    return null;
                };
                string at = Friends.Home + "f/";
                Is(Pack.Download() == PackText() && served.Contains("pack.json") && github.Count == 0, "pack.json from the friends' server, GitHub not asked");
                KnownRepo[] own = Pack.Parse(PackText()).Where(k => k.Private).ToArray();
                Release ui = GitHub.Latest(own[0]);
                Is(ui.Tag == "foreverui/v0.1.6" && ui.ZipName == "ForeverUI-0.1.6.zip" && ui.Private && ui.Sha256 == new string('c', 64) && ui.Size == 5000,
                    "ForeverUI's newest from the manifest, its sha256 the manifest's (" + ui.Tag + ")");
                Is(ui.ZipUrl == at + "foreverui/ForeverUI-0.1.6.zip" && Friends.Owns(ui.ZipUrl) && ui.PickedBy.Contains("friends' server"), "its zip on the friends' server (" + ui.ZipUrl + ")");
                Release fg = GitHub.Latest(own[1]), packs = GitHub.Latest(own[3]);
                Is(fg.Tag == "foreverguides/v0.1.17" && packs.Tag == "packs/v7", "the others by their own series");
                Is(Throws(delegate { GitHub.Latest(own[2]); }, null), "a release whose path has .. is left out (ForeverMap has none other)");
                Is(github.Count == 0 && served.Count(p => p == "manifest.json") == 1, "one manifest for all, no GitHub call (" + served.Count + ", " + github.Count + ")");
                TrackedAddon tracked = new TrackedAddon { Repo = Own, Series = own[0].Series, ZipPattern = own[0].Zip, Private = true };
                List<Release> older = GitHub.Older(tracked, "foreverui/v0.1.6", 5).ToList();
                Is(older.Count == 1 && older[0].Tag == "foreverui/v0.1.5" && Friends.Owns(older[0].ZipUrl), "an older version from the same manifest");
                SelfUpdate.Offer self = SelfUpdate.Check();
                Is(self != null && self.Version == "1.0.9" && self.Url == at + "manager/ForeverManager-1.0.9.exe" && self.Sha256 == new string('c', 64), "the Manager's own new version from the manifest");
                Is(Throws(delegate { Net.Download(Friends.Home + "manifest.json", Path.Combine(root, "no.zip"), null, null, 0, false, FakeToken); }, "only through"),
                    "a private download only from the server's files (f/)");
                Is(!Friends.Owns("https://friends.survivorui.com.evil.example/f/a.zip") && !Friends.Owns("http://friends.survivorui.com/f/a.zip"), "only the server's own https address counts");

                // Down: GitHub, then the server again after two minutes (Forget stands for them).
                up = false;
                GitHub.Forget();
                served.Clear();
                Is(Pack.Download() == PackText() && github.Contains("repos/" + Own + "/contents/pack.json"), "server down: pack.json from GitHub");
                Release gh = GitHub.Latest(own[0]);
                Is(gh.Tag == "foreverui/v0.1.0" && gh.ZipUrl.StartsWith("https://api.github.com/") && !Friends.Owns(gh.ZipUrl), "server down: releases from GitHub, with the token");
                Is(served.Count == 1, "a server that did not answer is not asked again at once (" + served.Count + ")");
                string log = File.ReadAllText(Path.Combine(Core.Home(), "log.txt"));
                Is(log.Contains("friends' server: pack.json") && log.Contains("GitHub instead"), "said in the log");
                up = true;
                GitHub.Forget();
                github.Clear();
                Is(GitHub.Latest(own[0]).Tag == "foreverui/v0.1.6" && github.Count == 0, "back up: the server again");

                // A manifest that cannot be read: GitHub.
                Friends.Fake = delegate(string path) { return path == "manifest.json" ? "{\"format\":2}" : PackText(); };
                GitHub.Forget();
                Is(GitHub.Latest(own[0]).Tag == "foreverui/v0.1.0", "a manifest of another format: GitHub");
                Is(Throws(delegate { Friends.Shape("{\"format\":1,\"releases\":[]}", at); }, "no release"), "an empty manifest is refused");
                object[] shaped = Friends.Shape(FriendsManifest(",{\"tag\":\"x/v1\",\"assets\":[{\"name\":\"a/b.zip\",\"path\":\"x/a.zip\",\"size\":1,\"sha256\":\"" + new string('d', 64) + "\"}]}"), at);
                Is(shaped.Length == 5, "a file with a / in its name, and one with .. in its path, leave their releases out (" + shaped.Length + ")");
                Is(Friends.Shape(FriendsManifest(""), at).Length == 5, "the rest kept");

                // 1.0.8: a public repository's releases through the server (/gh/), GitHub's API never asked.
                GitHub.FakeApi = null;
                Environment.SetEnvironmentVariable("FUM_NO_API", "1");
                List<string> proxied = new List<string>();
                Friends.Fake = delegate(string path)
                {
                    proxied.Add(path);
                    if (path == "gh/rusty-key/spoken-wow/releases?per_page=100") return "[" + ReleaseJson("spoken/v1.2.0", false, "Spoken-1.2.0.zip") + "]";
                    if (path.StartsWith("gh/someone/")) throw new Exception("it answered 404");
                    if (path.StartsWith("gh/")) throw new Exception("it answered 502");
                    return null;
                };
                GitHub.Forget();
                Release spoken = GitHub.Latest("rusty-key/spoken-wow", @"^spoken/v\d", "Spoken-*.zip");
                Is(spoken.Tag == "spoken/v1.2.0" && spoken.ZipName == "Spoken-1.2.0.zip" && proxied.Count == 1, "a public repository's releases from the server, with GitHub's API off (" + spoken.Tag + ")");
                Is(Friends.Proxy("repos/" + Own + "/releases?per_page=100") == null && proxied.Count == 1, "the pack's own repository never through /gh/");
                Is(Friends.Proxy("repos/a/b/contents/x") == null && Friends.Proxy("search/repositories?q=x") == null && proxied.Count == 1, "only lists of releases go through it");
                Is(Friends.Proxy("repos/someone/else/releases?per_page=30") == null && proxied.Count == 2, "a repository the server does not list: GitHub for it");
                Is(Friends.Proxy("repos/other/one/releases?per_page=30") == null && proxied.Count == 3, "after a 404 the server is still asked for the others");
                Is(Friends.Proxy("repos/rusty-key/spoken-wow/releases?per_page=100") == null && proxied.Count == 3, "after a server error, GitHub for two minutes");
                Environment.SetEnvironmentVariable("FUM_NO_API", null);

                // FUM_FRIENDS=off: never asked.
                Friends.Fake = delegate(string path) { served.Add("off " + path); return null; };
                Environment.SetEnvironmentVariable("FUM_FRIENDS", "off");
                GitHub.Forget();
                served.Clear();
                Is(GitHub.Latest(own[0]).Tag == "foreverui/v0.1.0" && served.Count == 0 && Friends.Base == null, "FUM_FRIENDS=off: GitHub only");
            }
            finally
            {
                Environment.SetEnvironmentVariable("FUM_FRIENDS", null);
                Friends.Fake = null;
                GitHub.FakeApi = null;
                GitHub.FakePrivate = null;
                GitHub.Forget();
            }
        }

        static void PrivateInstall()
        {
            string addons = Path.Combine(root, "game6", "Interface", "AddOns");
            // ForeverGuides: several ForeverGuides* folders in one zip.
            string fg = MakeZip("foreverguides.zip", new Dictionary<string, string>
            {
                { "ForeverGuides/ForeverGuides.toc", "## Interface: 16001\n## Version: 0.1.0" }, { "ForeverGuides/Core.lua", "--" },
                { "ForeverGuides_Data/ForeverGuides_Data.toc", "## Interface: 16001" }, { "ForeverGuides_Data/Data.lua", "--" },
                { "ForeverGuides_Forge/ForeverGuides_Forge.toc", "## Interface: 16001" },
                { "ForeverGuides_Options/ForeverGuides_Options.toc", "## Interface: 16001" },
            });
            Placed placed = Installer.Put(addons, fg, null, "ForeverGuides");
            Is(placed.Folders.Length == 4 && placed.Folders.All(f => Directory.Exists(Path.Combine(addons, f))), "ForeverGuides' 4 folders in (" + placed.Folders.Length + ")");
            // The guide packs: ForeverGuidesPack_ folders only, the guide rule.
            string packs = MakeZip("fgpacks.zip", new Dictionary<string, string>
            {
                { "ForeverGuidesPack_Alpha/ForeverGuidesPack_Alpha.toc", "## Interface: 16001" }, { "ForeverGuidesPack_Alpha/Guides.lua", "--" },
                { "ForeverGuidesPack_Beta/ForeverGuidesPack_Beta.toc", "## Interface: 16001" },
                { "PolestarPack_Lodestar/PolestarPack_Lodestar.toc", "## Interface: 16001" },
            });
            Is(!Throws(delegate { Installer.OnlyGuides(packs); }, null), "ForeverGuidesPack_ folders pass the guide rule");
            Is(Installer.Put(addons, packs, null, "packs").Folders.Length == 3, "the packs' folders in");
            string mixed = MakeZip("fgmixed.zip", new Dictionary<string, string>
            {
                { "ForeverGuidesPack_X/ForeverGuidesPack_X.toc", "## Interface: 16001" },
                { "ForeverGuides/ForeverGuides.toc", "## Interface: 16001" },
            });
            Is(Throws(delegate { Installer.OnlyGuides(mixed); }, "more than guides"), "a packs zip that brings the addon itself is refused");
            Is(!Installer.GuideFolder("ForeverGuidesPack_") && !Installer.GuideFolder("ForeverGuides") && Installer.GuideFolder("ForeverGuidesPack_A"), "a guide folder needs a name after its prefix");
            // ForeverMap's art (about 276 MiB): no cap on a GitHub addon, only the room on the drive.
            string temp = Path.Combine(root, "roomtemp6");
            Directory.CreateDirectory(temp);
            try
            {
                Environment.SetEnvironmentVariable("FUM_TEST_FREE", (2048L * 1048576).ToString());
                Is(!Throws(delegate { Installer.RoomToDownload(temp, addons, 290L * 1048576, 0); }, null), "a 290 MB zip goes with 2 GB free");
            }
            finally { Environment.SetEnvironmentVariable("FUM_TEST_FREE", null); }
        }

        static string ReleaseJson(string tag, bool pre, params string[] assets)
        {
            List<object> list = new List<object>();
            foreach (string a in assets)
                list.Add(new Dictionary<string, object> { { "name", a }, { "browser_download_url", "https://github.com/x/y/releases/download/" + Uri.EscapeDataString(tag) + "/" + a }, { "size", 1234 }, { "digest", "sha256:" + new string('a', 64) } });
            return Json.Write(new Dictionary<string, object> { { "tag_name", tag }, { "prerelease", pre }, { "draft", false }, { "assets", list }, { "html_url", "https://github.com/x/y/releases/tag/" + tag } });
        }

        static void Series()
        {
            // Page 1: 100 releases of other things and the bosses pack; page 2: the quests pack.
            List<string> page1 = new List<string>();
            for (int i = 0; i < 99; i++) page1.Add(ReleaseJson("manager/v0.1." + i, false, "Spoken-Manager.exe"));
            page1.Insert(40, ReleaseJson("bosses-audio/v1.0.0", false, "SpokenBossesAudio-1.0.0.zip"));
            List<string> second = new List<string>();
            for (int i = 0; i < 98; i++) second.Add(ReleaseJson("launcher/v0.0." + i, false, "Spoken-Launcher.exe"));
            second.Insert(10, ReleaseJson("quests-audio/v2.6.0", true, "SpokenQuestsAudioAll-2.6.0.zip"));
            second.Insert(11, ReleaseJson("quests-audio/v2.5.0", false, "SpokenQuestsAudioAll-2.5.0.zip"));
            string page2 = "[" + string.Join(",", second.ToArray()) + "]";
            List<string> asked = new List<string>();
            GitHub.Forget();
            GitHub.FakeApi = delegate(string path)
            {
                asked.Add(path);
                if (path.Contains("page=2")) return page2;
                if (path.Contains("page=3")) return "[]";
                return "[" + string.Join(",", page1.ToArray()) + "]";
            };
            try
            {
                Release quests = GitHub.Latest("x/y", @"^quests-audio/v\d", "SpokenQuestsAudioAll-*.zip");
                Is(quests.Tag == "quests-audio/v2.5.0" && quests.ZipName == "SpokenQuestsAudioAll-2.5.0.zip" && quests.Sha256 == new string('a', 64) && quests.Size == 1234,
                    "a series found on the second page, its test version left out (" + quests.Tag + ")");
                Is(asked.Count == 2 && asked[0].Contains("per_page=100"), "two pages of 100 asked (" + string.Join(" ", asked.ToArray()) + ")");
                Release bosses = GitHub.Latest("x/y", @"^bosses-audio/v\d", "SpokenBossesAudio-*.zip");
                Is(bosses.Tag == "bosses-audio/v1.0.0" && asked.Count == 2, "another series of the same repository: no call more");
                Is(Throws(delegate { GitHub.Latest("x/y", @"^places-audio/v\d", "SpokenPlacesAudio-*.zip"); }, GitHub.NoStable) && asked.Count == 3, "a series in none: three pages at most (" + asked.Count + ")");
                Release betas = GitHub.Latest("x/y", @"^quests-audio/v\d", "SpokenQuestsAudioAll-*.zip", true);
                Is(betas.Tag == "quests-audio/v2.6.0" && betas.Test, "with test versions, the newest");
                GitHub.Forget();
                GitHub.Latest("x/y", @"^bosses-audio/v\d", "SpokenBossesAudio-*.zip");
                Is(asked.Count == 4, "forgotten: asked again");
            }
            finally
            {
                GitHub.FakeApi = null;
                GitHub.Forget();
            }
        }

        // Its newest release has no zip (Forever Quest Tint v0.7.3, 2026-11-09:
        // its author forgot it): the newest before it that has one, the newer
        // one named on the row; none with a zip: the newest's own word.
        static void NoZip()
        {
            string answer = null;
            List<string> asked = new List<string>();
            GitHub.Forget();
            GitHub.FakeApi = delegate(string path) { asked.Add(path); return answer; };
            try
            {
                answer = "[" + ReleaseJson("v0.7.3", false) + "," + ReleaseJson("v0.7.2", false, "ForeverQuestTint-0.7.2.zip") + "," + ReleaseJson("v0.7.1", false, "ForeverQuestTint-0.7.1.zip") + "]";
                Release r = GitHub.Latest("a/tint", null, null);
                Is(r.Tag == "v0.7.2" && r.ZipName == "ForeverQuestTint-0.7.2.zip" && r.Skipped == "v0.7.3" && r.SkippedShort == "has no zip"
                    && r.SkippedWhy == "its release v0.7.3 has no zip" && asked.Count == 1,
                    "a newest release with no zip: the one before it, the newer one named (" + r.Tag + ", " + r.SkippedWhy + ", " + asked.Count + " call)");

                GitHub.Forget();
                answer = "[" + ReleaseJson("v0.7.3", false, "ForeverQuestTint-0.7.3.zip") + "," + ReleaseJson("v0.7.2", false, "ForeverQuestTint-0.7.2.zip") + "]";
                r = GitHub.Latest("a/tint", null, null);
                Is(r.Tag == "v0.7.3" && r.Skipped == null, "its zip put in later: the newest again, nothing left out");

                GitHub.Forget();
                answer = "[" + ReleaseJson("v0.7.3", false) + "," + ReleaseJson("v0.7.2", false) + "]";
                Is(Throws(delegate { GitHub.Latest("a/tint", null, null); }, "its release v0.7.3 has no zip"), "none with a zip: the newest's own word, a failed check");

                GitHub.Forget();
                answer = "[" + ReleaseJson("v0.8.0", true) + "," + ReleaseJson("v0.7.3", false) + "," + ReleaseJson("v0.7.2", false, "ForeverQuestTint-0.7.2.zip") + "]";
                r = GitHub.Latest("a/tint", null, null);
                Is(r.Tag == "v0.7.2" && r.Skipped == "v0.7.3", "a test version is no newest to leave out without betas");

                GitHub.Forget();
                answer = "[" + ReleaseJson("ui/v0.2.0", false, "Other-0.2.0.zip") + "," + ReleaseJson("map/v1.0", false, "Map-1.0.zip") + "," + ReleaseJson("ui/v0.1.9", false, "UI-0.1.9.zip") + "]";
                r = GitHub.Latest("a/pack", @"^ui/v\d", "UI-*.zip");
                Is(r.Tag == "ui/v0.1.9" && r.Skipped == "ui/v0.2.0" && r.SkippedShort == "has no zip" && r.SkippedWhy.Contains("named like UI-*.zip"),
                    "a series: its newest with no zip by its name left out for the one before, not another series' (" + r.Tag + ")");

                GitHub.Forget();
                answer = "[" + ReleaseJson("v2.0", false, "A-classic.zip", "A-wrath.zip") + "," + ReleaseJson("v1.9", false, "A-1.9.zip") + "]";
                r = GitHub.Latest("a/a", null, null);
                Is(r.Tag == "v1.9" && r.SkippedShort == "has several zips", "several zips and nothing saying which: left out too (" + r.SkippedShort + ")");

                GitHub.Forget();
                List<string> many = new List<string>();
                for (int i = 9; i >= 0; i--) many.Add(ReleaseJson("v1." + i, false));
                many.Add(ReleaseJson("v0.9", false, "A-0.9.zip"));
                answer = "[" + string.Join(",", many.ToArray()) + "]";
                Is(Throws(delegate { GitHub.Latest("a/a", null, null); }, "its release v1.9 has no zip"), "five older ones at most are tried");
            }
            finally
            {
                GitHub.FakeApi = null;
                GitHub.Forget();
            }
        }

        static void WebPages()
        {
            string refs = "001e# service=git-upload-pack\n0000015b1111111111111111111111111111111111111111 HEAD\0multi_ack\n" +
                "003f2222222222222222222222222222222222222222 refs/heads/main\n" +
                "00453333333333333333333333333333333333333333 refs/tags/quests-audio/v2.9.1\n" +
                "00484444444444444444444444444444444444444444 refs/tags/quests-audio/v2.9.1^{}\n" +
                "00465555555555555555555555555555555555555555 refs/tags/quests-audio/v2.10.0\n" +
                "004a6666666666666666666666666666666666666666 refs/tags/quests-audio-esMX/v9.0.0\n" +
                "00407777777777777777777777777777777777777777 refs/tags/spoken/v3.8.3\n0000";
            List<string> tags = GitHub.TagsIn(refs);
            Is(tags.Count == 4 && !tags.Exists(t => t.Contains("^")), "git's tags read, peeled ones once (" + string.Join(" ", tags.ToArray()) + ")");
            List<string> newest = GitHub.NewestFirst(tags, @"^quests-audio/v\d");
            Is(newest.Count == 2 && newest[0] == "quests-audio/v2.10.0", "the newest of a series by its version, not another language's");

            string page = "<div><ul><li class=\"Box-row\"><a href=\"/x/y/releases/download/quests-audio%2Fv2.10.0/SpokenQuestsAudioAll-2.10.0.zip\" rel=\"nofollow\">SpokenQuestsAudioAll-2.10.0.zip</a>" +
                "<span class=\"Truncate-text\">sha256:" + new string('b', 64) + "</span></li>" +
                "<li class=\"Box-row\"><a href=\"/x/y/archive/refs/tags/quests-audio/v2.10.0.zip\">Source code (zip)</a></li>" +
                "<li><a href=\"/x/y/releases/download/quests-audio%2Fv2.10.0/notes.txt\">notes.txt</a></li></ul></div>";
            List<Asset> assets = GitHub.ParseAssets(page, "x/y");
            Is(assets.Count == 2 && assets[0].Name == "SpokenQuestsAudioAll-2.10.0.zip" && assets[0].Sha256 == new string('b', 64)
                && assets[0].Url == "https://github.com/x/y/releases/download/quests-audio%2Fv2.10.0/SpokenQuestsAudioAll-2.10.0.zip" && assets[1].Sha256 == null,
                "a release page's files and sha256s, not its source code");
            Is(GitHub.ParseAssets(page, "other/repo").Count == 0, "only the repository's own downloads");
        }

        // A web server of our own on this computer, answering each request
        // with the next answer given: how a download is taken up again.
        static List<string> Serve(int port, params Func<string, byte[]>[] answers)
        {
            List<string> seen = new List<string>();
            System.Net.Sockets.TcpListener server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            server.Start();
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                try
                {
                    foreach (Func<string, byte[]> answer in answers)
                        using (System.Net.Sockets.TcpClient client = server.AcceptTcpClient())
                        using (System.Net.Sockets.NetworkStream stream = client.GetStream())
                        {
                            StringBuilder head = new StringBuilder();
                            while (!head.ToString().EndsWith("\r\n\r\n")) { int b = stream.ReadByte(); if (b < 0) break; head.Append((char)b); }
                            lock (seen) seen.Add(head.ToString());
                            byte[] reply = answer(head.ToString());
                            try { stream.Write(reply, 0, reply.Length); } catch { }
                        }
                }
                catch { }
                finally { server.Stop(); }
            });
            worker.IsBackground = true;
            worker.Start();
            return seen;
        }

        static byte[] Reply(string status, string headers, byte[] body, int send)
        {
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n" + headers + "Connection: close\r\n\r\n");
            byte[] all = new byte[head.Length + send];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            if (send > 0) Buffer.BlockCopy(body, 0, all, head.Length, send);
            return all;
        }

        static void Resume()
        {
            byte[] payload = new byte[300000];
            new Random(7).NextBytes(payload);
            int port = 47000 + new Random().Next(2000);
            string url = "http://127.0.0.1:" + port + "/file.zip";
            string path = Path.Combine(root, "resume.part");

            // The first answer stops at 100 KB; the second gives the rest.
            List<string> seen = Serve(port,
                delegate(string head) { return Reply("200 OK", "Content-Length: 300000\r\n", payload, 100000); },
                delegate(string head)
                {
                    byte[] rest = payload.Skip(100000).ToArray();
                    return Reply("206 Partial Content", "Content-Range: bytes 100000-299999/300000\r\nContent-Length: 200000\r\n", rest, rest.Length);
                });
            Net.Download(url, path, null, null, 0, true);
            Is(File.ReadAllBytes(path).SequenceEqual(payload), "a download that stopped is taken up where it stopped");
            Is(seen.Count == 2 && seen[1].IndexOf("Range: bytes=100000-", StringComparison.OrdinalIgnoreCase) >= 0, "only the rest asked for");

            // Whole already: the server has nothing after it.
            port++;
            seen = Serve(port, delegate(string head) { return Reply("416 Range Not Satisfiable", "Content-Range: bytes */300000\r\nContent-Length: 0\r\n", null, 0); });
            Net.Download("http://127.0.0.1:" + port + "/file.zip", path, null, null, 0, true);
            Is(File.ReadAllBytes(path).SequenceEqual(payload) && seen.Count == 1, "a whole one is not downloaded again");

            // A server that gives all of it again, from the start.
            port++;
            File.WriteAllBytes(path, payload.Take(5000).ToArray());
            seen = Serve(port, delegate(string head) { return Reply("200 OK", "Content-Length: 300000\r\n", payload, payload.Length); });
            Net.Download("http://127.0.0.1:" + port + "/file.zip", path, null, null, 0, true);
            Is(File.ReadAllBytes(path).SequenceEqual(payload), "a server that ignores the Range: started again");

            // Without resume (a guide's): nothing kept, nothing tried twice.
            port++;
            File.Delete(path);
            Serve(port, delegate(string head) { return Reply("200 OK", "Content-Length: 300000\r\n", payload, 1000); });
            Is(Throws(delegate { Net.Download("http://127.0.0.1:" + port + "/file.zip", path, null, null, 0, false); }, null), "without resume, a download that stops fails");

            Release a = new Release { ZipUrl = "https://github.com/x/y/releases/download/v1/a.zip", Sha256 = new string('c', 64) };
            Release b = new Release { ZipUrl = "https://github.com/x/y/releases/download/v1/a.zip" };
            Release c = new Release { ZipUrl = "https://github.com/x/y/releases/download/v2/a.zip" };
            Is(Installer.PartPath(root, a) == Installer.PartPath(root, a) && Installer.PartPath(root, a) != Installer.PartPath(root, b)
                && Installer.PartPath(root, b) != Installer.PartPath(root, c), "one held part per file");
        }

        static void Room()
        {
            string temp = Path.Combine(root, "roomtemp"), addons = Path.Combine(root, "game", "Interface", "AddOns");
            Directory.CreateDirectory(temp);
            try
            {
                Environment.SetEnvironmentVariable("FUM_TEST_FREE", (300L * 1048576).ToString());
                Is(Throws(delegate { Installer.RoomToDownload(temp, addons, 1480L * 1048576, 0); }, "not room enough"), "1.5 GB with 300 MB free is refused before downloading");
                Is(!Throws(delegate { Installer.RoomToDownload(temp, addons, 40L * 1048576, 0); }, null), "40 MB with 300 MB free goes");
                Is(!Throws(delegate { Installer.RoomToDownload(temp, addons, -1, 0); }, null), "no size known: not checked before");
                string zip = MakeZip("room.zip", new Dictionary<string, string> { { "A/A.toc", "## Interface: 16001" } });
                Is(!Throws(delegate { Installer.RoomToUnpack(addons, zip); }, null), "a small zip unpacks");
                Environment.SetEnvironmentVariable("FUM_TEST_FREE", "1000");
                Is(Throws(delegate { Installer.RoomToUnpack(addons, zip); }, "not room enough"), "no room to unpack is said");
            }
            finally { Environment.SetEnvironmentVariable("FUM_TEST_FREE", null); }
            Is(Core.Bytes(1479966451) == "1.38 GB" && Core.Bytes(157070901) == "149.8 MB", "sizes in GB and MB");
        }

        static void CurseLinks()
        {
            long id;
            string slug;
            Is(CurseForge.ParseLink("https://www.curseforge.com/wow/addons/quest-completist", out id, out slug) && slug == "quest-completist" && id == -1, "a CurseForge link: its slug");
            Is(CurseForge.ParseLink("curseforge.com/wow/addons/Quest-Completist/files/all?page=1", out id, out slug) && slug == "quest-completist", "a link to one of its pages, in another case");
            Is(CurseForge.ParseLink(" 27328 ", out id, out slug) && id == 27328 && slug == null, "a project id");
            Is(!CurseForge.ParseLink("https://www.curseforge.com/minecraft/mc-mods/jei", out id, out slug), "another game's link is refused");
            Is(!CurseForge.ParseLink("https://www.curseforge.com.evil.example/wow/addons/x", out id, out slug), "another site is refused");
            Is(!CurseForge.ParseLink("0", out id, out slug) && !CurseForge.ParseLink("hello", out id, out slug) && !CurseForge.ParseLink("99999999999", out id, out slug), "no id, no link");
            Is(CurseForge.RepoOf(27328) == "curseforge:27328", "a project's key");
        }

        static object[] FilesOf(string json)
        {
            return Json.Arr(Json.Parse(json) as Dictionary<string, object>, "data");
        }

        static string OneFile(string url, bool sha1, int type, string version)
        {
            Dictionary<string, object> f = new Dictionary<string, object>
            {
                { "id", 5 }, { "isAvailable", true }, { "displayName", "A 1.0" }, { "fileName", "A-1.0.zip" }, { "releaseType", type }, { "fileDate", "2026-01-01T00:00:00Z" },
                { "fileLength", 10 }, { "downloadUrl", url }, { "gameVersions", new[] { version } },
                { "hashes", sha1 ? new object[] { new Dictionary<string, object> { { "value", new string('d', 40) }, { "algo", 1 } } } : new object[0] }
            };
            return Json.Write(new Dictionary<string, object> { { "data", new object[] { f } } });
        }

        static void CursePick()
        {
            object[] files = FilesOf(File.ReadAllText(Fixture("curseforge", "v1-mods-27328-files-pageSize-50.json")));
            Release r = CurseForge.Pick(files, "1.60.1", 27328);
            Is(r.FileId == 7000004 && r.Tag == "QuestCompletist 3.8" && r.ZipName == "QuestCompletist-3.8.zip" && !r.Test,
                "the release for 1.60.1, before a newer beta for it and a newer release for another game (" + r.Tag + ")");
            Is(r.Sha1 == string.Format("{0:x40}", 7000004) && r.Sha256 == null, "its sha1 from CurseForge's hashes (" + r.Sha1 + ")");
            Is(r.ZipUrl.StartsWith("https://edge.forgecdn.net/") && r.Size > 0 && r.Repo == "curseforge:27328" && r.PickedBy.Contains("sha1"), "its download link and size");
            Release beta = CurseForge.Pick(files, "1.60.0", 27328);
            Is(beta.FileId == 7000002 && beta.Test, "only betas and alphas for a version: the beta, before a newer alpha (" + beta.Tag + ")");
            Is(Throws(delegate { CurseForge.Pick(files, "1.60.2", 27328); }, "no file of it for WoW Forever 1.60.2"), "no file for this version: said");
            Is(Throws(delegate { CurseForge.Pick(files, "", 27328); }, "not known"), "the game's version unknown: said");
            Is(Throws(delegate { CurseForge.Pick(FilesOf(OneFile(null, true, 1, "1.60.1")), "1.60.1", 1); }, "does not let apps download it"), "no download link: the author does not allow it, said");
            Is(Throws(delegate { CurseForge.Pick(FilesOf(OneFile("https://edge.forgecdn.net/files/1/1/A-1.0.zip", false, 1, "1.60.1")), "1.60.1", 1); }, "no sha1"), "no sha1: not taken");
            Is(Throws(delegate { CurseForge.Pick(FilesOf(OneFile("https://example.com/A-1.0.zip", true, 1, "1.60.1")), "1.60.1", 1); }, "not on CurseForge's file host"), "a link off CurseForge's file host: not taken");
            Is(Throws(delegate { CurseForge.Pick(FilesOf(OneFile("http://edge.forgecdn.net/A-1.0.zip", true, 1, "1.60.1")), "1.60.1", 1); }, "not on CurseForge's file host"), "a link without https: not taken");
            Is(!Throws(delegate { CurseForge.Pick(FilesOf(OneFile("https://mediafilez.forgecdn.net/files/1/1/A-1.0.zip", true, 3, "1.60.1")), "1.60.1", 1); }, null), "an alpha alone is taken, from either of its hosts");
        }

        static void CurseFlow()
        {
            string answers = Fixture("curseforge");
            try
            {
                // Off: a build without the key, and no test answers.
                Is(!CurseForge.On, "a test build without the key: CurseForge is off");
                Is(Throws(delegate { CurseForge.Latest(27328); }, "off in this build"), "and it says so, asking nothing");
                Is(MainFormBlocked(TrackedAddon.ForCurse(27328)) == CurseForge.Off, "the window says CurseForge is off for its projects");

                Environment.SetEnvironmentVariable("FUM_CURSEFORGE", answers);
                CurseForge.GameVersion = "1.60.1";
                Is(CurseForge.On, "with the test answers, on");
                TrackedAddon quest = TrackedAddon.ForCurse(27328);
                Release r = GitHub.Latest(quest);
                Is(r.FileId == 7000004, "an addon of the list from CurseForge, the same way (" + r.Tag + ")");

                // Add from CurseForge: by its link, by its id.
                long id;
                string slug;
                CurseForge.ParseLink("https://www.curseforge.com/wow/addons/quest-completist", out id, out slug);
                Dictionary<string, object> mod = CurseForge.BySlug(slug);
                TrackedAddon byLink = TrackedAddon.ForCurse(Json.Long(mod, "id"));
                CurseForge.Describe(byLink, mod);
                Is(byLink.CurseId == 27328 && byLink.Title == "Quest Completist" && byLink.Slug == "quest-completist" && byLink.Author == "test-author", "by link: the slug's project, not one of a near name (" + byLink.CurseId + ")");
                CurseForge.ParseLink("27328", out id, out slug);
                TrackedAddon byId = TrackedAddon.ForCurse(id);
                CurseForge.Describe(byId, CurseForge.Mod(id));
                Is(byId.Title == "Quest Completist" && byId.Slug == "quest-completist", "by id: the same project");
                Is(byLink.PageUrl == "https://www.curseforge.com/wow/addons/quest-completist" && byLink.RepoName == "QuestCompletist" && byLink.Name == "Quest Completist", "its page, and the folder a copy by hand would have");
                Is(Throws(delegate { CurseForge.BySlug("no-such-addon"); }, "has no"), "a slug CurseForge does not have");
                Is(Throws(delegate { CurseForge.Mod(424242); }, "no such project"), "an id CurseForge does not have");
                Is(Throws(delegate { CurseForge.Mod(900002); }, null), "an id of another game's is refused");

                // Its zip: checked against the sha1, addon folders only.
                string good = MakeZip("quest.zip", new Dictionary<string, string> { { "QuestCompletist/QuestCompletist.toc", "## Interface: 16001" }, { "QuestCompletist/core.lua", "--" }, { "QuestCompletist/Libs/x.lua", "--" } });
                Release file = new Release { Sha1 = Installer.Sha1Of(good), Size = new FileInfo(good).Length };
                Is(!Throws(delegate { Installer.Verify(good, file); }, null), "its zip matches CurseForge's sha1");
                Is(Throws(delegate { Installer.Verify(good, new Release { Sha1 = new string('0', 40) }); }, "sha1"), "a zip that does not match is refused");
                Is(!Throws(delegate { Installer.OnlyAddonFolders(good); }, null), "addon folders with their .toc: taken");
                string loose = MakeZip("loose.zip", new Dictionary<string, string> { { "QuestCompletist/QuestCompletist.toc", "x" }, { "readme.txt", "x" } });
                Is(Throws(delegate { Installer.OnlyAddonFolders(loose); }, "more than addon folders"), "a file beside the folders: refused");
                string other = MakeZip("other.zip", new Dictionary<string, string> { { "QuestCompletist/QuestCompletist.toc", "x" }, { "Other/other.lua", "x" } });
                Is(Throws(delegate { Installer.OnlyAddonFolders(other); }, "more than addon folders"), "a folder without a .toc: refused");
                string deep = MakeZip("deep.zip", new Dictionary<string, string> { { "Repo-main/Addon/Addon.toc", "x" } });
                Is(Throws(delegate { Installer.OnlyAddonFolders(deep); }, "not a folder at the top"), "an addon folder inside another: refused");
                string none = MakeZip("none.zip", new Dictionary<string, string> { { "readme.txt", "x" } });
                Is(Throws(delegate { Installer.OnlyAddonFolders(none); }, "no addon folder"), "no addon at all: refused");

                // Answers kept: a second question within 15 minutes asks nothing (with the real API; here, the answers' own count).
                CurseForge.Fake = delegate(string path) { return File.ReadAllText(Path.Combine(answers, "v1-mods-27328-files-pageSize-50.json")); };
                int calls = CurseForge.Calls;
                CurseForge.Latest(27328);
                Is(CurseForge.Calls == calls + 1, "one question for a project's file");
            }
            finally
            {
                CurseForge.Fake = null;
                CurseForge.GameVersion = "";
                Environment.SetEnvironmentVariable("FUM_CURSEFORGE", null);
            }
        }

        // MainForm's word on a row, without a window.
        static string MainFormBlocked(TrackedAddon a)
        {
            return (string)typeof(MainForm).GetMethod("Blocked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { a });
        }

        static string ManagerRelease(string tag, bool pre, bool digest, params string[] assets)
        {
            List<object> list = new List<object>();
            int id = 500;
            foreach (string name in assets)
            {
                Dictionary<string, object> d = new Dictionary<string, object> { { "name", name }, { "url", "https://api.github.com/repos/" + Own + "/releases/assets/" + (id++) }, { "size", 3 } };
                if (digest) d["digest"] = "sha256:" + Core.Sha256Of(Encoding.ASCII.GetBytes("new"));
                list.Add(d);
            }
            return Json.Write(new Dictionary<string, object> { { "tag_name", tag }, { "prerelease", pre }, { "draft", false }, { "assets", list } });
        }

        static object[] Rel(params string[] releases)
        {
            return Json.Parse("[" + string.Join(",", releases) + "]") as object[];
        }

        static void SelfUpdates()
        {
            object[] releases = Rel(ManagerRelease("foreverui/v9.9.9", false, true, "ForeverUI-9.9.9.zip"),
                ManagerRelease("manager/v2.0.0", true, true, "ForeverManager-2.0.0.exe"),
                ManagerRelease("manager/v1.2.0", false, true, "ForeverManager-1.2.0.exe"),
                ManagerRelease("manager/v1.1.0", false, true, "ForeverManager-1.1.0.exe"),
                ManagerRelease("manager/v1.0.0", false, true, "ForeverManager-1.0.0.exe"));
            SelfUpdate.Offer o = SelfUpdate.Pick(releases, "1.0.0");
            Is(o != null && o.Tag == "manager/v1.2.0" && o.Version == "1.2.0" && o.Name == "ForeverManager-1.2.0.exe" && o.Sha256 == Core.Sha256Of(Encoding.ASCII.GetBytes("new")) && o.Url.StartsWith("https://api.github.com/repos/" + Own + "/releases/assets/"),
                "the newest of the Manager's series (not a test version, not another series)");
            Is(SelfUpdate.Pick(releases, "1.2.0") == null && SelfUpdate.Pick(releases, "1.3.0") == null, "none when this one is as new or newer");
            Is(Throws(delegate { SelfUpdate.Pick(Rel(ManagerRelease("manager/v1.5.0", false, false, "ForeverManager-1.5.0.exe")), "1.0.0"); }, "no sha256"), "no sha256: not taken");
            Is(Throws(delegate { SelfUpdate.Pick(Rel(ManagerRelease("manager/v1.5.0", false, true, "ForeverManager-1.5.0.exe", "ForeverManager-1.5.0-b.exe")), "1.0.0"); }, "several"), "two exes: not taken");
            Is(Throws(delegate { SelfUpdate.Pick(Rel(ManagerRelease("manager/v1.5.0", false, true, "Other.exe")), "1.0.0"); }, "no files named like"), "no exe of its name: not taken");
            Is(Throws(delegate { SelfUpdate.Pick(Rel(ManagerRelease("manager/v1.5.0", false, true, "ForeverManager-1.5.0.exe").Replace("https://api.github.com/repos/" + Own, "https://api.github.com/repos/someone/else")), "1.0.0"); }, "no API address"),
                "an exe of another repository: not taken");

            // Downloaded beside, checked, ready; then swapped in at the next start.
            string folder = Path.Combine(root, "self");
            Directory.CreateDirectory(folder);
            string exe = Path.Combine(folder, "ForeverManager.exe");
            File.WriteAllText(exe, "old");
            Is(SelfUpdate.ExePath() == null, "in a test, no exe to update unless FUM_SELF_EXE names one");
            Environment.SetEnvironmentVariable("FUM_SELF_EXE", exe);
            try
            {
                string askedFor = null;
                Action<string, string> bad = delegate(string url, string path) { File.WriteAllText(path, "bad"); };
                Is(Throws(delegate { SelfUpdate.Fetch(o, exe, bad); }, "sha256"), "a download that does not match GitHub's sha256 is refused");
                Is(!File.Exists(exe + ".new") && !File.Exists(exe + ".download") && File.ReadAllText(exe) == "old", "and nothing of it is left; the exe as it was");
                SelfUpdate.Fetch(o, exe, delegate(string url, string path) { askedFor = url; File.WriteAllText(path, "new"); });
                Is(askedFor == o.Url && File.ReadAllText(exe + ".new") == "new" && File.ReadAllText(exe) == "old", "downloaded beside the exe, the exe untouched");
                Is(SelfUpdate.Ready(exe) == "1.2.0", "ready: 1.2.0");
                SelfUpdate.Fetch(o, exe, delegate(string url, string path) { throw new Exception("asked twice"); });
                Is(true, "ready already: not downloaded again");
                File.WriteAllText(exe + ".new", "changed");
                Is(SelfUpdate.Ready(exe) == null && !SelfUpdate.Swap(exe) && File.ReadAllText(exe) == "old", "a ready file changed since: not swapped in");
                SelfUpdate.Fetch(o, exe, delegate(string url, string path) { File.WriteAllText(path, "new"); });
                Is(!SelfUpdate.ApplyAtStart(new[] { "--tray" }), "at start in a test: swapped, never started");
                Is(File.ReadAllText(exe) == "new" && File.ReadAllText(exe + ".old") == "old" && !File.Exists(exe + ".new") && !File.Exists(exe + ".new.sha256"), "the new one in its place, the old one kept as .old");
                Is(SelfUpdate.Ready(exe) == null && !SelfUpdate.Swap(exe), "nothing ready any more");
                SelfUpdate.CleanOld();
                Is(!File.Exists(exe + ".old") && File.Exists(exe), "the next start deletes the old one");
                Is(SelfUpdate.VersionOf("manager/v1.2.0") == "1.2.0" && SelfUpdate.VersionOf("v1.0") == "1.0", "a tag's version");
            }
            finally { Environment.SetEnvironmentVariable("FUM_SELF_EXE", null); }
        }

        static void Live()
        {
            Release api = GitHub.Latest("EllesmereGaming/EllesmereUI", null, null);
            Console.WriteLine("  EllesmereUI api: " + api.Tag + " " + api.ZipName + " " + api.Size + " (" + api.PickedBy + ")");
            bool apiOut = api.PickedBy.Contains("GitHub's page");
            if (apiOut) Console.WriteLine("  (GitHub's API limit is used up: its pages answered instead)");
            Is(api.ZipName != null && api.ZipName.EndsWith(".zip") && (apiOut || (api.Sha256 != null && api.Size > 1000000)) && api.PickedBy.Contains("release.json"), "EllesmereUI by the API");
            Environment.SetEnvironmentVariable("FUM_NO_API", "1");
            GitHub.Forget();
            Release web = GitHub.Latest("EllesmereGaming/EllesmereUI", null, null);
            Console.WriteLine("  EllesmereUI web: " + web.Tag + " " + web.ZipName);
            Is(web.Tag == api.Tag && web.ZipName == api.ZipName, "EllesmereUI by GitHub's pages");
            Is(Throws(delegate { GitHub.Latest("EllesmereGaming/no-such-repo-here-0451", null, null); }, null), "no such repository, by the pages");
            Environment.SetEnvironmentVariable("FUM_NO_API", null);
            // Spoken and its voice packs: one repository, five series, one call.
            KnownRepo[] spoken = Pack.Parse(PackText()).Where(k => k.Repo == "rusty-key/spoken-wow").ToArray();
            int calls = GitHub.Calls;
            foreach (KnownRepo k in spoken)
            {
                Release r = GitHub.Latest(k.Repo, k.Series, k.Zip);
                Console.WriteLine("  " + k.Title + ": " + r.Tag + " " + r.ZipName + " (" + r.PickedBy + ")");
                Is(System.Text.RegularExpressions.Regex.IsMatch(r.Tag, k.Series) && GitHub.Like(r.ZipName, k.Zip), k.Title + " found");
            }
            Is(GitHub.Calls - calls <= 2, "Spoken's five in one or two API calls");
        }

        // The one real CurseForge question: Quest Completist's files, the one
        // for 1.60.1 picked. Nothing is downloaded; the key is never printed.
        static void CurseLive()
        {
            if (!Secrets.HasCurseForge) { Console.WriteLine("  this build has no CurseForge key (curseforge-key.txt missing): nothing asked"); return; }
            CurseForge.GameVersion = "1.60.1";
            int calls = CurseForge.Calls;
            try
            {
                Release r = CurseForge.Latest(27328);
                Console.WriteLine("  Quest Completist for 1.60.1: file " + r.FileId + ", " + r.ZipName + " (" + r.Tag + "), " + (r.Test ? "beta/alpha" : "release")
                    + ", " + Core.Bytes(r.Size) + ", out " + r.Published + ", sha1 " + (r.Sha1 != null ? "given" : "none") + ", download link on " + new Uri(r.ZipUrl).Host);
                Is(r.Sha1 != null && r.ZipUrl != null && r.ZipName.EndsWith(".zip"), "Quest Completist's file for 1.60.1, with its sha1 and link");
            }
            catch (Exception why)
            {
                Console.WriteLine("  Quest Completist: " + why.Message);
                Is(why is Final, "CurseForge answered, saying why there is no file to take (" + why.Message + ")");
            }
            Is(CurseForge.Calls - calls == 1, "one question asked (" + (CurseForge.Calls - calls) + ")");
        }
        static void RealZip(string zip, int expected)
        {
            string addons = Path.Combine(root, "real", "Interface", "AddOns");
            Placed placed = Installer.Put(addons, zip, null, "Real");
            Console.WriteLine("  real zip: " + placed.Folders.Length + " folders, " + placed.Skipped + " left out");
            Is(placed.Folders.Length == expected, expected + " folders");
            int files = Directory.GetFiles(addons, "*", SearchOption.AllDirectories).Length;
            using (ZipArchive archive = ZipFile.OpenRead(zip))
                Is(files == archive.Entries.Count(e => e.Name != "") - placed.Skipped, "every file out (" + files + ")");
            foreach (string folder in placed.Folders)
            {
                Toc toc = Installer.ReadToc(Path.Combine(addons, folder));
                Is(toc != null && Installer.ForeverInterface(toc.Interface) != null, folder + "'s .toc is for Forever");
            }
        }
    }
}
