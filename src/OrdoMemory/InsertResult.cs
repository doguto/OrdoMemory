namespace OrdoMemory
{
    // TryInsert / TryBulkInsert の結果
    public enum InsertResult
    {
        // 挿入に成功した
        Success,

        // Unique な SecondaryKey が既存の Record (BulkInsert では同じ一括挿入内の Record も含む) と重複した
        Duplicate,

        // スロットを使い切った (同時に持てる件数の上限、または世代の使い切り)
        Full
    }
}
