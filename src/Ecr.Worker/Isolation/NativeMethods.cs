// src/Ecr.Worker/Isolation/NativeMethods.cs

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Ecr.Worker.Isolation;

/// <summary>Виклики kernel32 для Job Object (лише Windows).</summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    /// <summary><c>JOB_OBJECT_LIMIT_PROCESS_MEMORY</c>: стеля закоміченої пам'яті кожного процесу.</summary>
    public const uint LimitProcessMemory = 0x0000_0100;

    /// <summary><c>JOB_OBJECT_LIMIT_JOB_MEMORY</c>: стеля закоміченої пам'яті всіх процесів разом.</summary>
    public const uint LimitJobMemory = 0x0000_0200;

    /// <summary><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: закриття останнього дескриптора вбиває всі процеси.</summary>
    public const uint LimitKillOnJobClose = 0x0000_2000;

    /// <summary><c>JobObjectExtendedLimitInformation</c>.</summary>
    public const int ExtendedLimitInformationClass = 9;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeJobHandle CreateJobObject(nint securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(
        SafeJobHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    /// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    /// <summary><c>IO_COUNTERS</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}

/// <summary>Дескриптор Job Object; закриття — <c>CloseHandle</c>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Для маршалера <c>LibraryImport</c>.</summary>
    public SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
