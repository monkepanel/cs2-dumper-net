using System.Diagnostics;
using System.Globalization;
using Cs2Dumper.Analysis;
using Cs2Dumper.Cli;
using Cs2Dumper.Logging;
using Cs2Dumper.Memory;
using Cs2Dumper.Output;

namespace Cs2Dumper;

internal static class Program
{
    private static int Main(string[] argv)
    {
        Args args;
        try
        {
            Args? parsed = Args.Parse(argv);
            if (parsed is null)
            {
                return 0; // help / version already printed
            }

            args = parsed;
        }
        catch (ArgException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 2;
        }

        Log.Init(Log.ConsoleLevelFromVerbosity(args.Verbose), args.NoLogFile ? null : "cs2-dumper.log");

        if (args.Connector is not null)
        {
            Log.Warn("a memflow connector was requested but this port always reads memory through the OS; ignoring");
        }

        try
        {
            using var process = GameProcess.OpenByName(args.ProcessName);

            var stopwatch = Stopwatch.StartNew();

            AnalysisResult result = Analyzer.AnalyzeAll(process);

            var output = new Output.Output(args.FileTypes, args.IndentSize, args.Output, result);
            output.DumpAll(process);

            stopwatch.Stop();
            Log.Info($"analysis completed in {FormatElapsed(stopwatch.Elapsed)}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        double seconds = elapsed.TotalSeconds;
        if (seconds >= 1.0)
        {
            return seconds.ToString("F2", CultureInfo.InvariantCulture) + "s";
        }

        double millis = elapsed.TotalMilliseconds;
        if (millis >= 1.0)
        {
            return millis.ToString("F2", CultureInfo.InvariantCulture) + "ms";
        }

        return (millis * 1000.0).ToString("F2", CultureInfo.InvariantCulture) + "µs";
    }
}
