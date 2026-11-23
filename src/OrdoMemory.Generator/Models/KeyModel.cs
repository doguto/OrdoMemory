namespace OrdoMemory.Generator.Models
{
    sealed class KeyModel
    {
        public KeyModel(FieldModel field, bool hash, bool unique, KeyTypeMapping mapping)
        {
            Field = field;
            Hash = hash;
            Unique = unique;
            KeyType = mapping.KeyType;
            KeyTraits = mapping.KeyTraits;
            Convert = mapping.Convert;
            KeyIsReference = mapping.IsReference;
            IndexField = "__" + char.ToLowerInvariant(field.Name[0]) + field.Name.Substring(1) + "Index";
        }

        public FieldModel Field { get; }
        public bool Hash { get; }
        public bool Unique { get; }
        public string KeyType { get; }
        public string KeyTraits { get; }
        public string Convert { get; }
        public bool KeyIsReference { get; }
        public string IndexField { get; }
    }
}
