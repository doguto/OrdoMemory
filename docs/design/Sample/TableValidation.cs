using System;
using System.Collections.Generic;

namespace OrdoMemory.Sample
{
    // テーブル操作の引数検証. テーブルの状態に依存しない検証だけを置く.
    // 状態に依存する検証 (挿入済みかどうか等) は、テーブル側で判定して ThrowXxx を呼ぶ.
    internal static class TableValidation
    {
        public static void ThrowIfNull<T>(T value, string paramName) where T : class
        {
            if (value == null) throw new ArgumentNullException(paramName);
        }

        // null 要素と、同一インスタンスの重複を検出する.
        public static void ValidateRecords<T>(IReadOnlyList<T> records, string paramName) where T : class
        {
            for (var i = 0; i < records.Count; i++)
            {
                if (records[i] == null) throw new ArgumentException($"{paramName}[{i}] is null.", paramName);
            }

            ValidateNoDuplicates(records, paramName);
        }

        public static void ThrowAlreadyInserted(string paramName, int index = -1)
        {
            var target = index < 0 ? paramName : $"{paramName}[{index}]";
            throw new ArgumentException($"{target} is already inserted.", paramName);
        }

        // 重複検出. アロケーションが発生するのはここだけ. ゼロアロケーション化する際はこの関数のみを差し替える.
        static void ValidateNoDuplicates<T>(IReadOnlyList<T> records, string paramName) where T : class
        {
            // 1 件以下では重複し得ないため、セットを作らない.
            if (records.Count < 2) return;

            var seen = new HashSet<T>();
            for (var i = 0; i < records.Count; i++)
            {
                if (!seen.Add(records[i])) throw new ArgumentException($"{paramName}[{i}] is duplicated.", paramName);
            }
        }
    }
}
