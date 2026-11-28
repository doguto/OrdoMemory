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

        readonly Dictionary<TKey, int> map = new(KeyComparer<TKey>.Default);

        // PK から、その PK が入っている PK 配列の中での位置への逆引き. 削除で配列を探さずに済ませるため
        // 1 つの PK は索引の中で 1 つの値にしか属さない (Record は値を 1 つだけ持つ) 前提
        readonly Dictionary<TPrimaryKey, int> positions = new();
        int nullPosting = -1;

        TPrimaryKey[][] postings = new TPrimaryKey[4][];
        int[] lengths = new int[4];
        int postingUsed;
        int postingFree = -1;
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

        public Enumerator Equal(TKey key)
        {
            var id = Find(key);
            return id < 0 ? default : new Enumerator(postings[id], lengths[id]);
        }

        // 同じ PK が既に索引にある場合は挿入せずに false を返す
        public bool Insert(TKey key, TPrimaryKey primaryKey)
        {
            var id = Find(key);
            var n = id < 0 ? 0 : lengths[id];
            if (!positions.TryAdd(primaryKey, n)) return false;

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

            // 逆引きした位置にその PK があれば、この値に属している
            if (!positions.TryGetValue(primaryKey, out var i)) return false;
            var list = postings[id];
            var last = lengths[id] - 1;
            if (i > last || !EqualityComparer<TPrimaryKey>.Default.Equals(list[i], primaryKey)) return false;

            // 末尾との入れ替えで消し、移した PK の位置を書き換える
            if (i != last)
            {
                var moved = list[last];
                list[i] = moved;
                positions[moved] = i;
            }

            positions.Remove(primaryKey);
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

        public bool AddRange(ReadOnlySpan<TKey> keys, ReadOnlySpan<TPrimaryKey> primaryKeys)
        {
            if (primaryKeys.Length < keys.Length) throw new ArgumentException("primaryKeys is shorter than keys.", nameof(primaryKeys));

            positions.EnsureCapacity(positions.Count + keys.Length);
            for (var i = 0; i < keys.Length; i++)
            {
                if (Insert(keys[i], primaryKeys[i])) continue;

                // 重複した PK があれば、この呼び出しで挿入した分を戻す
                for (var j = 0; j < i; j++)
                {
                    Remove(keys[j], primaryKeys[j]);
                }

                return false;
            }

            return true;
        }

        public void TrimExcess()
        {
            map.TrimExcess();
            positions.TrimExcess();
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

        int AllocatePosting()
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

            postings[id] = new TPrimaryKey[InitialPostingLength];
            lengths[id] = 0;
            return id;
        }

        void FreePosting(int id)
        {
            postings[id] = null;
            lengths[id] = postingFree;
            postingFree = id;
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
