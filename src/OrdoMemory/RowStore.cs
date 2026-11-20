using System;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    /// <summary>
    /// 世代付きハンドル + sparse set. PK = (世代 &lt;&lt; slotBits) | スロット番号.
    /// 世代は 1 から始めるため、PK = 0 (default) は払い出されない.
    /// 生成コードから使う. 二次索引の整合は呼び出し側の責務.
    /// </summary>
    public sealed class RowStore<TRow, TPk, TPkTraits>
        where TRow : struct
        where TPk : unmanaged
        where TPkTraits : struct, IPkTraits<TPk>
    {
        const int MinGrowLength = 4;
        const int MaxArrayLength = 0x7FFFFFC7;

        struct Slot
        {
            public int Dense;   // 使用中: dense の位置 / 空き: 次の空きスロット
            public uint Gen;    // 0: 未使用 / 使用を止めた
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

        // 生存が保証された PK (索引が返したもの) にのみ使う.
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

        // required 件分の dense とスロットを確保する. 確保できない場合は何も変更しない.
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

        // 事前に TryReserve で確保済みであること.
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

        // 末尾の Record を index へ移す. 移した Record への参照は無効になる.
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

            // Record が持つ参照を GC に解放させる.
            dense[last] = default;
            count = last;

            // 一周したスロットは古い PK と区別できないため、使用を止める (ABA 対策).
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

        // 索引の構造が変わったことを列挙子に知らせる.
        public void Touch()
        {
            version++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CheckVersion(int expected)
        {
            if (expected != version) ThrowModified();
        }

        // スロット表は使用済みの分 (slotCount) までしか縮めない.
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

        // 順序は保証しない (Remove のたびに変わる).
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

        // 索引が返す PK を Record に解決する. 索引の PK は常に生存しているため、世代の確認は行わない.
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
