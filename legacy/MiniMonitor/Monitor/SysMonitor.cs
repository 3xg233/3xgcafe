using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using MiniMonitor.Native;

namespace MiniMonitor.Monitor;

/// <summary>
/// 后台线程采集系统性能数据：CPU / 内存 / 磁盘 / 网络 每秒一次，CPU 温度 / 电池 每 5 秒一次。
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
    private double _disk0Pct = -1;
    private double _disk1Pct = -1;
    private double _batteryPct = -1;
    private int _batteryCharging;
    private double _rxBytesPerSec = -1;
    private double _txBytesPerSec = -1;

    // GPU 行映射：LUID → DXGI 适配器序号 / 显卡名称（启动时枚举一次，硬件不变映射不变）。
    // GPU0/GPU1 按 DXGI 序号固定对应系统适配器 0/1，不再随 WMI 枚举顺序互换。
    private readonly Dictionary<string, int> _gpuOrdinal = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _gpuNames = new(StringComparer.OrdinalIgnoreCase);
    private volatile string? _gpu0Name;
    private volatile string? _gpu1Name;

    // 两行磁盘指标对应的盘符（如 "C"/"D"）；null 表示该行没有可监控的盘
    private readonly string? _drive0Letter;
    private readonly string? _drive1Letter;

    private long _netRxPrev;
    private long _netTxPrev;
    private DateTime _netPrevTime = DateTime.MinValue;

    public double CpuPercent => Volatile.Read(ref _cpuPct);
    public double MemoryPercent => Volatile.Read(ref _memPct);
    public double TempCelsius => Volatile.Read(ref _tempC);
    public double Gpu0Percent => Volatile.Read(ref _gpu0Pct);
    public double Gpu1Percent => Volatile.Read(ref _gpu1Pct);
    /// <summary>GPU0 行对应的显卡名称（首个 GPU 采样周期后才有值，未知名称为 null）。</summary>
    public string? Gpu0Name => _gpu0Name;
    public string? Gpu1Name => _gpu1Name;
    public double Disk0Percent => Volatile.Read(ref _disk0Pct);
    public double Disk1Percent => Volatile.Read(ref _disk1Pct);
    public string? Drive0Letter => _drive0Letter;
    public string? Drive1Letter => _drive1Letter;
    public double BatteryPercent => Volatile.Read(ref _batteryPct);
    public bool BatteryCharging => Volatile.Read(ref _batteryCharging) != 0;
    public double RxBytesPerSec => Volatile.Read(ref _rxBytesPerSec);
    public double TxBytesPerSec => Volatile.Read(ref _txBytesPerSec);

    /// <param name="driveLetters">设置里的盘符（1~2 个字母）；为 null 或无效时自动检测固定盘。</param>
    public SysMonitor(IReadOnlyList<string>? driveLetters = null)
    {
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _ = _cpuCounter.NextValue(); // 预热，第一次调用返回 0

        (_drive0Letter, _drive1Letter) = ResolveDriveLetters(driveLetters);

        var adapters = Dxgi.EnumerateAdapters();
        for (int i = 0; i < adapters.Count; i++)
        {
            _gpuOrdinal[adapters[i].LuidKey] = i;
            if (!string.IsNullOrEmpty(adapters[i].Name))
                _gpuNames[adapters[i].LuidKey] = adapters[i].Name;
        }

        _worker = new Thread(Loop) { IsBackground = true, Name = "MiniMonitor.Sampler" };
        _worker.Start();
    }

    private void Loop()
    {
        var lastTemp = DateTime.MinValue;
        var lastGpu = DateTime.MinValue;
        var lastBattery = DateTime.MinValue;

        while (_running)
        {
            try
            {
                try { Volatile.Write(ref _cpuPct, Math.Max(0, _cpuCounter.NextValue())); }
                catch { Volatile.Write(ref _cpuPct, -1); }

                Volatile.Write(ref _memPct, QueryMemoryPercent());
                Volatile.Write(ref _disk0Pct, _drive0Letter is null ? -1 : QueryDrivePercent(_drive0Letter));
                Volatile.Write(ref _disk1Pct, _drive1Letter is null ? -1 : QueryDrivePercent(_drive1Letter));

                SampleNetwork();

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
                    Volatile.Write(ref _gpu0Pct, gpus.Count > 0 ? gpus[0].Util : -1);
                    Volatile.Write(ref _gpu1Pct, gpus.Count > 1 ? gpus[1].Util : -1);
                    _gpu0Name = gpus.Count > 0 ? gpus[0].Name : null;
                    _gpu1Name = gpus.Count > 1 ? gpus[1].Name : null;
                }

                if ((DateTime.UtcNow - lastBattery).TotalSeconds >= 5)
                {
                    lastBattery = DateTime.UtcNow;
                    var battery = QueryBattery();
                    Volatile.Write(ref _batteryPct, battery.Percent);
                    Volatile.Write(ref _batteryCharging, battery.Charging ? 1 : 0);
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

    /// <summary>
    /// 决定两行磁盘指标监控的盘符：设置里给了有效盘符（1~2 个 A-Z 字母）就用设置，
    /// 否则自动检测已就绪的固定盘、按字母序取前两个。任一行没有盘则为 null。
    /// </summary>
    private static (string? First, string? Second) ResolveDriveLetters(IReadOnlyList<string>? configured)
    {
        if (configured is not null)
        {
            var letters = configured
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => char.ToUpperInvariant(s.Trim()[0]))
                .Where(c => c is >= 'A' and <= 'Z')
                .Distinct()
                .Take(2)
                .Select(c => c.ToString())
                .ToList();
            if (letters.Count > 0)
                return (letters[0], letters.Count > 1 ? letters[1] : null);
        }

        try
        {
            var fixedLetters = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && !string.IsNullOrEmpty(d.Name))
                .Select(d => d.Name.Substring(0, 1).ToUpperInvariant())
                .OrderBy(l => l, StringComparer.Ordinal)
                .Take(2)
                .ToList();
            return (fixedLetters.Count > 0 ? fixedLetters[0] : null,
                    fixedLetters.Count > 1 ? fixedLetters[1] : null);
        }
        catch { return (null, null); }
    }

    // ---------- 网络：物理网卡 IP 层收发字节差值 ----------

    private void SampleNetwork()
    {
        try
        {
            var (rx, tx) = QueryNetworkTotals();
            if (rx < 0 || tx < 0 || _netPrevTime == DateTime.MinValue)
            {
                _netRxPrev = rx;
                _netTxPrev = tx;
                _netPrevTime = DateTime.UtcNow;
                return;
            }

            double dt = (DateTime.UtcNow - _netPrevTime).TotalSeconds;
            if (dt > 0)
            {
                Volatile.Write(ref _rxBytesPerSec, Math.Max(0, (rx - _netRxPrev) / dt));
                Volatile.Write(ref _txBytesPerSec, Math.Max(0, (tx - _netTxPrev) / dt));
            }
            _netRxPrev = rx;
            _netTxPrev = tx;
            _netPrevTime = DateTime.UtcNow;
        }
        catch
        {
            Volatile.Write(ref _rxBytesPerSec, -1);
            Volatile.Write(ref _txBytesPerSec, -1);
        }
    }

    private static (long Rx, long Tx) QueryNetworkTotals()
    {
        long rx = 0, tx = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var type = ni.NetworkInterfaceType;
                if (type is not (NetworkInterfaceType.Ethernet or
                                  NetworkInterfaceType.Wireless80211 or
                                  NetworkInterfaceType.Ppp)) continue;

                var stats = ni.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
            catch { /* 单个网卡统计失败不影响其他网卡 */ }
        }
        return (rx, tx);
    }

    // ---------- 电池：GetSystemPowerStatus ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(ref SystemPowerStatus status);

    private static (double Percent, bool Charging) QueryBattery()
    {
        try
        {
            var status = new SystemPowerStatus();
            if (!GetSystemPowerStatus(ref status)) return (-1, false);
            // 255 = 未知；128 = 无电池（台式机）
            if (status.BatteryLifePercent == 255 || status.BatteryFlag == 128) return (-1, false);

            bool charging = status.AcLineStatus == 1 || (status.BatteryFlag & 8) != 0;
            return (status.BatteryLifePercent, charging);
        }
        catch { return (-1, false); }
    }

    // ---------- GPU：GPU Engine 计数器按适配器(LUID)聚合，取最大引擎占用（与任务管理器口径一致） ----------

    /// <summary>
    /// 聚合各 LUID 的最大引擎占用后按 DXGI 适配器顺序排序，GPU0/GPU1 固定对应系统适配器 0/1；
    /// DXGI 枚举不到的 LUID 排在已知之后，按名称字典序兜底，保证顺序稳定不抖动。
    /// </summary>
    private List<(double Util, string? Name)> QueryGpuPercents()
    {
        var result = new List<(double Util, string? Name)>();
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

            foreach (var luid in maxByLuid.Keys
                         .OrderBy(k => _gpuOrdinal.TryGetValue(k, out var o) ? o : int.MaxValue)
                         .ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                result.Add((maxByLuid[luid], _gpuNames.TryGetValue(luid, out var n) ? n : null));
            }
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
                    double raw = Convert.ToDouble(value) / 10.0;
                    // 多数固件按 0.1°C 上报，少数按 0.1K（ACPI 惯例），两种都尝试，范围校验兜底
                    if (raw is > 0 and < 150) return raw;
                    double fromKelvin = raw - 273.15;
                    if (fromKelvin is > 0 and < 150) return fromKelvin;
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
