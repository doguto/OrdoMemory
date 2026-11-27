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

        // PK のうちスロット番号に使うビット数. 残りが世代になる. 0 なら PK のフィールドの型ごとの既定値 (uint: 22, ulong: 32)
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
