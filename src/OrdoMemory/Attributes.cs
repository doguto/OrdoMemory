using System;

namespace OrdoMemory
{
    public enum PkKind
    {
        UInt32,
        UInt64,
    }

    public enum IndexKind
    {
        Ordered,
        Hash,
    }

    [AttributeUsage(AttributeTargets.Struct, Inherited = false)]
    public sealed class OrdoTableAttribute : Attribute
    {
        public string TableName { get; set; }

        public PkKind Pk { get; set; } = PkKind.UInt32;

        public int SlotBits { get; set; }
    }

    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public sealed class SecondaryKeyAttribute : Attribute
    {
        public SecondaryKeyAttribute(IndexKind kind = IndexKind.Ordered)
        {
            Kind = kind;
        }

        public IndexKind Kind { get; }

        public bool Unique { get; set; } = true;
    }
}
