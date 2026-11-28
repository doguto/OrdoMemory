using System;

namespace OrdoMemory
{
    public enum IndexKind
    {
        Ordered,
        Hash
    }

    [AttributeUsage(AttributeTargets.Struct, Inherited = false)]
    public sealed class OrdoTableAttribute : Attribute
    {
        public string TableName { get; set; }

        // スロット番号のビット数. 0 なら既定値 (uint: 22, ulong: 32)
        public int SlotBits { get; set; }
    }

    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public sealed class PrimaryKeyAttribute : Attribute
    {
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
