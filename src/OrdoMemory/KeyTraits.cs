using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IOrderedKeyTraits<TKey>
    {
        int NormalizedBits { get; }

        bool NeedsFullComparison { get; }

        ulong Normalize(TKey key);

        int CompareFull(TKey a, TKey b);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct Int32Key : IOrderedKeyTraits<int>
    {
        public int NormalizedBits => 32;
        public bool NeedsFullComparison => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Normalize(int key)
        {
            return (uint)(key ^ int.MinValue);
        }

        public int CompareFull(int a, int b)
        {
            return a.CompareTo(b);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct Int64Key : IOrderedKeyTraits<long>
    {
        public int NormalizedBits => 64;
        public bool NeedsFullComparison => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Normalize(long key)
        {
            return (ulong)(key ^ long.MinValue);
        }

        public int CompareFull(long a, long b)
        {
            return a.CompareTo(b);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct UInt64Key : IOrderedKeyTraits<ulong>
    {
        public int NormalizedBits => 64;
        public bool NeedsFullComparison => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Normalize(ulong key)
        {
            return key;
        }

        public int CompareFull(ulong a, ulong b)
        {
            return a.CompareTo(b);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct DoubleKey : IOrderedKeyTraits<double>
    {
        const ulong SignBit = 1UL << 63;

        public int NormalizedBits => 64;
        public bool NeedsFullComparison => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Normalize(double key)
        {
            var bits = (ulong)BitConverter.DoubleToInt64Bits(key);
            return (bits & SignBit) != 0 ? ~bits : bits | SignBit;
        }

        public int CompareFull(double a, double b)
        {
            return a.CompareTo(b);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct StringKey : IOrderedKeyTraits<string>
    {
        public int NormalizedBits => 64;
        public bool NeedsFullComparison => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Normalize(string key)
        {
            if (key == null) return 0;
            ulong v = 0;
            var n = key.Length < 4 ? key.Length : 4;
            for (var i = 0; i < n; i++)
            {
                v |= (ulong)key[i] << (48 - 16 * i);
            }

            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareFull(string a, string b)
        {
            return string.CompareOrdinal(a, b);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class KeyConvert
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Double(double value)
        {
            if (value == 0) return 0;
            return double.IsNaN(value) ? double.NaN : value;
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class KeyComparer<TKey>
    {
        public static readonly IEqualityComparer<TKey> Default = typeof(TKey) == typeof(string)
            ? (IEqualityComparer<TKey>)(object)StringComparer.Ordinal
            : EqualityComparer<TKey>.Default;

        static readonly bool canBeNull = default(TKey) == null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNull(TKey key)
        {
            return canBeNull && key == null;
        }
    }
}
