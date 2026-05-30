using System.Text;
using Cs2Dumper.Logging;
using Cs2Dumper.Memory;
using Cs2Dumper.Pe;
using Cs2Dumper.Source2;

namespace Cs2Dumper.Analysis;

/// <summary>Port of <c>analysis/buttons.rs</c>: walks the client.dll key-button linked list.</summary>
internal static class Buttons
{
    // pattern!("488b15${'} 4885d2 74? 488b02 4885c0")
    private static readonly Atom[] Pattern = PatternParser.Parse("488b15${'} 4885d2 74? 488b02 4885c0");

    public static SortedDictionary<string, ulong> Analyze(GameProcess process)
    {
        ModuleInfo module = process.ModuleByName("client.dll");
        byte[] buf = process.ReadImage(module);
        var view = PeImage.FromBytes(buf);

        var save = new uint[2];
        if (!new Scanner(view).FindsCode(Pattern, save))
        {
            throw new InvalidOperationException("outdated button list pattern");
        }

        ulong listHead = process.ReadU64(module.Base + save[1]);
        return ReadButtons(process, module, listHead);
    }

    private static SortedDictionary<string, ulong> ReadButtons(GameProcess mem, ModuleInfo module, ulong listHead)
    {
        var result = AnalysisResult.NewButtonMap();

        ulong buttonPtr = listHead;
        while (buttonPtr != 0)
        {
            ulong namePtr = mem.ReadU64(buttonPtr + Layout.KeyButtonName);
            string name = mem.ReadUtf8Lossy(namePtr, 32);

            ulong stateAddr = buttonPtr + Layout.KeyButtonState;
            if (stateAddr >= module.Base)
            {
                ulong stateRva = stateAddr - module.Base;
                Log.Debug($"found \"{name}\" at {stateAddr:X} ({module.Name} + {stateRva:X})");
                result[name] = stateRva;
            }

            buttonPtr = mem.ReadU64(buttonPtr + Layout.KeyButtonNext);
        }

        return result;
    }
}
