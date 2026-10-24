using System;

namespace OrdoMemory.Bench.Pk
{
    // G: F の改良. ブロック情報 (Present / Alive / RankBase) を区画ごとではなく全体で 1 本の配列に置く.
    // Find が触るのは「ブロック情報 → 区画の Rows」だけになる (B と同じ回数).
    // Rows は区画 (Key 2048 個) ごとに持ち、削除は tombstone, コンパクションは区画単位.
    public sealed class SegmentedRankTable2 : IPkTable
    {
        const int SegmentBits = 11;
        const int BlockShift = SegmentBits - 6;
        const int BlocksPerSegment = 1 << BlockShift;
        const int MaxRows = 1 << SegmentBits;
        const double Threshold = 0.25;

        struct BlockInfo
        {
            public ulong Present;
            public ulong Alive;
            public int RankBase;
        }

        readonly int start;

        // blocks[blockHead + i] がブロック番号 (firstSegment * BlocksPerSegment + i) を担当する.
        BlockInfo[] blocks = new BlockInfo[BlocksPerSegment * 4];
        int blockHead;

        // 区画ごとの情報. segHead + i が区画番号 (firstSegment + i).
        Row[][] segRows = new Row[4][];
        int[] segUsed = new int[4];
        int[] segDeleted = new int[4];
        int segHead;
        int segmentCount;
        long firstSegment;

        long nextKey;
        int count;

        public SegmentedRankTable2(int start = 0)
        {
            this.start = start;
            nextKey = start;
        }

        public string Name => "G  Segmented rank v2 (2048)";
        public int Count => count;
        public bool SupportsRange => true;

        public bool Insert(in Row row, out int key)
        {
            key = 0;
            if (nextKey > int.MaxValue) return false;

            var offset = nextKey - start;
            var seg = (int)((offset >> SegmentBits) - firstSegment);
            if (seg == segmentCount) AppendSegment();

            var s = segHead + seg;
            ref var block = ref blocks[blockHead + (int)((offset >> 6) - (firstSegment << BlockShift))];
            var bit = (int)(offset & 63);
            var used = segUsed[s];
            if (bit == 0) block.RankBase = used;

            var rows = segRows[s];
            if (used == rows.Length)
            {
                Array.Resize(ref rows, Math.Min(rows.Length * 2, MaxRows));
                segRows[s] = rows;
            }

            key = (int)nextKey++;
            rows[used] = row;
            rows[used].Id = key;
            segUsed[s] = used + 1;
            block.Present |= 1UL << bit;
            block.Alive |= 1UL << bit;
            count++;
            return true;
        }

        public int GetScore(int key)
        {
            var offset = (long)key - start;
            if ((ulong)offset >= (ulong)(nextKey - start)) return -1;
            var b = (offset >> 6) - (firstSegment << BlockShift);
            if ((ulong)b >= (ulong)(segmentCount << BlockShift)) return -1;

            ref var block = ref blocks[blockHead + (int)b];
            var bit = (int)(offset & 63);
            if ((block.Alive >> bit & 1) == 0) return -1;
            return segRows[segHead + (int)(b >> BlockShift)][block.RankBase + Bits.PopCount(block.Present & Bits.Below(bit))].Score;
        }

        public bool UpdateScore(int key, int score)
        {
            var offset = (long)key - start;
            if ((ulong)offset >= (ulong)(nextKey - start)) return false;
            var b = (offset >> 6) - (firstSegment << BlockShift);
            if ((ulong)b >= (ulong)(segmentCount << BlockShift)) return false;

            ref var block = ref blocks[blockHead + (int)b];
            var bit = (int)(offset & 63);
            if ((block.Alive >> bit & 1) == 0) return false;
            segRows[segHead + (int)(b >> BlockShift)][block.RankBase + Bits.PopCount(block.Present & Bits.Below(bit))].Score = score;
            return true;
        }

        public bool Remove(int key)
        {
            var offset = (long)key - start;
            if ((ulong)offset >= (ulong)(nextKey - start)) return false;
            var b = (offset >> 6) - (firstSegment << BlockShift);
            if ((ulong)b >= (ulong)(segmentCount << BlockShift)) return false;

            ref var block = ref blocks[blockHead + (int)b];
            var bit = (int)(offset & 63);
            if ((block.Alive >> bit & 1) == 0) return false;

            block.Alive &= ~(1UL << bit);
            count--;
            var seg = (int)(b >> BlockShift);
            var s = segHead + seg;
            var deleted = ++segDeleted[s];
            var isTail = seg == segmentCount - 1;

            if (deleted == segUsed[s] && !isTail)
            {
                // 区画が空になった. Rows を手放し、ブロック情報を消す.
                segRows[s] = null;
                segUsed[s] = 0;
                segDeleted[s] = 0;
                Array.Clear(blocks, blockHead + (seg << BlockShift), BlocksPerSegment);
                TrimHead();
            }
            else if (deleted >= 64 && deleted >= segUsed[s] * Threshold)
            {
                CompactSegment(seg, isTail);
            }

            return true;
        }

        void CompactSegment(int seg, bool isTail)
        {
            var s = segHead + seg;
            var rows = segRows[s];
            var firstBlock = blockHead + (seg << BlockShift);
            var write = 0;
            for (var i = 0; i < BlocksPerSegment; i++)
            {
                ref var block = ref blocks[firstBlock + i];
                var present = block.Present;
                if (present == 0)
                {
                    block.RankBase = write;
                    continue;
                }

                var alive = block.Alive;
                var read = block.RankBase;
                block.RankBase = write;
                if (alive == present)
                {
                    var n = Bits.PopCount(present);
                    if (read != write) Array.Copy(rows, read, rows, write, n);
                    write += n;
                    continue;
                }

                while (present != 0)
                {
                    var bit = present & (0UL - present);
                    present ^= bit;
                    if ((alive & bit) != 0) rows[write++] = rows[read];
                    read++;
                }

                block.Present = alive;
            }

            Array.Clear(rows, write, segUsed[s] - write);
            segUsed[s] = write;
            segDeleted[s] = 0;

            if (!isTail && rows.Length > 64 && write * 2 < rows.Length)
            {
                var smaller = new Row[Math.Max(16, (write + 15) & ~15)];
                Array.Copy(rows, smaller, write);
                segRows[s] = smaller;
            }
        }

        public long SumAll()
        {
            long sum = 0;
            for (var seg = 0; seg < segmentCount; seg++)
            {
                var s = segHead + seg;
                var rows = segRows[s];
                if (rows == null) continue;
                if (segDeleted[s] == 0)
                {
                    var used = segUsed[s];
                    for (var i = 0; i < used; i++) sum += rows[i].Score;
                    continue;
                }

                var firstBlock = blockHead + (seg << BlockShift);
                for (var i = 0; i < BlocksPerSegment; i++)
                {
                    var present = blocks[firstBlock + i].Present;
                    var alive = blocks[firstBlock + i].Alive;
                    var slot = blocks[firstBlock + i].RankBase;
                    if (alive == present)
                    {
                        var end = slot + Bits.PopCount(present);
                        for (; slot < end; slot++) sum += rows[slot].Score;
                        continue;
                    }

                    while (present != 0)
                    {
                        var bit = present & (0UL - present);
                        present ^= bit;
                        if ((alive & bit) != 0) sum += rows[slot].Score;
                        slot++;
                    }
                }
            }

            return sum;
        }

        public long SumRange(int min, int max)
        {
            long sum = 0;
            var baseBlock = firstSegment << BlockShift;
            var lo = Math.Max((long)min - start, baseBlock << 6);
            var hi = Math.Min((long)max - start, nextKey - start - 1);
            if (lo > hi) return 0;

            var loBlock = lo >> 6;
            var hiBlock = hi >> 6;
            for (var blockNo = loBlock; blockNo <= hiBlock; blockNo++)
            {
                var b = (int)(blockNo - baseBlock);
                ref var block = ref blocks[blockHead + b];
                var present = block.Present;
                if (present == 0) continue;

                var range = ~0UL;
                if (blockNo == loBlock) range &= Bits.AtOrAbove((int)(lo & 63));
                if (blockNo == hiBlock && (hi & 63) != 63) range &= Bits.Below((int)(hi & 63) + 1);

                var rows = segRows[segHead + (b >> BlockShift)];
                var alive = block.Alive & range;
                var slot = block.RankBase + Bits.PopCount(present & ((range & (0UL - range)) - 1));
                var p = present & range;
                if (alive == p)
                {
                    var end = slot + Bits.PopCount(p);
                    for (; slot < end; slot++) sum += rows[slot].Score;
                    continue;
                }

                while (p != 0)
                {
                    var bit = p & (0UL - p);
                    p ^= bit;
                    if ((alive & bit) != 0) sum += rows[slot].Score;
                    slot++;
                }
            }

            return sum;
        }

        void AppendSegment()
        {
            if (segHead + segmentCount == segRows.Length)
            {
                if (segHead > segRows.Length / 2)
                {
                    // 先頭側の空きが半分を超えていれば詰める.
                    Array.Copy(segRows, segHead, segRows, 0, segmentCount);
                    Array.Copy(segUsed, segHead, segUsed, 0, segmentCount);
                    Array.Copy(segDeleted, segHead, segDeleted, 0, segmentCount);
                    Array.Clear(segRows, segmentCount, segHead);
                    var blockCount = segmentCount << BlockShift;
                    Array.Copy(blocks, blockHead, blocks, 0, blockCount);
                    Array.Clear(blocks, blockCount, blocks.Length - blockCount);
                }
                else
                {
                    var cap = segRows.Length * 2;
                    var r = new Row[cap][];
                    var u = new int[cap];
                    var d = new int[cap];
                    Array.Copy(segRows, segHead, r, 0, segmentCount);
                    Array.Copy(segUsed, segHead, u, 0, segmentCount);
                    Array.Copy(segDeleted, segHead, d, 0, segmentCount);
                    segRows = r;
                    segUsed = u;
                    segDeleted = d;
                    var nb = new BlockInfo[cap << BlockShift];
                    Array.Copy(blocks, blockHead, nb, 0, segmentCount << BlockShift);
                    blocks = nb;
                }

                segHead = 0;
                blockHead = 0;
            }

            var s = segHead + segmentCount;
            segRows[s] = new Row[64];
            segUsed[s] = 0;
            segDeleted[s] = 0;
            segmentCount++;
        }

        void TrimHead()
        {
            while (segmentCount > 1 && segRows[segHead] == null)
            {
                segHead++;
                blockHead += BlocksPerSegment;
                segmentCount--;
                firstSegment++;
            }
        }
    }

    public struct SegmentedRank2W : IPkTable
    {
        public SegmentedRankTable2 T;
        public string Name => T.Name;
        public int Count => T.Count;
        public bool SupportsRange => T.SupportsRange;
        public bool Insert(in Row row, out int key) => T.Insert(row, out key);
        public int GetScore(int key) => T.GetScore(key);
        public bool UpdateScore(int key, int score) => T.UpdateScore(key, score);
        public bool Remove(int key) => T.Remove(key);
        public long SumAll() => T.SumAll();
        public long SumRange(int min, int max) => T.SumRange(min, max);
    }
}
