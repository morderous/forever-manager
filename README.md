# Forever Manager

A small Windows app that keeps the Forever pack's WoW Forever addons up to date, for a few friends. A variant of ForeverUI Manager.

## What it does

- **The friends' server**: everything of the pack's own (ForeverUI, ForeverGuides, ForeverMap, the guide packs and this app's own new versions) comes from a Cloudflare Worker at `https://friends.survivorui.com/`, in front of a private R2 folder, and only to a build that holds the friends' key, sent in `X-Forever-Key`. It gives `manifest.json` (the pack's releases: tag, notes, and each file's path, size and sha256), `pack.json` and the files under `f/` (with Range). The manifest stands for a GitHub list of releases, so the addons' checks, the older versions and the app's own updates read it as they would read GitHub's, and every download is checked against its sha256. While the server does not answer, the rows say so and it is asked again two minutes later; the log says why.
- **Public addons through the server too**: the releases of the public GitHub repositories the pack lists come through `/gh/<owner>/<repo>/releases` (kept there 10 minutes), so a computer never meets GitHub's 60 checks an hour. A repository the server does not list, or the server down: GitHub is asked as before (the API, else its web pages).
- **No GitHub token** in the exe since 1.0.8 (1.0.7 and older read the pack's private repository with one).
- The list is the server's `pack.json`. It is read at the first check, every 4 hours (with the addons' new versions and the app's own) and on **Check for updates**, and kept in `%APPDATA%\ForeverManager\pack.json` for when the server does not answer. Tests: `FUM_FRIENDS` (another address, `http://127.0.0.1:<port>/` too, or `off`) and `tests\fake_worker.py` (the server's rules over a folder).
- An entry of `pack.json` is one of:
  - `{ "title", "series", "zip" }`: a release series of the pack's own (ForeverUI, ForeverGuides, ForeverMap, the guide packs), from the friends' server, checked against the manifest's sha256;
  - `{ "repo": "owner/name", ... }`: a public GitHub repository, as in ForeverUI Manager (the API, else GitHub's web pages);
  - `{ "curseforge": <project id>, "title" }`: a CurseForge project.
  - Also `required`, `ticked`, `guidesOnly` (a zip of `ForeverGuidesPack_`/`PolestarPack_` folders only) and `replaces`, as in ForeverUI Manager, and `recommended` (a "Recommended" tag on its row; ticked or not as `ticked` says).
  - `required` and `recommended` are read again from the pack each time: one the pack no longer requires is optional (it stays installed if it was). An item's `"ticked": false` is taken once by a row not installed, so Install selected leaves it out; a tick given by hand after that is kept.
- **CurseForge** through its own API (`api.curseforge.com`, the key in `x-api-key`, WoW is game 1), with a key that comes inside the exe. A project's file is the newest one listed for the game's own version (from `.build.info`, today 1.60.1), a release before a beta before an alpha; it is checked against CurseForge's sha1 and its zip may hold addon folders with their `.toc` only. A project whose author does not let apps download it (no download link) says so. **Add from CurseForge...** takes a `https://www.curseforge.com/wow/addons/<name>` link or a project id. Answers are kept 15 minutes, and the last one on disk. A build without the key says CurseForge is off.
- **Only its own folders**: the app moves or deletes only folders it put in AddOns itself. A folder of the same name that was already there (a copy by hand, another app's) is never touched without a yes; for the guide packs it is simply left as it is, also when the packs update.
- **Copies put in AddOns by hand** (since 1.0.4): at each check, an addon not installed by the app whose folders are in AddOns is compared file by file (size and CRC-32) with its newest release and up to 5 older ones. Each zip's list of files is read from its central directory with two small Range requests: nothing is downloaded. The same files as a release: taken as installed (that version; updates as any other). None of them: a purple **Dev** tag and "Dev build in AddOns"; Install selected leaves it alone unless its box is ticked, and then asks before it replaces it. Tests: `FUM_RECOGNIZE_ZIPS` (a folder of zips by name stands for the downloads), `window_test.py recognize`, `Tests.exe recognize-live <zip url> <AddOns> [size]`.
- **Updates itself**: a release in the friends' manifest tagged `manager/v<version>` with a `ForeverManager-<version>.exe` file is downloaded beside the exe (checked against the manifest's sha256) and goes in when the app starts again, or at once with **Restart to update**.
- **The buttons** (since 1.0.5): above the list, **Install selected (N)** (the ticked ones not installed), **Update all (N)** over the State column (the updates that wait, nothing new; "Auto update: checks every 4 h, next at HH:mm" beside it) and **Uninstall selected (N)** (the ticked installed ones, after one question listing them). A row with an update has an **Update** button in its State, one whose folders are gone a **Reinstall** button; each installed row ends in an **×** that uninstalls it on a second click within 4 s (its folders go to the Recycle Bin; any other click lets go). Below the list only Add from GitHub, Add from CurseForge and Remove from list; at the top, **Check for updates**.
- **The boxes**: on a row not installed, ticked means Install selected puts it in (kept; the pack's `ticked` at first). On an installed row (or a dev copy) it is a choice for this run only, for Uninstall selected (Install selected): unticked when the app starts and after each action. Tests: `FUM_SHOT_CLICK` (`<name>|update`, `<name>|cross`, `<name>|cross1`, `-|all`, `-|installsel`, `-|uninstallsel`), `FUM_SHOT_ARM`, `FUM_SHOT_MARK` (`A,B`: those boxes clicked).
- **A newest release that cannot be installed** (since 1.0.5): no zip, or several and nothing saying which (its author's slip, most often). The check takes the newest of the next five that can, says "v0.7.3 has no zip" in gold on its row and why in its details, and looks again at each check; none of them: a failed check, as before.
 Auto update, the tray, Start with Windows, the Recycle Bin for replaced folders, resumed downloads and the room checks are as in ForeverUI Manager. No Guides tab, no polestar.guide.
- The window can be resized and maximized; its size, place and maximized state are kept in `settings.ini` (`window`, `maximized`) and put back on a screen. Maximized, everything sits in a column in the middle 60% of the window (never narrower than the normal 780).
- Credits: "Made by morderous" after the subtitle (a link to github.com/morderous) and in the tray's tooltip.

## Build

```
build.bat
```

It reads `..\curseforge-key.txt` and `..\worker-key.txt` (one line each; `make_secrets.ps1` is the only thing that reads them), puts them in the exe XOR-masked with a random key made for that build, deletes the file it wrote, checks that neither is in the exe in the clear, and writes `..\dist\ForeverManager.exe`. A missing file gets a fake one and a warning (no friends' key: the pack cannot be read; no CurseForge key: CurseForge is off). Masked is not hidden: whoever has the exe can take the keys out, so the friends' key only reads the pack.\n\n## A new version of the app

1. Raise `Core.Version` and the two `Assembly*Version` lines in `ForeverManager.cs`.
2. `build.bat`, then rename `..\dist\ForeverManager.exe` to `ForeverManager-<version>.exe`.
3. Put it in the friends' manifest as a release tagged `manager/v<version>` with that exe as its one `ForeverManager-*.exe` file (not a pre-release). Every app finds it at its next check.

## Where it keeps its things

`%APPDATA%\ForeverManager`: `addons.json`, `settings.ini` (also `starters`, `unticked`, `window`, `maximized`), `log.txt`, `pack.json`, `curseforge\` (CurseForge's last answers). Downloads that stopped wait in `%TEMP%\ForeverManager`. The Windows Run value is `ForeverManager`.

## Tests

`tests\run_tests.sh` builds the app and `tests\Tests.cs` with fake secrets only and runs the offline checks (the pack, the pack's releases and CurseForge's answers from `tests\fixtures`). `run_tests.sh curseforge-live` asks CurseForge one real question (Quest Completist's files, the one for 1.60.1 picked, nothing downloaded) with the real key and a fake token. `python -I tests\window_test.py` opens the window on a test folder (`fresh`, `curseforge`) and saves pictures in `tests\out\shots`; it asks neither GitHub's API nor CurseForge. The exe is only ever opened with `FUM_HOME` set.
