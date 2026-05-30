using Cs2Dumper.Logging;
using Cs2Dumper.Memory;

namespace Cs2Dumper.Analysis;

/// <summary>Port of <c>analysis/mod.rs</c>: runs each pass, isolating failures to an empty default.</summary>
public static class Analyzer
{
    public static AnalysisResult AnalyzeAll(GameProcess process)
    {
        SortedDictionary<string, ulong> buttons = Run("buttons", () => Buttons.Analyze(process), AnalysisResult.NewButtonMap());
        Log.Info($"found {buttons.Count} buttons");

        SortedDictionary<string, SortedDictionary<string, ulong>> interfaces =
            Run("interfaces", () => Interfaces.Analyze(process), AnalysisResult.NewInterfaceMap());
        int interfaceCount = 0;
        foreach (KeyValuePair<string, SortedDictionary<string, ulong>> module in interfaces)
        {
            interfaceCount += module.Value.Count;
        }

        Log.Info($"found {interfaceCount} interfaces across {interfaces.Count} modules");

        SortedDictionary<string, SortedDictionary<string, uint>> offsets =
            Run("offsets", () => Offsets.Analyze(process), AnalysisResult.NewOffsetMap());
        int offsetCount = 0;
        foreach (KeyValuePair<string, SortedDictionary<string, uint>> module in offsets)
        {
            offsetCount += module.Value.Count;
        }

        Log.Info($"found {offsetCount} offsets across {offsets.Count} modules");

        SortedDictionary<string, SchemaModule> schemas =
            Run("schemas", () => Schemas.Analyze(process), AnalysisResult.NewSchemaMap());
        int classCount = 0;
        int enumCount = 0;
        foreach (KeyValuePair<string, SchemaModule> module in schemas)
        {
            classCount += module.Value.Classes.Count;
            enumCount += module.Value.Enums.Count;
        }

        Log.Info($"found {classCount} classes and {enumCount} enums across {schemas.Count} modules");

        return new AnalysisResult
        {
            Buttons = buttons,
            Interfaces = interfaces,
            Offsets = offsets,
            Schemas = schemas,
        };
    }

    private static T Run<T>(string name, Func<T> analyze, T fallback)
    {
        try
        {
            return analyze();
        }
        catch (Exception e)
        {
            Log.Error($"failed to read {name}: {e.Message}");
            return fallback;
        }
    }
}
