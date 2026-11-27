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
}

// 生成ファイルのヒント名が衝突しないことの確認用 (OrdoMemory.Tests.HintName.A_B と OrdoMemory.Tests.HintName_A.B)
namespace OrdoMemory.Tests.HintName
{
    [OrdoTable]
    public readonly partial struct A_B
    {
        [PrimaryKey] public readonly uint Id;

        public readonly int Value;
    }

    // 大文字小文字だけが違う型
    [OrdoTable]
    public readonly partial struct CaseRow
    {
        [PrimaryKey] public readonly uint Id;

        public readonly int Value;
    }

    [OrdoTable]
    public readonly partial struct caseRow
    {
        [PrimaryKey] public readonly uint Id;

        public readonly int Value;
    }
}

namespace OrdoMemory.Tests.HintName_A
{
    [OrdoTable]
    public readonly partial struct B
    {
        [PrimaryKey] public readonly uint Id;

        public readonly int Value;
    }
}
