using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace s3molib
{
    public class Package
    {
        internal const uint Magic = 1179664964; // 0x46504244 FPBD
        internal const uint Major = 2;
        internal const uint Minor = 0;
        internal const uint IndexVersion = 3;
        internal const int HeaderSize = 96;

        /// <summary>Resource types that are never merged and never reported as conflicts (sims3pack manifest, name map).</summary>
        public static bool IsIgnoredType(uint type) => type == 0x73E93EEB || type == 0x0166038C;

        public string FilePath { get; private set; }
        public void ChangePath(string path) => FilePath = path;
        public uint IndexCount { get; private set; }
        public uint IndexLength { get; private set; }
        public uint IndexPosition { get; internal set; }


        /// <summary> Read-only. Returns a cloned list of the actual list. Adding or removing items to this will not do anything.</summary>
        public List<ResourceEntry> ResourceEntries => new List<ResourceEntry>(_resourceEntries);

        /// <summary>The entries in index order, without copying the list.</summary>
        public IReadOnlyList<ResourceEntry> Entries => _resourceEntries;

        internal List<ResourceEntry> EntryList => _resourceEntries;
        private readonly List<ResourceEntry> _resourceEntries = new List<ResourceEntry>();

        // Key -> first entry with that key. Built on first use; replaces the old linear List.Find.
        private Dictionary<ResourceKey, ResourceEntry> _lookup;
        private readonly object _lookupLock = new object();

        private Dictionary<ResourceKey, ResourceEntry> Lookup
        {
            get
            {
                var lookup = _lookup;
                if (lookup != null)
                    return lookup;

                lock (_lookupLock)
                {
                    if (_lookup == null)
                    {
                        var d = new Dictionary<ResourceKey, ResourceEntry>(_resourceEntries.Count);
                        foreach (ResourceEntry r in _resourceEntries)
                            d.TryAdd(r.Key, r);
                        _lookup = d;
                    }
                    return _lookup;
                }
            }
        }


        /// <summary>Create a new blank package at path</summary>
        /// <param name="writeExtra">Write some extra bytes so s3pe can open the file.</param>
        public static Package New(string path, bool writeExtra = false)
        {
            if (Path.GetFileName(path) == "")
                throw new Exception("Need filename to create file");

            if (!Directory.Exists(Path.GetDirectoryName(path)))
                Directory.CreateDirectory(Path.GetDirectoryName(path));

            if (!string.Equals(Path.GetExtension(path), ".package", StringComparison.OrdinalIgnoreCase))
                path = Path.ChangeExtension(path, ".package");

            using (FileStream stream = new FileStream(path, FileMode.Create))
            using (BinaryWriter w = new BinaryWriter(stream))
            {
                WriteBlankHeader(stream, w);

                if (writeExtra) // Exactly same file as blank package generated from s3pe
                {
                    w.Write(7);
                    w.Write(new byte[12]);
                    stream.Position = 44; // 0x2C
                    w.Write(16);
                    w.Flush();
                    return new Package(path, 0, 16, HeaderSize);
                }

                w.Flush();
                return new Package(path, 0, 0, HeaderSize);
            }
        }

        /// <summary>Writes the 96 byte header of an empty package (identical to what older versions wrote).</summary>
        internal static void WriteBlankHeader(Stream stream, BinaryWriter w)
        {
            w.Write(Magic);
            w.Write(Major);
            w.Write(Minor);
            w.Write(new byte[48]);          // 0x0C - 0x3C
            w.Write(IndexVersion);          // 0x3C
            w.Write(HeaderSize);            // 0x40 index position
            w.Write(new byte[28]);          // 0x44 - 0x60
        }

        /// <summary>Load the package index at path (resource data is read on demand).</summary>
        public static Package Load(string path)
        {
            if (!File.Exists(path))
                throw new Exception($"File does not exists - {path}");
            if (!string.Equals(Path.GetExtension(path), ".package", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"File is not a package file - {path}");

            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess))
            {
                long fileLength = stream.Length;
                if (fileLength < HeaderSize)
                    throw new Exception($"File header size is smaller than '96' bytes - {path}");

                Span<byte> h = stackalloc byte[HeaderSize];
                stream.ReadExactly(h);

                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(0));
                uint major = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(4));
                uint minor = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(8));
                uint indexCount = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(36));
                uint indexLength = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(44));
                uint indexVersion = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(60));
                uint indexPosition = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(64));

                if (magic != Magic)
                    throw new Exception($"File magic does not match DBPF - {path}");
                if (major != Major)
                    Logger.LogWarning($"File major '{major}' does not match '{Major}' - {path}");
                if (minor != Minor)
                    Logger.LogWarning($"File minor '{minor}' does not match '{Minor}' - {path}");
                if (indexVersion != IndexVersion)
                    Logger.LogWarning($"File index version '{indexVersion}' does not match '{IndexVersion}' - {path}");
                if (indexPosition == 0)
                    Logger.LogWarning($"File does not have index position - {path}");

                // Unused header areas: 12-36, 40-44, 48-60, 68-96. Warn once per file, not once per byte.
                if (HasNonZero(h.Slice(12, 24)) || HasNonZero(h.Slice(40, 4)) || HasNonZero(h.Slice(48, 12)) || HasNonZero(h.Slice(68, 28)))
                    Logger.LogWarning($"File contains non-zero unused byte(s) - {path}");

                Package package = new Package(path, indexCount, indexLength, indexPosition);

                if (indexCount == 0)
                    return package;

                if ((long)indexPosition + 4 > fileLength)
                    throw new Exception($"File index position '{indexPosition}' is beyond the end of the file - {path}");

                stream.Position = indexPosition;
                Span<byte> flagBytes = stackalloc byte[4];
                stream.ReadExactly(flagBytes);
                uint bitFlag = BinaryPrimitives.ReadUInt32LittleEndian(flagBytes); // bitflag used to indicate common entries, reducing entries for each resources

                // Bits 0-3 mark Type, Group, Instance (high) and Instance (low) as shared by every entry.
                int commonCount = 0;
                for (int b = 0; b < 4; b++)
                    if ((bitFlag & (1u << b)) != 0)
                        commonCount++;

                int entrySize = 32 - commonCount * 4;
                long expectedLength = 4 + commonCount * 4 + (long)entrySize * indexCount;

                if (indexLength != expectedLength)
                    throw new Exception($"File index length '{indexLength}' does not match expected value of '{expectedLength}' - {path}");
                if ((long)indexPosition + expectedLength > fileLength)
                    throw new Exception($"File index extends beyond the end of the file - {path}");

                byte[] index = new byte[expectedLength - 4];
                stream.ReadExactly(index, 0, index.Length);

                int p = 0;
                uint commonType = 0, commonGroup = 0, commonId1 = 0, commonId2 = 0;
                if ((bitFlag & 0x01) != 0) { commonType = ReadU32(index, ref p); }
                if ((bitFlag & 0x02) != 0) { commonGroup = ReadU32(index, ref p); }
                if ((bitFlag & 0x04) != 0) { commonId1 = ReadU32(index, ref p); }
                if ((bitFlag & 0x08) != 0) { commonId2 = ReadU32(index, ref p); }

                package._resourceEntries.Capacity = (int)indexCount;

                for (int i = 0; i < indexCount; i++)
                {
                    uint type = (bitFlag & 0x01) != 0 ? commonType : ReadU32(index, ref p);
                    uint group = (bitFlag & 0x02) != 0 ? commonGroup : ReadU32(index, ref p);
                    uint id1 = (bitFlag & 0x04) != 0 ? commonId1 : ReadU32(index, ref p);
                    uint id2 = (bitFlag & 0x08) != 0 ? commonId2 : ReadU32(index, ref p);
                    uint chunkOffset = ReadU32(index, ref p);
                    uint fileSize = ReadU32(index, ref p);
                    uint memSize = ReadU32(index, ref p);
                    ushort compressed = BinaryPrimitives.ReadUInt16LittleEndian(index.AsSpan(p)); p += 2;
                    ushort unknown2 = BinaryPrimitives.ReadUInt16LittleEndian(index.AsSpan(p)); p += 2;

                    package._resourceEntries.Add(new ResourceEntry(package, type, group, id1, id2, chunkOffset, fileSize, memSize, compressed, unknown2));
                }

                return package;
            }
        }

        private static uint ReadU32(byte[] b, ref int p)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
            p += 4;
            return v;
        }

        private static bool HasNonZero(ReadOnlySpan<byte> s)
        {
            foreach (byte b in s)
                if (b != 0)
                    return true;
            return false;
        }

        /// <summary>Recreate a package from an index that was read earlier (used by the index cache).</summary>
        internal static Package FromIndex(string path, uint indexCount, uint indexLength, uint indexPosition, IReadOnlyList<CachedEntry> entries)
        {
            Package package = new Package(path, indexCount, indexLength, indexPosition);
            package._resourceEntries.Capacity = entries.Count;
            foreach (CachedEntry e in entries)
                package._resourceEntries.Add(new ResourceEntry(package, e.Type, e.Group, e.ID1, e.ID2, e.ChunkOffset, e.FileSize, e.MemSize, e.Compressed, e.Unknown2));
            return package;
        }

        internal struct CachedEntry
        {
            public uint Type, Group, ID1, ID2, ChunkOffset, FileSize, MemSize;
            public ushort Compressed, Unknown2;
        }

        /// <summary>Merge source packages into one package, or more if the package exceeds the file size limit, and save them at the directory path.
        /// Sources are in ascending priority: when two packages contain the same resource, the one later in the list wins.
        /// Produces the same files as the original implementation, but reads each source package through a single open stream.</summary>
        /// <returns>True if successful. False if failed.</returns>
        public static bool Merge(string directoryPath, IEnumerable<Package> source, uint fileSizeLimit = 1000000000, Action<int> progress = null)
        {
            if (File.Exists(directoryPath))
                directoryPath = Path.GetDirectoryName(directoryPath);

            if (!Directory.Exists(directoryPath))
                Directory.CreateDirectory(directoryPath);

            IList<Package> sources = source as IList<Package> ?? source.ToList();
            HashSet<ResourceKey> resourceKeys = new HashSet<ResourceKey>();
            byte[] buffer = new byte[1 << 16];

            using (MergeWriter writer = new MergeWriter(directoryPath, fileSizeLimit, 1))
            {
                for (int i = sources.Count - 1; i >= 0; i--)
                {
                    Package importPackage = sources[i];
                    progress?.Invoke(i);

                    using (PackageReader reader = importPackage.OpenReader())
                    {
                        foreach (ResourceEntry re in importPackage._resourceEntries)
                        {
                            if (!resourceKeys.Add(re.Key) || IsIgnoredType(re.Type))
                                continue;

                            int length = reader.ReadRaw(re, ref buffer);
                            writer.Write(re, buffer, length);
                        }
                    }
                }
                writer.Complete();
            }
            return true;
        }

        internal void WriteIndex(BinaryWriter writer)
        {
            writer.Write(0);

            foreach (var r in _resourceEntries)
            {
                writer.Write(r.Type);
                writer.Write(r.Group);
                writer.Write(r.ID1);
                writer.Write(r.ID2);
                writer.Write(r.ChunkOffset);
                writer.Write(r.FileSize | 0x80000000);
                writer.Write(r.MemSize);
                writer.Write(r.Compressed);
                writer.Write(r.Unknown2);
            }
        }

        internal void WriteHeader(Stream stream, BinaryWriter writer)
        {
            IndexCount = (uint)_resourceEntries.Count;
            IndexLength = 4 + IndexCount * 32;

            writer.Flush();
            stream.Position = 36; // 0x24
            writer.Write(IndexCount);
            stream.Position = 44; // 0x2C
            writer.Write(IndexLength);
            stream.Position = 64; // 0x40
            writer.Write(IndexPosition);
            writer.Flush();
        }

        /// <summary>Opens the package file once for reading many resources. Dispose when done.</summary>
        public PackageReader OpenReader() => new PackageReader(this);

        public byte[] GetRawData(ResourceEntry resourceEntry)
        {
            using (PackageReader reader = OpenReader())
                return reader.ReadRaw(resourceEntry);
        }

        public byte[] GetUncompressedData(ResourceEntry resourceEntry)
        {
            using (PackageReader reader = OpenReader())
                return reader.ReadUncompressed(resourceEntry);
        }

        internal Package(string path, uint indexCount, uint indexLength, uint indexPosition)
        {
            IndexCount = indexCount;
            IndexLength = indexLength;
            IndexPosition = indexPosition;
            FilePath = path;
        }

        internal bool Owns(ResourceEntry resourceEntry) => ReferenceEquals(resourceEntry.Package, this);

        public bool TryGetEntry(ResourceKey key, out ResourceEntry entry) => Lookup.TryGetValue(key, out entry);

        public bool ContainsKey(ResourceKey key) => Lookup.ContainsKey(key);

        /// <param name="ownResourceEntry">Resource entry that belongs to this package.</param>
        public bool HasConflictingResourceEntry(ResourceEntry resourceEntry, out ResourceEntry ownResourceEntry) =>
            Lookup.TryGetValue(resourceEntry.Key, out ownResourceEntry);


        public bool AddResourceEntry(ResourceEntry resourceEntry, bool replaceConflicting = true)
        {
            if (HasConflictingResourceEntry(resourceEntry, out ResourceEntry ownResourceEntry))
            {
                if (replaceConflicting)
                {
                    Logger.LogDebug($"Conflicting resouce key '{resourceEntry}', it will be replaced by the latter. '{Path.GetFileName(FilePath)}'    '{Path.GetFileName(resourceEntry.Package.FilePath)}'");
                    _resourceEntries.Remove(ownResourceEntry);
                }
                else
                {
                    Logger.LogDebug($"Conflicting resouce key '{resourceEntry}', the latter will be rejected. '{Path.GetFileName(FilePath)}'    '{Path.GetFileName(resourceEntry.Package.FilePath)}'");
                    return false;
                }
            }
            _resourceEntries.Add(resourceEntry);
            _lookup = null;
            return true;
        }

        public bool RemoveResourceEntry(ResourceEntry resourceEntry)
        {
            if (_resourceEntries.Remove(resourceEntry))
            {
                _lookup = null;
                return true;
            }

            Logger.LogDebug($"Package does not contain resource entry - {FilePath}");
            return false;
        }

        /// <summary>Checks for conflicts with other package.</summary>
        /// <returns>True if conflicting. False if not conflicting.</returns>
        /// <param name="conflictedOtherResourceEntries">Resource entries from packages that conflict with this instance.</param>>
        /// <param name="fastCheck">Returns true directly immediately after a conflict is found</param>
        public bool IsConflictingWith(Package package, out List<ResourceEntry> conflictedResourceEntries, out List<ResourceEntry> conflictedOtherResourceEntries, bool fastCheck = false)
        {
            conflictedResourceEntries = new List<ResourceEntry>();
            conflictedOtherResourceEntries = new List<ResourceEntry>();
            var lookup = Lookup;

            foreach (var otherR in package._resourceEntries)
            {
                if (lookup.TryGetValue(otherR.Key, out ResourceEntry ownR) && !IsIgnoredType(ownR.Type))
                {
                    if (fastCheck)
                        return true;

                    conflictedResourceEntries.Add(ownR);
                    conflictedOtherResourceEntries.Add(otherR);
                }
            }
            return (conflictedOtherResourceEntries.Count > 0);
        }

    }

    /// <summary>Reads resource data from one package through a single open file handle.</summary>
    public sealed class PackageReader : IDisposable
    {
        private readonly Package _package;
        private readonly FileStream _stream;

        internal PackageReader(Package package)
        {
            _package = package;
            if (!File.Exists(package.FilePath))
                throw new Exception($"File does not exist anymore since the last time it was opened - {package.FilePath}");
            _stream = new FileStream(package.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        }

        /// <summary>Reads the stored (possibly compressed) bytes of a resource into buffer, growing it if needed.</summary>
        /// <returns>Number of bytes read.</returns>
        public int ReadRaw(ResourceEntry resourceEntry, ref byte[] buffer)
        {
            if (!_package.Owns(resourceEntry))
                throw new Exception($"Resource entry '{resourceEntry}' does not belong to {_package.FilePath}");

            int length = (int)resourceEntry.FileSize;
            long end = (long)resourceEntry.ChunkOffset + length;
            if (end > _stream.Length)
                throw new Exception($"Resource '{resourceEntry}' points past the end of {Path.GetFileName(_package.FilePath)} - the package is probably truncated or corrupt.");

            if (buffer == null || buffer.Length < length)
                buffer = new byte[Math.Max(length, buffer == null ? 0 : buffer.Length * 2)];

            if (_stream.Position != resourceEntry.ChunkOffset)
                _stream.Position = resourceEntry.ChunkOffset;
            _stream.ReadExactly(buffer, 0, length);
            return length;
        }

        public byte[] ReadRaw(ResourceEntry resourceEntry)
        {
            byte[] data = new byte[resourceEntry.FileSize];
            ReadRaw(resourceEntry, ref data);
            return data;
        }

        public byte[] ReadUncompressed(ResourceEntry resourceEntry)
        {
            byte[] data = ReadRaw(resourceEntry);

            if (resourceEntry.FileSize != resourceEntry.MemSize)
            {
                using (MemoryStream stream = new MemoryStream(data))
                    data = s3pi.Package.Compression.UncompressStream(stream, (int)resourceEntry.FileSize, (int)resourceEntry.MemSize);
            }
            return data;
        }

        public void Dispose() => _stream.Dispose();
    }

    /// <summary>
    /// Writes merged packages (Merge1.package, Merge2.package, ...) splitting at a size limit.
    /// The byte layout matches what the original Package.Merge produced.
    /// </summary>
    internal sealed class MergeWriter : IDisposable
    {
        private readonly string _directory;
        private readonly long _sizeLimit;
        private int _nextNumber;

        private Package _current;
        private FileStream _stream;
        private BinaryWriter _writer;

        /// <summary>Files finished by this writer, in order.</summary>
        public List<Package> Completed { get; } = new List<Package>();

        public MergeWriter(string directory, long sizeLimit, int firstNumber)
        {
            _directory = directory;
            _sizeLimit = sizeLimit;
            _nextNumber = firstNumber;
            StartNewFile();
        }

        public Package Current => _current;
        public long Position => _stream.Position;

        public static string FileNameFor(int number) => $"Merge{number}.package";

        private void StartNewFile()
        {
            string path = Path.Combine(_directory, FileNameFor(_nextNumber++));
            _current = Package.New(path);
            _stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 1 << 20);
            _writer = new BinaryWriter(_stream);
            _stream.Position = _current.IndexPosition;
        }

        /// <summary>Appends one resource. Starts a new file first if the current one has reached the size limit.</summary>
        /// <returns>The entry as written into the merged package.</returns>
        public ResourceEntry Write(ResourceEntry source, byte[] data, int length)
        {
            if (_stream.Position >= _sizeLimit)
            {
                FinishCurrent();
                StartNewFile();
            }

            ResourceEntry written = new ResourceEntry(_current, source.Type, source.Group, source.ID1, source.ID2, (uint)_stream.Position, source.FileSize, source.MemSize, source.Compressed, source.Unknown2);
            _current.EntryList.Add(written);
            _writer.Write(data, 0, length);
            return written;
        }

        private void FinishCurrent()
        {
            _current.IndexPosition = (uint)_stream.Position;
            _current.WriteIndex(_writer);
            _current.WriteHeader(_stream, _writer);
            _writer.Flush();
            _stream.Dispose();
            _stream = null;
            Completed.Add(_current);
        }

        /// <summary>Finishes the last file. Must be called once all resources are written.</summary>
        public void Complete()
        {
            if (_stream != null)
                FinishCurrent();
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
