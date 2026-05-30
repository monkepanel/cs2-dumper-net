namespace Cs2Dumper.Memory;

/// <summary>RIP-relative reference resolution (port of <c>memory/address.rs</c>).</summary>
public static class Address
{
    public static ulong FollowCall(GameProcess mem, ulong baseAddr) => Rel32Target(mem, baseAddr, 0x1);

    public static ulong FollowJmp(GameProcess mem, ulong baseAddr) => Rel32Target(mem, baseAddr, 0x1);

    public static ulong ResolveRip(GameProcess mem, ulong baseAddr) => Rel32Target(mem, baseAddr, 0x3);

    private static ulong Rel32Target(GameProcess mem, ulong baseAddr, int offset)
    {
        int rel32 = mem.ReadI32(baseAddr + (ulong)offset); // RIP-relative displacement.
        long instrEnd = unchecked((long)(baseAddr + (ulong)offset + sizeof(int)));
        long target = unchecked(instrEnd + rel32);
        return unchecked((ulong)target);
    }
}
