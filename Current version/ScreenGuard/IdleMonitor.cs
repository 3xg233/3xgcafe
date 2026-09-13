using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ScreenGuard.Native;

namespace ScreenGuard;

/// <summary>
/// 空闲计时：每秒读取一次系统级"最后一次输入时间"，达到阈值就触发锁定。
/// 支持临时暂停（托盘菜单里限时关闭监控）。
/// </summary>
internal sealed class IdleMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private DateTime _pauseUntil = DateTime.MinValue;
    private bool _triggered;

    public IdleMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTick;
    }

    /// <summary>空闲多少分钟后锁定。</summary>
    public int IdleMinutes { get; set; } = 5;

    public TimeSpan IdleTime { get; private set; }

    public bool IsPaused => DateTime.Now < _pauseUntil;

    public TimeSpan PauseRemaining => IsPaused ? _pauseUntil - DateTime.Now : TimeSpan.Zero;

    public TimeSpan TimeUntilLock
    {
        get
        {
            TimeSpan limit = TimeSpan.FromMinutes(Math.Max(1, IdleMinutes));
            return limit - IdleTime;
        }
    }

    /// <summary>每秒触发一次（用于刷新托盘提示）。</summary>
    public event Action? Tick;

    /// <summary>空闲达到阈值时触发一次。</summary>
    public event Action? IdleThresholdReached;

    public void Start()
    {
        _triggered = false;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>解锁后重新计时。</summary>
    public void Restart()
    {
        _triggered = false;
        IdleTime = GetIdleTime();
        _timer.Start();
    }

    public void Pause(TimeSpan duration)
    {
        _pauseUntil = DateTime.Now + duration;
        _triggered = false;
    }

    public void Resume()
    {
        _pauseUntil = DateTime.MinValue;
        _triggered = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        IdleTime = GetIdleTime();
        Tick?.Invoke();

        if (_triggered || IsPaused)
            return;

        if (IdleTime >= TimeSpan.FromMinutes(Math.Max(1, IdleMinutes)))
        {
            _triggered = true;
            IdleThresholdReached?.Invoke();
        }
    }

    /// <summary>系统级空闲时长（TickCount 用 uint 运算，天然处理 49 天回绕）。</summary>
    public static TimeSpan GetIdleTime()
    {
        var info = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
        };

        if (!NativeMethods.GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        uint now = (uint)Environment.TickCount;
        return TimeSpan.FromMilliseconds(now - info.dwTime);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
