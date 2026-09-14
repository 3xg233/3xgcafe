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
    private DateTime? _idleBaseline;
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
        _idleBaseline = null;
        IdleTime = GetIdleTime();
        _timer.Start();
    }

    /// <summary>
    /// 重置空闲计时基准：此后空闲时长最多只算到"此刻"，不再累计基准之前的时长。
    /// 用于系统睡眠 / 息屏之后 —— 这段期间 <c>GetLastInputInfo</c> 不更新，
    /// 唤醒后系统空闲会虚高（等于睡眠时长 + 之前的空闲），不处理会在唤醒瞬间立刻锁定。
    /// </summary>
    public void ResetBaseline()
    {
        _idleBaseline = DateTime.Now;
        _triggered = false;
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

        // 有基准时把空闲时长截断为"自基准时刻以来的时长"，让计时从唤醒 / 亮屏那一刻重新开始
        if (_idleBaseline is DateTime baseline)
        {
            TimeSpan sinceBaseline = DateTime.Now - baseline;
            if (sinceBaseline < IdleTime)
                IdleTime = sinceBaseline;
        }

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
