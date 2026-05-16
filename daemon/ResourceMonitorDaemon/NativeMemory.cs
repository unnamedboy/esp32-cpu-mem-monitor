using System;
using System.Runtime.InteropServices;

namespace ResourceMonitorDaemon
{
    public static class NativeMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public void Init() => dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public static (double TotalGB, double UsedGB, uint MemoryLoad) GetMemoryStatus()
        {
            var stat = new MEMORYSTATUSEX();
            stat.Init();

            if (GlobalMemoryStatusEx(ref stat))
            {
                double total = stat.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                double avail = stat.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                return (total, total - avail, stat.dwMemoryLoad);
            }
            return (0, 0, 0);
        }
    }
}
