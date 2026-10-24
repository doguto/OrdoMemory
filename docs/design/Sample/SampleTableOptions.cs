using System;

namespace OrdoMemory.Sample
{
    public sealed class SampleTableOptions
    {
        public const double DefaultCompactionThreshold = 0.3;

        public bool AutoCompaction { get; }

        public double CompactionThreshold { get; }

        public SampleTableOptions(
            bool autoCompaction = true,
            double compactionThreshold = DefaultCompactionThreshold
        )
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
