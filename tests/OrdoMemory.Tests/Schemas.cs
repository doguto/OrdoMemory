namespace OrdoMemory.Tests
{
    public enum Kind : byte
    {
        A,
        B,
        C,
    }

    [RuntimeTable]
    public readonly partial struct Item
    {
        [SecondaryKey]
        public readonly int Code;

        [SecondaryKey(Unique = false)]
        public readonly string Name;

        [SecondaryKey(IndexKind.Hash, Unique = false)]
        public readonly Kind Kind;

        [SecondaryKey(IndexKind.Hash)]
        public readonly string Tag;

        [SecondaryKey(Unique = false)]
        public readonly double Score;

        public readonly string Description;
    }

    [RuntimeTable(Pk = PkKind.UInt64, TableName = "BigTable")]
    public readonly partial struct BigRow
    {
        [SecondaryKey(Unique = false)]
        public readonly long Value;

        public readonly int Pk;
    }

    // 世代 1bit: 1 度削除したスロットは使用を止める.
    [RuntimeTable(SlotBits = 31)]
    public readonly partial struct OneGenRow
    {
        public readonly int Value;
    }

    // スロット 2bit: 同時に 4 件まで.
    [RuntimeTable(SlotBits = 2)]
    public readonly partial struct TinyRow
    {
        [SecondaryKey]
        public readonly int Value;
    }
}
