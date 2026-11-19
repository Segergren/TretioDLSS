using Serilog;

namespace TretioDLSS.Capture;

/// <summary>Logs when an opened capture device delivers nothing, so a silent card is visible in the log.</summary>
public sealed class FirstFrameWatchdog : IDisposable
{
    private readonly Timer _timer;
    private volatile bool _arrived;

    public FirstFrameWatchdog(string device, string api, string format, int seconds = 3)
    {
        _timer = new Timer(_ =>
        {
            if (!_arrived)
                Log.Warning("{Device} opened through {Api} as {Format} but delivered no frame in {Seconds} s (no signal, HDCP, or the device app is not running)", device, api, format, seconds);
        }, null, seconds * 1000, Timeout.Infinite);
    }

    public void Arrived() => _arrived = true;

    public void Dispose() => _timer.Dispose();
}
