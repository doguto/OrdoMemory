using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace OrdoMemory.Benchmark.Benchmarks
{
    /// <summary>
    /// 1 件ずつの書き込み. 件数を保つ操作だけにし、繰り返しても状態が変わらないようにする.
    /// 名前の対応: Overwrite = db_bench overwrite, RemoveInsert = deleterandom + fillrandom.
    /// MasterMemory は 1 件ごとに全体を作り直すため除く (BatchUpdateBenchmarks で測る).
    /// </summary>
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

    /// <summary>
    /// Ops 件の更新を、各ストアのまとめて書く手段 (トランザクション / ImmutableBuilder) で 1 回に行う.
    /// </summary>
    public class BatchUpdateBenchmarks : StoreBenchmarkBase
    {
        // 1 回のバッチ内で同じ Record が重ならないよう、順列から連続して取る.
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
