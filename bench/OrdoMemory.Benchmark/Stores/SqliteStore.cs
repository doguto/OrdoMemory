using Microsoft.Data.Sqlite;

namespace OrdoMemory.Benchmark.Stores
{
    public sealed class SqliteStore : IStore
    {
        const string Columns = "code, category, value, name, description";

        readonly string path;
        readonly SqliteConnection connection;
        readonly SqliteCommand insert;
        readonly SqliteCommand find;
        readonly SqliteCommand findByCode;
        readonly SqliteCommand findByName;
        readonly SqliteCommand sumByCategory;
        readonly SqliteCommand sumRangeByCode;
        readonly SqliteCommand sumAll;
        readonly SqliteCommand updateValue;
        readonly SqliteCommand updateCategory;
        readonly SqliteCommand remove;
        long nextId = 1;

        public SqliteStore(string path)
        {
            this.path = path;
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path ?? ":memory:",
                Pooling = false
            }.ToString());
            connection.Open();

            if (path != null)
            {
                Execute("PRAGMA journal_mode=WAL;");
                Execute("PRAGMA synchronous=NORMAL;");
            }

            Execute(@"CREATE TABLE item (
                id INTEGER PRIMARY KEY,
                code INTEGER NOT NULL,
                category INTEGER NOT NULL,
                value INTEGER NOT NULL,
                name TEXT NOT NULL,
                description TEXT NOT NULL);
                CREATE UNIQUE INDEX ix_item_code ON item(code);
                CREATE INDEX ix_item_category ON item(category);
                CREATE UNIQUE INDEX ix_item_name ON item(name);");

            insert = Prepare($"INSERT INTO item (id, {Columns}) VALUES ($id, $code, $category, $value, $name, $description)",
                "$id", "$code", "$category", "$value", "$name", "$description");
            find = Prepare($"SELECT {Columns} FROM item WHERE id = $p0", "$p0");
            findByCode = Prepare($"SELECT {Columns} FROM item WHERE code = $p0", "$p0");
            findByName = Prepare($"SELECT {Columns} FROM item WHERE name = $p0", "$p0");
            sumByCategory = Prepare($"SELECT {Columns} FROM item WHERE category = $p0", "$p0");
            sumRangeByCode = Prepare($"SELECT {Columns} FROM item WHERE code BETWEEN $p0 AND $p1", "$p0", "$p1");
            sumAll = Prepare($"SELECT {Columns} FROM item");
            updateValue = Prepare("UPDATE item SET value = $p1 WHERE id = $p0", "$p0", "$p1");
            updateCategory = Prepare("UPDATE item SET category = $p1 WHERE id = $p0", "$p0", "$p1");
            remove = Prepare("DELETE FROM item WHERE id = $p0", "$p0");
        }

        public void BulkLoad(ItemData[] rows, long[] keys)
        {
            using var tx = connection.BeginTransaction();
            insert.Transaction = tx;
            for (var i = 0; i < rows.Length; i++)
            {
                keys[i] = Insert(rows[i]);
            }

            tx.Commit();
            insert.Transaction = null;
        }

        public long Insert(in ItemData row)
        {
            var id = nextId++;
            var p = insert.Parameters;
            p[0].Value = id;
            p[1].Value = row.code;
            p[2].Value = row.category;
            p[3].Value = row.value;
            p[4].Value = row.name;
            p[5].Value = row.description;
            insert.ExecuteNonQuery();
            return id;
        }

        public bool TryFind(long key, out ItemData row)
        {
            return QuerySingle(find, key, out row);
        }

        public bool TryFindByCode(int code, out ItemData row)
        {
            return QuerySingle(findByCode, code, out row);
        }

        public bool TryFindByName(string name, out ItemData row)
        {
            return QuerySingle(findByName, name, out row);
        }

        public long SumByCategory(int category)
        {
            sumByCategory.Parameters[0].Value = category;
            return SumRows(sumByCategory);
        }

        public long SumRangeByCode(int min, int max)
        {
            sumRangeByCode.Parameters[0].Value = min;
            sumRangeByCode.Parameters[1].Value = max;
            return SumRows(sumRangeByCode);
        }

        public long SumAll()
        {
            return SumRows(sumAll);
        }

        public bool UpdateValue(long key, int value)
        {
            return ExecuteKeyed(updateValue, key, value);
        }

        public bool UpdateCategory(long key, int category)
        {
            return ExecuteKeyed(updateCategory, key, category);
        }

        public bool Remove(long key)
        {
            remove.Parameters[0].Value = key;
            return remove.ExecuteNonQuery() == 1;
        }

        public void UpdateValues(long[] keys, int[] values)
        {
            using var tx = connection.BeginTransaction();
            updateValue.Transaction = tx;
            for (var i = 0; i < keys.Length; i++)
            {
                ExecuteKeyed(updateValue, keys[i], values[i]);
            }

            tx.Commit();
            updateValue.Transaction = null;
        }

        public void Dispose()
        {
            connection.Dispose();
            if (path != null) StoreFactory.DeleteFiles(path);
        }

        static bool QuerySingle(SqliteCommand command, object key, out ItemData row)
        {
            command.Parameters[0].Value = key;
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                row = default;
                return false;
            }

            row = ReadRow(reader);
            return true;
        }

        static long SumRows(SqliteCommand command)
        {
            long sum = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read()) sum += ReadRow(reader).value;
            return sum;
        }

        static ItemData ReadRow(SqliteDataReader reader)
        {
            return new ItemData
            {
                code = reader.GetInt32(0),
                category = reader.GetInt32(1),
                value = reader.GetInt32(2),
                name = reader.GetString(3),
                description = reader.GetString(4)
            };
        }

        static bool ExecuteKeyed(SqliteCommand command, long key, int value)
        {
            command.Parameters[0].Value = key;
            command.Parameters[1].Value = value;
            return command.ExecuteNonQuery() == 1;
        }

        SqliteCommand Prepare(string sql, params string[] parameters)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var name in parameters) command.Parameters.Add(new SqliteParameter(name, null));
            command.Prepare();
            return command;
        }

        void Execute(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
