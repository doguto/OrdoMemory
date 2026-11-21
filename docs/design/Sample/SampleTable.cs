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

        public const uint InvalidPrimaryKey = 0;

        struct Slot
        {
            public int Dense;
            public uint Generation;
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

        public bool TryInsert(in SampleRow row, out uint primaryKey)
        {
            if (!TryReserve(1))
            {
                primaryKey = InvalidPrimaryKey;
                return false;
            }

            primaryKey = Store(row);
            nameIndex.Insert(row.Name, primaryKey);
            return true;
        }

        public bool TryBulkInsert(ReadOnlySpan<SampleRow> rows, Span<uint> primaryKeys)
        {
            if (primaryKeys.Length < rows.Length) throw new ArgumentException("primaryKeys is shorter than rows.", nameof(primaryKeys));
            if (!TryReserve(rows.Length)) return false;

            var entries = new KeyValuePair<string, uint>[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                primaryKeys[i] = Store(rows[i]);
                entries[i] = new KeyValuePair<string, uint>(rows[i].Name, primaryKeys[i]);
            }

            nameIndex.AddRange(entries);
            return true;
        }

        public bool TryFind(uint primaryKey, out SampleRow row)
        {
            if (TryGetDense(primaryKey, out var index))
            {
                row = dense[index];
                return true;
            }

            row = default;
            return false;
        }

        public ref readonly SampleRow FindRef(uint primaryKey, out bool found)
        {
            found = TryGetDense(primaryKey, out var index);
            return ref found ? ref dense[index] : ref NotFound;
        }

        static readonly SampleRow NotFound = default;

        public bool Update(uint primaryKey, Optional<string> name = default, Optional<string> description = default)
        {
            if (!TryGetDense(primaryKey, out var index)) return false;

            var current = dense[index];
            var newName = name.OrElse(current.Name);
            if (!string.Equals(current.Name, newName, StringComparison.Ordinal))
            {
                nameIndex.Remove(current.Name, primaryKey);
                nameIndex.Insert(newName, primaryKey);
            }

            dense[index] = new SampleRow(newName, description.OrElse(current.Description));
            return true;
        }

        public bool Remove(uint primaryKey)
        {
            if (!TryGetDense(primaryKey, out var index)) return false;

            nameIndex.Remove(dense[index].Name, primaryKey);

            var last = count - 1;
            if (index != last)
            {
                dense[index] = dense[last];
                denseToSlot[index] = denseToSlot[last];
                slots[denseToSlot[index]].Dense = index;
            }

            dense[last] = default;
            count = last;

            var slot = (int)(primaryKey & SlotMask);
            ref var s = ref slots[slot];
            if (s.Generation < MaxGen)
            {
                s.Generation++;
                s.Dense = freeHead;
                freeHead = slot;
            }
            else
            {
                s.Generation = 0;
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
            foreach (var primaryKey in nameIndex.Equal(name)) return TryFind(primaryKey, out row);

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

            public uint CurrentPrimaryKey => table.PrimaryKeyOf(index);

            public bool MoveNext()
            {
                if (version != table.version) throw new InvalidOperationException("The table was modified during enumeration.");
                return ++index < table.count;
            }
        }

        public readonly struct NameEnumerable
        {
            readonly SampleTable table;
            readonly IEnumerable<uint> primaryKeys;

            internal NameEnumerable(SampleTable table, IEnumerable<uint> primaryKeys)
            {
                this.table = table;
                this.primaryKeys = primaryKeys;
            }

            public NameEnumerator GetEnumerator()
            {
                return new NameEnumerator(table, primaryKeys.GetEnumerator());
            }
        }

        public struct NameEnumerator
        {
            readonly SampleTable table;
            readonly IEnumerator<uint> primaryKeys;
            int index;

            internal NameEnumerator(SampleTable table, IEnumerator<uint> primaryKeys)
            {
                this.table = table;
                this.primaryKeys = primaryKeys;
                index = -1;
            }

            public ref readonly SampleRow Current => ref table.dense[index];

            public uint CurrentPrimaryKey => primaryKeys.Current;

            public bool MoveNext()
            {
                if (!primaryKeys.MoveNext()) return false;
                index = table.slots[(int)(primaryKeys.Current & SlotMask)].Dense;
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
                slots[slot].Generation = 1;
            }

            dense[count] = row;
            denseToSlot[count] = slot;
            slots[slot].Dense = count;
            count++;
            version++;
            return MakePrimaryKey(slots[slot].Generation, slot);
        }

        bool TryGetDense(uint primaryKey, out int index)
        {
            var generation = primaryKey >> SlotBits;
            var slot = primaryKey & SlotMask;
            if (generation == 0 || slot >= (uint)slotCount || slots[slot].Generation != generation)
            {
                index = -1;
                return false;
            }

            index = slots[slot].Dense;
            return true;
        }

        uint PrimaryKeyOf(int index)
        {
            var slot = denseToSlot[index];
            return MakePrimaryKey(slots[slot].Generation, slot);
        }

        static uint MakePrimaryKey(uint generation, int slot)
        {
            return (generation << SlotBits) | (uint)slot;
        }

        static void EnsureCapacity<T>(ref T[] array, int required)
        {
            if (required <= array.Length) return;
            var newLength = (int)Math.Min(Math.Max(Math.Max((long)array.Length * 2, MinGrowLength), required), MaxSlots);
            Array.Resize(ref array, newLength);
        }
    }
}
