using System;

namespace OrdoMemory.Bench.Pk
{
    // F: 区画分けした rank テーブル (B と C の折衷).
    // Key 2048 個ごとの区画に分け、区画ごとに Rows を 1 本持つ. Key → スロットは区画内の rank テーブルで O(1).
    // 削除は tombstone (生存ビットを落とすだけ). コンパクションは区画単位で行うため、1 回のコストは区画の大きさで抑えられる.
    public sealed class SegmentedRankTable : IPkTable
    {
        const int SegmentBits = 11;
        const int BlocksPerSegment = 1 << (SegmentBits - 6);
        const int MaxRows = 1 << SegmentBits;
        const double Threshold = 0.25;

        struct BlockInfo
        {
            public ulong Present;   // スロットを持つ Key
            public ulong Alive;     // 生存している Key (Present の部分集合)
            public int RankBase;    // このブロックの先頭スロット
        }

        struct Segment
        {
            public BlockInfo[] Blocks;
            public Row[] Rows;
            public int Used;        // 使用中スロット数 (生存 + tombstone)
            public int Deleted;     // tombstone の数
        }

        readonly int start;

        // segments[head + i] が区画番号 (firstSegment + i) を担当する.
        Segment[] segments = new Segment[8];
        int head;
        int segmentCount;
        long firstSegment;

        long nextKey;
        int count;

        public SegmentedRankTable(int start = 0)
        {
            this.start = start;
            nextKey = start;
        }

        public string Name => "F  Segmented rank (2048)";
        public int Count => count;
        public bool SupportsRange => true;

        public bool Insert(in Row row, out int key)
        {
            key = 0;
            if (nextKey > int.MaxValue) return false;

            var offset = nextKey - start;
            var index = (int)((offset >> SegmentBits) - firstSegment);
            if (index == segmentCount) AppendSegment();

            ref var seg = ref segments[head + index];
            ref var block = ref seg.Blocks[(int)(offset >> 6) & (BlocksPerSegment - 1)];
            var bit = (int)(offset & 63);

            // Key は欠番無く払い出すため、bit 0 がブロックの最初の Key.
            if (bit == 0) block.RankBase = seg.Used;
            if (seg.Used == seg.Rows.Length) Array.Resize(ref seg.Rows, Math.Min(seg.Rows.Length * 2, MaxRows));

            key = (int)nextKey++;
            seg.Rows[seg.Used] = row;
            seg.Rows[seg.Used].Id = key;
            seg.Used++;
            block.Present |= 1UL << bit;
            block.Alive |= 1UL << bit;
            count++;
            return true;
        }

        public int GetScore(int key)
        {
            var offset = (long)key - start;
            if ((ulong)offset >= (ulong)(nextKey - start)) return -1;
            var index = (offset >> SegmentBits) - firstSegment;
            if ((ulong)index >= (ulong)segmentCount) return -1;

            ref var seg = ref segments[head + (int)index];
            if (seg.Blocks == null) return -1;
            ref var block = ref seg.Blocks[(int)(offset >> 6) & (BlocksPerSegment - 1)];
            var bit = (int)(offset & 63);
            if ((block.Alive >> bit & 1) == 0) return -1;
            return seg.Rows[block.RankBase + Bits.PopCount(block.Present & Bits.Below(bit))].Score;
        }

        public bool UpdateScore(int key, int score)
        {
            var offset = (long)key - start;
            if ((ulong)offset >= (ulong)(nextKey - start)) return false;
            var index = (offset >> SegmentBits) - firstSegment;
            if ((ulong)index >= (ulong)segmentCount) return false;

            ref var seg = ref segments[head + (int)index];
            if (seg.Blocks == null) return false;
            ref var block = ref seg.Blocks[(int)(offset >> 6) & (BlocksPerSegment - 1)];
            var bit = (int)(offset & 63);
            if ((block.Alive >> bit & 1) == 0) return false;
            seg.Rows[block.RankBase + Bits.PopCount(block.Present & Bits.Below(bit))].Score = score;
            return true;
        }

        public bool Remove(int key)
        {
            var offset = (long)key - start;
            if ((ulong)offset >= (ulong)(nextKey - start)) return false;
            var index = (int)((offset >> SegmentBits) - firstSegment);
            if ((uint)index >= (uint)segmentCount) return false;

            ref var seg = ref segments[head + index];
            if (seg.Blocks == null) return false;
            ref var block = ref seg.Blocks[(int)(offset >> 6) & (BlocksPerSegment - 1)];
            var bit = (int)(offset & 63);
            if ((block.Alive >> bit & 1) == 0) return false;

            block.Alive &= ~(1UL << bit);
            seg.Deleted++;
            count--;

            var isTail = index == segmentCount - 1;
            if (seg.Deleted == seg.Used && !isTail)
            {
                // 区画が空になった. 配列を手放す.
                seg = default;
                TrimHead();
            }
            else if (seg.Deleted >= 64 && seg.Deleted >= seg.Used * Threshold)
            {
                CompactSegment(ref seg, isTail);
            }

            return true;
        }

        // 区画内の tombstone を取り除いて前方に詰める. コストは区画の Used に比例する (最大 2048 件).
        static void CompactSegment(ref Segment seg, bool isTail)
        {
            var rows = seg.Rows;
            var blocks = seg.Blocks;
            var write = 0;
            for (var b = 0; b < BlocksPerSegment; b++)
            {
                ref var block = ref blocks[b];
                var present = block.Present;
                if (present == 0)
                {
                    // 末尾区画のまだ使われていないブロック.
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

            Array.Clear(rows, write, seg.Used - write);
            seg.Used = write;
            seg.Deleted = 0;

            // 末尾以外は今後伸びないため、余りが大きければ縮める.
            if (!isTail && rows.Length > 64 && write * 2 < rows.Length)
            {
                var smaller = new Row[Math.Max(16, (write + 15) & ~15)];
                Array.Copy(rows, smaller, write);
                seg.Rows = smaller;
            }
        }

        public long SumAll()
        {
            long sum = 0;
            for (var s = head; s < head + segmentCount; s++)
            {
                ref var seg = ref segments[s];
                if (seg.Blocks == null) continue;
                var rows = seg.Rows;
                if (seg.Deleted == 0)
                {
                    for (var i = 0; i < seg.Used; i++) sum += rows[i].Score;
                    continue;
                }

                var blocks = seg.Blocks;
                for (var b = 0; b < BlocksPerSegment; b++)
                {
                    var present = blocks[b].Present;
                    var alive = blocks[b].Alive;
                    var slot = blocks[b].RankBase;
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
            var lo = Math.Max((long)min - start, firstSegment << SegmentBits);
            var hi = Math.Min((long)max - start, nextKey - start - 1);
            if (lo > hi) return 0;

            for (var blockNo = lo >> 6; blockNo <= hi >> 6; blockNo++)
            {
                ref var seg = ref segments[head + (int)((blockNo >> (SegmentBits - 6)) - firstSegment)];
                if (seg.Blocks == null)
                {
                    // 空の区画は丸ごと飛ばす.
                    blockNo |= BlocksPerSegment - 1;
                    continue;
                }

                ref var block = ref seg.Blocks[(int)blockNo & (BlocksPerSegment - 1)];
                var range = ~0UL;
                if (blockNo == lo >> 6) range &= Bits.AtOrAbove((int)(lo & 63));
                if (blockNo == hi >> 6 && (hi & 63) != 63) range &= Bits.Below((int)(hi & 63) + 1);

                var present = block.Present;
                var alive = block.Alive & range;
                var rows = seg.Rows;
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
            if (head + segmentCount == segments.Length)
            {
                if (head > segments.Length / 2)
                {
                    Array.Copy(segments, head, segments, 0, segmentCount);
                    Array.Clear(segments, segmentCount, head);
                }
                else
                {
                    var bigger = new Segment[segments.Length * 2];
                    Array.Copy(segments, head, bigger, 0, segmentCount);
                    segments = bigger;
                }

                head = 0;
            }

            segments[head + segmentCount] = new Segment { Blocks = new BlockInfo[BlocksPerSegment], Rows = new Row[64] };
            segmentCount++;
        }

        void TrimHead()
        {
            while (segmentCount > 1 && segments[head].Blocks == null)
            {
                head++;
                segmentCount--;
                firstSegment++;
            }
        }
    }

    public struct SegmentedRankW : IPkTable
    {
        public SegmentedRankTable T;
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
