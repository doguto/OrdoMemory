using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    /// <summary>
    /// Ordered 索引のキーを ulong に正規化する. ulong の大小がキーの順序と矛盾しないこと.
    /// NeedsFull が false の場合、正規化は単射であること.
    /// </summary>
    public interface IOrderedKeyTraits<TKey>
    {
        // 正規化キーが使うビット数. PK と 1 つの ulong に詰められるかの判定に使う.
        int NormBits { get; }

        // 正規化キーが等しいとき、完全なキーの比較が必要か.
        bool NeedsFull { get; }

        ulong Normalize(TKey key);

        int CompareFull(TKey a, TKey b);
    }

    public readonly struct Int32Key : IOrderedKeyTraits<int>
    {
        public int NormBits => 32;
        public bool NeedsFull => false;

        // 符号ビットを反転すると、符号無しの比較で int の順序になる.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Normalize(int key) => (uint)(key ^ int.MinValue);

        public int CompareFull(int a, int b) => a.CompareTo(b);
    }

    public readonly struct Int64Key : IOrderedKeyTraits<long>
    {
        public int NormBits => 64;
        public bool NeedsFull => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Normalize(long key) => (ulong)(key ^ long.MinValue);

        public int CompareFull(long a, long b) => a.CompareTo(b);
    }

    public readonly struct UInt64Key : IOrderedKeyTraits<ulong>
    {
        public int NormBits => 64;
        public bool NeedsFull => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Normalize(ulong key) => key;

        public int CompareFull(ulong a, ulong b) => a.CompareTo(b);
    }

    // KeyConvert.Double で正規化したキーを前提とする.
    public readonly struct DoubleKey : IOrderedKeyTraits<double>
    {
        const ulong SignBit = 1UL << 63;

        public int NormBits => 64;
        public bool NeedsFull => false;

        // 負数は全ビット反転、正数は符号ビットだけ立てると、符号無しの比較で double の順序になる.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Normalize(double key)
        {
            var bits = (ulong)BitConverter.DoubleToInt64Bits(key);
            return (bits & SignBit) != 0 ? ~bits : bits | SignBit;
        }

        public int CompareFull(double a, double b) => a.CompareTo(b);
    }

    public readonly struct StringKey : IOrderedKeyTraits<string>
    {
        public int NormBits => 64;
        public bool NeedsFull => true;

        // 先頭 4 文字 (UTF-16) を上位から詰める. 順序は string.CompareOrdinal と矛盾しない.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Normalize(string key)
        {
            if (key == null) return 0;
            ulong v = 0;
            var n = key.Length < 4 ? key.Length : 4;
            for (var i = 0; i < n; i++) v |= (ulong)key[i] << (48 - 16 * i);
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareFull(string a, string b) => string.CompareOrdinal(a, b);
    }

    public static class KeyConvert
    {
        // -0.0 と 0.0、NaN の表現の違いを 1 つにまとめ、索引上で同じキーとして扱う.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Double(double value)
        {
            if (value == 0) return 0;
            return double.IsNaN(value) ? double.NaN : value;
        }
    }

    public static class KeyComparer<TKey>
    {
        public static readonly IEqualityComparer<TKey> Default = typeof(TKey) == typeof(string)
            ? (IEqualityComparer<TKey>)(object)StringComparer.Ordinal
            : EqualityComparer<TKey>.Default;

        static readonly bool CanBeNull = default(TKey) == null;

        // 値型で key == null の box が実行されないよう、先に CanBeNull で弾く (JIT の最適化が無い環境向け).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNull(TKey key) => CanBeNull && key == null;
    }
}
