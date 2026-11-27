using System;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    public sealed partial class RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits>
        where TRow : struct, IRow<TRow, TPrimaryKey>
        where TPrimaryKey : unmanaged
        where TPrimaryKeyTraits : struct, IPrimaryKeyTraits<TPrimaryKey>
    {
        const int MinGrowLength = 4; // データ配列の長さ更新時の更新量の下限
        const int MaxArrayLength = 0x7FFFFFC7;

        // 生データを示す dense 配列の Index を持つ構造体
        struct Slot
        {
            public int dense;
            public uint generation;
        }

        readonly int slotBits;   // slot に割り当てる bit 数
        readonly ulong slotMask; // pk & slotMask で pk から slot 部を取り出す
        readonly int maxSlots;
        readonly uint maxGeneration;

        // 実データ
        TRow[] dense;
        int denseCount;

        // 主キーの Slot 部から dense 配列上の位置を求める中間配列
        // 空き Slot を指す 連結リストも兼ねる
        Slot[] slots;
        int slotCount;

        // 空き Slot の Index を指す
        // freeHead で指された Index の slots[] から、 free 部の連結リストになっている
        int freeHead = -1;

        // Generation を最大まで使い切り使用不可となった slot 数
        int retiredSlotCount;

        // 変更検知用の値. 変更をかける度に Increment
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
            slots = new Slot[capacity];
        }

        public int Count => denseCount;

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
        public ref TRow GetRowRef(int denseIndex)
        {
            return ref dense[denseIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(TPrimaryKey primaryKey)
        {
            return slots[(int)(default(TPrimaryKeyTraits).ToUInt64(primaryKey) & slotMask)].dense;
        }

        // Insert 前に挿入する個数分の空き容量があることを確認する
        public bool TryReserve(int required)
        {
            if (required < 0) throw new ArgumentOutOfRangeException(nameof(required));

            var freeSlotCount = slotCount - denseCount - retiredSlotCount;
            var newSlotCount = Math.Max(0, required - freeSlotCount);
            if ((long)slotCount + newSlotCount > maxSlots) return false;

            EnsureCapacity(ref dense, denseCount + required);
            EnsureCapacity(ref slots, slotCount + newSlotCount);
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

            var primaryKey = MakePrimaryKey(slots[slot].generation, slot);
            dense[denseCount] = row.WithPrimaryKey(primaryKey);
            slots[slot].dense = denseCount;
            denseCount++;
            version++;
            return primaryKey;
        }

        public void Remove(int denseIndex)
        {
            var slotIndex = SlotIndexOf(denseIndex);
            var last = denseCount - 1;
            if (denseIndex != last)
            {
                // dense 配列を常に密にするため、末尾要素を削除により空いた箇所にコピーする
                dense[denseIndex] = dense[last];
                slots[SlotIndexOf(denseIndex)].dense = denseIndex;
            }

            dense[last] = default;
            denseCount = last;

            // 削除した Record に割り当てられていた Slot の更新
            ref var slot = ref slots[slotIndex];
            if (slot.generation < maxGeneration)
            {
                slot.generation++;
                slot.dense = freeHead;
                freeHead = slotIndex;
            }
            else
            {
                // Generation が飽和していた場合は使用不可にする
                slot.generation = 0;
                retiredSlotCount++;
            }

            version++;
        }

        // dense の ref を使用して外から更新する場合は、 Touch() で version を明示的に更新する
        public void Touch()
        {
            version++;
        }

        public void TrimExcess()
        {
            Array.Resize(ref dense, denseCount);
            Array.Resize(ref slots, slotCount);
            version++;
        }

        // Record が持つ主キーの slot 部から、Record を指している slot を求める
        int SlotIndexOf(int denseIndex)
        {
            return (int)(default(TPrimaryKeyTraits).ToUInt64(dense[denseIndex].PrimaryKey) & slotMask);
        }

        TPrimaryKey MakePrimaryKey(uint generation, int slot)
        {
            return default(TPrimaryKeyTraits).FromUInt64(((ulong)generation << slotBits) | (uint)slot);
        }

        void EnsureCapacity<T>(ref T[] array, int required)
        {
            if (required <= array.Length) return;

            var doubled = Math.Max((long)array.Length * 2, MinGrowLength);
            var newLength = Math.Clamp(doubled, required, maxSlots);
            Array.Resize(ref array, (int)newLength);
        }
    }
}
