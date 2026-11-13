using System;
using System.Collections.Generic;

namespace OrdoMemory
{
    /// <summary>完全一致でしか引かない Unique な SecondaryKey の索引. null のキーも 1 件だけ持てる.</summary>
    public sealed class HashUniqueIndex<TKey, TPk> where TPk : unmanaged
    {
        readonly Dictionary<TKey, TPk> map = new Dictionary<TKey, TPk>(KeyComparer<TKey>.Default);
        bool hasNull;
        TPk nullPk;

        public int Count => map.Count + (hasNull ? 1 : 0);

        public bool ContainsKey(TKey key)
        {
            return KeyComparer<TKey>.IsNull(key) ? hasNull : map.ContainsKey(key);
        }

        public bool TryGetFirst(TKey key, out TPk pk)
        {
            if (!KeyComparer<TKey>.IsNull(key)) return map.TryGetValue(key, out pk);
            pk = hasNull ? nullPk : default;
            return hasNull;
        }

        public SingleEnumerator Equal(TKey key)
        {
            var found = TryGetFirst(key, out var pk);
            return new SingleEnumerator(found, pk);
        }

        // 同じ SecondaryKey が既にある場合は何もせず false を返す.
        public bool Insert(TKey key, TPk pk)
        {
            if (!KeyComparer<TKey>.IsNull(key)) return map.TryAdd(key, pk);
            if (hasNull) return false;
            hasNull = true;
            nullPk = pk;
            return true;
        }

        public bool Remove(TKey key, TPk pk)
        {
            if (!TryGetFirst(key, out var current) || !EqualityComparer<TPk>.Default.Equals(current, pk)) return false;

            if (!KeyComparer<TKey>.IsNull(key)) map.Remove(key);
            else hasNull = false;
            return true;
        }

        // 重複があれば何も変更せず false を返す.
        public bool AddRange(ReadOnlySpan<TKey> keys, ReadOnlySpan<TPk> pks)
        {
            if (pks.Length < keys.Length) throw new ArgumentException("pks is shorter than keys.", nameof(pks));

            map.EnsureCapacity(map.Count + keys.Length);
            for (var i = 0; i < keys.Length; i++)
            {
                if (Insert(keys[i], pks[i])) continue;
                for (var j = 0; j < i; j++) Remove(keys[j], pks[j]);
                return false;
            }

            return true;
        }

        public void TrimExcess()
        {
            map.TrimExcess();
        }

        public struct SingleEnumerator : IPkEnumerator<TPk>
        {
            readonly TPk pk;
            bool pending;

            internal SingleEnumerator(bool found, TPk pk)
            {
                this.pk = pk;
                pending = found;
            }

            public TPk Current => pk;

            public bool MoveNext()
            {
                if (!pending) return false;
                pending = false;
                return true;
            }
        }
    }

    /// <summary>
    /// 完全一致でしか引かない非 Unique な SecondaryKey の索引.
    /// 値ごとに PK の配列を持つ. PK は単調増加しないため配列は順序を持たず、削除は末尾との入れ替えとする.
    /// </summary>
    public sealed class HashMultiIndex<TKey, TPk> where TPk : unmanaged
    {
        const int InitialPostingLength = 2;

        readonly Dictionary<TKey, int> map = new Dictionary<TKey, int>(KeyComparer<TKey>.Default);
        int nullPosting = -1;

        TPk[][] postings = new TPk[4][];
        int[] lengths = new int[4];   // 空きでは次の空きの番号
        int postingUsed;
        int postingFree = -1;
        int count;

        public int Count => count;

        public bool ContainsKey(TKey key)
        {
            return Find(key) >= 0;
        }

        public bool TryGetFirst(TKey key, out TPk pk)
        {
            var id = Find(key);
            if (id < 0)
            {
                pk = default;
                return false;
            }

            pk = postings[id][0];
            return true;
        }

        public Enumerator Equal(TKey key)
        {
            var id = Find(key);
            return id < 0 ? default : new Enumerator(postings[id], lengths[id]);
        }

        // 同じ組が既にあるかは確かめない (PK はテーブル内で一意なため).
        public bool Insert(TKey key, TPk pk)
        {
            var id = Find(key);
            if (id < 0)
            {
                id = AllocPosting();
                if (KeyComparer<TKey>.IsNull(key)) nullPosting = id;
                else map.Add(key, id);
            }

            var list = postings[id];
            var n = lengths[id];
            if (n == list.Length)
            {
                Array.Resize(ref list, n * 2);
                postings[id] = list;
            }

            list[n] = pk;
            lengths[id] = n + 1;
            count++;
            return true;
        }

        // その値の件数 p に対して O(p).
        public bool Remove(TKey key, TPk pk)
        {
            var id = Find(key);
            if (id < 0) return false;

            var list = postings[id];
            var last = lengths[id] - 1;
            var comparer = EqualityComparer<TPk>.Default;
            for (var i = 0; i <= last; i++)
            {
                if (!comparer.Equals(list[i], pk)) continue;

                list[i] = list[last];
                lengths[id] = last;
                count--;
                if (last == 0)
                {
                    if (KeyComparer<TKey>.IsNull(key)) nullPosting = -1;
                    else map.Remove(key);
                    FreePosting(id);
                }

                return true;
            }

            return false;
        }

        public bool AddRange(ReadOnlySpan<TKey> keys, ReadOnlySpan<TPk> pks)
        {
            if (pks.Length < keys.Length) throw new ArgumentException("pks is shorter than keys.", nameof(pks));
            for (var i = 0; i < keys.Length; i++) Insert(keys[i], pks[i]);
            return true;
        }

        public void TrimExcess()
        {
            map.TrimExcess();
            for (var id = 0; id < postingUsed; id++)
            {
                var list = postings[id];
                if (list != null && list.Length != lengths[id]) Array.Resize(ref postings[id], lengths[id]);
            }
        }

        int Find(TKey key)
        {
            if (KeyComparer<TKey>.IsNull(key)) return nullPosting;
            return map.TryGetValue(key, out var id) ? id : -1;
        }

        int AllocPosting()
        {
            int id;
            if (postingFree >= 0)
            {
                id = postingFree;
                postingFree = lengths[id];
            }
            else
            {
                if (postingUsed == postings.Length)
                {
                    Array.Resize(ref postings, postingUsed * 2);
                    Array.Resize(ref lengths, postingUsed * 2);
                }

                id = postingUsed++;
            }

            postings[id] = new TPk[InitialPostingLength];
            lengths[id] = 0;
            return id;
        }

        void FreePosting(int id)
        {
            postings[id] = null;
            lengths[id] = postingFree;
            postingFree = id;
        }

        public struct Enumerator : IPkEnumerator<TPk>
        {
            readonly TPk[] list;
            readonly int length;
            int index;

            internal Enumerator(TPk[] list, int length)
            {
                this.list = list;
                this.length = length;
                index = -1;
            }

            public TPk Current => list[index];

            public bool MoveNext()
            {
                return ++index < length;
            }
        }
    }
}
