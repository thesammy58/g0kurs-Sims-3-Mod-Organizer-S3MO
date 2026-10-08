using System.Buffers.Binary;
using s3molib;

namespace S3MO.Tests;

/// <summary>Writes DBPF 2.0 packages the way Sims 3 tools do, with a few layout variations.</summary>
public static class Synthetic
{
    public sealed class Res
    {
        public uint Type, Group;
        public ulong Instance;
        public byte[] Stored;     // bytes as stored in the file (maybe compressed)
        public uint MemSize;
        public ushort Compressed; // 0xFFFF = compressed, 0 = not
        public ushort Unknown2 = 1;
    }

    /// <param name="indexFirst">Put the index right after the header instead of at the end.</param>
    /// <param name="commonFlags">Index "common field" flags; only flags that are valid for all entries are used.</param>
    public static void Write(string path, IReadOnlyList<Res> resources, uint commonFlags = 0, bool indexFirst = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // only keep common flags that really are common
        if (resources.Count == 0) commonFlags = 0;
        else
        {
            if ((commonFlags & 1) != 0 && resources.Any(r => r.Type != resources[0].Type)) commonFlags &= ~1u;
            if ((commonFlags & 2) != 0 && resources.Any(r => r.Group != resources[0].Group)) commonFlags &= ~2u;
            if ((commonFlags & 4) != 0 && resources.Any(r => (uint)(r.Instance >> 32) != (uint)(resources[0].Instance >> 32))) commonFlags &= ~4u;
            if ((commonFlags & 8) != 0 && resources.Any(r => (uint)r.Instance != (uint)resources[0].Instance)) commonFlags &= ~8u;
        }

        int common = 0;
        for (int b = 0; b < 4; b++) if ((commonFlags & (1u << b)) != 0) common++;
        int indexLength = 4 + common * 4 + (32 - common * 4) * resources.Count;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        fs.Position = 96;

        long indexPos;
        var offsets = new uint[resources.Count];
        if (indexFirst)
        {
            indexPos = 96;
            fs.Position = 96 + indexLength;
            for (int i = 0; i < resources.Count; i++) { offsets[i] = (uint)fs.Position; w.Write(resources[i].Stored); }
            fs.Position = indexPos;
            WriteIndex(w, resources, offsets, commonFlags);
        }
        else
        {
            for (int i = 0; i < resources.Count; i++) { offsets[i] = (uint)fs.Position; w.Write(resources[i].Stored); }
            indexPos = fs.Position;
            WriteIndex(w, resources, offsets, commonFlags);
        }

        fs.Position = 0;
        w.Write(0x46504244u); w.Write(2u); w.Write(0u);
        fs.Position = 36; w.Write((uint)resources.Count);
        fs.Position = 44; w.Write((uint)indexLength);
        fs.Position = 60; w.Write(3u);
        fs.Position = 64; w.Write((uint)indexPos);
        fs.Position = 95; w.Write((byte)0);
    }

    private static void WriteIndex(BinaryWriter w, IReadOnlyList<Res> rs, uint[] offsets, uint flags)
    {
        w.Write(flags);
        if (rs.Count > 0)
        {
            if ((flags & 1) != 0) w.Write(rs[0].Type);
            if ((flags & 2) != 0) w.Write(rs[0].Group);
            if ((flags & 4) != 0) w.Write((uint)(rs[0].Instance >> 32));
            if ((flags & 8) != 0) w.Write((uint)rs[0].Instance);
        }
        for (int i = 0; i < rs.Count; i++)
        {
            var r = rs[i];
            if ((flags & 1) == 0) w.Write(r.Type);
            if ((flags & 2) == 0) w.Write(r.Group);
            if ((flags & 4) == 0) w.Write((uint)(r.Instance >> 32));
            if ((flags & 8) == 0) w.Write((uint)r.Instance);
            w.Write(offsets[i]);
            w.Write((uint)r.Stored.Length | 0x80000000);
            w.Write(r.MemSize);
            w.Write(r.Compressed);
            w.Write(r.Unknown2);
        }
    }

    private static readonly uint[] Types = { 0x034AEECB, 0x00B2D882, 0x015A1849, 0x0333406C, 0x319E4F1D, 0x0166038C, 0x220557DA, 0x736884F1, 0x8FFB80F6, 0x73E93EEB };

    /// <summary>Makes resources; with probability <paramref name="sharedChance"/> a key comes from the shared pool (conflicts).</summary>
    public static List<Res> MakeResources(Random rnd, int count, int minSize, int maxSize, IList<(uint, uint, ulong)> sharedPool, double sharedChance, bool allowCompression = true)
    {
        var list = new List<Res>(count);
        for (int i = 0; i < count; i++)
        {
            uint type, group; ulong inst;
            if (sharedPool.Count > 0 && rnd.NextDouble() < sharedChance)
                (type, group, inst) = sharedPool[rnd.Next(sharedPool.Count)];
            else
            {
                type = Types[rnd.Next(Types.Length)];
                group = rnd.Next(4) == 0 ? (uint)rnd.Next() : 0u;
                inst = ((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next();
            }

            int size = rnd.Next(minSize, maxSize + 1);
            byte[] data = new byte[size];
            // compressible-ish content
            for (int k = 0; k < size; k++) data[k] = (byte)(rnd.Next(8) == 0 ? rnd.Next(256) : (k * 7) & 0x3F);

            var r = new Res { Type = type, Group = group, Instance = inst, MemSize = (uint)size };
            if (allowCompression && size > 64 && rnd.Next(3) == 0)
            {
                byte[] comp = s3pi.Package.Compression.CompressStream(data);
                if (comp.Length < size)
                {
                    r.Stored = comp;
                    r.Compressed = 0xFFFF;
                }
            }
            r.Stored ??= data;
            list.Add(r);
        }
        return list;
    }

    public static List<(uint, uint, ulong)> MakePool(Random rnd, int n)
    {
        var pool = new List<(uint, uint, ulong)>(n);
        for (int i = 0; i < n; i++)
            pool.Add((Types[rnd.Next(Types.Length - 2)], 0u, ((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next()));
        return pool;
    }
}
