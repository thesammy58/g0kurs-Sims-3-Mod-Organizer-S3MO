# S3MO 1.1.0: what changed

This version fixes four problems: slow startup, merges that redo everything, no subfolders, and broken layouts at high DPI. Your mods, profiles and settings work as before, and nothing has to be converted.

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

## High DPI

- **Framework and DPI mode:** moved from .NET 6, which is out of support, to .NET 10, and runs per-monitor DPI aware (configurable, see above).
- **Owner-drawn lists:** check box, text and icon positions in the drawn lists used fixed pixel offsets, and now scale with DPI. Text is vertically centred in the rows.
- **Check box clicks:** the check box you see is now also what you click. Before, the real click area was the list's own hidden check box, which moved differently at high DPI.
  - Space toggles the selected mods.
  - Double-clicking the check box no longer opens the mod window.
- **Saved layout:** window size, splitter positions and column widths are saved together with the DPI they were measured at, and scaled when restored. Windows saved on a monitor that's no longer there are moved back on screen.
- **Toolbar:** toolbar icons are scaled with the window.

## Other fixes

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
