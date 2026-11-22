using System.Collections.Generic;

namespace OrdoMemory.Benchmark.Stores
{
    public sealed class DictionaryStore : IStore
    {
        readonly Dictionary<int, ItemData> rows = new();
        readonly Dictionary<int, int> byCode = new();
        readonly Dictionary<string, int> byName = new(System.StringComparer.Ordinal);
        readonly Dictionary<int, HashSet<int>> byCategory = new();
        readonly SortedSet<int> codes = new();
        int nextId = 1;

        public void BulkLoad(ItemData[] source, long[] keys)
        {
            rows.EnsureCapacity(source.Length);
            byCode.EnsureCapacity(source.Length);
            byName.EnsureCapacity(source.Length);
            for (var i = 0; i < source.Length; i++)
            {
                keys[i] = Insert(source[i]);
            }
        }

        public long Insert(in ItemData row)
        {
            var id = nextId++;
            rows.Add(id, row);
            byCode.Add(row.code, id);
            byName.Add(row.name, id);
            AddCategory(row.category, id);
            codes.Add(row.code);
            return id;
        }

        public bool TryFind(long key, out ItemData row)
        {
            return rows.TryGetValue((int)key, out row);
        }

        public bool TryFindByCode(int code, out ItemData row)
        {
            if (byCode.TryGetValue(code, out var id)) return rows.TryGetValue(id, out row);
            row = default;
            return false;
        }

        public bool TryFindByName(string name, out ItemData row)
        {
            if (byName.TryGetValue(name, out var id)) return rows.TryGetValue(id, out row);
            row = default;
            return false;
        }

        public long SumByCategory(int category)
        {
            long sum = 0;
            if (!byCategory.TryGetValue(category, out var ids)) return 0;
            foreach (var id in ids) sum += rows[id].value;
            return sum;
        }

        public long SumRangeByCode(int min, int max)
        {
            long sum = 0;
            foreach (var code in codes.GetViewBetween(min, max)) sum += rows[byCode[code]].value;
            return sum;
        }

        public long SumAll()
        {
            long sum = 0;
            foreach (var row in rows.Values) sum += row.value;
            return sum;
        }

        public bool UpdateValue(long key, int value)
        {
            if (!rows.TryGetValue((int)key, out var row)) return false;
            row.value = value;
            rows[(int)key] = row;
            return true;
        }

        public bool UpdateCategory(long key, int category)
        {
            var id = (int)key;
            if (!rows.TryGetValue(id, out var row)) return false;
            if (row.category != category)
            {
                RemoveCategory(row.category, id);
                AddCategory(category, id);
                row.category = category;
                rows[id] = row;
            }

            return true;
        }

        public bool Remove(long key)
        {
            var id = (int)key;
            if (!rows.Remove(id, out var row)) return false;
            byCode.Remove(row.code);
            byName.Remove(row.name);
            RemoveCategory(row.category, id);
            codes.Remove(row.code);
            return true;
        }

        public void UpdateValues(long[] keys, int[] values)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                UpdateValue(keys[i], values[i]);
            }
        }

        public void Dispose()
        {
        }

        void AddCategory(int category, int id)
        {
            if (!byCategory.TryGetValue(category, out var ids))
            {
                ids = new HashSet<int>();
                byCategory.Add(category, ids);
            }

            ids.Add(id);
        }

        void RemoveCategory(int category, int id)
        {
            var ids = byCategory[category];
            ids.Remove(id);
            if (ids.Count == 0) byCategory.Remove(category);
        }
    }
}
