using System.Text;
using System.Text.Json;
using Cs2Dumper.Analysis;
using Cs2Dumper.Output;
using Cs2Dumper.Pe;

namespace Cs2Dumper;

/// <summary>
/// Offline verification harness. It reconstructs the analysis model from the reference repo's
/// committed <c>output/*.json</c> (ground truth produced by the original Rust tool) and checks
/// that this port's writers reproduce the committed files byte-for-byte, plus unit checks of the
/// pattern engine against pelite's own test vectors and the casing/JSON helpers.
/// </summary>
internal static class VerifyMain
{
    private static int _passed;
    private static int _failed;
    private static readonly Timestamp Ts = Timestamp.Now();

    private static int Main(string[] argv)
    {
        string refDir = Path.GetFullPath(argv.Length > 0
            ? argv[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".reference", "output"));

        Console.WriteLine($"cs2-dumper verification\nreference: {refDir}\n");

        PatternParserTests();
        PatternExecTests();
        HelperTests();

        if (Directory.Exists(refDir))
        {
            ButtonsTest(refDir);
            OffsetsTest(refDir);
            InterfacesTest(refDir);
            JsonRoundTripTest(refDir);
        }
        else
        {
            Console.WriteLine($"(skipping reference-file checks; {refDir} not found)");
        }

        SchemaCodeTest();

        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ---- pattern parser vs pelite's parse() unit tests ----------------------------------------

    private static void PatternParserTests()
    {
        Section("pattern parser (pelite parse() vectors)");

        Check("12 34 56 ? ?", "Save0 12 34 56");
        Check("B9'?? 68???? E8${'} 8B", "Save0 B9 Save1 Skip2 68 Skip4 E8 Push4 Jump4 Save2 Pop 8B");
        Check("${%{${%{}}}}", "Save0 Push4 Jump4 Push1 Jump1 Push4 Jump4 Push1 Jump1");
        Check("24 5A9e D0 AFBea3 fCdd", "Save0 24 5A 9E D0 AF BE A3 FC DD");
        Check("\"string\"", "Save0 73 74 72 69 6E 67");
        Check("*{FF D8 42}", "Save0 Push0 Ptr FF D8 42");
        Check("b8 [16] 50 [13-42] ff", "Save0 B8 Skip16 50 Skip13 Many29 FF");
        Check("e9 $ @4", "Save0 E9 Jump4 Aligned4");
        Check("83 c0 2a ( 6a ? | 68 ? ? ? ? ) e8", "Save0 83 C0 2A Case3 6A Skip1 Break3 Nop 68 Skip4 E8");
        return;

        static void Check(string pattern, string expected)
        {
            string actual = Describe(PatternParser.Parse(pattern));
            Assert($"parse(\"{pattern}\")", actual == expected, expected, actual);
        }
    }

    private static string Describe(Atom[] atoms)
    {
        var parts = new List<string>(atoms.Length);
        foreach (Atom a in atoms)
        {
            parts.Add(a.Kind switch
            {
                AtomKind.Byte => a.Arg.ToString("X2"),
                AtomKind.Save => "Save" + a.Arg,
                AtomKind.Push => "Push" + a.Arg,
                AtomKind.Skip => "Skip" + a.Arg,
                AtomKind.Many => "Many" + a.Arg,
                AtomKind.Aligned => "Aligned" + a.Arg,
                AtomKind.Case => "Case" + a.Arg,
                AtomKind.Break => "Break" + a.Arg,
                _ => a.Kind.ToString(),
            });
        }

        return string.Join(' ', parts);
    }

    // ---- pattern interpreter vs pelite's exec() unit tests ------------------------------------

    private static void PatternExecTests()
    {
        Section("pattern interpreter (pelite exec() vectors)");

        ExecOk("55 89 e5 83 ? ec", new byte[] { 0x55, 0x89, 0xe5, 0x83, 0xff, 0xec }, 0);

        ExecSave("b9 '37 13 00 00", new byte[] { 0xb9, 0x37, 0x13, 0x00, 0x00 }, 2, 1, 1);

        var buf = new byte[64];
        buf[0] = 0xb8;
        buf[17] = 0x50;
        buf[41] = 0xff;
        ExecSave("b8 [16] 50 [13-42] 'ff", buf, 2, 1, 41);

        ExecSave("31 c0 74 % 'c0", new byte[] { 0x31, 0xc0, 0x74, unchecked((byte)(sbyte)-3) }, 2, 1, 1);

        ExecSave("e8 $ '31 c0 c3",
            new byte[] { 0xe8, 10, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 0x31, 0xc0, 0xc3 }, 2, 1, 15);

        ExecSave("68 * '31 c0 c3",
            new byte[] { 0x68, 10, 0, 0, 0, 1, 2, 3, 4, 5, 0x31, 0xc0, 0xc3 }, 2, 1, 10);

        ExecSave("e8 $ { ' } 83 f0 5c c3",
            new byte[] { 0xe8, 10, 0, 0, 0, 0x83, 0xf0, 0x5c, 0xc3, 5, 6, 7, 8, 9, 10 }, 2, 1, 15);

        // i1 then u4 capture
        var save = new uint[3];
        bool ok = Scanner.ExecForTest(new byte[] { 0xe8, 0xff, 0xa0, 0x78, 0x56, 0x34, 0x12 },
            PatternParser.Parse("e8 i1 a0 u4"), 0, save);
        Assert("exec(\"e8 i1 a0 u4\")", ok && save[1] == unchecked((uint)(sbyte)-1) && save[2] == 0x12345678,
            "ok save1=FFFFFFFF save2=12345678", $"ok={ok} save1={save[1]:X} save2={save[2]:X}");

        ExecOk("83 c0 2a ( 6a ? | 68 ? ? ? ? ) e8", new byte[] { 0x83, 0xc0, 0x2a, 0x6a, 0x00, 0xe8 }, 0);
        ExecOk("83 c0 2a ( 6a ? | 68 ? ? ? ? ) e8", new byte[] { 0x83, 0xc0, 0x2a, 0x68, 0, 0, 0, 0x10, 0xe8 }, 0);
        return;

        static void ExecOk(string pattern, byte[] image, uint cursor)
        {
            bool ok = Scanner.ExecForTest(image, PatternParser.Parse(pattern), cursor, Array.Empty<uint>());
            Assert($"exec(\"{pattern}\")", ok, "matches", ok ? "matches" : "no match");
        }

        static void ExecSave(string pattern, byte[] image, int slots, int slot, uint expected)
        {
            var save = new uint[slots];
            bool ok = Scanner.ExecForTest(image, PatternParser.Parse(pattern), 0, save);
            Assert($"exec(\"{pattern}\")", ok && save[slot] == expected,
                $"save[{slot}]={expected:X}", ok ? $"save[{slot}]={save[slot]:X}" : "no match");
        }
    }

    // ---- helper unit tests --------------------------------------------------------------------

    private static void HelperTests()
    {
        Section("helpers (heck / slugify / zig / hex)");

        AssertEq("PascalCase client_dll", Heck.PascalCase("client_dll"), "ClientDll");
        AssertEq("PascalCase engine2_dll", Heck.PascalCase("engine2_dll"), "Engine2Dll");
        AssertEq("PascalCase v8system_dll", Heck.PascalCase("v8system_dll"), "V8systemDll");
        AssertEq("PascalCase rendersystemdx11_dll", Heck.PascalCase("rendersystemdx11_dll"), "Rendersystemdx11Dll");
        AssertEq("PascalCase panorama_text_pango_dll", Heck.PascalCase("panorama_text_pango_dll"), "PanoramaTextPangoDll");
        AssertEq("SnakeCase client_dll", Heck.SnakeCase("client_dll"), "client_dll");
        AssertEq("SnakeCase v8system_dll", Heck.SnakeCase("v8system_dll"), "v8system_dll");

        AssertEq("Slugify client.dll", Text.Slugify("client.dll"), "client_dll");
        AssertEq("Slugify C_X__Y_t", Text.Slugify("C_SceneEntity__QueuedEvents_t"), "C_SceneEntity__QueuedEvents_t");
        AssertEq("Slugify CHandle<C_PlayerPing>", Text.Slugify("CHandle<C_PlayerPing>"), "CHandle_C_PlayerPing_");

        AssertEq("ZigIdent use", Text.ZigIdent("use"), "use");
        AssertEq("ZigIdent error", Text.ZigIdent("error"), "@\"error\"");
        AssertEq("ZigIdent 123abc", Text.ZigIdent("123abc"), "@\"123abc\"");

        AssertEq("HexU64 0", Text.HexU64(0), "0x0");
        AssertEq("HexU32 0x2354230", Text.HexU32(0x2354230), "0x2354230");
        AssertEq("HexI32 0x58", Text.HexI32(0x58), "0x58");
        AssertEq("HexI64 -1", Text.HexI64(-1), "0xFFFFFFFFFFFFFFFF");
        AssertEq("HexI32 -1", Text.HexI32(-1), "0xFFFFFFFF");
    }

    // ---- reference output byte-for-byte checks ------------------------------------------------

    private static void ButtonsTest(string refDir)
    {
        Section("buttons (reconstructed from buttons.json)");

        using JsonDocument doc = ParseFile(refDir, "buttons.json");
        var map = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
        foreach (JsonProperty p in doc.RootElement.GetProperty("client.dll").EnumerateObject())
        {
            map[p.Name] = p.Value.GetUInt64();
        }

        foreach (string fileType in FileTypes)
        {
            string generated = GenerateItem(fileType, fmt => ButtonsWriter.Write(fmt, fileType, map));
            CompareToFile(refDir, $"buttons.{fileType}", fileType, generated);
        }
    }

    private static void OffsetsTest(string refDir)
    {
        Section("offsets (reconstructed from offsets.json)");

        using JsonDocument doc = ParseFile(refDir, "offsets.json");
        var map = new SortedDictionary<string, SortedDictionary<string, uint>>(StringComparer.Ordinal);
        foreach (JsonProperty module in doc.RootElement.EnumerateObject())
        {
            var entries = new SortedDictionary<string, uint>(StringComparer.Ordinal);
            foreach (JsonProperty entry in module.Value.EnumerateObject())
            {
                entries[entry.Name] = entry.Value.GetUInt32();
            }

            map[module.Name] = entries;
        }

        foreach (string fileType in FileTypes)
        {
            string generated = GenerateItem(fileType, fmt => OffsetsWriter.Write(fmt, fileType, map));
            CompareToFile(refDir, $"offsets.{fileType}", fileType, generated);
        }
    }

    private static void InterfacesTest(string refDir)
    {
        Section("interfaces (reconstructed from interfaces.json)");

        using JsonDocument doc = ParseFile(refDir, "interfaces.json");
        var map = new SortedDictionary<string, SortedDictionary<string, ulong>>(StringComparer.Ordinal);
        foreach (JsonProperty module in doc.RootElement.EnumerateObject())
        {
            var entries = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
            foreach (JsonProperty entry in module.Value.EnumerateObject())
            {
                entries[entry.Name] = entry.Value.GetUInt64();
            }

            map[module.Name] = entries;
        }

        foreach (string fileType in FileTypes)
        {
            string generated = GenerateItem(fileType, fmt => InterfacesWriter.Write(fmt, fileType, map));
            CompareToFile(refDir, $"interfaces.{fileType}", fileType, generated);
        }
    }

    private static void JsonRoundTripTest(string refDir)
    {
        Section("json round-trip (serde_json compatibility)");

        foreach (string path in Directory.GetFiles(refDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            string committed = File.ReadAllText(path).Replace("\r\n", "\n");
            using JsonDocument doc = JsonDocument.Parse(committed);
            string reserialized = Convert(doc.RootElement).ToPrettyString();
            Assert($"roundtrip {Path.GetFileName(path)}", reserialized == committed,
                "byte-identical", FirstDiff(reserialized, committed));
        }
    }

    private static Json Convert(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => ConvObject(e),
        JsonValueKind.Array => ConvArray(e),
        JsonValueKind.String => Json.Str(e.GetString()!),
        JsonValueKind.Number => Json.Raw(e.GetRawText()),
        JsonValueKind.True => Json.Raw("true"),
        JsonValueKind.False => Json.Raw("false"),
        _ => Json.Null,
    };

    private static Json ConvObject(JsonElement e)
    {
        var o = Json.Obj();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            o.Add(p.Name, Convert(p.Value));
        }

        return o;
    }

    private static Json ConvArray(JsonElement e)
    {
        var a = Json.Arr();
        foreach (JsonElement item in e.EnumerateArray())
        {
            a.Add(Convert(item));
        }

        return a;
    }

    // ---- schema writer focused checks ---------------------------------------------------------

    private static void SchemaCodeTest()
    {
        Section("schema writers (targeted format checks)");

        var enums = new List<SchemaEnum>
        {
            new()
            {
                Name = "EntityIOTargetType_t",
                Alignment = 4,
                Size = 5,
                Members = new List<SchemaEnumMember>
                {
                    new("ENTITY_IO_TARGET_INVALID", -1),
                    new("ENTITY_IO_TARGET_ENTITYNAME", 2),
                    new("ENTITY_IO_TARGET_EHANDLE", 6),
                    new("ENTITY_IO_TARGET_DUP", 6), // duplicate value, exercises rs/zig dedup
                    new("ENTITY_IO_TARGET_OR_CLASSNAME", 7),
                },
            },
        };

        var classes = new List<SchemaClass>
        {
            new()
            {
                Name = "CChild",
                ModuleName = "test.dll",
                ParentName = "CParent",
                Metadata = new List<ClassMetadata>
                {
                    new NetworkVarNamesMetadata("m_x", "int32"),
                    new UnknownMetadata("MFoo"),
                },
                Fields = new List<SchemaField>
                {
                    new("m_a", "bool", 0x8),
                    new("m_b", "int32", 0x10),
                },
            },
            new()
            {
                Name = "CEmpty",
                ModuleName = "test.dll",
                ParentName = null,
                Metadata = new List<ClassMetadata>(),
                Fields = new List<SchemaField>(),
            },
        };

        var schemas = new SortedDictionary<string, SchemaModule>(StringComparer.Ordinal)
        {
            ["test.dll"] = new SchemaModule(classes, enums),
        };

        string cs = GenerateItem("cs", fmt => SchemasWriter.Write(fmt, "cs", schemas));
        string hpp = GenerateItem("hpp", fmt => SchemasWriter.Write(fmt, "hpp", schemas));
        string rs = GenerateItem("rs", fmt => SchemasWriter.Write(fmt, "rs", schemas));
        string zig = GenerateItem("zig", fmt => SchemasWriter.Write(fmt, "zig", schemas));
        string json = GenerateItem("json", fmt => SchemasWriter.Write(fmt, "json", schemas));

        // enum value formatting per language
        Want("cs enum -1", cs, "ENTITY_IO_TARGET_INVALID = unchecked((uint)-1)");
        Want("cs enum hex", cs, "ENTITY_IO_TARGET_EHANDLE = 0x6");
        Want("hpp enum -1 -> max", hpp, "ENTITY_IO_TARGET_INVALID = 0xFFFFFFFF");
        Want("rs enum -1 -> MAX", rs, "ENTITY_IO_TARGET_INVALID = u32::MAX");
        Want("zig enum -1 wrapped", zig, "ENTITY_IO_TARGET_INVALID = 0xFFFFFFFF");
        Want("zig enum repr", zig, "pub const EntityIOTargetType_t = enum(u32) {");

        // rs/zig dedup: the duplicate value (6) keeps only the first (EHANDLE), drops DUP
        Want("rs keeps first dup", rs, "ENTITY_IO_TARGET_EHANDLE = 0x6");
        DoNotWant("rs drops dup", rs, "ENTITY_IO_TARGET_DUP");
        DoNotWant("zig drops dup", zig, "ENTITY_IO_TARGET_DUP");
        // cs keeps all members (no dedup)
        Want("cs keeps dup", cs, "ENTITY_IO_TARGET_DUP = 0x6");

        // headers, metadata, parent
        Want("cs alignment comment", cs, "// Alignment: 4");
        Want("cs member count comment", cs, "// Member count: 5");
        Want("cs parent slug", cs, "// Parent: CParent");
        Want("cs parent none", cs, "// Parent: None");
        Want("cs metadata header", cs, "//\n");
        Want("cs metadata varnames", cs, "// NetworkVarNames: m_x (int32)");
        Want("cs metadata unknown", cs, "// MFoo");

        // field formatting + empty class closes immediately
        Want("cs field", cs, "public const nint m_a = 0x8; // bool");
        Want("cs empty class", cs, "public static class CEmpty {\n        }");
        Want("rs field", rs, "pub const m_b: usize = 0x10; // int32");
        Want("hpp field", hpp, "constexpr std::ptrdiff_t m_a = 0x8; // bool");

        // json: members sorted, all kept (incl dup), parent raw, enum type string
        Want("json enum type", json, "\"type\": \"uint32\"");
        Want("json member dup kept", json, "\"ENTITY_IO_TARGET_DUP\": 6");
        Want("json parent raw", json, "\"parent\": \"CParent\"");
        Want("json null parent", json, "\"parent\": null");
        Want("json field offset decimal", json, "\"m_b\": 16");
    }

    // ---- plumbing -----------------------------------------------------------------------------

    private static readonly string[] FileTypes = { "cs", "hpp", "json", "rs", "zig" };

    private static string GenerateItem(string fileType, Action<Formatter> write)
    {
        var sb = new StringBuilder();
        var fmt = new Formatter(sb, 4);
        if (fileType != "json")
        {
            fmt.WriteLine("// Generated using https://github.com/a2x/cs2-dumper");
            fmt.WriteLine($"// {Ts.Display()}\n");
        }

        write(fmt);
        return sb.ToString();
    }

    private static JsonDocument ParseFile(string dir, string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, name)));

    private static void CompareToFile(string refDir, string fileName, string fileType, string generated)
    {
        string committed = File.ReadAllText(Path.Combine(refDir, fileName)).Replace("\r\n", "\n");
        string a = fileType == "json" ? generated : NormalizeBanner(generated);
        string b = fileType == "json" ? committed : NormalizeBanner(committed);
        Assert(fileName, a == b, "byte-identical", FirstDiff(a, b));
    }

    private static string NormalizeBanner(string text)
    {
        string[] lines = text.Split('\n');
        if (lines.Length > 1)
        {
            lines[1] = "<TIMESTAMP>"; // the only time-dependent line
        }

        return string.Join('\n', lines);
    }

    private static string FirstDiff(string actual, string expected)
    {
        string[] a = actual.Split('\n');
        string[] e = expected.Split('\n');
        int n = Math.Min(a.Length, e.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != e[i])
            {
                return $"line {i + 1}:\n    expected: {Trunc(e[i])}\n    actual:   {Trunc(a[i])}";
            }
        }

        if (a.Length != e.Length)
        {
            return $"line count differs: expected {e.Length}, actual {a.Length}";
        }

        return "(no line diff; trailing whitespace?)";
    }

    private static string Trunc(string s) => s.Length > 120 ? s[..120] + "…" : s;

    private static void Want(string name, string haystack, string needle) =>
        Assert(name, haystack.Contains(needle, StringComparison.Ordinal), $"contains \"{Trunc(needle)}\"", "missing");

    private static void DoNotWant(string name, string haystack, string needle) =>
        Assert(name, !haystack.Contains(needle, StringComparison.Ordinal), $"absent \"{Trunc(needle)}\"", "present");

    private static void AssertEq(string name, string actual, string expected) =>
        Assert(name, actual == expected, expected, actual);

    private static void Section(string title) => Console.WriteLine($"== {title} ==");

    private static void Assert(string name, bool ok, string expected, string actual)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}\n        expected: {expected}\n        got:      {actual}");
        }
    }
}
