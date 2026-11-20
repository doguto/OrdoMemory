using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OrdoMemory.Benchmark.Stores;

namespace OrdoMemory.Benchmark
{
    /// <summary>
    /// 比較対象の共通操作. 検索は Record 全体を取り出す (SQL の SELECT * 相当) ところまでを含める.
    /// キーは各ストアの PK (OrdoMemory はハンドル、それ以外は採番した Id).
    /// </summary>
    public interface IStore : IDisposable
    {
        // 空のストアにのみ呼ぶ. 各ストアの一括挿入の手段を使う.
        void BulkLoad(ItemData[] rows, long[] keys);

        long Insert(in ItemData row);

        bool TryFind(long key, out ItemData row);

        bool TryFindByCode(int code, out ItemData row);

        bool TryFindByName(string name, out ItemData row);

        // 以下の集計は、該当する Record を全て取り出して Value を足す.
        long SumByCategory(int category);

        long SumRangeByCode(int min, int max);

        long SumAll();

        bool UpdateValue(long key, int value);

        bool UpdateCategory(long key, int category);

        bool Remove(long key);

        // 各ストアのまとめて書く手段 (トランザクション / ImmutableBuilder) で更新する.
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
        LiteDbInMemory,
    }

    public static class StoreFactory
    {
        // 1 件ごとの書き込みで全体を作り直す (O(n)) ため、1 件ずつ書くベンチマークからは外す.
        public static bool SupportsPointWrites(Target target) => target != Target.MasterMemory;

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

            // LiteDB のログは "{name}-log.db".
            var log = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "-log" + Path.GetExtension(path));
            if (File.Exists(log)) File.Delete(log);
        }
    }

    /// <summary>
    /// BenchmarkDotNet の Params はコマンドラインから変えられないため、環境変数で絞る.
    /// 子プロセスにも引き継がれる.
    ///   ORDO_BENCH_N       件数 (カンマ区切り). 既定 1000,10000,100000
    ///   ORDO_BENCH_TARGETS 比較対象 (カンマ区切り). 既定は全て
    /// </summary>
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

        public static IEnumerable<Target> WritableTargets() => Targets().Where(StoreFactory.SupportsPointWrites);
    }
}
