using System;

namespace OrdoMemory.Sample
{
    public sealed class SampleTableOptions
    {
        public const double DefaultCompactionThreshold = 0.3;

        // false の場合、自動コンパクションを行わない. 手動の Compact は利用できる.
        public bool AutoCompaction { get; }

        // 使用中スロットのうち、削除済みスロットが占める割合. 0 < 値 <= 1. (N割 = N / 10)
        public double CompactionThreshold { get; }

        public SampleTableOptions(
            bool autoCompaction = true,
            double compactionThreshold = DefaultCompactionThreshold)
        {
            if (!(compactionThreshold > 0 && compactionThreshold <= 1))
            {
                throw new ArgumentOutOfRangeException(nameof(compactionThreshold));
            }

            AutoCompaction = autoCompaction;
            CompactionThreshold = compactionThreshold;
        }
    }
}
