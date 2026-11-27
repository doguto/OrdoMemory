namespace OrdoMemory.Sample
{
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
