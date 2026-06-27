using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WebAppLauncher.Core;

public static class JobObject
{
    private static readonly IntPtr JobHandle;

    static JobObject()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        JobHandle = CreateJobObject(IntPtr.Zero, null);
        if (JobHandle == IntPtr.Zero)
        {
            return;
        }

        var basicLimitInfo = new JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };

        var extendedLimitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = basicLimitInfo
        };

        var length = Marshal.SizeOf(extendedLimitInfo);
        var extendedLimitInfoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(extendedLimitInfo, extendedLimitInfoPtr, false);
            if (!SetInformationJobObject(JobHandle, JobObjectInfoClass.ExtendedLimitInformation, extendedLimitInfoPtr, (uint)length))
            {
                // Failed to set info
            }
        }
        finally
        {
            Marshal.FreeHGlobal(extendedLimitInfoPtr);
        }
    }

    public static void AssociateProcess(Process process)
    {
        if (JobHandle != IntPtr.Zero && !process.HasExited)
        {
            try
            {
                AssignProcessToJobObject(JobHandle, process.Handle);
            }
            catch
            {
                // Ignored
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoClass jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long ActiveProcessLimit;
        public long Affinity;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimitValue;
        public UIntPtr AffinityValue;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryLimit;
        public UIntPtr PeakJobMemoryLimit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
}
