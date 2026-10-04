using MasterMemory;
using MessagePack;

namespace OrdoMemory.Sample
{
    // 書き込めるのはテーブルだけ. 呼び出し側が保持する参照を書き換えて、索引と食い違うことを防ぐ.
    public sealed class SampleSchema
    {
        // Insert 時にテーブルが払い出す.
        public int Id { get; internal set; }
        public string Name { get; internal set; }
        public string Description { get; internal set; }

        public SampleSchema(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }
}
