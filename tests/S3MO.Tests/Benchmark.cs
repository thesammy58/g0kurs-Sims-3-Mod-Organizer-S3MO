using System.Diagnostics;
using System.Security.Cryptography;
using ns;
using s3molib;
using Orig = s3molib.Original;

namespace S3MO.Tests;

/// <summary>Times the original code against the new code on a generated mod collection.</summary>
public static class Benchmark
{
    public static void Run(int packageCount)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "bench");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var rnd = new Random(2024);
        var pool = Synthetic.MakePool(rnd, 4000);

        Console.WriteLine($"Generating {packageCount} packages...");
        int perMod = 4;
        int mods = Math.Max(1, packageCount / perMod);
        var modNames = new List<string>();
        long resources = 0, bytes = 0;
        for (int m = 0; m < mods; m++)
        {
            string name = $"Mod {m:0000}";
            modNames.Add(name);
            for (int p = 0; p < perMod; p++)
            {
                bool big = (m * perMod + p) % 400 == 7;   // a few large merged packs like many people have
                var res = Synthetic.MakeResources(rnd, big ? 20000 : rnd.Next(20, 450), 100, 1200, pool, 0.02, allowCompression: false);
                string file = Path.Combine(root, "Mods", name, $"part{p}.package");
                Synthetic.Write(file, res);
                resources += res.Count;
                bytes += res.Sum(r => (long)r.Stored.Length);
            }
        }
        Directory.CreateDirectory(Path.Combine(root, "Profiles", "Default"));
        File.WriteAllLines(Path.Combine(root, "Profiles", "Default", "modlist.txt"), modNames.Select(n => "+" + n));
        Console.WriteLine($"  {mods} mods, {mods * perMod} packages, {resources:N0} resources, {bytes / 1048576.0:0} MB\n");

        RunOn(root, "Default", allowSlowOriginal: true);
    }

    /// <summary>Shared by the synthetic benchmark and the real-data verify mode.</summary>
    public static void RunOn(string root, string profile, bool allowSlowOriginal)
    {
        // ---------------------------------------------------------------- startup
        Console.WriteLine("STARTUP (reading package indexes + conflict checks)");

        var sw = Stopwatch.StartNew();
        Program.LinuxQuirk(root, profile);
        var op = new Orig.ProfileModel(root);
        op.Setup(profile, save: false);
        var origUnreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in op.ModModels)
            foreach (var p in m.PackageModels)
            {
                try { _ = p.Package; }
                catch (Exception ex) { origUnreadable.Add(p.FilePath); Console.WriteLine($"  (original can't read {m.Name}\\{p.Name}: {ex.Message})"); }
            }
        double origLoad = sw.Elapsed.TotalSeconds;
        // the original's conflict check would stop at an unreadable package; leave those mods out of the timing
        var timedMods = op.ModModels.FindAll(m => m.PackageModels.All(p => !origUnreadable.Contains(p.FilePath)));

        double origFast = TimeOriginalFastCheck(timedMods, budgetSeconds: allowSlowOriginal ? 60 : 30, out bool extrapolated);
        Console.WriteLine($"  original: load indexes {origLoad:0.00}s, mod-vs-mod conflict check {(extrapolated ? "~" : "")}{FormatTime(origFast)}{(extrapolated ? " (extrapolated from a subset)" : "")}");
        Console.WriteLine($"            + in-mod and detailed conflict passes, + MD5 of every package file (not timed here)");

        sw.Restart();
        long md5Bytes = 0;
        using (var md5 = MD5.Create())
            foreach (var m in op.ModModels) foreach (var p in m.PackageModels)
                if (!origUnreadable.Contains(p.FilePath))
                    using (var fs = File.OpenRead(p.FilePath)) { md5.ComputeHash(fs); md5Bytes += fs.Length; }
        Console.WriteLine($"  original: MD5 pass over {md5Bytes / 1048576.0:0} MB took {sw.Elapsed.TotalSeconds:0.00}s (from OS cache; much slower from a cold disk)");

        string cacheFile = Path.Combine(Path.GetTempPath(), "s3mo-bench-" + Guid.NewGuid().ToString("N") + ".cache");
        try
        {
            sw.Restart();
            var np = new ProfileModel(root);
            np.Setup(profile, save: false);
            var cache = new PackageCache(cacheFile, Path.Combine(root, "Mods"));
            cache.Load();
            var r1 = np.Analyze(cache);
            np.ApplyAnalysis(r1);
            Console.WriteLine($"  new, first start (no cache): {sw.Elapsed.TotalSeconds:0.00}s for everything ({r1.Conflicts.ResourceCount:N0} resources, {r1.Conflicts.ConflictingKeyCount:N0} conflicting)");

            sw.Restart();
            var np2 = new ProfileModel(root);
            np2.Setup(profile, save: false);
            var cache2 = new PackageCache(cacheFile, Path.Combine(root, "Mods"));
            cache2.Load();
            var r2 = np2.Analyze(cache2);
            np2.ApplyAnalysis(r2);
            Console.WriteLine($"  new, later starts (cache):   {sw.Elapsed.TotalSeconds:0.00}s ({r2.CacheHits}/{r2.PackageCount} packages from cache)\n");

            // ---------------------------------------------------------------- full merge
            Console.WriteLine("FULL MERGE");
            var omods = op.ModModels.FindAll(m => m.Enabled);
            omods.Sort(new Orig.ModModelComparer(Orig.ModModelComparer.SortBy.Priority));
            var opk = new List<Orig.Package>();
            foreach (var m in omods)
            {
                var pms = m.PackageModels;
                pms.Sort((p1, p2) => StringLogicalComparer.Compare(p1.Name, p2.Name));
                opk.AddRange(pms.Where(p => !origUnreadable.Contains(p.FilePath)).Select(p => p.Package));
            }

            string outA = Path.Combine(Path.GetTempPath(), "s3mo-bench-orig");
            string outB = Path.Combine(Path.GetTempPath(), "s3mo-bench-new", "ModBuilds");
            if (Directory.Exists(outA)) Directory.Delete(outA, true);
            if (Directory.Exists(Path.GetDirectoryName(outB)!)) Directory.Delete(Path.GetDirectoryName(outB)!, true);

            sw.Restart();
            Orig.Package.Merge(outA, opk, 1_000_000_000);
            double origMerge = sw.Elapsed.TotalSeconds;
            Console.WriteLine($"  original merge: {origMerge:0.00}s");

            // Compare like with like: only packages the original also sees (top level of a mod folder, readable by the old reader).
            var build = np2.GetPackagesInBuildOrder(out var unreadable)
                .Where(p => !p.Name.Contains('\\') && !p.Name.Contains('/') && !origUnreadable.Contains(p.FilePath)).ToList();
            var sources = build.Select(p => BuildSource.FromFile(p.ModModel.Name + "\\" + p.Name, p.Package)).ToList();
            sw.Restart();
            var full = ModBuilder.Build(outB, sources, new BuildOptions());
            double newMerge = sw.Elapsed.TotalSeconds;
            Console.WriteLine($"  new full build: {newMerge:0.00}s ({full.BytesWritten / 1048576.0:0} MB, {full.OutputFileCount} file(s))  -> {origMerge / Math.Max(newMerge, 0.001):0.0}x faster");

            bool identical = Directory.GetFiles(outA, "*.package").All(f =>
                FilesEqual(f, Path.Combine(outB, Path.GetFileName(f)))) &&
                Directory.GetFiles(outA, "*.package").Length == Directory.GetFiles(outB, "*.package").Length;
            Console.WriteLine($"  output byte-identical to the original merge: {(identical ? "yes" : "NO")}\n");
            Directory.Delete(outA, true);

            // ---------------------------------------------------------------- incremental
            Console.WriteLine("INCREMENTAL (one package added since the last build)");
            if (sources.Count > 1)
            {
                // rebuild without the highest-priority package, then add it back
                var without = sources.Take(sources.Count - 1).ToList();
                ModBuilder.Build(outB, without, new BuildOptions { ForceFullRebuild = true });
                sw.Restart();
                var plan = ModBuilder.Plan(outB, sources, new BuildOptions());
                var inc = ModBuilder.Build(outB, sources, new BuildOptions());
                double incTime = sw.Elapsed.TotalSeconds;
                Console.WriteLine($"  {inc.Plan}");
                Console.WriteLine($"  took {incTime:0.000}s, wrote {inc.BytesWritten / 1048576.0:0.0} MB  (a full rebuild took {newMerge:0.00}s, the original merge {origMerge:0.00}s)");
                Console.WriteLine($"  up to date afterwards: {ModBuilder.Plan(outB, sources, new BuildOptions()).UpToDate}");
            }
            Directory.Delete(Path.GetDirectoryName(outB)!, true);
        }
        finally
        {
            try { File.Delete(cacheFile); } catch { }
        }
    }

    /// <summary>The original mod-vs-mod check (ModModel.FastCheckConflict) grows with the square of the collection,
    /// so it's timed on growing subsets until the budget is used up, then extrapolated.</summary>
    static double TimeOriginalFastCheck(List<Orig.ModModel> mods, double budgetSeconds, out bool extrapolated)
    {
        mods = mods.OrderBy(m => m.Priority).ToList();
        int n = Math.Max(2, Math.Min(mods.Count, 16));
        double lastTime = 0; int lastN = 0;
        var total = Stopwatch.StartNew();
        while (true)
        {
            var subset = mods.Take(n).ToList();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < subset.Count - 1; i++)
                for (int j = i + 1; j < subset.Count; j++)
                    Orig.PackageModel.FastCheckConflict(subset[i], subset[j]);
            lastTime = sw.Elapsed.TotalSeconds; lastN = n;

            if (n == mods.Count) { extrapolated = false; return lastTime; }
            if (total.Elapsed.TotalSeconds + lastTime * 4 > budgetSeconds) break;
            n = Math.Min(mods.Count, n * 2);
        }
        extrapolated = true;
        // resources grow ~linearly with mods; pairwise work with the square
        double ratio = (double)mods.Count / lastN;
        return lastTime * ratio * ratio;
    }

    static string FormatTime(double s) => s < 120 ? $"{s:0.00}s" : s < 7200 ? $"{s / 60:0.0} min" : $"{s / 3600:0.0} h";

    static bool FilesEqual(string a, string b)
    {
        if (!File.Exists(b)) return false;
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead(); using var sb = fb.OpenRead();
        var ba = new byte[1 << 20]; var bb = new byte[1 << 20];
        while (true)
        {
            int ra = sa.ReadAtLeast(ba, ba.Length, false);
            int rb = sb.ReadAtLeast(bb, bb.Length, false);
            if (ra != rb || !ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, rb))) return false;
            if (ra == 0) return true;
        }
    }
}

/// <summary>
/// Runs the comparison on a real S3MO folder without changing anything in it:
/// merged output goes to the temp folder and is deleted afterwards.
/// </summary>
public static class Verify
{
    public static void Run(string s3moFolder, string profile)
    {
        s3moFolder = Path.GetFullPath(s3moFolder);
        if (!Directory.Exists(Path.Combine(s3moFolder, "Mods")) || !Directory.Exists(Path.Combine(s3moFolder, "Profiles")))
            throw new Exception($"'{s3moFolder}' doesn't look like an S3MO folder (no Mods/Profiles inside).");

        if (profile == null)
        {
            string ini = Path.Combine(s3moFolder, "s3mo.ini");
            profile = File.Exists(ini) ? File.ReadAllLines(ini).FirstOrDefault(l => l.StartsWith("Profile="))?.Substring(8) : null;
            profile ??= "Default";
        }
        Console.WriteLine($"Verifying against '{s3moFolder}', profile '{profile}' (read-only; temp output in {Path.GetTempPath()})\n");

        long enabledBytes = 0;
        var p = new ProfileModel(s3moFolder);
        p.Setup(profile, save: false);
        foreach (var m in p.ModModels.Where(m => m.Enabled))
            foreach (var pk in m.Packages)
                enabledBytes += new FileInfo(pk.FilePath).Length;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!);
        Console.WriteLine($"Enabled packages total {enabledBytes / 1048576.0:0} MB; this needs about {2 * enabledBytes / 1048576.0:0} MB free in the temp folder ({drive.AvailableFreeSpace / 1048576.0:0} MB free).");
        if (drive.AvailableFreeSpace < 2.2 * enabledBytes)
            throw new Exception("Not enough free space in the temp folder.");

        int subfolderPackages = p.ModModels.Sum(m => m.Packages.Count(x => x.Name.Contains('\\') || x.Name.Contains('/')));
        if (subfolderPackages > 0)
            Console.WriteLine($"Note: {subfolderPackages} package(s) are in subfolders of mods. The old version ignores those, so the comparison below only covers top-level packages.\n");

        Benchmark.RunOn(s3moFolder, profile, allowSlowOriginal: false);
    }
}
