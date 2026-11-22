using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OrdoMemory.Benchmark.Stores;

namespace OrdoMemory.Benchmark
{
    public interface IStore : IDisposable
    {
        void BulkLoad(ItemData[] rows, long[] keys);

        long Insert(in ItemData row);

        bool TryFind(long key, out ItemData row);

        bool TryFindByCode(int code, out ItemData row);

        bool TryFindByName(string name, out ItemData row);

        long SumByCategory(int category);

        long SumRangeByCode(int min, int max);

        long SumAll();

        bool UpdateValue(long key, int value);

        bool UpdateCategory(long key, int category);

        bool Remove(long key);

        void UpdateValues(long[] keys, int[] values);
    }

    public enum Target
    {
        OrdoMemory,
        Dictionary,
        MasterMemory,
        Sqlite,
        SqliteInMemory,
        SqliteNet,
        SqliteNetInMemory,
        LiteDb,
        LiteDbInMemory
    }

    public static class StoreFactory
    {
        public static bool SupportsPointWrites(Target target)
        {
            return target != Target.MasterMemory;
        }

        public static IStore Create(Target target)
        {
            switch (target)
            {
                case Target.OrdoMemory: return new OrdoMemoryStore();
                case Target.Dictionary: return new DictionaryStore();
                case Target.MasterMemory: return new MasterMemoryStore();
                case Target.Sqlite: return new SqliteStore(TempFile("sqlite", ".db"));
                case Target.SqliteInMemory: return new SqliteStore(null);
                case Target.SqliteNet: return new SqliteNetStore(TempFile("sqlitenet", ".db"));
                case Target.SqliteNetInMemory: return new SqliteNetStore(null);
                case Target.LiteDb: return new LiteDbStore(TempFile("litedb", ".db"));
                case Target.LiteDbInMemory: return new LiteDbStore(null);
                default: throw new ArgumentOutOfRangeException(nameof(target));
            }
        }

        static string TempFile(string prefix, string extension)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ordomemory-bench");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"{prefix}-{Guid.NewGuid():N}{extension}");
        }

        public static void DeleteFiles(string path)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal", "-log" })
            {
                var file = path + suffix;
                if (File.Exists(file)) File.Delete(file);
            }

            var log = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "-log" + Path.GetExtension(path));
            if (File.Exists(log)) File.Delete(log);
        }
    }

    public static class BenchEnv
    {
        public static IEnumerable<int> Sizes()
        {
            var value = Environment.GetEnvironmentVariable("ORDO_BENCH_N");
            if (string.IsNullOrWhiteSpace(value)) return new[] { 1_000, 10_000, 100_000 };
            return value.Split(',').Select(s => int.Parse(s.Trim().Replace("_", "")));
        }

        public static IEnumerable<Target> Targets()
        {
            var value = Environment.GetEnvironmentVariable("ORDO_BENCH_TARGETS");
            if (string.IsNullOrWhiteSpace(value)) return (Target[])Enum.GetValues(typeof(Target));
            return value.Split(',').Select(s => (Target)Enum.Parse(typeof(Target), s.Trim(), true));
        }

        public static IEnumerable<Target> WritableTargets()
        {
            return Targets().Where(StoreFactory.SupportsPointWrites);
        }
    }
}
