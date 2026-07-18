using System.Runtime.InteropServices;

namespace NexusMonitor.Platform.Windows.Native;

internal static partial class Kernel32
{
    private const string Dll = "kernel32.dll";

    // ─── Snapshot / Process Enumeration ─────────────────────────────────────

    public const uint TH32CS_SNAPPROCESS = 0x00000002;
    public const uint TH32CS_SNAPMODULE  = 0x00000008;
    public const uint TH32CS_SNAPMODULE32= 0x00000010;
    public const uint TH32CS_SNAPALL     = 0x0000000F;
    public const uint TH32CS_SNAPTHREAD  = 0x00000004;
    public static readonly nint INVALID_HANDLE_VALUE = new(-1);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32FirstW(nint hSnapshot, ref PROCESSENTRY32 lppe);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32NextW(nint hSnapshot, ref PROCESSENTRY32 lppe);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Module32FirstW(nint hSnapshot, ref MODULEENTRY32 lpme);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Module32NextW(nint hSnapshot, ref MODULEENTRY32 lpme);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Thread32First(nint hSnapshot, ref THREADENTRY32 lpte);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Thread32Next(nint hSnapshot, ref THREADENTRY32 lpte);

    // ─── Process Control ─────────────────────────────────────────────────────

    public const uint PROCESS_QUERY_INFORMATION     = 0x0400;
    public const uint PROCESS_QUERY_LIMITED_INFO    = 0x1000;
    public const uint PROCESS_VM_READ               = 0x0010;
    public const uint PROCESS_TERMINATE             = 0x0001;
    public const uint PROCESS_SET_INFORMATION       = 0x0200;
    public const uint SYNCHRONIZE                   = 0x00100000;
    public const uint PROCESS_ALL_ACCESS            = 0x1F0FFF;
    public const uint PROCESS_DUP_HANDLE            = 0x0040;

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint hProcess, uint uExitCode);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessHandleCount(nint hProcess, out uint pdwHandleCount);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessIoCounters(nint hProcess, out IO_COUNTERS lpIoCounters);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetPriorityClass(nint hProcess, uint dwPriorityClass);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial uint GetPriorityClass(nint hProcess);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessAffinityMask(nint hProcess, nint dwProcessAffinityMask);

    // Priority class constants
    public const uint IDLE_PRIORITY_CLASS         = 0x00000040;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    public const uint NORMAL_PRIORITY_CLASS       = 0x00000020;
    public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    public const uint HIGH_PRIORITY_CLASS         = 0x00000080;
    public const uint REALTIME_PRIORITY_CLASS     = 0x00000100;

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadProcessMemory(
        nint hProcess,
        nint lpBaseAddress,
        [Out] byte[] lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);

    // ─── Memory ──────────────────────────────────────────────────────────────

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ─── Token / Security ────────────────────────────────────────────────────

    public const uint TOKEN_QUERY = 0x0008;

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out nint TokenHandle);

    // ─── Timing ──────────────────────────────────────────────────────────────

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint hProcess,
        out long lpCreationTime, out long lpExitTime,
        out long lpKernelTime, out long lpUserTime);

    [LibraryImport(Dll)]
    public static partial void GetSystemTimeAsFileTime(out long lpSystemTimeAsFileTime);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(
        out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    // ─── Virtual Memory Query ─────────────────────────────────────────────────
    // Uses DllImport for reliable struct out-param marshalling.

    [DllImport(Dll, SetLastError = true)]
    public static extern nint VirtualQueryEx(
        nint hProcess,
        nint lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer,
        nint dwLength);
}
