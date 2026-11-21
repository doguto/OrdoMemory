using System;

namespace OrdoMemory.Benchmark
{
    public struct ItemData
    {
        public int Code;
        public int Category;
        public int Value;
        public string Name;
        public string Description;
    }

    public static class Dataset
    {
        public const int RowsPerCategory = 64;

        static readonly string[] Descriptions = CreateDescriptions();

        public static int CategoryCount(int n) => Math.Max(1, n / RowsPerCategory);

        public static ItemData[] Create(int n, ulong seed)
        {
            var rng = new Rng(seed);
            var codes = new int[n];
            for (var i = 0; i < n; i++) codes[i] = i;
            for (var i = n - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (codes[i], codes[j]) = (codes[j], codes[i]);
            }

            var categories = CategoryCount(n);
            var rows = new ItemData[n];
            for (var i = 0; i < n; i++)
            {
                rows[i] = new ItemData
                {
                    Code = codes[i],
                    Category = rng.Next(categories),
                    Value = rng.Next(1_000_000),
                    Name = NameOf(codes[i]),
                    Description = Descriptions[rng.Next(Descriptions.Length)],
                };
            }

            return rows;
        }

        public static string NameOf(int code) => "item_" + code.ToString("D8");

        static string[] CreateDescriptions()
        {
            var result = new string[16];
            for (var i = 0; i < result.Length; i++) result[i] = "description of item kind " + i;
            return result;
        }
    }

    public struct Rng
    {
        ulong s;

        public Rng(ulong seed)
        {
            s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public ulong NextUInt64()
        {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            return s;
        }

        public int Next(int exclusiveMax) => (int)(NextUInt64() % (ulong)exclusiveMax);

        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    }

    public sealed class Zipfian
    {
        const double Theta = 0.99;

        readonly int n;
        readonly double zetan;
        readonly double alpha;
        readonly double eta;
        readonly bool scramble;
        Rng rng;

        public Zipfian(int n, ulong seed, bool scramble = true)
        {
            this.n = n;
            this.scramble = scramble;
            rng = new Rng(seed);

            for (var i = 1; i <= n; i++) zetan += 1.0 / Math.Pow(i, Theta);
            var zeta2 = 1.0 + 1.0 / Math.Pow(2, Theta);
            alpha = 1.0 / (1.0 - Theta);
            eta = (1.0 - Math.Pow(2.0 / n, 1.0 - Theta)) / (1.0 - zeta2 / zetan);
        }

        public int Next()
        {
            var u = rng.NextDouble();
            var uz = u * zetan;
            long rank;
            if (uz < 1.0) rank = 0;
            else if (uz < 1.0 + Math.Pow(0.5, Theta)) rank = 1;
            else rank = (long)(n * Math.Pow(eta * u - eta + 1.0, alpha));
            if (rank >= n) rank = n - 1;

            return scramble ? (int)(Fnv64((ulong)rank) % (ulong)n) : (int)rank;
        }

        static ulong Fnv64(ulong value)
        {
            var hash = 0xCBF29CE484222325UL;
            for (var i = 0; i < 8; i++)
            {
                hash ^= value & 0xFF;
                hash *= 0x100000001B3UL;
                value >>= 8;
            }

            return hash;
        }
    }
}
