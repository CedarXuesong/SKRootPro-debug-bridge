using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SkpdbDesktop;

/// <summary>
/// Absolute QPC deadlines on a dedicated thread. A high-resolution one-shot
/// timer does the sleeping; only the final 150 us are spun. Late frames are
/// skipped rather than replayed in a burst or shifting every later deadline.
/// </summary>
public sealed class PrecisionSampleClock : IDisposable
{
    public const int Rate = 240;
    private readonly SafeWaitHandle _timer;
    private readonly long _epoch = Stopwatch.GetTimestamp();
    private long _frame;
    public long MissedDeadlines { get; private set; }

    public PrecisionSampleClock()
    {
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x00100002);
        if (_timer.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建高精度采样时钟");
    }

    public void WaitNext(CancellationToken cancellationToken)
    {
        var frequency = Stopwatch.Frequency;
        var now = Stopwatch.GetTimestamp();
        var target = _epoch + ++_frame * frequency / Rate;
        if (now - target >= frequency / Rate)
        {
            var next = (now - _epoch) * Rate / frequency + 1;
            MissedDeadlines += next - _frame;
            _frame = next;
            target = _epoch + _frame * frequency / Rate;
        }
        var spinMargin = frequency * 150 / 1_000_000;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = target - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            if (remaining > spinMargin)
            {
                var due = -Math.Max(1, (remaining - spinMargin) * 10_000_000 / frequency);
                if (!SetWaitableTimerEx(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "设置采样时钟失败");
                // Each wait is at most one sample period, so Stop never waits
                // for a long ordinary sleep or an async thread-pool callback.
                if (WaitForSingleObject(_timer, 100) == 0xffffffff)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "等待采样时钟失败");
            }
            else Thread.SpinWait(16);
        }
    }

    public void Dispose() => _timer.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes,
        string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long due,
        int period, IntPtr completion, IntPtr argument, IntPtr wakeContext, uint tolerance);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
