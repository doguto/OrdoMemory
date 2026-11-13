using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace OrdoMemory.Bench.Sec
{
    // 二次索引の共通操作. PK は非負.
    public interface ISecIndex<TKey>
    {
        string Name { get; }
        bool SupportsRange { get; }
        bool Insert(TKey key, int pk);
        bool Remove(TKey key, int pk);
        // key と等しい SecondaryKey を持つ PK の合計と件数.
        long SumEqual(TKey key, out int count);
        // min 以上 max 以下.
        long SumRange(TKey min, TKey max, out int count);
        // 空の索引に一括構築する. 未対応なら false.
        bool Bulk(TKey[] keys, int[] pks, int n);
    }

    // 配列アリーナ B+Tree 用のキー正規化. struct で渡し、呼び出しをインライン化させる.
    public interface IKeyTraits<TKey>
    {
        // true: 正規化キーに PK を含み、ulong の比較だけで (SecondaryKey, PK) の順序が決まる.
        bool PacksPk { get; }
        // true: 正規化キーが等しいとき、完全なキーの比較が必要.
        bool NeedsFull { get; }
        ulong Normalize(TKey key, int pk);
        int CompareFull(TKey a, TKey b);
    }

    public struct IntKeyTraits : IKeyTraits<int>
    {
        public bool PacksPk => true;
        public bool NeedsFull => false;

        // 符号ビットを反転すると、符号無しの比較で int の順序になる. 下位 32bit に PK を詰める.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Normalize(int key, int pk) => ((ulong)(uint)(key ^ int.MinValue) << 32) | (uint)pk;

        public int CompareFull(int a, int b) => a.CompareTo(b);
    }

    public struct StringKeyTraits : IKeyTraits<string>
    {
        public bool PacksPk => false;
        public bool NeedsFull => true;

        // 先頭 4 文字 (UTF-16) をビッグエンディアンで詰める. 足りない分は 0. 順序は string.CompareOrdinal と矛盾しない.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Normalize(string key, int pk)
        {
            ulong v = 0;
            var n = key.Length < 4 ? key.Length : 4;
            for (var i = 0; i < n; i++) v |= (ulong)key[i] << (48 - 16 * i);
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareFull(string a, string b) => string.CompareOrdinal(a, b);
    }

    public sealed class EntryComparer<TKey> : IComparer<(TKey Key, int Pk)>
    {
        readonly IComparer<TKey> keyComparer;

        public EntryComparer(IComparer<TKey> keyComparer)
        {
            this.keyComparer = keyComparer;
        }

        public int Compare((TKey Key, int Pk) x, (TKey Key, int Pk) y)
        {
            var c = keyComparer.Compare(x.Key, y.Key);
            return c != 0 ? c : x.Pk.CompareTo(y.Pk);
        }
    }

    public static class Comparers
    {
        public static IComparer<TKey> For<TKey>()
        {
            if (typeof(TKey) == typeof(string)) return (IComparer<TKey>)(object)StringComparer.Ordinal;
            return Comparer<TKey>.Default;
        }
    }
}
