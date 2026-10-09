using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace s3molib
{
    /// <summary>
    /// Remembers the index of every package between runs, keyed by path, file size and last-write time.
    /// On startup only new or changed packages are opened and parsed; everything else comes from the cache file.
    /// The cache is purely an optimization: if it is missing, corrupt or from another version it is ignored and rebuilt.
    /// </summary>
    public sealed class PackageCache
    {
        private const string FileMagic = "S3MOIDX";
        private const int FileVersion = 1;

        private sealed class Record
        {
            public long Size;
            public long LastWriteTicks;
            public uint IndexCount, IndexLength, IndexPosition;
            public Package.CachedEntry[] Entries;
            public Package Package;      // materialized this session (shared between refreshes)
            public bool Used;            // touched this session; unused records are dropped on save
        }

        private readonly string _cacheFilePath;
        private readonly string _rootFolder;
        private readonly Dictionary<string, Record> _records = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private bool _dirty;

        private int _hits, _misses;
        public int Hits => _hits;
        public int Misses => _misses;
        public void ResetCounters() { _hits = 0; _misses = 0; }

        /// <param name="cacheFilePath">Where the cache is stored.</param>
        /// <param name="rootFolder">Packages are keyed relative to this folder, so moving the whole S3MO folder keeps the cache valid.</param>
        public PackageCache(string cacheFilePath, string rootFolder)
        {
            _cacheFilePath = cacheFilePath;
            _rootFolder = Path.GetFullPath(rootFolder);
        }

        private string KeyFor(string fullPath) => Path.GetRelativePath(_rootFolder, Path.GetFullPath(fullPath));

        /// <summary>Returns the package index for the file, from the cache when the file is unchanged.</summary>
        public Package GetPackage(string fullPath, out bool fromCache) => GetPackage(fullPath, out fromCache, out _, out _);

        /// <summary>The package as a build source, re-reading its index if the file changed since it was scanned.</summary>
        public BuildSource GetBuildSource(string fullPath, string id)
        {
            Package p = GetPackage(fullPath, out _, out long size, out long ticks);
            return new BuildSource(id, p, size, ticks);
        }

        private Package GetPackage(string fullPath, out bool fromCache, out long size, out long ticks)
        {
            FileInfo info = new FileInfo(fullPath);
            if (!info.Exists)
                throw new FileNotFoundException($"File does not exists - {fullPath}", fullPath);

            size = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
            string key = KeyFor(fullPath);

            lock (_lock)
            {
                if (_records.TryGetValue(key, out Record r) && r.Size == info.Length && r.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
                {
                    r.Used = true;
                    if (r.Package == null || !string.Equals(r.Package.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))
                        r.Package = Package.FromIndex(fullPath, r.IndexCount, r.IndexLength, r.IndexPosition, r.Entries);
                    fromCache = true;
                    Interlocked.Increment(ref _hits);
                    return r.Package;
                }
            }

            // Parse outside the lock so several packages can be read in parallel.
            Package package = Package.Load(fullPath);
            Interlocked.Increment(ref _misses);

            var entries = new Package.CachedEntry[package.EntryList.Count];
            for (int i = 0; i < entries.Length; i++)
            {
                ResourceEntry e = package.EntryList[i];
                entries[i] = new Package.CachedEntry
                {
                    Type = e.Type, Group = e.Group, ID1 = e.ID1, ID2 = e.ID2,
                    ChunkOffset = e.ChunkOffset, FileSize = e.FileSize, MemSize = e.MemSize,
                    Compressed = e.Compressed, Unknown2 = e.Unknown2
                };
            }

            lock (_lock)
            {
                _records[key] = new Record
                {
                    Size = info.Length,
                    LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                    IndexCount = package.IndexCount,
                    IndexLength = package.IndexLength,
                    IndexPosition = package.IndexPosition,
                    Entries = entries,
                    Package = package,
                    Used = true,
                };
                _dirty = true;
            }

            fromCache = false;
            return package;
        }

        /// <summary>Size and last-write time the cache saw for this file, if any.</summary>
        public static void GetFingerprint(string fullPath, out long size, out long lastWriteTicks)
        {
            FileInfo info = new FileInfo(fullPath);
            size = info.Length;
            lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        }

        public void Load()
        {
            lock (_lock)
            {
                _records.Clear();
                _dirty = false;

                if (!File.Exists(_cacheFilePath))
                    return;

                try
                {
                    using (FileStream fs = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
                    using (BinaryReader r = new BinaryReader(fs, Encoding.UTF8))
                    {
                        if (r.ReadString() != FileMagic || r.ReadInt32() != FileVersion)
                            return;

                        int count = r.ReadInt32();
                        for (int i = 0; i < count; i++)
                        {
                            string key = r.ReadString();
                            Record rec = new Record
                            {
                                Size = r.ReadInt64(),
                                LastWriteTicks = r.ReadInt64(),
                                IndexCount = r.ReadUInt32(),
                                IndexLength = r.ReadUInt32(),
                                IndexPosition = r.ReadUInt32(),
                            };
                            int n = r.ReadInt32();
                            if (n < 0 || n > 10_000_000)
                                throw new InvalidDataException("Bad entry count");
                            rec.Entries = new Package.CachedEntry[n];
                            for (int j = 0; j < n; j++)
                            {
                                rec.Entries[j] = new Package.CachedEntry
                                {
                                    Type = r.ReadUInt32(), Group = r.ReadUInt32(), ID1 = r.ReadUInt32(), ID2 = r.ReadUInt32(),
                                    ChunkOffset = r.ReadUInt32(), FileSize = r.ReadUInt32(), MemSize = r.ReadUInt32(),
                                    Compressed = r.ReadUInt16(), Unknown2 = r.ReadUInt16(),
                                };
                            }
                            _records[key] = rec;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Package cache could not be read and will be rebuilt ({ex.Message}).");
                    _records.Clear();
                }
            }
        }

        /// <summary>Writes the cache. Records for packages that weren't looked up since <see cref="Load"/> are dropped.</summary>
        public void Save()
        {
            lock (_lock)
            {
                bool anyUnused = false;
                foreach (var kv in _records)
                    if (!kv.Value.Used) { anyUnused = true; break; }

                if (!_dirty && !anyUnused && File.Exists(_cacheFilePath))
                    return;

                string tmp = _cacheFilePath + ".tmp";
                try
                {
                    using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    using (BinaryWriter w = new BinaryWriter(fs, Encoding.UTF8))
                    {
                        w.Write(FileMagic);
                        w.Write(FileVersion);

                        int count = 0;
                        foreach (var kv in _records)
                            if (kv.Value.Used) count++;
                        w.Write(count);

                        foreach (var kv in _records)
                        {
                            Record rec = kv.Value;
                            if (!rec.Used)
                                continue;
                            w.Write(kv.Key);
                            w.Write(rec.Size);
                            w.Write(rec.LastWriteTicks);
                            w.Write(rec.IndexCount);
                            w.Write(rec.IndexLength);
                            w.Write(rec.IndexPosition);
                            w.Write(rec.Entries.Length);
                            foreach (var e in rec.Entries)
                            {
                                w.Write(e.Type); w.Write(e.Group); w.Write(e.ID1); w.Write(e.ID2);
                                w.Write(e.ChunkOffset); w.Write(e.FileSize); w.Write(e.MemSize);
                                w.Write(e.Compressed); w.Write(e.Unknown2);
                            }
                        }
                    }
                    File.Move(tmp, _cacheFilePath, true);

                    // Forget unused records so the next save doesn't keep rewriting.
                    var unused = new List<string>();
                    foreach (var kv in _records)
                        if (!kv.Value.Used) unused.Add(kv.Key);
                    foreach (string k in unused)
                        _records.Remove(k);
                    _dirty = false;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Package cache could not be saved ({ex.Message}).");
                    try { File.Delete(tmp); } catch { }
                }
            }
        }

        /// <summary>Marks every record unused; call before a full rescan so removed packages get pruned on save.</summary>
        public void BeginScan()
        {
            lock (_lock)
                foreach (var kv in _records)
                    kv.Value.Used = false;
        }
    }
}
