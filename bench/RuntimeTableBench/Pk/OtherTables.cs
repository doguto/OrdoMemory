using System;
using System.Collections.Generic;

namespace OrdoMemory.Bench.Pk
{
    // B: 連続した 1 本の配列 + tombstone + rank テーブル.
    // Key 64 個ごとに「スロットを持つ Key のビットマップ」と「そのブロックの先頭スロット」を持ち、Key → スロットを O(1) で引く.
    // 削除は tombstone. 閾値 0.3 で全体コンパクションし、rank テーブルも作り直す.
    public sealed class RankTable : IPkTable
    {
        const double Threshold = 0.3;

        readonly int start;
        Row[] rows = new Row[16];
        ulong[] alive = new ulong[1];
        int used;
        int deleted;

        // present[i] / rankBase[i] はブロック番号 (baseBlock + i) を担当する.
        ulong[] present = new ulong[4];
        int[] rankBase = new int[4];
        int blocksUsed;
        long baseBlock;

        long nextKey;

        public RankTable(int start = 0)
        {
            this.start = start;
            nextKey = start;
        }

        public string Name => "B  Rank table + tombstone";
        public int Count => used - deleted;
        public bool SupportsRange => true;

        public bool Insert(in Row row, out int key)
        {
            key = 0;
            if (nextKey > int.MaxValue) return false;

            var offset = nextKey - start;
            var b = (int)((offset >> 6) - baseBlock);
            if (b == blocksUsed)
            {
                if (blocksUsed == present.Length)
                {
                    Array.Resize(ref present, blocksUsed * 2);
                    Array.Resize(ref rankBase, blocksUsed * 2);
                }

                present[b] = 0;
                rankBase[b] = used;
                blocksUsed++;
            }

            if (used == rows.Length)
            {
                Array.Resize(ref rows, rows.Length * 2);
                Array.Resize(ref alive, (rows.Length + 63) >> 6);
            }

            key = (int)nextKey++;
            rows[used] = row;
            rows[used].Id = key;
            alive[used >> 6] |= 1UL << (used & 63);
            present[b] |= 1UL << (int)(offset & 63);
            used++;
            return true;
        }

        public int GetScore(int key)
        {
            return TryGetAliveSlot(key, out var slot) ? rows[slot].Score : -1;
        }

        public bool UpdateScore(int key, int score)
        {
            if (!TryGetAliveSlot(key, out var slot)) return false;
            rows[slot].Score = score;
            return true;
        }

        public bool Remove(int key)
        {
            if (!TryGetAliveSlot(key, out var slot)) return false;
            alive[slot >> 6] &= ~(1UL << (slot & 63));
            deleted++;
            if ((double)deleted / used >= Threshold) Compact();
            return true;
        }

        public long SumAll()
        {
            long sum = 0;
            var words = (used + 63) >> 6;
            for (var w = 0; w < words; w++)
            {
                var m = alive[w];
                var baseSlot = w << 6;
                if (m == ~0UL)
                {
                    for (var i = 0; i < 64; i++) sum += rows[baseSlot + i].Score;
                    continue;
                }

                while (m != 0)
                {
                    sum += rows[baseSlot + Bits.TrailingZeroCount(m)].Score;
                    m &= m - 1;
                }
            }

            return sum;
        }

        public long SumRange(int min, int max)
        {
            long sum = 0;
            var lo = Math.Max((long)min - start, baseBlock << 6);
            var hi = Math.Min((long)max - start, nextKey - start - 1);
            if (lo > hi) return 0;

            var first = (int)((lo >> 6) - baseBlock);
            var last = (int)((hi >> 6) - baseBlock);
            for (var b = first; b <= last; b++)
            {
                var range = ~0UL;
                if (b == first) range &= Bits.AtOrAbove((int)(lo & 63));
                if (b == last && (hi & 63) != 63) range &= Bits.Below((int)(hi & 63) + 1);

                var m = present[b];
                var from = rankBase[b] + Bits.PopCount(m & ((range & (0UL - range)) - 1));
                var to = from + Bits.PopCount(m & range);
                for (var s = from; s < to; s++)
                {
                    if ((alive[s >> 6] >> (s & 63) & 1) != 0) sum += rows[s].Score;
                }
            }

            return sum;
        }

        bool TryGetAliveSlot(int key, out int slot)
        {
            slot = 0;
            var offset = (long)key - start;
            if ((ulong)offset >= (ulong)(nextKey - start)) return false;
            var b = (offset >> 6) - baseBlock;
            if ((ulong)b >= (ulong)blocksUsed) return false;

            var m = present[b];
            var bit = (int)(offset & 63);
            if ((m >> bit & 1) == 0) return false;
            slot = rankBase[b] + Bits.PopCount(m & Bits.Below(bit));
            return (alive[slot >> 6] >> (slot & 63) & 1) != 0;
        }

        public void Compact()
        {
            if (deleted == 0) return;

            var read = 0;
            var write = 0;
            for (var b = 0; b < blocksUsed; b++)
            {
                var m = present[b];
                var kept = 0UL;
                rankBase[b] = write;
                while (m != 0)
                {
                    var bit = m & (0UL - m);
                    m ^= bit;
                    if ((alive[read >> 6] >> (read & 63) & 1) != 0)
                    {
                        rows[write++] = rows[read];
                        kept |= bit;
                    }

                    read++;
                }

                present[b] = kept;
            }

            Array.Clear(rows, write, used - write);
            used = write;
            deleted = 0;

            // 生存スロットは先頭から詰まっている.
            Array.Clear(alive, 0, alive.Length);
            for (var w = 0; w < (write >> 6); w++) alive[w] = ~0UL;
            if ((write & 63) != 0) alive[write >> 6] = Bits.Below(write & 63);

            // 先頭側の空ブロックを切り詰める. 末尾ブロックは Insert の書き込み先なので残す.
            var trim = 0;
            while (trim < blocksUsed - 1 && present[trim] == 0) trim++;
            if (trim > 0)
            {
                Array.Copy(present, trim, present, 0, blocksUsed - trim);
                Array.Copy(rankBase, trim, rankBase, 0, blocksUsed - trim);
                blocksUsed -= trim;
                baseBlock += trim;
            }
        }
    }

    // D: 世代付きハンドル + sparse set (ECS 方式).
    // Key = (世代 << 22) | スロット. 削除は末尾との入れ替え. Key の順序は持たない.
    public sealed class SlotMapTable : IPkTable
    {
        const int SlotBits = 22;
        const int SlotMask = (1 << SlotBits) - 1;
        const int GenMask = (1 << (31 - SlotBits)) - 1;

        Row[] dense = new Row[16];
        int[] denseToSlot = new int[16];
        int count;

        int[] slotDense = new int[16];   // 使用中: dense の添字 / 空き: 次の空きスロット
        int[] slotGen = new int[16];
        int slotCount;
        int freeHead = -1;

        public string Name => "D  SlotMap + sparse set";
        public int Count => count;
        public bool SupportsRange => false;

        public bool Insert(in Row row, out int key)
        {
            int slot;
            if (freeHead >= 0)
            {
                slot = freeHead;
                freeHead = slotDense[slot];
            }
            else
            {
                if (slotCount > SlotMask)
                {
                    key = 0;
                    return false;
                }

                if (slotCount == slotDense.Length)
                {
                    Array.Resize(ref slotDense, slotCount * 2);
                    Array.Resize(ref slotGen, slotCount * 2);
                }

                slot = slotCount++;
            }

            if (count == dense.Length)
            {
                Array.Resize(ref dense, count * 2);
                Array.Resize(ref denseToSlot, count * 2);
            }

            key = (slotGen[slot] << SlotBits) | slot;
            dense[count] = row;
            dense[count].Id = key;
            denseToSlot[count] = slot;
            slotDense[slot] = count;
            count++;
            return true;
        }

        public int GetScore(int key)
        {
            var slot = key & SlotMask;
            if (slot >= slotCount || slotGen[slot] != (key >> SlotBits)) return -1;
            return dense[slotDense[slot]].Score;
        }

        public bool UpdateScore(int key, int score)
        {
            var slot = key & SlotMask;
            if (slot >= slotCount || slotGen[slot] != (key >> SlotBits)) return false;
            dense[slotDense[slot]].Score = score;
            return true;
        }

        public bool Remove(int key)
        {
            var slot = key & SlotMask;
            if (slot >= slotCount || slotGen[slot] != (key >> SlotBits)) return false;

            var d = slotDense[slot];
            var last = --count;
            dense[d] = dense[last];
            denseToSlot[d] = denseToSlot[last];
            slotDense[denseToSlot[d]] = d;
            dense[last] = default;

            slotGen[slot] = (slotGen[slot] + 1) & GenMask;
            slotDense[slot] = freeHead;
            freeHead = slot;
            return true;
        }

        public long SumAll()
        {
            long sum = 0;
            var d = dense;
            for (var i = 0; i < count; i++) sum += d[i].Score;
            return sum;
        }

        public long SumRange(int min, int max)
        {
            return 0;
        }
    }

    // E: Dictionary<int, Row>. 基準として.
    public sealed class DictionaryTable : IPkTable
    {
        readonly Dictionary<int, Row> map = new Dictionary<int, Row>();
        long nextKey;

        public string Name => "E  Dictionary<int,Row>";
        public int Count => map.Count;
        public bool SupportsRange => true;

        public bool Insert(in Row row, out int key)
        {
            key = 0;
            if (nextKey > int.MaxValue) return false;
            key = (int)nextKey++;
            var copy = row;
            copy.Id = key;
            map.Add(key, copy);
            return true;
        }

        public int GetScore(int key)
        {
            return map.TryGetValue(key, out var row) ? row.Score : -1;
        }

        public bool UpdateScore(int key, int score)
        {
            if (!map.TryGetValue(key, out var row)) return false;
            row.Score = score;
            map[key] = row;
            return true;
        }

        public bool Remove(int key)
        {
            return map.Remove(key);
        }

        public long SumAll()
        {
            long sum = 0;
            foreach (var pair in map) sum += pair.Value.Score;
            return sum;
        }

        public long SumRange(int min, int max)
        {
            long sum = 0;
            for (long k = min; k <= max; k++)
            {
                if (map.TryGetValue((int)k, out var row)) sum += row.Score;
            }

            return sum;
        }
    }
}
