using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    // SoA (Structure of Arrays) 形式の B+Tree によるソート済みインデックス。
    //
    // メモリレイアウト:
    // - 葉ノードと内部ノードはそれぞれ別のフラットな配列群に格納し、ノードは int の番号で参照する。
    // - ノード i のエントリはスロット [i * nodeSize, (i + 1) * nodeSize) を占め、
    //   有効なのは先頭の leafCount[i] / innerCount[i] 件。
    // - 葉: leafNorm / leafFull / leafPk がスロット単位、leafCount / leafNext / leafPrev がノード単位。
    //   葉は leafNext / leafPrev による双方向リンクリストで昇順に連結される (終端は -1)。
    // - 内部: innerNorm / innerFull / innerPk / innerChild がスロット単位、innerCount がノード単位。
    //   スロット j は子 innerChild[j] の部分木の下限をセパレータとして持つ (先頭スロットのキーは探索に影響しない)。
    // - 解放済みノードはフリーリストで再利用する。leafFree は leafNext、innerFree は innerCount を次要素へのリンクとして流用する。
    //
    // キーの表現:
    // - norm はキーを順序を保って ulong に正規化した値。NeedsFullComparison のときだけ *Full に元のキーを持ち、norm が等しい場合に比較する。
    // - packsPk (非ユニークかつ NormalizedBits + pkBits <= 64) のとき norm = (正規化キー << pkBits) | 主キー とし、
    //   主キーは norm の下位 pkBits ビットから復元する。このとき leafPk / innerPk は確保しない。
    // - comparesPk (非ユニークで pack できない) のときは leafPk / innerPk を持ち、キーが等しいエントリを主キーで順序付ける。
    // - ユニークの場合は主キーを比較に使わないため innerPk を持たない。
    public sealed class OrderedIndex<TKey, TKeyTraits, TPrimaryKey, TPrimaryKeyTraits>
        where TKeyTraits : struct, IOrderedKeyTraits<TKey>
        where TPrimaryKey : unmanaged
        where TPrimaryKeyTraits : struct, IPrimaryKeyTraits<TPrimaryKey>
    {
        public const int DefaultNodeSize = 64;

        const int RebuildRatio = 16;

        readonly int nodeSize;
        readonly bool unique;
        readonly bool packsPk;
        readonly bool comparesPk;
        readonly int pkBits;
        readonly ulong pkMask;

        ulong[] leafNorm;
        TKey[] leafFull;
        TPrimaryKey[] leafPk;
        int[] leafCount;
        int[] leafNext;
        int[] leafPrev;
        int leafUsed;
        int leafFree;

        ulong[] innerNorm;
        TKey[] innerFull;
        ulong[] innerPk;
        int[] innerChild;
        int[] innerCount;
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

            this.nodeSize = nodeSize;
            this.unique = unique;
            pkBits = default(TPrimaryKeyTraits).Bits;
            pkMask = pkBits == 64 ? ulong.MaxValue : (1UL << pkBits) - 1;
            packsPk = !unique && default(TKeyTraits).NormalizedBits + pkBits <= 64;
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
        static ulong PkToUInt64(TPrimaryKey primaryKey)
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
                var fullComparison = default(TKeyTraits).CompareFull(full, probe.Key);
                if (fullComparison != 0) return fullComparison;
            }

            if (!comparesPk) return 0;
            return primaryKey == probe.Pk ? 0 : primaryKey < probe.Pk ? -1 : 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int CompareLeaf(int slot, in Probe probe)
        {
            return Compare(leafNorm[slot], NeedsFullComparison ? leafFull[slot] : default, comparesPk ? PkToUInt64(leafPk[slot]) : 0, probe);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int CompareInner(int slot, in Probe probe)
        {
            return Compare(innerNorm[slot], NeedsFullComparison ? innerFull[slot] : default, comparesPk ? innerPk[slot] : 0, probe);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int CompareKey(int slot, ulong keyNorm, TKey key)
        {
            var storedKeyNorm = packsPk ? leafNorm[slot] >> pkBits : leafNorm[slot];
            if (storedKeyNorm != keyNorm) return storedKeyNorm < keyNorm ? -1 : 1;
            return NeedsFullComparison ? default(TKeyTraits).CompareFull(leafFull[slot], key) : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        TPrimaryKey PkAtSlot(int slot)
        {
            return packsPk ? default(TPrimaryKeyTraits).FromUInt64(leafNorm[slot] & pkMask) : leafPk[slot];
        }

        int LeafLowerBound(int leaf, in Probe probe)
        {
            var lo = 0;
            var hi = leafCount[leaf];
            var baseSlot = leaf * nodeSize;
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
            var hi = innerCount[node];
            var baseSlot = node * nodeSize;
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
                node = innerChild[node * nodeSize + pos];
            }

            return node;
        }

        public bool ContainsKey(TKey key)
        {
            return TryGetFirst(key, out _);
        }

        public bool TryGetFirst(TKey key, out TPrimaryKey primaryKey)
        {
            var enumerator = Range(key, key);
            if (enumerator.MoveNext())
            {
                primaryKey = enumerator.Current;
                return true;
            }

            primaryKey = default;
            return false;
        }

        public RangeEnumerator Equal(TKey key)
        {
            return Range(key, key);
        }

        public RangeEnumerator Range(TKey min, TKey max)
        {
            var traits = default(TKeyTraits);
            var minNorm = traits.Normalize(min);
            var maxNorm = traits.Normalize(max);
            if (count == 0 || minNorm > maxNorm || (minNorm == maxNorm && NeedsFullComparison && traits.CompareFull(min, max) > 0))
            {
                return new RangeEnumerator(this, -1, 0, maxNorm, max);
            }

            var probe = MakeProbe(min, 0);
            var leaf = Descend(probe);
            var pos = LeafLowerBound(leaf, probe);
            return new RangeEnumerator(this, leaf, pos, maxNorm, max);
        }

        public struct RangeEnumerator : IPrimaryKeyEnumerator<TPrimaryKey>
        {
            readonly OrderedIndex<TKey, TKeyTraits, TPrimaryKey, TPrimaryKeyTraits> index;
            readonly ulong maxNorm;
            readonly TKey max;
            int leaf;
            int pos;
            TPrimaryKey current;

            internal RangeEnumerator(OrderedIndex<TKey, TKeyTraits, TPrimaryKey, TPrimaryKeyTraits> index, int leaf, int pos, ulong maxNorm, TKey max)
            {
                this.index = index;
                this.leaf = leaf;
                this.pos = pos;
                this.maxNorm = maxNorm;
                this.max = max;
                current = default;
            }

            public TPrimaryKey Current => current;

            public bool MoveNext()
            {
                while (leaf >= 0)
                {
                    if (pos < index.leafCount[leaf])
                    {
                        var slot = leaf * index.nodeSize + pos;
                        if (index.CompareKey(slot, maxNorm, max) > 0)
                        {
                            leaf = -1;
                            return false;
                        }

                        current = index.PkAtSlot(slot);
                        pos++;
                        return true;
                    }

                    leaf = index.leafNext[leaf];
                    pos = 0;
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
            var probe = MakeProbe(key, PkToUInt64(primaryKey));
            var leaf = Descend(probe);
            var pos = LeafLowerBound(leaf, probe);
            if (pos < leafCount[leaf] && CompareLeaf(leaf * nodeSize + pos, probe) == 0) return false;

            count++;
            if (leafCount[leaf] < nodeSize)
            {
                LeafInsertAt(leaf, pos, probe.Norm, key, primaryKey);
                return true;
            }

            var split = pos == nodeSize && leafNext[leaf] < 0 ? nodeSize : nodeSize / 2;
            var right = AllocLeaf();
            MoveLeafEntries(leaf, split, right, nodeSize - split);
            leafCount[leaf] = split;
            leafCount[right] = nodeSize - split;

            var next = leafNext[leaf];
            leafNext[right] = next;
            leafPrev[right] = leaf;
            if (next >= 0) leafPrev[next] = right;
            leafNext[leaf] = right;

            if (pos < split)
            {
                LeafInsertAt(leaf, pos, probe.Norm, key, primaryKey);
            }
            else
            {
                LeafInsertAt(right, pos - split, probe.Norm, key, primaryKey);
            }

            var rightFirstSlot = right * nodeSize;
            PromoteSplit(1, leafNorm[rightFirstSlot], NeedsFullComparison ? leafFull[rightFirstSlot] : default, comparesPk ? PkToUInt64(leafPk[rightFirstSlot]) : 0, right, leaf);
            return true;
        }

        void LeafInsertAt(int leaf, int pos, ulong norm, TKey key, TPrimaryKey primaryKey)
        {
            var baseSlot = leaf * nodeSize;
            var entryCount = leafCount[leaf];
            var move = entryCount - pos;
            if (move > 0)
            {
                Array.Copy(leafNorm, baseSlot + pos, leafNorm, baseSlot + pos + 1, move);
                if (NeedsFullComparison) Array.Copy(leafFull, baseSlot + pos, leafFull, baseSlot + pos + 1, move);
                if (!packsPk) Array.Copy(leafPk, baseSlot + pos, leafPk, baseSlot + pos + 1, move);
            }

            leafNorm[baseSlot + pos] = norm;
            if (NeedsFullComparison) leafFull[baseSlot + pos] = key;
            if (!packsPk) leafPk[baseSlot + pos] = primaryKey;
            leafCount[leaf] = entryCount + 1;
        }

        void MoveLeafEntries(int fromLeaf, int fromPos, int toLeaf, int length)
        {
            if (length <= 0) return;
            var src = fromLeaf * nodeSize + fromPos;
            var dst = toLeaf * nodeSize;
            Array.Copy(leafNorm, src, leafNorm, dst, length);
            if (NeedsFullComparison)
            {
                Array.Copy(leafFull, src, leafFull, dst, length);
                Array.Clear(leafFull, src, length);
            }

            if (!packsPk) Array.Copy(leafPk, src, leafPk, dst, length);
        }

        void PromoteSplit(int level, ulong sepNorm, TKey sepFull, ulong sepPk, int child, int leftChild)
        {
            while (true)
            {
                if (level > height)
                {
                    var newRoot = AllocInner();
                    var rootFirstSlot = newRoot * nodeSize;
                    SetInner(rootFirstSlot, 0, default, 0, leftChild);
                    SetInner(rootFirstSlot + 1, sepNorm, sepFull, sepPk, child);
                    innerCount[newRoot] = 2;
                    root = newRoot;
                    height++;
                    EnsurePathCapacity();
                    return;
                }

                var node = pathNode[level];
                var pos = pathPos[level] + 1;
                if (innerCount[node] < nodeSize)
                {
                    InnerInsertAt(node, pos, sepNorm, sepFull, sepPk, child);
                    return;
                }

                var split = pos == nodeSize ? nodeSize : nodeSize / 2;
                var right = AllocInner();
                var src = node * nodeSize + split;
                var dst = right * nodeSize;
                var movedCount = nodeSize - split;
                if (movedCount > 0)
                {
                    Array.Copy(innerNorm, src, innerNorm, dst, movedCount);
                    if (NeedsFullComparison)
                    {
                        Array.Copy(innerFull, src, innerFull, dst, movedCount);
                        Array.Clear(innerFull, src, movedCount);
                    }

                    if (comparesPk) Array.Copy(innerPk, src, innerPk, dst, movedCount);
                    Array.Copy(innerChild, src, innerChild, dst, movedCount);
                }

                innerCount[node] = split;
                innerCount[right] = movedCount;
                if (pos <= split && split != nodeSize)
                {
                    InnerInsertAt(node, pos, sepNorm, sepFull, sepPk, child);
                }
                else
                {
                    InnerInsertAt(right, pos - split, sepNorm, sepFull, sepPk, child);
                }

                var rightFirstSlot = right * nodeSize;
                sepNorm = innerNorm[rightFirstSlot];
                sepFull = NeedsFullComparison ? innerFull[rightFirstSlot] : default;
                sepPk = comparesPk ? innerPk[rightFirstSlot] : 0;
                child = right;
                leftChild = node;
                level++;
            }
        }

        void InnerInsertAt(int node, int pos, ulong norm, TKey full, ulong primaryKey, int child)
        {
            var baseSlot = node * nodeSize;
            var move = innerCount[node] - pos;
            if (move > 0)
            {
                Array.Copy(innerNorm, baseSlot + pos, innerNorm, baseSlot + pos + 1, move);
                if (NeedsFullComparison) Array.Copy(innerFull, baseSlot + pos, innerFull, baseSlot + pos + 1, move);
                if (comparesPk) Array.Copy(innerPk, baseSlot + pos, innerPk, baseSlot + pos + 1, move);
                Array.Copy(innerChild, baseSlot + pos, innerChild, baseSlot + pos + 1, move);
            }

            SetInner(baseSlot + pos, norm, full, primaryKey, child);
            innerCount[node]++;
        }

        void SetInner(int slot, ulong norm, TKey full, ulong primaryKey, int child)
        {
            innerNorm[slot] = norm;
            if (NeedsFullComparison) innerFull[slot] = full;
            if (comparesPk) innerPk[slot] = primaryKey;
            innerChild[slot] = child;
        }

        public bool Remove(TKey key, TPrimaryKey primaryKey)
        {
            if (count == 0) return false;

            var pkValue = PkToUInt64(primaryKey);
            var probe = MakeProbe(key, pkValue);
            var leaf = Descend(probe);
            var pos = LeafLowerBound(leaf, probe);
            var baseSlot = leaf * nodeSize;
            if (pos >= leafCount[leaf] || CompareLeaf(baseSlot + pos, probe) != 0) return false;
            if (unique && PkToUInt64(leafPk[baseSlot + pos]) != pkValue) return false;

            var newCount = leafCount[leaf] - 1;
            var move = newCount - pos;
            if (move > 0)
            {
                Array.Copy(leafNorm, baseSlot + pos + 1, leafNorm, baseSlot + pos, move);
                if (NeedsFullComparison) Array.Copy(leafFull, baseSlot + pos + 1, leafFull, baseSlot + pos, move);
                if (!packsPk) Array.Copy(leafPk, baseSlot + pos + 1, leafPk, baseSlot + pos, move);
            }

            if (NeedsFullComparison) leafFull[baseSlot + newCount] = default;
            leafCount[leaf] = newCount;
            count--;

            if (newCount == 0 && height > 0)
            {
                var prev = leafPrev[leaf];
                var next = leafNext[leaf];
                if (prev >= 0) leafNext[prev] = next;
                if (next >= 0) leafPrev[next] = prev;
                FreeLeaf(leaf);
                RemoveFromParent(1);
            }

            return true;
        }

        void RemoveFromParent(int level)
        {
            while (true)
            {
                var node = pathNode[level];
                var pos = pathPos[level];
                var baseSlot = node * nodeSize;
                var newCount = innerCount[node] - 1;
                var move = newCount - pos;
                if (move > 0)
                {
                    Array.Copy(innerNorm, baseSlot + pos + 1, innerNorm, baseSlot + pos, move);
                    if (NeedsFullComparison) Array.Copy(innerFull, baseSlot + pos + 1, innerFull, baseSlot + pos, move);
                    if (comparesPk) Array.Copy(innerPk, baseSlot + pos + 1, innerPk, baseSlot + pos, move);
                    Array.Copy(innerChild, baseSlot + pos + 1, innerChild, baseSlot + pos, move);
                }

                if (NeedsFullComparison) innerFull[baseSlot + newCount] = default;
                innerCount[node] = newCount;

                if (level == height)
                {
                    while (height > 0 && innerCount[root] == 1)
                    {
                        var oldRoot = root;
                        root = innerChild[oldRoot * nodeSize];
                        FreeInner(oldRoot);
                        height--;
                    }

                    return;
                }

                if (newCount > 0) return;
                FreeInner(node);
                level++;
            }
        }

        public bool AddRange(ReadOnlySpan<TKey> keys, ReadOnlySpan<TPrimaryKey> primaryKeys)
        {
            if (primaryKeys.Length < keys.Length) throw new ArgumentException("primaryKeys is shorter than keys.", nameof(primaryKeys));

            var incomingCount = keys.Length;
            if (incomingCount == 0) return true;

            if (incomingCount < count / RebuildRatio)
            {
                for (var i = 0; i < incomingCount; i++)
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
                var written = 0;
                for (var leaf = LeftmostLeaf(); leaf >= 0; leaf = leafNext[leaf])
                {
                    var baseSlot = leaf * nodeSize;
                    for (var i = 0; i < leafCount[leaf]; i++)
                    {
                        buf.Set(written++, leafNorm[baseSlot + i], NeedsFullComparison ? leafFull[baseSlot + i] : default, PkAtSlot(baseSlot + i));
                    }
                }

                Build(buf, written);
            }
            finally
            {
                buf.Return();
            }
        }

        bool MergeRebuild(ReadOnlySpan<TKey> keys, ReadOnlySpan<TPrimaryKey> primaryKeys)
        {
            var incomingCount = keys.Length;
            var incoming = new EntryBuffer(incomingCount);
            var order = ArrayPool<int>.Shared.Rent(incomingCount);
            var merged = new EntryBuffer(count + incomingCount);
            try
            {
                for (var i = 0; i < incomingCount; i++)
                {
                    var probe = MakeProbe(keys[i], PkToUInt64(primaryKeys[i]));
                    incoming.Set(i, probe.Norm, keys[i], primaryKeys[i]);
                    order[i] = i;
                }

                sortComparer ??= new EntryComparer(this);
                sortComparer.target = incoming;
                Array.Sort(order, 0, incomingCount, sortComparer);
                sortComparer.target = default;

                var written = 0;
                var leaf = LeftmostLeaf();
                var pos = 0;
                var incomingPos = 0;
                while (incomingPos < incomingCount)
                {
                    while (leaf >= 0 && pos >= leafCount[leaf])
                    {
                        leaf = leafNext[leaf];
                        pos = 0;
                    }

                    var src = order[incomingPos];
                    var probe = new Probe(incoming.Norm[src], NeedsFullComparison ? incoming.Full[src] : default, PkToUInt64(incoming.Pk[src]));
                    if (incomingPos > 0)
                    {
                        var prev = order[incomingPos - 1];
                        if (Compare(incoming.Norm[prev], NeedsFullComparison ? incoming.Full[prev] : default, PkToUInt64(incoming.Pk[prev]), probe) == 0) return false;
                    }

                    var comparison = leaf >= 0 ? CompareLeaf(leaf * nodeSize + pos, probe) : 1;
                    if (comparison == 0) return false;
                    if (comparison < 0)
                    {
                        var slot = leaf * nodeSize + pos;
                        merged.Set(written++, leafNorm[slot], NeedsFullComparison ? leafFull[slot] : default, PkAtSlot(slot));
                        pos++;
                    }
                    else
                    {
                        merged.Set(written++, incoming.Norm[src], NeedsFullComparison ? incoming.Full[src] : default, incoming.Pk[src]);
                        incomingPos++;
                    }
                }

                for (; leaf >= 0; leaf = leafNext[leaf], pos = 0)
                {
                    for (; pos < leafCount[leaf]; pos++)
                    {
                        var slot = leaf * nodeSize + pos;
                        merged.Set(written++, leafNorm[slot], NeedsFullComparison ? leafFull[slot] : default, PkAtSlot(slot));
                    }
                }

                Build(merged, written);
                return true;
            }
            finally
            {
                incoming.Return();
                merged.Return();
                ArrayPool<int>.Shared.Return(order);
            }
        }

        void Build(in EntryBuffer entries, int entryCount)
        {
            var leaves = Math.Max(1, (entryCount + nodeSize - 1) / nodeSize);
            var inners = 0;
            for (var width = leaves; width > 1; width = (width + nodeSize - 1) / nodeSize)
            {
                inners += (width + nodeSize - 1) / nodeSize;
            }

            Reset(leaves, Math.Max(1, inners));
            count = entryCount;
            if (entryCount == 0) return;

            var currentLevel = ArrayPool<int>.Shared.Rent(leaves);
            var nextLevel = ArrayPool<int>.Shared.Rent((leaves + nodeSize - 1) / nodeSize);
            try
            {
                leafUsed = 0;
                var prevLeaf = -1;
                for (var leafIndex = 0; leafIndex < leaves; leafIndex++)
                {
                    var leaf = AllocLeaf();
                    var start = leafIndex * nodeSize;
                    var leafEntryCount = Math.Min(nodeSize, entryCount - start);
                    var baseSlot = leaf * nodeSize;
                    Array.Copy(entries.Norm, start, leafNorm, baseSlot, leafEntryCount);
                    if (NeedsFullComparison) Array.Copy(entries.Full, start, leafFull, baseSlot, leafEntryCount);
                    if (!packsPk) Array.Copy(entries.Pk, start, leafPk, baseSlot, leafEntryCount);

                    leafCount[leaf] = leafEntryCount;
                    leafPrev[leaf] = prevLeaf;
                    if (prevLeaf >= 0) leafNext[prevLeaf] = leaf;
                    prevLeaf = leaf;
                    currentLevel[leafIndex] = leaf;
                }

                var width = leaves;
                var isLeafLevel = true;
                while (width > 1)
                {
                    var parents = (width + nodeSize - 1) / nodeSize;
                    for (var parentIndex = 0; parentIndex < parents; parentIndex++)
                    {
                        var node = AllocInner();
                        var start = parentIndex * nodeSize;
                        var childCount = Math.Min(nodeSize, width - start);
                        for (var i = 0; i < childCount; i++)
                        {
                            var child = currentLevel[start + i];
                            var childFirstSlot = child * nodeSize;
                            if (isLeafLevel)
                            {
                                SetInner(node * nodeSize + i, leafNorm[childFirstSlot], NeedsFullComparison ? leafFull[childFirstSlot] : default, comparesPk ? PkToUInt64(leafPk[childFirstSlot]) : 0, child);
                            }
                            else
                            {
                                SetInner(node * nodeSize + i, innerNorm[childFirstSlot], NeedsFullComparison ? innerFull[childFirstSlot] : default, comparesPk ? innerPk[childFirstSlot] : 0, child);
                            }
                        }

                        innerCount[node] = childCount;
                        nextLevel[parentIndex] = node;
                    }

                    var swap = currentLevel;
                    currentLevel = nextLevel;
                    nextLevel = swap;
                    width = parents;
                    isLeafLevel = false;
                    height++;
                }

                root = currentLevel[0];
                EnsurePathCapacity();
            }
            finally
            {
                ArrayPool<int>.Shared.Return(currentLevel);
                ArrayPool<int>.Shared.Return(nextLevel);
            }
        }

        int LeftmostLeaf()
        {
            var node = root;
            for (var level = height; level > 0; level--)
            {
                node = innerChild[node * nodeSize];
            }

            return node;
        }

        readonly struct EntryBuffer
        {
            public readonly ulong[] Norm;
            public readonly TKey[] Full;
            public readonly TPrimaryKey[] Pk;

            public EntryBuffer(int capacity)
            {
                Norm = ArrayPool<ulong>.Shared.Rent(Math.Max(1, capacity));
                Full = NeedsFullComparison ? ArrayPool<TKey>.Shared.Rent(Math.Max(1, capacity)) : null;
                Pk = ArrayPool<TPrimaryKey>.Shared.Rent(Math.Max(1, capacity));
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
                var buffer = target;
                var probe = new Probe(buffer.Norm[y], NeedsFullComparison ? buffer.Full[y] : default, PkToUInt64(buffer.Pk[y]));
                return index.Compare(buffer.Norm[x], NeedsFullComparison ? buffer.Full[x] : default, PkToUInt64(buffer.Pk[x]), probe);
            }
        }

        void Reset(int leafCapacity, int innerCapacity)
        {
            leafNorm = new ulong[leafCapacity * nodeSize];
            leafFull = NeedsFullComparison ? new TKey[leafCapacity * nodeSize] : null;
            leafPk = packsPk ? null : new TPrimaryKey[leafCapacity * nodeSize];
            leafCount = new int[leafCapacity];
            leafNext = new int[leafCapacity];
            leafPrev = new int[leafCapacity];
            leafUsed = 0;
            leafFree = -1;

            innerNorm = new ulong[innerCapacity * nodeSize];
            innerFull = NeedsFullComparison ? new TKey[innerCapacity * nodeSize] : null;
            innerPk = comparesPk ? new ulong[innerCapacity * nodeSize] : null;
            innerChild = new int[innerCapacity * nodeSize];
            innerCount = new int[innerCapacity];
            innerUsed = 0;
            innerFree = -1;

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
                leafFree = leafNext[leaf];
            }
            else
            {
                if (leafUsed == leafCount.Length)
                {
                    var newCapacity = leafCount.Length * 2;
                    Array.Resize(ref leafNorm, newCapacity * nodeSize);
                    if (NeedsFullComparison) Array.Resize(ref leafFull, newCapacity * nodeSize);
                    if (!packsPk) Array.Resize(ref leafPk, newCapacity * nodeSize);
                    Array.Resize(ref leafCount, newCapacity);
                    Array.Resize(ref leafNext, newCapacity);
                    Array.Resize(ref leafPrev, newCapacity);
                }

                leaf = leafUsed++;
            }

            leafCount[leaf] = 0;
            leafNext[leaf] = -1;
            leafPrev[leaf] = -1;
            return leaf;
        }

        void FreeLeaf(int leaf)
        {
            leafNext[leaf] = leafFree;
            leafFree = leaf;
        }

        int AllocInner()
        {
            int node;
            if (innerFree >= 0)
            {
                node = innerFree;
                innerFree = innerCount[node];
            }
            else
            {
                if (innerUsed == innerCount.Length)
                {
                    var newCapacity = innerCount.Length * 2;
                    Array.Resize(ref innerNorm, newCapacity * nodeSize);
                    if (NeedsFullComparison) Array.Resize(ref innerFull, newCapacity * nodeSize);
                    if (comparesPk) Array.Resize(ref innerPk, newCapacity * nodeSize);
                    Array.Resize(ref innerChild, newCapacity * nodeSize);
                    Array.Resize(ref innerCount, newCapacity);
                }

                node = innerUsed++;
            }

            innerCount[node] = 0;
            return node;
        }

        void FreeInner(int node)
        {
            if (NeedsFullComparison) Array.Clear(innerFull, node * nodeSize, nodeSize);
            innerCount[node] = innerFree;
            innerFree = node;
        }
    }
}
