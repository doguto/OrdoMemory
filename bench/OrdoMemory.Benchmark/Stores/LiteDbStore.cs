using System.IO;
using LiteDB;

namespace OrdoMemory.Benchmark.Stores
{
    public sealed class LdItem
    {
        [BsonId]
        public int Id { get; set; }

        public int Code { get; set; }

        public int Category { get; set; }

        public int Value { get; set; }

        public string Name { get; set; }

        public string Description { get; set; }
    }

    /// <summary>
    /// LiteDB 5 (組み込みのドキュメント DB). path が null なら MemoryStream 上に置く.
    /// 部分更新の API が無いため、更新は読み出し → 書き戻しとする.
    /// </summary>
    public sealed class LiteDbStore : IStore
    {
        readonly string path;
        readonly LiteDatabase db;
        readonly ILiteCollection<LdItem> items;
        int nextId = 1;

        public LiteDbStore(string path)
        {
            this.path = path;
            db = path == null
                ? new LiteDatabase(new MemoryStream())
                : new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct });

            items = db.GetCollection<LdItem>("item");
            items.EnsureIndex(x => x.Code, true);
            items.EnsureIndex(x => x.Category);
            items.EnsureIndex(x => x.Name, true);
        }

        public void BulkLoad(ItemData[] rows, long[] keys)
        {
            var docs = new LdItem[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                docs[i] = ToItem(nextId++, rows[i]);
                keys[i] = docs[i].Id;
            }

            items.InsertBulk(docs);
        }

        public long Insert(in ItemData row)
        {
            var item = ToItem(nextId++, row);
            items.Insert(item);
            return item.Id;
        }

        public bool TryFind(long key, out ItemData row) => ToData(items.FindById((int)key), out row);

        public bool TryFindByCode(int code, out ItemData row) => ToData(items.FindOne(Query.EQ(nameof(LdItem.Code), code)), out row);

        public bool TryFindByName(string name, out ItemData row) => ToData(items.FindOne(Query.EQ(nameof(LdItem.Name), name)), out row);

        public long SumByCategory(int category)
        {
            long sum = 0;
            foreach (var item in items.Find(Query.EQ(nameof(LdItem.Category), category))) sum += item.Value;
            return sum;
        }

        public long SumRangeByCode(int min, int max)
        {
            long sum = 0;
            foreach (var item in items.Find(Query.Between(nameof(LdItem.Code), min, max))) sum += item.Value;
            return sum;
        }

        public long SumAll()
        {
            long sum = 0;
            foreach (var item in items.FindAll()) sum += item.Value;
            return sum;
        }

        public bool UpdateValue(long key, int value)
        {
            var item = items.FindById((int)key);
            if (item == null) return false;
            item.Value = value;
            return items.Update(item);
        }

        public bool UpdateCategory(long key, int category)
        {
            var item = items.FindById((int)key);
            if (item == null) return false;
            item.Category = category;
            return items.Update(item);
        }

        public bool Remove(long key) => items.Delete((int)key);

        public void UpdateValues(long[] keys, int[] values)
        {
            db.BeginTrans();
            for (var i = 0; i < keys.Length; i++) UpdateValue(keys[i], values[i]);
            db.Commit();
        }

        public void Dispose()
        {
            db.Dispose();
            if (path != null) StoreFactory.DeleteFiles(path);
        }

        static LdItem ToItem(int id, in ItemData row) => new LdItem
        {
            Id = id,
            Code = row.Code,
            Category = row.Category,
            Value = row.Value,
            Name = row.Name,
            Description = row.Description,
        };

        static bool ToData(LdItem item, out ItemData row)
        {
            if (item == null)
            {
                row = default;
                return false;
            }

            row = new ItemData
            {
                Code = item.Code,
                Category = item.Category,
                Value = item.Value,
                Name = item.Name,
                Description = item.Description,
            };
            return true;
        }
    }
}
