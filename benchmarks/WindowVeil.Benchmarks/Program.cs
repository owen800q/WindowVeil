using BenchmarkDotNet.Running;

namespace WindowVeil.Benchmarks;

/// <summary>
/// <code>
/// dotnet run -c Release --project benchmarks/WindowVeil.Benchmarks -- --filter *          # BenchmarkDotNet suites
/// dotnet run -c Release --project benchmarks/WindowVeil.Benchmarks -- --probe [report.md] # handles, memory, tracking CPU
/// </code>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe")
            return await ResourceProbe.RunAsync(args.Length > 1 ? args[1] : Path.Combine("artifacts", "benchmarks", "resource-probe.md"));
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
