using System;

namespace OrdoMemory
{
    public enum PrimaryKeyKind
    {
        UInt32,
        UInt64
    }

    public enum IndexKind
    {
        Ordered,
        Hash
    }

    [AttributeUsage(AttributeTargets.Struct, Inherited = false)]
    public sealed class OrdoTableAttribute : Attribute
    {
        public string TableName { get; set; }

        // PK の型. [PrimaryKey] を付けたフィールドの型と一致させる
        public PrimaryKeyKind PrimaryKeyType { get; set; } = PrimaryKeyKind.UInt32;

        // PK のうちスロット番号に使うビット数. 残りが世代になる. 0 なら PK の型ごとの既定値 (uint: 22, ulong: 32)
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
