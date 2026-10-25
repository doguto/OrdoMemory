using System;
using System.Collections.Generic;

namespace OrdoMemory.Bench.Sec
{
    // S1: SortedSet<(Key, PK)>. .NET 標準の赤黒木 (1 エントリ 1 オブジェクト).
    public sealed class SortedSetIndex<TKey> : ISecIndex<TKey>
    {
        readonly SortedSet<(TKey Key, int Pk)> set;

        public SortedSetIndex()
        {
            set = new SortedSet<(TKey, int)>(new EntryComparer<TKey>(Comparers.For<TKey>()));
        }

        public string Name => "S1 SortedSet<(K,PK)>";
        public bool SupportsRange => true;
        public bool Insert(TKey key, int pk) => set.Add((key, pk));
        public bool Remove(TKey key, int pk) => set.Remove((key, pk));
        public long SumEqual(TKey key, out int count) => SumRange(key, key, out count);

        public long SumRange(TKey min, TKey max, out int count)
        {
            long sum = 0;
            count = 0;
            foreach (var e in set.GetViewBetween((min, 0), (max, int.MaxValue)))
            {
                sum += e.Pk;
                count++;
            }

            return sum;
        }

        public bool Bulk(TKey[] keys, int[] pks, int n)
        {
            return false;
        }
    }

    // S2: class ノードの B+Tree. 比較は IComparer<TKey> 経由. PR の SecondaryIndex を素直に実装した形.
    public sealed class ClassBTree<TKey> : ISecIndex<TKey>
    {
        abstract class Node
        {
            public TKey[] Keys;
            public int[] Pks;
            public int Count;
        }

        sealed class Leaf : Node
        {
            public Leaf Next;
            public Leaf Prev;
        }

        sealed class Inner : Node
        {
            public Node[] Children;
        }

        readonly int b;
        readonly IComparer<TKey> comparer;
        Node root;
        int height;
        Inner[] pathNode = new Inner[32];
        int[] pathPos = new int[32];

        public ClassBTree(int nodeSize)
        {
            b = nodeSize;
            comparer = Comparers.For<TKey>();
        }

        public string Name => $"S2 Class B+Tree B={b}";
        public bool SupportsRange => true;

        int Compare(Node node, int i, TKey key, int pk)
        {
            var c = comparer.Compare(node.Keys[i], key);
            return c != 0 ? c : node.Pks[i].CompareTo(pk);
        }

        int LowerBound(Node node, TKey key, int pk)
        {
            int lo = 0, hi = node.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (Compare(node, mid, key, pk) < 0) lo = mid + 1;
                else hi = mid;
            }

            return lo;
        }

        int ChildIndex(Inner node, TKey key, int pk)
        {
            int lo = 0, hi = node.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (Compare(node, mid, key, pk) <= 0) lo = mid + 1;
                else hi = mid;
            }

            return lo == 0 ? 0 : lo - 1;
        }

        Leaf Descend(TKey key, int pk)
        {
            var node = root;
            for (var level = height; level > 0; level--)
            {
                var inner = (Inner)node;
                var pos = ChildIndex(inner, key, pk);
                pathNode[level] = inner;
                pathPos[level] = pos;
                node = inner.Children[pos];
            }

            return (Leaf)node;
        }

        Leaf NewLeaf() => new Leaf { Keys = new TKey[b], Pks = new int[b] };
        Inner NewInner() => new Inner { Keys = new TKey[b], Pks = new int[b], Children = new Node[b] };

        public bool Insert(TKey key, int pk)
        {
            if (root == null) root = NewLeaf();
            var leaf = Descend(key, pk);
            var pos = LowerBound(leaf, key, pk);
            if (pos < leaf.Count && Compare(leaf, pos, key, pk) == 0) return false;

            if (leaf.Count < b)
            {
                InsertAt(leaf, pos, key, pk, null);
                return true;
            }

            var split = pos == b && leaf.Next == null ? b : b / 2;
            var right = NewLeaf();
            MoveTail(leaf, split, right);
            right.Next = leaf.Next;
            right.Prev = leaf;
            if (leaf.Next != null) leaf.Next.Prev = right;
            leaf.Next = right;
            if (pos < split) InsertAt(leaf, pos, key, pk, null);
            else InsertAt(right, pos - split, key, pk, null);

            Promote(1, right.Keys[0], right.Pks[0], right, leaf);
            return true;
        }

        void InsertAt(Node node, int pos, TKey key, int pk, Node child)
        {
            var move = node.Count - pos;
            if (move > 0)
            {
                Array.Copy(node.Keys, pos, node.Keys, pos + 1, move);
                Array.Copy(node.Pks, pos, node.Pks, pos + 1, move);
                if (child != null) Array.Copy(((Inner)node).Children, pos, ((Inner)node).Children, pos + 1, move);
            }

            node.Keys[pos] = key;
            node.Pks[pos] = pk;
            if (child != null) ((Inner)node).Children[pos] = child;
            node.Count++;
        }

        void MoveTail(Node from, int split, Node to)
        {
            var n = from.Count - split;
            if (n > 0)
            {
                Array.Copy(from.Keys, split, to.Keys, 0, n);
                Array.Copy(from.Pks, split, to.Pks, 0, n);
                Array.Clear(from.Keys, split, n);
                if (from is Inner fi)
                {
                    Array.Copy(fi.Children, split, ((Inner)to).Children, 0, n);
                    Array.Clear(fi.Children, split, n);
                }
            }

            to.Count = Math.Max(n, 0);
            from.Count = split;
        }

        void Promote(int level, TKey sepKey, int sepPk, Node child, Node left)
        {
            while (true)
            {
                if (level > height)
                {
                    var newRoot = NewInner();
                    newRoot.Keys[0] = left.Keys[0];
                    newRoot.Pks[0] = left.Pks[0];
                    newRoot.Children[0] = left;
                    newRoot.Keys[1] = sepKey;
                    newRoot.Pks[1] = sepPk;
                    newRoot.Children[1] = child;
                    newRoot.Count = 2;
                    root = newRoot;
                    height++;
                    return;
                }

                var node = pathNode[level];
                var pos = pathPos[level] + 1;
                if (node.Count < b)
                {
                    InsertAt(node, pos, sepKey, sepPk, child);
                    return;
                }

                var split = pos == b ? b : b / 2;
                var right = NewInner();
                MoveTail(node, split, right);
                if (pos <= split && split != b) InsertAt(node, pos, sepKey, sepPk, child);
                else InsertAt(right, pos - split, sepKey, sepPk, child);

                sepKey = right.Keys[0];
                sepPk = right.Pks[0];
                child = right;
                left = node;
                level++;
            }
        }

        public bool Remove(TKey key, int pk)
        {
            if (root == null) return false;
            var leaf = Descend(key, pk);
            var pos = LowerBound(leaf, key, pk);
            if (pos >= leaf.Count || Compare(leaf, pos, key, pk) != 0) return false;

            RemoveAt(leaf, pos);
            if (leaf.Count == 0 && height > 0)
            {
                if (leaf.Prev != null) leaf.Prev.Next = leaf.Next;
                if (leaf.Next != null) leaf.Next.Prev = leaf.Prev;

                for (var level = 1; ; level++)
                {
                    var node = pathNode[level];
                    RemoveAt(node, pathPos[level]);
                    if (level == height)
                    {
                        // 子が 1 つの根は取り除く (新しい根も子が 1 つなら繰り返す).
                        while (height > 0 && root.Count == 1)
                        {
                            root = ((Inner)root).Children[0];
                            height--;
                        }

                        if (height > 0 && root.Count == 0)
                        {
                            root = NewLeaf();
                            height = 0;
                        }

                        break;
                    }

                    if (node.Count > 0) break;
                }
            }

            return true;
        }

        void RemoveAt(Node node, int pos)
        {
            var count = node.Count - 1;
            var move = count - pos;
            if (move > 0)
            {
                Array.Copy(node.Keys, pos + 1, node.Keys, pos, move);
                Array.Copy(node.Pks, pos + 1, node.Pks, pos, move);
                if (node is Inner inner) Array.Copy(inner.Children, pos + 1, inner.Children, pos, move);
            }

            node.Keys[count] = default;
            if (node is Inner i2) i2.Children[count] = null;
            node.Count = count;
        }

        public long SumEqual(TKey key, out int count) => SumRange(key, key, out count);

        public long SumRange(TKey min, TKey max, out int count)
        {
            count = 0;
            if (root == null) return 0;
            var leaf = Descend(min, 0);
            var pos = LowerBound(leaf, min, 0);
            long sum = 0;
            while (leaf != null)
            {
                for (; pos < leaf.Count; pos++)
                {
                    if (comparer.Compare(leaf.Keys[pos], max) > 0) return sum;
                    sum += leaf.Pks[pos];
                    count++;
                }

                leaf = leaf.Next;
                pos = 0;
            }

            return sum;
        }

        public bool Bulk(TKey[] keys, int[] pks, int n)
        {
            return false;
        }
    }

    // S4: Dictionary<Key, PK の整列済み配列>. 等価検索のみ.
    public sealed class HashPostingIndex<TKey> : ISecIndex<TKey>
    {
        sealed class Posting
        {
            public int[] Pks = new int[2];
            public int Count;
        }

        readonly Dictionary<TKey, Posting> map;

        public HashPostingIndex()
        {
            map = typeof(TKey) == typeof(string)
                ? new Dictionary<TKey, Posting>((IEqualityComparer<TKey>)(object)StringComparer.Ordinal)
                : new Dictionary<TKey, Posting>();
        }

        public string Name => "S4 Hash -> sorted PK[]";
        public bool SupportsRange => false;

        public bool Insert(TKey key, int pk)
        {
            if (!map.TryGetValue(key, out var p))
            {
                p = new Posting();
                map.Add(key, p);
            }

            // PK は単調増加のため、ほとんどの場合は末尾への追記.
            var pos = p.Count;
            if (pos > 0 && p.Pks[pos - 1] >= pk)
            {
                pos = Array.BinarySearch(p.Pks, 0, p.Count, pk);
                if (pos >= 0) return false;
                pos = ~pos;
            }

            if (p.Count == p.Pks.Length) Array.Resize(ref p.Pks, p.Count * 2);
            Array.Copy(p.Pks, pos, p.Pks, pos + 1, p.Count - pos);
            p.Pks[pos] = pk;
            p.Count++;
            return true;
        }

        public bool Remove(TKey key, int pk)
        {
            if (!map.TryGetValue(key, out var p)) return false;
            var pos = Array.BinarySearch(p.Pks, 0, p.Count, pk);
            if (pos < 0) return false;
            p.Count--;
            Array.Copy(p.Pks, pos + 1, p.Pks, pos, p.Count - pos);
            if (p.Count == 0) map.Remove(key);
            return true;
        }

        public long SumEqual(TKey key, out int count)
        {
            count = 0;
            if (!map.TryGetValue(key, out var p)) return 0;
            long sum = 0;
            var pks = p.Pks;
            for (var i = 0; i < p.Count; i++) sum += pks[i];
            count = p.Count;
            return sum;
        }

        public long SumRange(TKey min, TKey max, out int count)
        {
            throw new NotSupportedException();
        }

        public bool Bulk(TKey[] keys, int[] pks, int n)
        {
            return false;
        }
    }

    // S5: 値ごとに、PK の 64 個単位のビットマップ (ブロック番号, Mask) を整列して持つ. 値の種類が少ないキー向け.
    // 値の一覧は整列済み配列で持ち、範囲検索も行える.
    public sealed class BitmapIndex<TKey> : ISecIndex<TKey>
    {
        sealed class Posting
        {
            public int[] Blocks = new int[4];
            public ulong[] Masks = new ulong[4];
            public int Count;
        }

        readonly IComparer<TKey> comparer;
        TKey[] values = new TKey[4];
        Posting[] postings = new Posting[4];
        int valueCount;

        public BitmapIndex()
        {
            comparer = Comparers.For<TKey>();
        }

        public string Name => "S5 Bitmap (low-card)";
        public bool SupportsRange => true;

        int FindValue(TKey key)
        {
            return Array.BinarySearch(values, 0, valueCount, key, comparer);
        }

        public bool Insert(TKey key, int pk)
        {
            var v = FindValue(key);
            if (v < 0)
            {
                v = ~v;
                if (valueCount == values.Length)
                {
                    Array.Resize(ref values, valueCount * 2);
                    Array.Resize(ref postings, valueCount * 2);
                }

                Array.Copy(values, v, values, v + 1, valueCount - v);
                Array.Copy(postings, v, postings, v + 1, valueCount - v);
                values[v] = key;
                postings[v] = new Posting();
                valueCount++;
            }

            var p = postings[v];
            var block = pk >> 6;
            var bit = 1UL << (pk & 63);

            // PK は単調増加のため、ほとんどの場合は末尾のブロック.
            int pos;
            if (p.Count > 0 && p.Blocks[p.Count - 1] == block) pos = p.Count - 1;
            else if (p.Count == 0 || p.Blocks[p.Count - 1] < block) pos = ~p.Count;
            else pos = Array.BinarySearch(p.Blocks, 0, p.Count, block);

            if (pos >= 0)
            {
                if ((p.Masks[pos] & bit) != 0) return false;
                p.Masks[pos] |= bit;
                return true;
            }

            pos = ~pos;
            if (p.Count == p.Blocks.Length)
            {
                Array.Resize(ref p.Blocks, p.Count * 2);
                Array.Resize(ref p.Masks, p.Count * 2);
            }

            Array.Copy(p.Blocks, pos, p.Blocks, pos + 1, p.Count - pos);
            Array.Copy(p.Masks, pos, p.Masks, pos + 1, p.Count - pos);
            p.Blocks[pos] = block;
            p.Masks[pos] = bit;
            p.Count++;
            return true;
        }

        public bool Remove(TKey key, int pk)
        {
            var v = FindValue(key);
            if (v < 0) return false;
            var p = postings[v];
            var pos = Array.BinarySearch(p.Blocks, 0, p.Count, pk >> 6);
            if (pos < 0) return false;
            var bit = 1UL << (pk & 63);
            if ((p.Masks[pos] & bit) == 0) return false;
            p.Masks[pos] &= ~bit;
            if (p.Masks[pos] == 0)
            {
                p.Count--;
                Array.Copy(p.Blocks, pos + 1, p.Blocks, pos, p.Count - pos);
                Array.Copy(p.Masks, pos + 1, p.Masks, pos, p.Count - pos);
            }

            return true;
        }

        public long SumEqual(TKey key, out int count)
        {
            count = 0;
            var v = FindValue(key);
            return v < 0 ? 0 : SumPosting(postings[v], ref count);
        }

        public long SumRange(TKey min, TKey max, out int count)
        {
            count = 0;
            var v = FindValue(min);
            if (v < 0) v = ~v;
            long sum = 0;
            for (; v < valueCount && comparer.Compare(values[v], max) <= 0; v++) sum += SumPosting(postings[v], ref count);
            return sum;
        }

        static long SumPosting(Posting p, ref int count)
        {
            long sum = 0;
            for (var i = 0; i < p.Count; i++)
            {
                var m = p.Masks[i];
                long baseKey = (long)p.Blocks[i] << 6;
                count += Bits.PopCount(m);
                while (m != 0)
                {
                    sum += baseKey + Bits.TrailingZeroCount(m);
                    m &= m - 1;
                }
            }

            return sum;
        }

        public bool Bulk(TKey[] keys, int[] pks, int n)
        {
            return false;
        }
    }
}
