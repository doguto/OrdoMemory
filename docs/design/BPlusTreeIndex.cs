using System;
using System.Collections.Generic;

namespace OrdoMemory.Design
{
    public sealed class BPlusTreeIndex<TKey>
    {
        const int DefaultOrder = 64;

        // 一括構築時の充填率. 満杯にすると直後の挿入で全ノードが分割されるため、余白を残す.
        const double BuildFillRatio = 0.75;

        readonly struct Entry
        {
            public readonly TKey Key;
            public readonly int Primary;

            public Entry(TKey key, int primary)
            {
                Key = key;
                Primary = primary;
            }
        }

        abstract class Node
        {
            // 葉は要素数、内部ノードはキー数 (子は Count + 1 個).
            public int Count;
            public readonly Entry[] Keys;

            protected Node(int order)
            {
                Keys = new Entry[order];
            }
        }

        sealed class Leaf : Node
        {
            public Leaf Next;

            public Leaf(int order) : base(order)
            {
            }
        }

        // Children[i] のキー < Keys[i] <= Children[i + 1] のキー.
        sealed class Internal : Node
        {
            public readonly Node[] Children;

            public Internal(int order) : base(order)
            {
                Children = new Node[order + 1];
            }
        }

        readonly IComparer<TKey> comparer;

        // 1 ノードが持てる最大キー数.
        readonly int order;

        // 根以外のノードが持つべき最小キー数.
        readonly int minKeys;

        Node root;

        public int Count { get; private set; }

        public BPlusTreeIndex(IComparer<TKey> comparer, int order = DefaultOrder)
        {
            if (comparer == null) throw new ArgumentNullException(nameof(comparer));
            if (order < 4 || order % 2 != 0) throw new ArgumentOutOfRangeException(nameof(order));

            this.comparer = comparer;
            this.order = order;
            minKeys = order / 2;
        }

        public bool Insert(TKey key, int primary)
        {
            var entry = new Entry(key, primary);
            if (root == null)
            {
                var leaf = new Leaf(order);
                leaf.Keys[0] = entry;
                leaf.Count = 1;
                root = leaf;
                Count = 1;
                return true;
            }

            var right = InsertInto(root, entry, out var separator, out var inserted);
            if (right != null)
            {
                var newRoot = new Internal(order);
                newRoot.Children[0] = root;
                newRoot.Children[1] = right;
                newRoot.Keys[0] = separator;
                newRoot.Count = 1;
                root = newRoot;
            }

            if (inserted) Count++;
            return inserted;
        }

        /// <summary>
        /// 既存の内容と items を合わせて、ソート済みの状態から一括構築する.
        /// 1 件ずつ Insert するよりも分割が起きず、充填率も揃う.
        /// </summary>
        public void AddRange(IReadOnlyList<KeyValuePair<TKey, int>> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            if (items.Count == 0) return;

            var entries = new List<Entry>(Count + items.Count);
            for (var leaf = FirstLeaf(); leaf != null; leaf = leaf.Next)
            {
                for (var i = 0; i < leaf.Count; i++) entries.Add(leaf.Keys[i]);
            }

            for (var i = 0; i < items.Count; i++) entries.Add(new Entry(items[i].Key, items[i].Value));

            entries.Sort(Compare);

            // 完全に同じ組は 1 件にまとめる (Insert と同じ扱い).
            var unique = new List<Entry>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                if (unique.Count == 0 || Compare(unique[unique.Count - 1], entries[i]) != 0) unique.Add(entries[i]);
            }

            Count = unique.Count;
            root = BuildFromSorted(unique);
        }

        public bool Remove(TKey key, int primary)
        {
            if (root == null || !DeleteFrom(root, new Entry(key, primary))) return false;

            Count--;

            // 根が子 1 つだけになったら 1 段低くする. 空になった葉の根は木ごと破棄する.
            if (root is Internal rootInternal && rootInternal.Count == 0)
            {
                root = rootInternal.Children[0];
            }
            else if (root is Leaf rootLeaf && rootLeaf.Count == 0)
            {
                root = null;
            }

            return true;
        }

        /// <summary>min 以上 max 以下の SecondaryKey を持つ PrimaryKey を、(SecondaryKey, PrimaryKey) の昇順で返す.</summary>
        public IEnumerable<int> Range(TKey min, TKey max)
        {
            var from = new Entry(min, int.MinValue);
            var leaf = FindLeaf(from);
            if (leaf == null) yield break;

            var index = LowerBound(leaf.Keys, leaf.Count, from);
            while (leaf != null)
            {
                for (; index < leaf.Count; index++)
                {
                    if (comparer.Compare(leaf.Keys[index].Key, max) > 0) yield break;
                    yield return leaf.Keys[index].Primary;
                }

                leaf = leaf.Next;
                index = 0;
            }
        }

        /// <summary>SecondaryKey が key と等しい PrimaryKey を昇順で返す.</summary>
        public IEnumerable<int> Equal(TKey key)
        {
            return Range(key, key);
        }

        int Compare(Entry a, Entry b)
        {
            var result = comparer.Compare(a.Key, b.Key);
            return result != 0 ? result : a.Primary.CompareTo(b.Primary);
        }

        // key 以上となる最初の位置. 無ければ count.
        int LowerBound(Entry[] keys, int count, Entry key)
        {
            var low = 0;
            var high = count;
            while (low < high)
            {
                var mid = low + ((high - low) >> 1);
                if (Compare(keys[mid], key) < 0) low = mid + 1;
                else high = mid;
            }

            return low;
        }

        // key 以下のキーの個数 = key を含む部分木の子の位置.
        int ChildIndex(Internal node, Entry key)
        {
            var low = 0;
            var high = node.Count;
            while (low < high)
            {
                var mid = low + ((high - low) >> 1);
                if (Compare(node.Keys[mid], key) <= 0) low = mid + 1;
                else high = mid;
            }

            return low;
        }

        Leaf FindLeaf(Entry key)
        {
            var node = root;
            while (node is Internal internalNode)
            {
                node = internalNode.Children[ChildIndex(internalNode, key)];
            }

            return node as Leaf;
        }

        Leaf FirstLeaf()
        {
            var node = root;
            while (node is Internal internalNode)
            {
                node = internalNode.Children[0];
            }

            return node as Leaf;
        }

        Node BuildFromSorted(List<Entry> sorted)
        {
            if (sorted.Count == 0) return null;

            // 葉を左から順に詰める. ノードごとの件数は均等に配り、最小キー数を下回らないようにする.
            var leafSizes = Distribute(sorted.Count, minKeys, order);
            var nodes = new List<Node>(leafSizes.Length);
            var lowerKeys = new List<Entry>(leafSizes.Length);
            Leaf previous = null;
            var position = 0;
            foreach (var size in leafSizes)
            {
                var leaf = new Leaf(order);
                for (var i = 0; i < size; i++) leaf.Keys[i] = sorted[position + i];

                leaf.Count = size;
                position += size;

                if (previous != null) previous.Next = leaf;

                previous = leaf;
                nodes.Add(leaf);
                lowerKeys.Add(leaf.Keys[0]);
            }

            // 根が 1 つになるまで、1 段上の内部ノードを作る.
            while (nodes.Count > 1)
            {
                var groupSizes = Distribute(nodes.Count, minKeys + 1, order + 1);
                var parents = new List<Node>(groupSizes.Length);
                var parentLowerKeys = new List<Entry>(groupSizes.Length);
                position = 0;
                foreach (var size in groupSizes)
                {
                    var parent = new Internal(order);
                    for (var j = 0; j < size; j++)
                    {
                        parent.Children[j] = nodes[position + j];
                        if (j > 0) parent.Keys[j - 1] = lowerKeys[position + j];
                    }

                    parent.Count = size - 1;
                    parents.Add(parent);
                    parentLowerKeys.Add(lowerKeys[position]);
                    position += size;
                }

                nodes = parents;
                lowerKeys = parentLowerKeys;
            }

            return nodes[0];
        }

        // total 個を、充填率 BuildFillRatio を目標に、各 [min, max] 個のグループへ均等に配る.
        static int[] Distribute(int total, int min, int max)
        {
            var target = Math.Max(min, (int)(max * BuildFillRatio));
            var groups = (total + target - 1) / target;
            groups = Math.Min(groups, total / min);
            groups = Math.Max(groups, (total + max - 1) / max);
            groups = Math.Max(groups, 1);

            var sizes = new int[groups];
            var baseSize = total / groups;
            var remainder = total % groups;
            for (var i = 0; i < groups; i++)
            {
                sizes[i] = baseSize + (i < remainder ? 1 : 0);
            }

            return sizes;
        }

        // node の部分木へ挿入する. 分割が起きた場合は、新しく右にできたノードを返し、親へ登録するキーを separator に返す.
        Node InsertInto(Node node, Entry entry, out Entry separator, out bool inserted)
        {
            if (node is Leaf leaf) return InsertIntoLeaf(leaf, entry, out separator, out inserted);

            var parent = (Internal)node;
            var childIndex = ChildIndex(parent, entry);
            var childRight = InsertInto(parent.Children[childIndex], entry, out var childSeparator, out inserted);
            if (childRight == null)
            {
                separator = default;
                return null;
            }

            return InsertIntoInternal(parent, childIndex, childSeparator, childRight, out separator);
        }

        Node InsertIntoLeaf(Leaf leaf, Entry entry, out Entry separator, out bool inserted)
        {
            separator = default;
            var index = LowerBound(leaf.Keys, leaf.Count, entry);
            if (index < leaf.Count && Compare(leaf.Keys[index], entry) == 0)
            {
                inserted = false;
                return null;
            }

            inserted = true;
            if (leaf.Count < order)
            {
                InsertAt(leaf, index, entry);
                return null;
            }

            // 半分ずつに分けてから挿入する. どちらに入っても双方が最小キー数以上になる.
            var right = new Leaf(order);
            var keep = order / 2;
            var move = leaf.Count - keep;
            Array.Copy(leaf.Keys, keep, right.Keys, 0, move);
            Array.Clear(leaf.Keys, keep, move);
            right.Count = move;
            leaf.Count = keep;

            if (index <= keep) InsertAt(leaf, index, entry);
            else InsertAt(right, index - keep, entry);

            right.Next = leaf.Next;
            leaf.Next = right;
            separator = right.Keys[0];
            return right;
        }

        static void InsertAt(Leaf leaf, int index, Entry entry)
        {
            Array.Copy(leaf.Keys, index, leaf.Keys, index + 1, leaf.Count - index);
            leaf.Keys[index] = entry;
            leaf.Count++;
        }

        // 子 childIndex の分割結果 (key, child) を登録する. 満杯なら分割し、右ノードと昇格キーを返す.
        Node InsertIntoInternal(Internal node, int childIndex, Entry key, Node child, out Entry separator)
        {
            if (node.Count < order)
            {
                InsertAt(node, childIndex, key, child);
                separator = default;
                return null;
            }

            // 満杯なので、挿入後の order + 1 個のキーを一時配列に並べ、中央を昇格させて左右を同数にする.
            var keys = new Entry[order + 1];
            var children = new Node[order + 2];
            Array.Copy(node.Keys, 0, keys, 0, childIndex);
            keys[childIndex] = key;
            Array.Copy(node.Keys, childIndex, keys, childIndex + 1, node.Count - childIndex);
            Array.Copy(node.Children, 0, children, 0, childIndex + 1);
            children[childIndex + 1] = child;
            Array.Copy(node.Children, childIndex + 1, children, childIndex + 2, node.Count - childIndex);

            var mid = order / 2;
            var right = new Internal(order);
            var move = keys.Length - mid - 1;

            Array.Copy(keys, 0, node.Keys, 0, mid);
            Array.Clear(node.Keys, mid, node.Keys.Length - mid);
            Array.Copy(children, 0, node.Children, 0, mid + 1);
            Array.Clear(node.Children, mid + 1, node.Children.Length - (mid + 1));
            node.Count = mid;

            Array.Copy(keys, mid + 1, right.Keys, 0, move);
            Array.Copy(children, mid + 1, right.Children, 0, move + 1);
            right.Count = move;

            separator = keys[mid];
            return right;
        }

        static void InsertAt(Internal node, int index, Entry key, Node child)
        {
            Array.Copy(node.Keys, index, node.Keys, index + 1, node.Count - index);
            Array.Copy(node.Children, index + 1, node.Children, index + 2, node.Count - index);
            node.Keys[index] = key;
            node.Children[index + 1] = child;
            node.Count++;
        }

        bool DeleteFrom(Node node, Entry entry)
        {
            if (node is Leaf leaf)
            {
                var index = LowerBound(leaf.Keys, leaf.Count, entry);
                if (index >= leaf.Count || Compare(leaf.Keys[index], entry) != 0) return false;

                Array.Copy(leaf.Keys, index + 1, leaf.Keys, index, leaf.Count - index - 1);
                leaf.Count--;
                leaf.Keys[leaf.Count] = default;
                return true;
            }

            var parent = (Internal)node;
            var childIndex = ChildIndex(parent, entry);
            if (!DeleteFrom(parent.Children[childIndex], entry)) return false;

            if (parent.Children[childIndex].Count < minKeys) Rebalance(parent, childIndex);

            return true;
        }

        // 最小キー数を下回った子を、隣から借りるか、隣と併合して直す.
        void Rebalance(Internal parent, int childIndex)
        {
            var left = childIndex > 0 ? parent.Children[childIndex - 1] : null;
            var right = childIndex < parent.Count ? parent.Children[childIndex + 1] : null;

            if (left != null && left.Count > minKeys) BorrowFromLeft(parent, childIndex);
            else if (right != null && right.Count > minKeys) BorrowFromRight(parent, childIndex);
            else if (left != null) Merge(parent, childIndex - 1);
            else Merge(parent, childIndex);
        }

        static void BorrowFromLeft(Internal parent, int childIndex)
        {
            var child = parent.Children[childIndex];
            var left = parent.Children[childIndex - 1];

            if (child is Leaf childLeaf)
            {
                var leftLeaf = (Leaf)left;
                Array.Copy(childLeaf.Keys, 0, childLeaf.Keys, 1, childLeaf.Count);
                childLeaf.Keys[0] = leftLeaf.Keys[leftLeaf.Count - 1];
                childLeaf.Count++;
                leftLeaf.Count--;
                leftLeaf.Keys[leftLeaf.Count] = default;
                parent.Keys[childIndex - 1] = childLeaf.Keys[0];
            }
            else
            {
                var childNode = (Internal)child;
                var leftNode = (Internal)left;
                Array.Copy(childNode.Keys, 0, childNode.Keys, 1, childNode.Count);
                Array.Copy(childNode.Children, 0, childNode.Children, 1, childNode.Count + 1);
                childNode.Keys[0] = parent.Keys[childIndex - 1];
                childNode.Children[0] = leftNode.Children[leftNode.Count];
                childNode.Count++;
                parent.Keys[childIndex - 1] = leftNode.Keys[leftNode.Count - 1];
                leftNode.Keys[leftNode.Count - 1] = default;
                leftNode.Children[leftNode.Count] = null;
                leftNode.Count--;
            }
        }

        static void BorrowFromRight(Internal parent, int childIndex)
        {
            var child = parent.Children[childIndex];
            var right = parent.Children[childIndex + 1];

            if (child is Leaf childLeaf)
            {
                var rightLeaf = (Leaf)right;
                childLeaf.Keys[childLeaf.Count] = rightLeaf.Keys[0];
                childLeaf.Count++;
                Array.Copy(rightLeaf.Keys, 1, rightLeaf.Keys, 0, rightLeaf.Count - 1);
                rightLeaf.Count--;
                rightLeaf.Keys[rightLeaf.Count] = default;
                parent.Keys[childIndex] = rightLeaf.Keys[0];
            }
            else
            {
                var childNode = (Internal)child;
                var rightNode = (Internal)right;
                childNode.Keys[childNode.Count] = parent.Keys[childIndex];
                childNode.Children[childNode.Count + 1] = rightNode.Children[0];
                childNode.Count++;
                parent.Keys[childIndex] = rightNode.Keys[0];
                Array.Copy(rightNode.Keys, 1, rightNode.Keys, 0, rightNode.Count - 1);
                Array.Copy(rightNode.Children, 1, rightNode.Children, 0, rightNode.Count);
                rightNode.Keys[rightNode.Count - 1] = default;
                rightNode.Children[rightNode.Count] = null;
                rightNode.Count--;
            }
        }

        // 子 index と子 index + 1 を、左へ併合する. 親からは区切りのキーと右の子を取り除く.
        static void Merge(Internal parent, int index)
        {
            var left = parent.Children[index];
            var right = parent.Children[index + 1];

            if (left is Leaf leftLeaf)
            {
                var rightLeaf = (Leaf)right;
                Array.Copy(rightLeaf.Keys, 0, leftLeaf.Keys, leftLeaf.Count, rightLeaf.Count);
                leftLeaf.Count += rightLeaf.Count;
                leftLeaf.Next = rightLeaf.Next;
            }
            else
            {
                var leftNode = (Internal)left;
                var rightNode = (Internal)right;
                leftNode.Keys[leftNode.Count] = parent.Keys[index];
                Array.Copy(rightNode.Keys, 0, leftNode.Keys, leftNode.Count + 1, rightNode.Count);
                Array.Copy(rightNode.Children, 0, leftNode.Children, leftNode.Count + 1, rightNode.Count + 1);
                leftNode.Count += rightNode.Count + 1;
            }

            Array.Copy(parent.Keys, index + 1, parent.Keys, index, parent.Count - index - 1);
            Array.Copy(parent.Children, index + 2, parent.Children, index + 1, parent.Count - index - 1);
            parent.Keys[parent.Count - 1] = default;
            parent.Children[parent.Count] = null;
            parent.Count--;
        }
    }
}
