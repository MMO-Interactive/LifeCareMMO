using System.Runtime.InteropServices;

namespace StormChase.Networking;

internal static unsafe class NativeMethods
{
    internal const string Library = "stormchase_gns";
    internal static readonly object Gate = new();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint sc_host_create(ushort port, int maxConnections, [MarshalAs(UnmanagedType.LPUTF8Str)] string authority, byte* certificate, int certificateLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string expectedIdentity, byte* error, int errorLength);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void sc_host_destroy(nint host);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern uint sc_connect(nint host, [MarshalAs(UnmanagedType.LPUTF8Str)] string address);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sc_send(nint host, uint connection, byte* data, int length, int reliable);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void sc_close(nint host, uint connection, [MarshalAs(UnmanagedType.LPUTF8Str)] string reason);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sc_poll(nint host, out uint connection, byte* payload, int capacity, out int length, byte* reason, int reasonCapacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sc_status(nint host, uint connection, byte* text, int capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void sc_simulate(float lossPercent, int lagMilliseconds);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sc_generate_identity([MarshalAs(UnmanagedType.LPUTF8Str)] string identity, byte* authority, int authorityCapacity, out int authorityLength, byte* certificate, int certificateCapacity, out int certificateLength);
}
