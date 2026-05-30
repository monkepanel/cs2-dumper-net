using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Cs2Dumper.Memory;

/// <summary>A loaded module: name, image base and image size (SizeOfImage).</summary>
public readonly record struct ModuleInfo(string Name, ulong Base, uint Size);

/// <summary>Raised when a required remote read fails (mirrors memflow's error propagation).</summary>
public sealed class MemoryReadException(ulong address, int size)
    : Exception($"failed to read {size} bytes at {address:X}")
{
    public ulong Address { get; } = address;
    public int Size { get; } = size;
}

/// <summary>
/// Reads memory of an external process by name. Replaces memflow + memflow-native:
/// process/module discovery via Toolhelp32, reads via ReadProcessMemory.
/// </summary>
public sealed class GameProcess : IDisposable
{
    private const uint PageSize = 0x1000;

    private readonly nint _handle;
    private readonly List<ModuleInfo> _modules;
    private readonly byte[] _scratch = new byte[512];

    public uint Pid { get; }
    public IReadOnlyList<ModuleInfo> Modules => _modules;

    private GameProcess(nint handle, uint pid, List<ModuleInfo> modules)
    {
        _handle = handle;
        Pid = pid;
        _modules = modules;
    }

    public static GameProcess OpenByName(string processName)
    {
        uint pid = FindProcessId(processName)
            ?? throw new InvalidOperationException($"process \"{processName}\" not found");

        nint handle = Win32.OpenProcess(Win32.PROCESS_VM_READ | Win32.PROCESS_QUERY_INFORMATION, false, pid);
        if (handle == 0)
        {
            throw new InvalidOperationException(
                $"OpenProcess failed for pid {pid} (error {Marshal.GetLastPInvokeError()}); try running as administrator");
        }

        List<ModuleInfo> modules = EnumerateModules(pid);
        return new GameProcess(handle, pid, modules);
    }

    private static unsafe uint? FindProcessId(string processName)
    {
        nint snapshot = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPPROCESS, 0);
        if (snapshot == Win32.INVALID_HANDLE_VALUE)
        {
            throw new InvalidOperationException($"CreateToolhelp32Snapshot failed (error {Marshal.GetLastPInvokeError()})");
        }

        try
        {
            var entry = new Win32.PROCESSENTRY32W { dwSize = (uint)sizeof(Win32.PROCESSENTRY32W) };
            if (!Win32.Process32FirstW(snapshot, ref entry))
            {
                return null;
            }

            do
            {
                string name = Win32.FixedToString(entry.szExeFile, 260);
                if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.th32ProcessID;
                }
            }
            while (Win32.Process32NextW(snapshot, ref entry));

            return null;
        }
        finally
        {
            Win32.CloseHandle(snapshot);
        }
    }

    private static unsafe List<ModuleInfo> EnumerateModules(uint pid)
    {
        // The module snapshot occasionally fails with ERROR_BAD_LENGTH while the target
        // is loading modules; retry a few times like every other tool does.
        nint snapshot = Win32.INVALID_HANDLE_VALUE;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            snapshot = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPMODULE | Win32.TH32CS_SNAPMODULE32, pid);
            if (snapshot != Win32.INVALID_HANDLE_VALUE)
            {
                break;
            }

            if (Marshal.GetLastPInvokeError() != Win32.ERROR_BAD_LENGTH)
            {
                break;
            }
        }

        if (snapshot == Win32.INVALID_HANDLE_VALUE)
        {
            throw new InvalidOperationException($"module snapshot failed (error {Marshal.GetLastPInvokeError()})");
        }

        var modules = new List<ModuleInfo>();
        try
        {
            var entry = new Win32.MODULEENTRY32W { dwSize = (uint)sizeof(Win32.MODULEENTRY32W) };
            if (!Win32.Module32FirstW(snapshot, ref entry))
            {
                return modules;
            }

            do
            {
                string name = Win32.FixedToString(entry.szModule, 256);
                modules.Add(new ModuleInfo(name, (ulong)entry.modBaseAddr, entry.modBaseSize));
            }
            while (Win32.Module32NextW(snapshot, ref entry));
        }
        finally
        {
            Win32.CloseHandle(snapshot);
        }

        return modules;
    }

    public ModuleInfo ModuleByName(string name)
    {
        foreach (ModuleInfo m in _modules)
        {
            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }

        throw new InvalidOperationException($"module \"{name}\" not found");
    }

    public bool TryGetModule(string name, out ModuleInfo module)
    {
        foreach (ModuleInfo m in _modules)
        {
            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                module = m;
                return true;
            }
        }

        module = default;
        return false;
    }

    /// <summary>Single ReadProcessMemory call. Returns the number of bytes actually copied
    /// (works for ERROR_PARTIAL_COPY where a prefix is readable).</summary>
    public unsafe int ReadPartial(ulong address, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        nuint read = 0;
        fixed (byte* p = destination)
        {
            Win32.ReadProcessMemory(_handle, (nuint)address, p, (nuint)destination.Length, &read);
        }

        return (int)read;
    }

    public bool TryReadExact(ulong address, Span<byte> destination) =>
        ReadPartial(address, destination) == destination.Length;

    /// <summary>Reads a whole module image by RVA, tolerating unmapped holes (left as zero),
    /// mirroring memflow's partial <c>read_raw(...).data_part()</c>.</summary>
    public byte[] ReadImage(ModuleInfo module)
    {
        var buffer = new byte[module.Size];

        // Fast path: try the whole image in one call (modules are usually fully committed).
        int n = ReadPartial(module.Base, buffer);
        if (n == buffer.Length)
        {
            return buffer;
        }

        // Slow path: walk the image, skipping the faulting page after each short read.
        int pos = n;
        AdvancePastHole(module.Base, ref pos);
        while (pos < buffer.Length)
        {
            int chunk = Math.Min(buffer.Length - pos, 1 << 20);
            int got = ReadPartial(module.Base + (ulong)pos, buffer.AsSpan(pos, chunk));
            if (got == chunk)
            {
                pos += chunk;
            }
            else
            {
                pos += got;
                AdvancePastHole(module.Base, ref pos);
            }
        }

        return buffer;
    }

    private static void AdvancePastHole(ulong baseAddr, ref int pos)
    {
        ulong addr = baseAddr + (ulong)pos;
        ulong skip = PageSize - (addr & (PageSize - 1));
        pos += (int)skip; // leave the unreadable page zero-filled
    }

    public ulong ReadU64(ulong address)
    {
        Span<byte> b = stackalloc byte[8];
        if (!TryReadExact(address, b))
        {
            throw new MemoryReadException(address, 8);
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(b);
    }

    public bool TryReadU64(ulong address, out ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        if (!TryReadExact(address, b))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(b);
        return true;
    }

    public uint ReadU32(ulong address)
    {
        Span<byte> b = stackalloc byte[4];
        if (!TryReadExact(address, b))
        {
            throw new MemoryReadException(address, 4);
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    public int ReadI32(ulong address) => unchecked((int)ReadU32(address));

    public bool TryReadU32(ulong address, out uint value)
    {
        Span<byte> b = stackalloc byte[4];
        if (!TryReadExact(address, b))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(b);
        return true;
    }

    /// <summary>memflow's <c>read_utf8_lossy</c>: reads up to <paramref name="max"/> bytes,
    /// truncates at the first NUL, decodes as lossy UTF-8. Throws only if nothing is readable.</summary>
    public string ReadUtf8Lossy(ulong address, int max)
    {
        byte[] buf = max <= _scratch.Length ? _scratch : new byte[max];
        int n = ReadPartial(address, buf.AsSpan(0, max));
        if (n <= 0)
        {
            throw new MemoryReadException(address, max);
        }

        int nul = Array.IndexOf(buf, (byte)0, 0, n);
        if (nul < 0)
        {
            nul = n;
        }

        return Encoding.UTF8.GetString(buf, 0, nul);
    }

    /// <summary>Reads a NUL-terminated lossy UTF-8 string out of an already-fetched buffer
    /// (mirrors <c>CStr::from_ptr</c> over a struct field).</summary>
    public static string CStringFromBuffer(ReadOnlySpan<byte> buffer)
    {
        int nul = buffer.IndexOf((byte)0);
        if (nul < 0)
        {
            nul = buffer.Length;
        }

        return Encoding.UTF8.GetString(buffer[..nul]);
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            Win32.CloseHandle(_handle);
        }
    }
}
