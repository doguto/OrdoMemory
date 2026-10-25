using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OrdoMemory.Bench.Sec
{
    // ---- 値型ラッパー (PkBench と同じ理由) ----

    public struct SortedSetW<K> : ISecIndex<K>
    {
        public SortedSetIndex<K> T;
        public string Name => T.Name;
        public bool SupportsRange => T.SupportsRange;
        public bool Insert(K key, int pk) => T.Insert(key, pk);
        public bool Remove(K key, int pk) => T.Remove(key, pk);
        public long SumEqual(K key, out int count) => T.SumEqual(key, out count);
        public long SumRange(K min, K max, out int count) => T.SumRange(min, max, out count);
        public bool Bulk(K[] keys, int[] pks, int n) => T.Bulk(keys, pks, n);
    }

    public struct ClassW<K> : ISecIndex<K>
    {
        public ClassBTree<K> T;
        public string Name => T.Name;
        public bool SupportsRange => T.SupportsRange;
        public bool Insert(K key, int pk) => T.Insert(key, pk);
        public bool Remove(K key, int pk) => T.Remove(key, pk);
        public long SumEqual(K key, out int count) => T.SumEqual(key, out count);
        public long SumRange(K min, K max, out int count) => T.SumRange(min, max, out count);
        public bool Bulk(K[] keys, int[] pks, int n) => T.Bulk(keys, pks, n);
    }

    public struct ArenaW<K, TT> : ISecIndex<K> where TT : struct, IKeyTraits<K>
    {
        public ArenaBTree<K, TT> T;
        public string Name => T.Name;
        public bool SupportsRange => T.SupportsRange;
        public bool Insert(K key, int pk) => T.Insert(key, pk);
        public bool Remove(K key, int pk) => T.Remove(key, pk);
        public long SumEqual(K key, out int count) => T.SumEqual(key, out count);
        public long SumRange(K min, K max, out int count) => T.SumRange(min, max, out count);
        public bool Bulk(K[] keys, int[] pks, int n) => T.Bulk(keys, pks, n);
    }

    public struct HashW<K> : ISecIndex<K>
    {
        public HashPostingIndex<K> T;
        public string Name => T.Name;
        public bool SupportsRange => T.SupportsRange;
        public bool Insert(K key, int pk) => T.Insert(key, pk);
        public bool Remove(K key, int pk) => T.Remove(key, pk);
        public long SumEqual(K key, out int count) => T.SumEqual(key, out count);
        public long SumRange(K min, K max, out int count) => T.SumRange(min, max, out count);
        public bool Bulk(K[] keys, int[] pks, int n) => T.Bulk(keys, pks, n);
    }

    public struct BitmapW<K> : ISecIndex<K>
    {
        public BitmapIndex<K> T;
        public string Name => T.Name;
        public bool SupportsRange => T.SupportsRange;
        public bool Insert(K key, int pk) => T.Insert(key, pk);
        public bool Remove(K key, int pk) => T.Remove(key, pk);
        public long SumEqual(K key, out int count) => T.SumEqual(key, out count);
        public long SumRange(K min, K max, out int count) => T.SumRange(min, max, out count);
        public bool Bulk(K[] keys, int[] pks, int n) => T.Bulk(keys, pks, n);
    }

    public static class SecBench
    {
        public static readonly string[] MetricOrder =
        {
            "insert ns", "mem B/entry", "equal ns", "equal ns/hit", "range ns", "range ns/hit", "churn ns", "churn max us", "bulk ns/entry", "alloc B/query",
        };

        // ---- データセット ----

        public sealed class Dataset<K>
        {
            public string Name;
            public K[] Keys;             // 挿入する N 件のキー
            public K[] Distinct;         // 整列済みの値の一覧
            public int RangeWidth;       // 範囲検索で含める値の数
            public bool LowCard;
        }

        public static Dataset<int> IntHigh(int n, ulong seed)
        {
            var rng = new XorShift(seed);
            var card = Math.Max(1, n / 4);
            var keys = new int[n];
            for (var i = 0; i < n; i++) keys[i] = rng.Next(card);
            return new Dataset<int> { Name = $"int key, ~{card:N0} distinct", Keys = keys, Distinct = Enumerable.Range(0, card).ToArray(), RangeWidth = 16 };
        }

        public static Dataset<int> IntLow(int n, ulong seed)
        {
            var rng = new XorShift(seed);
            var keys = new int[n];
            for (var i = 0; i < n; i++) keys[i] = rng.Next(16);
            return new Dataset<int> { Name = "int key, 16 distinct (low-card)", Keys = keys, Distinct = Enumerable.Range(0, 16).ToArray(), RangeWidth = 2, LowCard = true };
        }

        public static Dataset<string> Strings(int n, ulong seed, bool commonPrefix)
        {
            var rng = new XorShift(seed);
            var card = Math.Max(1, n / 4);
            var pool = new HashSet<string>(StringComparer.Ordinal);
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            while (pool.Count < card)
            {
                string s;
                if (commonPrefix)
                {
                    s = "Item_" + rng.Next(card * 4).ToString("D7");
                }
                else
                {
                    var len = 8 + rng.Next(9);
                    var c = new char[len];
                    for (var i = 0; i < len; i++) c[i] = chars[rng.Next(chars.Length)];
                    s = new string(c);
                }

                pool.Add(s);
            }

            var distinct = pool.ToArray();
            Array.Sort(distinct, StringComparer.Ordinal);
            var keys = new string[n];
            for (var i = 0; i < n; i++) keys[i] = distinct[rng.Next(distinct.Length)];
            return new Dataset<string>
            {
                Name = commonPrefix ? $"string key \"Item_0000000\" (common prefix), ~{card:N0} distinct" : $"string key random 8-16 chars, ~{card:N0} distinct",
                Keys = keys,
                Distinct = distinct,
                RangeWidth = 16,
            };
        }

        // ---- 実行 ----

        public static void RunAll(int n, int runs, string filter)
        {
            RunSet(IntHigh(n, 11), n, runs, filter, IntFactories(false));
            RunSet(IntLow(n, 12), n, runs, filter, IntFactories(true));
            RunSet(Strings(n, 13, false), n, runs, filter, StringFactories());
            RunSet(Strings(n, 14, true), n, runs, filter, StringFactories());
        }

        static List<Func<Dataset<int>, int, ulong, (string, Dictionary<string, double>)>> IntFactories(bool lowCard)
        {
            var list = new List<Func<Dataset<int>, int, ulong, (string, Dictionary<string, double>)>>
            {
                (d, n, s) => Run(new SortedSetW<int> { T = new SortedSetIndex<int>() }, d, n, s),
                (d, n, s) => Run(new ClassW<int> { T = new ClassBTree<int>(32) }, d, n, s),
                (d, n, s) => Run(new ArenaW<int, IntKeyTraits> { T = new ArenaBTree<int, IntKeyTraits>(16) }, d, n, s),
                (d, n, s) => Run(new ArenaW<int, IntKeyTraits> { T = new ArenaBTree<int, IntKeyTraits>(32) }, d, n, s),
                (d, n, s) => Run(new ArenaW<int, IntKeyTraits> { T = new ArenaBTree<int, IntKeyTraits>(64) }, d, n, s),
                (d, n, s) => Run(new ArenaW<int, IntKeyTraits> { T = new ArenaBTree<int, IntKeyTraits>(128) }, d, n, s),
                (d, n, s) => Run(new HashW<int> { T = new HashPostingIndex<int>() }, d, n, s),
            };
            if (lowCard) list.Add((d, n, s) => Run(new BitmapW<int> { T = new BitmapIndex<int>() }, d, n, s));
            return list;
        }

        static List<Func<Dataset<string>, int, ulong, (string, Dictionary<string, double>)>> StringFactories()
        {
            return new List<Func<Dataset<string>, int, ulong, (string, Dictionary<string, double>)>>
            {
                (d, n, s) => Run(new SortedSetW<string> { T = new SortedSetIndex<string>() }, d, n, s),
                (d, n, s) => Run(new ClassW<string> { T = new ClassBTree<string>(32) }, d, n, s),
                (d, n, s) => Run(new ArenaW<string, StringKeyTraits> { T = new ArenaBTree<string, StringKeyTraits>(16) }, d, n, s),
                (d, n, s) => Run(new ArenaW<string, StringKeyTraits> { T = new ArenaBTree<string, StringKeyTraits>(32) }, d, n, s),
                (d, n, s) => Run(new ArenaW<string, StringKeyTraits> { T = new ArenaBTree<string, StringKeyTraits>(64) }, d, n, s),
                (d, n, s) => Run(new HashW<string> { T = new HashPostingIndex<string>() }, d, n, s),
            };
        }

        static void RunSet<K>(Dataset<K> data, int n, int runs, string filter, List<Func<Dataset<K>, int, ulong, (string, Dictionary<string, double>)>> factories)
        {
            var results = new Dictionary<string, List<Dictionary<string, double>>>();
            var order = new List<string>();

            // ウォームアップ.
            var small = new Dataset<K> { Name = data.Name, Keys = data.Keys.Take(Math.Min(n, 20000)).ToArray(), Distinct = data.Distinct, RangeWidth = data.RangeWidth, LowCard = data.LowCard };
            foreach (var f in factories) f(small, small.Keys.Length, 1);

            for (var r = 0; r < runs; r++)
            {
                foreach (var f in factories)
                {
                    var (name, m) = f(data, n, (ulong)(r + 1));
                    if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!results.ContainsKey(name))
                    {
                        results[name] = new List<Dictionary<string, double>>();
                        order.Add(name);
                    }

                    results[name].Add(m);
                    Console.Error.WriteLine($"  [{data.Name}] run {r + 1}/{runs} {name} done");
                }
            }

            Report.PrintTable($"Secondary index: {data.Name}  N={n:N0}  runs={runs} (median)", order, MetricOrder, results);
        }

        static (string, Dictionary<string, double>) Run<W, K>(W t, Dataset<K> data, int n, ulong seed) where W : struct, ISecIndex<K>
        {
            var m = new Dictionary<string, double>();
            var rng = new XorShift(seed * 0x9E3779B97F4A7C15UL);
            var keys = data.Keys;
            var curKey = new K[n];
            var curPk = new int[n];
            var queries = data.LowCard ? 400 : 200000;
            var qIdx = new int[queries];
            for (var i = 0; i < queries; i++) qIdx[i] = rng.Next(n);
            var churn = n;
            var lat = new long[churn];
            Collect();

            var mem0 = GC.GetTotalMemory(true);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
            {
                t.Insert(keys[i], i);
                curKey[i] = keys[i];
                curPk[i] = i;
            }

            sw.Stop();
            m["insert ns"] = Ns(sw, n);
            m["mem B/entry"] = (double)(GC.GetTotalMemory(true) - mem0) / n;

            long sink = 0;
            long hits = 0;
            var alloc0 = PkBenchAlloc();
            sw.Restart();
            for (var i = 0; i < queries; i++)
            {
                sink += t.SumEqual(curKey[qIdx[i]], out var c);
                hits += c;
            }

            sw.Stop();
            m["equal ns"] = Ns(sw, queries);
            m["equal ns/hit"] = Ns(sw, Math.Max(1, hits));

            if (t.SupportsRange)
            {
                hits = 0;
                var distinct = data.Distinct;
                sw.Restart();
                for (var i = 0; i < queries; i++)
                {
                    var lo = qIdx[i] % Math.Max(1, distinct.Length - data.RangeWidth + 1);
                    sink += t.SumRange(distinct[lo], distinct[Math.Min(distinct.Length - 1, lo + data.RangeWidth - 1)], out var c);
                    hits += c;
                }

                sw.Stop();
                m["range ns"] = Ns(sw, queries);
                m["range ns/hit"] = Ns(sw, Math.Max(1, hits));
            }
            else
            {
                m["range ns"] = double.NaN;
                m["range ns/hit"] = double.NaN;
            }

            var alloc1 = PkBenchAlloc();
            m["alloc B/query"] = alloc0 < 0 ? double.NaN : (double)(alloc1 - alloc0) / (queries * (t.SupportsRange ? 2 : 1));

            // 乱順の削除 + 新しい PK (単調増加) での挿入
            var nextPk = n;
            for (var i = 0; i < churn; i++)
            {
                var p = rng.Next(n);
                var newKey = keys[rng.Next(n)];
                var t0 = Stopwatch.GetTimestamp();
                t.Remove(curKey[p], curPk[p]);
                t.Insert(newKey, nextPk);
                lat[i] = Stopwatch.GetTimestamp() - t0;
                curKey[p] = newKey;
                curPk[p] = nextPk++;
            }

            m["churn ns"] = lat.Average() * 1e9 / Stopwatch.Frequency;
            m["churn max us"] = lat.Max() * 1e6 / Stopwatch.Frequency;

            var name = t.Name;
            t = default;
            Collect();

            // 一括構築
            m["bulk ns/entry"] = double.NaN;
            var pks = new int[n];
            for (var i = 0; i < n; i++) pks[i] = i;
            var fresh = Fresh<W, K>(name);
            if (fresh.HasValue)
            {
                var f = fresh.Value;
                sw.Restart();
                var ok = f.Bulk(keys, pks, n);
                sw.Stop();
                if (ok) m["bulk ns/entry"] = Ns(sw, n);
            }

            if (sink == 42) Console.WriteLine();
            return (name, m);
        }

        // 名前から同じ構成の空のインスタンスを作る (一括構築用).
        static W? Fresh<W, K>(string name) where W : struct, ISecIndex<K>
        {
            object o = null;
            var b = name.Contains("B=") ? int.Parse(name.Substring(name.IndexOf("B=") + 2)) : 32;
            if (typeof(W) == typeof(ArenaW<int, IntKeyTraits>)) o = new ArenaW<int, IntKeyTraits> { T = new ArenaBTree<int, IntKeyTraits>(b) };
            if (typeof(W) == typeof(ArenaW<string, StringKeyTraits>)) o = new ArenaW<string, StringKeyTraits> { T = new ArenaBTree<string, StringKeyTraits>(b) };
            return o == null ? (W?)null : (W)o;
        }

        // ---- 正しさの検証 ----

        public static void Verify()
        {
            var intIndexes = new Func<ISecIndex<int>>[]
            {
                () => new SortedSetIndex<int>(), () => new ClassBTree<int>(4), () => new ClassBTree<int>(32),
                () => new ArenaBTree<int, IntKeyTraits>(4), () => new ArenaBTree<int, IntKeyTraits>(32), () => new BitmapIndex<int>(),
                () => new HashPostingIndex<int>(),
            };
            var strIndexes = new Func<ISecIndex<string>>[]
            {
                () => new SortedSetIndex<string>(), () => new ClassBTree<string>(4), () => new ClassBTree<string>(32),
                () => new ArenaBTree<string, StringKeyTraits>(4), () => new ArenaBTree<string, StringKeyTraits>(32), () => new BitmapIndex<string>(),
                () => new HashPostingIndex<string>(),
            };

            foreach (var card in new[] { 8, 2000 })
            {
                var ik = Enumerable.Range(-card / 2, card).ToArray();
                foreach (var f in intIndexes) VerifyOne(f, ik, Comparer<int>.Default, card);
                var sk = Enumerable.Range(0, card).Select(i => i % 3 == 0 ? "Item_" + i.ToString("D5") : (i % 3 == 1 ? "x" + i : "あ" + (char)('a' + i % 26) + i)).ToArray();
                foreach (var f in strIndexes) VerifyOne(f, sk, StringComparer.Ordinal, card);
            }

            // 一括構築
            foreach (var bsize in new[] { 4, 32 })
            {
                VerifyBulk(() => new ArenaBTree<int, IntKeyTraits>(bsize), Enumerable.Range(0, 50).Select(i => i * 7 - 100).ToArray(), Comparer<int>.Default);
                VerifyBulk(() => new ArenaBTree<string, StringKeyTraits>(bsize), Enumerable.Range(0, 50).Select(i => "Item_" + (i * 7)).ToArray(), StringComparer.Ordinal);
            }
        }

        static void VerifyOne<K>(Func<ISecIndex<K>> factory, K[] keyPool, IComparer<K> cmp, int card)
        {
            var idx = factory();
            var rng = new XorShift((ulong)card * 31 + 7);
            var reference = new SortedSet<(K, int)>(new EntryComparer<K>(cmp));
            var live = new List<(K, int)>();
            var nextPk = 0;
            var sorted = keyPool.OrderBy(k => k, cmp).ToArray();

            for (var i = 0; i < 60000; i++)
            {
                var phase = (i / 15000) % 2;
                var op = rng.Next(100);
                if (op < (phase == 0 ? 60 : 35))
                {
                    var key = keyPool[rng.Next(keyPool.Length)];
                    // 時々、古い PK で挿入する (単調でない挿入).
                    var pk = rng.Next(10) == 0 ? rng.Next(nextPk + 1) : nextPk++;
                    var expect = reference.Add((key, pk));
                    if (idx.Insert(key, pk) != expect) throw new Exception($"{idx.Name}: insert mismatch");
                    if (expect) live.Add((key, pk));
                }
                else if (live.Count > 0)
                {
                    var p = rng.Next(live.Count);
                    var e = live[p];
                    live[p] = live[live.Count - 1];
                    live.RemoveAt(live.Count - 1);
                    reference.Remove(e);
                    if (!idx.Remove(e.Item1, e.Item2)) throw new Exception($"{idx.Name}: remove failed");
                    if (idx.Remove(e.Item1, e.Item2)) throw new Exception($"{idx.Name}: double remove");
                }

                if (i % 211 == 0)
                {
                    var key = keyPool[rng.Next(keyPool.Length)];
                    var es = reference.GetViewBetween((key, int.MinValue), (key, int.MaxValue));
                    long expect = es.Sum(x => (long)x.Item2);
                    if (idx.SumEqual(key, out var c) != expect || c != es.Count) throw new Exception($"{idx.Name}: equal mismatch");

                    if (idx.SupportsRange)
                    {
                        var a = rng.Next(sorted.Length);
                        var bIdx = Math.Min(sorted.Length - 1, a + rng.Next(20));
                        var rs = reference.GetViewBetween((sorted[a], int.MinValue), (sorted[bIdx], int.MaxValue));
                        long rexpect = rs.Sum(x => (long)x.Item2);
                        if (idx.SumRange(sorted[a], sorted[bIdx], out var rc) != rexpect || rc != rs.Count) throw new Exception($"{idx.Name}: range mismatch");
                    }
                }
            }

            Console.WriteLine($"verify OK: {idx.Name} <{typeof(K).Name}> card={card}");
        }

        static void VerifyBulk<K>(Func<ISecIndex<K>> factory, K[] distinct, IComparer<K> cmp)
        {
            var rng = new XorShift(5);
            const int n = 5000;
            var keys = new K[n];
            var pks = new int[n];
            for (var i = 0; i < n; i++)
            {
                keys[i] = distinct[rng.Next(distinct.Length)];
                pks[i] = i;
            }

            var idx = factory();
            idx.Bulk(keys, pks, n);
            var reference = new SortedSet<(K, int)>(new EntryComparer<K>(cmp));
            for (var i = 0; i < n; i++) reference.Add((keys[i], pks[i]));

            // 一括構築の後に挿入 / 削除も行う.
            for (var i = 0; i < 3000; i++)
            {
                var p = rng.Next(n);
                if (reference.Remove((keys[p], pks[p])) != idx.Remove(keys[p], pks[p])) throw new Exception($"{idx.Name}: bulk remove mismatch");
                var k = distinct[rng.Next(distinct.Length)];
                var pk = n + i;
                reference.Add((k, pk));
                idx.Insert(k, pk);
            }

            var sorted = distinct.OrderBy(x => x, cmp).ToArray();
            foreach (var key in sorted)
            {
                var es = reference.GetViewBetween((key, int.MinValue), (key, int.MaxValue));
                if (idx.SumEqual(key, out var c) != es.Sum(x => (long)x.Item2) || c != es.Count) throw new Exception($"{idx.Name}: bulk equal mismatch");
            }

            if (idx.SumRange(sorted[0], sorted[sorted.Length - 1], out var all) != reference.Sum(x => (long)x.Item2) || all != reference.Count) throw new Exception($"{idx.Name}: bulk range mismatch");
            Console.WriteLine($"verify OK: {idx.Name} <{typeof(K).Name}> bulk");
        }

        static long PkBenchAlloc() => Pk.PkBench.Allocated();

        static double Ns(Stopwatch sw, long ops) => sw.Elapsed.TotalMilliseconds * 1e6 / ops;

        static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
