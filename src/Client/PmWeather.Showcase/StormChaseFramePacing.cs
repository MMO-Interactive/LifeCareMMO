using System.Diagnostics;
using System.Runtime.InteropServices;
using Engine.Rendering.Backend;

/// <summary>
/// UI-loop pacing. The loops previously slept in MsgWaitForMultipleObjects(8 ms) after
/// every submission; with Windows' default per-process 15.6 ms timer granularity that
/// sleep lasted ~15.6 ms whenever no input arrived, holding a stationary player near
/// 30 FPS. Now the timer runs at 1 ms and the UI waits only while the renderer still
/// has the previous frame queued, keeping exactly one frame in flight.
/// </summary>
internal static class StormChaseFramePacing
{
    private static int highResolution;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    /// <summary>MMOI_STORM_CHASE_LEGACY_PACING=1 restores the old fixed 8 ms wait (for A/B measurement).</summary>
    private static readonly bool Legacy = Environment.GetEnvironmentVariable("MMOI_STORM_CHASE_LEGACY_PACING") == "1";

    public static void EnableHighResolutionTimer()
    {
        if (Legacy || Interlocked.Exchange(ref highResolution, 1) != 0) return;
        timeBeginPeriod(1);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => timeEndPeriod(1);
    }

    /// <summary>Service input until the renderer has picked up the newest frame (bounded).</summary>
    public static void WaitForRenderer(DedicatedRenderBackendHost host, NativeSurface surface, double maximumMilliseconds = 20)
    {
        if (Legacy) { surface.WaitForMessages(8); return; }
        long started = Stopwatch.GetTimestamp();
        while (host.GetProgressSnapshot().QueuedFrameCount > 0
               && Stopwatch.GetElapsedTime(started).TotalMilliseconds < maximumMilliseconds)
            surface.WaitForMessages(1);
    }
}
