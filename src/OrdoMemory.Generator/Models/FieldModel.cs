namespace OrdoMemory.Generator.Models
{
    sealed class FieldModel
    {
        public FieldModel(string name, string type)
        {
            Name = name;
            Type = type;

            var parameterName = "@" + char.ToLowerInvariant(name[0]) + name.Substring(1);
            // 生成する Update の引数 primaryKey と衝突させない
            ParameterName = parameterName == "@primaryKey" ? "@primaryKeyValue" : parameterName;
        }

        public string Name { get; }
        public string Type { get; }
        public string ParameterName { get; }
    }
}
