using System.Runtime.InteropServices;

namespace Cs2Dumper.Memory;

/// <summary>
/// Minimal Win32 surface needed to enumerate processes/modules and read remote memory.
/// Uses <see cref="LibraryImportAttribute"/> source-generated marshalling so the whole
/// thing stays NativeAOT-friendly (no runtime marshalling / reflection).
/// </summary>
internal static unsafe partial class Win32
{
    public const uint TH32CS_SNAPPROCESS = 0x00000002;
    public const uint TH32CS_SNAPMODULE = 0x00000008;
    public const uint TH32CS_SNAPMODULE32 = 0x00000010;

    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_VM_READ = 0x0010;

    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_PARTIAL_COPY = 299;
    public const int ERROR_BAD_LENGTH = 24;

    public static readonly nint INVALID_HANDLE_VALUE = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public nint modBaseAddr;
        public uint modBaseSize;
        public nint hModule;
        public fixed char szModule[256];   // MAX_MODULE_NAME32 + 1
        public fixed char szExePath[260];  // MAX_PATH
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32FirstW(nint hSnapshot, ref PROCESSENTRY32W lppe);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32NextW(nint hSnapshot, ref PROCESSENTRY32W lppe);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Module32FirstW(nint hSnapshot, ref MODULEENTRY32W lpme);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Module32NextW(nint hSnapshot, ref MODULEENTRY32W lpme);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadProcessMemory(nint hProcess, nuint lpBaseAddress, byte* lpBuffer, nuint nSize, nuint* lpNumberOfBytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    public static string FixedToString(char* buffer, int maxChars)
    {
        int len = 0;
        while (len < maxChars && buffer[len] != '\0')
        {
            len++;
        }

        return new string(buffer, 0, len);
    }
}
