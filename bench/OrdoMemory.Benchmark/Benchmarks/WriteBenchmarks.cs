using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace OrdoMemory.Benchmark.Benchmarks
{
    public class WriteBenchmarks : StoreBenchmarkBase
    {
        int[] uniform;
        int[] categoryStream;
        Rng valueRng;

        public override IEnumerable<Target> TargetSource => BenchEnv.WritableTargets();

        protected override void OnSetup()
        {
            uniform = UniformStream(N, Seed + 11);
            categoryStream = UniformStream(categories, Seed + 12);
            valueRng = new Rng(Seed + 13);
        }

        [Benchmark(OperationsPerInvoke = Ops, Description = "Update non-key column")]
        public void Overwrite()
        {
            var start = NextBlock();
            for (var i = 0; i < Ops; i++) store.UpdateValue(keys[uniform[start + i]], valueRng.Next(1_000_000));
        }

        [Benchmark(OperationsPerInvoke = Ops, Description = "Update secondary key")]
        public void UpdateKey()
        {
            var start = NextBlock();
            for (var i = 0; i < Ops; i++) store.UpdateCategory(keys[uniform[start + i]], categoryStream[start + i]);
        }

        [Benchmark(OperationsPerInvoke = Ops, Description = "Remove + Insert (churn)")]
        public void RemoveInsert()
        {
            var start = NextBlock();
            for (var i = 0; i < Ops; i++) Replace(uniform[start + i], valueRng.Next(1_000_000));
        }
    }

    public class BatchUpdateBenchmarks : StoreBenchmarkBase
    {
        int[] permutation;
        int position;
        long[] batchKeys;
        int[] batchValues;
        Rng valueRng;

        protected override void OnSetup()
        {
            var rng = new Rng(Seed + 21);
            permutation = new int[N];
            for (var i = 0; i < N; i++) permutation[i] = i;
            for (var i = N - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (permutation[i], permutation[j]) = (permutation[j], permutation[i]);
            }

            batchKeys = new long[Ops];
            batchValues = new int[Ops];
            valueRng = new Rng(Seed + 22);
        }

        [Benchmark(OperationsPerInvoke = Ops, Description = "Batch update (1000 rows / commit)")]
        public void BatchUpdate()
        {
            for (var i = 0; i < Ops; i++)
            {
                batchKeys[i] = keys[permutation[position]];
                if (++position == N) position = 0;
                batchValues[i] = valueRng.Next(1_000_000);
            }

            store.UpdateValues(batchKeys, batchValues);
        }
    }
}
