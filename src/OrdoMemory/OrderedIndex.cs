using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class OrderedIndex<TKey, TKeyTraits, TPrimaryKey, TPrimaryKeyTraits>
        where TKeyTraits : struct, IOrderedKeyTraits<TKey>
        where TPrimaryKey : unmanaged
        where TPrimaryKeyTraits : struct, IPrimaryKeyTraits<TPrimaryKey>
    {
        public const int DefaultNodeSize = 64;

        const int RebuildRatio = 16;

        readonly int b;
        readonly bool unique;
        readonly bool packsPk;
        readonly bool comparesPk;
        readonly int pkBits;
        readonly ulong pkMask;

        ulong[] lNorm;
        TKey[] lFull;
        TPrimaryKey[] lPk;
        int[] lCount;
        int[] lNext;
        int[] lPrev;
        int leafUsed;
        int leafFree;

        ulong[] iNorm;
        TKey[] iFull;
        ulong[] iPk;
        int[] iChild;
        int[] iCount;
        int innerUsed;
        int innerFree;

        int root;
        int height;
        int count;

        int[] pathNode = new int[16];
        int[] pathPos = new int[16];

        EntryComparer sortComparer;

        public OrderedIndex(bool unique, int nodeSize = DefaultNodeSize)
        {
            if (nodeSize < 4) throw new ArgumentOutOfRangeException(nameof(nodeSize));

            b = nodeSize;
            this.unique = unique;
            pkBits = default(TPrimaryKeyTraits).Bits;
            pkMask = pkBits == 64 ? ulong.MaxValue : (1UL << pkBits) - 1;
            // 正規化値が順序を確定しないキー (NeedsFullComparison) では、PK を下位 bit に詰めると正規化値の比較だけで (prefix, PK) 順に並んでしまい、フルキー順にならないため詰めない
            packsPk = !unique && !NeedsFullComparison && default(TKeyTraits).NormalizedBits + pkBits <= 64;
            comparesPk = !unique && !packsPk;
            Reset(4, 4);
        }

        public int Count => count;

        public bool IsUnique => unique;

        static bool NeedsFullComparison
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default(TKeyTraits).NeedsFullComparison;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong ToU(TPrimaryKey primaryKey)
        {
            return default(TPrimaryKeyTraits).ToUInt64(primaryKey);
        }

        readonly struct Probe
        {
            public readonly ulong Norm;
            public readonly TKey Key;
            public readonly ulong Pk;

            public Probe(ulong norm, TKey key, ulong primaryKey)
            {
                Norm = norm;
                Key = key;
                Pk = primaryKey;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Probe MakeProbe(TKey key, ulong primaryKey)
        {
            var norm = default(TKeyTraits).Normalize(key);
            if (packsPk) norm = (norm << pkBits) | primaryKey;
            return new Probe(norm, key, primaryKey);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int Compare(ulong norm, TKey full, ulong primaryKey, in Probe probe)
        {
            if (norm != probe.Norm) return norm < probe.Norm ? -1 : 1;
            if (NeedsFullComparison)
            {
                var c = default(TKeyTraits).CompareFull(full, probe.Key);
                if (c != 0) return c;
            }

            if (!comparesPk) return 0;
            return primaryKey == probe.Pk ? 0 : primaryKey < probe.Pk ? -1 : 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int CompareLeaf(int slot, in Probe probe)
        {
            return Compare(lNorm[slot], NeedsFullComparison ? lFull[slot] : default, comparesPk ? ToU(lPk[slot]) : 0, probe);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int CompareInner(int slot, in Probe probe)
        {
            return Compare(iNorm[slot], NeedsFullComparison ? iFull[slot] : default, comparesPk ? iPk[slot] : 0, probe);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int CompareKey(int slot, ulong keyNorm, TKey key)
        {
            var n = packsPk ? lNorm[slot] >> pkBits : lNorm[slot];
            if (n != keyNorm) return n < keyNorm ? -1 : 1;
            return NeedsFullComparison ? default(TKeyTraits).CompareFull(lFull[slot], key) : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        TPrimaryKey PkAtSlot(int slot)
        {
            return packsPk ? default(TPrimaryKeyTraits).FromUInt64(lNorm[slot] & pkMask) : lPk[slot];
        }

        int LeafLowerBound(int leaf, in Probe probe)
        {
            var lo = 0;
            var hi = lCount[leaf];
            var baseSlot = leaf * b;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (CompareLeaf(baseSlot + mid, probe) < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        int ChildIndex(int node, in Probe probe)
        {
            var lo = 0;
            var hi = iCount[node];
            var baseSlot = node * b;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (CompareInner(baseSlot + mid, probe) <= 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo == 0 ? 0 : lo - 1;
        }

        int Descend(in Probe probe)
        {
            var node = root;
            for (var level = height; level > 0; level--)
            {
                var pos = ChildIndex(node, probe);
                pathNode[level] = node;
                pathPos[level] = pos;
                node = iChild[node * b + pos];
            }

            return node;
        }

        public bool ContainsKey(TKey key)
        {
            return TryGetFirst(key, out _);
        }

        public bool TryGetFirst(TKey key, out TPrimaryKey primaryKey)
        {
            var e = Range(key, key);
            if (e.MoveNext())
            {
                primaryKey = e.Current;
                return true;
            }

            primaryKey = default;
            return false;
        }

        public RangeEnumerator Equal(TKey key)
        {
            return Range(key, key);
        }

        // 閉区間 [min, max] を昇順に列挙する.
        public RangeEnumerator Range(TKey min, TKey max)
        {
            return RangeCore(true, min, true, true, max, true, false);
        }

        // 両端の開閉と列挙の向きを指定して範囲を列挙する.
        public RangeEnumerator Range(TKey min, bool minInclusive, TKey max, bool maxInclusive, bool descending)
        {
            return RangeCore(true, min, minInclusive, true, max, maxInclusive, descending);
        }

        // key より大きい (inclusive なら以上の) キーを列挙する. 上端は持たない.
        public RangeEnumerator GreaterThan(TKey key, bool inclusive, bool descending)
        {
            return RangeCore(true, key, inclusive, false, default, false, descending);
        }

        // key より小さい (inclusive なら以下の) キーを列挙する. 下端は持たない.
        public RangeEnumerator LessThan(TKey key, bool inclusive, bool descending)
        {
            return RangeCore(false, default, false, true, key, inclusive, descending);
        }

        // 索引の全件をキー順に列挙する.
        public RangeEnumerator All(bool descending)
        {
            return RangeCore(false, default, false, false, default, false, descending);
        }

        RangeEnumerator RangeCore(bool hasMin, TKey min, bool minInclusive, bool hasMax, TKey max, bool maxInclusive, bool descending)
        {
            var traits = default(TKeyTraits);
            if (count == 0) return default;

            if (hasMin && hasMax)
            {
                var minNorm = traits.Normalize(min);
                var maxNorm = traits.Normalize(max);
                var c = minNorm != maxNorm ? (minNorm < maxNorm ? -1 : 1) : NeedsFullComparison ? traits.CompareFull(min, max) : 0;
                if (c > 0 || (c == 0 && !(minInclusive && maxInclusive))) return default;
            }

            int leaf;
            int pos;
            if (!descending)
            {
                if (hasMin)
                {
                    Seek(min, !minInclusive, out leaf, out pos);
                }
                else
                {
                    leaf = LeftmostLeaf();
                    pos = 0;
                }

                return new RangeEnumerator(this, leaf, pos, false, hasMax, hasMax ? traits.Normalize(max) : 0, max, maxInclusive);
            }

            if (hasMax)
            {
                // 上端の直後 (inclusive なら max より大きい最初の要素, そうでなければ max 以上の最初の要素) の 1 つ前から始める.
                Seek(max, maxInclusive, out leaf, out pos);
                pos--;
            }
            else
            {
                leaf = RightmostLeaf();
                pos = lCount[leaf] - 1;
            }

            return new RangeEnumerator(this, leaf, pos, true, hasMin, hasMin ? traits.Normalize(min) : 0, min, minInclusive);
        }

        // key 以上 (after なら key より大きい) の最初の要素の位置を求める.
        // 該当が無い場合は最後の葉の末尾 (pos == lCount) を返す.
        void Seek(TKey key, bool after, out int leaf, out int pos)
        {
            // 非 Unique では (key, PK) の組で並ぶため、PK を最小値 / 最大値にした組で探す.
            // Unique では PK を比較しないため、after の場合は key と等しい要素を読み飛ばす.
            var probe = MakeProbe(key, after ? pkMask : 0);
            leaf = Descend(probe);
            pos = LeafLowerBound(leaf, probe);
            var keyNorm = default(TKeyTraits).Normalize(key);
            while (true)
            {
                if (pos >= lCount[leaf])
                {
                    var next = lNext[leaf];
                    if (next < 0) return;
                    leaf = next;
                    pos = 0;
                    continue;
                }

                if (!after || CompareKey(leaf * b + pos, keyNorm, key) != 0) return;
                pos++;
            }
        }

        public struct RangeEnumerator : IPrimaryKeyEnumerator<TPrimaryKey>
        {
            readonly OrderedIndex<TKey, TKeyTraits, TPrimaryKey, TPrimaryKeyTraits> index;
            readonly ulong endNorm;
            readonly TKey end;
            readonly bool descending;
            readonly bool hasEnd;
            readonly bool endInclusive;
            int leaf;
            int pos;
            TPrimaryKey current;

            // default の RangeEnumerator は空の列挙とする (index == null).
            internal RangeEnumerator(OrderedIndex<TKey, TKeyTraits, TPrimaryKey, TPrimaryKeyTraits> index, int leaf, int pos, bool descending, bool hasEnd, ulong endNorm, TKey end, bool endInclusive)
            {
                this.index = index;
                this.leaf = leaf;
                this.pos = pos;
                this.descending = descending;
                this.hasEnd = hasEnd;
                this.endNorm = endNorm;
                this.end = end;
                this.endInclusive = endInclusive;
                current = default;
            }

            public TPrimaryKey Current => current;

            public bool MoveNext()
            {
                if (index == null) return false;
                return descending ? MovePrev() : MoveForward();
            }

            bool MoveForward()
            {
                while (leaf >= 0)
                {
                    if (pos < index.lCount[leaf])
                    {
                        var slot = leaf * index.b + pos;
                        if (hasEnd)
                        {
                            var c = index.CompareKey(slot, endNorm, end);
                            if (c > 0 || (c == 0 && !endInclusive))
                            {
                                leaf = -1;
                                return false;
                            }
                        }

                        current = index.PkAtSlot(slot);
                        pos++;
                        return true;
                    }

                    leaf = index.lNext[leaf];
                    pos = 0;
                }

                return false;
            }

            bool MovePrev()
            {
                while (leaf >= 0)
                {
                    if (pos >= 0)
                    {
                        var slot = leaf * index.b + pos;
                        if (hasEnd)
                        {
                            var c = index.CompareKey(slot, endNorm, end);
                            if (c < 0 || (c == 0 && !endInclusive))
                            {
                                leaf = -1;
                                return false;
                            }
                        }

                        current = index.PkAtSlot(slot);
                        pos--;
                        return true;
                    }

                    leaf = index.lPrev[leaf];
                    pos = leaf >= 0 ? index.lCount[leaf] - 1 : -1;
                }

                return false;
            }

            public RangeEnumerator GetEnumerator()
            {
                return this;
            }
        }

        public bool Insert(TKey key, TPrimaryKey primaryKey)
        {
            var probe = MakeProbe(key, ToU(primaryKey));
            var leaf = Descend(probe);
            var pos = LeafLowerBound(leaf, probe);
            if (pos < lCount[leaf] && CompareLeaf(leaf * b + pos, probe) == 0) return false;

            count++;
            if (lCount[leaf] < b)
            {
                LeafInsertAt(leaf, pos, probe.Norm, key, primaryKey);
                return true;
            }

            var split = pos == b && lNext[leaf] < 0 ? b : b / 2;
            var right = AllocLeaf();
            MoveLeafEntries(leaf, split, right, b - split);
            lCount[leaf] = split;
            lCount[right] = b - split;

            var next = lNext[leaf];
            lNext[right] = next;
            lPrev[right] = leaf;
            if (next >= 0) lPrev[next] = right;
            lNext[leaf] = right;

            if (pos < split)
            {
                LeafInsertAt(leaf, pos, probe.Norm, key, primaryKey);
            }
            else
            {
                LeafInsertAt(right, pos - split, probe.Norm, key, primaryKey);
            }

            var rs = right * b;
            PromoteSplit(1, lNorm[rs], NeedsFullComparison ? lFull[rs] : default, comparesPk ? ToU(lPk[rs]) : 0, right, leaf);
            return true;
        }

        void LeafInsertAt(int leaf, int pos, ulong norm, TKey key, TPrimaryKey primaryKey)
        {
            var baseSlot = leaf * b;
            var n = lCount[leaf];
            var move = n - pos;
            if (move > 0)
            {
                Array.Copy(lNorm, baseSlot + pos, lNorm, baseSlot + pos + 1, move);
                if (NeedsFullComparison) Array.Copy(lFull, baseSlot + pos, lFull, baseSlot + pos + 1, move);
                if (!packsPk) Array.Copy(lPk, baseSlot + pos, lPk, baseSlot + pos + 1, move);
            }

            lNorm[baseSlot + pos] = norm;
            if (NeedsFullComparison) lFull[baseSlot + pos] = key;
            if (!packsPk) lPk[baseSlot + pos] = primaryKey;
            lCount[leaf] = n + 1;
        }

        void MoveLeafEntries(int from, int fromPos, int to, int n)
        {
            if (n <= 0) return;
            var src = from * b + fromPos;
            var dst = to * b;
            Array.Copy(lNorm, src, lNorm, dst, n);
            if (NeedsFullComparison)
            {
                Array.Copy(lFull, src, lFull, dst, n);
                Array.Clear(lFull, src, n);
            }

            if (!packsPk) Array.Copy(lPk, src, lPk, dst, n);
        }

        void PromoteSplit(int level, ulong sepNorm, TKey sepFull, ulong sepPk, int child, int leftChild)
        {
            while (true)
            {
                if (level > height)
                {
                    var newRoot = AllocInner();
                    var s = newRoot * b;
                    SetInner(s, 0, default, 0, leftChild);
                    SetInner(s + 1, sepNorm, sepFull, sepPk, child);
                    iCount[newRoot] = 2;
                    root = newRoot;
                    height++;
                    EnsurePathCapacity();
                    return;
                }

                var node = pathNode[level];
                var pos = pathPos[level] + 1;
                if (iCount[node] < b)
                {
                    InnerInsertAt(node, pos, sepNorm, sepFull, sepPk, child);
                    return;
                }

                var split = pos == b ? b : b / 2;
                var right = AllocInner();
                var src = node * b + split;
                var dst = right * b;
                var n = b - split;
                if (n > 0)
                {
                    Array.Copy(iNorm, src, iNorm, dst, n);
                    if (NeedsFullComparison)
                    {
                        Array.Copy(iFull, src, iFull, dst, n);
                        Array.Clear(iFull, src, n);
                    }

                    if (comparesPk) Array.Copy(iPk, src, iPk, dst, n);
                    Array.Copy(iChild, src, iChild, dst, n);
                }

                iCount[node] = split;
                iCount[right] = n;
                if (pos <= split && split != b)
                {
                    InnerInsertAt(node, pos, sepNorm, sepFull, sepPk, child);
                }
                else
                {
                    InnerInsertAt(right, pos - split, sepNorm, sepFull, sepPk, child);
                }

                var rs = right * b;
                sepNorm = iNorm[rs];
                sepFull = NeedsFullComparison ? iFull[rs] : default;
                sepPk = comparesPk ? iPk[rs] : 0;
                child = right;
                leftChild = node;
                level++;
            }
        }

        void InnerInsertAt(int node, int pos, ulong norm, TKey full, ulong primaryKey, int child)
        {
            var baseSlot = node * b;
            var move = iCount[node] - pos;
            if (move > 0)
            {
                Array.Copy(iNorm, baseSlot + pos, iNorm, baseSlot + pos + 1, move);
                if (NeedsFullComparison) Array.Copy(iFull, baseSlot + pos, iFull, baseSlot + pos + 1, move);
                if (comparesPk) Array.Copy(iPk, baseSlot + pos, iPk, baseSlot + pos + 1, move);
                Array.Copy(iChild, baseSlot + pos, iChild, baseSlot + pos + 1, move);
            }

            SetInner(baseSlot + pos, norm, full, primaryKey, child);
            iCount[node]++;
        }

        void SetInner(int slot, ulong norm, TKey full, ulong primaryKey, int child)
        {
            iNorm[slot] = norm;
            if (NeedsFullComparison) iFull[slot] = full;
            if (comparesPk) iPk[slot] = primaryKey;
            iChild[slot] = child;
        }

        public bool Remove(TKey key, TPrimaryKey primaryKey)
        {
            if (count == 0) return false;

            var pkValue = ToU(primaryKey);
            var probe = MakeProbe(key, pkValue);
            var leaf = Descend(probe);
            var pos = LeafLowerBound(leaf, probe);
            var baseSlot = leaf * b;
            if (pos >= lCount[leaf] || CompareLeaf(baseSlot + pos, probe) != 0) return false;
            if (unique && ToU(lPk[baseSlot + pos]) != pkValue) return false;

            var n = lCount[leaf] - 1;
            var move = n - pos;
            if (move > 0)
            {
                Array.Copy(lNorm, baseSlot + pos + 1, lNorm, baseSlot + pos, move);
                if (NeedsFullComparison) Array.Copy(lFull, baseSlot + pos + 1, lFull, baseSlot + pos, move);
                if (!packsPk) Array.Copy(lPk, baseSlot + pos + 1, lPk, baseSlot + pos, move);
            }

            if (NeedsFullComparison) lFull[baseSlot + n] = default;
            lCount[leaf] = n;
            count--;

            if (height == 0) return true;

            if (n == 0)
            {
                UnlinkAndFreeLeaf(leaf);
                RemoveFromParent(1);
            }
            else if (n <= b / 4)
            {
                Rebalance(leaf, n);
            }

            return true;
        }

        // 充填が 1/4 以下になった葉を、同じ親の隣の葉と併合するか、隣の葉から借りて均す.
        // 触るのは葉 2 枚と親 1 つだけなので O(b) で済む. 内部ノードは空になるまで縮めない.
        void Rebalance(int leaf, int n)
        {
            var parent = pathNode[1];
            var pos = pathPos[1];
            var parentBase = parent * b;
            if (pos > 0)
            {
                var left = iChild[parentBase + pos - 1];
                var s = lCount[left];
                if (s + n <= b)
                {
                    // 左の葉の末尾へ移し、この葉を外す.
                    CopyLeafEntries(leaf, 0, left, s, n);
                    lCount[left] = s + n;
                    lCount[leaf] = 0;
                    UnlinkAndFreeLeaf(leaf);
                    RemoveFromParent(1);
                    return;
                }

                // 左の葉の末尾から借りる. この葉の最小キーが変わるため区切りを更新する.
                var m = (s - n) / 2;
                CopyLeafEntries(leaf, 0, leaf, m, n);
                CopyLeafEntries(left, s - m, leaf, 0, m);
                lCount[left] = s - m;
                lCount[leaf] = n + m;
                SetSeparator(parentBase + pos, leaf);
                return;
            }

            if (pos + 1 < iCount[parent])
            {
                var right = iChild[parentBase + pos + 1];
                var s = lCount[right];
                if (s + n <= b)
                {
                    // 右の葉をこの葉の末尾へ移し、右の葉を外す.
                    CopyLeafEntries(right, 0, leaf, n, s);
                    lCount[leaf] = n + s;
                    lCount[right] = 0;
                    UnlinkAndFreeLeaf(right);
                    pathPos[1] = pos + 1;
                    RemoveFromParent(1);
                    return;
                }

                // 右の葉の先頭から借りる. 右の葉の最小キーが変わるため区切りを更新する.
                var m = (s - n) / 2;
                CopyLeafEntries(right, 0, leaf, n, m);
                CopyLeafEntries(right, m, right, 0, s - m);
                lCount[leaf] = n + m;
                lCount[right] = s - m;
                SetSeparator(parentBase + pos + 1, right);
            }
        }

        // 重なりのある移動にも使える. 移動元で使わなくなった範囲のキーは消す.
        void CopyLeafEntries(int from, int fromPos, int to, int toPos, int n)
        {
            if (n <= 0) return;
            var src = from * b + fromPos;
            var dst = to * b + toPos;
            Array.Copy(lNorm, src, lNorm, dst, n);
            if (NeedsFullComparison)
            {
                Array.Copy(lFull, src, lFull, dst, n);
                if (src + n <= dst || dst + n <= src)
                {
                    Array.Clear(lFull, src, n);
                }
                else if (src < dst)
                {
                    Array.Clear(lFull, src, dst - src);
                }
                else
                {
                    Array.Clear(lFull, dst + n, src - dst);
                }
            }

            if (!packsPk) Array.Copy(lPk, src, lPk, dst, n);
        }

        void SetSeparator(int innerSlot, int leaf)
        {
            var ls = leaf * b;
            iNorm[innerSlot] = lNorm[ls];
            if (NeedsFullComparison) iFull[innerSlot] = lFull[ls];
            if (comparesPk) iPk[innerSlot] = ToU(lPk[ls]);
        }

        void UnlinkAndFreeLeaf(int leaf)
        {
            var prev = lPrev[leaf];
            var next = lNext[leaf];
            if (prev >= 0) lNext[prev] = next;
            if (next >= 0) lPrev[next] = prev;
            FreeLeaf(leaf);
        }

        void RemoveFromParent(int level)
        {
            while (true)
            {
                var node = pathNode[level];
                var pos = pathPos[level];
                var baseSlot = node * b;
                var n = iCount[node] - 1;
                var move = n - pos;
                if (move > 0)
                {
                    Array.Copy(iNorm, baseSlot + pos + 1, iNorm, baseSlot + pos, move);
                    if (NeedsFullComparison) Array.Copy(iFull, baseSlot + pos + 1, iFull, baseSlot + pos, move);
                    if (comparesPk) Array.Copy(iPk, baseSlot + pos + 1, iPk, baseSlot + pos, move);
                    Array.Copy(iChild, baseSlot + pos + 1, iChild, baseSlot + pos, move);
                }

                if (NeedsFullComparison) iFull[baseSlot + n] = default;
                iCount[node] = n;

                if (level == height)
                {
                    while (height > 0 && iCount[root] == 1)
                    {
                        var old = root;
                        root = iChild[old * b];
                        FreeInner(old);
                        height--;
                    }

                    return;
                }

                if (n > 0) return;
                FreeInner(node);
                level++;
            }
        }

        public bool AddRange(ReadOnlySpan<TKey> keys, ReadOnlySpan<TPrimaryKey> primaryKeys)
        {
            if (primaryKeys.Length < keys.Length) throw new ArgumentException("primaryKeys is shorter than keys.", nameof(primaryKeys));

            var k = keys.Length;
            if (k == 0) return true;

            if (k < count / RebuildRatio)
            {
                for (var i = 0; i < k; i++)
                {
                    if (Insert(keys[i], primaryKeys[i])) continue;
                    for (var j = 0; j < i; j++)
                    {
                        Remove(keys[j], primaryKeys[j]);
                    }

                    return false;
                }

                return true;
            }

            return MergeRebuild(keys, primaryKeys);
        }

        public void TrimExcess()
        {
            var buf = new EntryBuffer(count);
            try
            {
                var w = 0;
                for (var leaf = LeftmostLeaf(); leaf >= 0; leaf = lNext[leaf])
                {
                    var baseSlot = leaf * b;
                    for (var i = 0; i < lCount[leaf]; i++)
                    {
                        buf.Set(w++, lNorm[baseSlot + i], NeedsFullComparison ? lFull[baseSlot + i] : default, PkAtSlot(baseSlot + i));
                    }
                }

                Build(buf, w, true);
            }
            finally
            {
                buf.Return();
            }
        }

        bool MergeRebuild(ReadOnlySpan<TKey> keys, ReadOnlySpan<TPrimaryKey> primaryKeys)
        {
            var k = keys.Length;
            var incoming = new EntryBuffer(k);
            var order = ArrayPool<int>.Shared.Rent(k);
            var merged = new EntryBuffer(count + k);
            try
            {
                for (var i = 0; i < k; i++)
                {
                    var probe = MakeProbe(keys[i], ToU(primaryKeys[i]));
                    incoming.Set(i, probe.Norm, keys[i], primaryKeys[i]);
                    order[i] = i;
                }

                sortComparer ??= new EntryComparer(this);
                sortComparer.target = incoming;
                Array.Sort(order, 0, k, sortComparer);
                sortComparer.target = default;

                var w = 0;
                var leaf = LeftmostLeaf();
                var pos = 0;
                var j = 0;
                while (j < k)
                {
                    while (leaf >= 0 && pos >= lCount[leaf])
                    {
                        leaf = lNext[leaf];
                        pos = 0;
                    }

                    var src = order[j];
                    var probe = new Probe(incoming.Norm[src], NeedsFullComparison ? incoming.Full[src] : default, ToU(incoming.Pk[src]));
                    if (j > 0)
                    {
                        var prev = order[j - 1];
                        if (Compare(incoming.Norm[prev], NeedsFullComparison ? incoming.Full[prev] : default, ToU(incoming.Pk[prev]), probe) == 0) return false;
                    }

                    var c = leaf >= 0 ? CompareLeaf(leaf * b + pos, probe) : 1;
                    if (c == 0) return false;
                    if (c < 0)
                    {
                        var slot = leaf * b + pos;
                        merged.Set(w++, lNorm[slot], NeedsFullComparison ? lFull[slot] : default, PkAtSlot(slot));
                        pos++;
                    }
                    else
                    {
                        merged.Set(w++, incoming.Norm[src], NeedsFullComparison ? incoming.Full[src] : default, incoming.Pk[src]);
                        j++;
                    }
                }

                for (; leaf >= 0; leaf = lNext[leaf], pos = 0)
                {
                    for (; pos < lCount[leaf]; pos++)
                    {
                        var slot = leaf * b + pos;
                        merged.Set(w++, lNorm[slot], NeedsFullComparison ? lFull[slot] : default, PkAtSlot(slot));
                    }
                }

                Build(merged, w, false);
                return true;
            }
            finally
            {
                incoming.Return();
                merged.Return();
                ArrayPool<int>.Shared.Return(order);
            }
        }

        // exact = true (TrimExcess) なら必要な分だけ確保し直す.
        // false なら容量が足りる限り既存の配列を使い回し、足りなければ倍以上に広げて確保する.
        // 呼び出し側は entries を木とは別のバッファに集め終えているため、使い回しても読み途中のデータは壊れない.
        void Build(in EntryBuffer entries, int n, bool exact)
        {
            var leaves = Math.Max(1, (n + b - 1) / b);
            var inners = 0;
            for (var width = leaves; width > 1; width = (width + b - 1) / b)
            {
                inners += (width + b - 1) / b;
            }

            PrepareLeaves(leaves, exact);
            PrepareInners(Math.Max(1, inners), exact);
            root = AllocLeaf();
            height = 0;
            count = n;
            if (n == 0) return;

            var level = ArrayPool<int>.Shared.Rent(leaves);
            var upper = ArrayPool<int>.Shared.Rent((leaves + b - 1) / b);
            try
            {
                leafUsed = 0;
                var prevLeaf = -1;
                for (var l = 0; l < leaves; l++)
                {
                    var leaf = AllocLeaf();
                    var start = l * b;
                    var cnt = Math.Min(b, n - start);
                    var baseSlot = leaf * b;
                    Array.Copy(entries.Norm, start, lNorm, baseSlot, cnt);
                    if (NeedsFullComparison) Array.Copy(entries.Full, start, lFull, baseSlot, cnt);
                    if (!packsPk) Array.Copy(entries.Pk, start, lPk, baseSlot, cnt);

                    lCount[leaf] = cnt;
                    lPrev[leaf] = prevLeaf;
                    if (prevLeaf >= 0) lNext[prevLeaf] = leaf;
                    prevLeaf = leaf;
                    level[l] = leaf;
                }

                var width = leaves;
                var isLeafLevel = true;
                while (width > 1)
                {
                    var parents = (width + b - 1) / b;
                    for (var p = 0; p < parents; p++)
                    {
                        var node = AllocInner();
                        var start = p * b;
                        var cnt = Math.Min(b, width - start);
                        for (var i = 0; i < cnt; i++)
                        {
                            var child = level[start + i];
                            var cs = child * b;
                            if (isLeafLevel)
                            {
                                SetInner(node * b + i, lNorm[cs], NeedsFullComparison ? lFull[cs] : default, comparesPk ? ToU(lPk[cs]) : 0, child);
                            }
                            else
                            {
                                SetInner(node * b + i, iNorm[cs], NeedsFullComparison ? iFull[cs] : default, comparesPk ? iPk[cs] : 0, child);
                            }
                        }

                        iCount[node] = cnt;
                        upper[p] = node;
                    }

                    var swap = level;
                    level = upper;
                    upper = swap;
                    width = parents;
                    isLeafLevel = false;
                    height++;
                }

                root = level[0];
                EnsurePathCapacity();
            }
            finally
            {
                ArrayPool<int>.Shared.Return(level);
                ArrayPool<int>.Shared.Return(upper);
            }
        }

        int LeftmostLeaf()
        {
            var node = root;
            for (var level = height; level > 0; level--)
            {
                node = iChild[node * b];
            }

            return node;
        }

        int RightmostLeaf()
        {
            var node = root;
            for (var level = height; level > 0; level--)
            {
                node = iChild[node * b + iCount[node] - 1];
            }

            return node;
        }

        readonly struct EntryBuffer
        {
            public readonly ulong[] Norm;
            public readonly TKey[] Full;
            public readonly TPrimaryKey[] Pk;

            public EntryBuffer(int n)
            {
                Norm = ArrayPool<ulong>.Shared.Rent(Math.Max(1, n));
                Full = NeedsFullComparison ? ArrayPool<TKey>.Shared.Rent(Math.Max(1, n)) : null;
                Pk = ArrayPool<TPrimaryKey>.Shared.Rent(Math.Max(1, n));
            }

            public void Set(int i, ulong norm, TKey full, TPrimaryKey primaryKey)
            {
                Norm[i] = norm;
                if (Full != null) Full[i] = full;
                Pk[i] = primaryKey;
            }

            public void Return()
            {
                if (Norm == null) return;
                ArrayPool<ulong>.Shared.Return(Norm);
                if (Full != null) ArrayPool<TKey>.Shared.Return(Full, true);
                ArrayPool<TPrimaryKey>.Shared.Return(Pk);
            }
        }

        sealed class EntryComparer : IComparer<int>
        {
            readonly OrderedIndex<TKey, TKeyTraits, TPrimaryKey, TPrimaryKeyTraits> index;
            public EntryBuffer target;

            public EntryComparer(OrderedIndex<TKey, TKeyTraits, TPrimaryKey, TPrimaryKeyTraits> index)
            {
                this.index = index;
            }

            public int Compare(int x, int y)
            {
                var t = target;
                var probe = new Probe(t.Norm[y], NeedsFullComparison ? t.Full[y] : default, ToU(t.Pk[y]));
                return index.Compare(t.Norm[x], NeedsFullComparison ? t.Full[x] : default, ToU(t.Pk[x]), probe);
            }
        }

        void Reset(int leafCapacity, int innerCapacity)
        {
            PrepareLeaves(leafCapacity, true);
            PrepareInners(innerCapacity, true);
            root = AllocLeaf();
            height = 0;
            count = 0;
        }

        void PrepareLeaves(int capacity, bool exact)
        {
            if (exact || lCount == null || lCount.Length < capacity)
            {
                if (!exact && lCount != null) capacity = Math.Max(capacity, lCount.Length * 2);
                lNorm = new ulong[capacity * b];
                lFull = NeedsFullComparison ? new TKey[capacity * b] : null;
                lPk = packsPk ? null : new TPrimaryKey[capacity * b];
                lCount = new int[capacity];
                lNext = new int[capacity];
                lPrev = new int[capacity];
            }
            else if (NeedsFullComparison)
            {
                // 参照を残さないよう、使っていた範囲のキーを消す.
                Array.Clear(lFull, 0, leafUsed * b);
            }

            leafUsed = 0;
            leafFree = -1;
        }

        void PrepareInners(int capacity, bool exact)
        {
            if (exact || iCount == null || iCount.Length < capacity)
            {
                if (!exact && iCount != null) capacity = Math.Max(capacity, iCount.Length * 2);
                iNorm = new ulong[capacity * b];
                iFull = NeedsFullComparison ? new TKey[capacity * b] : null;
                iPk = comparesPk ? new ulong[capacity * b] : null;
                iChild = new int[capacity * b];
                iCount = new int[capacity];
            }
            else if (NeedsFullComparison)
            {
                Array.Clear(iFull, 0, innerUsed * b);
            }

            innerUsed = 0;
            innerFree = -1;
        }

        void EnsurePathCapacity()
        {
            if (height < pathNode.Length) return;
            Array.Resize(ref pathNode, pathNode.Length * 2);
            Array.Resize(ref pathPos, pathPos.Length * 2);
        }

        int AllocLeaf()
        {
            int leaf;
            if (leafFree >= 0)
            {
                leaf = leafFree;
                leafFree = lNext[leaf];
            }
            else
            {
                if (leafUsed == lCount.Length)
                {
                    var cap = lCount.Length * 2;
                    Array.Resize(ref lNorm, cap * b);
                    if (NeedsFullComparison) Array.Resize(ref lFull, cap * b);
                    if (!packsPk) Array.Resize(ref lPk, cap * b);
                    Array.Resize(ref lCount, cap);
                    Array.Resize(ref lNext, cap);
                    Array.Resize(ref lPrev, cap);
                }

                leaf = leafUsed++;
            }

            lCount[leaf] = 0;
            lNext[leaf] = -1;
            lPrev[leaf] = -1;
            return leaf;
        }

        void FreeLeaf(int leaf)
        {
            lNext[leaf] = leafFree;
            leafFree = leaf;
        }

        int AllocInner()
        {
            int node;
            if (innerFree >= 0)
            {
                node = innerFree;
                innerFree = iCount[node];
            }
            else
            {
                if (innerUsed == iCount.Length)
                {
                    var cap = iCount.Length * 2;
                    Array.Resize(ref iNorm, cap * b);
                    if (NeedsFullComparison) Array.Resize(ref iFull, cap * b);
                    if (comparesPk) Array.Resize(ref iPk, cap * b);
                    Array.Resize(ref iChild, cap * b);
                    Array.Resize(ref iCount, cap);
                }

                node = innerUsed++;
            }

            iCount[node] = 0;
            return node;
        }

        void FreeInner(int node)
        {
            if (NeedsFullComparison) Array.Clear(iFull, node * b, b);
            iCount[node] = innerFree;
            innerFree = node;
        }
    }
}
