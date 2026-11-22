using System;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    public sealed class RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits>
        where TRow : struct
        where TPrimaryKey : unmanaged
        where TPrimaryKeyTraits : struct, IPrimaryKeyTraits<TPrimaryKey>
    {
        const int MinGrowLength = 4;
        const int MaxArrayLength = 0x7FFFFFC7;

        struct Slot
        {
            public int dense;
            public uint generation;
        }

        readonly int slotBits;
        readonly ulong slotMask;
        readonly int maxSlots;
        readonly uint maxGeneration;

        TRow[] dense;
        int[] denseToSlot;
        int count;

        Slot[] slots;
        int slotCount;
        int freeHead = -1;
        int retired;

        int version;

        public RowStore(int capacity, int slotBits)
        {
            var generationBits = default(TPrimaryKeyTraits).Bits - slotBits;
            if (slotBits < 1 || generationBits < 1 || generationBits > 32) throw new ArgumentOutOfRangeException(nameof(slotBits));

            this.slotBits = slotBits;
            slotMask = (1UL << slotBits) - 1;
            maxSlots = slotBits >= 31 ? MaxArrayLength : 1 << slotBits;
            maxGeneration = generationBits == 32 ? uint.MaxValue : (1u << generationBits) - 1;

            if (capacity < 0 || capacity > maxSlots) throw new ArgumentOutOfRangeException(nameof(capacity));
            dense = new TRow[capacity];
            denseToSlot = new int[capacity];
            slots = new Slot[capacity];
        }

        public int Count => count;

        public int Capacity => dense.Length;

        public int Version => version;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetIndex(TPrimaryKey primaryKey, out int index)
        {
            var value = default(TPrimaryKeyTraits).ToUInt64(primaryKey);
            var generation = value >> slotBits;
            var slot = value & slotMask;
            if (generation == 0 || slot >= (ulong)slotCount)
            {
                index = -1;
                return false;
            }

            ref var s = ref slots[(int)slot];
            if (s.generation != generation)
            {
                index = -1;
                return false;
            }

            index = s.dense;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TRow RowAt(int index)
        {
            return ref dense[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int IndexOf(TPrimaryKey primaryKey)
        {
            return slots[(int)(default(TPrimaryKeyTraits).ToUInt64(primaryKey) & slotMask)].dense;
        }

        public TPrimaryKey PrimaryKeyAt(int index)
        {
            var slot = denseToSlot[index];
            return MakePrimaryKey(slots[slot].generation, slot);
        }

        public bool TryReserve(int required)
        {
            if (required < 0) throw new ArgumentOutOfRangeException(nameof(required));

            var freeSlots = slotCount - count - retired;
            var newSlots = Math.Max(0, required - freeSlots);
            if ((long)slotCount + newSlots > maxSlots) return false;

            EnsureCapacity(ref dense, count + required);
            EnsureCapacity(ref denseToSlot, count + required);
            EnsureCapacity(ref slots, slotCount + newSlots);
            return true;
        }

        public TPrimaryKey Store(in TRow row)
        {
            int slot;
            if (freeHead >= 0)
            {
                slot = freeHead;
                freeHead = slots[slot].dense;
            }
            else
            {
                slot = slotCount++;
                slots[slot].generation = 1;
            }

            dense[count] = row;
            denseToSlot[count] = slot;
            slots[slot].dense = count;
            count++;
            version++;
            return MakePrimaryKey(slots[slot].generation, slot);
        }

        public void RemoveAt(int index)
        {
            var slot = denseToSlot[index];
            var last = count - 1;
            if (index != last)
            {
                dense[index] = dense[last];
                denseToSlot[index] = denseToSlot[last];
                slots[denseToSlot[index]].dense = index;
            }

            dense[last] = default;
            count = last;

            ref var s = ref slots[slot];
            if (s.generation < maxGeneration)
            {
                s.generation++;
                s.dense = freeHead;
                freeHead = slot;
            }
            else
            {
                s.generation = 0;
                retired++;
            }

            version++;
        }

        public void Touch()
        {
            version++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CheckVersion(int expected)
        {
            if (expected != version) ThrowModified();
        }

        public void TrimExcess()
        {
            Array.Resize(ref dense, count);
            Array.Resize(ref denseToSlot, count);
            Array.Resize(ref slots, slotCount);
            version++;
        }

        public AllEnumerable All()
        {
            return new AllEnumerable(this);
        }

        public IndexEnumerable<TPrimaryKeyEnumerator> Enumerate<TPrimaryKeyEnumerator>(TPrimaryKeyEnumerator primaryKeys) where TPrimaryKeyEnumerator : struct, IPrimaryKeyEnumerator<TPrimaryKey>
        {
            return new IndexEnumerable<TPrimaryKeyEnumerator>(this, primaryKeys);
        }

        TPrimaryKey MakePrimaryKey(uint generation, int slot)
        {
            return default(TPrimaryKeyTraits).FromUInt64(((ulong)generation << slotBits) | (uint)slot);
        }

        void EnsureCapacity<T>(ref T[] array, int required)
        {
            if (required <= array.Length) return;
            var newLength = (int)Math.Min(Math.Max(Math.Max((long)array.Length * 2, MinGrowLength), required), maxSlots);
            Array.Resize(ref array, newLength);
        }

        static void ThrowModified()
        {
            throw new InvalidOperationException("The table was modified during enumeration.");
        }

        public readonly struct AllEnumerable
        {
            readonly RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store;

            internal AllEnumerable(RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store)
            {
                this.store = store;
            }

            public AllEnumerator GetEnumerator()
            {
                return new AllEnumerator(store);
            }
        }

        public struct AllEnumerator
        {
            readonly RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store;
            readonly int version;
            int index;

            internal AllEnumerator(RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store)
            {
                this.store = store;
                version = store.version;
                index = -1;
            }

            public ref readonly TRow Current => ref store.dense[index];

            public TPrimaryKey CurrentPrimaryKey => store.PrimaryKeyAt(index);

            public bool MoveNext()
            {
                store.CheckVersion(version);
                return ++index < store.count;
            }
        }

        public readonly struct IndexEnumerable<TPrimaryKeyEnumerator> where TPrimaryKeyEnumerator : struct, IPrimaryKeyEnumerator<TPrimaryKey>
        {
            readonly RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store;
            readonly TPrimaryKeyEnumerator primaryKeys;
            readonly int version;

            internal IndexEnumerable(RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store, TPrimaryKeyEnumerator primaryKeys)
            {
                this.store = store;
                this.primaryKeys = primaryKeys;
                version = store.version;
            }

            public IndexEnumerator<TPrimaryKeyEnumerator> GetEnumerator()
            {
                return new IndexEnumerator<TPrimaryKeyEnumerator>(store, primaryKeys, version);
            }
        }

        public struct IndexEnumerator<TPrimaryKeyEnumerator> where TPrimaryKeyEnumerator : struct, IPrimaryKeyEnumerator<TPrimaryKey>
        {
            readonly RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store;
            readonly int version;
            TPrimaryKeyEnumerator primaryKeys;
            int index;

            internal IndexEnumerator(RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store, TPrimaryKeyEnumerator primaryKeys, int version)
            {
                this.store = store;
                this.primaryKeys = primaryKeys;
                this.version = version;
                index = -1;
            }

            public ref readonly TRow Current => ref store.dense[index];

            public TPrimaryKey CurrentPrimaryKey => primaryKeys.Current;

            public bool MoveNext()
            {
                store.CheckVersion(version);
                if (!primaryKeys.MoveNext()) return false;
                index = store.IndexOf(primaryKeys.Current);
                return true;
            }
        }
    }
}
