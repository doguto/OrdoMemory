using BenchmarkDotNet.Attributes;

namespace OrdoMemory.Benchmark.Benchmarks
{
    /// <summary>
    /// 読み取りのみ. 全ての比較対象で測る.
    /// 名前の対応: ReadRandom = db_bench readrandom, ReadZipfian = YCSB Workload C, ScanAll = db_bench readseq.
    /// </summary>
    public class ReadBenchmarks : StoreBenchmarkBase
    {
        public const int RangeLength = 64;

        int[] uniform;
        int[] zipfian;
        int[] categoryStream;
        int[] rangeStart;

        protected override void OnSetup()
        {
            uniform = UniformStream(N, Seed + 1);
            categoryStream = UniformStream(categories, Seed + 2);
            rangeStart = UniformStream(System.Math.Max(1, N - RangeLength + 1), Seed + 3);

            var zipf = new Zipfian(N, Seed + 4);
            zipfian = new int[StreamLength];
            for (var i = 0; i < zipfian.Length; i++) zipfian[i] = zipf.Next();
        }

        [Benchmark(OperationsPerInvoke = Ops, Description = "PK (uniform)")]
        public long ReadRandom()
        {
            var start = NextBlock();
            long sum = 0;
            for (var i = 0; i < Ops; i++)
            {
                store.TryFind(keys[uniform[start + i]], out var row);
                sum += row.Value;
            }

            return sum;
        }

        [Benchmark(OperationsPerInvoke = Ops, Description = "PK (zipfian) = YCSB-C")]
        public long ReadZipfian()
        {
            var start = NextBlock();
            long sum = 0;
            for (var i = 0; i < Ops; i++)
            {
                store.TryFind(keys[zipfian[start + i]], out var row);
                sum += row.Value;
            }

            return sum;
        }

        [Benchmark(OperationsPerInvoke = Ops, Description = "Unique int key")]
        public long ReadByCode()
        {
            var start = NextBlock();
            long sum = 0;
            for (var i = 0; i < Ops; i++)
            {
                store.TryFindByCode(rows[uniform[start + i]].Code, out var row);
                sum += row.Value;
            }

            return sum;
        }

        [Benchmark(OperationsPerInvoke = Ops, Description = "Unique string key")]
        public long ReadByName()
        {
            var start = NextBlock();
            long sum = 0;
            for (var i = 0; i < Ops; i++)
            {
                store.TryFindByName(rows[uniform[start + i]].Name, out var row);
                sum += row.Value;
            }

            return sum;
        }

        [Benchmark(OperationsPerInvoke = Ops, Description = "Non-unique key (~64 rows)")]
        public long ReadByCategory()
        {
            var start = NextBlock();
            long sum = 0;
            for (var i = 0; i < Ops; i++) sum += store.SumByCategory(categoryStream[start + i]);
            return sum;
        }

        [Benchmark(OperationsPerInvoke = Ops, Description = "Range (64 rows)")]
        public long ScanRange()
        {
            var start = NextBlock();
            long sum = 0;
            for (var i = 0; i < Ops; i++)
            {
                var min = rangeStart[start + i];
                sum += store.SumRangeByCode(min, min + RangeLength - 1);
            }

            return sum;
        }

        [Benchmark(Description = "Scan all")]
        public long ScanAll()
        {
            return store.SumAll();
        }
    }
}
