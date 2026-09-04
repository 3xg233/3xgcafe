using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace MiniMonitor.Monitor;

/// <summary>
/// 后台线程采集系统性能数据：CPU / 内存 / 磁盘 / GPU 每秒一次，CPU 温度每 5 秒一次。
/// </summary>
public sealed class SysMonitor : IDisposable
{
    private readonly Thread _worker;
    private volatile bool _running = true;

    private readonly PerformanceCounter _cpuCounter;

    // 快照字段：后台线程每秒写入，UI 线程读取。
    // 用 double + (-1 表示不可用) 哨兵值避免可空类型的撕裂风险；读写均经 Volatile.Read/Write
    // 保证写入对 UI 线程立即可见（C# 不允许 volatile double，故用 Volatile 类提供获取/释放语义）。
    private double _cpuPct = -1;
    private double _memPct = -1;
    private double _tempC = -1;
    private double _gpu0Pct = -1;
    private double _gpu1Pct = -1;
    private double _diskCPct = -1;
    private double _diskDPct = -1;

    public double CpuPercent => Volatile.Read(ref _cpuPct);
    public double MemoryPercent => Volatile.Read(ref _memPct);
    public double TempCelsius => Volatile.Read(ref _tempC);
    public double Gpu0Percent => Volatile.Read(ref _gpu0Pct);
    public double Gpu1Percent => Volatile.Read(ref _gpu1Pct);
    public double DiskCPercent => Volatile.Read(ref _diskCPct);
    public double DiskDPercent => Volatile.Read(ref _diskDPct);

    public SysMonitor()
    {
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _ = _cpuCounter.NextValue(); // 预热，第一次调用返回 0

        _worker = new Thread(Loop) { IsBackground = true, Name = "MiniMonitor.Sampler" };
        _worker.Start();
    }

    private void Loop()
    {
        var lastTemp = DateTime.MinValue;
        var lastGpu = DateTime.MinValue;

        while (_running)
        {
            try
            {
                try { Volatile.Write(ref _cpuPct, Math.Max(0, _cpuCounter.NextValue())); }
                catch { Volatile.Write(ref _cpuPct, -1); }

                Volatile.Write(ref _memPct, QueryMemoryPercent());
                Volatile.Write(ref _diskCPct, QueryDrivePercent("C"));
                Volatile.Write(ref _diskDPct, QueryDrivePercent("D"));

                if ((DateTime.UtcNow - lastTemp).TotalSeconds >= 5)
                {
                    lastTemp = DateTime.UtcNow;
                    Volatile.Write(ref _tempC, QueryCpuTemperature() ?? -1);
                }

                // GPU Engine 计数器查询较重，2 秒一次
                if ((DateTime.UtcNow - lastGpu).TotalSeconds >= 2)
                {
                    lastGpu = DateTime.UtcNow;
                    var gpus = QueryGpuPercents();
                    Volatile.Write(ref _gpu0Pct, gpus.Count > 0 ? gpus[0] : -1);
                    Volatile.Write(ref _gpu1Pct, gpus.Count > 1 ? gpus[1] : -1);
                }
            }
            catch { /* 单轮采集失败不致命，下一轮重试 */ }

            Thread.Sleep(1000);
        }
    }

    // ---------- 内存：GlobalMemoryStatusEx ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint DwLength;
        public uint DwMemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    private static double QueryMemoryPercent()
    {
        try
        {
            var st = new MemoryStatusEx { DwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref st) || st.TotalPhys == 0) return -1;
            return 100.0 * (1.0 - (double)st.AvailPhys / st.TotalPhys);
        }
        catch { return -1; }
    }

    // ---------- 磁盘：指定盘符的使用率 ----------

    private static double QueryDrivePercent(string letter)
    {
        try
        {
            var d = new DriveInfo(letter);
            if (!d.IsReady || d.DriveType != DriveType.Fixed || d.TotalSize <= 0) return -1;
            return 100.0 * (1.0 - (double)d.TotalFreeSpace / d.TotalSize);
        }
        catch { return -1; } // 盘不存在或未就绪
    }

    // ---------- GPU：GPU Engine 计数器按适配器(LUID)聚合，取最大引擎占用（与任务管理器口径一致） ----------

    private static List<double> QueryGpuPercents()
    {
        var result = new List<double>();
        try
        {
            // 每个引擎实例: pid_1234_luid_0x00000000_0x000A4321_phys_0_eng_0_engtype_3D
            using var searcher = new ManagementObjectSearcher(Scope("root\\cimv2"), new ObjectQuery(
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"));
            var maxByLuid = new Dictionary<string, double>();

            foreach (var obj in searcher.Get())
            {
                var name = Convert.ToString(obj["Name"]) ?? "";
                var util = Convert.ToDouble(obj["UtilizationPercentage"]);
                var luid = ExtractLuid(name);
                if (luid is null) continue;
                if (util > maxByLuid.GetValueOrDefault(luid))
                    maxByLuid[luid] = util;
            }

            foreach (var key in maxByLuid.Keys)
                result.Add(maxByLuid[key]);
        }
        catch { /* 老系统/虚拟机无 GPU 计数器时忽略 */ }
        return result;
    }

    private static string? ExtractLuid(string instanceName)
    {
        var parts = instanceName.Split('_');
        // 期望: ["pid","1234","luid","0x00000000","0x000A4321","phys",...]
        for (int i = 0; i < parts.Length - 2; i++)
        {
            if (string.Equals(parts[i], "luid", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1] + "_" + parts[i + 2];
        }
        return null;
    }

    // ---------- CPU 温度：WMI（失败返回 null） ----------

    /// <summary>
    /// 优先用 ThermalZoneInformation（普通权限可读，ACPI 热区，单位 0.1°C）；
    /// 失败再尝试 MSAcpi（管理员权限，CPU 热区更精确）。所有查询带 5 秒超时避免挂起采集循环。
    /// </summary>
    private static double? QueryCpuTemperature()
    {
        // 方案一（首选）：性能计数器热区信息，无需管理员
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope("root\\cimv2"), new ObjectQuery(
                "SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"));
            foreach (var obj in searcher.Get())
            {
                var value = obj["Temperature"];
                if (value is not null)
                {
                    double celsius = Convert.ToDouble(value) / 10.0;
                    if (celsius is > 0 and < 150) return celsius;
                }
            }
        }
        catch { /* 尝试方案二 */ }

        // 方案二：ACPI 热区（需要管理员权限，很多机器可用）
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope("root\\WMI"), new ObjectQuery(
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));
            foreach (var obj in searcher.Get())
            {
                var value = obj["CurrentTemperature"];
                if (value is not null)
                {
                    double celsius = Convert.ToDouble(value) / 10.0 - 273.15;
                    if (celsius is > 0 and < 150) return celsius;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>创建带超时选项的 WMI 作用域，防止权限拒绝查询长时间挂起。</summary>
    private static ManagementScope Scope(string path)
    {
        var options = new ConnectionOptions
        {
            Timeout = TimeSpan.FromSeconds(5),
            EnablePrivileges = true,
        };
        return new ManagementScope(path, options);
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            if (!_worker.Join(2000)) _worker.Interrupt();
        }
        catch { }
        _cpuCounter.Dispose();
    }
}
