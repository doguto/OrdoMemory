namespace OrdoMemory.Tests
{
    public enum Kind : byte
    {
        A,
        B,
        C
    }

    [OrdoTable]
    public readonly partial struct Item
    {
        [SecondaryKey] public readonly int Code;

        [SecondaryKey(Unique = false)] public readonly string Name;

        [SecondaryKey(IndexKind.Hash, Unique = false)]
        public readonly Kind Kind;

        [SecondaryKey(IndexKind.Hash)] public readonly string Tag;

        [SecondaryKey(Unique = false)] public readonly double Score;

        public readonly string Description;
    }

    [OrdoTable(PrimaryKey = PrimaryKeyKind.UInt64, TableName = "BigTable")]
    public readonly partial struct BigRow
    {
        [SecondaryKey(Unique = false)] public readonly long Value;

        public readonly int PrimaryKey;
    }

    [OrdoTable(SlotBits = 31)]
    public readonly partial struct OneGenRow
    {
        public readonly int Value;
    }

    [OrdoTable(SlotBits = 2)]
    public readonly partial struct TinyRow
    {
        [SecondaryKey] public readonly int Value;
    }
}
