using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace OrdoMemory.Tests
{
    public class TableTests
    {
        static Item NewItem(int code, string name = "n", Kind kind = Kind.A, string tag = null, double score = 0, string description = null)
        {
            return new Item(code, name, kind, tag ?? "tag" + code, score, description);
        }

        [Fact]
        public void InsertFindRemove()
        {
            var table = new ItemTable();
            Assert.True(table.TryInsert(NewItem(1, "alice"), out var pk));
            Assert.NotEqual(ItemTable.InvalidPk, pk);

            Assert.True(table.TryFind(pk, out var row));
            Assert.Equal("alice", row.Name);

            ref readonly var r = ref table.FindRef(pk, out var found);
            Assert.True(found);
            Assert.Equal(1, r.Code);

            Assert.False(table.TryFind(ItemTable.InvalidPk, out _));
            Assert.True(table.Remove(pk));
            Assert.False(table.Remove(pk));
            Assert.False(table.Contains(pk));
            Assert.Equal(0, table.Count);
            Assert.False(table.TryFindByCode(1, out _));
        }

        [Fact]
        public void ReusedSlotGetsNewGeneration()
        {
            var table = new ItemTable();
            table.TryInsert(NewItem(1), out var first);
            table.Remove(first);
            table.TryInsert(NewItem(2), out var second);

            Assert.Equal(first & ((1u << ItemTable.SlotBits) - 1), second & ((1u << ItemTable.SlotBits) - 1));
            Assert.NotEqual(first, second);
            Assert.False(table.Contains(first));
            Assert.True(table.TryFind(second, out var row));
            Assert.Equal(2, row.Code);
        }

        [Fact]
        public void RemoveMovesLastRecord()
        {
            var table = new ItemTable();
            var pks = new uint[5];
            for (var i = 0; i < 5; i++) table.TryInsert(NewItem(i), out pks[i]);

            table.Remove(pks[1]);
            for (var i = 0; i < 5; i++)
            {
                if (i == 1) continue;
                Assert.True(table.TryFind(pks[i], out var row));
                Assert.Equal(i, row.Code);
            }

            var seen = new List<(uint, int)>();
            foreach (var it in Enumerate(table)) seen.Add(it);
            Assert.Equal(new[] { 0, 2, 3, 4 }, seen.Select(s => s.Item2).OrderBy(x => x));
            foreach (var (pk, code) in seen)
            {
                Assert.True(table.TryFind(pk, out var row));
                Assert.Equal(code, row.Code);
            }
        }

        static List<(uint, int)> Enumerate(ItemTable table)
        {
            var list = new List<(uint, int)>();
            var e = table.All().GetEnumerator();
            while (e.MoveNext()) list.Add((e.CurrentPk, e.Current.Code));
            return list;
        }

        [Fact]
        public void UniqueConstraint()
        {
            var table = new ItemTable();
            Assert.True(table.TryInsert(NewItem(1, tag: "x"), out var a));
            Assert.False(table.TryInsert(NewItem(1, tag: "y"), out var dup));
            Assert.Equal(ItemTable.InvalidPk, dup);
            Assert.False(table.TryInsert(NewItem(2, tag: "x"), out _));
            Assert.True(table.TryInsert(NewItem(2, tag: "y"), out var b));

            Assert.False(table.Update(b, code: 1));
            Assert.True(table.TryFind(b, out var row));
            Assert.Equal(2, row.Code);
            Assert.True(table.TryFindByCode(2, out _));

            Assert.True(table.Update(b, code: 3, tag: "z"));
            Assert.False(table.TryFindByCode(2, out _));
            Assert.True(table.TryFindByCode(3, out row));
            Assert.Equal("z", row.Tag);
            Assert.True(table.TryFindByTag("z", out _));
            Assert.False(table.TryFindByTag("y", out _));
            Assert.Equal(2, table.Count);
        }

        [Fact]
        public void UpdateOnlySpecifiedFields()
        {
            var table = new ItemTable();
            table.TryInsert(NewItem(1, "a", Kind.B, "t", 1.5, "d"), out var pk);
            Assert.True(table.Update(pk, description: null));

            table.TryFind(pk, out var row);
            Assert.Null(row.Description);
            Assert.Equal("a", row.Name);
            Assert.Equal(Kind.B, row.Kind);
            Assert.Equal(1.5, row.Score);

            Assert.True(table.Update(pk, name: "b", kind: Kind.C));
            Assert.False(table.TryFindFirstByName("a", out _));
            Assert.True(table.TryFindFirstByName("b", out _));
            Assert.Equal(0, CountKind(table, Kind.B));
            Assert.Equal(1, CountKind(table, Kind.C));
        }

        static int CountKind(ItemTable table, Kind kind)
        {
            var n = 0;
            foreach (ref readonly var _ in table.FindAllByKind(kind)) n++;
            return n;
        }

        [Fact]
        public void NonUniqueAndRangeQueries()
        {
            var table = new ItemTable();
            var names = new[] { "bob", "alice", "bob", "carol", "alice", "bobby", "", null };
            for (var i = 0; i < names.Length; i++) table.TryInsert(NewItem(i, names[i], (Kind)(i % 3), score: i - 3.5), out _);

            var bobs = new List<int>();
            foreach (ref readonly var r in table.FindAllByName("bob")) bobs.Add(r.Code);
            Assert.Equal(new[] { 0, 2 }, bobs.OrderBy(x => x));

            var range = new List<string>();
            foreach (ref readonly var r in table.FindRangeByName("alice", "bob")) range.Add(r.Name);
            Assert.Equal(new[] { "alice", "alice", "bob", "bob" }, range);

            var all = new List<string>();
            foreach (ref readonly var r in table.FindRangeByName(null, "￿")) all.Add(r.Name);
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), all);

            var scores = new List<double>();
            foreach (ref readonly var r in table.FindRangeByScore(-2, 1)) scores.Add(r.Score);
            Assert.Equal(new[] { -1.5, -0.5, 0.5 }, scores);

            var codes = new List<int>();
            foreach (ref readonly var r in table.FindRangeByCode(5, 2)) codes.Add(r.Code);
            Assert.Empty(codes);

            var kinds = new List<int>();
            foreach (ref readonly var r in table.FindAllByKind(Kind.B)) kinds.Add(r.Code);
            Assert.Equal(new[] { 1, 4, 7 }, kinds.OrderBy(x => x));

            Assert.True(table.TryFindFirstByName(null, out var nullRow));
            Assert.Equal(7, nullRow.Code);
        }

        [Fact]
        public void ModificationDuringEnumerationThrows()
        {
            var table = new ItemTable();
            var pks = new uint[3];
            for (var i = 0; i < 3; i++) table.TryInsert(NewItem(i), out pks[i]);

            Assert.Throws<InvalidOperationException>(() =>
            {
                foreach (ref readonly var r in table.All()) table.TryInsert(NewItem(r.Code + 100), out _);
            });

            Assert.Throws<InvalidOperationException>(() =>
            {
                foreach (ref readonly var r in table.FindRangeByCode(0, 1000)) table.Update(pks[0], code: r.Code + 1000);
            });
        }

        [Fact]
        public void BulkInsertSmallAndLarge()
        {
            var table = new ItemTable();
            var rows = Enumerable.Range(0, 1000).Select(i => NewItem(i * 2, "n" + (i % 17), (Kind)(i % 3), score: i)).ToArray();
            var pks = new uint[rows.Length];
            Assert.True(table.TryBulkInsert(rows, pks));
            Assert.Equal(1000, table.Count);

            var small = Enumerable.Range(0, 10).Select(i => NewItem(i * 2 + 1)).ToArray();
            var smallPks = new uint[10];
            Assert.True(table.TryBulkInsert(small, smallPks));
            Assert.Equal(1010, table.Count);

            var codes = new List<int>();
            foreach (ref readonly var r in table.FindRangeByCode(0, 30)) codes.Add(r.Code);
            Assert.Equal(Enumerable.Range(0, 21).Select(i => i < 20 ? i : 20), codes.Take(21));

            for (var i = 0; i < rows.Length; i++)
            {
                Assert.True(table.TryFind(pks[i], out var row));
                Assert.Equal(i * 2, row.Code);
            }
        }

        [Fact]
        public void BulkInsertRollsBackOnDuplicate()
        {
            var table = new ItemTable();
            for (var i = 0; i < 100; i++) table.TryInsert(NewItem(i), out _);

            // 既存と重複 (1 件ずつ挿入する経路).
            var conflict = new[] { NewItem(1000), NewItem(5, tag: "fresh") };
            Assert.False(table.TryBulkInsert(conflict, new uint[2]));

            // バッチ内で重複 (作り直す経路). Code の索引は通り、Tag の索引で失敗する.
            var batch = Enumerable.Range(0, 200).Select(i => NewItem(2000 + i, tag: i == 150 ? "dup" : i == 10 ? "dup" : null)).ToArray();
            Assert.False(table.TryBulkInsert(batch, new uint[200]));

            Assert.Equal(100, table.Count);
            Assert.False(table.TryFindByCode(1000, out _));
            Assert.False(table.TryFindByCode(2000, out _));
            Assert.False(table.TryFindByTag("dup", out _));
            var n = 0;
            foreach (ref readonly var _ in table.FindRangeByCode(int.MinValue, int.MaxValue)) n++;
            Assert.Equal(100, n);
            n = 0;
            foreach (ref readonly var _ in table.FindRangeByName(null, "￿")) n++;
            Assert.Equal(100, n);
        }

        [Fact]
        public void GenerationWrapRetiresSlot()
        {
            var table = new OneGenRowTable();
            Assert.True(table.TryInsert(new OneGenRow(1), out var a));
            Assert.True(table.Remove(a));
            Assert.True(table.TryInsert(new OneGenRow(2), out var b));
            Assert.NotEqual(a & ((1u << OneGenRowTable.SlotBits) - 1), b & ((1u << OneGenRowTable.SlotBits) - 1));
            Assert.False(table.Contains(a));
        }

        [Fact]
        public void SlotExhaustion()
        {
            var table = new TinyRowTable();
            for (var i = 0; i < 4; i++) Assert.True(table.TryInsert(new TinyRow(i), out _));
            Assert.False(table.TryInsert(new TinyRow(9), out _));
            Assert.False(table.TryBulkInsert(new[] { new TinyRow(10) }, new uint[1]));
            Assert.Equal(4, table.Count);
        }

        [Fact]
        public void UInt64Pk()
        {
            var table = new BigTable();
            Assert.True(table.TryInsert(new BigRow(5, 1), out ulong a));
            Assert.True(table.TryInsert(new BigRow(5, 2), out var b));
            Assert.True(table.TryInsert(new BigRow(long.MinValue, 3), out _));
            Assert.Equal(1UL << 32, a & ~0xFFFFFFFFUL);

            var pks = new List<ulong>();
            foreach (ref readonly var _ in table.FindAllByValue(5)) { }
            var e = table.FindAllByValue(5).GetEnumerator();
            while (e.MoveNext()) pks.Add(e.CurrentPk);
            Assert.Equal(new[] { a, b }.OrderBy(x => x), pks);

            Assert.True(table.Update(a, pkValue: 10));
            Assert.True(table.TryFind(a, out var row));
            Assert.Equal(10, row.Pk);
        }

        [Fact]
        public void TrimExcessKeepsContents()
        {
            var table = new ItemTable(100);
            var pks = new uint[50];
            for (var i = 0; i < 50; i++) table.TryInsert(NewItem(i, "n" + (i % 5)), out pks[i]);
            for (var i = 0; i < 50; i += 2) table.Remove(pks[i]);

            table.TrimExcess();
            Assert.Equal(25, table.Capacity);
            for (var i = 1; i < 50; i += 2) Assert.True(table.TryFindByCode(i, out _));

            var n = 0;
            foreach (ref readonly var _ in table.FindAllByName("n1")) n++;
            Assert.Equal(5, n);
            Assert.True(table.TryInsert(NewItem(1000), out _));
        }

        [Fact]
        public void QueriesDoNotAllocate()
        {
            var table = new ItemTable();
            for (var i = 0; i < 1000; i++) table.TryInsert(NewItem(i, "n" + (i % 10), (Kind)(i % 3), score: i), out _);

            long sum = 0;
            void Run()
            {
                foreach (ref readonly var r in table.All()) sum += r.Code;
                foreach (ref readonly var r in table.FindAllByName("n3")) sum += r.Code;
                foreach (ref readonly var r in table.FindRangeByCode(100, 200)) sum += r.Code;
                foreach (ref readonly var r in table.FindAllByKind(Kind.B)) sum += r.Code;
                foreach (ref readonly var r in table.FindRangeByScore(10, 20)) sum += r.Code;
                if (table.TryFindByTag("tag5", out var row)) sum += row.Code;
            }

            Run();
            var before = GC.GetAllocatedBytesForCurrentThread();
            Run();
            Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
            Assert.NotEqual(0, sum);
        }
    }
}
