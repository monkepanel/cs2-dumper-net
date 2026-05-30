namespace Cs2Dumper.Analysis;

// Sorted-map aliases mirror the reference's BTreeMaps. Ordinal ordering matches Rust's
// UTF-8 byte ordering for the ASCII identifier data the schema system contains.
using ButtonMap = SortedDictionary<string, ulong>;
using InterfaceMap = SortedDictionary<string, SortedDictionary<string, ulong>>;
using OffsetMap = SortedDictionary<string, SortedDictionary<string, uint>>;
using SchemaMap = SortedDictionary<string, SchemaModule>;

/// <summary>Per-module schema payload: a list of classes and a list of enums, in memory order.</summary>
public sealed record SchemaModule(List<SchemaClass> Classes, List<SchemaEnum> Enums);

public abstract record ClassMetadata;

public sealed record UnknownMetadata(string Name) : ClassMetadata;

public sealed record NetworkChangeCallbackMetadata(string Name) : ClassMetadata;

public sealed record NetworkVarNamesMetadata(string Name, string TypeName) : ClassMetadata;

public sealed class SchemaClass
{
    public required string Name { get; init; }
    public required string ModuleName { get; init; }
    public string? ParentName { get; init; }
    public required List<ClassMetadata> Metadata { get; init; }
    public required List<SchemaField> Fields { get; init; }
}

public sealed record SchemaField(string Name, string TypeName, int Offset);

public sealed class SchemaEnum
{
    public required string Name { get; init; }
    public required byte Alignment { get; init; }
    public required ushort Size { get; init; } // member count, kept under the reference's field name
    public required List<SchemaEnumMember> Members { get; init; }
}

public sealed record SchemaEnumMember(string Name, long Value);

/// <summary>Aggregate of everything the four analysis passes produce (mirrors <c>AnalysisResult</c>).</summary>
public sealed class AnalysisResult
{
    public required ButtonMap Buttons { get; init; }
    public required InterfaceMap Interfaces { get; init; }
    public required OffsetMap Offsets { get; init; }
    public required SchemaMap Schemas { get; init; }

    public static ButtonMap NewButtonMap() => new(StringComparer.Ordinal);

    public static InterfaceMap NewInterfaceMap() => new(StringComparer.Ordinal);

    public static SortedDictionary<string, ulong> NewInterfaceEntries() => new(StringComparer.Ordinal);

    public static OffsetMap NewOffsetMap() => new(StringComparer.Ordinal);

    public static SortedDictionary<string, uint> NewOffsetEntries() => new(StringComparer.Ordinal);

    public static SchemaMap NewSchemaMap() => new(StringComparer.Ordinal);
}
