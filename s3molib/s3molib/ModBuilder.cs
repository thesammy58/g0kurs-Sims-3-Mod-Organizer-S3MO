using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace s3molib
{
    /// <summary>One package that goes into the build, with the fingerprint used to detect changes.</summary>
    public sealed class BuildSource
    {
        /// <summary>Stable identifier (path relative to the S3MO Mods folder).</summary>
        public string Id { get; }
        public Package Package { get; }
        public long Size { get; }
        public long LastWriteTicks { get; }

        public BuildSource(string id, Package package, long size, long lastWriteTicks)
        {
            Id = id;
            Package = package;
            Size = size;
            LastWriteTicks = lastWriteTicks;
        }

        /// <summary>Creates a source and reads the fingerprint from the package file on disk.</summary>
        public static BuildSource FromFile(string id, Package package)
        {
            FileInfo info = new FileInfo(package.FilePath);
            return new BuildSource(id, package, info.Length, info.LastWriteTimeUtc.Ticks);
        }
    }

    public sealed class BuildOptions
    {
        /// <summary>A new MergeN.package is started once the current one reaches this size (bytes).</summary>
        public long SizeLimit { get; set; } = 1_000_000_000;

        /// <summary>Ignore the previous build and write everything again (also removes unused space).</summary>
        public bool ForceFullRebuild { get; set; }

        /// <summary>When more than this fraction of ModBuilds would be unused space, rebuild from scratch instead of patching.</summary>
        public double CompactThreshold { get; set; } = 0.35;

        /// <summary>...but only once the unused space is at least this many bytes (small builds are cheap to keep patching).</summary>
        public long CompactMinBytes { get; set; } = 64L << 20;

        /// <summary>Optional progress callback (called on the building thread).</summary>
        public Action<BuildProgress> Progress { get; set; }
    }

    public sealed class BuildProgress
    {
        public string Message { get; internal set; }
        public long BytesDone { get; internal set; }
        public long BytesTotal { get; internal set; }
    }

    /// <summary>What a build would do, compared to the build that's already in ModBuilds.</summary>
    public sealed class BuildPlan
    {
        public bool FullRebuild { get; internal set; }
        /// <summary>Why a full rebuild is needed (null for incremental builds).</summary>
        public string Reason { get; internal set; }

        public int ResourceCount { get; internal set; }
        public int Unchanged { get; internal set; }
        public int Added { get; internal set; }
        public int Replaced { get; internal set; }
        public int Removed { get; internal set; }
        public long BytesToWrite { get; internal set; }

        public bool UpToDate => !FullRebuild && Added == 0 && Replaced == 0 && Removed == 0;

        public override string ToString() => FullRebuild
            ? $"full rebuild ({Reason}): {ResourceCount} resources, {BytesToWrite / 1048576.0:0.0} MB"
            : UpToDate ? "up to date"
            : $"incremental: {Added} added, {Replaced} replaced, {Removed} removed, {Unchanged} kept, {BytesToWrite / 1048576.0:0.0} MB to write";

        // Data carried from planning to building.
        internal BuildState State;
        internal List<Desired> Order;
        internal bool[] Keep;
        internal List<BuildState.Entry>[] KeptByFile;
        internal bool[] TouchedFiles;
    }

    internal readonly struct Desired
    {
        public readonly int Source;
        public readonly ResourceEntry Entry;
        public Desired(int source, ResourceEntry entry) { Source = source; Entry = entry; }
    }

    public sealed class BuildResult
    {
        public BuildPlan Plan { get; internal set; }
        public long BytesWritten { get; internal set; }
        public int ResourcesWritten { get; internal set; }
        public int OutputFileCount { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }
    }

    /// <summary>
    /// Builds ModBuilds/MergeN.package from the enabled packages.
    ///
    /// A full build writes exactly what the original merge wrote. In addition it saves a small build state
    /// (which source each merged resource came from), so the next build only has to write what changed:
    /// new or changed resources are appended after the existing data, a fresh index is written after them,
    /// and only then is the header pointer updated. Until that last step the old index is still valid, so an
    /// interrupted build leaves the previous build usable. Space taken by replaced resources is reclaimed by a
    /// full rebuild once it passes <see cref="BuildOptions.CompactThreshold"/>.
    /// </summary>
    public static class ModBuilder
    {
        public const string StateFileName = "s3mo.buildstate";
        private const long MaxPackageSize = 3_500_000_000; // offsets are 32-bit; stay well clear of 4 GB

        /// <param name="sources">Packages in ascending priority: when two contain the same resource, the later one wins.</param>
        public static BuildPlan Plan(string buildFolder, IReadOnlyList<BuildSource> sources, BuildOptions options)
        {
            options ??= new BuildOptions();
            BuildPlan plan = new BuildPlan();

            // Same order and first-wins rule as the original merge: highest priority first, index order within a package.
            var desired = new Dictionary<ResourceKey, int>();
            var order = new List<Desired>();
            long totalBytes = 0;
            for (int s = sources.Count - 1; s >= 0; s--)
            {
                foreach (ResourceEntry e in sources[s].Package.Entries)
                {
                    if (Package.IsIgnoredType(e.Type))
                        continue;
                    if (desired.TryAdd(e.Key, order.Count))
                    {
                        order.Add(new Desired(s, e));
                        totalBytes += e.FileSize;
                    }
                }
            }
            plan.Order = order;
            plan.ResourceCount = order.Count;

            string reason = null;
            BuildState state = options.ForceFullRebuild ? null : BuildState.TryLoad(buildFolder, out reason);
            if (options.ForceFullRebuild)
                reason = "requested";

            if (state == null)
                return Full(plan, reason, totalBytes);

            plan.State = state;
            bool[] keep = new bool[order.Count];
            var keptByFile = new List<BuildState.Entry>[state.Files.Count];
            bool[] touched = new bool[state.Files.Count];
            long[] liveKept = new long[state.Files.Count];
            for (int f = 0; f < keptByFile.Length; f++)
                keptByFile[f] = new List<BuildState.Entry>();

            // Which current source (if any) is the same, unchanged file as each source of the previous build.
            // First by path; then, for files that were moved or their mod folder renamed, by file name + size +
            // last-write time (a move keeps all three), so reorganizing folders doesn't rewrite unchanged data.
            var currentById = new Dictionary<string, int>(sources.Count, StringComparer.OrdinalIgnoreCase);
            var currentByFile = new Dictionary<string, int>(sources.Count, StringComparer.OrdinalIgnoreCase);
            for (int s = 0; s < sources.Count; s++)
            {
                currentById[sources[s].Id] = s;
                currentByFile.TryAdd(FileFingerprint(sources[s].Id, sources[s].Size, sources[s].LastWriteTicks), s);
            }
            int[] sameSource = new int[state.Sources.Count];
            for (int s = 0; s < state.Sources.Count; s++)
            {
                BuildState.SourceRecord old = state.Sources[s];
                if (currentById.TryGetValue(old.Id, out int c) && sources[c].Size == old.Size && sources[c].LastWriteTicks == old.LastWriteTicks)
                    sameSource[s] = c;
                else if (currentByFile.TryGetValue(FileFingerprint(old.Id, old.Size, old.LastWriteTicks), out c))
                    sameSource[s] = c;
                else
                    sameSource[s] = -1;
            }

            foreach (BuildState.Entry se in state.Entries)
            {
                if (!desired.TryGetValue(se.Key, out int di))
                {
                    plan.Removed++;
                    touched[se.File] = true;
                    continue;
                }

                Desired d = order[di];

                if (!keep[di] && sameSource[se.Source] == d.Source)
                {
                    keep[di] = true;
                    plan.Unchanged++;
                    var kept = se;
                    kept.Source = d.Source;     // re-point at the current source list
                    keptByFile[se.File].Add(kept);
                    liveKept[se.File] += se.FileSize;
                }
                else
                {
                    plan.Replaced++;
                    touched[se.File] = true;
                }
            }

            long bytesToWrite = 0;
            int toWrite = 0;
            for (int i = 0; i < order.Count; i++)
                if (!keep[i]) { bytesToWrite += order[i].Entry.FileSize; toWrite++; }

            plan.Added = toWrite - plan.Replaced;
            plan.BytesToWrite = bytesToWrite;
            plan.Keep = keep;
            plan.KeptByFile = keptByFile;
            plan.TouchedFiles = touched;

            if (plan.UpToDate)
                return plan;

            // Decide whether patching is still worth it.
            int last = state.Files.Count - 1;
            if (toWrite > 0)
                touched[last] = true;

            long dead = 0, total = 0;
            for (int f = 0; f < state.Files.Count; f++)
            {
                var file = state.Files[f];
                total += file.Length;
                dead += touched[f]
                    ? file.Length - Package.HeaderSize - liveKept[f]                       // old index becomes unused too
                    : file.IndexPosition - Package.HeaderSize - liveKept[f];
            }
            total += bytesToWrite + 32L * toWrite;

            if (plan.Unchanged == 0)
                return Full(plan, "nothing from the previous build can be kept", totalBytes);
            if (total > 0 && (double)dead / total > options.CompactThreshold && dead >= options.CompactMinBytes)
                return Full(plan, $"compacting, {100.0 * dead / total:0}% of ModBuilds is unused space", totalBytes);
            if (state.Files[last].Length > MaxPackageSize)
                return Full(plan, "the last merged package is too large to extend", totalBytes);

            return plan;
        }

        /// <summary>File name (without folders) + size + last-write time, used to recognize a file that was moved.</summary>
        private static string FileFingerprint(string id, long size, long lastWriteTicks)
        {
            int slash = id.LastIndexOfAny(new[] { '\\', '/' });
            string name = slash >= 0 ? id.Substring(slash + 1) : id;
            return $"{name}|{size}|{lastWriteTicks}";
        }

        private static BuildPlan Full(BuildPlan plan, string reason, long totalBytes)
        {
            plan.FullRebuild = true;
            plan.Reason = reason;
            plan.Added = plan.ResourceCount;
            plan.Replaced = plan.Removed = plan.Unchanged = 0;
            plan.BytesToWrite = totalBytes;
            plan.State = null;
            return plan;
        }

        public static BuildResult Build(string buildFolder, IReadOnlyList<BuildSource> sources, BuildOptions options)
        {
            options ??= new BuildOptions();
            Stopwatch sw = Stopwatch.StartNew();
            BuildPlan plan = Plan(buildFolder, sources, options);
            BuildResult result = new BuildResult { Plan = plan };

            if (plan.UpToDate)
            {
                result.OutputFileCount = plan.State.Files.Count;
            }
            else if (plan.FullRebuild)
            {
                BuildFull(buildFolder, sources, plan, options, result);
            }
            else
            {
                BuildIncremental(buildFolder, sources, plan, options, result);
            }

            result.Elapsed = sw.Elapsed;
            return result;
        }

        // ---------------------------------------------------------------- full build

        private static void BuildFull(string buildFolder, IReadOnlyList<BuildSource> sources, BuildPlan plan, BuildOptions options, BuildResult result)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(buildFolder));
            string name = Path.GetFileName(Path.GetFullPath(buildFolder));
            string temp = Path.Combine(parent, name + ".s3mo-new");
            string old = Path.Combine(parent, name + ".s3mo-old");

            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);

            var entries = new List<BuildState.Entry>(plan.Order.Count);
            var progress = new ProgressTracker(options.Progress, plan.BytesToWrite);

            using (var copier = new SourceCopier(sources))
            using (MergeWriter writer = new MergeWriter(temp, options.SizeLimit, 1))
            {
                foreach (Desired d in plan.Order)
                {
                    progress.Source(sources[d.Source]);
                    int length = copier.Read(d.Source, d.Entry);
                    ResourceEntry written = writer.Write(d.Entry, copier.Buffer, length);
                    entries.Add(BuildState.Entry.From(written, d.Source, writer.Completed.Count));
                    progress.Add(length);
                    result.BytesWritten += length;
                }
                writer.Complete();

                BuildState state = new BuildState { SizeLimit = options.SizeLimit };
                foreach (Package p in writer.Completed)
                    state.Files.Add(BuildState.FileRecord.From(p));
                AddSources(state, sources);
                state.Entries = entries;
                state.Save(temp);
                result.OutputFileCount = state.Files.Count;
            }
            result.ResourcesWritten = entries.Count;

            // Swap the finished build in. The old build stays in place until the new one is complete.
            progress.Message("Replacing previous build");
            if (Directory.Exists(old))
                Directory.Delete(old, true);
            if (Directory.Exists(buildFolder))
            {
                try { Directory.Move(buildFolder, old); }
                catch (IOException ex) { throw new IOException($"Couldn't replace the previous build in '{buildFolder}' - is the game (or another program) using it? {ex.Message}", ex); }
            }
            Directory.Move(temp, buildFolder);
            try
            {
                if (Directory.Exists(old))
                    Directory.Delete(old, true);
            }
            catch (Exception ex) { Logger.LogWarning($"Couldn't remove the old build folder '{old}': {ex.Message}"); }
        }

        // ---------------------------------------------------------------- incremental build

        private static void BuildIncremental(string buildFolder, IReadOnlyList<BuildSource> sources, BuildPlan plan, BuildOptions options, BuildResult result)
        {
            BuildState state = plan.State;
            int fileCount = state.Files.Count;
            var keptByFile = new List<List<BuildState.Entry>>(plan.KeptByFile);
            var newByFile = new List<List<BuildState.Entry>>();
            for (int f = 0; f < fileCount; f++)
                newByFile.Add(new List<BuildState.Entry>());
            var touched = new List<bool>(plan.TouchedFiles);

            var progress = new ProgressTracker(options.Progress, plan.BytesToWrite);

            // 1) Append new and changed resources to the last merged package (starting new packages at the size limit).
            int target = fileCount - 1;
            FileStream stream = null;
            BinaryWriter writer = null;
            try
            {
                using (var copier = new SourceCopier(sources))
                {
                    for (int i = 0; i < plan.Order.Count; i++)
                    {
                        if (plan.Keep[i])
                            continue;
                        Desired d = plan.Order[i];

                        if (stream == null)
                            stream = OpenForAppend(buildFolder, state.Files[target].Name, out writer);

                        if (stream.Position >= options.SizeLimit)
                        {
                            // Same rule as the full build: once a package reaches the limit, continue in a new one.
                            if (newByFile[target].Count > 0)
                                FinishFile(stream, writer, state, target, keptByFile[target], newByFile[target], buildFolder);
                            writer.Dispose();
                            stream.Dispose();

                            int number = NextMergeNumber(state);
                            string fileName = MergeWriter.FileNameFor(number);
                            Package.New(Path.Combine(buildFolder, fileName));
                            state.Files.Add(new BuildState.FileRecord { Name = fileName });
                            keptByFile.Add(new List<BuildState.Entry>());
                            newByFile.Add(new List<BuildState.Entry>());
                            touched.Add(true);
                            target = state.Files.Count - 1;
                            stream = OpenForAppend(buildFolder, fileName, out writer);
                        }

                        progress.Source(sources[d.Source]);
                        int length = copier.Read(d.Source, d.Entry);
                        uint offset = (uint)stream.Position;
                        writer.Write(copier.Buffer, 0, length);

                        var e = BuildState.Entry.From(d.Entry, d.Source, target);
                        e.Offset = offset;
                        newByFile[target].Add(e);
                        result.BytesWritten += length;
                        result.ResourcesWritten++;
                        progress.Add(length);
                    }
                }

                if (stream != null && newByFile[target].Count > 0)
                    FinishFile(stream, writer, state, target, keptByFile[target], newByFile[target], buildFolder);
            }
            finally
            {
                writer?.Dispose();
                stream?.Dispose();
            }

            // 2) Rewrite the index of any other package that lost resources.
            progress.Message("Updating merged package indexes");
            for (int f = 0; f < fileCount; f++)
            {
                if (!touched[f] || newByFile[f].Count > 0)
                    continue;
                using (FileStream s = OpenForAppend(buildFolder, state.Files[f].Name, out BinaryWriter w))
                using (w)
                    FinishFile(s, w, state, f, keptByFile[f], newByFile[f], buildFolder);
            }

            for (int f = 0; f < state.Files.Count; f++)
            {
                if (!touched[f] && newByFile[f].Count == 0)
                    continue;
                FileInfo info = new FileInfo(Path.Combine(buildFolder, state.Files[f].Name));
                var rec = state.Files[f];
                rec.Length = info.Length;
                rec.LastWriteTicks = info.LastWriteTimeUtc.Ticks;
                state.Files[f] = rec;
            }

            // 3) Save the new state last; if anything above failed, the old state no longer matches the files and the next build is a full one.
            var entries = new List<BuildState.Entry>(plan.ResourceCount);
            for (int f = 0; f < state.Files.Count; f++)
            {
                entries.AddRange(keptByFile[f]);
                entries.AddRange(newByFile[f]);
            }
            state.Entries = entries;
            state.Sources.Clear();
            AddSources(state, sources);
            state.SizeLimit = options.SizeLimit;
            state.Save(buildFolder);
            result.OutputFileCount = state.Files.Count;
        }

        private static FileStream OpenForAppend(string folder, string fileName, out BinaryWriter writer)
        {
            string path = Path.Combine(folder, fileName);
            FileStream s;
            try { s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 20); }
            catch (IOException ex) { throw new IOException($"Couldn't open '{path}' for writing - is the game running? {ex.Message}", ex); }
            s.Seek(0, SeekOrigin.End);
            writer = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true);
            return s;
        }

        /// <summary>Writes the index (kept + new entries) at the end of the file, then points the header at it.</summary>
        private static void FinishFile(FileStream stream, BinaryWriter w, BuildState state, int fileIndex, List<BuildState.Entry> kept, List<BuildState.Entry> added, string folder)
        {
            w.Flush();
            uint indexPosition = (uint)stream.Seek(0, SeekOrigin.End);
            int count = kept.Count + added.Count;

            w.Write(0);
            foreach (var list in new[] { kept, added })
            {
                foreach (BuildState.Entry e in list)
                {
                    w.Write(e.Key.Type);
                    w.Write(e.Key.Group);
                    w.Write((uint)(e.Key.Instance >> 32));
                    w.Write((uint)(e.Key.Instance & 0xFFFFFFFF));
                    w.Write(e.Offset);
                    w.Write(e.FileSize | 0x80000000);
                    w.Write(e.MemSize);
                    w.Write(e.Compressed);
                    w.Write(e.Unknown2);
                }
            }
            w.Flush();
            stream.Flush(true);

            // Header last: until here the old index is intact.
            stream.Position = 36;
            w.Write((uint)count);
            stream.Position = 44;
            w.Write((uint)(4 + count * 32));
            stream.Position = 64;
            w.Write(indexPosition);
            w.Flush();
            stream.Flush(true);

            for (int i = 0; i < added.Count; i++)
            {
                var e = added[i];
                e.File = fileIndex;
                added[i] = e;
            }

            // Length and last-write time are recorded after the file is closed (NTFS can update them on close).
            var rec = state.Files[fileIndex];
            rec.IndexPosition = indexPosition;
            rec.IndexCount = (uint)count;
            state.Files[fileIndex] = rec;
        }

        private static int NextMergeNumber(BuildState state)
        {
            int max = 0;
            foreach (var f in state.Files)
            {
                string n = Path.GetFileNameWithoutExtension(f.Name);
                if (n.StartsWith("Merge", StringComparison.OrdinalIgnoreCase) && int.TryParse(n.Substring(5), out int k))
                    max = Math.Max(max, k);
            }
            return max + 1;
        }

        private static void AddSources(BuildState state, IReadOnlyList<BuildSource> sources)
        {
            foreach (BuildSource s in sources)
                state.Sources.Add(new BuildState.SourceRecord { Id = s.Id, Size = s.Size, LastWriteTicks = s.LastWriteTicks });
        }

        /// <summary>Reads resources from the sources, keeping the current source file open and reusing one buffer.</summary>
        private sealed class SourceCopier : IDisposable
        {
            private readonly IReadOnlyList<BuildSource> _sources;
            private int _open = -1;
            private PackageReader _reader;
            public byte[] Buffer = new byte[1 << 16];

            public SourceCopier(IReadOnlyList<BuildSource> sources) { _sources = sources; }

            public int Read(int source, ResourceEntry entry)
            {
                if (source != _open)
                {
                    _reader?.Dispose();
                    _reader = _sources[source].Package.OpenReader();
                    _open = source;
                }
                return _reader.ReadRaw(entry, ref Buffer);
            }

            public void Dispose() => _reader?.Dispose();
        }

        private sealed class ProgressTracker
        {
            private readonly Action<BuildProgress> _callback;
            private readonly BuildProgress _p = new BuildProgress();
            private BuildSource _last;
            private long _sinceReport;

            public ProgressTracker(Action<BuildProgress> callback, long total)
            {
                _callback = callback;
                _p.BytesTotal = total;
            }

            public void Source(BuildSource s)
            {
                if (_callback == null || ReferenceEquals(s, _last))
                    return;
                _last = s;
                _p.Message = $"Merging {s.Id}";
                Report();
            }

            public void Message(string message)
            {
                if (_callback == null) return;
                _p.Message = message;
                Report();
            }

            public void Add(long bytes)
            {
                _p.BytesDone += bytes;
                _sinceReport += bytes;
                if (_callback != null && _sinceReport >= 8 << 20)
                    Report();
            }

            private void Report()
            {
                _sinceReport = 0;
                _callback(new BuildProgress { Message = _p.Message, BytesDone = _p.BytesDone, BytesTotal = _p.BytesTotal });
            }
        }
    }

    /// <summary>What's in ModBuilds: the merged files, the sources they were built from, and where each resource came from.</summary>
    internal sealed class BuildState
    {
        private const string Magic = "S3MOBUILD";
        private const int Version = 1;

        public struct FileRecord
        {
            public string Name;
            public long Length;
            public long LastWriteTicks;
            public uint IndexPosition;
            public uint IndexCount;

            public static FileRecord From(Package p)
            {
                FileInfo info = new FileInfo(p.FilePath);
                return new FileRecord
                {
                    Name = Path.GetFileName(p.FilePath),
                    Length = info.Length,
                    LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                    IndexPosition = p.IndexPosition,
                    IndexCount = p.IndexCount,
                };
            }
        }

        public struct SourceRecord
        {
            public string Id;
            public long Size;
            public long LastWriteTicks;
        }

        public struct Entry
        {
            public ResourceKey Key;
            public int Source;
            public int File;
            public uint Offset, FileSize, MemSize;
            public ushort Compressed, Unknown2;

            public static Entry From(ResourceEntry e, int source, int file) => new Entry
            {
                Key = e.Key,
                Source = source,
                File = file,
                Offset = e.ChunkOffset,
                FileSize = e.FileSize,
                MemSize = e.MemSize,
                Compressed = e.Compressed,
                Unknown2 = e.Unknown2,
            };
        }

        public long SizeLimit;
        public List<FileRecord> Files = new List<FileRecord>();
        public List<SourceRecord> Sources = new List<SourceRecord>();
        public List<Entry> Entries = new List<Entry>();

        public void Save(string folder)
        {
            string path = Path.Combine(folder, ModBuilder.StateFileName);
            string tmp = path + ".tmp";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (BinaryWriter w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(SizeLimit);
                w.Write(Files.Count);
                foreach (var f in Files)
                {
                    w.Write(f.Name); w.Write(f.Length); w.Write(f.LastWriteTicks); w.Write(f.IndexPosition); w.Write(f.IndexCount);
                }
                w.Write(Sources.Count);
                foreach (var s in Sources)
                {
                    w.Write(s.Id); w.Write(s.Size); w.Write(s.LastWriteTicks);
                }
                w.Write(Entries.Count);
                foreach (var e in Entries)
                {
                    w.Write(e.Key.Type); w.Write(e.Key.Group); w.Write(e.Key.Instance);
                    w.Write(e.Source); w.Write(e.File);
                    w.Write(e.Offset); w.Write(e.FileSize); w.Write(e.MemSize); w.Write(e.Compressed); w.Write(e.Unknown2);
                }
                w.Flush();
                fs.Flush(true);
            }
            File.Move(tmp, path, true);
        }

        /// <summary>Loads the state and checks that ModBuilds still holds exactly the files it describes.</summary>
        public static BuildState TryLoad(string folder, out string reason)
        {
            reason = null;
            string path = Path.Combine(folder, ModBuilder.StateFileName);

            if (!Directory.Exists(folder))
            {
                reason = "no previous build";
                return null;
            }
            if (!File.Exists(path))
            {
                reason = File.Exists(Path.Combine(folder, "Merge.bin")) ? "previous build was made by an older S3MO version" : "no previous build";
                return null;
            }

            BuildState state = new BuildState();
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
                using (BinaryReader r = new BinaryReader(fs, Encoding.UTF8))
                {
                    if (r.ReadString() != Magic || r.ReadInt32() != Version)
                    {
                        reason = "build state is from a different version";
                        return null;
                    }
                    state.SizeLimit = r.ReadInt64();
                    int files = r.ReadInt32();
                    for (int i = 0; i < files; i++)
                        state.Files.Add(new FileRecord { Name = r.ReadString(), Length = r.ReadInt64(), LastWriteTicks = r.ReadInt64(), IndexPosition = r.ReadUInt32(), IndexCount = r.ReadUInt32() });
                    int sources = r.ReadInt32();
                    for (int i = 0; i < sources; i++)
                        state.Sources.Add(new SourceRecord { Id = r.ReadString(), Size = r.ReadInt64(), LastWriteTicks = r.ReadInt64() });
                    int entries = r.ReadInt32();
                    state.Entries = new List<Entry>(entries);
                    for (int i = 0; i < entries; i++)
                    {
                        var e = new Entry
                        {
                            Key = new ResourceKey(r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt64()),
                            Source = r.ReadInt32(),
                            File = r.ReadInt32(),
                            Offset = r.ReadUInt32(),
                            FileSize = r.ReadUInt32(),
                            MemSize = r.ReadUInt32(),
                            Compressed = r.ReadUInt16(),
                            Unknown2 = r.ReadUInt16(),
                        };
                        if (e.Source < 0 || e.Source >= state.Sources.Count || e.File < 0 || e.File >= state.Files.Count)
                            throw new InvalidDataException("entry out of range");
                        state.Entries.Add(e);
                    }
                }
            }
            catch (Exception ex)
            {
                reason = $"build state couldn't be read ({ex.Message})";
                return null;
            }

            if (state.Files.Count == 0)
            {
                reason = "previous build has no files";
                return null;
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Span<byte> header = stackalloc byte[Package.HeaderSize];
            foreach (FileRecord f in state.Files)
            {
                known.Add(f.Name);
                FileInfo info = new FileInfo(Path.Combine(folder, f.Name));
                if (!info.Exists)
                {
                    reason = $"{f.Name} is missing";
                    return null;
                }
                if (info.Length != f.Length || info.LastWriteTimeUtc.Ticks != f.LastWriteTicks)
                {
                    reason = $"{f.Name} was changed outside S3MO";
                    return null;
                }
                try
                {
                    using (FileStream fs = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        fs.ReadExactly(header);
                    uint indexPosition = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(64));
                    uint indexCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(36));
                    if (indexPosition != f.IndexPosition || indexCount != f.IndexCount)
                    {
                        reason = $"{f.Name} doesn't match the build state";
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    reason = $"{f.Name} couldn't be read ({ex.Message})";
                    return null;
                }
            }

            foreach (string file in Directory.EnumerateFiles(folder, "*.package"))
            {
                if (!known.Contains(Path.GetFileName(file)))
                {
                    reason = $"unexpected file {Path.GetFileName(file)} in the build folder";
                    return null;
                }
            }

            return state;
        }
    }
}
