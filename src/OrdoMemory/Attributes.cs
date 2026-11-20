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

    /// <summary>
    /// readonly partial struct に付けると、コンストラクタと {Struct}Table を生成する.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, Inherited = false)]
    public sealed class RuntimeTableAttribute : Attribute
    {
        /// <summary>生成するテーブルの型名. 省略時は {Struct}Table.</summary>
        public string TableName { get; set; }

        public PkKind Pk { get; set; } = PkKind.UInt32;

        /// <summary>PK のうちスロット番号に使うビット数. 0 は既定 (UInt32: 22, UInt64: 32).</summary>
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
