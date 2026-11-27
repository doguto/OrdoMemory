using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace OrdoMemory.Benchmark.Benchmarks
{
    public enum YcsbWorkload
    {
        A,
        B,
        D,
        E,
        F
    }

    public class YcsbBenchmarks : StoreBenchmarkBase
    {
        const int MaxScanLength = 100;

        enum Op : byte
        {
            Read,
            Update,
            Insert,
            Scan,
            ReadModifyWrite
        }

        Op[] ops;
        int[] args;
        int[] scanLengths;
        int oldest;
        Rng valueRng;

        [ParamsAllValues]
        public YcsbWorkload Workload { get; set; }

        public override IEnumerable<Target> TargetSource => BenchEnv.WritableTargets();

        protected override void OnSetup()
        {
            var rng = new Rng(Seed + 31);
            var scrambled = new Zipfian(N, Seed + 32);
            var latest = new Zipfian(N, Seed + 33, false);
            valueRng = new Rng(Seed + 34);

            ops = new Op[StreamLength];
            args = new int[StreamLength];
            scanLengths = new int[StreamLength];
            for (var i = 0; i < StreamLength; i++)
            {
                var p = rng.Next(100);
                switch (Workload)
                {
                    case YcsbWorkload.A:
                        ops[i] = p < 50 ? Op.Read : Op.Update;
                        args[i] = scrambled.Next();
                        break;
                    case YcsbWorkload.B:
                        ops[i] = p < 95 ? Op.Read : Op.Update;
                        args[i] = scrambled.Next();
                        break;
                    case YcsbWorkload.D:
                        ops[i] = p < 95 ? Op.Read : Op.Insert;
                        args[i] = latest.Next();
                        break;
                    case YcsbWorkload.E:
                        ops[i] = p < 95 ? Op.Scan : Op.Insert;
                        args[i] = scrambled.Next();
                        scanLengths[i] = 1 + rng.Next(MaxScanLength);
                        break;
                    case YcsbWorkload.F:
                        ops[i] = p < 50 ? Op.Read : Op.ReadModifyWrite;
                        args[i] = scrambled.Next();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        [Benchmark(OperationsPerInvoke = Ops)]
        public long Run()
        {
            var start = NextBlock();
            long sum = 0;
            for (var i = start; i < start + Ops; i++)
            {
                switch (ops[i])
                {
                    case Op.Read:
                        store.TryFind(keys[ReadIndex(args[i])], out var row);
                        sum += row.value;
                        break;
                    case Op.Update:
                        store.UpdateValue(keys[args[i]], valueRng.Next(1_000_000));
                        break;
                    case Op.Insert:
                        Replace(oldest, valueRng.Next(1_000_000));
                        if (++oldest == N) oldest = 0;
                        break;
                    case Op.Scan:
                        sum += store.SumRangeByCode(args[i], args[i] + scanLengths[i] - 1);
                        break;
                    case Op.ReadModifyWrite:
                        var key = keys[args[i]];
                        store.TryFind(key, out var current);
                        store.UpdateValue(key, current.value + 1);
                        break;
                }
            }

            return sum;
        }

        int ReadIndex(int arg)
        {
            if (Workload != YcsbWorkload.D) return arg;
            var index = oldest - 1 - arg;
            return index < 0 ? index + N : index;
        }
    }
}
