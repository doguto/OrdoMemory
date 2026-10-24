using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OrdoMemory.Bench.Pk
{
    public sealed class PkResult
    {
        public string Name;
        public readonly Dictionary<string, double> Metrics = new Dictionary<string, double>();
    }

    public static class PkBench
    {
        public static readonly string[] MetricOrder =
        {
            "insert ns", "mem B/row", "find ns", "update ns",
            "churn ns", "churn p99.9 ns", "churn max us", "find(churned) ns", "all ns/row", "range64 ns", "mem(churned) B/row",
            "fifo ns", "fifo max us", "find(fifo) ns",
            "alloc B/query",
        };

        public static IEnumerable<Func<IPkTable>> Factories()
        {
            yield return () => new CurrentClassW { T = new CurrentClassTable() };
            yield return () => new CurrentStructW { T = new CurrentStructTable(false) };
            yield return () => new CurrentStructW { T = new CurrentStructTable(true) };
            yield return () => new RankW { T = new RankTable() };
            yield return () => new BlockW { T = new BlockTable() };
            yield return () => new SegmentedRankW { T = new SegmentedRankTable() };
            yield return () => new SlotMapW { T = new SlotMapTable() };
            yield return () => new DictionaryW { T = new DictionaryTable() };
        }

        // ---- 計測 ----

        public static void RunAll(int n, int runs, string filter)
        {
            var names = Names(1024);
            var results = new Dictionary<string, List<PkResult>>();
            var order = new List<string>();

            // ウォームアップ (JIT の段階コンパイルを済ませる).
            foreach (var f in Factories())
            {
                Dispatch(f(), Math.Min(n, 20000), names, 1);
                DispatchFifo(f(), Math.Min(n, 20000), names, 1);
            }

            for (var r = 0; r < runs; r++)
            {
                foreach (var f in Factories())
                {
                    var probe = f();
                    if (filter != null && probe.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var res = Dispatch(probe, n, names, (ulong)(r + 1));
                    foreach (var p in DispatchFifo(f(), n, names, (ulong)(r + 7))) res.Metrics[p.Key] = p.Value;
                    if (!results.ContainsKey(res.Name))
                    {
                        results[res.Name] = new List<PkResult>();
                        order.Add(res.Name);
                    }

                    results[res.Name].Add(res);
                    Console.Error.WriteLine($"  run {r + 1}/{runs} {res.Name} done");
                }
            }

            Report.PrintTable($"PK storage  N={n:N0}  runs={runs} (median)", order, MetricOrder, results.ToDictionary(p => p.Key, p => p.Value.Select(x => x.Metrics).ToList()));
        }

        static PkResult Dispatch(IPkTable boxed, int n, string[] names, ulong seed)
        {
            // 値型で具象化するため、型ごとに分岐する.
            switch (boxed)
            {
                case CurrentClassW w: return Run(w, n, names, seed);
                case CurrentStructW w: return Run(w, n, names, seed);
                case RankW w: return Run(w, n, names, seed);
                case BlockW w: return Run(w, n, names, seed);
                case SegmentedRankW w: return Run(w, n, names, seed);
                case SlotMapW w: return Run(w, n, names, seed);
                case DictionaryW w: return Run(w, n, names, seed);
                default: throw new NotSupportedException();
            }
        }

        static Dictionary<string, double> DispatchFifo(IPkTable boxed, int n, string[] names, ulong seed)
        {
            switch (boxed)
            {
                case CurrentClassW w: return Fifo(w, n, names, seed);
                case CurrentStructW w: return Fifo(w, n, names, seed);
                case RankW w: return Fifo(w, n, names, seed);
                case BlockW w: return Fifo(w, n, names, seed);
                case SegmentedRankW w: return Fifo(w, n, names, seed);
                case SlotMapW w: return Fifo(w, n, names, seed);
                case DictionaryW w: return Fifo(w, n, names, seed);
                default: throw new NotSupportedException();
            }
        }

        static PkResult Run<T>(T t, int n, string[] names, ulong seed) where T : struct, IPkTable
        {
            var res = new PkResult { Name = t.Name };
            var m = res.Metrics;
            var rng = new XorShift(seed * 0x9E3779B97F4A7C15UL);
            var rows = MakeRows(n, names, ref rng);
            var pool = new int[n];
            var probes = n * 2;
            var idx = new int[probes];
            for (var i = 0; i < probes; i++) idx[i] = rng.Next(n);
            var lat = new long[n];
            Collect();

            // Insert + メモリ
            var mem0 = GC.GetTotalMemory(true);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < n; i++) t.Insert(rows[i], out pool[i]);
            sw.Stop();
            m["insert ns"] = Ns(sw, n);
            m["mem B/row"] = (double)(GC.GetTotalMemory(true) - mem0) / n;

            // Find (乱順)
            long sink = 0;
            sw.Restart();
            for (var i = 0; i < probes; i++) sink += t.GetScore(pool[idx[i]]);
            sw.Stop();
            m["find ns"] = Ns(sw, probes);

            sw.Restart();
            for (var i = 0; i < probes; i++) t.UpdateScore(pool[idx[i]], i & 1023);
            sw.Stop();
            m["update ns"] = Ns(sw, probes);

            // 乱順の Remove + Insert (削除累計が件数を大きく超える状態を作る)
            var churn = n * 4;
            sw.Restart();
            for (var i = 0; i < churn; i++)
            {
                var p = rng.Next(n);
                t.Remove(pool[p]);
                t.Insert(rows[p], out pool[p]);
            }

            sw.Stop();
            m["churn ns"] = Ns(sw, churn);

            // 1 操作ごとの遅延 (コンパクションのスパイク)
            for (var i = 0; i < lat.Length; i++)
            {
                var p = rng.Next(n);
                var t0 = Stopwatch.GetTimestamp();
                t.Remove(pool[p]);
                t.Insert(rows[p], out pool[p]);
                lat[i] = Stopwatch.GetTimestamp() - t0;
            }

            Array.Sort(lat);
            m["churn p99.9 ns"] = lat[(int)(lat.Length * 0.999)] * 1e9 / Stopwatch.Frequency;
            m["churn max us"] = lat[lat.Length - 1] * 1e6 / Stopwatch.Frequency;

            sw.Restart();
            for (var i = 0; i < probes; i++) sink += t.GetScore(pool[idx[i]]);
            sw.Stop();
            m["find(churned) ns"] = Ns(sw, probes);

            var alloc0 = Allocated();
            sw.Restart();
            const int allReps = 5;
            for (var i = 0; i < allReps; i++) sink += t.SumAll();
            sw.Stop();
            m["all ns/row"] = Ns(sw, (long)allReps * t.Count);

            var rangeQueries = 100000;
            if (t.SupportsRange)
            {
                // Key は churn 後に後方へずれているため、生存 Key から窓の開始位置を選ぶ.
                sw.Restart();
                for (var i = 0; i < rangeQueries; i++)
                {
                    var k = pool[idx[i % probes]];
                    sink += t.SumRange(k, k + 63);
                }

                sw.Stop();
                m["range64 ns"] = Ns(sw, rangeQueries);
            }
            else
            {
                m["range64 ns"] = double.NaN;
            }

            var alloc1 = Allocated();
            m["alloc B/query"] = alloc0 < 0 ? double.NaN : (double)(alloc1 - alloc0) / (allReps + (t.SupportsRange ? rangeQueries : 0));
            m["mem(churned) B/row"] = (double)(GC.GetTotalMemory(true) - mem0) / n;

            GC.KeepAlive(rows);
            t = default;
            Collect();

            if (sink == 42) Console.WriteLine();
            return res;
        }

        static Dictionary<string, double> Fifo<T>(T t, int n, string[] names, ulong seed) where T : struct, IPkTable
        {
            var m = new Dictionary<string, double>();
            var rng = new XorShift(seed * 0x9E3779B97F4A7C15UL);
            var rows = MakeRows(n, names, ref rng);

            // 生存 Key をリングバッファで持ち、最も古いものから消す.
            var ring = new int[n];
            for (var i = 0; i < n; i++) t.Insert(rows[i], out ring[i]);
            Collect();

            var ops = n * 4;
            var lat = new long[ops];
            var headIndex = 0;
            for (var i = 0; i < ops; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                t.Remove(ring[headIndex]);
                t.Insert(rows[headIndex], out ring[headIndex]);
                lat[i] = Stopwatch.GetTimestamp() - t0;
                headIndex = headIndex + 1 == n ? 0 : headIndex + 1;
            }

            m["fifo ns"] = lat.Average() * 1e9 / Stopwatch.Frequency;
            m["fifo max us"] = lat.Max() * 1e6 / Stopwatch.Frequency;

            var probes = n * 2;
            var idx = new int[probes];
            for (var i = 0; i < probes; i++) idx[i] = rng.Next(n);
            long sink = 0;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < probes; i++) sink += t.GetScore(ring[idx[i]]);
            sw.Stop();
            m["find(fifo) ns"] = Ns(sw, probes);
            if (sink == 42) Console.WriteLine();
            return m;
        }

        // ---- 正しさの検証 ----

        public static void Verify()
        {
            foreach (var f in Factories())
            {
                var t = f();
                VerifyOne(t, 1, maxAlive: 3000, ops: 300000);
                VerifyOne(f(), 2, maxAlive: 70, ops: 100000);
                Console.WriteLine($"verify OK: {t.Name}");
            }
        }

        static void VerifyOne(IPkTable t, ulong seed, int maxAlive, int ops)
        {
            var rng = new XorShift(seed);
            var reference = new SortedDictionary<int, int>();
            var alive = new List<int>();
            var fifo = new Queue<int>();

            for (var i = 0; i < ops; i++)
            {
                var op = rng.Next(100);
                var phaseFifo = (i / 20000) % 2 == 1;
                if (op < 50 && alive.Count < maxAlive)
                {
                    var row = new Row { Score = rng.Next(1000000), Name = "n" };
                    if (!t.Insert(row, out var key)) throw new Exception("insert failed");
                    if (reference.ContainsKey(key)) throw new Exception($"duplicate key {key}");
                    reference[key] = row.Score;
                    alive.Add(key);
                    fifo.Enqueue(key);
                }
                else if (op < 90 && alive.Count > 0)
                {
                    int key;
                    if (phaseFifo)
                    {
                        do key = fifo.Dequeue(); while (!reference.ContainsKey(key));
                        alive.Remove(key);
                    }
                    else
                    {
                        var p = rng.Next(alive.Count);
                        key = alive[p];
                        alive[p] = alive[alive.Count - 1];
                        alive.RemoveAt(alive.Count - 1);
                    }

                    if (!t.Remove(key)) throw new Exception($"remove failed {key}");
                    reference.Remove(key);
                    if (t.Remove(key)) throw new Exception($"double remove succeeded {key}");
                }
                else if (op < 95 && alive.Count > 0)
                {
                    var key = alive[rng.Next(alive.Count)];
                    var score = rng.Next(1000000);
                    if (!t.UpdateScore(key, score)) throw new Exception("update failed");
                    reference[key] = score;
                }
                else if (alive.Count > 0)
                {
                    var key = alive[rng.Next(alive.Count)];
                    if (t.GetScore(key) != reference[key]) throw new Exception($"find mismatch {key}");
                }

                if (t.Count != reference.Count) throw new Exception($"count mismatch {t.Count} != {reference.Count} at {i}");

                if (i % 997 == 0)
                {
                    long sum = 0;
                    foreach (var v in reference.Values) sum += v;
                    if (t.SumAll() != sum) throw new Exception($"sumall mismatch at {i}");

                    if (t.SupportsRange && reference.Count > 0)
                    {
                        var keys = reference.Keys.ToArray();
                        for (var q = 0; q < 20; q++)
                        {
                            var a = keys[rng.Next(keys.Length)] + rng.Next(130) - 65;
                            var b = a + rng.Next(300);
                            long expect = 0;
                            foreach (var p in reference)
                            {
                                if (p.Key >= a && p.Key <= b) expect += p.Value;
                            }

                            var actual = t.SumRange(a, b);
                            if (actual != expect) throw new Exception($"range mismatch [{a},{b}] {actual} != {expect} at {i}");
                        }
                    }
                }

                // 存在しない Key / 削除済み Key
                if (i % 101 == 0)
                {
                    var bogus = rng.Next(int.MaxValue);
                    if (!reference.ContainsKey(bogus) && t.GetScore(bogus) != -1) throw new Exception($"bogus key found {bogus}");
                }
            }
        }

        // ---- 補助 ----

        public static string[] Names(int count)
        {
            var names = new string[count];
            for (var i = 0; i < count; i++) names[i] = "name_" + i.ToString("D5");
            return names;
        }

        static Row[] MakeRows(int n, string[] names, ref XorShift rng)
        {
            var rows = new Row[n];
            for (var i = 0; i < n; i++)
            {
                rows[i] = new Row
                {
                    Score = rng.Next(1000000),
                    Category = rng.Next(16),
                    Name = names[rng.Next(names.Length)],
                    Description = names[rng.Next(names.Length)],
                };
            }

            return rows;
        }

        static double Ns(Stopwatch sw, long ops)
        {
            return sw.Elapsed.TotalMilliseconds * 1e6 / ops;
        }

        static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        public static long Allocated()
        {
#if NET
            return GC.GetAllocatedBytesForCurrentThread();
#else
            return -1;
#endif
        }
    }
}
