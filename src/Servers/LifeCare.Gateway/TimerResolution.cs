using System.Runtime.InteropServices;

namespace LifeCare.Gateway;

/// <summary>Raises this process's Windows timer resolution so short relay waits wake on time.</summary>
internal static class TimerResolution
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    public static void Request(uint milliseconds) => TimeBeginPeriod(milliseconds);
}
