using System;
using System.Collections.Generic;

namespace OrdoMemory.Sample
{
    // 世代付きハンドル + sparse set によるテーブル.
    // PK = (世代 << SlotBits) | スロット番号. Record は dense に隙間無く並べ、削除は末尾との入れ替えで行う.
    // 世代は 1 から始めるため、PK = 0 (default) は払い出されず、無効値として使える.
    public sealed class SampleTable
    {
        const int SlotBits = 22;
        const uint SlotMask = (1u << SlotBits) - 1;
        const int MaxSlots = (int)SlotMask + 1;
        const uint MaxGen = (1u << (32 - SlotBits)) - 1;
        const int MinGrowLength = 4;

        public const uint InvalidPk = 0;

        struct Slot
        {
            public int Dense;   // 使用中: dense の位置 / 空き: 次の空きスロット
            public uint Gen;    // 0: 未使用 / 使用を止めた. どの PK とも一致しない
        }

        // 生存している Record だけを隙間無く並べる. 順序は保証しない.
        SampleRow[] dense;
        int[] denseToSlot;
        int count;

        // スロット番号 → dense の位置と世代.
        Slot[] slots;
        int slotCount;
        int freeHead = -1;

        // 世代が一周して使用を止めたスロットの数.
        int retired;

        // Name の索引. 実装は外部から渡す.
        readonly SecondaryIndex<string> nameIndex;

        // Insert / Remove で進める. 列挙中の変更を検出する.
        int version;

        public int Count => count;

        public int Capacity => dense.Length;

        public SampleTable(int capacity, SecondaryIndex<string> nameIndex)
        {
            if (capacity < 0 || capacity > MaxSlots) throw new ArgumentOutOfRangeException(nameof(capacity));

            this.nameIndex = nameIndex ?? throw new ArgumentNullException(nameof(nameIndex));
            dense = new SampleRow[capacity];
            denseToSlot = new int[capacity];
            slots = new Slot[capacity];
        }

        public bool TryInsert(in SampleRow row, out uint pk)
        {
            if (!TryReserve(1))
            {
                pk = InvalidPk;
                return false;
            }

            pk = Store(row);
            nameIndex.Insert(row.Name, pk);
            return true;
        }

        // 払い出した PK は pks の先頭から書き込む. 確保できない場合は何も挿入しない.
        public bool TryBulkInsert(ReadOnlySpan<SampleRow> rows, Span<uint> pks)
        {
            if (pks.Length < rows.Length) throw new ArgumentException("pks is shorter than rows.", nameof(pks));
            if (!TryReserve(rows.Length)) return false;

            // 実装では ArrayPool から借りる.
            var entries = new KeyValuePair<string, uint>[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                pks[i] = Store(rows[i]);
                entries[i] = new KeyValuePair<string, uint>(rows[i].Name, pks[i]);
            }

            nameIndex.AddRange(entries);
            return true;
        }

        public bool TryFind(uint pk, out SampleRow row)
        {
            if (TryGetDense(pk, out var index))
            {
                row = dense[index];
                return true;
            }

            row = default;
            return false;
        }

        // 見つからない場合は found = false とし、ダミーへの参照を返す.
        // 参照は次の Insert / Remove まで有効.
        public ref readonly SampleRow FindRef(uint pk, out bool found)
        {
            found = TryGetDense(pk, out var index);
            return ref found ? ref dense[index] : ref NotFound;
        }

        static readonly SampleRow NotFound = default;

        // 指定した項目だけを更新する. アロケーションは発生しない.
        public bool Update(uint pk, Optional<string> name = default, Optional<string> description = default)
        {
            if (!TryGetDense(pk, out var index)) return false;

            // SecondaryKey が変わらない場合は、索引を更新しない. 索引は書き換え前の Name で引く.
            var current = dense[index];
            var newName = name.OrElse(current.Name);
            if (!string.Equals(current.Name, newName, StringComparison.Ordinal))
            {
                nameIndex.Remove(current.Name, pk);
                nameIndex.Insert(newName, pk);
            }

            dense[index] = new SampleRow(newName, description.OrElse(current.Description));
            return true;
        }

        // 末尾の Record を空いた位置へ移す. 移した Record への参照は無効になる.
        public bool Remove(uint pk)
        {
            if (!TryGetDense(pk, out var index)) return false;

            nameIndex.Remove(dense[index].Name, pk);

            var last = count - 1;
            if (index != last)
            {
                dense[index] = dense[last];
                denseToSlot[index] = denseToSlot[last];
                slots[denseToSlot[index]].Dense = index;
            }

            // Record が持つ参照を GC に解放させる.
            dense[last] = default;
            count = last;

            // 世代を進めて空きリストへ戻す. 一周したスロットは古い PK と区別できないため、使用を止める.
            var slot = (int)(pk & SlotMask);
            ref var s = ref slots[slot];
            if (s.Gen < MaxGen)
            {
                s.Gen++;
                s.Dense = freeHead;
                freeHead = slot;
            }
            else
            {
                s.Gen = 0;
                retired++;
            }

            version++;
            return true;
        }

        public AllEnumerable All()
        {
            return new AllEnumerable(this);
        }

        // 索引上の先頭 (SecondaryKey が等しいものの中では PK の昇順で先頭).
        public bool TryFindFirstByName(string name, out SampleRow row)
        {
            foreach (var pk in nameIndex.Equal(name)) return TryFind(pk, out row);

            row = default;
            return false;
        }

        public NameEnumerable FindAllByName(string name)
        {
            return new NameEnumerable(this, nameIndex.Equal(name));
        }

        public NameEnumerable FindRangeByName(string min, string max)
        {
            return new NameEnumerable(this, nameIndex.Range(min, max));
        }

        // 余った容量を返す. スロット表は使用済みの分 (slotCount) までしか縮めない.
        public void TrimExcess()
        {
            Array.Resize(ref dense, count);
            Array.Resize(ref denseToSlot, count);
            Array.Resize(ref slots, slotCount);
        }

        // ---- 列挙 ----

        public readonly struct AllEnumerable
        {
            readonly SampleTable table;

            internal AllEnumerable(SampleTable table)
            {
                this.table = table;
            }

            public AllEnumerator GetEnumerator()
            {
                return new AllEnumerator(table);
            }
        }

        // dense を先頭から読む. 順序は保証しない.
        public struct AllEnumerator
        {
            readonly SampleTable table;
            readonly int version;
            int index;

            internal AllEnumerator(SampleTable table)
            {
                this.table = table;
                version = table.version;
                index = -1;
            }

            public ref readonly SampleRow Current => ref table.dense[index];

            public uint CurrentPk => table.PkOf(index);

            public bool MoveNext()
            {
                if (version != table.version) throw new InvalidOperationException("The table was modified during enumeration.");
                return ++index < table.count;
            }
        }

        public readonly struct NameEnumerable
        {
            readonly SampleTable table;
            readonly IEnumerable<uint> pks;

            internal NameEnumerable(SampleTable table, IEnumerable<uint> pks)
            {
                this.table = table;
                this.pks = pks;
            }

            public NameEnumerator GetEnumerator()
            {
                return new NameEnumerator(table, pks.GetEnumerator());
            }
        }

        // 索引が返す PK を Record に解決する. 索引の PK は常に生存しているため、存在確認は行わない.
        public struct NameEnumerator
        {
            readonly SampleTable table;
            readonly IEnumerator<uint> pks;
            int index;

            internal NameEnumerator(SampleTable table, IEnumerator<uint> pks)
            {
                this.table = table;
                this.pks = pks;
                index = -1;
            }

            public ref readonly SampleRow Current => ref table.dense[index];

            public uint CurrentPk => pks.Current;

            public bool MoveNext()
            {
                if (!pks.MoveNext()) return false;
                index = table.slots[(int)(pks.Current & SlotMask)].Dense;
                return true;
            }
        }

        // ---- 内部 ----

        // count 件分の dense とスロットを確保する. 確保できない場合は何も変更しない.
        bool TryReserve(int required)
        {
            // 空きリストで足りない分は、新しいスロットを使う.
            var freeSlots = slotCount - count - retired;
            var newSlots = Math.Max(0, required - freeSlots);
            if ((long)slotCount + newSlots > MaxSlots) return false;

            EnsureCapacity(ref dense, count + required);
            EnsureCapacity(ref denseToSlot, count + required);
            EnsureCapacity(ref slots, slotCount + newSlots);
            return true;
        }

        // 事前に TryReserve で確保済みであること.
        uint Store(in SampleRow row)
        {
            int slot;
            if (freeHead >= 0)
            {
                slot = freeHead;
                freeHead = slots[slot].Dense;
            }
            else
            {
                slot = slotCount++;
                slots[slot].Gen = 1;
            }

            dense[count] = row;
            denseToSlot[count] = slot;
            slots[slot].Dense = count;
            count++;
            version++;
            return MakePk(slots[slot].Gen, slot);
        }

        bool TryGetDense(uint pk, out int index)
        {
            // 世代 0 の PK は払い出していない. 使用を止めたスロット (Gen = 0) との一致もここで弾く.
            var gen = pk >> SlotBits;
            var slot = pk & SlotMask;
            if (gen == 0 || slot >= (uint)slotCount || slots[slot].Gen != gen)
            {
                index = -1;
                return false;
            }

            index = slots[slot].Dense;
            return true;
        }

        uint PkOf(int index)
        {
            var slot = denseToSlot[index];
            return MakePk(slots[slot].Gen, slot);
        }

        static uint MakePk(uint gen, int slot)
        {
            return (gen << SlotBits) | (uint)slot;
        }

        static void EnsureCapacity<T>(ref T[] array, int required)
        {
            if (required <= array.Length) return;
            var newLength = (int)Math.Min(Math.Max(Math.Max((long)array.Length * 2, MinGrowLength), required), MaxSlots);
            Array.Resize(ref array, newLength);
        }
    }
}
