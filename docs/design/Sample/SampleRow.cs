namespace OrdoMemory.Sample
{
    // テーブルが唯一の実体を持つ Record. PK は持たない (列挙子の CurrentPk などから求める).
    // readonly struct とし、ref readonly / in で受け取ったときの隠れたコピーを防ぐ.
    public readonly struct SampleRow
    {
        public readonly string Name;
        public readonly string Description;

        public SampleRow(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }
}
