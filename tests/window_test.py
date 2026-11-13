"""Opens the built window (tests/out/ForeverManager.exe, built by run_tests.sh
with FAKE secrets) on a fake WoW Forever folder and saves a picture of it per
scenario, in tests/out/shots. The window sits off screen, never takes focus
and has no tray icon; the real AddOns, settings and Run key are never touched
(FUM_HOME, FUM_GAME, FUM_TEST_BIN). Nothing goes to GitHub's API or to
CurseForge: the pack's list is tests/fixtures/pack.json (FUM_PACK), its own
releases tests/fixtures/releases.json (FUM_PRIVATE_RELEASES), CurseForge's
answers tests/fixtures/curseforge (FUM_CURSEFORGE), and the public addons are
read from GitHub's web pages (FUM_NO_API=1).

    python -I tests/window_test.py [fresh] [big] [maxed] [curseforge]

fresh: a new list from the pack, checked: its 13 addons (4 of its own,
8 public, Quest Completist from CurseForge), no Guides tab; Quest Completist
picked; only the 4 of its own ticked (what Install all puts in), the rest
unticked. big: the same list in a bigger window (FUM_SHOT_SIZE, 1400x900
inside), shots/big.png; maxed: 1920x1000 laid out as maximized
(FUM_SHOT_MAXIMIZED, still off screen), shots/maxed.png; settings.ini gets
no window= from a test. curseforge: Add from CurseForge filled with a link (shots/curse-add.png),
then the window with what it added (shots/curse-added.png).
Prints one line per scenario; exit code = failures.
"""
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out")
EXE = os.path.join(OUT, "ForeverManager.exe")
WORK = os.path.join(OUT, "window")
SHOTS = os.path.join(OUT, "shots")
FIX = os.path.join(HERE, "fixtures")
LINK = "https://www.curseforge.com/wow/addons/example-addon"


def fake_game():
    game = os.path.join(WORK, "World of Warcraft", "_forever_")
    os.makedirs(os.path.join(game, "Interface", "AddOns"), exist_ok=True)
    open(os.path.join(game, "Wow.exe"), "wb").close()
    with open(os.path.join(game, ".flavor.info"), "w") as f:
        f.write("Product Flavor!STRING:0\nwowf\n")
    return game


def run(name, extra):
    env = dict(os.environ)
    env.update({
        "FUM_HOME": os.path.join(WORK, "home"),
        "FUM_GAME": fake_game(),
        "FUM_GAME_VERSION": "1.60.1",
        "FUM_TEST_BIN": os.path.join(WORK, "bin"),
        "FUM_SHOT": os.path.join(SHOTS, name + ".png"),
        "FUM_PACK": os.path.join(FIX, "pack.json"),
        "FUM_PRIVATE_RELEASES": os.path.join(FIX, "releases.json"),
        "FUM_CURSEFORGE": os.path.join(FIX, "curseforge"),
        "FUM_NO_API": "1",
    })
    env.pop("FUM_SELF_EXE", None)
    env.update(extra)
    shots = [env["FUM_SHOT"]] + [env[k] for k in ("FUM_SHOT_CURSE", "FUM_SHOT_CURSE_DONE") if k in env]
    for shot in shots:
        if os.path.exists(shot):
            os.remove(shot)
    started = time.time()
    try:
        subprocess.run([EXE], env=env, timeout=600)
    except subprocess.TimeoutExpired:
        return False, "timed out"
    return all(os.path.exists(s) for s in shots), "%.0f s" % (time.time() - started)


def rows():
    with open(os.path.join(WORK, "home", "addons.json"), encoding="utf-8") as f:
        return json.load(f)["addons"]


def main():
    if not os.path.exists(EXE):
        print("build it first: sh tests/run_tests.sh")
        return 1
    wanted = sys.argv[1:] or ["fresh", "big", "maxed", "curseforge"]
    os.makedirs(SHOTS, exist_ok=True)
    if "fresh" in wanted and os.path.isdir(WORK):
        shutil.rmtree(WORK)
    fails = 0
    for name in wanted:
        extra = {}
        if name == "recognize":
            # ForeverUI in AddOns by hand, the same files as its release: taken
            # as installed. The guide packs by hand, other files: a dev copy.
            if os.path.isdir(WORK):
                shutil.rmtree(WORK)
            import zipfile
            zips = os.path.join(WORK, "zips")
            os.makedirs(zips)
            addons = os.path.join(fake_game(), "Interface", "AddOns")
            files = {"ForeverUI-0.1.0.zip": {"ForeverUI/ForeverUI.toc": "## Title: ForeverUI\n## Version: 0.1.0\n", "ForeverUI/Core.lua": "local x = 1\n"},
                     "ForeverGuidesPacks-1.zip": {"ForeverGuidesPack_Test/ForeverGuidesPack_Test.toc": "## Title: Test pack\n", "ForeverGuidesPack_Test/Guide.lua": "return {}\n"}}
            for zipname, inside in files.items():
                with zipfile.ZipFile(os.path.join(zips, zipname), "w") as z:
                    for path, text in inside.items():
                        z.writestr(path, text)
                        os.makedirs(os.path.dirname(os.path.join(addons, path)), exist_ok=True)
                        with open(os.path.join(addons, path), "w", newline="") as f:
                            f.write(text.replace("return {}", "return { mine = true }"))
            extra["FUM_RECOGNIZE_ZIPS"] = zips
            extra["FUM_SHOT_PICK"] = "ForeverGuides Packs"
        if name in ("fresh", "big", "maxed"):
            extra["FUM_SHOT_PICK"] = "Quest Completist"
        if name == "big":
            extra["FUM_SHOT_SIZE"] = "1400x900"
        if name == "maxed":
            extra["FUM_SHOT_SIZE"] = "1920x1000"
            extra["FUM_SHOT_MAXIMIZED"] = "1"
        if name == "curseforge":
            extra["FUM_SHOT_CURSE"] = os.path.join(SHOTS, "curse-add.png")
            extra["FUM_SHOT_CURSE_TEXT"] = LINK
            extra["FUM_SHOT_CURSE_DONE"] = os.path.join(SHOTS, "curse-added.png")
        ok, took = run(name, extra)
        data = rows()
        own = [r for r in data if r.get("private")]
        curse = [r for r in data if r.get("kind") == "curseforge"]
        public = [r for r in data if r.get("kind") != "curseforge" and not r.get("private")]
        failed = [r.get("title") for r in data if r.get("known") is None and r.get("want", True)]
        log = open(os.path.join(WORK, "home", "log.txt"), encoding="utf-8").read() if os.path.exists(os.path.join(WORK, "home", "log.txt")) else ""
        note = "addons=%d own=%d public=%d curseforge=%s no_version_yet=%s" % (
            len(data), len(own), len(public), ",".join("%s:%s" % (r.get("title"), r.get("known")) for r in curse), ",".join(failed) or "-")
        if name == "fresh":
            quest = [r for r in curse if r.get("curseforge") == 27328]
            ok = ok and len(data) == 13 and len(own) == 4 and len(public) == 8 and len(quest) == 1 \
                and quest[0].get("known") == "QuestCompletist 3.8" and quest[0].get("slug") == "quest-completist" and all(r.get("known") for r in own) \
                and not any(r.get("tag") for r in data) and "FAKE_TEST" not in log
        if name in ("fresh", "big", "maxed"):
            ticked = [r.get("title") for r in data if r.get("want", True)]
            note += " ticked=%s" % ",".join(ticked)
            ok = ok and sorted(ticked) == sorted(r.get("title") for r in own) and len(ticked) == 4
            ini = os.path.join(WORK, "home", "settings.ini")
            ok = ok and not (os.path.exists(ini) and "window=" in open(ini, encoding="utf-8").read())
        if name in ("big", "maxed") and ok:
            from struct import unpack
            with open(os.path.join(SHOTS, name + ".png"), "rb") as f:
                width, height = unpack(">II", f.read(24)[16:24])
            note += " shot=%dx%d" % (width, height)
            ok = ok and width >= (1920 if name == "maxed" else 1400) and height >= 900
        if name == "curseforge":
            added = [r for r in curse if r.get("curseforge") == 900001]
            ok = ok and len(added) == 1 and added[0].get("slug") == "example-addon" and added[0].get("title") == "Example Addon" \
                and added[0].get("known") == "Example Addon 1.2" and "source" not in added[0]
        if name == "recognize":
            ui = [r for r in own if r.get("title") == "ForeverUI"]
            packs = [r for r in own if r.get("guidesOnly")]
            note += " foreverui=%s packs=%s" % (ui[0].get("tag") if ui else None, packs[0].get("tag") if packs else None)
            ok = ok and len(ui) == 1 and ui[0].get("tag") == "foreverui/v0.1.0" and ui[0].get("folders") == ["ForeverUI"] \
                and len(packs) == 1 and packs[0].get("tag") is None and "a dev copy" in log and "taken as installed" in log
        print("%-10s %s  %s  %s" % (name, "ok" if ok else "FAIL", took, note))
        fails += 0 if ok else 1
    return fails


if __name__ == "__main__":
    sys.exit(main())
