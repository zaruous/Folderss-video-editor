using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VideoEditor.Media
{
    /// <summary>
    /// FFmpeg 자식 프로세스를 Windows 작업 개체에 넣는다(닫히면 같이 종료). Folderss가 어떻게 끝나든 FFmpeg가 남아 돌지 않는다.
    /// Windows가 아니면 아무것도 하지 않는다.
    /// </summary>
    internal static class JobObject
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;
        private static readonly object Gate = new object();
        private static IntPtr _job;

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimit
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimit
        {
            public BasicLimit BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, uint length);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        public static void Add(Process process)
        {
            if (!OperatingSystem.IsWindows())
                return;
            try
            {
                lock (Gate)
                {
                    if (_job == IntPtr.Zero)
                    {
                        var job = CreateJobObjectW(IntPtr.Zero, null);
                        var info = new ExtendedLimit();
                        info.BasicLimitInformation.LimitFlags = KillOnJobClose;
                        if (job != IntPtr.Zero && SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf(typeof(ExtendedLimit))))
                            _job = job;
                    }
                    if (_job != IntPtr.Zero)
                        AssignProcessToJobObject(_job, process.Handle);
                }
            }
            catch (Exception)
            {
                // 작업 개체는 정리 보조 수단이다 — 실패해도 FFmpeg 실행은 계속한다(창 닫을 때 직접 끝냄)
            }
        }
    }
}
