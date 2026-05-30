using Cs2Dumper.Logging;
using Cs2Dumper.Memory;
using Cs2Dumper.Pe;

namespace Cs2Dumper.Analysis;

/// <summary>
/// Port of <c>analysis/offsets.rs</c>: signature-scans a fixed set of modules for named offsets.
/// The two pattern callbacks (dwViewAngles, dwLocalPlayerPawn) re-scan relative to a base RVA,
/// exactly as the reference's <c>pattern_map!</c> macro does.
/// </summary>
internal static class Offsets
{
    private delegate void Callback(Scanner scanner, SortedDictionary<string, uint> map, uint rva);

    private sealed class Compiled
    {
        public required string Name { get; init; }
        public required Atom[] Atoms { get; init; }
        public required int SaveLen { get; init; }
        public Callback? Callback { get; init; }
    }

    private static Compiled Make(string name, string pattern, Callback? callback = null)
    {
        Atom[] atoms = PatternParser.Parse(pattern);
        return new Compiled
        {
            Name = name,
            Atoms = atoms,
            SaveLen = Math.Max(PatternParser.SaveLen(atoms), 2),
            Callback = callback,
        };
    }

    private static readonly Atom[] ViewAnglesPattern = PatternParser.Parse("f2420f108428u4");
    private static readonly Atom[] LocalPlayerPawnPattern = PatternParser.Parse("4c39b6u4 74? 4488be");

    private static readonly Compiled[] Client =
    {
        Make("dwCSGOInput", "488905${'} 0f57c0 0f1105", static (scanner, map, rva) =>
        {
            var save = new uint[2];
            if (scanner.FindsCode(ViewAnglesPattern, save))
            {
                map["dwViewAngles"] = unchecked(rva + save[1]);
            }
        }),
        Make("dwEntityList", "48890d${'} e9${} cc"),
        Make("dwGameEntitySystem", "488b1d${'} 48891d[4] 4c63b3"),
        Make("dwGameEntitySystem_highestEntityIndex", "ff81u4 4885d2"),
        Make("dwGameRules", "f6c1010f85${} 4c8b05${'} 4d85"),
        Make("dwGlobalVars", "488915${'} 488942"),
        Make("dwGlowManager", "488b05${'} c3 cccccccccccccccc 8b41"),
        Make("dwLocalPlayerController", "488b05${'} 4189be"),
        Make("dwPlantedC4", "488b15${'} 41ffc0 488d4c24? 448905[4]"),
        Make("dwPrediction", "488d05${'} c3 cccccccccccccccc 405356 4154", static (scanner, map, rva) =>
        {
            var save = new uint[2];
            if (scanner.FindsCode(LocalPlayerPawnPattern, save))
            {
                map["dwLocalPlayerPawn"] = unchecked(rva + save[1]);
            }
        }),
        Make("dwSensitivity", "488d0d${[8]'} 660f6ecd"),
        Make("dwSensitivity_sensitivity", "488d7eu1 480fbae0? 72? 85d2 490f4fff"),
        Make("dwViewMatrix", "488d0d${'} 48c1e006"),
        Make("dwViewRender", "488905${'} 488bc8 4885c0"),
        Make("dwWeaponC4", "488b15${'} 488b5c24? ffc0 8905${} 488bc6 488934ea 80be"),
    };

    private static readonly Compiled[] Engine2 =
    {
        Make("dwBuildNumber", "8905${'} 488d0d${} ff15${} 488b0d"),
        Make("dwNetworkGameClient", "48893d${'} ff87"),
        Make("dwNetworkGameClient_clientTickCount", "8b81u4 c3 cccccccccccccccccc 8b81${} c3 cccccccccccccccccc 83b9"),
        Make("dwNetworkGameClient_deltaTick", "4c8db7u4 4c897c24"),
        Make("dwNetworkGameClient_isBackgroundMap", "0fb681u4 c3 cccccccccccccccc 0fb681${} c3 cccccccccccccccc 4053"),
        Make("dwNetworkGameClient_localPlayer", "428b94d3u4 5b 49ffe3 32c0 5b c3 cccccccccccccccc 4053"),
        Make("dwNetworkGameClient_maxClients", "8b81u4 c3????????? 8b81[4] c3????????? 8b81"),
        Make("dwNetworkGameClient_serverTickCount", "8b81u4 c3 cccccccccccccccccc 83b9"),
        Make("dwNetworkGameClient_signOnState", "448b81u4 488d0d"),
        Make("dwWindowHeight", "8b05${'} 8903"),
        Make("dwWindowWidth", "8b05${'} 8907"),
    };

    private static readonly Compiled[] InputSystem =
    {
        Make("dwInputSystem", "488905${'} 33c0"),
    };

    private static readonly Compiled[] Matchmaking =
    {
        Make("dwGameTypes", "488d0d${'} ff90"),
    };

    private static readonly Compiled[] SoundSystem =
    {
        Make("dwSoundSystem", "488d05${'} c3 cccccccccccccccc 488915"),
        Make("dwSoundSystem_engineViewData", "0f1147u1 0f104e? 0f118f"),
    };

    public static SortedDictionary<string, SortedDictionary<string, uint>> Analyze(GameProcess process)
    {
        var map = AnalysisResult.NewOffsetMap();

        (string Module, Compiled[] Patterns)[] modules =
        {
            ("client.dll", Client),
            ("engine2.dll", Engine2),
            ("inputsystem.dll", InputSystem),
            ("matchmaking.dll", Matchmaking),
            ("soundsystem.dll", SoundSystem),
        };

        foreach ((string moduleName, Compiled[] patterns) in modules)
        {
            ModuleInfo module = process.ModuleByName(moduleName);
            byte[] buf = process.ReadImage(module);
            var view = PeImage.FromBytes(buf);
            var scanner = new Scanner(view);

            map[moduleName] = ScanModule(scanner, patterns, moduleName, view.ImageBase);
        }

        return map;
    }

    private static SortedDictionary<string, uint> ScanModule(Scanner scanner, Compiled[] patterns, string moduleName, ulong imageBase)
    {
        var map = AnalysisResult.NewOffsetEntries();

        foreach (Compiled pattern in patterns)
        {
            var save = new uint[pattern.SaveLen];
            if (!scanner.FindsCode(pattern.Atoms, save))
            {
                Log.Error($"outdated pattern: {pattern.Name}");
                continue;
            }

            uint rva = save[1];
            map[pattern.Name] = rva;
            pattern.Callback?.Invoke(scanner, map, rva);
        }

        foreach (KeyValuePair<string, uint> entry in map)
        {
            Log.Debug($"found \"{entry.Key}\" at {imageBase + entry.Value:X} ({moduleName} + {entry.Value:X})");
        }

        return map;
    }
}
