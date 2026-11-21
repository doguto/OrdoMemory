using System;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    public sealed class RowStore<TRow, TPk, TPkTraits>
        where TRow : struct
        where TPk : unmanaged
        where TPkTraits : struct, IPkTraits<TPk>
    {
        const int MinGrowLength = 4;
        const int MaxArrayLength = 0x7FFFFFC7;

        struct Slot
        {
            public int Dense;
            public uint Gen;
        }

        readonly int slotBits;
        readonly ulong slotMask;
        readonly int maxSlots;
        readonly uint maxGen;

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
            var genBits = default(TPkTraits).Bits - slotBits;
            if (slotBits < 1 || genBits < 1 || genBits > 32) throw new ArgumentOutOfRangeException(nameof(slotBits));

            this.slotBits = slotBits;
            slotMask = (1UL << slotBits) - 1;
            maxSlots = slotBits >= 31 ? MaxArrayLength : 1 << slotBits;
            maxGen = genBits == 32 ? uint.MaxValue : (1u << genBits) - 1;

            if (capacity < 0 || capacity > maxSlots) throw new ArgumentOutOfRangeException(nameof(capacity));
            dense = new TRow[capacity];
            denseToSlot = new int[capacity];
            slots = new Slot[capacity];
        }

        public int Count => count;

        public int Capacity => dense.Length;

        public int Version => version;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetIndex(TPk pk, out int index)
        {
            var value = default(TPkTraits).ToUInt64(pk);
            var gen = value >> slotBits;
            var slot = value & slotMask;
            if (gen == 0 || slot >= (ulong)slotCount)
            {
                index = -1;
                return false;
            }

            ref var s = ref slots[(int)slot];
            if (s.Gen != gen)
            {
                index = -1;
                return false;
            }

            index = s.Dense;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TRow RowAt(int index)
        {
            return ref dense[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int IndexOf(TPk pk)
        {
            return slots[(int)(default(TPkTraits).ToUInt64(pk) & slotMask)].Dense;
        }

        public TPk PkAt(int index)
        {
            var slot = denseToSlot[index];
            return MakePk(slots[slot].Gen, slot);
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

        public TPk Store(in TRow row)
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

        public void RemoveAt(int index)
        {
            var slot = denseToSlot[index];
            var last = count - 1;
            if (index != last)
            {
                dense[index] = dense[last];
                denseToSlot[index] = denseToSlot[last];
                slots[denseToSlot[index]].Dense = index;
            }

            dense[last] = default;
            count = last;

            ref var s = ref slots[slot];
            if (s.Gen < maxGen)
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

        public IndexEnumerable<TInner> Enumerate<TInner>(TInner pks) where TInner : struct, IPkEnumerator<TPk>
        {
            return new IndexEnumerable<TInner>(this, pks);
        }

        TPk MakePk(uint gen, int slot)
        {
            return default(TPkTraits).FromUInt64(((ulong)gen << slotBits) | (uint)slot);
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
            readonly RowStore<TRow, TPk, TPkTraits> store;

            internal AllEnumerable(RowStore<TRow, TPk, TPkTraits> store)
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
            readonly RowStore<TRow, TPk, TPkTraits> store;
            readonly int version;
            int index;

            internal AllEnumerator(RowStore<TRow, TPk, TPkTraits> store)
            {
                this.store = store;
                version = store.version;
                index = -1;
            }

            public ref readonly TRow Current => ref store.dense[index];

            public TPk CurrentPk => store.PkAt(index);

            public bool MoveNext()
            {
                store.CheckVersion(version);
                return ++index < store.count;
            }
        }

        public readonly struct IndexEnumerable<TInner> where TInner : struct, IPkEnumerator<TPk>
        {
            readonly RowStore<TRow, TPk, TPkTraits> store;
            readonly TInner pks;
            readonly int version;

            internal IndexEnumerable(RowStore<TRow, TPk, TPkTraits> store, TInner pks)
            {
                this.store = store;
                this.pks = pks;
                version = store.version;
            }

            public IndexEnumerator<TInner> GetEnumerator()
            {
                return new IndexEnumerator<TInner>(store, pks, version);
            }
        }

        public struct IndexEnumerator<TInner> where TInner : struct, IPkEnumerator<TPk>
        {
            readonly RowStore<TRow, TPk, TPkTraits> store;
            readonly int version;
            TInner pks;
            int index;

            internal IndexEnumerator(RowStore<TRow, TPk, TPkTraits> store, TInner pks, int version)
            {
                this.store = store;
                this.pks = pks;
                this.version = version;
                index = -1;
            }

            public ref readonly TRow Current => ref store.dense[index];

            public TPk CurrentPk => pks.Current;

            public bool MoveNext()
            {
                store.CheckVersion(version);
                if (!pks.MoveNext()) return false;
                index = store.IndexOf(pks.Current);
                return true;
            }
        }
    }
}
