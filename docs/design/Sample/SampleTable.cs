using System;
using System.Collections.Generic;

namespace OrdoMemory.Sample
{
    public class SampleTable
    {
        const int MaxArrayLength = 0x7FFFFFC7;
        const int MinGrowLength = 4;

        readonly int start;
        readonly SampleTableOptions options;

        // data / keys / alive は同じ添字で対応する. keys は昇順.
        SampleSchema[] data;
        int[] keys;
        bool[] alive;

        // Name の索引. 実装は外部から渡す.
        readonly SecondaryIndex<string> nameIndex;

        // 使用中スロット数 (生存 + 削除済み). 次の書き込み先の添字でもある.
        int used;
        int deletedCount;

        // これまでに削除された Record の累計. コンパクションでもリセットしない.
        long removedTotal;

        // 次に払い出す Key. Key は再利用しない.
        long nextKey;

        public int Count => used - deletedCount;

        public int Capacity => data.Length;

        public SampleTable(int start, int capacity, SecondaryIndex<string> nameIndex, SampleTableOptions options = null)
        {
            if (capacity < 0 || capacity > MaxLength(start))
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            this.nameIndex = nameIndex ?? throw new ArgumentNullException(nameof(nameIndex));
            this.options = options ?? new SampleTableOptions();

            this.start = start;
            data = new SampleSchema[capacity];
            keys = new int[capacity];
            alive = new bool[capacity];
            nextKey = start;
        }

        // 払い出された Key は record.Id に入る.
        public bool Insert(SampleSchema record)
        {
            TableValidation.ThrowIfNull(record, nameof(record));
            if (IsStored(record)) TableValidation.ThrowAlreadyInserted(nameof(record));
            if (!TryReserve(1)) return false;

            var key = Store(record);
            nameIndex.Insert(record.Name, key);
            return true;
        }

        public bool BulkInsert(IReadOnlyList<SampleSchema> records)
        {
            TableValidation.ThrowIfNull(records, nameof(records));

            // Key の払い出しより前に検証する. 途中で失敗して、一部だけ挿入された状態になることを防ぐ.
            TableValidation.ValidateRecords(records, nameof(records));
            for (var i = 0; i < records.Count; i++)
            {
                if (IsStored(records[i])) TableValidation.ThrowAlreadyInserted(nameof(records), i);
            }

            if (!TryReserve(records.Count)) return false;

            var entries = new KeyValuePair<string, int>[records.Count];
            for (var i = 0; i < records.Count; i++)
            {
                var key = Store(records[i]);
                entries[i] = new KeyValuePair<string, int>(records[i].Name, key);
            }

            // 索引は 1 件ずつ更新せず、まとめて構築する.
            nameIndex.AddRange(entries);
            return true;
        }

        // 既存の record をその場で書き換える. 指定した項目だけを更新し、アロケーションは発生しない.
        public bool Update(int key, Optional<string> name = default, Optional<string> description = default)
        {
            if (!TryGetAliveIndex(key, out var index)) return false;

            // SecondaryKey が変わらない場合は、索引を更新しない. 索引は書き換え前の Name で引く.
            var current = data[index];
            var newName = name.OrElse(current.Name);
            if (!string.Equals(current.Name, newName, StringComparison.Ordinal))
            {
                nameIndex.Remove(current.Name, key);
                nameIndex.Insert(newName, key);
            }

            current.Name = newName;
            current.Description = description.OrElse(current.Description);
            return true;
        }

        // 論理削除. スロットはコンパクションまで残る.
        public bool Remove(int key)
        {
            if (!TryGetAliveIndex(key, out var index)) return false;

            nameIndex.Remove(data[index].Name, key);
            alive[index] = false;
            deletedCount++;
            removedTotal++;

            if (ShouldAutoCompact()) Compact();
            return true;
        }

        // 削除済みスロットを取り除いて前方に詰める. 回収したスロット数を返す.
        // data への参照は無効となる.
        public int Compact()
        {
            if (deletedCount == 0) return 0;

            var write = 0;
            for (var read = 0; read < used; read++)
            {
                if (!alive[read]) continue;

                if (write != read)
                {
                    data[write] = data[read];
                    keys[write] = keys[read];
                    alive[write] = true;
                }

                write++;
            }

            // 詰めた後の空き領域を初期化する. 削除済み Record への参照も、ここで解放される.
            Array.Clear(data, write, used - write);
            Array.Clear(keys, write, used - write);
            Array.Clear(alive, write, used - write);

            var reclaimed = used - write;
            used = write;
            deletedCount = 0;
            return reclaimed;
        }


        public SampleSchema Find(int key)
        {
            return TryGetAliveIndex(key, out var index) ? data[index] : null;
        }

        public IEnumerable<SampleSchema> FindRange(int min, int max)
        {
            for (var index = LowerBound(min); index < used && keys[index] <= max; index++)
            {
                if (alive[index]) yield return data[index];
            }
        }

        public SampleSchema FindFirstByName(string name)
        {
            foreach (var key in nameIndex.Equal(name)) return Resolve(key);

            return null;
        }

        public IEnumerable<SampleSchema> FindAllByName(string name)
        {
            foreach (var key in nameIndex.Equal(name)) yield return Resolve(key);
        }

        public IEnumerable<SampleSchema> FindRangeByName(string min, string max)
        {
            foreach (var key in nameIndex.Range(min, max)) yield return Resolve(key);
        }

        public IEnumerable<SampleSchema> All()
        {
            for (var index = 0; index < used; index++)
            {
                if (alive[index]) yield return data[index];
            }
        }


        bool ShouldAutoCompact()
        {
            return options.AutoCompaction
                   && used > 0
                   && (double)deletedCount / used >= options.CompactionThreshold;
        }

        // count 件分の Key と、書き込み先のスロットを確保する. 確保できない場合は何も変更しない.
        bool TryReserve(int count)
        {
            // Key は再利用しないため、コンパクション後も枯渇は戻らない.
            if (nextKey + count - 1 > int.MaxValue) return false;

            return EnsureCapacity((long)used + count);
        }

        // 事前に TryReserve で確保済みであること.
        int Store(SampleSchema record)
        {
            var key = (int)nextKey;
            nextKey++;

            record.Id = key;
            data[used] = record;
            keys[used] = key;
            alive[used] = true;
            used++;
            return key;
        }

        // 索引が返す Key は常に使用中のため、生存確認は行わない.
        SampleSchema Resolve(int key)
        {
            TryFindIndex(key, out var index);
            return data[index];
        }

        bool IsStored(SampleSchema record)
        {
            return TryGetAliveIndex(record.Id, out var index) && ReferenceEquals(data[index], record);
        }

        bool TryGetAliveIndex(int key, out int index)
        {
            return TryFindIndex(key, out index) && alive[index];
        }

        // keys から Key の添字を引く. 削除済みのスロットも対象とする.
        bool TryFindIndex(int key, out int index)
        {
            // int 同士の減算はオーバーフローし得るため long で計算する.
            var offset = (long)key - start;
            if (offset < 0 || offset >= nextKey - start || used == 0)
            {
                index = -1;
                return false;
            }

            // 欠けている Key は削除済みの分だけなので、添字は [offset - removedTotal, offset] に収まる.
            var upper = (int)Math.Min(offset, used - 1);
            var lower = (int)Math.Max(0L, offset - removedTotal);
            if (lower > upper)
            {
                index = -1;
                return false;
            }

            var found = Array.BinarySearch(keys, lower, upper - lower + 1, key);
            index = found >= 0 ? found : -1;
            return found >= 0;
        }

        // key 以上の Key を持つ最初の添字. 無ければ used.
        int LowerBound(int key)
        {
            var found = Array.BinarySearch(keys, 0, used, key);
            return found >= 0 ? found : ~found;
        }

        bool EnsureCapacity(long required)
        {
            if (required <= data.Length) return true;

            var max = MaxLength(start);
            if (required > max) return false;

            var doubled = Math.Max((long)data.Length * 2, MinGrowLength);
            var newLength = (int)Math.Min(Math.Max(required, doubled), max);

            Array.Resize(ref data, newLength);
            Array.Resize(ref keys, newLength);
            Array.Resize(ref alive, newLength);
            return true;
        }

        static int MaxLength(int start)
        {
            return (int)Math.Min(MaxArrayLength, (long)int.MaxValue - start + 1);
        }
    }
}
