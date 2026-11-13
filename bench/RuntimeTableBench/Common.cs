using System;
using System.Runtime.CompilerServices;

namespace OrdoMemory.Bench
{
    // 計測用の Record. 64bit 環境で 32B (参照 2 + int 3 + パディング).
    public struct Row
    {
        public int Id;
        public int Score;
        public int Category;
        public string Name;
        public string Description;
    }

    // 現行案 (PR #10) の class 版 Record.
    public sealed class RowObj
    {
        public int Id;
        public int Score;
        public int Category;
        public string Name;
        public string Description;
    }

    // 主キー側の共通操作. ベンチマークは struct ラッパー経由で呼ぶ (インターフェース呼び出しのコストを含めないため).
    public interface IPkTable
    {
        string Name { get; }
        int Count { get; }
        bool Insert(in Row row, out int key);
        // 見つからない場合は -1. Score は非負.
        int GetScore(int key);
        bool UpdateScore(int key, int score);
        bool Remove(int key);
        long SumAll();
        // Key 順を持たない実装は false.
        bool SupportsRange { get; }
        long SumRange(int min, int max);
    }

    // netstandard2.1 には BitOperations が無いため、Unity と同じ条件で SWAR を使う.
    public static class Bits
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int PopCount(ulong x)
        {
            x -= (x >> 1) & 0x5555555555555555UL;
            x = (x & 0x3333333333333333UL) + ((x >> 2) & 0x3333333333333333UL);
            x = (x + (x >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((x * 0x0101010101010101UL) >> 56);
        }

        // x != 0 であること.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int TrailingZeroCount(ulong x)
        {
            return PopCount((x & (0UL - x)) - 1);
        }

        // bit 未満のビットを残すマスク. bit は 0..63.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Below(int bit)
        {
            return (1UL << bit) - 1;
        }

        // bit 以上のビットを残すマスク. bit は 0..63.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong AtOrAbove(int bit)
        {
            return ~0UL << bit;
        }
    }

    public struct XorShift
    {
        ulong s;

        public XorShift(ulong seed)
        {
            s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public ulong Next()
        {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            return s;
        }

        public int Next(int max)
        {
            return (int)((Next() >> 33) % (ulong)max);
        }
    }
}
