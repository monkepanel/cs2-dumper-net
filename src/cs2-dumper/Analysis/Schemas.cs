using Cs2Dumper.Logging;
using Cs2Dumper.Memory;
using Cs2Dumper.Pe;
using Cs2Dumper.Source2;

namespace Cs2Dumper.Analysis;

/// <summary>
/// Port of <c>analysis/schemas.rs</c>: finds the SchemaSystem, walks each type scope's
/// class/enum <c>UtlTsHash</c> tables and decodes their bindings, fields, metadata and enumerators.
/// </summary>
internal static class Schemas
{
    // pattern!("4c8d35${'} 0f2845")
    private static readonly Atom[] SchemaSystemPattern = PatternParser.Parse("4c8d35${'} 0f2845");

    // A guard far above any real schema size, purely to avoid hanging on corrupt linked lists.
    private const int TraversalCap = 1 << 21;

    public static SortedDictionary<string, SchemaModule> Analyze(GameProcess process)
    {
        byte[] schemaSystem = ReadSchemaSystem(process);

        var result = AnalysisResult.NewSchemaMap();
        foreach (TypeScope scope in ReadTypeScopes(process, schemaSystem))
        {
            result[scope.ModuleName] = new SchemaModule(scope.Classes, scope.Enums);
        }

        return result;
    }

    private static byte[] ReadSchemaSystem(GameProcess process)
    {
        ModuleInfo module = process.ModuleByName("schemasystem.dll");
        byte[] buf = process.ReadImage(module);
        var view = PeImage.FromBytes(buf);

        var save = new uint[2];
        if (!new Scanner(view).FindsCode(SchemaSystemPattern, save))
        {
            throw new InvalidOperationException("outdated schema system pattern");
        }

        var schemaSystem = new byte[Layout.SchemaSystemSize];
        if (process.ReadPartial(module.Base + save[1], schemaSystem) <= 0)
        {
            throw new MemoryReadException(module.Base + save[1], Layout.SchemaSystemSize);
        }

        if (Buf.I32(schemaSystem, Layout.SchemaSystemRegistrationCount) == 0)
        {
            throw new InvalidOperationException("no schema registrations");
        }

        return schemaSystem;
    }

    private sealed record TypeScope(string ModuleName, List<SchemaClass> Classes, List<SchemaEnum> Enums);

    private static List<TypeScope> ReadTypeScopes(GameProcess mem, byte[] schemaSystem)
    {
        int count = Buf.I32(schemaSystem, Layout.SchemaSystemTypeScopes + Layout.UtlVectorCount);
        ulong data = Buf.U64(schemaSystem, Layout.SchemaSystemTypeScopes + Layout.UtlVectorData);

        var result = new List<TypeScope>();
        var scopeBuf = new byte[Layout.TypeScopeSize];

        for (int i = 0; i < count; i++)
        {
            ulong scopePtr = mem.ReadU64(data + (ulong)i * 8);
            if (mem.ReadPartial(scopePtr, scopeBuf) <= 0)
            {
                throw new MemoryReadException(scopePtr, Layout.TypeScopeSize);
            }

            string moduleName = GameProcess.CStringFromBuffer(
                scopeBuf.AsSpan(Layout.TypeScopeName, Layout.TypeScopeNameLen));

            var classes = new List<SchemaClass>();
            foreach (ulong bindingPtr in TsHashElements(mem, scopeBuf, Layout.TypeScopeClassBindings))
            {
                if (TryReadClassBinding(mem, bindingPtr, out SchemaClass? cls))
                {
                    classes.Add(cls);
                }
            }

            var enums = new List<SchemaEnum>();
            foreach (ulong bindingPtr in TsHashElements(mem, scopeBuf, Layout.TypeScopeEnumBindings))
            {
                if (TryReadEnumBinding(mem, bindingPtr, out SchemaEnum? en))
                {
                    enums.Add(en);
                }
            }

            if (classes.Count == 0 && enums.Count == 0)
            {
                continue;
            }

            Log.Debug($"module \"{moduleName}\" contains {classes.Count} class(es) and {enums.Count} enum(s)");
            result.Add(new TypeScope(moduleName, classes, enums));
        }

        return result;
    }

    private static List<ulong> TsHashElements(GameProcess mem, byte[] scopeBuf, int hashOffset)
    {
        int usedCount = Buf.I32(scopeBuf, hashOffset + Layout.TsHashBlocksAllocated);
        int freeCount = Buf.I32(scopeBuf, hashOffset + Layout.TsHashPeakAllocated);
        ulong freeHeadNext = Buf.U64(scopeBuf, hashOffset + Layout.TsHashFreeBlocksHeadNext);

        var elements = new List<ulong>();
        AppendAllocated(mem, scopeBuf, hashOffset, usedCount, elements);
        AppendUnallocated(mem, freeHeadNext, freeCount, elements);

        // Drop duplicate pointers that appear in both the allocated and free lists (first wins).
        var seen = new HashSet<ulong>(elements.Count);
        var deduped = new List<ulong>(elements.Count);
        foreach (ulong ptr in elements)
        {
            if (seen.Add(ptr))
            {
                deduped.Add(ptr);
            }
        }

        return deduped;
    }

    private static void AppendAllocated(GameProcess mem, byte[] scopeBuf, int hashOffset, int usedCount, List<ulong> elements)
    {
        Span<byte> node = stackalloc byte[Layout.TsHashFixedDataSize];
        int guard = 0;

        for (int bucket = 0; bucket < Layout.TsHashBucketCount; bucket++)
        {
            int bucketOffset = hashOffset + Layout.TsHashBuckets + bucket * Layout.TsHashBucketStride;
            ulong nodePtr = Buf.U64(scopeBuf, bucketOffset + Layout.TsHashBucketFirstUncommitted);

            while (nodePtr != 0)
            {
                if (++guard > TraversalCap || !mem.TryReadExact(nodePtr, node))
                {
                    break;
                }

                ulong dataPtr = Buf.U64(node, Layout.TsHashFixedDataData);
                if (dataPtr != 0)
                {
                    elements.Add(dataPtr);
                }

                // The reference casts the count to usize, so a negative (garbage) count is "unbounded".
                if (usedCount >= 0 && elements.Count >= usedCount)
                {
                    break;
                }

                nodePtr = Buf.U64(node, Layout.TsHashFixedDataNext);
            }
        }
    }

    private static void AppendUnallocated(GameProcess mem, ulong blobPtr, int freeCount, List<ulong> elements)
    {
        Span<byte> blob = stackalloc byte[Layout.TsHashBlobSize];
        int start = elements.Count;
        int guard = 0;

        while (blobPtr != 0)
        {
            if (++guard > TraversalCap || !mem.TryReadExact(blobPtr, blob))
            {
                break;
            }

            ulong dataPtr = Buf.U64(blob, Layout.TsHashBlobData);
            if (dataPtr != 0)
            {
                elements.Add(dataPtr);
            }

            if (freeCount >= 0 && elements.Count - start >= freeCount)
            {
                break;
            }

            blobPtr = Buf.U64(blob, Layout.TsHashBlobNext);
        }
    }

    private static bool TryReadClassBinding(GameProcess mem, ulong bindingPtr, out SchemaClass cls)
    {
        cls = null!;
        try
        {
            var b = new byte[Layout.ClassSize];
            if (mem.ReadPartial(bindingPtr, b) <= 0)
            {
                return false;
            }

            string moduleName = mem.ReadUtf8Lossy(Buf.U64(b, Layout.ClassModuleName), 128) + ".dll";
            string name = mem.ReadUtf8Lossy(Buf.U64(b, Layout.ClassName), 128);
            if (name.Length == 0)
            {
                return false; // bail!("invalid class name")
            }

            string? parentName = ReadParentName(mem, Buf.U64(b, Layout.ClassBaseClasses));
            List<SchemaField> fields = ReadFields(mem, b);
            List<ClassMetadata> metadata = ReadMetadata(mem, b);

            cls = new SchemaClass
            {
                Name = name,
                ModuleName = moduleName,
                ParentName = parentName,
                Metadata = metadata,
                Fields = fields,
            };
            return true;
        }
        catch (MemoryReadException)
        {
            return false; // read_class_binding(..).ok() drops the class on any read error
        }
    }

    private static string? ReadParentName(GameProcess mem, ulong baseClasses)
    {
        if (baseClasses == 0)
        {
            return null;
        }

        try
        {
            ulong classPtr = mem.ReadU64(baseClasses + Layout.BaseClassInfoClass);
            ulong namePtr = mem.ReadU64(classPtr + Layout.BaseClassName);
            string parentName = mem.ReadUtf8Lossy(namePtr, 128);
            return parentName.Length > 0 ? parentName : null;
        }
        catch (MemoryReadException)
        {
            return null; // parent stays None on failure, the class is kept
        }
    }

    private static List<SchemaField> ReadFields(GameProcess mem, byte[] binding)
    {
        var fields = new List<SchemaField>();

        ulong fieldsPtr = Buf.U64(binding, Layout.ClassFields);
        if (fieldsPtr == 0)
        {
            return fields;
        }

        short fieldCount = Buf.I16(binding, Layout.ClassFieldCount);
        Span<byte> field = stackalloc byte[Layout.FieldReadSize];

        for (int i = 0; i < fieldCount; i++)
        {
            ulong fieldAddr = fieldsPtr + (ulong)i * Layout.FieldStride;
            if (!mem.TryReadExact(fieldAddr, field))
            {
                throw new MemoryReadException(fieldAddr, Layout.FieldReadSize);
            }

            ulong typePtr = Buf.U64(field, Layout.FieldType);
            if (typePtr == 0)
            {
                continue; // skip fields with a null type
            }

            string name = mem.ReadUtf8Lossy(Buf.U64(field, Layout.FieldName), 128);
            ulong typeNamePtr = mem.ReadU64(typePtr + Layout.TypeName);
            string typeName = mem.ReadUtf8Lossy(typeNamePtr, 128).Replace(" ", "");
            int offset = Buf.I32(field, Layout.FieldOffset);

            fields.Add(new SchemaField(name, typeName, offset));
        }

        return fields;
    }

    private static List<ClassMetadata> ReadMetadata(GameProcess mem, byte[] binding)
    {
        var metadata = new List<ClassMetadata>();

        ulong metadataPtr = Buf.U64(binding, Layout.ClassStaticMetadata);
        if (metadataPtr == 0)
        {
            return metadata;
        }

        short metadataCount = Buf.I16(binding, Layout.ClassStaticMetadataCount);
        Span<byte> entry = stackalloc byte[Layout.MetaStride];
        Span<byte> networkValue = stackalloc byte[Layout.NetworkValueReadSize];

        for (int i = 0; i < metadataCount; i++)
        {
            ulong entryAddr = metadataPtr + (ulong)i * Layout.MetaStride;
            if (!mem.TryReadExact(entryAddr, entry))
            {
                throw new MemoryReadException(entryAddr, Layout.MetaStride);
            }

            ulong networkValuePtr = Buf.U64(entry, Layout.MetaNetworkValue);
            if (networkValuePtr == 0)
            {
                continue; // skip entries without a network value
            }

            string name = mem.ReadUtf8Lossy(Buf.U64(entry, Layout.MetaName), 128);
            if (!mem.TryReadExact(networkValuePtr, networkValue))
            {
                throw new MemoryReadException(networkValuePtr, Layout.NetworkValueReadSize);
            }

            switch (name)
            {
                case "MNetworkChangeCallback":
                {
                    string cb = mem.ReadUtf8Lossy(Buf.U64(networkValue, Layout.NetworkValueNamePtr), 128);
                    metadata.Add(new NetworkChangeCallbackMetadata(cb));
                    break;
                }
                case "MNetworkVarNames":
                {
                    string varName = mem.ReadUtf8Lossy(Buf.U64(networkValue, Layout.NetworkValueNamePtr), 128);
                    string typeName = mem.ReadUtf8Lossy(Buf.U64(networkValue, Layout.NetworkValueVarTypeName), 128).Replace(" ", "");
                    metadata.Add(new NetworkVarNamesMetadata(varName, typeName));
                    break;
                }
                default:
                    metadata.Add(new UnknownMetadata(name));
                    break;
            }
        }

        return metadata;
    }

    private static bool TryReadEnumBinding(GameProcess mem, ulong bindingPtr, out SchemaEnum en)
    {
        en = null!;
        try
        {
            var b = new byte[Layout.EnumSize];
            if (mem.ReadPartial(bindingPtr, b) <= 0)
            {
                return false;
            }

            string name = mem.ReadUtf8Lossy(Buf.U64(b, Layout.EnumName), 128);
            if (name.Length == 0)
            {
                return false; // bail!("invalid enum name")
            }

            byte alignment = Buf.U8(b, Layout.EnumAlignment);
            ushort enumeratorCount = Buf.U16(b, Layout.EnumEnumeratorCount);
            List<SchemaEnumMember> members = ReadEnumMembers(mem, b, enumeratorCount);

            en = new SchemaEnum
            {
                Name = name,
                Alignment = alignment,
                Size = enumeratorCount,
                Members = members,
            };
            return true;
        }
        catch (MemoryReadException)
        {
            return false;
        }
    }

    private static List<SchemaEnumMember> ReadEnumMembers(GameProcess mem, byte[] binding, ushort enumeratorCount)
    {
        var members = new List<SchemaEnumMember>();

        ulong enumeratorsPtr = Buf.U64(binding, Layout.EnumEnumerators);
        if (enumeratorsPtr == 0)
        {
            return members;
        }

        Span<byte> enumerator = stackalloc byte[Layout.EnumeratorReadSize];

        for (int i = 0; i < enumeratorCount; i++)
        {
            ulong enumAddr = enumeratorsPtr + (ulong)i * Layout.EnumeratorStride;
            if (!mem.TryReadExact(enumAddr, enumerator))
            {
                throw new MemoryReadException(enumAddr, Layout.EnumeratorReadSize);
            }

            string name = mem.ReadUtf8Lossy(Buf.U64(enumerator, Layout.EnumeratorName), 128);
            long value = unchecked((long)Buf.U64(enumerator, Layout.EnumeratorValue));
            members.Add(new SchemaEnumMember(name, value));
        }

        return members;
    }
}
