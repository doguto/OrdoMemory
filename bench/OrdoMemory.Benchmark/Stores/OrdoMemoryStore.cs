using System;

namespace OrdoMemory.Benchmark.Stores
{
    public sealed class OrdoMemoryStore : IStore
    {
        OrdoItemTable table = new();

        public void BulkLoad(ItemData[] rows, long[] keys)
        {
            var items = new OrdoItem[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                items[i] = ToItem(rows[i]);
            }

            var primaryKeys = new uint[rows.Length];
            if (table.TryBulkInsert(items, primaryKeys) != InsertResult.Success) throw new InvalidOperationException("bulk insert failed");
            for (var i = 0; i < primaryKeys.Length; i++)
            {
                keys[i] = primaryKeys[i];
            }
        }

        public long Insert(in ItemData row)
        {
            if (table.TryInsert(ToItem(row), out var primaryKey) != InsertResult.Success) throw new InvalidOperationException("insert failed");
            return primaryKey;
        }

        public bool TryFind(long key, out ItemData row)
        {
            var found = table.TryFind((uint)key, out var item);
            row = ToData(item);
            return found;
        }

        public bool TryFindByCode(int code, out ItemData row)
        {
            var found = table.TryFindByCode(code, out var item);
            row = ToData(item);
            return found;
        }

        public bool TryFindByName(string name, out ItemData row)
        {
            var found = table.TryFindByName(name, out var item);
            row = ToData(item);
            return found;
        }

        public long SumByCategory(int category)
        {
            long sum = 0;
            foreach (ref readonly var item in table.FindAllByCategory(category)) sum += item.Value;
            return sum;
        }

        public long SumRangeByCode(int min, int max)
        {
            long sum = 0;
            foreach (ref readonly var item in table.FindRangeByCode(min, max)) sum += item.Value;
            return sum;
        }

        public long SumAll()
        {
            long sum = 0;
            foreach (ref readonly var item in table.All()) sum += item.Value;
            return sum;
        }

        public bool UpdateValue(long key, int value)
        {
            return table.Update((uint)key, value: value);
        }

        public bool UpdateCategory(long key, int category)
        {
            return table.Update((uint)key, category: category);
        }

        public bool Remove(long key)
        {
            return table.Remove((uint)key);
        }

        public void UpdateValues(long[] keys, int[] values)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                table.Update((uint)keys[i], value: values[i]);
            }
        }

        public void Dispose()
        {
            table = null;
        }

        static OrdoItem ToItem(in ItemData row)
        {
            return new OrdoItem(row.code, row.category, row.name, row.value, row.description);
        }

        static ItemData ToData(in OrdoItem item)
        {
            return new ItemData
            {
                code = item.Code,
                category = item.Category,
                value = item.Value,
                name = item.Name,
                description = item.Description
            };
        }
    }
}
