using System;
using System.Runtime.CompilerServices;

namespace OrdoMemory.Bench.Sec
{
    // S3: 配列アリーナ上の B+Tree (設計案).
    // ノードはオブジェクトにせず、葉と内部ノードそれぞれ数本の配列に置いて int の番号で指す.
    // 比較はまず正規化キー (ulong) で行い、等しいときだけ完全なキー / PK を見る.
    // 内部ノードの各エントリは (子の最小キー, 子). 子の探索は「最小キー <= 探索キー となる最後の子」 (先頭の子は下限なし).
    // 削除は空になったノードだけを解放する (free-at-empty).
    public sealed class ArenaBTree<TKey, TTraits> : ISecIndex<TKey>
        where TTraits : struct, IKeyTraits<TKey>
    {
        readonly int b;
        TTraits traits;

        // 葉: スロット [leaf * b, leaf * b + b)
        ulong[] lNorm;
        TKey[] lFull;     // NeedsFull のときのみ
        int[] lPk;        // !PacksPk のときのみ
        int[] lCount;
        int[] lNext;
        int[] lPrev;
        int leafCap;
        int leafUsed;
        int leafFree = -1;

        // 内部ノード
        ulong[] iNorm;
        TKey[] iFull;
        int[] iPk;
        int[] iChild;
        int[] iCount;
        int innerCap;
        int innerUsed;
        int innerFree = -1;

        int root = -1;
        int height;       // 0 = 根が葉

        int[] pathNode = new int[32];
        int[] pathPos = new int[32];

        public ArenaBTree(int nodeSize)
        {
            b = nodeSize;
            traits = default;
            leafCap = 4;
            lNorm = new ulong[leafCap * b];
            lFull = traits.NeedsFull ? new TKey[leafCap * b] : null;
            lPk = traits.PacksPk ? null : new int[leafCap * b];
            lCount = new int[leafCap];
            lNext = new int[leafCap];
            lPrev = new int[leafCap];

            innerCap = 4;
            iNorm = new ulong[innerCap * b];
            iFull = traits.NeedsFull ? new TKey[innerCap * b] : null;
            iPk = traits.PacksPk ? null : new int[innerCap * b];
            iChild = new int[innerCap * b];
            iCount = new int[innerCap];
        }

        public string Name => $"S3 Arena B+Tree B={b}";
        public bool SupportsRange => true;

        // ---- 比較 ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int CompareLeaf(int slot, ulong norm, TKey key, int pk)
        {
            var n = lNorm[slot];
            if (n != norm) return n < norm ? -1 : 1;
            if (traits.NeedsFull)
            {
                var c = traits.CompareFull(lFull[slot], key);
                if (c != 0) return c;
            }

            if (traits.PacksPk) return 0;
            var p = lPk[slot];
            return p == pk ? 0 : (p < pk ? -1 : 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int CompareInner(int slot, ulong norm, TKey key, int pk)
        {
            var n = iNorm[slot];
            if (n != norm) return n < norm ? -1 : 1;
            if (traits.NeedsFull)
            {
                var c = traits.CompareFull(iFull[slot], key);
                if (c != 0) return c;
            }

            if (traits.PacksPk) return 0;
            var p = iPk[slot];
            return p == pk ? 0 : (p < pk ? -1 : 1);
        }

        // 葉の中で probe 以上となる最初の位置.
        int LeafLowerBound(int leaf, ulong norm, TKey key, int pk)
        {
            var lo = 0;
            var hi = lCount[leaf];
            var baseSlot = leaf * b;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (CompareLeaf(baseSlot + mid, norm, key, pk) < 0) lo = mid + 1;
                else hi = mid;
            }

            return lo;
        }

        // 内部ノードで、最小キー <= probe となる最後の子の位置 (無ければ 0).
        int ChildIndex(int node, ulong norm, TKey key, int pk)
        {
            var lo = 0;
            var hi = iCount[node];
            var baseSlot = node * b;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (CompareInner(baseSlot + mid, norm, key, pk) <= 0) lo = mid + 1;
                else hi = mid;
            }

            return lo == 0 ? 0 : lo - 1;
        }

        int Descend(ulong norm, TKey key, int pk)
        {
            var node = root;
            for (var level = height; level > 0; level--)
            {
                var pos = ChildIndex(node, norm, key, pk);
                pathNode[level] = node;
                pathPos[level] = pos;
                node = iChild[node * b + pos];
            }

            return node;
        }

        // ---- 挿入 ----

        public bool Insert(TKey key, int pk)
        {
            var norm = traits.Normalize(key, pk);
            if (root < 0)
            {
                root = AllocLeaf();
                height = 0;
            }

            var leaf = Descend(norm, key, pk);
            var pos = LeafLowerBound(leaf, norm, key, pk);
            if (pos < lCount[leaf] && CompareLeaf(leaf * b + pos, norm, key, pk) == 0) return false;

            if (lCount[leaf] < b)
            {
                LeafInsertAt(leaf, pos, norm, key, pk);
                return true;
            }

            // 分割. 右端の葉の末尾への追記は、新しい葉に新エントリだけを置く (葉が満杯のまま並ぶ).
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

            if (pos < split) LeafInsertAt(leaf, pos, norm, key, pk);
            else LeafInsertAt(right, pos - split, norm, key, pk);

            var rs = right * b;
            PromoteSplit(1, lNorm[rs], traits.NeedsFull ? lFull[rs] : default, traits.PacksPk ? 0 : lPk[rs], right, leaf);
            return true;
        }

        void LeafInsertAt(int leaf, int pos, ulong norm, TKey key, int pk)
        {
            var baseSlot = leaf * b;
            var count = lCount[leaf];
            var move = count - pos;
            if (move > 0)
            {
                Array.Copy(lNorm, baseSlot + pos, lNorm, baseSlot + pos + 1, move);
                if (traits.NeedsFull) Array.Copy(lFull, baseSlot + pos, lFull, baseSlot + pos + 1, move);
                if (!traits.PacksPk) Array.Copy(lPk, baseSlot + pos, lPk, baseSlot + pos + 1, move);
            }

            lNorm[baseSlot + pos] = norm;
            if (traits.NeedsFull) lFull[baseSlot + pos] = key;
            if (!traits.PacksPk) lPk[baseSlot + pos] = pk;
            lCount[leaf] = count + 1;
        }

        void MoveLeafEntries(int from, int fromPos, int to, int n)
        {
            if (n <= 0) return;
            var src = from * b + fromPos;
            var dst = to * b;
            Array.Copy(lNorm, src, lNorm, dst, n);
            if (traits.NeedsFull)
            {
                Array.Copy(lFull, src, lFull, dst, n);
                Array.Clear(lFull, src, n);
            }

            if (!traits.PacksPk) Array.Copy(lPk, src, lPk, dst, n);
        }

        // level の親に (sep, child) を追加する. leftChild は分割前のノード (根の分割時に使う).
        void PromoteSplit(int level, ulong sepNorm, TKey sepFull, int sepPk, int child, int leftChild)
        {
            while (true)
            {
                if (level > height)
                {
                    // 根の分割. 新しい根は [左 (下限なし), 右].
                    var newRoot = AllocInner();
                    var s = newRoot * b;
                    iNorm[s] = 0;
                    if (traits.NeedsFull) iFull[s] = default;
                    if (!traits.PacksPk) iPk[s] = 0;
                    iChild[s] = leftChild;
                    SetInner(s + 1, sepNorm, sepFull, sepPk, child);
                    iCount[newRoot] = 2;
                    root = newRoot;
                    height++;
                    if (height >= pathNode.Length)
                    {
                        Array.Resize(ref pathNode, pathNode.Length * 2);
                        Array.Resize(ref pathPos, pathPos.Length * 2);
                    }

                    return;
                }

                var node = pathNode[level];
                var pos = pathPos[level] + 1;
                var count = iCount[node];
                if (count < b)
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
                    if (traits.NeedsFull)
                    {
                        Array.Copy(iFull, src, iFull, dst, n);
                        Array.Clear(iFull, src, n);
                    }

                    if (!traits.PacksPk) Array.Copy(iPk, src, iPk, dst, n);
                    Array.Copy(iChild, src, iChild, dst, n);
                }

                iCount[node] = split;
                iCount[right] = n;
                if (pos <= split && split != b) InnerInsertAt(node, pos, sepNorm, sepFull, sepPk, child);
                else InnerInsertAt(right, pos - split, sepNorm, sepFull, sepPk, child);

                var rs = right * b;
                sepNorm = iNorm[rs];
                sepFull = traits.NeedsFull ? iFull[rs] : default;
                sepPk = traits.PacksPk ? 0 : iPk[rs];
                child = right;
                leftChild = node;
                level++;
            }
        }

        void InnerInsertAt(int node, int pos, ulong norm, TKey full, int pk, int child)
        {
            var baseSlot = node * b;
            var move = iCount[node] - pos;
            if (move > 0)
            {
                Array.Copy(iNorm, baseSlot + pos, iNorm, baseSlot + pos + 1, move);
                if (traits.NeedsFull) Array.Copy(iFull, baseSlot + pos, iFull, baseSlot + pos + 1, move);
                if (!traits.PacksPk) Array.Copy(iPk, baseSlot + pos, iPk, baseSlot + pos + 1, move);
                Array.Copy(iChild, baseSlot + pos, iChild, baseSlot + pos + 1, move);
            }

            SetInner(baseSlot + pos, norm, full, pk, child);
            iCount[node]++;
        }

        void SetInner(int slot, ulong norm, TKey full, int pk, int child)
        {
            iNorm[slot] = norm;
            if (traits.NeedsFull) iFull[slot] = full;
            if (!traits.PacksPk) iPk[slot] = pk;
            iChild[slot] = child;
        }

        // ---- 削除 ----

        public bool Remove(TKey key, int pk)
        {
            if (root < 0) return false;
            var norm = traits.Normalize(key, pk);
            var leaf = Descend(norm, key, pk);
            var pos = LeafLowerBound(leaf, norm, key, pk);
            if (pos >= lCount[leaf] || CompareLeaf(leaf * b + pos, norm, key, pk) != 0) return false;

            var baseSlot = leaf * b;
            var count = lCount[leaf] - 1;
            var move = count - pos;
            if (move > 0)
            {
                Array.Copy(lNorm, baseSlot + pos + 1, lNorm, baseSlot + pos, move);
                if (traits.NeedsFull) Array.Copy(lFull, baseSlot + pos + 1, lFull, baseSlot + pos, move);
                if (!traits.PacksPk) Array.Copy(lPk, baseSlot + pos + 1, lPk, baseSlot + pos, move);
            }

            if (traits.NeedsFull) lFull[baseSlot + count] = default;
            lCount[leaf] = count;

            if (count == 0 && height > 0)
            {
                var prev = lPrev[leaf];
                var next = lNext[leaf];
                if (prev >= 0) lNext[prev] = next;
                if (next >= 0) lPrev[next] = prev;
                FreeLeaf(leaf);
                RemoveFromParent(1);
            }

            return true;
        }

        // level のノードから pathPos[level] の子を外す. 空になったら親からも外す.
        void RemoveFromParent(int level)
        {
            while (true)
            {
                var node = pathNode[level];
                var pos = pathPos[level];
                var baseSlot = node * b;
                var count = iCount[node] - 1;
                var move = count - pos;
                if (move > 0)
                {
                    Array.Copy(iNorm, baseSlot + pos + 1, iNorm, baseSlot + pos, move);
                    if (traits.NeedsFull) Array.Copy(iFull, baseSlot + pos + 1, iFull, baseSlot + pos, move);
                    if (!traits.PacksPk) Array.Copy(iPk, baseSlot + pos + 1, iPk, baseSlot + pos, move);
                    Array.Copy(iChild, baseSlot + pos + 1, iChild, baseSlot + pos, move);
                }

                if (traits.NeedsFull) iFull[baseSlot + count] = default;
                iCount[node] = count;

                if (level == height)
                {
                    // 子が 1 つの根は取り除く (新しい根も子が 1 つなら繰り返す).
                    while (height > 0 && iCount[root] == 1)
                    {
                        var old = root;
                        root = iChild[old * b];
                        FreeInner(old);
                        height--;
                    }

                    if (height > 0 && iCount[root] == 0)
                    {
                        FreeInner(root);
                        root = AllocLeaf();
                        height = 0;
                    }

                    return;
                }

                if (count > 0) return;
                FreeInner(node);
                level++;
            }
        }

        // ---- 検索 ----

        public long SumEqual(TKey key, out int count)
        {
            return SumRange(key, key, out count);
        }

        public long SumRange(TKey min, TKey max, out int count)
        {
            count = 0;
            if (root < 0) return 0;

            var norm = traits.Normalize(min, 0);
            var leaf = Descend(norm, min, 0);
            var pos = LeafLowerBound(leaf, norm, min, 0);
            var maxNorm = traits.Normalize(max, int.MaxValue);

            long sum = 0;
            while (leaf >= 0)
            {
                var baseSlot = leaf * b;
                var end = lCount[leaf];
                for (; pos < end; pos++)
                {
                    var slot = baseSlot + pos;
                    var n = lNorm[slot];
                    if (n > maxNorm) return sum;
                    if (traits.NeedsFull && n == maxNorm && traits.CompareFull(lFull[slot], max) > 0) return sum;
                    sum += traits.PacksPk ? (long)(uint)n : lPk[slot];
                    count++;
                }

                leaf = lNext[leaf];
                pos = 0;
            }

            return sum;
        }

        // ---- 一括構築 ----

        public bool Bulk(TKey[] keys, int[] pks, int n)
        {
            if (root >= 0) throw new InvalidOperationException("Bulk requires an empty index.");
            if (n == 0) return true;

            var norms = new ulong[n];
            var order = new int[n];
            for (var i = 0; i < n; i++)
            {
                norms[i] = traits.Normalize(keys[i], pks[i]);
                order[i] = i;
            }

            if (traits.PacksPk && !traits.NeedsFull)
            {
                Array.Sort(norms, order);
            }
            else
            {
                Array.Sort(order, new BulkComparer(norms, keys, pks, traits));
                var sorted = new ulong[n];
                for (var i = 0; i < n; i++) sorted[i] = norms[order[i]];
                norms = sorted;
            }

            // 葉を満杯に詰めて左から並べる.
            var leaves = (n + b - 1) / b;
            var level = new int[leaves];
            var prevLeaf = -1;
            for (var l = 0; l < leaves; l++)
            {
                var leaf = AllocLeaf();
                var start = l * b;
                var cnt = Math.Min(b, n - start);
                var baseSlot = leaf * b;
                Array.Copy(norms, start, lNorm, baseSlot, cnt);
                for (var i = 0; i < cnt; i++)
                {
                    var src = order[start + i];
                    if (traits.NeedsFull) lFull[baseSlot + i] = keys[src];
                    if (!traits.PacksPk) lPk[baseSlot + i] = pks[src];
                }

                lCount[leaf] = cnt;
                lPrev[leaf] = prevLeaf;
                if (prevLeaf >= 0) lNext[prevLeaf] = leaf;
                prevLeaf = leaf;
                level[l] = leaf;
            }

            // 内部ノードを下から積む.
            var isLeafLevel = true;
            height = 0;
            while (level.Length > 1)
            {
                var parents = (level.Length + b - 1) / b;
                var upper = new int[parents];
                for (var p = 0; p < parents; p++)
                {
                    var node = AllocInner();
                    var start = p * b;
                    var cnt = Math.Min(b, level.Length - start);
                    for (var i = 0; i < cnt; i++)
                    {
                        var child = level[start + i];
                        var s = node * b + i;
                        if (isLeafLevel)
                        {
                            var cs = child * b;
                            SetInner(s, lNorm[cs], traits.NeedsFull ? lFull[cs] : default, traits.PacksPk ? 0 : lPk[cs], child);
                        }
                        else
                        {
                            var cs = child * b;
                            SetInner(s, iNorm[cs], traits.NeedsFull ? iFull[cs] : default, traits.PacksPk ? 0 : iPk[cs], child);
                        }
                    }

                    iCount[node] = cnt;
                    upper[p] = node;
                }

                level = upper;
                isLeafLevel = false;
                height++;
            }

            root = level[0];
            return true;
        }

        sealed class BulkComparer : System.Collections.Generic.IComparer<int>
        {
            readonly ulong[] norms;
            readonly TKey[] keys;
            readonly int[] pks;
            readonly TTraits traits;

            public BulkComparer(ulong[] norms, TKey[] keys, int[] pks, TTraits traits)
            {
                this.norms = norms;
                this.keys = keys;
                this.pks = pks;
                this.traits = traits;
            }

            public int Compare(int x, int y)
            {
                var a = norms[x];
                var c = norms[y];
                if (a != c) return a < c ? -1 : 1;
                if (traits.NeedsFull)
                {
                    var r = traits.CompareFull(keys[x], keys[y]);
                    if (r != 0) return r;
                }

                return pks[x].CompareTo(pks[y]);
            }
        }

        // ---- ノードの確保 ----

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
                if (leafUsed == leafCap)
                {
                    leafCap *= 2;
                    Array.Resize(ref lNorm, leafCap * b);
                    if (traits.NeedsFull) Array.Resize(ref lFull, leafCap * b);
                    if (!traits.PacksPk) Array.Resize(ref lPk, leafCap * b);
                    Array.Resize(ref lCount, leafCap);
                    Array.Resize(ref lNext, leafCap);
                    Array.Resize(ref lPrev, leafCap);
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
                if (innerUsed == innerCap)
                {
                    innerCap *= 2;
                    Array.Resize(ref iNorm, innerCap * b);
                    if (traits.NeedsFull) Array.Resize(ref iFull, innerCap * b);
                    if (!traits.PacksPk) Array.Resize(ref iPk, innerCap * b);
                    Array.Resize(ref iChild, innerCap * b);
                    Array.Resize(ref iCount, innerCap);
                }

                node = innerUsed++;
            }

            iCount[node] = 0;
            return node;
        }

        void FreeInner(int node)
        {
            iCount[node] = innerFree;
            innerFree = node;
        }
    }
}
