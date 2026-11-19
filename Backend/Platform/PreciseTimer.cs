using System.Runtime.InteropServices;
using TretioDLSS.Pipeline;

namespace TretioDLSS.Platform;

/// <summary>Sleeps until a QPC deadline: high-resolution waitable timer, then spin the last stretch.</summary>
public sealed class PreciseTimer : IDisposable
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private readonly IntPtr _timer;
    private readonly IntPtr[] _handles = new IntPtr[2];

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, IntPtr name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public PreciseTimer()
    {
        _timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    }

    /// <summary>Waits until the deadline or until the event is set. Returns true if the event woke it.</summary>
    public bool WaitUntil(long deadlineQpc, WaitHandle? wake = null, double spinMs = 0.8)
    {
        long spinTicks = Qpc.FromMs(spinMs);
        long now = Qpc.Now;
        long sleepUntil = deadlineQpc - spinTicks;
        if (sleepUntil > now)
        {
            // Relative due time in 100 ns units, negative.
            long due = -(long)((sleepUntil - now) * 10_000_000.0 / Qpc.Frequency);
            if (_timer != IntPtr.Zero && SetWaitableTimerEx(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            {
                if (wake != null)
                {
                    _handles[0] = _timer;
                    _handles[1] = wake.SafeWaitHandle.DangerousGetHandle();
                    uint r = WaitForMultipleObjects(2, _handles, false, 1000);
                    if (r == 1) return true;
                }
                else
                {
                    WaitForSingleObject(_timer, 1000);
                }
            }
            else
            {
                int ms = (int)Qpc.ToMs(sleepUntil - now);
                if (wake != null ? wake.WaitOne(ms) : false) return true;
                if (wake == null) Thread.Sleep(ms);
            }
        }
        while (Qpc.Now < deadlineQpc)
        {
            if (wake != null && wake.WaitOne(0)) return true;
            Thread.SpinWait(20);
        }
        return false;
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
    }
}
