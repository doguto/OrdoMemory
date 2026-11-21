using SQLite;

namespace OrdoMemory.Benchmark.Stores
{
    [Table("item")]
    public sealed class SnItem
    {
        [PrimaryKey]
        public int Id { get; set; }

        [Indexed(Name = "ix_item_code", Unique = true)]
        public int Code { get; set; }

        [Indexed(Name = "ix_item_category")]
        public int Category { get; set; }

        public int Value { get; set; }

        [Indexed(Name = "ix_item_name", Unique = true)]
        public string Name { get; set; }

        public string Description { get; set; }
    }

    public sealed class SqliteNetStore : IStore
    {
        readonly string path;
        readonly SQLiteConnection connection;
        int nextId = 1;

        public SqliteNetStore(string path)
        {
            this.path = path;
            var flags = SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.NoMutex;
            connection = new SQLiteConnection(new SQLiteConnectionString(path ?? ":memory:", flags, true));
            if (path != null)
            {
                connection.ExecuteScalar<string>("PRAGMA journal_mode=WAL;");
                connection.Execute("PRAGMA synchronous=NORMAL;");
            }

            connection.CreateTable<SnItem>();
        }

        public void BulkLoad(ItemData[] rows, long[] keys)
        {
            var items = new SnItem[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                items[i] = ToItem(nextId++, rows[i]);
                keys[i] = items[i].Id;
            }

            connection.InsertAll(items);
        }

        public long Insert(in ItemData row)
        {
            var item = ToItem(nextId++, row);
            connection.Insert(item);
            return item.Id;
        }

        public bool TryFind(long key, out ItemData row) => ToData(connection.Find<SnItem>((int)key), out row);

        public bool TryFindByCode(int code, out ItemData row) =>
            ToData(connection.FindWithQuery<SnItem>("SELECT * FROM item WHERE Code = ?", code), out row);

        public bool TryFindByName(string name, out ItemData row) =>
            ToData(connection.FindWithQuery<SnItem>("SELECT * FROM item WHERE Name = ?", name), out row);

        public long SumByCategory(int category) => Sum(connection.Query<SnItem>("SELECT * FROM item WHERE Category = ?", category));

        public long SumRangeByCode(int min, int max) =>
            Sum(connection.Query<SnItem>("SELECT * FROM item WHERE Code BETWEEN ? AND ?", min, max));

        public long SumAll()
        {
            long sum = 0;
            foreach (var item in connection.Table<SnItem>()) sum += item.Value;
            return sum;
        }

        public bool UpdateValue(long key, int value) =>
            connection.Execute("UPDATE item SET Value = ? WHERE Id = ?", value, (int)key) == 1;

        public bool UpdateCategory(long key, int category) =>
            connection.Execute("UPDATE item SET Category = ? WHERE Id = ?", category, (int)key) == 1;

        public bool Remove(long key) => connection.Delete<SnItem>((int)key) == 1;

        public void UpdateValues(long[] keys, int[] values)
        {
            connection.RunInTransaction(() =>
            {
                for (var i = 0; i < keys.Length; i++) UpdateValue(keys[i], values[i]);
            });
        }

        public void Dispose()
        {
            connection.Dispose();
            if (path != null) StoreFactory.DeleteFiles(path);
        }

        static long Sum(System.Collections.Generic.List<SnItem> items)
        {
            long sum = 0;
            foreach (var item in items) sum += item.Value;
            return sum;
        }

        static SnItem ToItem(int id, in ItemData row) => new SnItem
        {
            Id = id,
            Code = row.Code,
            Category = row.Category,
            Value = row.Value,
            Name = row.Name,
            Description = row.Description,
        };

        static bool ToData(SnItem item, out ItemData row)
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
