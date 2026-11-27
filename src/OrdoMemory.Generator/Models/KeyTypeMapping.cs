namespace OrdoMemory.Generator.Models
{
    readonly struct KeyTypeMapping
    {
        public KeyTypeMapping(string keyType, string traits, string convert, bool isReference = false)
        {
            KeyType = keyType;
            KeyTraits = "global::OrdoMemory." + traits;
            Convert = convert;
            IsReference = isReference;
        }

        public string KeyType { get; }
        public string KeyTraits { get; }
        public string Convert { get; }
        public bool IsReference { get; }
    }
}
