using MasterMemory;
using MessagePack;

[assembly: MasterMemoryGeneratorOptions(Namespace = "OrdoMemory.Benchmark.MasterMemoryDb")]

namespace OrdoMemory.Benchmark
{
    [OrdoTable]
    public readonly partial struct OrdoItem
    {
        [SecondaryKey] public readonly int Code;

        [SecondaryKey(Unique = false)] public readonly int Category;

        [SecondaryKey(IndexKind.Hash)] public readonly string Name;

        public readonly int Value;

        public readonly string Description;
    }

    [MemoryTable("item"), MessagePackObject(true)]
    public sealed class MmItem
    {
        [PrimaryKey]
        public int Id { get; set; }

        [MasterMemory.SecondaryKey(0)]
        public int Code { get; set; }

        [MasterMemory.SecondaryKey(1), NonUnique]
        public int Category { get; set; }

        [MasterMemory.SecondaryKey(2)]
        public string Name { get; set; }

        public int Value { get; set; }

        public string Description { get; set; }
    }
}
