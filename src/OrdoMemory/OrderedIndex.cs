using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    /// <summary>
    /// 配列アリーナ上の B+Tree.
    /// ノードはオブジェクトにせず、葉と内部ノードをそれぞれ数本の配列に置いて int の番号で指す.
    /// 非 Unique の索引のキーは (SecondaryKey, PK) の組. 正規化キーと PK が 64bit に収まる場合は 1 つの ulong に詰める.
    /// 内部ノードの各エントリは (子の最小キー, 子). 削除は空になったノードだけを解放する (free-at-empty).
    /// </summary>
    public sealed class OrderedIndex<TKey, TKeyTraits, TPk, TPkTraits>
        where TKeyTraits : struct, IOrderedKeyTraits<TKey>
        where TPk : unmanaged
        where TPkTraits : struct, IPkTraits<TPk>
    {
        public const int DefaultNodeSize = 64;

        // 一括挿入で、件数が既存のこの割合未満なら 1 件ずつ挿入する.
        const int RebuildRatio = 16;

        readonly int b;
        readonly bool unique;
        readonly bool packsPk;
        readonly bool comparesPk;
        readonly int pkBits;
        readonly ulong pkMask;

        // 葉: スロット [leaf * b, leaf * b + b)
        ulong[] lNorm;
        TKey[] lFull;   // NeedsFull のときのみ
        TPk[] lPk;      // !packsPk のときのみ
        int[] lCount;
        int[] lNext;
        int[] lPrev;
        int leafUsed;
        int leafFree;

        // 内部ノード
        ulong[] iNorm;
        TKey[] iFull;   // NeedsFull のときのみ
        ulong[] iPk;    // comparesPk のときのみ
        int[] iChild;
        int[] iCount;
        int innerUsed;
        int innerFree;

        int root;
        int height;     // 0: 根が葉
        int count;

        int[] pathNode = new int[16];
        int[] pathPos = new int[16];

        EntryComparer sortComparer;

        public OrderedIndex(bool unique, int nodeSize = DefaultNodeSize)
        {
            if (nodeSize < 4) throw new ArgumentOutOfRangeException(nameof(nodeSize));

            b = nodeSize;
            this.unique = unique;
            pkBits = default(TPkTraits).Bits;
            pkMask = pkBits == 64 ? ulong.MaxValue : (1UL << pkBits) - 1;
            packsPk = !unique && default(TKeyTraits).NormBits + pkBits <= 64;
            comparesPk = !unique && !packsPk;
            Reset(4, 4);
        }

        public int Count => count;

        public bool IsUnique => unique;

        static bool NeedsFull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default(TKeyTraits).NeedsFull;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong ToU(TPk pk) => default(TPkTraits).ToUInt64(pk);

        // ---- 比較 ----

        readonly struct Probe
        {
            public readonly ulong Norm;
            public readonly TKey Key;
            public readonly ulong Pk;

            public Probe(ulong norm, TKey key, ulong pk)
            {
                Norm = norm;
                Key = key;
                Pk = pk;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Probe MakeProbe(TKey key, ulong pk)
        {
            var norm = default(TKeyTraits).Normalize(key);
            if (packsPk) norm = (norm << pkBits) | pk;
            return new Probe(norm, key, pk);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int Compare(ulong norm, TKey full, ulong pk, in Probe probe)
        {
            if (norm != probe.Norm) return norm < probe.Norm ? -1 : 1;
            if (NeedsFull)
            {
                var c = default(TKeyTraits).CompareFull(full, probe.Key);
                if (c != 0) return c;
            }

            if (!comparesPk) return 0;
            return pk == probe.Pk ? 0 : (pk < probe.Pk ? -1 : 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int CompareLeaf(int slot, in Probe probe)
        {
            return Compare(lNorm[slot], NeedsFull ? lFull[slot] : default, comparesPk ? ToU(lPk[slot]) : 0, probe);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int CompareInner(int slot, in Probe probe)
        {
            return Compare(iNorm[slot], NeedsFull ? iFull[slot] : default, comparesPk ? iPk[slot] : 0, probe);
        }

        // PK を除いた SecondaryKey だけの比較.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int CompareKey(int slot, ulong keyNorm, TKey key)
        {
            var n = packsPk ? lNorm[slot] >> pkBits : lNorm[slot];
            if (n != keyNorm) return n < keyNorm ? -1 : 1;
            return NeedsFull ? default(TKeyTraits).CompareFull(lFull[slot], key) : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        TPk PkAtSlot(int slot)
        {
            return packsPk ? default(TPkTraits).FromUInt64(lNorm[slot] & pkMask) : lPk[slot];
        }

        int LeafLowerBound(int leaf, in Probe probe)
        {
            var lo = 0;
            var hi = lCount[leaf];
            var baseSlot = leaf * b;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (CompareLeaf(baseSlot + mid, probe) < 0) lo = mid + 1;
                else hi = mid;
            }

            return lo;
        }

        // 最小キー <= probe となる最後の子の位置. 先頭の子は下限なしとして扱う.
        int ChildIndex(int node, in Probe probe)
        {
            var lo = 0;
            var hi = iCount[node];
            var baseSlot = node * b;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (CompareInner(baseSlot + mid, probe) <= 0) lo = mid + 1;
                else hi = mid;
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

        // ---- 検索 ----

        public bool ContainsKey(TKey key)
        {
            return TryGetFirst(key, out _);
        }

        // SecondaryKey が等しいものの中では PK の昇順で先頭.
        public bool TryGetFirst(TKey key, out TPk pk)
        {
            var e = Range(key, key);
            if (e.MoveNext())
            {
                pk = e.Current;
                return true;
            }

            pk = default;
            return false;
        }

        public RangeEnumerator Equal(TKey key)
        {
            return Range(key, key);
        }

        // min 以上 max 以下を (SecondaryKey, PK) の昇順で返す.
        public RangeEnumerator Range(TKey min, TKey max)
        {
            var traits = default(TKeyTraits);
            var minNorm = traits.Normalize(min);
            var maxNorm = traits.Normalize(max);
            if (count == 0 || minNorm > maxNorm || (minNorm == maxNorm && NeedsFull && traits.CompareFull(min, max) > 0))
            {
                return new RangeEnumerator(this, -1, 0, maxNorm, max);
            }

            var probe = MakeProbe(min, 0);
            var leaf = Descend(probe);
            var pos = LeafLowerBound(leaf, probe);
            return new RangeEnumerator(this, leaf, pos, maxNorm, max);
        }

        public struct RangeEnumerator : IPkEnumerator<TPk>
        {
            readonly OrderedIndex<TKey, TKeyTraits, TPk, TPkTraits> index;
            readonly ulong maxNorm;
            readonly TKey max;
            int leaf;
            int pos;
            TPk current;

            internal RangeEnumerator(OrderedIndex<TKey, TKeyTraits, TPk, TPkTraits> index, int leaf, int pos, ulong maxNorm, TKey max)
            {
                this.index = index;
                this.leaf = leaf;
                this.pos = pos;
                this.maxNorm = maxNorm;
                this.max = max;
                current = default;
            }

            public TPk Current => current;

            public bool MoveNext()
            {
                while (leaf >= 0)
                {
                    if (pos < index.lCount[leaf])
                    {
                        var slot = leaf * index.b + pos;
                        if (index.CompareKey(slot, maxNorm, max) > 0)
                        {
                            leaf = -1;
                            return false;
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

            public RangeEnumerator GetEnumerator()
            {
                return this;
            }
        }

        // ---- 挿入 ----

        // 既に同じエントリ (Unique では同じ SecondaryKey) がある場合は何もせず false を返す.
        public bool Insert(TKey key, TPk pk)
        {
            var probe = MakeProbe(key, ToU(pk));
            var leaf = Descend(probe);
            var pos = LeafLowerBound(leaf, probe);
            if (pos < lCount[leaf] && CompareLeaf(leaf * b + pos, probe) == 0) return false;

            count++;
            if (lCount[leaf] < b)
            {
                LeafInsertAt(leaf, pos, probe.Norm, key, pk);
                return true;
            }

            // 右端の葉の末尾への追記は、新しい葉に新エントリだけを置く (単調増加の挿入で葉を満杯に保つ).
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

            if (pos < split) LeafInsertAt(leaf, pos, probe.Norm, key, pk);
            else LeafInsertAt(right, pos - split, probe.Norm, key, pk);

            var rs = right * b;
            PromoteSplit(1, lNorm[rs], NeedsFull ? lFull[rs] : default, comparesPk ? ToU(lPk[rs]) : 0, right, leaf);
            return true;
        }

        void LeafInsertAt(int leaf, int pos, ulong norm, TKey key, TPk pk)
        {
            var baseSlot = leaf * b;
            var n = lCount[leaf];
            var move = n - pos;
            if (move > 0)
            {
                Array.Copy(lNorm, baseSlot + pos, lNorm, baseSlot + pos + 1, move);
                if (NeedsFull) Array.Copy(lFull, baseSlot + pos, lFull, baseSlot + pos + 1, move);
                if (!packsPk) Array.Copy(lPk, baseSlot + pos, lPk, baseSlot + pos + 1, move);
            }

            lNorm[baseSlot + pos] = norm;
            if (NeedsFull) lFull[baseSlot + pos] = key;
            if (!packsPk) lPk[baseSlot + pos] = pk;
            lCount[leaf] = n + 1;
        }

        void MoveLeafEntries(int from, int fromPos, int to, int n)
        {
            if (n <= 0) return;
            var src = from * b + fromPos;
            var dst = to * b;
            Array.Copy(lNorm, src, lNorm, dst, n);
            if (NeedsFull)
            {
                Array.Copy(lFull, src, lFull, dst, n);
                Array.Clear(lFull, src, n);
            }

            if (!packsPk) Array.Copy(lPk, src, lPk, dst, n);
        }

        // level の親に (sep, child) を追加する. leftChild は分割前のノード (根の分割で使う).
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
                    if (NeedsFull)
                    {
                        Array.Copy(iFull, src, iFull, dst, n);
                        Array.Clear(iFull, src, n);
                    }

                    if (comparesPk) Array.Copy(iPk, src, iPk, dst, n);
                    Array.Copy(iChild, src, iChild, dst, n);
                }

                iCount[node] = split;
                iCount[right] = n;
                if (pos <= split && split != b) InnerInsertAt(node, pos, sepNorm, sepFull, sepPk, child);
                else InnerInsertAt(right, pos - split, sepNorm, sepFull, sepPk, child);

                var rs = right * b;
                sepNorm = iNorm[rs];
                sepFull = NeedsFull ? iFull[rs] : default;
                sepPk = comparesPk ? iPk[rs] : 0;
                child = right;
                leftChild = node;
                level++;
            }
        }

        void InnerInsertAt(int node, int pos, ulong norm, TKey full, ulong pk, int child)
        {
            var baseSlot = node * b;
            var move = iCount[node] - pos;
            if (move > 0)
            {
                Array.Copy(iNorm, baseSlot + pos, iNorm, baseSlot + pos + 1, move);
                if (NeedsFull) Array.Copy(iFull, baseSlot + pos, iFull, baseSlot + pos + 1, move);
                if (comparesPk) Array.Copy(iPk, baseSlot + pos, iPk, baseSlot + pos + 1, move);
                Array.Copy(iChild, baseSlot + pos, iChild, baseSlot + pos + 1, move);
            }

            SetInner(baseSlot + pos, norm, full, pk, child);
            iCount[node]++;
        }

        void SetInner(int slot, ulong norm, TKey full, ulong pk, int child)
        {
            iNorm[slot] = norm;
            if (NeedsFull) iFull[slot] = full;
            if (comparesPk) iPk[slot] = pk;
            iChild[slot] = child;
        }

        // ---- 削除 ----

        public bool Remove(TKey key, TPk pk)
        {
            if (count == 0) return false;

            var pkValue = ToU(pk);
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
                if (NeedsFull) Array.Copy(lFull, baseSlot + pos + 1, lFull, baseSlot + pos, move);
                if (!packsPk) Array.Copy(lPk, baseSlot + pos + 1, lPk, baseSlot + pos, move);
            }

            if (NeedsFull) lFull[baseSlot + n] = default;
            lCount[leaf] = n;
            count--;

            if (n == 0 && height > 0)
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
                var n = iCount[node] - 1;
                var move = n - pos;
                if (move > 0)
                {
                    Array.Copy(iNorm, baseSlot + pos + 1, iNorm, baseSlot + pos, move);
                    if (NeedsFull) Array.Copy(iFull, baseSlot + pos + 1, iFull, baseSlot + pos, move);
                    if (comparesPk) Array.Copy(iPk, baseSlot + pos + 1, iPk, baseSlot + pos, move);
                    Array.Copy(iChild, baseSlot + pos + 1, iChild, baseSlot + pos, move);
                }

                if (NeedsFull) iFull[baseSlot + n] = default;
                iCount[node] = n;

                if (level == height)
                {
                    // 子が 1 つの根は取り除く.
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

        // ---- 一括挿入 / 作り直し ----

        /// <summary>
        /// 件数が既存に比べて少なければ 1 件ずつ挿入し、多ければ整列してマージし、下から作り直す.
        /// 重複 (Unique では SecondaryKey の重複) があれば何も変更せず false を返す.
        /// </summary>
        public bool AddRange(ReadOnlySpan<TKey> keys, ReadOnlySpan<TPk> pks)
        {
            if (pks.Length < keys.Length) throw new ArgumentException("pks is shorter than keys.", nameof(pks));

            var k = keys.Length;
            if (k == 0) return true;

            if (k < count / RebuildRatio)
            {
                for (var i = 0; i < k; i++)
                {
                    if (Insert(keys[i], pks[i])) continue;
                    for (var j = 0; j < i; j++) Remove(keys[j], pks[j]);
                    return false;
                }

                return true;
            }

            return MergeRebuild(keys, pks);
        }

        // 余った容量を返す. ノードを満杯に詰めて作り直す.
        public void TrimExcess()
        {
            var buf = new EntryBuffer(count);
            try
            {
                var w = 0;
                for (var leaf = LeftmostLeaf(); leaf >= 0; leaf = lNext[leaf])
                {
                    var baseSlot = leaf * b;
                    for (var i = 0; i < lCount[leaf]; i++) buf.Set(w++, lNorm[baseSlot + i], NeedsFull ? lFull[baseSlot + i] : default, PkAtSlot(baseSlot + i));
                }

                Build(buf, w);
            }
            finally
            {
                buf.Return();
            }
        }

        bool MergeRebuild(ReadOnlySpan<TKey> keys, ReadOnlySpan<TPk> pks)
        {
            var k = keys.Length;
            var incoming = new EntryBuffer(k);
            var order = ArrayPool<int>.Shared.Rent(k);
            var merged = new EntryBuffer(count + k);
            try
            {
                for (var i = 0; i < k; i++)
                {
                    var probe = MakeProbe(keys[i], ToU(pks[i]));
                    incoming.Set(i, probe.Norm, keys[i], pks[i]);
                    order[i] = i;
                }

                sortComparer ??= new EntryComparer(this);
                sortComparer.Target = incoming;
                Array.Sort(order, 0, k, sortComparer);
                sortComparer.Target = default;

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
                    var probe = new Probe(incoming.Norm[src], NeedsFull ? incoming.Full[src] : default, ToU(incoming.Pk[src]));
                    if (j > 0)
                    {
                        var prev = order[j - 1];
                        if (Compare(incoming.Norm[prev], NeedsFull ? incoming.Full[prev] : default, ToU(incoming.Pk[prev]), probe) == 0) return false;
                    }

                    var c = leaf >= 0 ? CompareLeaf(leaf * b + pos, probe) : 1;
                    if (c == 0) return false;
                    if (c < 0)
                    {
                        var slot = leaf * b + pos;
                        merged.Set(w++, lNorm[slot], NeedsFull ? lFull[slot] : default, PkAtSlot(slot));
                        pos++;
                    }
                    else
                    {
                        merged.Set(w++, incoming.Norm[src], NeedsFull ? incoming.Full[src] : default, incoming.Pk[src]);
                        j++;
                    }
                }

                for (; leaf >= 0; leaf = lNext[leaf], pos = 0)
                {
                    for (; pos < lCount[leaf]; pos++)
                    {
                        var slot = leaf * b + pos;
                        merged.Set(w++, lNorm[slot], NeedsFull ? lFull[slot] : default, PkAtSlot(slot));
                    }
                }

                Build(merged, w);
                return true;
            }
            finally
            {
                incoming.Return();
                merged.Return();
                ArrayPool<int>.Shared.Return(order);
            }
        }

        // 整列済みのエントリから、葉を満杯に詰めて下から積む.
        void Build(in EntryBuffer entries, int n)
        {
            var leaves = Math.Max(1, (n + b - 1) / b);
            var inners = 0;
            for (var width = leaves; width > 1; width = (width + b - 1) / b) inners += (width + b - 1) / b;
            Reset(leaves, Math.Max(1, inners));
            count = n;
            if (n == 0) return;

            var level = ArrayPool<int>.Shared.Rent(leaves);
            var upper = ArrayPool<int>.Shared.Rent((leaves + b - 1) / b);
            try
            {
                // Reset で作った根 (葉 0) から順に使う.
                leafUsed = 0;
                var prevLeaf = -1;
                for (var l = 0; l < leaves; l++)
                {
                    var leaf = AllocLeaf();
                    var start = l * b;
                    var cnt = Math.Min(b, n - start);
                    var baseSlot = leaf * b;
                    Array.Copy(entries.Norm, start, lNorm, baseSlot, cnt);
                    if (NeedsFull) Array.Copy(entries.Full, start, lFull, baseSlot, cnt);
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
                            if (isLeafLevel) SetInner(node * b + i, lNorm[cs], NeedsFull ? lFull[cs] : default, comparesPk ? ToU(lPk[cs]) : 0, child);
                            else SetInner(node * b + i, iNorm[cs], NeedsFull ? iFull[cs] : default, comparesPk ? iPk[cs] : 0, child);
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
            for (var level = height; level > 0; level--) node = iChild[node * b];
            return node;
        }

        readonly struct EntryBuffer
        {
            public readonly ulong[] Norm;
            public readonly TKey[] Full;
            public readonly TPk[] Pk;

            public EntryBuffer(int n)
            {
                Norm = ArrayPool<ulong>.Shared.Rent(Math.Max(1, n));
                Full = NeedsFull ? ArrayPool<TKey>.Shared.Rent(Math.Max(1, n)) : null;
                Pk = ArrayPool<TPk>.Shared.Rent(Math.Max(1, n));
            }

            public void Set(int i, ulong norm, TKey full, TPk pk)
            {
                Norm[i] = norm;
                if (Full != null) Full[i] = full;
                Pk[i] = pk;
            }

            public void Return()
            {
                if (Norm == null) return;
                ArrayPool<ulong>.Shared.Return(Norm);
                if (Full != null) ArrayPool<TKey>.Shared.Return(Full, true);
                ArrayPool<TPk>.Shared.Return(Pk);
            }
        }

        sealed class EntryComparer : IComparer<int>
        {
            readonly OrderedIndex<TKey, TKeyTraits, TPk, TPkTraits> index;
            public EntryBuffer Target;

            public EntryComparer(OrderedIndex<TKey, TKeyTraits, TPk, TPkTraits> index)
            {
                this.index = index;
            }

            public int Compare(int x, int y)
            {
                var t = Target;
                var probe = new Probe(t.Norm[y], NeedsFull ? t.Full[y] : default, ToU(t.Pk[y]));
                return index.Compare(t.Norm[x], NeedsFull ? t.Full[x] : default, ToU(t.Pk[x]), probe);
            }
        }

        // ---- ノードの確保 ----

        void Reset(int leafCapacity, int innerCapacity)
        {
            lNorm = new ulong[leafCapacity * b];
            lFull = NeedsFull ? new TKey[leafCapacity * b] : null;
            lPk = packsPk ? null : new TPk[leafCapacity * b];
            lCount = new int[leafCapacity];
            lNext = new int[leafCapacity];
            lPrev = new int[leafCapacity];
            leafUsed = 0;
            leafFree = -1;

            iNorm = new ulong[innerCapacity * b];
            iFull = NeedsFull ? new TKey[innerCapacity * b] : null;
            iPk = comparesPk ? new ulong[innerCapacity * b] : null;
            iChild = new int[innerCapacity * b];
            iCount = new int[innerCapacity];
            innerUsed = 0;
            innerFree = -1;

            // 根は常に存在させ、挿入と探索で空の木を特別扱いしない.
            root = AllocLeaf();
            height = 0;
            count = 0;
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
                    if (NeedsFull) Array.Resize(ref lFull, cap * b);
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
                    if (NeedsFull) Array.Resize(ref iFull, cap * b);
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
            if (NeedsFull) Array.Clear(iFull, node * b, b);
            iCount[node] = innerFree;
            innerFree = node;
        }
    }
}
