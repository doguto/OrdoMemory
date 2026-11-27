using System;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    public sealed partial class RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits>
    {
        public AllEnumerable All()
        {
            return new AllEnumerable(this);
        }

        public SubsetEnumerable<TPrimaryKeyEnumerator> Subset<TPrimaryKeyEnumerator>(TPrimaryKeyEnumerator primaryKeys) where TPrimaryKeyEnumerator : struct, IPrimaryKeyEnumerator<TPrimaryKey>
        {
            return new SubsetEnumerable<TPrimaryKeyEnumerator>(this, primaryKeys);
        }

        // Enumerable 系の遅延評価でアクセスする場合に取得してから変更が無いか確認する
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckVersion(int expected)
        {
            if (expected != version) ThrowModified();
        }

        static void ThrowModified()
        {
            throw new InvalidOperationException("The table was modified during enumeration.");
        }

        public readonly struct AllEnumerable
        {
            readonly RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store;

            internal AllEnumerable(RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store)
            {
                this.store = store;
            }

            public AllEnumerator GetEnumerator()
            {
                return new AllEnumerator(store);
            }
        }

        public struct AllEnumerator
        {
            readonly RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store;
            readonly int version;
            int index;

            internal AllEnumerator(RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store)
            {
                this.store = store;
                version = store.version;
                index = -1;
            }

            public ref readonly TRow Current => ref store.dense[index];

            public TPrimaryKey CurrentPrimaryKey => store.GetPrimaryKey(index);

            public bool MoveNext()
            {
                store.CheckVersion(version);
                return ++index < store.denseCount;
            }
        }

        public readonly struct SubsetEnumerable<TPrimaryKeyEnumerator> where TPrimaryKeyEnumerator : struct, IPrimaryKeyEnumerator<TPrimaryKey>
        {
            readonly RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store;
            readonly TPrimaryKeyEnumerator primaryKeys;
            readonly int version;

            internal SubsetEnumerable(RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store, TPrimaryKeyEnumerator primaryKeys)
            {
                this.store = store;
                this.primaryKeys = primaryKeys;
                version = store.version;
            }

            public SubsetEnumerator<TPrimaryKeyEnumerator> GetEnumerator()
            {
                return new SubsetEnumerator<TPrimaryKeyEnumerator>(store, primaryKeys, version);
            }
        }

        public struct SubsetEnumerator<TPrimaryKeyEnumerator> where TPrimaryKeyEnumerator : struct, IPrimaryKeyEnumerator<TPrimaryKey>
        {
            readonly RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store;
            readonly int version;
            TPrimaryKeyEnumerator primaryKeys;
            int index;

            internal SubsetEnumerator(RowStore<TRow, TPrimaryKey, TPrimaryKeyTraits> store, TPrimaryKeyEnumerator primaryKeys, int version)
            {
                this.store = store;
                this.primaryKeys = primaryKeys;
                this.version = version;
                index = -1;
            }

            public ref readonly TRow Current => ref store.dense[index];

            public TPrimaryKey CurrentPrimaryKey => primaryKeys.Current;

            public bool MoveNext()
            {
                store.CheckVersion(version);
                if (!primaryKeys.MoveNext()) return false;
                index = store.GetIndex(primaryKeys.Current);
                return true;
            }
        }
    }
}
