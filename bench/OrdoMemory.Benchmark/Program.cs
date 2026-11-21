using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

namespace OrdoMemory.Benchmark
{
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "verify") return Verify.Run() ? 0 : 1;

            if (args.Length > 0 && args[0] == "footprint")
            {
                Footprint.Run(args.Length > 1 ? args[1] : "BenchmarkDotNet.Artifacts/results/footprint.md");
                return 0;
            }

            var config = DefaultConfig.Instance
                .AddDiagnoser(MemoryDiagnoser.Default)
                .AddColumn(StatisticColumn.P95)
                .HideColumns(Column.Error, Column.StdDev, Column.RatioSD)
                .WithOptions(ConfigOptions.DisableLogFile);

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
            return 0;
        }
    }
}
