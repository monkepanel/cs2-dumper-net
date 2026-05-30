using System.Buffers.Binary;

namespace Cs2Dumper.Pe;

public sealed class PeException(string message) : Exception(message);

/// <summary>
/// Parses a section-aligned (in-memory) PE64 image. RVA == offset into the buffer, which
/// is exactly how memflow's <c>PeView::from_bytes</c> over a read module image behaves.
/// Provides the code range (for the scanner) and an export-by-name lookup (for interfaces).
/// </summary>
public sealed class PeImage
{
    private readonly byte[] _image;

    public byte[] Image => _image;
    public ulong ImageBase { get; }
    public uint BaseOfCode { get; }
    public uint SizeOfCode { get; }
    public uint ExportDirRva { get; }
    public uint ExportDirSize { get; }

    private PeImage(byte[] image, ulong imageBase, uint baseOfCode, uint sizeOfCode, uint exportRva, uint exportSize)
    {
        _image = image;
        ImageBase = imageBase;
        BaseOfCode = baseOfCode;
        SizeOfCode = sizeOfCode;
        ExportDirRva = exportRva;
        ExportDirSize = exportSize;
    }

    public static PeImage FromBytes(byte[] image)
    {
        if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            throw new PeException("invalid DOS header");
        }

        int ntOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
        if (ntOffset < 0 || (long)ntOffset + 0x108 > image.Length)
        {
            throw new PeException("invalid NT header offset");
        }

        if (image[ntOffset] != (byte)'P' || image[ntOffset + 1] != (byte)'E' ||
            image[ntOffset + 2] != 0 || image[ntOffset + 3] != 0)
        {
            throw new PeException("invalid PE signature");
        }

        int opt = ntOffset + 24; // PE signature (4) + IMAGE_FILE_HEADER (20)
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(opt));
        if (magic != 0x20B)
        {
            throw new PeException("not a PE32+ image");
        }

        uint sizeOfCode = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(opt + 0x04));
        uint baseOfCode = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(opt + 0x14));
        ulong imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(opt + 0x18));
        uint numberOfRvaAndSizes = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(opt + 0x6C));

        uint exportRva = 0;
        uint exportSize = 0;
        if (numberOfRvaAndSizes > 0)
        {
            exportRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(opt + 0x70));
            exportSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(opt + 0x74));
        }

        return new PeImage(image, imageBase, baseOfCode, sizeOfCode, exportRva, exportSize);
    }

    private bool InBounds(uint rva, int size) => (ulong)rva + (uint)size <= (ulong)_image.Length;

    private uint Ru32(uint rva) => BinaryPrimitives.ReadUInt32LittleEndian(_image.AsSpan((int)rva));

    private ushort Ru16(uint rva) => BinaryPrimitives.ReadUInt16LittleEndian(_image.AsSpan((int)rva));

    /// <summary>Finds a named export and returns its RVA if it is a real symbol (not a forwarder),
    /// mirroring pelite's <c>exports().by().name(..)</c> + the <c>Export::Symbol</c> match.</summary>
    public bool TryFindExportSymbol(ReadOnlySpan<byte> name, out uint rva)
    {
        rva = 0;
        if (ExportDirRva == 0 || ExportDirSize == 0 || !InBounds(ExportDirRva, 0x28))
        {
            return false;
        }

        uint numberOfNames = Ru32(ExportDirRva + 0x18);
        uint addressOfFunctions = Ru32(ExportDirRva + 0x1C);
        uint addressOfNames = Ru32(ExportDirRva + 0x20);
        uint addressOfNameOrdinals = Ru32(ExportDirRva + 0x24);

        if (numberOfNames == 0 || numberOfNames > int.MaxValue)
        {
            return false;
        }

        int lo = 0;
        int hi = (int)numberOfNames - 1;
        while (lo <= hi)
        {
            int mid = (int)((uint)(lo + hi) >> 1);
            uint nameEntryRva = addressOfNames + (uint)mid * 4;
            if (!InBounds(nameEntryRva, 4))
            {
                return false;
            }

            uint nameRva = Ru32(nameEntryRva);
            int cmp = CompareCString(nameRva, name);
            if (cmp == 0)
            {
                uint ordEntryRva = addressOfNameOrdinals + (uint)mid * 2;
                if (!InBounds(ordEntryRva, 2))
                {
                    return false;
                }

                ushort ordinal = Ru16(ordEntryRva);
                uint funcEntryRva = addressOfFunctions + (uint)ordinal * 4;
                if (!InBounds(funcEntryRva, 4))
                {
                    return false;
                }

                uint funcRva = Ru32(funcEntryRva);

                // Forwarder exports point back into the export directory; those are not symbols.
                if (funcRva >= ExportDirRva && funcRva < ExportDirRva + ExportDirSize)
                {
                    return false;
                }

                rva = funcRva;
                return true;
            }

            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return false;
    }

    private int CompareCString(uint rva, ReadOnlySpan<byte> target)
    {
        int i = 0;
        while (true)
        {
            byte a = (ulong)rva + (uint)i < (ulong)_image.Length ? _image[rva + (uint)i] : (byte)0;
            byte b = i < target.Length ? target[i] : (byte)0;
            if (a != b)
            {
                return a < b ? -1 : 1;
            }

            if (a == 0)
            {
                return 0;
            }

            i++;
        }
    }
}
