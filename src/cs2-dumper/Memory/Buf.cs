using System.Buffers.Binary;

namespace Cs2Dumper.Memory;

/// <summary>Little-endian field reads out of an in-memory struct buffer.</summary>
internal static class Buf
{
    public static byte U8(ReadOnlySpan<byte> b, int off) => b[off];

    public static ushort U16(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt16LittleEndian(b[off..]);

    public static short I16(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadInt16LittleEndian(b[off..]);

    public static uint U32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b[off..]);

    public static int I32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadInt32LittleEndian(b[off..]);

    public static ulong U64(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt64LittleEndian(b[off..]);
}
