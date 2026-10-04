using System;
using System.Collections.Generic;
using OrdoMemory.Sample;

namespace OrdoMemory.Design
{
    public class SampleTable
    {
        const int MaxArrayLength = 0x7FFFFFC7;
        const int MinGrowLength = 4;

        readonly int start;

        SampleSchema[] data;

        readonly BPlusTreeIndex<string> nameIndex = new(StringComparer.Ordinal);

        bool[] alive;
        long nextFreshKey;

        readonly Stack<int> freeKeys = new();

        public int Count { get; private set; }

        public int Capacity => data.Length;

        public SampleTable(int start, int capacity)
        {
            if (capacity < 0 || capacity > MaxLength(start))
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            this.start = start;
            data = new SampleSchema[capacity];
            alive = new bool[capacity];
            nextFreshKey = start;
        }

        public bool Insert(SampleSchema record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (!TryAllocateKey(out var key)) return false;

            Store(key, record);
            nameIndex.Insert(record.Name, key);
            return true;
        }

        public bool BulkInsert(IReadOnlyList<SampleSchema> records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));

            // freeKeys で賄えない分だけ、未使用の Key を払い出す.
            var freshCount = Math.Max(0, records.Count - freeKeys.Count);
            if (!EnsureCapacity(nextFreshKey - start + freshCount)) return false;

            var entries = new KeyValuePair<string, int>[records.Count];
            for (var i = 0; i < records.Count; i++)
            {
                // 事前に拡張済みのため失敗しない.
                TryAllocateKey(out var key);
                Store(key, records[i]);
                entries[i] = new KeyValuePair<string, int>(records[i].Name, key);
            }

            // 索引は 1 件ずつ更新せず、まとめて構築する.
            nameIndex.AddRange(entries);
            return true;
        }

        public bool Update(SampleSchema record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (!TryGetAliveIndex(record.Id, out var index)) return false;

            // SecondaryKey が変わらない場合は、索引を更新しない.
            var current = data[index];
            if (!string.Equals(current.Name, record.Name, StringComparison.Ordinal))
            {
                nameIndex.Remove(current.Name, record.Id);
                nameIndex.Insert(record.Name, record.Id);
            }

            data[index] = record;
            return true;
        }

        public bool Remove(int key)
        {
            if (!TryGetAliveIndex(key, out var index)) return false;

            nameIndex.Remove(data[index].Name, key);
            data[index] = null;
            alive[index] = false;
            freeKeys.Push(key);
            Count--;
            return true;
        }


        public SampleSchema Find(int key)
        {
            return TryGetAliveIndex(key, out var index) ? data[index] : null;
        }

        public IEnumerable<SampleSchema> FindRange(int min, int max)
        {
            // 未使用の領域 (nextFreshKey 以降) を読まないよう、範囲を使用済みの Key に絞る.
            var lower = Math.Max((long)min, start);
            var upper = Math.Min((long)max, nextFreshKey - 1);

            for (var key = lower; key <= upper; key++)
            {
                var index = (int)(key - start);
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
            var used = (int)(nextFreshKey - start);
            for (var index = 0; index < used; index++)
            {
                if (alive[index]) yield return data[index];
            }
        }


        bool TryAllocateKey(out int key)
        {
            if (freeKeys.Count > 0)
            {
                key = freeKeys.Pop();
                return true;
            }

            if (!EnsureCapacity(nextFreshKey - start + 1))
            {
                key = default;
                return false;
            }

            key = (int)nextFreshKey;
            nextFreshKey++;
            return true;
        }

        void Store(int key, SampleSchema record)
        {
            var index = (int)((long)key - start);
            record.Id = key;
            data[index] = record;
            alive[index] = true;
            Count++;
        }

        // 索引が返す Key は常に使用中のため、生存確認は行わない.
        SampleSchema Resolve(int key)
        {
            return data[(int)((long)key - start)];
        }

        bool TryGetAliveIndex(int key, out int index)
        {
            // int 同士の減算はオーバーフローし得るため long で計算する.
            var offset = (long)key - start;
            if (offset < 0 || offset >= nextFreshKey - start)
            {
                index = -1;
                return false;
            }

            index = (int)offset;
            return alive[index];
        }

        bool EnsureCapacity(long required)
        {
            if (required <= data.Length) return true;

            var max = MaxLength(start);
            if (required > max) return false;

            var doubled = Math.Max((long)data.Length * 2, MinGrowLength);
            var newLength = (int)Math.Min(Math.Max(required, doubled), max);

            Array.Resize(ref data, newLength);
            Array.Resize(ref alive, newLength);
            return true;
        }

        static int MaxLength(int start)
        {
            return (int)Math.Min(MaxArrayLength, (long)int.MaxValue - start + 1);
        }
    }
}
