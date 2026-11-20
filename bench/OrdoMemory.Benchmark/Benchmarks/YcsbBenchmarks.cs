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
        F,
    }

    /// <summary>
    /// YCSB のコアワークロード (https://github.com/brianfrankcooper/YCSB/wiki/Core-Workloads).
    ///   A: read 50 / update 50 (zipfian)
    ///   B: read 95 / update 5 (zipfian)
    ///   C: read 100 → ReadBenchmarks.ReadZipfian
    ///   D: read 95 / insert 5 (最近挿入したものほど読む)
    ///   E: scan 95 / insert 5 (開始位置 zipfian, 長さ 1〜100 の一様分布)
    ///   F: read 50 / read-modify-write 50 (zipfian)
    /// YCSB との違い: insert は最も古い Record を消してから入れ、件数を N に保つ (繰り返し呼んでも状態が変わらないように).
    /// </summary>
    public class YcsbBenchmarks : StoreBenchmarkBase
    {
        const int MaxScanLength = 100;

        enum Op : byte
        {
            Read,
            Update,
            Insert,
            Scan,
            ReadModifyWrite,
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
            var latest = new Zipfian(N, Seed + 33, scramble: false);
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
                        sum += row.Value;
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
                        store.UpdateValue(key, current.Value + 1);
                        break;
                }
            }

            return sum;
        }

        // D では args は「最新から何番目か」. 論理位置は挿入順に並び、oldest - 1 が最新.
        int ReadIndex(int arg)
        {
            if (Workload != YcsbWorkload.D) return arg;
            var index = oldest - 1 - arg;
            return index < 0 ? index + N : index;
        }
    }
}
