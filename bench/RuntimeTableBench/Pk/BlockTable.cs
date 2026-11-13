using System;
using System.Collections.Generic;

namespace OrdoMemory.Bench.Pk
{
    // C: 設計案. Key 64 個ごとのブロックに、生存 Record だけを Key 順に詰めて持つ.
    // Key → 位置は「生存ビットマップの popcount」で O(1). 削除はブロック内で詰める. 全体コンパクションは無い.
    public sealed class BlockTable : IPkTable
    {
        const int BlockBits = 6;
        const int BlockSize = 1 << BlockBits;
        const int MinRows = 8;

        internal struct Block
        {
            public ulong Mask;
            public Row[] Rows;
        }

        readonly int start;

        // blocks[head + i] がブロック番号 (firstBlock + i) を担当する.
        Block[] blocks = new Block[16];
        int head;
        int blockCount;
        long firstBlock;

        long nextKey;
        int count;

        // 容量クラス (8, 16, 32, 64) ごとの再利用プール.
        readonly Stack<Row[]>[] pools = { new Stack<Row[]>(), new Stack<Row[]>(), new Stack<Row[]>(), new Stack<Row[]>() };

        public BlockTable(int start = 0)
        {
            this.start = start;
            nextKey = start;
        }

        public string Name => "C  Block bitmap (proposal)";
        public int Count => count;
        public bool SupportsRange => true;

        public bool Insert(in Row row, out int key)
        {
            key = 0;
            if (nextKey > int.MaxValue) return false;

            var offset = nextKey - start;
            var index = (int)((offset >> BlockBits) - firstBlock);
            if (index == blockCount) AppendBlock();

            ref var block = ref blocks[head + index];
            if (block.Rows == null) block.Rows = Rent(BlockSize);

            // Key は単調増加なので、書き込み先は常に末尾ブロックの末尾.
            var slot = Bits.PopCount(block.Mask);
            key = (int)nextKey++;
            block.Rows[slot] = row;
            block.Rows[slot].Id = key;
            block.Mask |= 1UL << (int)(offset & (BlockSize - 1));
            count++;
            return true;
        }

        public int GetScore(int key)
        {
            var offset = (long)key - start;
            if ((ulong)offset >= (ulong)(nextKey - start)) return -1;
            var index = (offset >> BlockBits) - firstBlock;
            if ((ulong)index >= (ulong)blockCount) return -1;

            ref var block = ref blocks[head + (int)index];
            var bit = (int)(offset & (BlockSize - 1));
            var mask = block.Mask;
            if ((mask >> bit & 1) == 0) return -1;
            return block.Rows[Bits.PopCount(mask & Bits.Below(bit))].Score;
        }

        public bool UpdateScore(int key, int score)
        {
            var offset = (long)key - start;
            if ((ulong)offset >= (ulong)(nextKey - start)) return false;
            var index = (offset >> BlockBits) - firstBlock;
            if ((ulong)index >= (ulong)blockCount) return false;

            ref var block = ref blocks[head + (int)index];
            var bit = (int)(offset & (BlockSize - 1));
            var mask = block.Mask;
            if ((mask >> bit & 1) == 0) return false;
            block.Rows[Bits.PopCount(mask & Bits.Below(bit))].Score = score;
            return true;
        }

        public bool Remove(int key)
        {
            var offset = (long)key - start;
            if ((ulong)offset >= (ulong)(nextKey - start)) return false;
            var index = (int)((offset >> BlockBits) - firstBlock);
            if ((uint)index >= (uint)blockCount) return false;

            ref var block = ref blocks[head + index];
            var bit = (int)(offset & (BlockSize - 1));
            var mask = block.Mask;
            if ((mask >> bit & 1) == 0) return false;

            var rows = block.Rows;
            var n = Bits.PopCount(mask);
            var slot = Bits.PopCount(mask & Bits.Below(bit));
            Array.Copy(rows, slot + 1, rows, slot, n - slot - 1);
            rows[n - 1] = default;
            block.Mask = mask & ~(1UL << bit);
            n--;
            count--;

            // 末尾ブロックには今後も Insert が来るため、縮小も解放もしない.
            var isTail = index == blockCount - 1;
            if (isTail) return true;

            if (n == 0)
            {
                Return(rows);
                block.Rows = null;
                TrimHead();
            }
            else if (rows.Length > MinRows && n <= rows.Length / 4)
            {
                var smaller = Rent(rows.Length / 2);
                Array.Copy(rows, smaller, n);
                Array.Clear(rows, 0, n);
                Return(rows);
                block.Rows = smaller;
            }

            return true;
        }

        public long SumAll()
        {
            long sum = 0;
            var e = All();
            while (e.MoveNext()) sum += e.Current.Score;
            return sum;
        }

        public long SumRange(int min, int max)
        {
            long sum = 0;
            var e = FindRange(min, max);
            while (e.MoveNext()) sum += e.Current.Score;
            return sum;
        }

        public Enumerator All()
        {
            return new Enumerator(blocks, head, head + blockCount, ~0UL, ~0UL);
        }

        public Enumerator FindRange(int min, int max)
        {
            var lo = Math.Max((long)min - start, firstBlock << BlockBits);
            var hi = Math.Min((long)max - start, nextKey - start - 1);
            if (lo > hi) return default;

            var first = head + (int)((lo >> BlockBits) - firstBlock);
            var last = head + (int)((hi >> BlockBits) - firstBlock);
            var firstMask = Bits.AtOrAbove((int)(lo & (BlockSize - 1)));
            var hiBit = (int)(hi & (BlockSize - 1));
            var lastMask = hiBit == BlockSize - 1 ? ~0UL : Bits.Below(hiBit + 1);
            return new Enumerator(blocks, first, last + 1, firstMask, lastMask);
        }

        // ブロック内では対象の Record が連続するため、ビットを 1 つずつ辿らずに添字の区間で回す.
        public struct Enumerator
        {
            readonly Block[] blocks;
            readonly int begin;
            readonly int end;
            readonly ulong firstMask;
            readonly ulong lastMask;
            int index;
            Row[] rows;
            int slot;
            int slotEnd;

            internal Enumerator(Block[] blocks, int begin, int end, ulong firstMask, ulong lastMask)
            {
                this.blocks = blocks;
                this.end = end;
                this.firstMask = firstMask;
                this.lastMask = lastMask;
                index = begin - 1;
                rows = null;
                slot = 0;
                slotEnd = 0;
                this.begin = begin;
            }

            public ref readonly Row Current => ref rows[slot];

            public bool MoveNext()
            {
                if (++slot < slotEnd) return true;

                while (++index < end)
                {
                    var mask = blocks[index].Mask;
                    if (mask == 0) continue;

                    var range = ~0UL;
                    if (index == begin) range &= firstMask;
                    if (index == end - 1) range &= lastMask;

                    // range は連続したビット列なので、対象は [popcount(下側), popcount(下側 + 範囲)) の連続区間.
                    var from = Bits.PopCount(mask & LowerOf(range));
                    var to = from + Bits.PopCount(mask & range);
                    if (from == to) continue;

                    rows = blocks[index].Rows;
                    slot = from;
                    slotEnd = to;
                    return true;
                }

                return false;
            }

            // 連続したビット列 range より下のビットを残すマスク.
            static ulong LowerOf(ulong range)
            {
                return (range & (0UL - range)) - 1;
            }
        }

        void AppendBlock()
        {
            if (head + blockCount == blocks.Length)
            {
                if (head > blocks.Length / 2)
                {
                    // 先頭側の空きが半分を超えていれば、拡張せずに詰める.
                    Array.Copy(blocks, head, blocks, 0, blockCount);
                    Array.Clear(blocks, blockCount, head);
                }
                else
                {
                    var bigger = new Block[blocks.Length * 2];
                    Array.Copy(blocks, head, bigger, 0, blockCount);
                    blocks = bigger;
                }

                head = 0;
            }

            blocks[head + blockCount] = default;
            blockCount++;
        }

        void TrimHead()
        {
            // 末尾ブロックは残す (Insert の書き込み先).
            while (blockCount > 1 && blocks[head].Mask == 0)
            {
                blocks[head] = default;
                head++;
                blockCount--;
                firstBlock++;
            }
        }

        Row[] Rent(int length)
        {
            var pool = pools[PoolIndex(length)];
            return pool.Count > 0 ? pool.Pop() : new Row[length];
        }

        // プールが配列を溜め込み続けないよう、容量クラスごとに上限を設ける.
        const int MaxPooled = 64;

        void Return(Row[] rows)
        {
            var pool = pools[PoolIndex(rows.Length)];
            if (pool.Count < MaxPooled) pool.Push(rows);
        }

        static int PoolIndex(int length)
        {
            switch (length)
            {
                case 8: return 0;
                case 16: return 1;
                case 32: return 2;
                default: return 3;
            }
        }
    }
}
