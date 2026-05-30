using System.Text;
using Cs2Dumper.Logging;
using Cs2Dumper.Memory;
using Cs2Dumper.Pe;
using Cs2Dumper.Source2;

namespace Cs2Dumper.Analysis;

/// <summary>Port of <c>analysis/interfaces.rs</c>: resolves CreateInterface registries per module.</summary>
internal static class Interfaces
{
    private static readonly byte[] CreateInterface = "CreateInterface"u8.ToArray();

    public static SortedDictionary<string, SortedDictionary<string, ulong>> Analyze(GameProcess process)
    {
        var result = AnalysisResult.NewInterfaceMap();

        foreach (ModuleInfo module in process.Modules)
        {
            try
            {
                byte[] buf = process.ReadImage(module);
                var view = PeImage.FromBytes(buf);

                if (!view.TryFindExportSymbol(CreateInterface, out uint symbol))
                {
                    continue;
                }

                ulong listPtr = Address.ResolveRip(process, module.Base + symbol);
                ulong listHead = process.ReadU64(listPtr);

                SortedDictionary<string, ulong> ifaces = ReadInterfaces(process, module, listHead);
                if (ifaces.Count > 0)
                {
                    result[module.Name] = ifaces;
                }
            }
            catch (Exception e) when (e is MemoryReadException or PeException)
            {
                // Mirrors the reference's `.ok()?`: any failure for a module simply skips it.
            }
        }

        return result;
    }

    private static SortedDictionary<string, ulong> ReadInterfaces(GameProcess mem, ModuleInfo module, ulong listHead)
    {
        var result = AnalysisResult.NewInterfaceEntries();

        ulong regPtr = listHead;
        while (regPtr != 0)
        {
            ulong createFn = mem.ReadU64(regPtr + Layout.InterfaceRegCreateFn);
            ulong namePtr = mem.ReadU64(regPtr + Layout.InterfaceRegName);
            ulong next = mem.ReadU64(regPtr + Layout.InterfaceRegNext);

            string name = mem.ReadUtf8Lossy(namePtr, 128);
            ulong instanceAddr = Address.ResolveRip(mem, createFn);

            if (instanceAddr >= module.Base)
            {
                ulong instanceRva = instanceAddr - module.Base;
                Log.Debug($"found \"{name}\" at {instanceAddr:X} ({module.Name} + {instanceRva:X})");
                result[name] = instanceRva;
            }

            regPtr = next;
        }

        return result;
    }
}
