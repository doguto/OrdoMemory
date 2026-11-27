using System;
using System.Collections;
using System.Collections.Generic;
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

        static void ThrowDestinationTooShort()
        {
            throw new ArgumentException("Destination is too short.", "destination");
        }

        // foreach では struct の GetEnumerator を使い割り当てなしで列挙する
        // LINQ 等へ渡す場合は IEnumerable<TRow> の明示実装を経由する (boxing が発生する)
        public readonly struct AllEnumerable : IEnumerable<TRow>
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

            // 全 Record を destination の先頭へコピーし、コピーした件数を返す
            public int CopyTo(Span<TRow> destination)
            {
                var count = store.denseCount;
                if (destination.Length < count) ThrowDestinationTooShort();
                new ReadOnlySpan<TRow>(store.dense, 0, count).CopyTo(destination);
                return count;
            }

            IEnumerator<TRow> IEnumerable<TRow>.GetEnumerator()
            {
                return GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        public struct AllEnumerator : IEnumerator<TRow>
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

            public bool MoveNext()
            {
                store.CheckVersion(version);
                return ++index < store.denseCount;
            }

            TRow IEnumerator<TRow>.Current => Current;

            object IEnumerator.Current => Current;

            void IEnumerator.Reset()
            {
                store.CheckVersion(version);
                index = -1;
            }

            void IDisposable.Dispose()
            {
            }
        }

        public readonly struct SubsetEnumerable<TPrimaryKeyEnumerator> : IEnumerable<TRow> where TPrimaryKeyEnumerator : struct, IPrimaryKeyEnumerator<TPrimaryKey>
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

            // 対象の Record を destination の先頭へコピーし、コピーした件数を返す
            // 件数は列挙するまで分からないため、destination が足りなくなった時点で例外を投げる
            public int CopyTo(Span<TRow> destination)
            {
                var written = 0;
                foreach (ref readonly var row in this)
                {
                    if (written == destination.Length) ThrowDestinationTooShort();
                    destination[written++] = row;
                }

                return written;
            }

            IEnumerator<TRow> IEnumerable<TRow>.GetEnumerator()
            {
                return GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        public struct SubsetEnumerator<TPrimaryKeyEnumerator> : IEnumerator<TRow> where TPrimaryKeyEnumerator : struct, IPrimaryKeyEnumerator<TPrimaryKey>
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

            public bool MoveNext()
            {
                store.CheckVersion(version);
                if (!primaryKeys.MoveNext()) return false;
                index = store.GetIndex(primaryKeys.Current);
                return true;
            }

            TRow IEnumerator<TRow>.Current => Current;

            object IEnumerator.Current => Current;

            // 主キー列挙子は巻き戻せないため Reset は未サポート
            void IEnumerator.Reset()
            {
                throw new NotSupportedException();
            }

            void IDisposable.Dispose()
            {
            }
        }
    }
}
