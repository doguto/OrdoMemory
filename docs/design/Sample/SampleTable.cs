using System;
using System.Collections.Generic;

namespace OrdoMemory.Sample
{
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
            public int Dense;
            public uint Gen;
        }

        SampleRow[] dense;
        int[] denseToSlot;
        int count;

        Slot[] slots;
        int slotCount;
        int freeHead = -1;

        int retired;

        readonly SecondaryIndex<string> nameIndex;

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

        public bool TryBulkInsert(ReadOnlySpan<SampleRow> rows, Span<uint> pks)
        {
            if (pks.Length < rows.Length) throw new ArgumentException("pks is shorter than rows.", nameof(pks));
            if (!TryReserve(rows.Length)) return false;

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

        public ref readonly SampleRow FindRef(uint pk, out bool found)
        {
            found = TryGetDense(pk, out var index);
            return ref found ? ref dense[index] : ref NotFound;
        }

        static readonly SampleRow NotFound = default;

        public bool Update(uint pk, Optional<string> name = default, Optional<string> description = default)
        {
            if (!TryGetDense(pk, out var index)) return false;

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

            dense[last] = default;
            count = last;

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

        public void TrimExcess()
        {
            Array.Resize(ref dense, count);
            Array.Resize(ref denseToSlot, count);
            Array.Resize(ref slots, slotCount);
        }

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

        bool TryReserve(int required)
        {
            var freeSlots = slotCount - count - retired;
            var newSlots = Math.Max(0, required - freeSlots);
            if ((long)slotCount + newSlots > MaxSlots) return false;

            EnsureCapacity(ref dense, count + required);
            EnsureCapacity(ref denseToSlot, count + required);
            EnsureCapacity(ref slots, slotCount + newSlots);
            return true;
        }

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
