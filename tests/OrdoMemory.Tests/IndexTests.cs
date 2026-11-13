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
            public readonly List<(TKey Key, ulong Pk)> Entries = new List<(TKey, ulong)>();

            public Model(IComparer<TKey> comparer)
            {
                this.comparer = comparer;
            }

            int Compare((TKey Key, ulong Pk) a, (TKey Key, ulong Pk) b)
            {
                var c = comparer.Compare(a.Key, b.Key);
                return c != 0 ? c : a.Pk.CompareTo(b.Pk);
            }

            public void Add(TKey key, ulong pk)
            {
                Entries.Add((key, pk));
                Entries.Sort(Compare);
            }

            public IEnumerable<ulong> Range(TKey min, TKey max)
            {
                return Entries.Where(e => comparer.Compare(e.Key, min) >= 0 && comparer.Compare(e.Key, max) <= 0).Select(e => e.Pk);
            }
        }

        static List<ulong> Collect<TKey, TKT, TPk, TPT>(OrderedIndex<TKey, TKT, TPk, TPT>.RangeEnumerator e)
            where TKT : struct, IOrderedKeyTraits<TKey>
            where TPk : unmanaged
            where TPT : struct, IPkTraits<TPk>
        {
            var list = new List<ulong>();
            while (e.MoveNext()) list.Add(default(TPT).ToUInt64(e.Current));
            return list;
        }

        static void RunRandom<TKey, TKT, TPk, TPT>(bool unique, Func<Random, TKey> nextKey, IComparer<TKey> comparer, int seed)
            where TKT : struct, IOrderedKeyTraits<TKey>
            where TPk : unmanaged
            where TPT : struct, IPkTraits<TPk>
        {
            var rnd = new Random(seed);
            var index = new OrderedIndex<TKey, TKT, TPk, TPT>(unique, nodeSize: 4);
            var model = new Model<TKey>(comparer);
            ulong nextPk = 1;
            var traits = default(TPT);

            for (var step = 0; step < 4000; step++)
            {
                var op = rnd.Next(10);
                if (op < 5 || model.Entries.Count == 0)
                {
                    var key = nextKey(rnd);
                    var pk = nextPk++;
                    var exists = unique && model.Entries.Any(e => comparer.Compare(e.Key, key) == 0);
                    Assert.Equal(!exists, index.Insert(key, traits.FromUInt64(pk)));
                    if (!exists) model.Add(key, pk);
                }
                else if (op < 8)
                {
                    var victim = model.Entries[rnd.Next(model.Entries.Count)];
                    Assert.True(index.Remove(victim.Key, traits.FromUInt64(victim.Pk)));
                    Assert.False(index.Remove(victim.Key, traits.FromUInt64(victim.Pk)));
                    model.Entries.Remove(victim);
                }
                else if (op < 9)
                {
                    var k = rnd.Next(1, 40);
                    var keys = new TKey[k];
                    var pks = new TPk[k];
                    for (var i = 0; i < k; i++)
                    {
                        keys[i] = nextKey(rnd);
                        pks[i] = traits.FromUInt64(nextPk++);
                    }

                    var conflict = unique && (keys.Distinct(new EqComparer<TKey>(comparer)).Count() != k
                        || keys.Any(key => model.Entries.Any(e => comparer.Compare(e.Key, key) == 0)));
                    Assert.Equal(!conflict, index.AddRange(keys, pks));
                    if (!conflict)
                    {
                        for (var i = 0; i < k; i++) model.Add(keys[i], traits.ToUInt64(pks[i]));
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
                }
            }

            foreach (var e in model.Entries.ToList())
            {
                Assert.True(index.Remove(e.Key, traits.FromUInt64(e.Pk)));
                model.Entries.Remove(e);
            }

            Assert.Equal(0, index.Count);
            Assert.True(index.Insert(nextKey(rnd), traits.FromUInt64(nextPk)));
        }

        sealed class EqComparer<T> : IEqualityComparer<T>
        {
            readonly IComparer<T> comparer;
            public EqComparer(IComparer<T> comparer) => this.comparer = comparer;
            public bool Equals(T x, T y) => comparer.Compare(x, y) == 0;
            public int GetHashCode(T obj) => 0;
        }

        static string RandomString(Random rnd)
        {
            var len = rnd.Next(0, 7);
            var chars = new char[len];
            for (var i = 0; i < len; i++) chars[i] = (char)('a' + rnd.Next(3));
            return rnd.Next(30) == 0 ? null : new string(chars);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void IntPacked(int seed) => RunRandom<int, Int32Key, uint, UInt32Pk>(false, r => r.Next(-50, 50), Comparer<int>.Default, seed);

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void IntUnique(int seed) => RunRandom<int, Int32Key, uint, UInt32Pk>(true, r => r.Next(-300, 300), Comparer<int>.Default, seed);

        [Theory]
        [InlineData(5)]
        public void IntWithUInt64Pk(int seed) => RunRandom<int, Int32Key, ulong, UInt64Pk>(false, r => r.Next(-50, 50), Comparer<int>.Default, seed);

        [Theory]
        [InlineData(6)]
        public void LongNonUnique(int seed) => RunRandom<long, Int64Key, uint, UInt32Pk>(false, r => r.Next(-50, 50) * (long)uint.MaxValue, Comparer<long>.Default, seed);

        [Theory]
        [InlineData(7)]
        [InlineData(8)]
        public void StringNonUnique(int seed) => RunRandom<string, StringKey, uint, UInt32Pk>(false, RandomString, StringComparer.Ordinal, seed);

        [Theory]
        [InlineData(9)]
        public void StringUnique(int seed) => RunRandom<string, StringKey, uint, UInt32Pk>(true, RandomString, StringComparer.Ordinal, seed);

        [Theory]
        [InlineData(10)]
        public void DoubleNonUnique(int seed) => RunRandom<double, DoubleKey, uint, UInt32Pk>(false, r => KeyConvert.Double(Math.Round(r.NextDouble() * 20 - 10, 1)), Comparer<double>.Default, seed);

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

            Assert.True(index.TryGetFirst(null, out var pk));
            Assert.Equal(3u, pk);
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
