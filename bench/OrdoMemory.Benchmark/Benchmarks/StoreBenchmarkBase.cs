using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace OrdoMemory.Benchmark.Benchmarks
{
    /// <summary>
    /// N 件を一括で入れたストアに対して操作を繰り返す.
    /// 1 回の呼び出しで Ops 回の操作を行い、結果は 1 操作あたりで示す.
    /// 操作の引数は長さ StreamLength の列から順に取り、同じ数件だけがキャッシュに乗り続けることを避ける.
    /// </summary>
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
            for (var i = 0; i < result.Length; i++) result[i] = rng.Next(exclusiveMax);
            return result;
        }

        // 呼び出しごとに Ops 個ずつ先へ進める.
        protected int NextBlock()
        {
            var start = cursor;
            cursor += Ops;
            if (cursor + Ops > StreamLength) cursor = 0;
            return start;
        }

        // 削除して同じ Code / Name で挿入し直す. 件数と Code の分布を保つため.
        protected void Replace(int index, int value)
        {
            store.Remove(keys[index]);
            var row = rows[index];
            row.Value = value;
            rows[index] = row;
            keys[index] = store.Insert(row);
        }
    }
}
