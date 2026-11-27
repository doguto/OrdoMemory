using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed partial class RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits>
        where TRow : struct, IRow<TRow, TPrimaryKey>
        where TPrimaryKey : unmanaged
        where TPrimaryKeyTraits : struct, IPrimaryKeyTraits<TPrimaryKey>
    {
        const int MinGrowLength = 4; // データ配列の長さ更新時の更新量の下限
        // .NET の Array.MaxLength (1 次元配列の要素数の上限) と同じ値. netstandard2.1 には Array.MaxLength が無いため定数で持つ
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

        // これまでに Store で払い出した PK の数. 生涯で払い出せる PK の残数の計算に使う
        ulong storedCount;

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

        // 削除を挟まずに、今から続けて Insert できる件数
        // 未使用の slot と空きリスト上の slot の合計. 世代を使い切った slot は含まない
        public int AvailableSlotCount => maxSlots - denseCount - retiredSlotCount;

        // 世代を使い切り、使用を止めた slot の数
        public int RetiredSlotCount => retiredSlotCount;

        // 生存中の Record を削除して slot を再利用する場合も含め、今後払い出せる PK の残数 (生涯の Insert 回数の残り)
        // 各 slot は世代 1..maxGeneration の maxGeneration 回だけ使えるため、maxSlots * maxGeneration から払い出した数を引いた値になる
        // 0 になると、テーブルが空でも Insert は失敗する
        public ulong RemainingInsertCount => (ulong)maxSlots * maxGeneration - storedCount;

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

        // 格納済みの PK から dense の位置を求める. 格納されていない PK (削除済み・無効値) は例外にする
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(TPrimaryKey primaryKey)
        {
            var value = default(TPrimaryKeyTraits).ToUInt64(primaryKey);
            ref var s = ref slots[(int)(value & slotMask)];
            if (s.generation != value >> slotBits || s.generation == 0) ThrowHelper.ThrowInvalidPrimaryKey(nameof(primaryKey));
            return s.dense;
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

        // 事前に TryReserve で容量を確保しておくこと
        public TPrimaryKey Store(in TRow row)
        {
            if (denseCount >= dense.Length || (freeHead < 0 && slotCount >= slots.Length)) ThrowHelper.ThrowNotReserved();

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
            storedCount++;
            version++;
            return primaryKey;
        }

        public void Remove(int denseIndex)
        {
            if ((uint)denseIndex >= (uint)denseCount) ThrowHelper.ThrowArgumentOutOfRange(nameof(denseIndex));

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
