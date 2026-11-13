using System;
using OrdoMemory.Bench.Pk;

namespace OrdoMemory.Bench
{
    static class Program
    {
        // 使い方: pk [--n 1000000] [--runs 3] [--filter Block] / verify
        static int Main(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : "pk";
            var n = IntArg(args, "--n", 1000000);
            var runs = IntArg(args, "--runs", 3);
            var filter = StringArg(args, "--filter");

            switch (mode)
            {
                case "verify":
                    PkBench.Verify();
                    Sec.SecBench.Verify();
                    return 0;
                case "sec":
                    Sec.SecBench.RunAll(n, runs, filter);
                    return 0;
                case "pk":
                    PkBench.RunAll(n, runs, filter);
                    return 0;
                default:
                    Console.Error.WriteLine("unknown mode");
                    return 1;
            }
        }

        static int IntArg(string[] args, string name, int fallback)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : fallback;
        }

        static string StringArg(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
