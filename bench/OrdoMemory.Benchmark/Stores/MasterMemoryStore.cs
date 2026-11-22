using System;
using System.Collections.Generic;
using OrdoMemory.Benchmark.MasterMemoryDb;
using OrdoMemory.Benchmark.MasterMemoryDb.Tables;

namespace OrdoMemory.Benchmark.Stores
{
    public sealed class MasterMemoryStore : IStore
    {
        MemoryDatabase db = new(new MmItemTable(Array.Empty<MmItem>()));
        int nextId = 1;

        public void BulkLoad(ItemData[] rows, long[] keys)
        {
            var items = new MmItem[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                items[i] = ToItem(nextId++, rows[i]);
                keys[i] = items[i].Id;
            }

            var builder = new DatabaseBuilder();
            builder.Append(items);
            db = new MemoryDatabase(builder.Build());
        }

        public long Insert(in ItemData row)
        {
            var item = ToItem(nextId++, row);
            var builder = db.ToImmutableBuilder();
            builder.Diff(new[] { item });
            db = builder.Build();
            return item.Id;
        }

        public bool TryFind(long key, out ItemData row)
        {
            var found = db.MmItemTable.TryFindById((int)key, out var item);
            row = found ? ToData(item) : default;
            return found;
        }

        public bool TryFindByCode(int code, out ItemData row)
        {
            var found = db.MmItemTable.TryFindByCode(code, out var item);
            row = found ? ToData(item) : default;
            return found;
        }

        public bool TryFindByName(string name, out ItemData row)
        {
            var found = db.MmItemTable.TryFindByName(name, out var item);
            row = found ? ToData(item) : default;
            return found;
        }

        public long SumByCategory(int category)
        {
            long sum = 0;
            var view = db.MmItemTable.FindByCategory(category);
            for (var i = 0; i < view.Count; i++)
            {
                sum += view[i].Value;
            }

            return sum;
        }

        public long SumRangeByCode(int min, int max)
        {
            long sum = 0;
            var view = db.MmItemTable.FindRangeByCode(min, max);
            for (var i = 0; i < view.Count; i++)
            {
                sum += view[i].Value;
            }

            return sum;
        }

        public long SumAll()
        {
            long sum = 0;
            var view = db.MmItemTable.All;
            for (var i = 0; i < view.Count; i++)
            {
                sum += view[i].Value;
            }

            return sum;
        }

        public bool UpdateValue(long key, int value)
        {
            if (!db.MmItemTable.TryFindById((int)key, out _)) return false;
            UpdateValues(new[] { key }, new[] { value });
            return true;
        }

        public bool UpdateCategory(long key, int category)
        {
            if (!db.MmItemTable.TryFindById((int)key, out var current)) return false;
            var next = Clone(current);
            next.Category = category;
            var builder = db.ToImmutableBuilder();
            builder.Diff(new[] { next });
            db = builder.Build();
            return true;
        }

        public bool Remove(long key)
        {
            if (!db.MmItemTable.TryFindById((int)key, out _)) return false;
            var builder = db.ToImmutableBuilder();
            builder.RemoveMmItem(new[] { (int)key });
            db = builder.Build();
            return true;
        }

        public void UpdateValues(long[] keys, int[] values)
        {
            var changed = new List<MmItem>(keys.Length);
            for (var i = 0; i < keys.Length; i++)
            {
                if (!db.MmItemTable.TryFindById((int)keys[i], out var current)) continue;
                var next = Clone(current);
                next.Value = values[i];
                changed.Add(next);
            }

            var builder = db.ToImmutableBuilder();
            builder.Diff(changed.ToArray());
            db = builder.Build();
        }

        public void Dispose()
        {
            db = null;
        }

        static MmItem ToItem(int id, in ItemData row)
        {
            return new MmItem
            {
                Id = id,
                Code = row.code,
                Category = row.category,
                Name = row.name,
                Value = row.value,
                Description = row.description
            };
        }

        static MmItem Clone(MmItem item)
        {
            return new MmItem
            {
                Id = item.Id,
                Code = item.Code,
                Category = item.Category,
                Name = item.Name,
                Value = item.Value,
                Description = item.Description
            };
        }

        static ItemData ToData(MmItem item)
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
