using System;
using System.Collections.Generic;
using System.Linq;

namespace OrdoMemory.Bench
{
    public static class Report
    {
        // 実装ごとに複数回の結果を受け取り、中央値を表にする. Markdown として貼れる形式.
        public static void PrintTable(string title, List<string> impls, string[] metrics, Dictionary<string, List<Dictionary<string, double>>> results)
        {
            var runtime = Type.GetType("Mono.Runtime") != null ? "Mono " : "CoreCLR ";
            Console.WriteLine();
            Console.WriteLine($"### {title}  [{runtime}{Environment.Version}]");
            Console.WriteLine();
            Console.WriteLine("| impl | " + string.Join(" | ", metrics) + " |");
            Console.WriteLine("|---|" + string.Concat(metrics.Select(_ => "---:|")));
            foreach (var impl in impls)
            {
                var cells = metrics.Select(metric => Format(Median(results[impl].Where(r => r.ContainsKey(metric)).Select(r => r[metric]))));
                Console.WriteLine($"| {impl} | " + string.Join(" | ", cells) + " |");
            }
        }

        static double Median(IEnumerable<double> values)
        {
            var a = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
            if (a.Length == 0) return double.NaN;
            return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
        }

        static string Format(double v)
        {
            if (double.IsNaN(v)) return "-";
            if (v >= 100) return v.ToString("F0");
            if (v >= 10) return v.ToString("F1");
            return v.ToString("F2");
        }
    }
}
