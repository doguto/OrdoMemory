using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace OrdoMemory.Benchmark.Benchmarks
{
    public abstract class StoreBenchmarkBase
    {
        public const int Ops = 1000;
        protected const int StreamLength = 1 << 16;
        protected const ulong Seed = 20261120;

        protected IStore store;
        protected ItemData[] rows;
        protected long[] keys;
        protected int categories;
        protected int cursor;
        protected long sink;

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
            categories = Dataset.CategoryCount(N);
            store = StoreFactory.Create(Target);
            store.BulkLoad(rows, keys);
            OnSetup();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            store.Dispose();
            store = null;
        }

        protected virtual void OnSetup()
        {
        }

        protected int[] UniformStream(int exclusiveMax, ulong seed)
        {
            var rng = new Rng(seed);
            var result = new int[StreamLength];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = rng.Next(exclusiveMax);
            }

            return result;
        }

        protected int NextBlock()
        {
            var start = cursor;
            cursor += Ops;
            if (cursor + Ops > StreamLength) cursor = 0;
            return start;
        }

        protected void Replace(int index, int value)
        {
            store.Remove(keys[index]);
            var row = rows[index];
            row.value = value;
            rows[index] = row;
            keys[index] = store.Insert(row);
        }
    }
}
