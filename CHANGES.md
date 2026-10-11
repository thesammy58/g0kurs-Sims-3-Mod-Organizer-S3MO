# S3MO 1.2.0: what changed

This version fixes four problems: slow startup, merges that redo everything, no subfolders, and broken layouts at high DPI. Your mods, profiles and settings work as before, and nothing has to be converted.

## New in 1.2.0

Everything else on this page came with 1.1.0.

- **Backup before the first build:** S3MO offers to copy the game's Mods folder before it changes anything there, and offers to delete that copy a few starts later. See [Backup of the game's Mods folder](#backup-of-the-games-mods-folder).
- **Clear caches** asks before deleting and says in the log when it's done.
- **Settings file:** `s3mo.ini` with a setting written twice no longer crashes the program at startup.

## Installing

1. Install the **.NET 10 SDK** (free): https://dotnet.microsoft.com/download/dotnet/10.0. Visual Studio isn't needed.
2. Double-click **`build.bat`**. The program ends up in the `publish` folder.
3. **Back up your S3MO folder.** Then copy everything from `publish` into it, replacing the old files. Keep `Mods`, `Profiles` and `s3mo.ini` where they are.
4. Right-click `s3mo.exe` → Properties → Compatibility → *Change high DPI settings*, and **untick the override**. Windows remembers that setting for the exe path, so it carries over to the new exe unless you turn it off.

The first build after updating is a full rebuild, because older versions didn't save the information needed to patch a build. After that, builds only write what changed.

If the layout still looks wrong on your setup, add the line `DpiMode=SystemAware` to `s3mo.ini`. It's a less sharp fallback that doesn't need the compatibility setting. Removing the line goes back to the default, `PerMonitorV2`.

## Startup

- **Conflict check:** the old check compared every package with every other package by searching lists, so the work grew with the square of your collection. Now every resource goes into one lookup table in a single pass. All three conflict views come from that table: mod vs mod, package vs package inside a mod, and the per-resource detail.
- **MD5 pass removed:** startup no longer reads every byte of every package to compute hashes. The "needs rebuild" check uses file size and last-modified time instead.
- **Index cache:** each package's index is saved in `s3mo.cache`. On later starts, only new or changed packages are opened. Deleting the cache is safe; it gets rebuilt.
- **Mod list populates once:** filling the mod list used to rewrite `modlist.txt` and re-check the build once per checked mod. It now does this once.

Measured on a generated collection of 1,200 packages, about 340,000 resources and 211 MB:

| | old | new |
|---|---|---|
| Conflict check (mod vs mod only) | ~85 s (estimated from a subset) | **0.3 s** for the whole startup scan, all three conflict views included |
| Later starts (cache) | the same again, plus MD5 of every file | **0.2 s** |

## Building / merging

- **Faster full builds:** each source package is opened once, and its resources are copied through one reused buffer. The old code opened the file again for every resource and searched the resource list each time. A full build produces **byte-for-byte the same Merge files** as the old version; the tests check this.
- **Incremental builds:** the build saves `ModBuilds\s3mo.buildstate`, which records which package each merged resource came from.
  - On the next build, only new or changed resources are appended. Then a new index is written, and the header is updated last.
  - Disabling, removing or reordering mods also works incrementally. A resource that was hidden comes back from the right lower-priority package.
- **Safe if interrupted:** a build that stops partway leaves the previous build working. Full rebuilds are written to a temporary folder and only swapped in once they're complete. The old version deleted ModBuilds first.
- **Compaction:** space taken up by replaced resources is reclaimed by an automatic full rebuild once it passes 35% of ModBuilds (and at least 64 MB). To force one, **right-click** the build button or **Shift+click** it.
- **Exact rebuild warning:** the "needs rebuild" warning runs the same comparison without writing anything.
  - Reordering mods that don't conflict no longer asks for a rebuild.
  - Editing a package does.
  - The tooltip says what changed.
- **Progress:** builds show a progress bar.
- **Unreadable packages:** a package that can't be read no longer crashes the build. It's left out, and you get a list of which ones were skipped.

Same benchmark as above:

| | old | new |
|---|---|---|
| Full merge (167 MB) | 4.2 s | 1.5 s |
| Add one package and re-merge | 4.2 s (everything again) | **0.5 s** |

These numbers were measured on Linux, where opening a file is cheap. The old merge opened a file for every single resource, which costs more on Windows (especially with antivirus scanning), so the difference there should be bigger.

## Subfolders

- **Packages in subfolders:** packages in subfolders of a mod (e.g. `Mods\Hair Pack\Female\hair.package`) are now found. They're shown with their folder and included in conflict checks and builds. Inside a mod, packages are merged in name order as before, now including the folder part.
- **Zip installs:** installing a zip keeps its folders. Before, files inside folders in a zip were silently dropped.
- **Folders in the mod list:** a mod with subfolders gets an arrow in the mod list. Opening it shows the folders as indented rows, each with its own check box. Click the arrow, double-click a folder, or use the Right/Left arrow keys to open and close them.
  - Unticking a folder leaves it and everything below it out of builds and conflict checks.
  - A mod with some folders switched off shows a partly ticked box.
  - Switching a mod off and on again keeps its folder choices.
  - The Files panel only shows the files directly inside the selected mod or folder, not the whole mod at once. Files that won't be built are greyed out.
  - Selecting a mod or folder colours every row that holds a conflicting package, including the folders on the way to it, so opening a coloured mod shows which folder the conflict is in. Green means the selection overwrites it, red means it overwrites the selection. When a folder is selected, other folders of the same mod are coloured too (the later name wins); yellow means some of both.
  - Folder rows show their own conflict icons.
  - The order is still set per mod: drag mods (not folders) to reorder them. Dropping below the last row moves them to the end.
  - Switched-off folders are saved per profile in `Profiles\<name>\disabledfolders.txt`. `modlist.txt` is unchanged, so older versions can still read it. Copying a profile copies this file too.
- **Moved files aren't rewritten:** moving packages between folders (or between mods) is recognised by file name, size and last-modified time. If a moved package still wins the same resources, nothing is written. Before, reorganising folders made the build treat every moved file as new, which showed up as unused space and could trigger a full rebuild.

## High DPI

- **Framework and DPI mode:** moved from .NET 6, which is out of support, to .NET 10, and runs per-monitor DPI aware (configurable, see above).
- **Owner-drawn lists:** check box, text and icon positions in the drawn lists used fixed pixel offsets, and now scale with DPI. Text is vertically centred in the rows.
- **Check box clicks:** the check box you see is now also what you click. Before, the real click area was the list's own hidden check box, which moved differently at high DPI.
  - Space toggles the selected mods.
  - Double-clicking the check box no longer opens the mod window.
- **Saved layout:** window size, splitter positions and column widths are saved together with the DPI they were measured at, and scaled when restored. Windows saved on a monitor that's no longer there are moved back on screen.
- **Toolbar:** toolbar icons are scaled with the window.
- **Lists fill their panes:** the mod list, the Files tab and the lists in the mod window are now docked instead of anchored. Anchored controls inside the split panes kept their original size at high DPI, which left large empty areas under them.
- **No more stray colours in lists:** the empty strip right of the last column and below the last row is now painted. It used to show leftover pixels, such as a yellow bar under the mod list.
- **Mod window:** the package list uses the full width (the right-hand pane was always empty), and the resource list's Name column fills the remaining width.

## Quick guide

- A **?** button next to Info opens a quick guide that explains the row colours, check boxes, flag icons, priority, the Files list and every toolbar button, using the real icons. It stays open beside the main window, so you can compare it with the list.

## Backup of the game's Mods folder

- **Offered before the first build:** building replaces `Resource.cfg` in the game's Mods folder, so mods still in that folder (for example in `Mods\Packages`) stop loading. When there's no backup yet and the folder has files S3MO didn't make, pressing Build first asks whether to back it up.
  - The copy goes to `Game Mods Backup\<date and time>\Mods` next to `s3mo.exe`, with a note on how to put it back.
  - `ModBuilds` is left out, because S3MO can always make it again.
  - Free space is checked first, and progress is shown like a build.
  - A copy that's interrupted is never mistaken for a backup.
  - "Don't ask again" turns the question off.
- **Offered for deletion later:** after S3MO has been started 5 times since that first backup, with at least one build in between, it says how much space the backup takes and asks whether to delete it.
  - "Keep backup" asks again a few starts later, unless "Do not show this message again" is ticked.
  - Backups made from the folder menu are never suggested for deletion.
  - A deletion that's cut short never leaves something that looks like a complete backup.
- **Any time:** the folder button's menu can make a new backup and open the backups folder.

## Other fixes

- **Clear caches asks first and reports back:** the broom button asks "Clear your game's caches?" before deleting anything, and the log says when it's finished and how much was deleted. If nothing is selected in Settings, it says so. Large folders like WorldCaches are deleted in the background, so the window doesn't freeze.

- **The index cache is now read when the program starts.** It was saved after every scan but only used for scans later in the same session, so every start read all packages again.
- **Clearer header note:** "File contains non-zero unused byte(s)" is replaced by one note per scan. It says how many packages have extra data in header fields the game doesn't use (usually a date or version number from the tool that made them), names a few of them, and says that it's harmless.

- **Logging:**
  - Background work no longer writes into the log box from another thread, which could cause random freezes.
  - `s3mo.log` is flushed after every line. Before, it was lost when the app crashed.
  - A package with odd header bytes now logs one warning instead of one per byte.
- **Busy-waits:** loops that spun a CPU core at 100% while waiting (e.g. when opening a mod's info window) are gone.
- **Package reading:**
  - `.PACKAGE` files with an uppercase extension are accepted.
  - File handles are closed when a package fails to load, so the file isn't left locked.
  - Truncated packages give a clear error.
  - Packages whose index shares the low half of the instance ID are now read correctly (index flag `0x08`).
  - The package viewer reads a package through one open file instead of reopening it for every resource.
- List icons are loaded once instead of creating a new bitmap for every row on every repaint.
- **Mod icon:** the mod list now shows the "packages inside this mod overwrite each other" icon even when the mod has no conflicts with other mods.
- **Robustness:**
  - Empty lines in `modlist.txt`, renaming a mod to a name that already exists, and deleting a mod that's in use no longer crash.
  - `.sims3pack` files can't write outside the install folder through odd file names.

## Checking it yourself

`tests\S3MO.Tests` contains the original library code, unchanged apart from its namespace, and compares it with the new code:

```
cd tests\S3MO.Tests
dotnet run -c Release                                  # all checks (about 15 seconds)
dotnet run -c Release bench 1200                       # the benchmark above
dotnet run -c Release verify "C:\path\to\your\S3MO"    # your real mods, read-only
```

`verify` doesn't change anything in your S3MO folder. It times the old and new code on your enabled mods, merges them with both into your temp folder, compares the results byte for byte, and deletes the copies afterwards. It needs free space of roughly twice the size of your enabled packages. The test suite checks the following:

- the new reader reads the same entries as the old one
- conflicts match exactly
- full merges are byte-identical to the old code
- in the default run, 160 random incremental builds (add, delete, change, enable, disable, reorder) each produce the same content as a from-scratch build. `dotnet run -c Release soak 30` runs 1,200 more; all of them passed here.
- tampered, stray or old-format ModBuilds are detected and rebuilt in full
