namespace OrdoMemory
{
    public enum InsertResult
    {
        Success,

        // Unique な SecondaryKey の重複 (BulkInsert では一括挿入内も含む)
        Duplicate,

        // スロットを使い切った
        Full
    }
}
