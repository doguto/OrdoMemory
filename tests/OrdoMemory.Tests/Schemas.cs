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
        [PrimaryKey] public readonly uint Id;

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
        [PrimaryKey] public readonly ulong Id;

        [SecondaryKey(Unique = false)] public readonly long Value;

        public readonly int PrimaryKey;
    }

    [OrdoTable(SlotBits = 31)]
    public readonly partial struct OneGenRow
    {
        [PrimaryKey] public readonly uint Id;

        public readonly int Value;
    }

    [OrdoTable(SlotBits = 2)]
    public readonly partial struct TinyRow
    {
        [PrimaryKey] public readonly uint Id;

        [SecondaryKey] public readonly int Value;
    }

    // readonly struct ではなく、キー以外のフィールドを UpdateRef で書き換えられる
    [OrdoTable]
    public partial struct Character
    {
        [PrimaryKey] public readonly uint Id;

        [SecondaryKey] public readonly int Code;

        [SecondaryKey(IndexKind.Hash, Unique = false)]
        public readonly int Team;

        public int Hp;

        public string Note;

        public readonly bool IsAlive() => Hp > 0;
    }
}
