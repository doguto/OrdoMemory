using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace OrdoMemory.Benchmark.Benchmarks
{
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

    public class BulkLoadBenchmarks : LoadBenchmarkBase
    {
        [Benchmark(Description = "Bulk load N rows")]
        public void BulkLoad()
        {
            store.BulkLoad(rows, keys);
        }
    }

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
