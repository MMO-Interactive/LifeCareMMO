using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;

// Opt-in --fly-smoke verification. Injected input is gated to this process's
// foreground HWND, and every key is released in a finally block.
internal static class FlyCameraProbe
{
    public static object Verify(NativeSurface surface, FlyCamera camera)
    {
        var checks = new List<string>();
        GetCursorPos(out var originalCursor);
        try
        {
            Focus(surface);
            Post(surface, 0x0201, 0);
            surface.PumpMessages();
            Require(surface.MouseLookCaptured && GetCapture() == surface.WindowHandle, "Click did not capture mouse look.");
            CheckClip(surface);
            checks.Add("click capture and client clipping");

            Vector3 initialForward = camera.Forward;
            Require(surface.IsInputActive, "Input focus changed before mouse injection.");
            Send(new Input { Type = 0, MouseX = 140, MouseY = -60, MouseFlags = 1 });
            Pump(surface);
            surface.UpdateFlyCamera(camera, 0);
            Require(Vector3.Distance(initialForward, camera.Forward) > 0.02f && camera.Forward.Y > initialForward.Y,
                "Native mouse input did not turn the camera right/up.");
            checks.Add("native relative mouse look");

            Vector3 before = camera.Position;
            MoveWithKeys(surface, camera, [0x57]);
            Vector3 delta = camera.Position - before;
            // Mouse packets queued during the key pump can turn the camera in
            // this same update. Movement must follow that updated direction.
            Vector3 forward = camera.Forward;
            Require(MathF.Abs(delta.Length() - 35) < 0.1f && Vector3.Dot(Vector3.Normalize(delta), forward) > 0.999f,
                $"W did not fly along the viewing direction: distance={delta.Length()}, delta={delta}, expected={forward}, actual={camera.Forward}, speed={camera.SpeedMetersPerSecond}.");
            checks.Add("W follows view direction");

            before = camera.Position;
            MoveWithKeys(surface, camera, [0x44]);
            Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, camera.Forward));
            delta = camera.Position - before;
            Require(MathF.Abs(delta.Length() - 35) < 0.1f && Vector3.Dot(Vector3.Normalize(delta), right) > 0.999f,
                "D strafe direction is incorrect.");
            checks.Add("D strafes right");

            before = camera.Position;
            MoveWithKeys(surface, camera, [0x57, 0x10]);
            Require(MathF.Abs(Vector3.Distance(camera.Position, before) - 140) < 0.1f, "Shift did not boost speed four times.");
            checks.Add("Shift speed boost");

            before = camera.Position;
            MoveWithKeys(surface, camera, [0x20]);
            Require(Vector3.Distance(camera.Position - before, new Vector3(0, 35, 0)) < 0.1f, "Space did not move up.");
            MoveWithKeys(surface, camera, [0x51]);
            Require(Vector3.Distance(camera.Position, before) < 0.1f, "Q did not move down.");
            checks.Add("Space up and Q down");

            before = camera.Position;
            MoveWithKeys(surface, camera, [0x57, 0x44]);
            Require(MathF.Abs(Vector3.Distance(camera.Position, before) - 35) < 0.1f, "Diagonal input increases movement speed.");
            checks.Add("normalized diagonal movement");

            Post(surface, 0x020A, 120u << 16);
            surface.PumpMessages();
            surface.UpdateFlyCamera(camera, 0);
            Require(MathF.Abs(camera.SpeedMetersPerSecond - 437.5f) < 0.1f, "Mouse wheel did not change speed.");
            checks.Add("wheel speed adjustment");

            Post(surface, 0x0100, 0x1B);
            surface.PumpMessages();
            CheckReleased(surface);
            Require(!surface.IsCloseRequested, "First Escape closed the captured demo.");
            checks.Add("Escape releases without closing");

            Post(surface, 0x0100, 0x09);
            surface.PumpMessages();
            surface.UpdateFlyCamera(camera, 0);
            Require(Vector3.Distance(camera.Position, new(-6000, 1100, -7800)) < 0.1f, "Tab did not select the wide preset.");
            checks.Add("Tab camera preset");

            surface.CaptureMouseLook();
            surface.ToggleFullscreen();
            surface.PumpMessages();
            Require(surface.IsFullscreen && surface.MouseLookCaptured, "Fullscreen did not preserve mouse-look state.");
            CheckClip(surface);
            surface.ToggleFullscreen();
            surface.PumpMessages();
            Require(!surface.IsFullscreen && surface.MouseLookCaptured, "Restored window lost mouse-look state.");
            CheckClip(surface);
            checks.Add("fullscreen roundtrip preserves capture and clip bounds");

            nint focusProbe = CreateWindowExW(0, "STATIC", "MMOI input focus verification", 0x00CF0000,
                100, 100, 320, 180, 0, 0, 0, 0);
            Require(focusProbe != 0, "Could not create focus-loss probe.");
            try
            {
                ShowWindow(focusProbe, 5);
                Activate(focusProbe);
                Pump(surface);
                Require(GetForegroundWindow() == focusProbe, "Could not activate focus-loss probe.");
                CheckReleased(surface);
                before = camera.Position;
                surface.UpdateFlyCamera(camera, 0.1f);
                Require(camera.Position == before, "Inactive window changed camera position.");
            }
            finally { DestroyWindow(focusProbe); }
            checks.Add("focus loss releases cursor and pauses input");

            Focus(surface);
            surface.CaptureMouseLook();
            ShowWindow(surface.WindowHandle, 6);
            Pump(surface);
            Require(surface.IsMinimized, "Minimize did not take effect.");
            CheckReleased(surface);
            ShowWindow(surface.WindowHandle, 9);
            Focus(surface);
            checks.Add("minimize releases cursor");

            camera.Look(0, -100000);
            Require(float.IsFinite(camera.Forward.Y) && camera.Forward.Y < 1, "Pitch clamp is invalid.");
            camera.Look(0, 100000);
            Require(camera.Forward.Y > -1, "Downward pitch clamp is invalid.");
            checks.Add("pitch limits");
            Console.WriteLine($"Fly camera native input/lifecycle checks passed: {checks.Count}.");
            return checks;
        }
        finally
        {
            surface.ReleaseMouseLook();
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    public static void VerifyCapturedClose(NativeSurface surface)
    {
        Focus(surface);
        surface.CaptureMouseLook();
        Require(surface.MouseLookCaptured, "Close verification could not capture the cursor.");
        Post(surface, 0x0010, 0);
        surface.PumpMessages();
        Require(surface.IsCloseRequested, "WM_CLOSE did not request shutdown.");
        CheckReleased(surface);
    }

    private static void MoveWithKeys(NativeSurface surface, FlyCamera camera, ushort[] keys)
    {
        var pressed = new List<ushort>();
        try
        {
            foreach (ushort key in keys)
            {
                Require(surface.IsInputActive && (GetAsyncKeyState(key) & 0x8000) == 0, "Input target changed or test key is already held.");
                Send(new Input { Type = 1, Key = key });
                pressed.Add(key);
            }
            Pump(surface);
            Require(surface.IsInputActive, "Input focus changed during keyboard verification.");
            foreach (ushort key in keys) Require((GetAsyncKeyState(key) & 0x8000) != 0, $"Injected key 0x{key:X} is not held.");
            surface.UpdateFlyCamera(camera, 0.1f);
        }
        finally
        {
            foreach (ushort key in pressed) Send(new Input { Type = 1, Key = key, KeyFlags = 2 });
            Pump(surface);
        }
    }

    private static void Focus(NativeSurface surface)
    {
        Activate(surface.WindowHandle);
        Pump(surface);
        Require(surface.IsInputActive, "Could not activate the demo for input verification.");
    }

    private static void Activate(nint window)
    {
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
    }

    private static void Pump(NativeSurface surface) { Thread.Sleep(30); surface.PumpMessages(); }
    private static void Post(NativeSurface surface, uint message, uint parameter)
    {
        if (!PostMessageW(surface.WindowHandle, message, parameter, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void Send(Input input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void CheckClip(NativeSurface surface)
    {
        Require(GetClipCursor(out var clip), "Could not read capture bounds.");
        Require(GetClientRect(surface.WindowHandle, out var client), "Could not read client bounds.");
        var corner = new Point { X = client.Right, Y = client.Bottom };
        var origin = new Point();
        Require(ClientToScreen(surface.WindowHandle, ref origin) && ClientToScreen(surface.WindowHandle, ref corner), "Could not map capture bounds.");
        Require(clip.Left == origin.X && clip.Top == origin.Y && clip.Right == corner.X && clip.Bottom == corner.Y,
            "Cursor clip does not match the client area.");
    }

    private static void CheckReleased(NativeSurface surface)
    {
        Require(!surface.MouseLookCaptured && GetCapture() != surface.WindowHandle, "Mouse capture was not released.");
        Require(GetClipCursor(out var clip), "Could not read released cursor bounds.");
        Require(clip.Left == GetSystemMetrics(76) && clip.Top == GetSystemMetrics(77) &&
            clip.Right == clip.Left + GetSystemMetrics(78) && clip.Bottom == clip.Top + GetSystemMetrics(79),
            "Cursor is still clipped after release.");
        var cursor = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        Require(GetCursorInfo(ref cursor) && (cursor.Flags & 1) != 0, "Cursor is still hidden after release.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int MouseX;
        [FieldOffset(12)] public int MouseY;
        [FieldOffset(16)] public uint MouseData;
        [FieldOffset(20)] public uint MouseFlags;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint KeyFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public uint Size, Flags; public nint Cursor; public Point Position; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint thread, uint otherThread, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo cursor);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out Rectangle rectangle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rectangle rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(nint window, uint message, nuint parameter, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}
