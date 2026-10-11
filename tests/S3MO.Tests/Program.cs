using System.Diagnostics;
using System.Security.Cryptography;
using ns;
using s3molib;
using Orig = s3molib.Original;

namespace S3MO.Tests;

public static class Program
{
    static int _failures;

    static void Check(bool ok, string what)
    {
        if (!ok)
        {
            _failures++;
            Console.WriteLine($"  FAIL: {what}");
        }
    }

    public static int Main(string[] args)
    {
        Logger.WarningLoggedEvent += s => { if (Verbose) Console.WriteLine("  [warn] " + s); };
        Logger.ErrorLoggedEvent += s => Console.WriteLine("  [error] " + s);

        string mode = args.Length > 0 ? args[0] : "test";
        try
        {
            switch (mode)
            {
                case "test":
                    RunAllTests();
                    break;
                case "soak":
                {
                    // many more random incremental sequences (slow)
                    string work = Path.Combine(AppContext.BaseDirectory, "soakwork");
                    if (Directory.Exists(work)) Directory.Delete(work, true);
                    Directory.CreateDirectory(work);
                    int n = args.Length > 1 ? int.Parse(args[1]) : 20;
                    for (int seed = 100; seed < 100 + n; seed++)
                        TestIncremental(work, seed, compactThreshold: (seed % 3) switch { 0 => 1.0, 1 => 0.35, _ => 0.1 });
                    break;
                }
                case "bench":
                    Benchmark.Run(args.Length > 1 ? int.Parse(args[1]) : 1200);
                    break;
                case "verify":
                    Verify.Run(args[1], args.Length > 2 ? args[2] : null);
                    break;
                default:
                    Console.WriteLine("usage: test | bench [packages] | verify <S3MO folder> [profile]");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            return 3;
        }

        if (_failures > 0)
        {
            Console.WriteLine($"\n{_failures} check(s) FAILED");
            return 1;
        }
        Console.WriteLine("\nAll checks passed.");
        return 0;
    }

    public static bool Verbose = false;

    // ------------------------------------------------------------------------------------------------

    static void RunAllTests()
    {
        string work = Path.Combine(AppContext.BaseDirectory, "testwork");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        TestLoadEquivalence(work);
        TestEdgeCases(work);
        foreach (int seed in new[] { 1, 2, 3 })
            TestConflictsAndMerge(work, seed);
        TestCache(work);
        TestSubfolders(work);
        foreach (int seed in new[] { 11, 12, 13, 14 })
            TestIncremental(work, seed, compactThreshold: seed % 2 == 0 ? 1.0 : 0.25);
        TestMovedPackages(work);
        TestDisabledFolders(work);
        TestConflictHighlight(work);
        TestHeaderNote(work);
        TestGameModsBackup(work);
        TestTamperDetection(work);
    }

    // ---------------------------------------------------------------- package reading

    static void TestLoadEquivalence(string work)
    {
        Console.WriteLine("Package.Load: new reader vs original reader");
        var rnd = new Random(7);
        string dir = Path.Combine(work, "load");
        var pool = Synthetic.MakePool(rnd, 50);
        int files = 0;
        for (int i = 0; i < 60; i++)
        {
            var res = Synthetic.MakeResources(rnd, rnd.Next(0, 400), 1, 3000, pool, 0.1);
            // original reader mis-parses flag 0x08, so only compare flags 0-7 here
            uint flags = (uint)rnd.Next(8);
            if (i % 5 == 0) { foreach (var r in res) r.Type = 0x034AEECB; flags |= 1; }
            string path = Path.Combine(dir, $"p{i}.package");
            Synthetic.Write(path, res, flags, indexFirst: i % 4 == 1);

            Orig.Package o;
            try { o = Orig.Package.Load(path); }
            catch (Exception) { if (res.Count == 0) continue; throw; } // original can't read some empty packages
            var n = Package.Load(path);
            files++;

            var oe = o.ResourceEntries; var ne = n.ResourceEntries;
            Check(oe.Count == ne.Count, $"{path}: entry count {oe.Count} vs {ne.Count}");
            for (int k = 0; k < Math.Min(oe.Count, ne.Count); k++)
            {
                var a = oe[k]; var b = ne[k];
                bool same = a.Type == b.Type && a.Group == b.Group && a.Instance == b.Instance && a.ChunkOffset == b.ChunkOffset
                    && a.FileSize == b.FileSize && a.MemSize == b.MemSize && a.Compressed == b.Compressed && a.Unknown2 == b.Unknown2
                    && a.ToString() == b.ToString();
                if (!same) { Check(false, $"{path}: entry {k} differs"); break; }
            }
            // data reads (raw and uncompressed) agree
            for (int k = 0; k < Math.Min(20, ne.Count); k++)
            {
                Check(o.GetRawData(oe[k]).AsSpan().SequenceEqual(n.GetRawData(ne[k])), "raw data");
                Check(o.GetUncompressedData(oe[k]).AsSpan().SequenceEqual(n.GetUncompressedData(ne[k])), "uncompressed data");
            }
            // conflict check agrees
            if (i > 0)
            {
                var prevO = Orig.Package.Load(Path.Combine(dir, $"p{i - 1}.package"));
                var prevN = Package.Load(Path.Combine(dir, $"p{i - 1}.package"));
                bool co = o.IsConflictingWith(prevO, out var lo, out _, false);
                bool cn = n.IsConflictingWith(prevN, out var ln, out _, false);
                Check(co == cn && lo.Count == ln.Count, "IsConflictingWith");
            }
        }

        // flag 0x08 (shared low instance) - original gets this wrong, check the new reader against the source data
        {
            var res = Synthetic.MakeResources(rnd, 100, 1, 100, pool, 0);
            foreach (var r in res) r.Instance = (r.Instance & 0xFFFFFFFF00000000UL) | 0x12345678;
            string path = Path.Combine(dir, "flag8.package");
            Synthetic.Write(path, res, 8);
            var n = Package.Load(path);
            Check(n.Entries.Count == 100 && n.Entries.Select(e => e.Instance).SequenceEqual(res.Select(r => r.Instance)), "flag 0x08 parsed correctly");
            Check(n.Entries.Zip(res).All(z => n.GetRawData(z.First).AsSpan().SequenceEqual(z.Second.Stored)), "flag 0x08 data offsets");
        }
        Console.WriteLine($"  compared {files} packages");
    }

    static void TestEdgeCases(string work)
    {
        Console.WriteLine("Edge cases");
        string dir = Path.Combine(work, "edge");
        Directory.CreateDirectory(dir);

        // uppercase extension is accepted now
        var rnd = new Random(3);
        string upper = Path.Combine(dir, "UPPER.PACKAGE");
        Synthetic.Write(upper, Synthetic.MakeResources(rnd, 5, 10, 20, new List<(uint, uint, ulong)>(), 0));
        Check(Package.Load(upper).Entries.Count == 5, "uppercase .PACKAGE loads");

        // empty package created by Package.New loads (original threw)
        string empty = Path.Combine(dir, "empty.package");
        Package.New(empty);
        Check(Package.Load(empty).Entries.Count == 0, "blank package loads");

        // truncated package -> clear error, and the file is not left locked
        string trunc = Path.Combine(dir, "trunc.package");
        Synthetic.Write(trunc, Synthetic.MakeResources(rnd, 50, 100, 200, new List<(uint, uint, ulong)>(), 0));
        var bytes = File.ReadAllBytes(trunc);
        File.WriteAllBytes(trunc, bytes.AsSpan(0, bytes.Length - 40).ToArray());
        bool threw = false;
        try { Package.Load(trunc); } catch { threw = true; }
        Check(threw, "truncated index throws");
        File.Delete(trunc); // would fail on Windows if the handle leaked
        Check(!File.Exists(trunc), "file not locked after failed load");

        // Merge of nothing produces one empty package that loads
        string mdir = Path.Combine(dir, "mergeempty");
        Package.Merge(mdir, new List<Package>());
        Check(Package.Load(Path.Combine(mdir, "Merge1.package")).Entries.Count == 0, "empty merge");
    }

    // ---------------------------------------------------------------- conflicts + full merge vs original

    sealed class Scenario
    {
        public string Root = "";
        public string Mods => Path.Combine(Root, "Mods");
        public List<string> ModNames = new();
        public Random Rnd = new(1);
        public List<(uint, uint, ulong)> Pool = new();
        public int Counter;
    }

    static Scenario MakeScenario(string root, int seed, int mods, int maxPackages, int maxResources, int maxSize, double shared, bool subfolders = false)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var sc = new Scenario { Root = root, Rnd = new Random(seed) };
        sc.Pool = Synthetic.MakePool(sc.Rnd, Math.Max(20, mods * 4));
        for (int m = 0; m < mods; m++)
        {
            string name = $"Mod {m:000} {(char)('A' + sc.Rnd.Next(26))}";
            sc.ModNames.Add(name);
            int pkgs = sc.Rnd.Next(1, maxPackages + 1);
            for (int p = 0; p < pkgs; p++)
                AddPackage(sc, name, maxResources, maxSize, shared, subfolders && sc.Rnd.Next(3) == 0);
        }
        // profile with random order and enabled state
        var order = sc.ModNames.OrderBy(_ => sc.Rnd.Next()).ToList();
        Directory.CreateDirectory(Path.Combine(root, "Profiles", "Test"));
        File.WriteAllLines(Path.Combine(root, "Profiles", "Test", "modlist.txt"), order.Select(n => (sc.Rnd.Next(5) == 0 ? "-" : "+") + n));
        LinuxQuirk(root, "Test");
        return sc;
    }

    static string AddPackage(Scenario sc, string mod, int maxResources, int maxSize, double shared, bool inSubfolder = false)
    {
        var res = Synthetic.MakeResources(sc.Rnd, sc.Rnd.Next(1, maxResources + 1), 1, maxSize, sc.Pool, shared);
        // some duplicate keys inside a single package
        if (res.Count > 3 && sc.Rnd.Next(10) == 0) { res[1].Type = res[0].Type; res[1].Group = res[0].Group; res[1].Instance = res[0].Instance; }
        string sub = inSubfolder ? Path.Combine("Sub" + sc.Rnd.Next(3), "") : "";
        string file = Path.Combine(sc.Mods, mod, sub, $"pkg{sc.Counter++:0000}_{sc.Rnd.Next(1000)}.package");
        Synthetic.Write(file, res, (uint)sc.Rnd.Next(8), sc.Rnd.Next(6) == 0);
        return file;
    }

    /// <summary>
    /// The original code builds paths like "Profiles\\Test\\modlist.txt" with literal backslashes. On Windows that's a
    /// path; on Linux (where these tests also run) it's a single odd file name in the root. Give the original its file.
    /// </summary>
    public static void LinuxQuirk(string root, string profile)
    {
        if (OperatingSystem.IsWindows()) return;
        string real = Path.Combine(root, "Profiles", profile, "modlist.txt");
        if (File.Exists(real))
            File.Copy(real, Path.Combine(root, $"Profiles\\{profile}\\modlist.txt"), true);
    }

    /// <summary>Points the original code (which only knows the program folder) at a scenario by copying it there.</summary>
    static void StageForOriginal(Scenario sc)
    {
        string baseDir = AppContext.BaseDirectory;
        foreach (string d in new[] { "Mods", "Profiles" })
        {
            string target = Path.Combine(baseDir, d);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            CopyDir(Path.Combine(sc.Root, d), target);
        }
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (string d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static void TestConflictsAndMerge(string work, int seed)
    {
        Console.WriteLine($"Conflicts + full merge vs original (seed {seed})");
        var sc = MakeScenario(Path.Combine(work, $"cm{seed}"), seed, mods: 40, maxPackages: 5, maxResources: 150, maxSize: 4000, shared: 0.05);

        // ---- original
        var op = new Orig.ProfileModel(sc.Root);
        op.Setup("Test");
        op.CheckModModelConflicts();
        op.CheckPackageConflicts();
        op.CheckDetailedConflicts();

        // ---- new
        var np = new ProfileModel(sc.Root);
        np.Setup("Test");
        var analysis = np.Analyze(null);
        np.ApplyAnalysis(analysis);

        int modConf = 0, inModConf = 0, detail = 0;
        foreach (var om in op.ModModels)
        {
            var nm = np.GetModModel(om.Name);
            Check(om.Priority == nm.Priority && om.Enabled == nm.Enabled, $"{om.Name}: priority/enabled");
            var a = om.ConflictingModModels.Select(m => m.Name).ToHashSet();
            var b = nm.ConflictingModModels.Select(m => m.Name).ToHashSet();
            Check(a.SetEquals(b), $"{om.Name}: conflicting mods {a.Count} vs {b.Count}");
            modConf += a.Count;

            foreach (var opk in om.PackageModels)
            {
                Check(nm.TryGetPackageModel(opk.Name, out var npk), $"package {opk.Name} exists");
                if (npk == null) continue;
                var c = opk.ConflictingPackageModelsInModModel.Select(p => p.Name).ToHashSet();
                var d = npk.ConflictingPackageModelsInModModel.Select(p => p.Name).ToHashSet();
                Check(c.SetEquals(d), $"{om.Name}/{opk.Name}: in-mod conflicts {c.Count} vs {d.Count}");
                inModConf += c.Count;

                var ro = opk.GetConflictingResources().ToHashSet();
                var rn = npk.GetConflictingResources().ToHashSet();
                Check(ro.SetEquals(rn), $"{om.Name}/{opk.Name}: conflicting resources {ro.Count} vs {rn.Count}");
                foreach (string r in ro.Intersect(rn))
                {
                    var x = opk.GetConflictingPackageModelsByResource(r).Select(p => p.ModModel.Name + "/" + p.Name).ToHashSet();
                    var y = npk.GetConflictingPackageModelsByResource(r).Select(p => p.ModModel.Name + "/" + p.Name).ToHashSet();
                    Check(x.SetEquals(y), $"{opk.Name} {r}: detail");
                    detail++;
                }
            }
        }
        Console.WriteLine($"  mod conflicts {modConf}, in-mod {inModConf}, detailed resource links {detail}");
        Check(modConf > 0 && inModConf > 0 && detail > 0, "scenario actually has conflicts");

        // ---- merge: original BuildMods order
        var omods = op.ModModels.FindAll(m => m.Enabled);
        omods.Sort(new Orig.ModModelComparer(Orig.ModModelComparer.SortBy.Priority));
        var oPackages = new List<Orig.Package>();
        foreach (var m in omods)
        {
            var pms = m.PackageModels;
            pms.Sort((p1, p2) => StringLogicalComparer.Compare(p1.Name, p2.Name));
            oPackages.AddRange(pms.Select(p => p.Package));
        }
        uint limit = 600_000; // small so several Merge files are produced
        string outOrig = Path.Combine(work, $"cm{seed}_orig");
        Orig.Package.Merge(outOrig, oPackages, limit);

        var nPackages = np.GetPackagesInBuildOrder(out var unreadable);
        Check(unreadable.Count == 0, "no unreadable packages");
        Check(nPackages.Select(p => p.ModModel.Name + "/" + p.Name).SequenceEqual(omods.SelectMany(m => m.PackageModels.OrderBy(p => p.Name, Comparer<string>.Create(StringLogicalComparer.Compare)).Select(p => m.Name + "/" + p.Name))), "build order matches");

        string outNewMerge = Path.Combine(work, $"cm{seed}_newmerge");
        Package.Merge(outNewMerge, nPackages.Select(p => p.Package).ToList(), limit);

        string outBuilder = Path.Combine(work, $"cm{seed}_builder", "ModBuilds");
        var sources = nPackages.Select(p => BuildSource.FromFile(p.ModModel.Name + "\\" + p.Name, p.Package)).ToList();
        var result = ModBuilder.Build(outBuilder, sources, new BuildOptions { SizeLimit = limit });
        Check(result.Plan.FullRebuild, "first build is full");

        CompareFolders(outOrig, outNewMerge, "Package.Merge");
        CompareFolders(outOrig, outBuilder, "ModBuilder full build");

        // a second plan right away must be up to date
        var plan2 = ModBuilder.Plan(outBuilder, sources, new BuildOptions { SizeLimit = limit });
        Check(plan2.UpToDate, "plan after build is up to date: " + plan2);
    }

    static void CompareFolders(string a, string b, string what)
    {
        var fa = Directory.GetFiles(a, "*.package").Select(Path.GetFileName).OrderBy(x => x).ToList();
        var fb = Directory.GetFiles(b, "*.package").Select(Path.GetFileName).OrderBy(x => x).ToList();
        Check(fa.SequenceEqual(fb), $"{what}: same files ({fa.Count} vs {fb.Count})");
        int same = 0;
        foreach (var f in fa.Intersect(fb))
        {
            bool eq = File.ReadAllBytes(Path.Combine(a, f!)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(b, f!)));
            Check(eq, $"{what}: {f} byte-identical");
            if (eq) same++;
        }
        Console.WriteLine($"  {what}: {same}/{fa.Count} merged files byte-identical to the original");
    }

    // ---------------------------------------------------------------- cache

    static void TestCache(string work)
    {
        Console.WriteLine("Package index cache");
        var sc = MakeScenario(Path.Combine(work, "cache"), 21, mods: 15, maxPackages: 4, maxResources: 100, maxSize: 500, shared: 0.05);
        string cacheFile = Path.Combine(sc.Root, "s3mo.cache");

        var cache = new PackageCache(cacheFile, sc.Mods);
        cache.Load();
        var p1 = new ProfileModel(sc.Root); p1.Setup("Test");
        var r1 = p1.Analyze(cache); p1.ApplyAnalysis(r1);
        Check(r1.CacheHits == 0 && File.Exists(cacheFile), "first scan reads everything and writes the cache");

        // new cache object (= next program start)
        var cache2 = new PackageCache(cacheFile, sc.Mods);
        cache2.Load();
        var p2 = new ProfileModel(sc.Root); p2.Setup("Test");
        var r2 = p2.Analyze(cache2); p2.ApplyAnalysis(r2);
        Check(r2.CacheHits == r2.PackageCount, $"second start: all from cache ({r2.CacheHits}/{r2.PackageCount})");
        Check(SameConflicts(p1, p2), "cached analysis gives same conflicts");

        // change one package
        string changed = Directory.GetFiles(sc.Mods, "*.package", SearchOption.AllDirectories)[3];
        Synthetic.Write(changed, Synthetic.MakeResources(sc.Rnd, 7, 10, 20, sc.Pool, 0.5));
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));
        var cache3 = new PackageCache(cacheFile, sc.Mods);
        cache3.Load();
        var p3 = new ProfileModel(sc.Root); p3.Setup("Test");
        var r3 = p3.Analyze(cache3);
        Check(r3.CacheHits == r3.PackageCount - 1, $"one changed package re-read ({r3.PackageCount - r3.CacheHits} misses)");
        var pm = p3.ModModels.SelectMany(m => m.Packages).First(p => p.FilePath == changed);
        Check(pm.Package.Entries.Count == 7, "changed package has new content");

        // corrupt cache -> ignored
        File.WriteAllBytes(cacheFile, new byte[] { 1, 2, 3, 4, 5 });
        var cache4 = new PackageCache(cacheFile, sc.Mods);
        cache4.Load();
        var p4 = new ProfileModel(sc.Root); p4.Setup("Test");
        var r4 = p4.Analyze(cache4);
        Check(r4.CacheHits == 0 && r4.FailedCount == 0, "corrupt cache is ignored");

        // broken package -> reported, not fatal, excluded from build order
        string broken = Directory.GetFiles(sc.Mods, "*.package", SearchOption.AllDirectories)[5];
        File.WriteAllBytes(broken, new byte[200]);
        var p5 = new ProfileModel(sc.Root); p5.Setup("Test");
        var r5 = p5.Analyze(new PackageCache(cacheFile, sc.Mods));
        Check(r5.FailedCount == 1, "broken package reported");
        p5.ModModels.ForEach(m => m.Update(m.Name, true, m.Priority));
        p5.GetPackagesInBuildOrder(out var unreadable);
        Check(unreadable.Count == 1 && unreadable[0].FilePath == broken, "broken package left out of the build");
    }

    static bool SameConflicts(ProfileModel a, ProfileModel b)
    {
        foreach (var ma in a.ModModels)
        {
            var mb = b.GetModModel(ma.Name);
            if (!ma.ConflictingModModels.Select(m => m.Name).ToHashSet().SetEquals(mb.ConflictingModModels.Select(m => m.Name))) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- subfolders

    static void TestSubfolders(string work)
    {
        Console.WriteLine("Subfolders inside mods");
        string root = Path.Combine(work, "sub");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var rnd = new Random(5);
        var none = new List<(uint, uint, ulong)>();
        string mod = Path.Combine(root, "Mods", "Hair Pack");
        Synthetic.Write(Path.Combine(mod, "top.package"), Synthetic.MakeResources(rnd, 3, 10, 20, none, 0));
        Synthetic.Write(Path.Combine(mod, "Female", "hair.package"), Synthetic.MakeResources(rnd, 4, 10, 20, none, 0));
        Synthetic.Write(Path.Combine(mod, "Male", "hair.package"), Synthetic.MakeResources(rnd, 5, 10, 20, none, 0)); // same file name, different folder
        Synthetic.Write(Path.Combine(mod, "Male", "Deep", "x.PACKAGE"), Synthetic.MakeResources(rnd, 2, 10, 20, none, 0));
        File.WriteAllText(Path.Combine(mod, "Female", "readme.txt"), "hi");
        Directory.CreateDirectory(Path.Combine(root, "Profiles", "Test"));
        File.WriteAllLines(Path.Combine(root, "Profiles", "Test", "modlist.txt"), new[] { "+Hair Pack" });

        var p = new ProfileModel(root);
        p.Setup("Test");
        var m = p.GetModModel("Hair Pack");
        var names = m.GetSortedPackages().Select(x => x.Name.Replace('\\', '/')).ToList();
        Check(names.Count == 4, $"found 4 packages ({string.Join(", ", names)})");
        Check(names.Contains("Female/hair.package") && names.Contains("Male/hair.package") && names.Contains("Male/Deep/x.PACKAGE"), "relative names");
        Check(m.GetOtherFiles().Select(x => x.Replace('\\', '/')).SequenceEqual(new[] { "Female/readme.txt" }), "other files listed with folder");
        p.ApplyAnalysis(p.Analyze(null));
        var build = p.GetPackagesInBuildOrder(out _);
        Check(build.Count == 4, "all four go into the build");

        // rename keeps relative names working
        p.RenameMod("Hair Pack", "Hair Pack 2");
        var m2 = p.GetModModel("Hair Pack 2");
        Check(m2.Packages.All(x => File.Exists(x.FilePath)), "paths valid after rename");
    }

    // ---------------------------------------------------------------- incremental builds

    static List<BuildSource> SourcesFor(ProfileModel p, PackageCache cache)
    {
        var list = new List<BuildSource>();
        foreach (var pm in p.GetPackagesInBuildOrder(out _))
            list.Add(BuildSource.FromFile(pm.ModModel.Name + "\\" + pm.Name, cache.GetPackage(pm.FilePath, out _)));
        return list;
    }

    /// <summary>key -> stored bytes + flags, over all merged files; also checks there are no duplicate keys.</summary>
    static Dictionary<ResourceKey, (byte[] data, uint mem, ushort comp, ushort unk)> Effective(string folder, out int files)
    {
        var map = new Dictionary<ResourceKey, (byte[], uint, ushort, ushort)>();
        var f = Directory.GetFiles(folder, "*.package");
        files = f.Length;
        foreach (string file in f)
        {
            var pkg = Package.Load(file);
            using var reader = pkg.OpenReader();
            foreach (var e in pkg.Entries)
            {
                if (!map.TryAdd(e.Key, (reader.ReadRaw(e), e.MemSize, e.Compressed, e.Unknown2)))
                    Check(false, $"duplicate key {e.Key} in merged output ({Path.GetFileName(file)})");
            }
        }
        return map;
    }

    static bool SameContent(Dictionary<ResourceKey, (byte[] data, uint mem, ushort comp, ushort unk)> a, Dictionary<ResourceKey, (byte[] data, uint mem, ushort comp, ushort unk)> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var o)) return false;
            if (o.mem != kv.Value.mem || o.comp != kv.Value.comp || o.unk != kv.Value.unk || !o.data.AsSpan().SequenceEqual(kv.Value.data)) return false;
        }
        return true;
    }

    static void TestIncremental(string work, int seed, double compactThreshold)
    {
        Console.WriteLine($"Incremental builds (seed {seed}, compact at {compactThreshold:P0})");
        var sc = MakeScenario(Path.Combine(work, $"inc{seed}"), seed, mods: 25, maxPackages: 4, maxResources: 120, maxSize: 3000, shared: 0.08, subfolders: true);
        string build = Path.Combine(sc.Root, "Sims3Mods", "ModBuilds");
        string reference = Path.Combine(sc.Root, "Reference", "ModBuilds");
        var cache = new PackageCache(Path.Combine(sc.Root, "s3mo.cache"), sc.Mods);
        var opts = new BuildOptions { SizeLimit = 400_000, CompactThreshold = compactThreshold, CompactMinBytes = 0 };

        var p = new ProfileModel(sc.Root); p.Setup("Test");
        ModBuilder.Build(build, SourcesFor(p, cache), opts);

        int incremental = 0, full = 0;
        long incBytes = 0;
        var rnd = new Random(seed * 31);

        for (int step = 0; step < 40; step++)
        {
            string action;
            var mods = p.ModModels;
            int k = rnd.Next(8);
            switch (k)
            {
                case 0: // add a package to a mod
                {
                    var m = mods[rnd.Next(mods.Count)];
                    AddPackage(sc, m.Name, 80, 3000, 0.1, rnd.Next(2) == 0);
                    action = "add package to " + m.Name;
                    break;
                }
                case 1: // delete a package
                {
                    var all = Directory.GetFiles(sc.Mods, "*.package", SearchOption.AllDirectories);
                    string f = all[rnd.Next(all.Length)];
                    File.Delete(f);
                    action = "delete " + Path.GetFileName(f);
                    break;
                }
                case 2: // change a package's content
                {
                    var all = Directory.GetFiles(sc.Mods, "*.package", SearchOption.AllDirectories);
                    string f = all[rnd.Next(all.Length)];
                    Synthetic.Write(f, Synthetic.MakeResources(sc.Rnd, rnd.Next(1, 60), 1, 2000, sc.Pool, 0.2));
                    File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddSeconds(step + 1));
                    action = "modify " + Path.GetFileName(f);
                    break;
                }
                case 3: // toggle a mod
                {
                    var m = mods[rnd.Next(mods.Count)];
                    m.Update(m.Name, !m.Enabled, m.Priority);
                    p.Save("Test");
                    action = (m.Enabled ? "enable " : "disable ") + m.Name;
                    break;
                }
                case 4: // move a mod in the load order
                {
                    var ordered = mods.OrderBy(m => m.Priority).ToList();
                    var m = ordered[rnd.Next(ordered.Count)];
                    ordered.Remove(m);
                    ordered.Insert(rnd.Next(ordered.Count + 1), m);
                    for (int i = 0; i < ordered.Count; i++) ordered[i].Update(ordered[i].Name, ordered[i].Enabled, i);
                    p.Save("Test");
                    action = "reorder " + m.Name;
                    break;
                }
                case 5: // new mod
                {
                    string name = $"New {step}";
                    AddPackage(sc, name, 100, 3000, 0.15);
                    var lines = File.ReadAllLines(Path.Combine(sc.Root, "Profiles", "Test", "modlist.txt")).ToList();
                    lines.Add("+" + name);
                    File.WriteAllLines(Path.Combine(sc.Root, "Profiles", "Test", "modlist.txt"), lines);
                    action = "new mod " + name;
                    break;
                }
                case 6: // move a package into another folder (same or different mod)
                {
                    var all = Directory.GetFiles(sc.Mods, "*.package", SearchOption.AllDirectories);
                    string f = all[rnd.Next(all.Length)];
                    var m = mods[rnd.Next(mods.Count)];
                    string dest = Path.Combine(sc.Mods, m.Name, "Moved" + rnd.Next(3), Path.GetFileName(f));
                    if (!File.Exists(dest))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Move(f, dest);
                    }
                    action = "move " + Path.GetFileName(f) + " to " + m.Name;
                    break;
                }
                default: // nothing changed
                    action = "no change";
                    break;
            }

            // reload models the way the app does on Refresh
            p = new ProfileModel(sc.Root); p.Setup("Test");
            var sources = SourcesFor(p, cache);

            var plan = ModBuilder.Plan(build, sources, opts);
            if (k == 7) Check(plan.UpToDate, "no change -> up to date");
            var res = ModBuilder.Build(build, sources, opts);
            if (res.Plan.FullRebuild) full++; else if (!res.Plan.UpToDate) { incremental++; incBytes += res.BytesWritten; }
            if (Verbose) Console.WriteLine($"  step {step}: {action} -> {res.Plan}");

            // compare with a from-scratch build
            if (Directory.Exists(Path.GetDirectoryName(reference)!)) Directory.Delete(Path.GetDirectoryName(reference)!, true);
            ModBuilder.Build(reference, sources, new BuildOptions { SizeLimit = opts.SizeLimit, ForceFullRebuild = true });
            var a = Effective(build, out int filesA);
            var b = Effective(reference, out _);
            Check(SameContent(a, b), $"step {step} ({action}): incremental build content == full build content ({a.Count} vs {b.Count} resources)");

            var again = ModBuilder.Plan(build, sources, opts);
            Check(again.UpToDate, $"step {step}: up to date right after building ({again})");
        }
        long totalSize = Directory.GetFiles(build, "*.package").Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"  40 steps: {incremental} incremental, {full} full rebuilds; incremental builds wrote {incBytes / 1024} KB in total (build size {totalSize / 1024} KB)");
    }

    /// <summary>Key -> (file name|size|mtime of the winning package, stored size), computed independently of ModBuilder.</summary>
    static Dictionary<ResourceKey, (string who, uint size)> Winners(List<BuildSource> sources)
    {
        var map = new Dictionary<ResourceKey, (string, uint)>();
        for (int s = sources.Count - 1; s >= 0; s--)
        {
            string id = sources[s].Id;
            string who = id.Substring(id.LastIndexOfAny(new[] { '\\', '/' }) + 1) + "|" + sources[s].Size + "|" + sources[s].LastWriteTicks;
            foreach (var e in sources[s].Package.Entries)
                if (!Package.IsIgnoredType(e.Type))
                    map.TryAdd(e.Key, (who, e.FileSize));
        }
        return map;
    }

    static void TestMovedPackages(string work)
    {
        Console.WriteLine("Moving packages between folders and mods");

        // 1) No conflicts at all: moving files around (subfolders, other mods, renamed mod folder) must write nothing.
        {
            var sc = MakeScenario(Path.Combine(work, "move1"), 41, mods: 12, maxPackages: 4, maxResources: 80, maxSize: 2000, shared: 0);
            foreach (var line in File.ReadAllLines(Path.Combine(sc.Root, "Profiles", "Test", "modlist.txt"))) { }
            File.WriteAllLines(Path.Combine(sc.Root, "Profiles", "Test", "modlist.txt"), sc.ModNames.Select(n => "+" + n));
            string build = Path.Combine(sc.Root, "S", "ModBuilds");
            var cache = new PackageCache(Path.Combine(sc.Root, "c"), sc.Mods);
            var p = new ProfileModel(sc.Root); p.Setup("Test");
            ModBuilder.Build(build, SourcesFor(p, cache), null);

            var files = Directory.GetFiles(sc.Mods, "*.package", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i += 2)
            {
                string dest = Path.Combine(sc.Mods, sc.ModNames[(i * 7) % sc.ModNames.Count], "Sub", "Deeper", Path.GetFileName(files[i]));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (!File.Exists(dest)) File.Move(files[i], dest);
            }
            p = new ProfileModel(sc.Root); p.Setup("Test");
            p.RenameMod(sc.ModNames[0], "Renamed Mod");
            p = new ProfileModel(sc.Root); p.Setup("Test");

            var sources = SourcesFor(p, cache);
            var plan = ModBuilder.Plan(build, sources, null);
            Check(!plan.FullRebuild && plan.BytesToWrite == 0 && plan.UpToDate, $"moves without conflicts write nothing ({plan})");
            var res = ModBuilder.Build(build, sources, null);
            Check(res.BytesWritten == 0, "nothing written");
        }

        // 2) With conflicts: only resources whose winner changes are written.
        foreach (int seed in new[] { 42, 43, 44 })
        {
            var sc = MakeScenario(Path.Combine(work, "move" + seed), seed, mods: 15, maxPackages: 4, maxResources: 80, maxSize: 2000, shared: 0.15);
            string build = Path.Combine(sc.Root, "S", "ModBuilds");
            var cache = new PackageCache(Path.Combine(sc.Root, "c"), sc.Mods);
            var opts = new BuildOptions { CompactThreshold = 1.0 };
            var p = new ProfileModel(sc.Root); p.Setup("Test");
            var before = SourcesFor(p, cache);
            ModBuilder.Build(build, before, opts);

            var rnd = new Random(seed);
            var files = Directory.GetFiles(sc.Mods, "*.package", SearchOption.AllDirectories);
            foreach (string f in files.Where(_ => rnd.Next(3) == 0))
            {
                string dest = Path.Combine(sc.Mods, sc.ModNames[rnd.Next(sc.ModNames.Count)], "Moved", Path.GetFileName(f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (!File.Exists(dest)) File.Move(f, dest);
            }
            p = new ProfileModel(sc.Root); p.Setup("Test");
            var after = SourcesFor(p, cache);

            var w0 = Winners(before); var w1 = Winners(after);
            long expected = w1.Where(kv => !w0.TryGetValue(kv.Key, out var o) || o.who != kv.Value.who).Sum(kv => (long)kv.Value.size);
            int expectedRemoved = w0.Keys.Count(k => !w1.ContainsKey(k));
            long total = w1.Sum(kv => (long)kv.Value.size);

            var plan = ModBuilder.Plan(build, after, opts);
            Check(!plan.FullRebuild && plan.BytesToWrite == expected && plan.Removed == expectedRemoved,
                $"seed {seed}: writes only changed winners ({plan.BytesToWrite} vs expected {expected} of {total} bytes)");
            ModBuilder.Build(build, after, opts);

            string reference = Path.Combine(sc.Root, "R", "ModBuilds");
            ModBuilder.Build(reference, after, new BuildOptions { ForceFullRebuild = true });
            Check(SameContent(Effective(build, out _), Effective(reference, out _)), $"seed {seed}: content after moves == full build");
            Console.WriteLine($"  seed {seed}: moved {files.Length / 3}ish packages, rewrote {expected / 1024} KB of {total / 1024} KB");
        }
    }

    static void TestDisabledFolders(string work)
    {
        Console.WriteLine("Switching folders inside mods on and off");
        string root = Path.Combine(work, "folders");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var rnd = new Random(9);
        var pool = Synthetic.MakePool(rnd, 30);
        string items = Path.Combine(root, "Mods", "CAS Items");
        string other = Path.Combine(root, "Mods", "Other");
        // every package shares keys with "Other", so conflicts show up wherever a package is enabled
        List<Synthetic.Res> Conflicting(int n) { var r = Synthetic.MakeResources(rnd, n, 10, 200, pool, 0); for (int i = 0; i < r.Count; i++) { var k = pool[i % pool.Count]; r[i].Type = k.Item1; r[i].Group = k.Item2; r[i].Instance = k.Item3; } return r.GroupBy(x => (x.Type, x.Group, x.Instance)).Select(g => g.First()).ToList(); }
        Synthetic.Write(Path.Combine(items, "top.package"), Synthetic.MakeResources(rnd, 5, 10, 100, pool, 0));
        Synthetic.Write(Path.Combine(items, "Female", "hair.package"), Conflicting(10));
        Synthetic.Write(Path.Combine(items, "Female", "Hair", "Long", "long.package"), Synthetic.MakeResources(rnd, 6, 10, 100, pool, 0));
        Synthetic.Write(Path.Combine(items, "Male", "beard.package"), Synthetic.MakeResources(rnd, 7, 10, 100, pool, 0));
        Directory.CreateDirectory(Path.Combine(items, "Empty"));
        Synthetic.Write(Path.Combine(other, "other.package"), Conflicting(30));
        Directory.CreateDirectory(Path.Combine(root, "Profiles", "Default"));
        File.WriteAllLines(Path.Combine(root, "Profiles", "Default", "modlist.txt"), new[] { "+CAS Items", "+Other" });
        string F(params string[] parts) => Path.Combine(parts);

        var p = new ProfileModel(root); p.Setup("Default");
        var mod = p.GetModModel("CAS Items");
        Check(mod.RootFolder.Children.Select(c => c.Name).SequenceEqual(new[] { "Empty", "Female", "Male" }), "folder tree top level");
        Check(mod.FindFolder(F("Female", "Hair", "Long"))?.Depth == 3, "nested folder depth");
        Check(mod.GetPackagesIn("").Count == 1 && mod.GetPackagesIn("Female").Count == 1 && mod.GetPackagesUnder("Female").Count == 2, "packages per folder");

        p.ApplyAnalysis(p.Analyze(null));
        Check(mod.ConflictingModModels.Count == 1, "conflict through Female\\hair.package");
        Check(p.GetPackagesInBuildOrder(out _).Count == 5, "all 5 packages in the build");

        p.SetFolderEnabled(mod, "Female", false);
        p.Save("Default");
        Check(File.ReadAllText(Path.Combine(root, "Profiles", "Default", "disabledfolders.txt")).Trim() == "CAS Items\\Female", "saved as 'CAS Items\\Female'");
        Check(File.ReadAllLines(Path.Combine(root, "Profiles", "Default", "modlist.txt")).SequenceEqual(new[] { "+CAS Items", "+Other" }), "modlist.txt unchanged format");

        p = new ProfileModel(root); p.Setup("Default");
        mod = p.GetModModel("CAS Items");
        Check(!p.IsFolderEnabled(mod, "Female") && p.IsFolderEnabled(mod, F("Female", "Hair")), "own state reloaded");
        Check(!p.IsFolderEffectivelyEnabled(mod, F("Female", "Hair", "Long")) && p.IsFolderEffectivelyEnabled(mod, "Male"), "effective state follows parents");
        Check(p.HasDisabledFoldersBelow(mod, "") && !p.HasDisabledFoldersBelow(mod, "Male"), "partly-on detection");
        var build = p.GetPackagesInBuildOrder(out _).Select(x => x.Name).ToList();
        Check(build.Count == 3 && !build.Any(n => n.StartsWith("Female")), "Female folder left out of the build: " + string.Join(", ", build));
        p.ApplyAnalysis(p.Analyze(null));
        Check(mod.ConflictingModModels.Count == 0, "no conflicts once the conflicting folder is off");

        // turning the mod off and on keeps the folder choice
        mod.Update(mod.Name, false, mod.Priority); p.Save("Default");
        mod.Update(mod.Name, true, mod.Priority); p.Save("Default");
        Check(!p.IsFolderEnabled(mod, "Female"), "folder choice survives mod off/on");

        // incremental build drops exactly those resources
        string buildDir = Path.Combine(root, "S", "ModBuilds");
        var cache = new PackageCache(Path.Combine(root, "c"), Path.Combine(root, "Mods"));
        p.SetFolderEnabled(mod, "Female", true);
        ModBuilder.Build(buildDir, SourcesFor(p, cache), new BuildOptions { CompactThreshold = 1 });
        p.SetFolderEnabled(mod, "Female", false);
        var srcs = SourcesFor(p, cache);
        var res = ModBuilder.Build(buildDir, srcs, new BuildOptions { CompactThreshold = 1 });
        Check(!res.Plan.FullRebuild && res.Plan.Removed + res.Plan.Replaced > 0, "folder off -> incremental update: " + res.Plan);
        string reference = Path.Combine(root, "R", "ModBuilds");
        ModBuilder.Build(reference, srcs, new BuildOptions { ForceFullRebuild = true });
        Check(SameContent(Effective(buildDir, out _), Effective(reference, out _)), "content matches a full build without the folder");

        // renaming the mod carries the folder setting along
        p.Save("Default");
        p.RenameMod("CAS Items", "CAS Stuff");
        p = new ProfileModel(root); p.Setup("Default");
        var renamed = p.GetModModel("CAS Stuff");
        Check(!p.IsFolderEnabled(renamed, "Female") && p.GetPackagesInBuildOrder(out _).Count == 3, "folder setting follows a rename");

        // entries for folders that no longer exist are dropped
        Directory.Move(Path.Combine(root, "Mods", "CAS Stuff", "Female"), Path.Combine(root, "Mods", "CAS Stuff", "Women"));
        p = new ProfileModel(root); p.Setup("Default");
        Check(!p.HasDisabledFoldersBelow(p.GetModModel("CAS Stuff"), ""), "stale entry dropped after the folder was renamed");
    }

    static void TestConflictHighlight(string work)
    {
        Console.WriteLine("Mod list colours for the selected mod or folder");
        string root = Path.Combine(work, "highlight");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var rnd = new Random(5);
        var pool = Synthetic.MakePool(rnd, 4);
        string F(params string[] parts) => Path.Combine(parts);
        // a package holding resources with the given instance numbers (same type/group), so shared numbers conflict
        void Pkg(string relative, params int[] ids)
        {
            var r = Synthetic.MakeResources(rnd, ids.Length, 10, 50, pool, 0, allowCompression: false);
            for (int i = 0; i < ids.Length; i++) { r[i].Type = 0x034AEECB; r[i].Group = 0; r[i].Instance = (ulong)ids[i]; }
            Synthetic.Write(Path.Combine(root, "Mods", relative), r);
        }
        Pkg(F("Fixes", "fix.package"), 1);
        Pkg(F("Store", "store.package"), 1, 2, 3, 4);
        Pkg(F("Off", "off.package"), 2);
        Pkg(F("Loose Mods", "Children", "c.package"), 3);
        Pkg(F("Loose Mods", "Music", "m.package"), 100);
        Pkg(F("CAS Items", "Female", "f.package"), 50);
        Pkg(F("CAS Items", "Female", "Hair", "h.package"), 4, 50, 60);
        Pkg(F("CAS Items", "MERGED", "merged.package"), 60);
        Pkg(F("CAS Items", "Male", "m.package"), 200);
        Directory.CreateDirectory(Path.Combine(root, "Profiles", "Default"));
        File.WriteAllLines(Path.Combine(root, "Profiles", "Default", "modlist.txt"), new[] { "+Fixes", "+Store", "-Off", "+Loose Mods", "+CAS Items" });

        var p = new ProfileModel(root); p.Setup("Default");
        p.ApplyAnalysis(p.Analyze(null));
        string Show(ISet<string> s) => "{" + string.Join(", ", s.OrderBy(x => x)) + "}";

        // the Store mod: wins over Fixes, loses to the folders in Loose Mods and CAS Items that hold the overlapping packages
        var losing = new HashSet<string>(); var winning = new HashSet<string>();
        ConflictHighlight.Compute(p.GetModModel("Store"), "", losing, winning);
        Check(losing.SetEquals(new[] { "Fixes" }), "Store overwrites Fixes only: " + Show(losing));
        Check(winning.SetEquals(new[] { "Loose Mods", F("Loose Mods", "Children"), "CAS Items", F("CAS Items", "Female"), F("CAS Items", "Female", "Hair") }),
              "rows overwriting Store include the folders on the way to the package: " + Show(winning));

        // a folder: other mods, plus other folders of its own mod (later name wins), but not the folders that hold it
        losing.Clear(); winning.Clear();
        ConflictHighlight.Compute(p.GetModModel("CAS Items"), F("Female", "Hair"), losing, winning);
        bool femaleFirst = StringLogicalComparer.Compare(F("Female", "f.package"), F("Female", "Hair", "h.package")) < 0;
        var expectLosing = new HashSet<string> { "Store" };
        var expectWinning = new HashSet<string> { F("CAS Items", "MERGED") };
        (femaleFirst ? expectLosing : expectWinning).Add(F("CAS Items", "Female")); // the package sits directly in Female
        Check(losing.SetEquals(expectLosing), "Female\\Hair overwrites: " + Show(losing));
        Check(winning.SetEquals(expectWinning), "Female\\Hair is overwritten by: " + Show(winning));
        Check(!losing.Contains("CAS Items") && !winning.Contains("CAS Items"), "the selection's own mod row isn't coloured");

        // switched-off mods and folders without conflicts stay plain
        Check(!winning.Contains("Off") && !losing.Contains("Off"), "switched-off mod not coloured");
        losing.Clear(); winning.Clear();
        ConflictHighlight.Compute(p.GetModModel("CAS Items"), "Male", losing, winning);
        Check(losing.Count == 0 && winning.Count == 0, "folder without conflicts colours nothing");
    }

    static void TestHeaderNote(string work)
    {
        Console.WriteLine("Note about extra data in unused header fields");
        string root = Path.Combine(work, "headernote");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var rnd = new Random(3);
        var pool = Synthetic.MakePool(rnd, 4);
        for (int i = 0; i < 7; i++)
        {
            string path = Path.Combine(root, "Mods", "Tool Output", $"odd{i}.package");
            Synthetic.Write(path, Synthetic.MakeResources(rnd, 3, 10, 50, pool, 0));
            using var f = new FileStream(path, FileMode.Open, FileAccess.Write);
            f.Position = 24; f.Write(new byte[] { 0x5E, 0x1A, 0x33, 0x4F }); // a "created" date, as some tools write
        }
        Synthetic.Write(Path.Combine(root, "Mods", "Clean", "clean.package"), Synthetic.MakeResources(rnd, 3, 10, 50, pool, 0));
        Directory.CreateDirectory(Path.Combine(root, "Profiles", "Default"));
        File.WriteAllLines(Path.Combine(root, "Profiles", "Default", "modlist.txt"), new[] { "+Tool Output", "+Clean" });

        var lines = new List<string>();
        void Collect(string s) { lock (lines) lines.Add(s); }
        Logger.InfoLoggedEvent += Collect; Logger.WarningLoggedEvent += Collect;
        try
        {
            var cache = new PackageCache(Path.Combine(root, "s3mo.cache"), Path.Combine(root, "Mods"));
            var p = new ProfileModel(root); p.Setup("Default");
            p.Analyze(cache);
            var notes = lines.Where(l => l.Contains("file header")).ToList();
            Check(notes.Count == 1, $"one note for all 7 files, got {notes.Count}");
            Check(notes.Count == 1 && notes[0].Contains("7 packages") && notes[0].Contains("harmless") && notes[0].Contains("and 2 more") && !notes[0].Contains("clean.package"),
                  "note names the count, a few files and that it's harmless: " + notes.FirstOrDefault());
            Check(!lines.Any(l => l.Contains("non-zero unused")), "old per-file warning gone");

            lines.Clear();
            cache = new PackageCache(Path.Combine(root, "s3mo.cache"), Path.Combine(root, "Mods"));
            p = new ProfileModel(root); p.Setup("Default");
            var second = p.Analyze(cache);
            Check(second.CacheHits == 8, $"a new start reads s3mo.cache without being told to: {second.CacheHits} of 8 from the cache");
            Check(!lines.Any(l => l.Contains("file header")), "not repeated once the files come from the cache");
        }
        finally { Logger.InfoLoggedEvent -= Collect; Logger.WarningLoggedEvent -= Collect; }
    }

    static void TestGameModsBackup(string work)
    {
        Console.WriteLine("Backing up the game's Mods folder");
        string root = Path.Combine(work, "backup");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string game = Path.Combine(root, "The Sims 3", "Mods");
        string s3mo = Path.Combine(root, "S3MO");
        Directory.CreateDirectory(s3mo);
        void Put(string relative, string text) { string f = Path.Combine(game, relative); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, text); }

        // only what S3MO makes: nothing to offer
        Put("Resource.cfg", "Priority 495\r\nPackedFile \"ModBuilds/*.package\"\r\n\r\nPackedFile \"Extra/*.package\"\r\n");
        Put(Path.Combine("ModBuilds", "Merge0.package"), new string('x', 5000));
        Put(Path.Combine("ModBuilds", "s3mo.buildstate"), "state");
        var c = GameModsBackup.Scan(game);
        Check(c.UserFileCount == 0 && c.Files.Count == 1, $"S3MO's own Resource.cfg and ModBuilds don't count: {c.UserFileCount} user file(s), {c.Files.Count} to copy");
        Check(!GameModsBackup.HasBackup(s3mo), "no backup yet");

        // the game's usual setup with CC in Packages
        Put("Resource.cfg", "Priority 500\r\nPackedFile *.package\r\nPackedFile */*.package\r\n");
        Put(Path.Combine("Packages", "hair.package"), "hair");
        Put(Path.Combine("Packages", "Sub", "shoes.package"), "shoes");
        Put(Path.Combine("Overrides", "x.package"), "override");
        DateTime old = new DateTime(2015, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(game, "Packages", "hair.package"), old);
        c = GameModsBackup.Scan(game);
        Check(c.UserFileCount == 4 && c.Files.Count == 4 && c.Examples.Count == 3, $"CC and the game's Resource.cfg count: {c.UserFileCount}");
        Check(c.TotalBytes == c.Files.Sum(f => f.Length), "total size adds up");

        // an interrupted backup doesn't count and is cleaned up
        Directory.CreateDirectory(Path.Combine(GameModsBackup.BackupRoot(s3mo), "2026-01-01 10.00.incomplete", "Mods"));
        Check(!GameModsBackup.HasBackup(s3mo), "interrupted backup isn't a backup");

        var reports = new List<long>();
        var r = GameModsBackup.Create(game, s3mo, c, p => reports.Add(p.BytesDone), default, new DateTime(2026, 10, 11, 18, 45, 0));
        string copied = Path.Combine(r.BackupFolder, "Mods");
        Check(Path.GetFileName(r.BackupFolder) == "2026-10-11 18.45" && GameModsBackup.HasBackup(s3mo), "dated backup folder: " + r.BackupFolder);
        Check(r.FilesCopied == 4 && r.Failed.Count == 0, $"all 4 files copied ({r.FilesCopied}, {r.Failed.Count} failed)");
        Check(File.ReadAllText(Path.Combine(copied, "Packages", "Sub", "shoes.package")) == "shoes" && File.Exists(Path.Combine(copied, "Resource.cfg")), "folders and files kept");
        Check(!Directory.Exists(Path.Combine(copied, "ModBuilds")), "ModBuilds left out");
        Check(File.GetLastWriteTimeUtc(Path.Combine(copied, "Packages", "hair.package")) == old, "file dates kept");
        Check(File.Exists(Path.Combine(r.BackupFolder, "About this backup.txt")), "explains how to restore");
        Check(!Directory.EnumerateDirectories(GameModsBackup.BackupRoot(s3mo), "*.incomplete").Any(), "interrupted leftover removed");
        Check(reports.Count == 4 && reports[^1] == c.TotalBytes, "progress reaches the total");

        // a second backup in the same minute gets its own folder
        var r2 = GameModsBackup.Create(game, s3mo, c, null, default, new DateTime(2026, 10, 11, 18, 45, 0));
        Check(r2.BackupFolder != r.BackupFolder && Directory.Exists(r.BackupFolder), "second backup doesn't overwrite the first: " + Path.GetFileName(r2.BackupFolder));

        // deleting: size first, read-only files are no obstacle, the folder of backups goes once it's empty
        long size = GameModsBackup.SizeOf(r.BackupFolder);
        Check(size >= c.TotalBytes, $"backup size counts its files: {size} >= {c.TotalBytes}");
        File.SetAttributes(Path.Combine(r.BackupFolder, "Mods", "Packages", "hair.package"), FileAttributes.ReadOnly);
        GameModsBackup.Delete(r.BackupFolder);
        Check(!Directory.Exists(r.BackupFolder) && !Directory.EnumerateDirectories(GameModsBackup.BackupRoot(s3mo), "*.incomplete").Any(), "deleted completely, read-only file included");
        Check(GameModsBackup.HasBackup(s3mo), "the other backup is untouched");
        GameModsBackup.Delete(r2.BackupFolder);
        Check(!GameModsBackup.HasBackup(s3mo) && !Directory.Exists(GameModsBackup.BackupRoot(s3mo)), "empty backups folder removed too");
        Check(Directory.Exists(game) && File.Exists(Path.Combine(game, "Packages", "hair.package")), "the game's Mods folder itself is never touched");
    }

    static void TestTamperDetection(string work)
    {
        Console.WriteLine("Build state safety checks");
        var sc = MakeScenario(Path.Combine(work, "tamper"), 77, mods: 6, maxPackages: 2, maxResources: 30, maxSize: 500, shared: 0.1);
        string build = Path.Combine(sc.Root, "S", "ModBuilds");
        var cache = new PackageCache(Path.Combine(sc.Root, "c"), sc.Mods);
        var p = new ProfileModel(sc.Root); p.Setup("Test");
        var sources = SourcesFor(p, cache);
        ModBuilder.Build(build, sources, null);

        // stray package in ModBuilds
        File.WriteAllBytes(Path.Combine(build, "Other.package"), new byte[10]);
        var plan = ModBuilder.Plan(build, sources, null);
        Check(plan.FullRebuild && plan.Reason!.Contains("unexpected"), "stray file forces full rebuild: " + plan.Reason);
        ModBuilder.Build(build, sources, null);
        Check(!File.Exists(Path.Combine(build, "Other.package")), "stray file removed by full rebuild");

        // merged file modified
        string m1 = Path.Combine(build, "Merge1.package");
        File.SetLastWriteTimeUtc(m1, DateTime.UtcNow.AddDays(-1));
        plan = ModBuilder.Plan(build, sources, null);
        Check(plan.FullRebuild && plan.Reason!.Contains("changed outside"), "modified merge file forces full rebuild: " + plan.Reason);
        ModBuilder.Build(build, sources, null);

        // old-version build (Merge.bin, no state)
        File.Delete(Path.Combine(build, ModBuilder.StateFileName));
        File.WriteAllBytes(Path.Combine(build, "Merge.bin"), new byte[17]);
        plan = ModBuilder.Plan(build, sources, null);
        Check(plan.FullRebuild && plan.Reason!.Contains("older"), "old build detected: " + plan.Reason);
        ModBuilder.Build(build, sources, null);
        Check(!File.Exists(Path.Combine(build, "Merge.bin")) && File.Exists(Path.Combine(build, ModBuilder.StateFileName)), "old build replaced");

        // simulated crash during an incremental build: data appended but header not updated
        AddPackage(sc, sc.ModNames[0], 20, 300, 0);
        p = new ProfileModel(sc.Root); p.Setup("Test");
        p.ModModels.ForEach(m => m.Update(m.Name, true, m.Priority));
        sources = SourcesFor(p, cache);
        string last = Directory.GetFiles(build, "*.package").OrderBy(x => x).Last();
        var before = Effective(build, out _);
        using (var fs = new FileStream(last, FileMode.Append)) fs.Write(new byte[5000]);
        var stillValid = Effective(build, out _);
        Check(SameContent(before, stillValid), "appended bytes don't affect the existing build");
        plan = ModBuilder.Plan(build, sources, null);
        Check(plan.FullRebuild, "interrupted build detected -> full rebuild");
    }
}
