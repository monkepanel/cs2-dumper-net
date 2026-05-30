namespace Cs2Dumper.Source2;

/// <summary>
/// Field offsets / strides for every Source 2 structure the dumper walks, transcribed from
/// the reference <c>src/source2/**</c> <c>#[repr(C)]</c> definitions. All sizes are in bytes.
/// </summary>
internal static class Layout
{
    // client::input::KeyButton
    public const int KeyButtonName = 0x08;
    public const int KeyButtonState = 0x30;
    public const int KeyButtonNext = 0x88;

    // tier1::InterfaceReg
    public const int InterfaceRegCreateFn = 0x00;
    public const int InterfaceRegName = 0x08;
    public const int InterfaceRegNext = 0x10;

    // schema_system::SchemaSystem
    public const int SchemaSystemTypeScopes = 0x190; // UtlVector
    public const int SchemaSystemRegistrationCount = 0x280;
    public const int SchemaSystemSize = 0x284;

    // tier1::UtlVector
    public const int UtlVectorCount = 0x00;
    public const int UtlVectorData = 0x08;

    // schema_system::SchemaSystemTypeScope
    public const int TypeScopeName = 0x08; // [c_char; 256]
    public const int TypeScopeNameLen = 256;
    public const int TypeScopeClassBindings = 0x560; // UtlTsHash
    public const int TypeScopeEnumBindings = 0x1DD0; // UtlTsHash
    public const int TypeScopeSize = 0x3640;

    // tier1::UtlTsHash (offsets relative to the hash start)
    public const int TsHashBlocksAllocated = 0x0C; // entry_mem.blocks_allocated
    public const int TsHashPeakAllocated = 0x10;   // entry_mem.peak_allocated
    public const int TsHashFreeBlocksHeadNext = 0x20; // entry_mem.free_blocks.head.next
    public const int TsHashBuckets = 0x60;
    public const int TsHashBucketStride = 0x18;
    public const int TsHashBucketFirstUncommitted = 0x10;
    public const int TsHashBucketCount = 256;

    // tier1::UtlTsHashFixedData (allocated node)
    public const int TsHashFixedDataNext = 0x08;
    public const int TsHashFixedDataData = 0x10;
    public const int TsHashFixedDataSize = 0x18;

    // tier1::UtlTsHashAllocatedBlob (free node)
    public const int TsHashBlobNext = 0x00;
    public const int TsHashBlobData = 0x10;
    public const int TsHashBlobSize = 0x18;

    // schema_system::SchemaClassInfoData
    public const int ClassName = 0x08;
    public const int ClassModuleName = 0x18;
    public const int ClassFieldCount = 0x24; // i16
    public const int ClassStaticMetadataCount = 0x26; // i16
    public const int ClassFields = 0x30;
    public const int ClassBaseClasses = 0x40;
    public const int ClassStaticMetadata = 0x48;
    public const int ClassSize = 0x78;

    // schema_system::SchemaClassFieldData
    public const int FieldName = 0x00;
    public const int FieldType = 0x08;
    public const int FieldOffset = 0x10;
    public const int FieldStride = 0x20;
    public const int FieldReadSize = 0x18;

    // schema_system::SchemaType
    public const int TypeName = 0x08;

    // schema_system::SchemaBaseClassInfoData / SchemaBaseClass
    public const int BaseClassInfoClass = 0x18;
    public const int BaseClassName = 0x10;

    // schema_system::SchemaMetadataEntryData
    public const int MetaName = 0x00;
    public const int MetaNetworkValue = 0x08;
    public const int MetaStride = 0x10;

    // schema_system::SchemaNetworkValue union
    public const int NetworkValueNamePtr = 0x00; // value.name_ptr / var_value.name
    public const int NetworkValueVarTypeName = 0x08; // var_value.type_name
    public const int NetworkValueReadSize = 0x10;

    // schema_system::SchemaEnumInfoData
    public const int EnumName = 0x08;
    public const int EnumAlignment = 0x19;
    public const int EnumEnumeratorCount = 0x1C; // u16
    public const int EnumEnumerators = 0x20;
    public const int EnumSize = 0x48;

    // schema_system::SchemaEnumeratorInfoData
    public const int EnumeratorName = 0x00;
    public const int EnumeratorValue = 0x08; // union, read as u64
    public const int EnumeratorStride = 0x20;
    public const int EnumeratorReadSize = 0x10;
}
