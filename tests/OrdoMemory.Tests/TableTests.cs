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
            Assert.True(table.TryInsert(NewItem(1, "alice"), out var primaryKey));
            Assert.NotEqual(ItemTable.InvalidPrimaryKey, primaryKey);

            Assert.True(table.TryFind(primaryKey, out var row));
            Assert.Equal("alice", row.Name);

            ref readonly var r = ref table.FindRef(primaryKey, out var found);
            Assert.True(found);
            Assert.Equal(1, r.Code);

            Assert.False(table.TryFind(ItemTable.InvalidPrimaryKey, out _));
            Assert.True(table.Remove(primaryKey));
            Assert.False(table.Remove(primaryKey));
            Assert.False(table.Contains(primaryKey));
            Assert.Equal(0, table.Count);
            Assert.False(table.TryFindByCode(1, out _));
        }

        [Fact]
        public void RowHoldsPrimaryKey()
        {
            var table = new ItemTable();
            Assert.True(table.TryInsert(NewItem(1), out var first));
            Assert.True(table.TryInsert(NewItem(2), out var second));
            Assert.True(table.TryFind(first, out var row));
            Assert.Equal(first, row.Id);

            Assert.True(table.TryFindByCode(2, out row));
            Assert.Equal(second, row.Id);

            Assert.True(table.Update(first, name: "renamed", code: 10));
            Assert.True(table.TryFind(first, out row));
            Assert.Equal(first, row.Id);
            Assert.Equal("renamed", row.Name);

            table.Remove(first);
            Assert.True(table.TryFind(second, out row));
            Assert.Equal(second, row.Id);

            foreach (ref readonly var r in table.All()) Assert.Equal(second, r.Id);
        }

        [Fact]
        public void BulkInsertedRowsHoldPrimaryKey()
        {
            var table = new ItemTable();
            var rows = Enumerable.Range(0, 10).Select(i => NewItem(i)).ToArray();
            var primaryKeys = new uint[rows.Length];
            Assert.True(table.TryBulkInsert(rows, primaryKeys));

            for (var i = 0; i < rows.Length; i++)
            {
                Assert.True(table.TryFind(primaryKeys[i], out var row));
                Assert.Equal(primaryKeys[i], row.Id);
            }
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
            var primaryKeys = new uint[5];
            for (var i = 0; i < 5; i++)
            {
                table.TryInsert(NewItem(i), out primaryKeys[i]);
            }

            table.Remove(primaryKeys[1]);
            for (var i = 0; i < 5; i++)
            {
                if (i == 1) continue;
                Assert.True(table.TryFind(primaryKeys[i], out var row));
                Assert.Equal(i, row.Code);
            }

            var seen = new List<(uint, int)>();
            foreach (var it in Enumerate(table)) seen.Add(it);
            Assert.Equal(new[] { 0, 2, 3, 4 }, seen.Select(s => s.Item2).OrderBy(x => x));
            foreach (var (primaryKey, code) in seen)
            {
                Assert.True(table.TryFind(primaryKey, out var row));
                Assert.Equal(code, row.Code);
            }
        }

        static List<(uint, int)> Enumerate(ItemTable table)
        {
            var list = new List<(uint, int)>();
            foreach (ref readonly var r in table.All())
            {
                list.Add((r.Id, r.Code));
            }

            return list;
        }

        [Fact]
        public void UniqueConstraint()
        {
            var table = new ItemTable();
            Assert.True(table.TryInsert(NewItem(1, tag: "x"), out var a));
            Assert.False(table.TryInsert(NewItem(1, tag: "y"), out var dup));
            Assert.Equal(ItemTable.InvalidPrimaryKey, dup);
            Assert.False(table.TryInsert(NewItem(2, tag: "x"), out _));
            Assert.True(table.TryInsert(NewItem(2, tag: "y"), out var b));

            Assert.False(table.Update(b, 1));
            Assert.True(table.TryFind(b, out var row));
            Assert.Equal(2, row.Code);
            Assert.True(table.TryFindByCode(2, out _));

            Assert.True(table.Update(b, 3, tag: "z"));
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
            table.TryInsert(NewItem(1, "a", Kind.B, "t", 1.5, "d"), out var primaryKey);
            Assert.True(table.Update(primaryKey, description: null));

            table.TryFind(primaryKey, out var row);
            Assert.Null(row.Description);
            Assert.Equal("a", row.Name);
            Assert.Equal(Kind.B, row.Kind);
            Assert.Equal(1.5, row.Score);

            Assert.True(table.Update(primaryKey, name: "b", kind: Kind.C));
            Assert.False(table.TryFindFirstByName("a", out _));
            Assert.True(table.TryFindFirstByName("b", out _));
            Assert.Equal(0, CountKind(table, Kind.B));
            Assert.Equal(1, CountKind(table, Kind.C));
        }

        [Fact]
        public void UpdateIgnoresUserConstructorParameterOrder()
        {
            var table = new SwapRowTable();
            Assert.True(table.TryInsert(new SwapRow(b: 2, a: 1), out var primaryKey));

            Assert.True(table.Update(primaryKey, a: 10));

            Assert.True(table.TryFind(primaryKey, out var row));
            Assert.Equal(primaryKey, row.Id);
            Assert.Equal(10, row.A);
            Assert.Equal(2, row.B);
            Assert.False(table.TryFindFirstByA(1, out _));
            Assert.True(table.TryFindFirstByA(10, out _));
            Assert.False(table.TryFindFirstByA(2, out _));
        }

        [Fact]
        public void UpdateOfMutableRowIgnoresUserConstructorParameterOrder()
        {
            var table = new MutableSwapRowTable();
            Assert.True(table.TryInsert(new MutableSwapRow(b: 2, a: 1), out var primaryKey));

            // readonly の A が変わるため Record 全体を作り直す
            Assert.True(table.Update(primaryKey, a: 10));
            Assert.True(table.TryFind(primaryKey, out var row));
            Assert.Equal(primaryKey, row.Id);
            Assert.Equal(10, row.A);
            Assert.Equal(2, row.B);

            // B はその場で書き込む
            Assert.True(table.Update(primaryKey, b: 5));
            Assert.True(table.TryFind(primaryKey, out row));
            Assert.Equal(10, row.A);
            Assert.Equal(5, row.B);
            Assert.True(table.TryFindFirstByA(10, out _));
            Assert.False(table.TryFindFirstByA(1, out _));
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
            for (var i = 0; i < names.Length; i++)
            {
                table.TryInsert(NewItem(i, names[i], (Kind)(i % 3), score: i - 3.5), out _);
            }

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
        public void SecondaryKeyFindRef()
        {
            var table = new ItemTable();
            table.TryInsert(NewItem(1, "alice", Kind.A, "t1"), out var first);
            table.TryInsert(NewItem(2, "bob", Kind.B, "t2"), out var second);
            table.TryInsert(NewItem(3, "bob", Kind.B, "t3"), out _);

            // Unique (Ordered)
            ref readonly var byCode = ref table.FindRefByCode(2, out var found);
            Assert.True(found);
            Assert.Equal(second, byCode.Id);
            Assert.Equal("bob", byCode.Name);

            ref readonly var missingCode = ref table.FindRefByCode(99, out found);
            Assert.False(found);
            Assert.Equal(default(Item).Id, missingCode.Id);
            Assert.Null(missingCode.Name);

            // Unique (Hash)
            ref readonly var byTag = ref table.FindRefByTag("t1", out found);
            Assert.True(found);
            Assert.Equal(first, byTag.Id);
            table.FindRefByTag("none", out found);
            Assert.False(found);

            // 非 Unique は TryFindFirstByXxx と同じ Record を返す
            Assert.True(table.TryFindFirstByName("bob", out var firstBob));
            ref readonly var byName = ref table.FindFirstRefByName("bob", out found);
            Assert.True(found);
            Assert.Equal(firstBob.Id, byName.Id);
            table.FindFirstRefByName("carol", out found);
            Assert.False(found);

            Assert.True(table.TryFindFirstByKind(Kind.B, out var firstB));
            ref readonly var byKind = ref table.FindFirstRefByKind(Kind.B, out found);
            Assert.True(found);
            Assert.Equal(firstB.Id, byKind.Id);
            table.FindFirstRefByKind(Kind.C, out found);
            Assert.False(found);

            table.Remove(second);
            table.FindRefByCode(2, out found);
            Assert.False(found);
        }

        [Fact]
        public void ModificationDuringEnumerationThrows()
        {
            var table = new ItemTable();
            var primaryKeys = new uint[3];
            for (var i = 0; i < 3; i++)
            {
                table.TryInsert(NewItem(i), out primaryKeys[i]);
            }

            Assert.Throws<InvalidOperationException>(() =>
            {
                foreach (ref readonly var r in table.All()) table.TryInsert(NewItem(r.Code + 100), out _);
            });

            Assert.Throws<InvalidOperationException>(() =>
            {
                foreach (ref readonly var r in table.FindRangeByCode(0, 1000)) table.Update(primaryKeys[0], r.Code + 1000);
            });
        }

        [Fact]
        public void BulkInsertSmallAndLarge()
        {
            var table = new ItemTable();
            var rows = Enumerable.Range(0, 1000).Select(i => NewItem(i * 2, "n" + i % 17, (Kind)(i % 3), score: i)).ToArray();
            var primaryKeys = new uint[rows.Length];
            Assert.True(table.TryBulkInsert(rows, primaryKeys));
            Assert.Equal(1000, table.Count);

            var small = Enumerable.Range(0, 10).Select(i => NewItem(i * 2 + 1)).ToArray();
            var smallPrimaryKeys = new uint[10];
            Assert.True(table.TryBulkInsert(small, smallPrimaryKeys));
            Assert.Equal(1010, table.Count);

            var codes = new List<int>();
            foreach (ref readonly var r in table.FindRangeByCode(0, 30)) codes.Add(r.Code);
            Assert.Equal(Enumerable.Range(0, 21).Select(i => i < 20 ? i : 20), codes.Take(21));

            for (var i = 0; i < rows.Length; i++)
            {
                Assert.True(table.TryFind(primaryKeys[i], out var row));
                Assert.Equal(i * 2, row.Code);
            }
        }

        [Fact]
        public void BulkInsertRollsBackOnDuplicate()
        {
            var table = new ItemTable();
            for (var i = 0; i < 100; i++)
            {
                table.TryInsert(NewItem(i), out _);
            }

            var conflict = new[] { NewItem(1000), NewItem(5, tag: "fresh") };
            Assert.False(table.TryBulkInsert(conflict, new uint[2]));

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
            for (var i = 0; i < 4; i++)
            {
                Assert.True(table.TryInsert(new TinyRow(i), out _));
            }

            Assert.False(table.TryInsert(new TinyRow(9), out _));
            Assert.False(table.TryBulkInsert(new[] { new TinyRow(10) }, new uint[1]));
            Assert.Equal(4, table.Count);
        }

        [Fact]
        public void InsertResultDistinguishesFailureReasons()
        {
            var table = new TinyRowTable();
            Assert.True(table.TryInsert(new TinyRow(0), out var primaryKey, out var result));
            Assert.Equal(InsertResult.Success, result);
            Assert.NotEqual(TinyRowTable.InvalidPrimaryKey, primaryKey);

            Assert.False(table.TryInsert(new TinyRow(0), out primaryKey, out result));
            Assert.Equal(InsertResult.Duplicate, result);
            Assert.Equal(TinyRowTable.InvalidPrimaryKey, primaryKey);

            for (var i = 1; i < 4; i++)
            {
                Assert.True(table.TryInsert(new TinyRow(i), out _, out result));
                Assert.Equal(InsertResult.Success, result);
            }

            Assert.False(table.TryInsert(new TinyRow(9), out primaryKey, out result));
            Assert.Equal(InsertResult.Full, result);
            Assert.Equal(TinyRowTable.InvalidPrimaryKey, primaryKey);

            // 重複と容量枯渇が同時に起きる場合は Duplicate を返す
            Assert.False(table.TryInsert(new TinyRow(0), out _, out result));
            Assert.Equal(InsertResult.Duplicate, result);
            Assert.Equal(4, table.Count);
        }

        [Fact]
        public void BulkInsertResultDistinguishesFailureReasons()
        {
            var table = new TinyRowTable();
            var primaryKeys = new uint[4];
            Assert.True(table.TryBulkInsert(Array.Empty<TinyRow>(), primaryKeys, out var result));
            Assert.Equal(InsertResult.Success, result);

            Assert.True(table.TryBulkInsert(new[] { new TinyRow(0), new TinyRow(1) }, primaryKeys, out result));
            Assert.Equal(InsertResult.Success, result);

            Assert.False(table.TryBulkInsert(new[] { new TinyRow(2), new TinyRow(0) }, primaryKeys, out result));
            Assert.Equal(InsertResult.Duplicate, result);

            Assert.False(table.TryBulkInsert(new[] { new TinyRow(2), new TinyRow(2) }, primaryKeys, out result));
            Assert.Equal(InsertResult.Duplicate, result);
            Assert.Equal(2, table.Count);

            Assert.False(table.TryBulkInsert(new[] { new TinyRow(2), new TinyRow(3), new TinyRow(4) }, primaryKeys, out result));
            Assert.Equal(InsertResult.Full, result);
            Assert.Equal(2, table.Count);

            Assert.True(table.TryBulkInsert(new[] { new TinyRow(2), new TinyRow(3) }, primaryKeys, out result));
            Assert.Equal(InsertResult.Success, result);
            Assert.Equal(4, table.Count);
        }

        [Fact]
        public void InsertResultWithoutUniqueKeys()
        {
            var table = new OneGenRowTable();
            Assert.True(table.TryInsert(new OneGenRow(1), out _, out var result));
            Assert.Equal(InsertResult.Success, result);
            Assert.True(table.TryInsert(new OneGenRow(1), out _, out result));
            Assert.Equal(InsertResult.Success, result);
        }

        [Fact]
        public void UInt64PrimaryKey()
        {
            var table = new BigTable();
            Assert.True(table.TryInsert(new BigRow(5, 1), out var a));
            Assert.True(table.TryInsert(new BigRow(5, 2), out var b));
            Assert.True(table.TryInsert(new BigRow(long.MinValue, 3), out _));
            Assert.Equal(1UL << 32, a & ~0xFFFFFFFFUL);

            var primaryKeys = new List<ulong>();
            foreach (ref readonly var r in table.FindAllByValue(5))
            {
                primaryKeys.Add(r.Id);
            }

            Assert.Equal(new[] { a, b }.OrderBy(x => x), primaryKeys);

            Assert.True(table.Update(a, primaryKeyValue: 10));
            Assert.True(table.TryFind(a, out var row));
            Assert.Equal(10, row.PrimaryKey);
            Assert.Equal(a, row.Id);
        }

        [Fact]
        public void TrimExcessKeepsContents()
        {
            var table = new ItemTable(100);
            var primaryKeys = new uint[50];
            for (var i = 0; i < 50; i++)
            {
                table.TryInsert(NewItem(i, "n" + i % 5), out primaryKeys[i]);
            }

            for (var i = 0; i < 50; i += 2)
            {
                table.Remove(primaryKeys[i]);
            }

            table.TrimExcess();
            Assert.Equal(25, table.Capacity);
            for (var i = 1; i < 50; i += 2)
            {
                Assert.True(table.TryFindByCode(i, out _));
            }

            var n = 0;
            foreach (ref readonly var _ in table.FindAllByName("n1")) n++;
            Assert.Equal(5, n);
            Assert.True(table.TryInsert(NewItem(1000), out _));
        }

        [Fact]
        public void QueriesDoNotAllocate()
        {
            var table = new ItemTable();
            for (var i = 0; i < 1000; i++)
            {
                table.TryInsert(NewItem(i, "n" + i % 10, (Kind)(i % 3), score: i), out _);
            }

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

        [Fact]
        public void UpdateWritesMutableFieldsInPlace()
        {
            var table = new CharacterTable();
            Assert.True(table.TryInsert(new Character(10, 1, "g", 100, "a"), out var first));
            Assert.True(table.TryInsert(new Character(20, 1, "g", 50, "b"), out var second));

            Assert.True(table.Update(first, hp: 70));
            ref readonly var row = ref table.FindRef(first, out _);
            Assert.Equal(70, row.Hp);
            Assert.Equal("a", row.Note);
            Assert.Equal(first, row.Id);
            Assert.Equal(10, row.Code);

            // 値が同じキーを指定しても、索引は付け替えない
            Assert.True(table.Update(first, code: 10, team: 1, note: "hit"));
            row = ref table.FindRef(first, out _);
            Assert.Equal(70, row.Hp);
            Assert.Equal("hit", row.Note);

            Assert.Equal(50, table.FindRef(second, out _).Hp);
            Assert.True(table.TryFindByCode(10, out var byCode));
            Assert.Equal("hit", byCode.Note);
            Assert.Equal(2, CountTeam(table, 1));
        }

        [Fact]
        public void UpdateChangesKeysAndMutableFieldsTogether()
        {
            var table = new CharacterTable();
            Assert.True(table.TryInsert(new Character(10, 1, "g", 100, "a"), out var primaryKey));
            Assert.True(table.TryInsert(new Character(20, 2, "g", 100, "b"), out _));

            Assert.True(table.Update(primaryKey, code: 11, team: 2, hp: 5));
            Assert.False(table.TryFindByCode(10, out _));
            Assert.True(table.TryFindByCode(11, out var row));
            Assert.Equal(primaryKey, row.Id);
            Assert.Equal(5, row.Hp);
            Assert.Equal("a", row.Note);
            Assert.Equal(0, CountTeam(table, 1));
            Assert.Equal(2, CountTeam(table, 2));

            // Unique 制約に違反する場合は、他のフィールドも書き換えない
            Assert.False(table.Update(primaryKey, code: 20, hp: 999));
            Assert.Equal(5, table.FindRef(primaryKey, out _).Hp);
        }

        [Fact]
        public void UpdateMutableSecondaryKeyInPlace()
        {
            var table = new CharacterTable();
            Assert.True(table.TryInsert(new Character(10, 1, "g", 100, "a"), out var primaryKey));
            Assert.True(table.TryInsert(new Character(20, 1, "g", 100, "b"), out _));

            // readonly でない SecondaryKey (FK) の変更は、索引を付け替えてその場で書き込む
            Assert.True(table.Update(primaryKey, team: 2, group: "h", hp: 1));
            ref readonly var row = ref table.FindRef(primaryKey, out _);
            Assert.Equal(2, row.Team);
            Assert.Equal("h", row.Group);
            Assert.Equal(1, row.Hp);
            Assert.Equal(10, row.Code);
            Assert.Equal(1, CountTeam(table, 1));
            Assert.Equal(1, CountTeam(table, 2));
            Assert.True(table.TryFindFirstByGroup("h", out var byGroup));
            Assert.Equal(primaryKey, byGroup.Id);
            Assert.True(table.TryFindFirstByGroup("g", out var other));
            Assert.NotEqual(primaryKey, other.Id);

            // 索引の構造が変わるため、列挙中の FK の変更は例外になる
            Assert.Throws<InvalidOperationException>(() =>
            {
                foreach (ref readonly var _ in table.All()) table.Update(primaryKey, team: 3);
            });
        }

        [Fact]
        public void UpdateMutableFieldsDuringEnumeration()
        {
            var table = new CharacterTable();
            for (var i = 0; i < 10; i++) table.TryInsert(new Character(i, i % 2, "g", i, null), out _);

            // キーが変わらない Update は構造を変えないため、列挙中でも行える
            foreach (ref readonly var row in table.All()) table.Update(row.Id, hp: row.Hp + 100);

            foreach (ref readonly var row in table.All()) Assert.Equal(row.Code + 100, row.Hp);
        }

        [Fact]
        public void UpdateMissingPrimaryKey()
        {
            var table = new CharacterTable();
            Assert.True(table.TryInsert(new Character(10, 1, "g", 100, "a"), out var primaryKey));
            Assert.True(table.Remove(primaryKey));
            Assert.False(table.Update(primaryKey, hp: 1));
            Assert.False(table.Update(CharacterTable.InvalidPrimaryKey, hp: 1));
        }

        static int CountTeam(CharacterTable table, int team)
        {
            var n = 0;
            foreach (ref readonly var _ in table.FindAllByTeam(team)) n++;
            return n;
        }

        [Fact]
        public void HintNamesDoNotCollide()
        {
            var a = new HintName.A_BTable();
            var b = new HintName_A.BTable();
            var upper = new HintName.CaseRowTable();
            var lower = new HintName.caseRowTable();
            Assert.True(a.TryInsert(new HintName.A_B(1), out _));
            Assert.True(b.TryInsert(new HintName_A.B(2), out _));
            Assert.True(upper.TryInsert(new HintName.CaseRow(3), out _));
            Assert.True(lower.TryInsert(new HintName.caseRow(4), out _));
            Assert.Equal(1, a.Count + b.Count - upper.Count - lower.Count + 1);
        }
    }
}
