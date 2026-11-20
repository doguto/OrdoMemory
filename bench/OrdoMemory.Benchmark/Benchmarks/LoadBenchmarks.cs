using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace OrdoMemory.Benchmark.Benchmarks
{
    /// <summary>
    /// 空のストアに N 件を入れるまで. 1 回ごとに新しいストアを作るため、呼び出しは 1 反復につき 1 回.
    /// 結果は N 件全体の時間 (1 件あたりではない).
    /// </summary>
    [InvocationCount(1)]
    public abstract class LoadBenchmarkBase
    {
        protected const ulong Seed = 20261120;

        protected IStore store;
        protected ItemData[] rows;
        protected long[] keys;

        [ParamsSource(nameof(TargetSource))]
        public Target Target { get; set; }

        [ParamsSource(nameof(SizeSource))]
        public int N { get; set; }

        public virtual IEnumerable<Target> TargetSource => BenchEnv.Targets();

        public IEnumerable<int> SizeSource => BenchEnv.Sizes();

        [GlobalSetup]
        public void GlobalSetup()
        {
            rows = Dataset.Create(N, Seed);
            keys = new long[N];
        }

        [IterationSetup]
        public void IterationSetup()
        {
            store = StoreFactory.Create(Target);
        }

        [IterationCleanup]
        public void IterationCleanup()
        {
            store.Dispose();
            store = null;
        }
    }

    // 名前の対応: db_bench fillrandom (一括).
    public class BulkLoadBenchmarks : LoadBenchmarkBase
    {
        [Benchmark(Description = "Bulk load N rows")]
        public void BulkLoad()
        {
            store.BulkLoad(rows, keys);
        }
    }

    // 名前の対応: db_bench fillrandom (1 件ずつ, 自動コミット).
    public class InsertBenchmarks : LoadBenchmarkBase
    {
        public override IEnumerable<Target> TargetSource => BenchEnv.WritableTargets();

        [Benchmark(Description = "Insert N rows one by one")]
        public void InsertOneByOne()
        {
            for (var i = 0; i < rows.Length; i++) keys[i] = store.Insert(rows[i]);
        }
    }
}
