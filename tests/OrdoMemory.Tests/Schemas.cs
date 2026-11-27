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

    // readonly struct ではなく、readonly でないフィールドは Update でその場で書き換えられる
    [OrdoTable]
    public partial struct Character
    {
        [PrimaryKey] public readonly uint Id;

        // readonly の SecondaryKey. 変わる場合は Record 全体を作り直す
        [SecondaryKey] public readonly int Code;

        // readonly でない SecondaryKey (FK を想定). 索引を付け替えてその場で書き込む
        [SecondaryKey(IndexKind.Hash, Unique = false)]
        public int Team;

        [SecondaryKey(Unique = false)] public string Group;

        public int Hp;

        public string Note;

        public readonly bool IsAlive() => Hp > 0;
    }

    // 利用者定義のコンストラクタが、生成されるコンストラクタと同じ型の並びで引数の順番だけ違う
    [OrdoTable]
    public readonly partial struct SwapRow
    {
        [PrimaryKey] public readonly uint Id;

        [SecondaryKey(Unique = false)] public readonly int A;

        public readonly int B;

        public SwapRow(int b, int a)
        {
            Id = 0;
            A = a;
            B = b;
        }
    }

    // SwapRow の readonly struct でない版. A (readonly の SecondaryKey) の変更は作り直し、B はその場で書き込む
    [OrdoTable]
    public partial struct MutableSwapRow
    {
        [PrimaryKey] public readonly uint Id;

        [SecondaryKey(Unique = false)] public readonly int A;

        public int B;

        public MutableSwapRow(int b, int a)
        {
            Id = 0;
            A = a;
            B = b;
        }
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
