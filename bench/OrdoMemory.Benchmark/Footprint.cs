using System;
using System.IO;
using System.Text;

namespace OrdoMemory.Benchmark
{
    public static class Footprint
    {
        public static void Run(string outputPath)
        {
            SQLitePCL.Batteries_V2.Init();
            var sb = new StringBuilder();
            sb.AppendLine("| Target | N | managed B/row | native B/row | total B/row |");
            sb.AppendLine("|---|---:|---:|---:|---:|");

            foreach (var n in BenchEnv.Sizes())
            {
                foreach (var target in BenchEnv.Targets())
                {
                    using (var warm = StoreFactory.Create(target)) warm.Insert(Dataset.Create(1, 1)[0]);

                    var (managed, native) = Measure(target, n);
                    var line = $"| {target} | {n:N0} | {managed / (double)n:F1} | {native / (double)n:F1} | {(managed + native) / (double)n:F1} |";
                    Console.WriteLine(line);
                    sb.AppendLine(line);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
            File.WriteAllText(outputPath, sb.ToString());
            Console.WriteLine($"-> {outputPath}");
        }

        static (long managed, long native) Measure(Target target, int n)
        {
            Collect();
            var managed0 = GC.GetTotalMemory(true);
            var native0 = SQLitePCL.raw.sqlite3_memory_used();

            var store = StoreFactory.Create(target);
            Load(store, n);

            Collect();
            var managed1 = GC.GetTotalMemory(true);
            var native1 = SQLitePCL.raw.sqlite3_memory_used();
            GC.KeepAlive(store);
            store.Dispose();

            return (managed1 - managed0, native1 - native0);
        }

        static void Load(IStore store, int n)
        {
            store.BulkLoad(Dataset.Create(n, 20261120), new long[n]);
        }

        static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
