using System;
using System.Collections.Generic;

namespace OrdoMemory.Benchmark
{
    public static class Verify
    {
        const int N = 2000;
        const int Steps = 4000;

        public static bool Run()
        {
            var expected = Trace(Target.Dictionary);
            var ok = true;
            foreach (Target target in Enum.GetValues(typeof(Target)))
            {
                if (target == Target.Dictionary) continue;
                var actual = Trace(target);
                var mismatch = FirstMismatch(expected, actual);
                Console.WriteLine(mismatch < 0 ? $"OK    {target}" : $"FAIL  {target} (step {mismatch}: expected {Get(expected, mismatch)}, actual {Get(actual, mismatch)})");
                ok &= mismatch < 0;
            }

            return ok;
        }

        static List<long> Trace(Target target)
        {
            var trace = new List<long>();
            var rng = new Rng(7);
            var rows = Dataset.Create(N, 3);
            var keys = new long[N];
            var alive = new bool[N];
            var categories = Dataset.CategoryCount(N);

            using var store = StoreFactory.Create(target);
            store.BulkLoad(rows, keys);
            for (var i = 0; i < N; i++) alive[i] = true;
            trace.Add(store.SumAll());

            for (var step = 0; step < Steps; step++)
            {
                var i = rng.Next(N);
                switch (rng.Next(10))
                {
                    case 0:
                        trace.Add(Hash(store.TryFind(keys[i], out var a), a));
                        break;
                    case 1:
                        trace.Add(Hash(store.TryFindByCode(rng.Next(N + 10), out var b), b));
                        break;
                    case 2:
                        trace.Add(Hash(store.TryFindByName(Dataset.NameOf(rng.Next(N + 10)), out var c), c));
                        break;
                    case 3:
                        trace.Add(store.SumByCategory(rng.Next(categories)));
                        break;
                    case 4:
                        var min = rng.Next(N);
                        trace.Add(store.SumRangeByCode(min, min + rng.Next(100)));
                        break;
                    case 5:
                        trace.Add(store.UpdateValue(keys[i], rng.Next(1000)) ? 1 : 0);
                        break;
                    case 6:
                        trace.Add(store.UpdateCategory(keys[i], rng.Next(categories)) ? 1 : 0);
                        break;
                    case 7:
                        if (alive[i])
                        {
                            trace.Add(store.Remove(keys[i]) ? 1 : 0);
                            alive[i] = false;
                        }
                        else
                        {
                            keys[i] = store.Insert(rows[i]);
                            alive[i] = true;
                        }

                        trace.Add(store.TryFind(keys[i], out _) ? 1 : 0);
                        break;
                    case 8:
                        var batch = new List<long>();
                        var values = new List<int>();
                        for (var j = 0; j < 20; j++)
                        {
                            var k = (i + j * 97) % N;
                            if (!alive[k]) continue;
                            batch.Add(keys[k]);
                            values.Add(rng.Next(1000));
                        }

                        store.UpdateValues(batch.ToArray(), values.ToArray());
                        trace.Add(store.SumAll());
                        break;
                    default:
                        trace.Add(store.SumAll());
                        break;
                }
            }

            return trace;
        }

        static long Hash(bool found, in ItemData row)
        {
            if (!found) return -1;
            return HashCode.Combine(row.Code, row.Category, row.Value, row.Name, row.Description) & 0x7FFFFFFF;
        }

        static int FirstMismatch(List<long> expected, List<long> actual)
        {
            var n = Math.Max(expected.Count, actual.Count);
            for (var i = 0; i < n; i++)
            {
                if (i >= expected.Count || i >= actual.Count || expected[i] != actual[i]) return i;
            }

            return -1;
        }

        static string Get(List<long> list, int i) => i < list.Count ? list[i].ToString() : "(none)";
    }
}
