using System;

namespace s3molib
{
    /// <summary>
    /// The identity of a resource inside a package: Type, Group and Instance.
    /// Two resources with the same key conflict; the higher-priority one wins in the merge.
    /// Using this struct as a dictionary key replaces the old linear searches and string keys.
    /// </summary>
    public readonly struct ResourceKey : IEquatable<ResourceKey>, IComparable<ResourceKey>
    {
        public readonly uint Type;
        public readonly uint Group;
        public readonly ulong Instance;

        public ResourceKey(uint type, uint group, ulong instance)
        {
            Type = type;
            Group = group;
            Instance = instance;
        }

        public bool Equals(ResourceKey other) => Type == other.Type && Group == other.Group && Instance == other.Instance;
        public override bool Equals(object obj) => obj is ResourceKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Type, Group, Instance);

        public int CompareTo(ResourceKey other)
        {
            int c = Type.CompareTo(other.Type);
            if (c != 0) return c;
            c = Group.CompareTo(other.Group);
            if (c != 0) return c;
            return Instance.CompareTo(other.Instance);
        }

        public static bool operator ==(ResourceKey a, ResourceKey b) => a.Equals(b);
        public static bool operator !=(ResourceKey a, ResourceKey b) => !a.Equals(b);

        /// <summary>Same format the app has always shown: 0xTTTTTTTT-0xGGGGGGGG-0xIIIIIIIIIIIIIIII</summary>
        public override string ToString() => $"0x{Type:X8}-0x{Group:X8}-0x{Instance:X16}";
    }
}
