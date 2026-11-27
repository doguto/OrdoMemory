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
