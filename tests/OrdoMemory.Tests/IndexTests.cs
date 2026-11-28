using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace OrdoMemory.Tests
{
    public class IndexTests
    {
        sealed class Model<TKey>
        {
            readonly IComparer<TKey> comparer;
            public readonly List<(TKey Key, ulong PrimaryKey)> Entries = new();

            public Model(IComparer<TKey> comparer)
            {
                this.comparer = comparer;
            }

            int Compare((TKey Key, ulong PrimaryKey) a, (TKey Key, ulong PrimaryKey) b)
            {
                var c = comparer.Compare(a.Key, b.Key);
                return c != 0 ? c : a.PrimaryKey.CompareTo(b.PrimaryKey);
            }

            public void Add(TKey key, ulong primaryKey)
            {
                Entries.Add((key, primaryKey));
                Entries.Sort(Compare);
            }

            public IEnumerable<ulong> Range(TKey min, TKey max)
            {
                return Entries.Where(e => comparer.Compare(e.Key, min) >= 0 && comparer.Compare(e.Key, max) <= 0).Select(e => e.PrimaryKey);
            }

            public IEnumerable<ulong> Range(bool hasMin, TKey min, bool minInclusive, bool hasMax, TKey max, bool maxInclusive, bool descending)
            {
                var q = Entries.Where(e =>
                {
                    if (hasMin)
                    {
                        var c = comparer.Compare(e.Key, min);
                        if (c < 0 || (c == 0 && !minInclusive)) return false;
                    }

                    if (hasMax)
                    {
                        var c = comparer.Compare(e.Key, max);
                        if (c > 0 || (c == 0 && !maxInclusive)) return false;
                    }

                    return true;
                }).Select(e => e.PrimaryKey);
                return descending ? q.Reverse() : q;
            }
        }

        static List<ulong> Collect<TKey, TKT, TPrimaryKey, TPT>(OrderedIndex<TKey, TKT, TPrimaryKey, TPT>.RangeEnumerator e)
            where TKT : struct, IOrderedKeyTraits<TKey>
            where TPrimaryKey : unmanaged
            where TPT : struct, IPrimaryKeyTraits<TPrimaryKey>
        {
            var list = new List<ulong>();
            while (e.MoveNext()) list.Add(default(TPT).ToUInt64(e.Current));
            return list;
        }

        static void RunRandom<TKey, TKT, TPrimaryKey, TPT>(bool unique, Func<Random, TKey> nextKey, IComparer<TKey> comparer, int seed)
            where TKT : struct, IOrderedKeyTraits<TKey>
            where TPrimaryKey : unmanaged
            where TPT : struct, IPrimaryKeyTraits<TPrimaryKey>
        {
            var rnd = new Random(seed);
            var index = new OrderedIndex<TKey, TKT, TPrimaryKey, TPT>(unique, 4);
            var model = new Model<TKey>(comparer);
            ulong nextPrimaryKey = 1;
            var traits = default(TPT);

            for (var step = 0; step < 4000; step++)
            {
                var op = rnd.Next(10);
                if (op < 5 || model.Entries.Count == 0)
                {
                    var key = nextKey(rnd);
                    var primaryKey = nextPrimaryKey++;
                    var exists = unique && model.Entries.Any(e => comparer.Compare(e.Key, key) == 0);
                    Assert.Equal(!exists, index.Insert(key, traits.FromUInt64(primaryKey)));
                    if (!exists) model.Add(key, primaryKey);
                }
                else if (op < 8)
                {
                    var victim = model.Entries[rnd.Next(model.Entries.Count)];
                    Assert.True(index.Remove(victim.Key, traits.FromUInt64(victim.PrimaryKey)));
                    Assert.False(index.Remove(victim.Key, traits.FromUInt64(victim.PrimaryKey)));
                    model.Entries.Remove(victim);
                }
                else if (op < 9)
                {
                    var k = rnd.Next(1, 40);
                    var keys = new TKey[k];
                    var primaryKeys = new TPrimaryKey[k];
                    for (var i = 0; i < k; i++)
                    {
                        keys[i] = nextKey(rnd);
                        primaryKeys[i] = traits.FromUInt64(nextPrimaryKey++);
                    }

                    var conflict = unique && (keys.Distinct(new EqComparer<TKey>(comparer)).Count() != k
                                              || keys.Any(key => model.Entries.Any(e => comparer.Compare(e.Key, key) == 0)));
                    Assert.Equal(!conflict, index.AddRange(keys, primaryKeys));
                    if (!conflict)
                    {
                        for (var i = 0; i < k; i++)
                        {
                            model.Add(keys[i], traits.ToUInt64(primaryKeys[i]));
                        }
                    }
                }
                else if (step % 50 == 0)
                {
                    index.TrimExcess();
                }

                Assert.Equal(model.Entries.Count, index.Count);
                if (step % 20 == 0)
                {
                    var a = nextKey(rnd);
                    var b = nextKey(rnd);
                    Assert.Equal(model.Range(a, b), Collect(index.Range(a, b)));
                    Assert.Equal(model.Range(a, a), Collect(index.Equal(a)));
                    CheckRangeOptions(index, model, a, b);
                    if (model.Entries.Count > 0)
                    {
                        // 索引に実在するキーを境界にして、等しいキーの扱いを確かめる
                        var c = model.Entries[rnd.Next(model.Entries.Count)].Key;
                        var d = model.Entries[rnd.Next(model.Entries.Count)].Key;
                        CheckRangeOptions(index, model, c, d);
                        CheckRangeOptions(index, model, c, c);
                    }
                }
            }

            foreach (var e in model.Entries.ToList())
            {
                Assert.True(index.Remove(e.Key, traits.FromUInt64(e.PrimaryKey)));
                model.Entries.Remove(e);
            }

            Assert.Equal(0, index.Count);
            Assert.True(index.Insert(nextKey(rnd), traits.FromUInt64(nextPrimaryKey)));
        }

        static void CheckRangeOptions<TKey, TKT, TPrimaryKey, TPT>(OrderedIndex<TKey, TKT, TPrimaryKey, TPT> index, Model<TKey> model, TKey a, TKey b)
            where TKT : struct, IOrderedKeyTraits<TKey>
            where TPrimaryKey : unmanaged
            where TPT : struct, IPrimaryKeyTraits<TPrimaryKey>
        {
            foreach (var descending in new[] { false, true })
            {
                Assert.Equal(model.Range(false, default, false, false, default, false, descending), Collect(index.All(descending)));
                foreach (var inclusive in new[] { false, true })
                {
                    Assert.Equal(model.Range(true, a, inclusive, false, default, false, descending), Collect(index.GreaterThan(a, inclusive, descending)));
                    Assert.Equal(model.Range(false, default, false, true, a, inclusive, descending), Collect(index.LessThan(a, inclusive, descending)));
                    foreach (var maxInclusive in new[] { false, true })
                    {
                        Assert.Equal(model.Range(true, a, inclusive, true, b, maxInclusive, descending), Collect(index.Range(a, inclusive, b, maxInclusive, descending)));
                    }
                }
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RangeOptionsWithManyDuplicates(bool unique)
        {
            // 同じキーが複数の葉にまたがる場合と、PK が最大値の要素を含む場合
            var index = new OrderedIndex<int, Int32Key, uint, UInt32PrimaryKey>(unique, 4);
            var model = new Model<int>(Comparer<int>.Default);
            uint primaryKey = 1;
            for (var key = 0; key < 10; key++)
            {
                var n = unique ? 1 : key % 4 == 0 ? 13 : key % 3;
                for (var i = 0; i < n; i++)
                {
                    var pk = key == 4 && i == n - 1 ? uint.MaxValue : primaryKey++;
                    Assert.True(index.Insert(key * 2, pk));
                    model.Add(key * 2, pk);
                }
            }

            for (var a = -2; a <= 21; a++)
            {
                for (var b = -2; b <= 21; b++)
                {
                    CheckRangeOptions(index, model, a, b);
                }
            }
        }

        [Fact]
        public void RangeOptionsOnEmptyIndex()
        {
            var index = new OrderedIndex<string, StringKey, uint, UInt32PrimaryKey>(false, 4);
            var model = new Model<string>(StringComparer.Ordinal);
            CheckRangeOptions(index, model, "a", "b");
            CheckRangeOptions(index, model, null, null);
            Assert.False(default(OrderedIndex<string, StringKey, uint, UInt32PrimaryKey>.RangeEnumerator).MoveNext());
        }

        sealed class EqComparer<T> : IEqualityComparer<T>
        {
            readonly IComparer<T> comparer;

            public EqComparer(IComparer<T> comparer)
            {
                this.comparer = comparer;
            }

            public bool Equals(T x, T y)
            {
                return comparer.Compare(x, y) == 0;
            }

            public int GetHashCode(T obj)
            {
                return 0;
            }
        }

        static string RandomString(Random rnd)
        {
            var len = rnd.Next(0, 7);
            var chars = new char[len];
            for (var i = 0; i < len; i++)
            {
                chars[i] = (char)('a' + rnd.Next(3));
            }

            return rnd.Next(30) == 0 ? null : new string(chars);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void IntPacked(int seed)
        {
            RunRandom<int, Int32Key, uint, UInt32PrimaryKey>(false, r => r.Next(-50, 50), Comparer<int>.Default, seed);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void IntUnique(int seed)
        {
            RunRandom<int, Int32Key, uint, UInt32PrimaryKey>(true, r => r.Next(-300, 300), Comparer<int>.Default, seed);
        }

        [Theory]
        [InlineData(5)]
        public void IntWithUInt64Pk(int seed)
        {
            RunRandom<int, Int32Key, ulong, UInt64PrimaryKey>(false, r => r.Next(-50, 50), Comparer<int>.Default, seed);
        }

        [Theory]
        [InlineData(6)]
        public void LongNonUnique(int seed)
        {
            RunRandom<long, Int64Key, uint, UInt32PrimaryKey>(false, r => r.Next(-50, 50) * (long)uint.MaxValue, Comparer<long>.Default, seed);
        }

        [Theory]
        [InlineData(7)]
        [InlineData(8)]
        public void StringNonUnique(int seed)
        {
            RunRandom<string, StringKey, uint, UInt32PrimaryKey>(false, RandomString, StringComparer.Ordinal, seed);
        }

        [Theory]
        [InlineData(9)]
        public void StringUnique(int seed)
        {
            RunRandom<string, StringKey, uint, UInt32PrimaryKey>(true, RandomString, StringComparer.Ordinal, seed);
        }

        // 先頭 1 文字のみを正規化値とし、残りはフル比較に委ねるキー
        // (NormalizedBits + PK ビット数 <= 64 でも PK を詰めてはいけないケース)
        readonly struct ShortPrefixStringKey : IOrderedKeyTraits<string>
        {
            public int NormalizedBits => 16;
            public bool NeedsFullComparison => true;

            public ulong Normalize(string key)
            {
                return string.IsNullOrEmpty(key) ? 0UL : key[0];
            }

            public int CompareFull(string a, string b)
            {
                return string.CompareOrdinal(a, b);
            }
        }

        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        public void ShortPrefixStringNonUnique(int seed)
        {
            RunRandom<string, ShortPrefixStringKey, uint, UInt32PrimaryKey>(false, RandomString, StringComparer.Ordinal, seed);
        }

        [Fact]
        public void ShortPrefixStringRangeOrder()
        {
            var index = new OrderedIndex<string, ShortPrefixStringKey, uint, UInt32PrimaryKey>(false, 4);
            Assert.True(index.Insert("ab", 1));
            Assert.True(index.Insert("aa", 2));
            Assert.Equal(new ulong[] { 2 }, Collect(index.Equal("aa")));
            Assert.Equal(new ulong[] { 2, 1 }, Collect(index.Range("a", "b")));
        }

        [Theory]
        [InlineData(10)]
        public void DoubleNonUnique(int seed)
        {
            RunRandom<double, DoubleKey, uint, UInt32PrimaryKey>(false, r => KeyConvert.Double(Math.Round(r.NextDouble() * 20 - 10, 1)), Comparer<double>.Default, seed);
        }

        [Fact]
        public void DoubleNormalizationOrder()
        {
            var values = new[] { double.NegativeInfinity, -1e300, -1.5, -double.Epsilon, 0.0, double.Epsilon, 2.5, 1e300, double.PositiveInfinity };
            var norms = values.Select(v => default(DoubleKey).Normalize(v)).ToArray();
            Assert.Equal(norms.OrderBy(x => x), norms);
            Assert.Equal(default(DoubleKey).Normalize(KeyConvert.Double(-0.0)), default(DoubleKey).Normalize(0.0));
        }

        static void RunRemoveHeavy<TKey, TKT, TPrimaryKey, TPT>(bool unique, int nodeSize, Func<Random, TKey> nextKey, IComparer<TKey> comparer, int seed)
            where TKT : struct, IOrderedKeyTraits<TKey>
            where TPrimaryKey : unmanaged
            where TPT : struct, IPrimaryKeyTraits<TPrimaryKey>
        {
            var rnd = new Random(seed);
            var index = new OrderedIndex<TKey, TKT, TPrimaryKey, TPT>(unique, nodeSize);
            var reference = new SortedSet<(TKey Key, ulong PrimaryKey)>(Comparer<(TKey Key, ulong PrimaryKey)>.Create((x, y) =>
            {
                var c = comparer.Compare(x.Key, y.Key);
                return c != 0 ? c : x.PrimaryKey.CompareTo(y.PrimaryKey);
            }));
            var entries = new List<(TKey Key, ulong PrimaryKey)>();
            var traits = default(TPT);
            ulong nextPrimaryKey = 1;

            void Check()
            {
                Assert.Equal(reference.Count, index.Count);
                var a = nextKey(rnd);
                var b = nextKey(rnd);
                Assert.Equal(reference.Where(e => comparer.Compare(e.Key, a) >= 0 && comparer.Compare(e.Key, b) <= 0).Select(e => e.PrimaryKey), Collect(index.Range(a, b)));
                Assert.Equal(reference.Where(e => comparer.Compare(e.Key, a) == 0).Select(e => e.PrimaryKey), Collect(index.Equal(a)));
            }

            // 増やす局面と減らす局面を交互に繰り返し、併合と借り入れを何度も起こす.
            for (var phase = 0; phase < 8; phase++)
            {
                var grow = phase % 2 == 0;
                var steps = grow ? 1500 : 1400;
                for (var step = 0; step < steps; step++)
                {
                    var insert = grow ? rnd.Next(10) < 8 : rnd.Next(10) < 2;
                    if (insert || entries.Count == 0)
                    {
                        var key = nextKey(rnd);
                        var primaryKey = nextPrimaryKey++;
                        var exists = unique && reference.Any(e => comparer.Compare(e.Key, key) == 0);
                        Assert.Equal(!exists, index.Insert(key, traits.FromUInt64(primaryKey)));
                        if (exists) continue;
                        reference.Add((key, primaryKey));
                        entries.Add((key, primaryKey));
                    }
                    else
                    {
                        var i = rnd.Next(entries.Count);
                        var victim = entries[i];
                        entries[i] = entries[entries.Count - 1];
                        entries.RemoveAt(entries.Count - 1);
                        Assert.True(index.Remove(victim.Key, traits.FromUInt64(victim.PrimaryKey)));
                        Assert.False(index.Remove(victim.Key, traits.FromUInt64(victim.PrimaryKey)));
                        reference.Remove(victim);
                    }

                    if (step % 50 == 0) Check();
                }

                Assert.Equal(reference.Select(e => e.PrimaryKey), Collect(index.Range(reference.Min.Key, reference.Max.Key)));
            }

            foreach (var e in entries)
            {
                Assert.True(index.Remove(e.Key, traits.FromUInt64(e.PrimaryKey)));
            }

            Assert.Equal(0, index.Count);
            Assert.True(index.Insert(nextKey(rnd), traits.FromUInt64(nextPrimaryKey)));
        }

        [Theory]
        [InlineData(21, 4)]
        [InlineData(22, 8)]
        public void RemoveHeavyIntPacked(int seed, int nodeSize)
        {
            RunRemoveHeavy<int, Int32Key, uint, UInt32PrimaryKey>(false, nodeSize, r => r.Next(-200, 200), Comparer<int>.Default, seed);
        }

        [Theory]
        [InlineData(23, 4)]
        [InlineData(24, 8)]
        public void RemoveHeavyIntUnique(int seed, int nodeSize)
        {
            RunRemoveHeavy<int, Int32Key, uint, UInt32PrimaryKey>(true, nodeSize, r => r.Next(-3000, 3000), Comparer<int>.Default, seed);
        }

        [Theory]
        [InlineData(25, 5)]
        public void RemoveHeavyLongNonUnique(int seed, int nodeSize)
        {
            RunRemoveHeavy<long, Int64Key, uint, UInt32PrimaryKey>(false, nodeSize, r => r.Next(-100, 100) * (long)uint.MaxValue, Comparer<long>.Default, seed);
        }

        [Theory]
        [InlineData(26, 4)]
        [InlineData(27, 8)]
        public void RemoveHeavyStringNonUnique(int seed, int nodeSize)
        {
            RunRemoveHeavy<string, StringKey, uint, UInt32PrimaryKey>(false, nodeSize, RandomString, StringComparer.Ordinal, seed);
        }

        [Fact]
        public void RemoveKeepsLeavesFilled()
        {
            const int nodeSize = 16;
            const int n = 20_000;
            var index = new OrderedIndex<int, Int32Key, uint, UInt32PrimaryKey>(true, nodeSize);
            var rnd = new Random(28);
            var keys = Enumerable.Range(0, n).OrderBy(_ => rnd.Next()).ToArray();
            foreach (var key in keys) Assert.True(index.Insert(key, (uint)key));

            // ランダムに 9 割を消す. 併合しなければ葉はほぼそのまま残り、1 枚あたり 1 件程度になる.
            for (var i = 0; i < n * 9 / 10; i++) Assert.True(index.Remove(keys[i], (uint)keys[i]));

            var leafCount = CountLeaves(index);
            Assert.True(index.Count > leafCount * (nodeSize / 4), $"count {index.Count}, leaves {leafCount}");
            Assert.Equal(keys.Skip(n * 9 / 10).OrderBy(x => x).Select(x => (ulong)x), Collect(index.Range(int.MinValue, int.MaxValue)));
        }

        static int CountLeaves<TKey, TKT, TPrimaryKey, TPT>(OrderedIndex<TKey, TKT, TPrimaryKey, TPT> index)
            where TKT : struct, IOrderedKeyTraits<TKey>
            where TPrimaryKey : unmanaged
            where TPT : struct, IPrimaryKeyTraits<TPrimaryKey>
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = index.GetType();
            var nodeSize = (int)type.GetField("b", flags).GetValue(index);
            var node = (int)type.GetField("root", flags).GetValue(index);
            var height = (int)type.GetField("height", flags).GetValue(index);
            var iChild = (int[])type.GetField("iChild", flags).GetValue(index);
            var lNext = (int[])type.GetField("lNext", flags).GetValue(index);
            for (var level = height; level > 0; level--) node = iChild[node * nodeSize];

            var leaves = 0;
            for (; node >= 0; node = lNext[node]) leaves++;
            return leaves;
        }

        [Fact]
        public void AddRangeRebuildReusesArrays()
        {
            const int n = 100_000;
            const int k = 10_000;
            var index = new OrderedIndex<int, Int32Key, uint, UInt32PrimaryKey>(false);
            var keys = new int[n];
            var primaryKeys = new uint[n];
            for (var i = 0; i < n; i++)
            {
                keys[i] = i * 2;
                primaryKeys[i] = (uint)i + 1;
            }

            Assert.True(index.AddRange(keys, primaryKeys));

            var batchKeys = new int[k];
            var batchPrimaryKeys = new uint[k];
            for (var i = 0; i < k; i++)
            {
                batchKeys[i] = i * 20 + 1;
                batchPrimaryKeys[i] = (uint)(n + i + 1);
            }

            void Round()
            {
                // k >= n / 16 なので作り直しの経路を通る.
                Assert.True(index.AddRange(batchKeys, batchPrimaryKeys));
                for (var i = 0; i < k; i++)
                {
                    Assert.True(index.Remove(batchKeys[i], batchPrimaryKeys[i]));
                }
            }

            for (var i = 0; i < 3; i++) Round();

            const int rounds = 5;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < rounds; i++) Round();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // 葉の正規化キーだけで n * 8 バイトある. 作り直しのたびに確保し直していればこれを大きく超える.
            Assert.True(allocated < n * 8 / 4, $"allocated {allocated} bytes in {rounds} rounds");
            Assert.Equal(n, index.Count);

            var e = index.Range(int.MinValue, int.MaxValue);
            var expected = 0;
            while (e.MoveNext())
            {
                Assert.Equal((uint)expected + 1, e.Current);
                expected++;
            }

            Assert.Equal(n, expected);
        }

        [Theory]
        [InlineData(11, false)]
        [InlineData(12, true)]
        public void AddRangeRebuildRepeatedString(int seed, bool unique)
        {
            var rnd = new Random(seed);
            var index = new OrderedIndex<string, StringKey, uint, UInt32PrimaryKey>(unique, 4);
            var reference = new SortedSet<(string Key, uint PrimaryKey)>(Comparer<(string Key, uint PrimaryKey)>.Create((x, y) =>
            {
                var c = string.CompareOrdinal(x.Key, y.Key);
                return c != 0 ? c : x.PrimaryKey.CompareTo(y.PrimaryKey);
            }));
            var present = new HashSet<string>();
            uint nextPrimaryKey = 1;

            for (var round = 0; round < 60; round++)
            {
                // 既存件数の 1/16 以上をまとめて入れ、作り直しの経路を通す.
                var k = Math.Max(1, reference.Count / 4 + rnd.Next(1, 20));
                var keys = new List<string>();
                var primaryKeys = new List<uint>();
                for (var i = 0; i < k; i++)
                {
                    var key = "k" + rnd.Next(0, 2000).ToString("D5");
                    if (unique && (present.Contains(key) || keys.Contains(key))) continue;
                    keys.Add(key);
                    primaryKeys.Add(nextPrimaryKey++);
                }

                Assert.True(index.AddRange(keys.ToArray(), primaryKeys.ToArray()));
                for (var i = 0; i < keys.Count; i++)
                {
                    reference.Add((keys[i], primaryKeys[i]));
                    present.Add(keys[i]);
                }

                var victims = reference.Where(_ => rnd.Next(3) == 0).ToList();
                foreach (var v in victims)
                {
                    Assert.True(index.Remove(v.Key, v.PrimaryKey));
                    reference.Remove(v);
                    present.Remove(v.Key);
                }

                if (round % 15 == 14) index.TrimExcess();

                Assert.Equal(reference.Count, index.Count);
                Assert.Equal(reference.Select(x => (ulong)x.PrimaryKey), Collect(index.Range("", "￿")));
                var a = "k" + rnd.Next(0, 2000).ToString("D5");
                var b = "k" + rnd.Next(0, 2000).ToString("D5");
                var expected = reference.Where(x => string.CompareOrdinal(x.Key, a) >= 0 && string.CompareOrdinal(x.Key, b) <= 0).Select(x => (ulong)x.PrimaryKey);
                Assert.Equal(expected, Collect(index.Range(a, b)));
            }
        }

        [Fact]
        public void HashMultiIndex()
        {
            var index = new HashMultiIndex<string, uint>();
            index.Insert("a", 1);
            index.Insert("a", 2);
            index.Insert(null, 3);
            index.Insert("b", 4);
            Assert.Equal(4, index.Count);

            Assert.True(index.Remove("a", 1));
            Assert.False(index.Remove("a", 1));
            var e = index.Equal("a");
            Assert.True(e.MoveNext());
            Assert.Equal(2u, e.Current);
            Assert.False(e.MoveNext());

            Assert.True(index.TryGetFirst(null, out var primaryKey));
            Assert.Equal(3u, primaryKey);
            Assert.True(index.Remove(null, 3));
            Assert.False(index.ContainsKey(null));
            Assert.True(index.Remove("a", 2));
            Assert.False(index.Equal("a").MoveNext());
            index.Insert("c", 5);
            index.TrimExcess();
            Assert.True(index.ContainsKey("c"));
            Assert.Equal(2, index.Count);
        }

        [Fact]
        public void HashUniqueIndex()
        {
            var index = new HashUniqueIndex<string, uint>();
            Assert.True(index.Insert(null, 1));
            Assert.False(index.Insert(null, 2));
            Assert.True(index.Insert("x", 3));
            Assert.False(index.Remove("x", 4));
            Assert.False(index.AddRange(new[] { "y", "x" }, new uint[] { 5, 6 }));
            Assert.False(index.ContainsKey("y"));
            Assert.True(index.Remove("x", 3));
            Assert.Equal(1, index.Count);
        }
    }
}
