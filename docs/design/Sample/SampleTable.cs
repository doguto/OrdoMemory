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

        SampleSchema[] data;
        int[] keys;
        bool[] alive;

        readonly SecondaryIndex<string> nameIndex;

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
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (IsStored(record)) throw new ArgumentException("record is already inserted.", nameof(record));
            if (!TryReserve(1)) return false;

            var key = Store(record);
            nameIndex.Insert(record.Name, key);
            return true;
        }

        public bool BulkInsert(IReadOnlyList<SampleSchema> records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));

            // Key の払い出しより前に検証する. 途中で失敗して、一部だけ挿入された状態になることを防ぐ.
            // 簡易実装のため、同一インスタンスの重複は検出しない.
            for (var i = 0; i < records.Count; i++)
            {
                if (records[i] == null) throw new ArgumentException($"records[{i}] is null.", nameof(records));
                if (IsStored(records[i])) throw new ArgumentException($"records[{i}] is already inserted.", nameof(records));
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

            // 未使用領域の初期化. メモリ確保自体はされたままであり、 data.Length 自体は変化しない
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

        // 複数件を返す検索は、results をクリアしてから結果を書き込み、件数を返す.
        // 呼び出しが返った時点で結果は確定しているため、その後の Insert / Remove / Compact の影響を受けない.
        // results を使い回せば、アロケーションは発生しない.

        public int FindRange(int min, int max, List<SampleSchema> results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));

            results.Clear();
            for (var index = LowerBound(min); index < used && keys[index] <= max; index++)
            {
                if (alive[index]) results.Add(data[index]);
            }

            return results.Count;
        }

        public SampleSchema FindFirstByName(string name)
        {
            foreach (var key in nameIndex.Equal(name)) return Resolve(key);

            return null;
        }

        public int FindAllByName(string name, List<SampleSchema> results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));

            results.Clear();
            foreach (var key in nameIndex.Equal(name)) results.Add(Resolve(key));

            return results.Count;
        }

        public int FindRangeByName(string min, string max, List<SampleSchema> results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));

            results.Clear();
            foreach (var key in nameIndex.Range(min, max)) results.Add(Resolve(key));

            return results.Count;
        }

        public int All(List<SampleSchema> results)
        {
            return Where(null, results);
        }

        // predicate が null の場合は全件を返す. 全件の中間配列は作らず、走査しながら絞り込む.
        public int Where(Predicate<SampleSchema> predicate, List<SampleSchema> results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));

            results.Clear();
            for (var index = 0; index < used; index++)
            {
                if (alive[index] && (predicate == null || predicate(data[index]))) results.Add(data[index]);
            }

            return results.Count;
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
