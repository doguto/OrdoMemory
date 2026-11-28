using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace OrdoMemory
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class HashUniqueIndex<TKey, TPrimaryKey> where TPrimaryKey : unmanaged
    {
        readonly Dictionary<TKey, TPrimaryKey> map = new(KeyComparer<TKey>.Default);
        bool hasNull;
        TPrimaryKey nullPrimaryKey;

        public int Count => map.Count + (hasNull ? 1 : 0);

        public bool ContainsKey(TKey key)
        {
            return KeyComparer<TKey>.IsNull(key) ? hasNull : map.ContainsKey(key);
        }

        public bool TryGetFirst(TKey key, out TPrimaryKey primaryKey)
        {
            if (!KeyComparer<TKey>.IsNull(key)) return map.TryGetValue(key, out primaryKey);
            primaryKey = hasNull ? nullPrimaryKey : default;
            return hasNull;
        }

        public int CountOf(TKey key)
        {
            return ContainsKey(key) ? 1 : 0;
        }

        public SingleEnumerator Equal(TKey key)
        {
            var found = TryGetFirst(key, out var primaryKey);
            return new SingleEnumerator(found, primaryKey);
        }

        public bool Insert(TKey key, TPrimaryKey primaryKey)
        {
            if (!KeyComparer<TKey>.IsNull(key)) return map.TryAdd(key, primaryKey);
            if (hasNull) return false;
            hasNull = true;
            nullPrimaryKey = primaryKey;
            return true;
        }

        public bool Remove(TKey key, TPrimaryKey primaryKey)
        {
            if (!TryGetFirst(key, out var current) || !EqualityComparer<TPrimaryKey>.Default.Equals(current, primaryKey)) return false;

            if (!KeyComparer<TKey>.IsNull(key))
            {
                map.Remove(key);
            }
            else
            {
                hasNull = false;
            }

            return true;
        }

        public bool AddRange(ReadOnlySpan<TKey> keys, ReadOnlySpan<TPrimaryKey> primaryKeys)
        {
            if (primaryKeys.Length < keys.Length) throw new ArgumentException("primaryKeys is shorter than keys.", nameof(primaryKeys));

            map.EnsureCapacity(map.Count + keys.Length);
            for (var i = 0; i < keys.Length; i++)
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

        public void Clear()
        {
            map.Clear();
            hasNull = false;
            nullPrimaryKey = default;
        }

        public void TrimExcess()
        {
            map.TrimExcess();
        }

        public struct SingleEnumerator : IPrimaryKeyEnumerator<TPrimaryKey>
        {
            readonly TPrimaryKey primaryKey;
            bool pending;

            internal SingleEnumerator(bool found, TPrimaryKey primaryKey)
            {
                this.primaryKey = primaryKey;
                pending = found;
            }

            public TPrimaryKey Current => primaryKey;

            public bool MoveNext()
            {
                if (!pending) return false;
                pending = false;
                return true;
            }
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class HashMultiIndex<TKey, TPrimaryKey> where TPrimaryKey : unmanaged
    {
        const int InitialPostingLength = 2;

        const int MaxPooledPostingLength = 16;

        readonly Dictionary<TKey, int> map = new(KeyComparer<TKey>.Default);
        int nullPosting = -1;

        TPrimaryKey[][] postings = new TPrimaryKey[4][];
        int[] lengths = new int[4];
        int postingUsed;
        int[] freeIds = new int[4];
        int freeCount;
        int count;

        public int Count => count;

        public bool ContainsKey(TKey key)
        {
            return Find(key) >= 0;
        }

        public bool TryGetFirst(TKey key, out TPrimaryKey primaryKey)
        {
            var id = Find(key);
            if (id < 0)
            {
                primaryKey = default;
                return false;
            }

            primaryKey = postings[id][0];
            return true;
        }

        public int CountOf(TKey key)
        {
            var id = Find(key);
            return id < 0 ? 0 : lengths[id];
        }

        public Enumerator Equal(TKey key)
        {
            var id = Find(key);
            return id < 0 ? default : new Enumerator(postings[id], lengths[id]);
        }

        public bool Insert(TKey key, TPrimaryKey primaryKey)
        {
            var id = Find(key);
            if (id < 0)
            {
                id = AllocatePosting();
                if (KeyComparer<TKey>.IsNull(key))
                {
                    nullPosting = id;
                }
                else
                {
                    map.Add(key, id);
                }
            }

            var list = postings[id];
            var n = lengths[id];
            if (n == list.Length)
            {
                Array.Resize(ref list, n * 2);
                postings[id] = list;
            }

            list[n] = primaryKey;
            lengths[id] = n + 1;
            count++;
            return true;
        }

        public bool Remove(TKey key, TPrimaryKey primaryKey)
        {
            var id = Find(key);
            if (id < 0) return false;

            var list = postings[id];
            var last = lengths[id] - 1;
            var comparer = EqualityComparer<TPrimaryKey>.Default;
            for (var i = 0; i <= last; i++)
            {
                if (!comparer.Equals(list[i], primaryKey)) continue;

                list[i] = list[last];
                lengths[id] = last;
                count--;
                if (last == 0)
                {
                    if (KeyComparer<TKey>.IsNull(key))
                    {
                        nullPosting = -1;
                    }
                    else
                    {
                        map.Remove(key);
                    }

                    FreePosting(id);
                }

                return true;
            }

            return false;
        }

        public bool AddRange(ReadOnlySpan<TKey> keys, ReadOnlySpan<TPrimaryKey> primaryKeys)
        {
            if (primaryKeys.Length < keys.Length) throw new ArgumentException("primaryKeys is shorter than keys.", nameof(primaryKeys));
            for (var i = 0; i < keys.Length; i++)
            {
                Insert(keys[i], primaryKeys[i]);
            }

            return true;
        }

        public void Clear()
        {
            map.Clear();
            nullPosting = -1;
            for (var id = 0; id < postingUsed; id++)
            {
                if (postings[id] != null && postings[id].Length > MaxPooledPostingLength) postings[id] = null;
            }

            postingUsed = 0;
            freeCount = 0;
            count = 0;
        }

        public void TrimExcess()
        {
            map.TrimExcess();
            for (var i = 0; i < freeCount; i++) postings[freeIds[i]] = null;
            Array.Clear(postings, postingUsed, postings.Length - postingUsed);
            for (var id = 0; id < postingUsed; id++)
            {
                if (postings[id] != null && postings[id].Length != lengths[id]) Array.Resize(ref postings[id], lengths[id]);
            }
        }

        int Find(TKey key)
        {
            if (KeyComparer<TKey>.IsNull(key)) return nullPosting;
            return map.TryGetValue(key, out var id) ? id : -1;
        }

        int AllocatePosting()
        {
            int id;
            if (freeCount > 0)
            {
                id = freeIds[--freeCount];
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

            if (postings[id] == null) postings[id] = new TPrimaryKey[InitialPostingLength];
            lengths[id] = 0;
            return id;
        }

        void FreePosting(int id)
        {
            if (postings[id].Length > MaxPooledPostingLength) postings[id] = null;
            if (freeCount == freeIds.Length) Array.Resize(ref freeIds, freeCount * 2);
            freeIds[freeCount++] = id;
        }

        public struct Enumerator : IPrimaryKeyEnumerator<TPrimaryKey>
        {
            readonly TPrimaryKey[] list;
            readonly int length;
            int index;

            internal Enumerator(TPrimaryKey[] list, int length)
            {
                this.list = list;
                this.length = length;
                index = -1;
            }

            public TPrimaryKey Current => list[index];

            public bool MoveNext()
            {
                return ++index < length;
            }
        }
    }
}
