using System.Runtime.InteropServices;
using System.Diagnostics;
using Engine.GUI;
using Shared.Contracts.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StormChase.Protocol;

internal sealed record StormChaseTelemetry(string Phase, float PeakWind, float Width, float Distance,
    string LocalWeather, float Influence, double Fps, double GpuMilliseconds, string CameraMode,
    string TimeOfDay, int BrokenParts, int ProbeCount, int Photos, bool Muted, string ProbeReading = "",
    int FunnelEpisode = 0, int Touchdowns = 0, bool HasFunnel = true, StormChaseCampaignTelemetry? Campaign = null,
    StormLocalConditions? LocalStorm = null, float Gust = 1);

internal sealed record StormChaseCampaignTelemetry(int Day, float Hour, string Forecast, string ForecastSummary,
    float Risk, int StormCount, decimal Cash, string VehicleName, float SpeedMph, float Fuel, float FuelCapacity,
    float Health, bool Anchored, bool ShopOpen, int ShopSelection, string Message, bool Refueling,
    IReadOnlyList<StormChaseSettlement> Settlements, IReadOnlyList<StormChaseGasStation> Stations,
    IReadOnlyList<ChaseFieldProbe> Probes, Shared.Contracts.World.Vector3f VehiclePosition,
    IReadOnlyList<StormChaseRoadRoute>? Roads = null, int Popularity = 0,
    MissionState? Mission = null, ActivityState[]? Activities = null, bool DispatchOpen = false,
    AtmosphereSnapshot? Atmosphere = null, StormChaseBoundary? Boundary = null, string Job = StormChaseJobs.Chaser, int? Seed = null);

/// <summary>Engine.GUI layout and interaction submitted with the native game frame.</summary>
internal sealed partial class StormChaseHud : IDisposable
{
    private readonly GuiDx12Composer composer = new();
    private readonly GuiLayoutEngine layout;
    private readonly Dictionary<(string, int), nint> fonts = new();
    private nint dc, bitmap, previousBitmap, bits;
    private long revision;
    private int width, height;
    private byte[] pixels = [];
    private Shared.Contracts.World.Vector3f observerPosition;
    private readonly StormChaseRadarCache radarCache = new();
    private readonly Stopwatch radarClock = Stopwatch.StartNew();
    public bool Visible { get; set; } = true;
    public bool RegionalMap { get; set; }
    public bool ForecastOpen { get; set; }
    public StormChaseRadarProduct RadarProduct { get; set; }
    public GuiFrame LastFrame { get; private set; } = GuiFrame.Empty;
    public RenderUiFrame? Ui { get; private set; }
    /// <summary>Extra chase-screen elements (LifeCare build strip and pie wheel), in HUD pixels.</summary>
    public Func<int, int, IEnumerable<GuiElement>>? WorldOverlay { get; set; }

    public StormChaseHud(nint owner)
    {
        layout = new(composer);
    }

    public void Update(NativeSurface surface, StormChaseTelemetry telemetry, RenderFrame frame, GuiInputState? input = null)
    {
        if (!Visible || surface.IsMinimized || surface.IsCloseRequested || surface.Width < 200 || surface.Height < 150)
        { Ui = null; LastFrame = GuiFrame.Empty; return; }
        int w = surface.Width, h = surface.Height;
        // The chase HUD scales itself in real pixels (sharp glyphs at any resolution). The
        // modal pages keep their original fixed layout, shrunk on small windows as before.
        bool modal = HousingOpen || telemetry.Campaign?.DispatchOpen == true || (ForecastOpen && telemetry.Campaign is not null);
        float scale = modal ? Math.Min(1, Math.Min(w / 960f, h / 720f)) : 1;
        if (scale < 1)
        {
            w = (int)Math.Ceiling(w / scale); h = (int)Math.Ceiling(h / scale);
            if (input is { } guiInput) input = new(guiInput.Pointer with { X = guiInput.Pointer.X / scale, Y = guiInput.Pointer.Y / scale });
        }
        observerPosition = frame.Camera.Position;
        if (!modal && !MapOpen) ProcessDrag(input ?? GuiInputState.Empty, HudScale(w, h));
        LastFrame = layout.BuildFrame(BuildDocument(w, h, telemetry), new(w, h), input ?? GuiInputState.Empty, LastFrame.Interaction);
        // Windows the player opens are a top layer: the base HUD's GPU geometry (minimap, compass) is drawn before them.
        int topLayer = int.MaxValue;
        for (int i = 0; i < LastFrame.DrawCommands.Count; i++)
            if (LastFrame.DrawCommands[i] is GuiFillRectangleCommand { Color: var marker } && marker == TopLayerMarker) { topLayer = i; break; }
        bool chase = !modal && !MapOpen && telemetry.Campaign?.ShopOpen != true;
        Ui = composer.Build(LastFrame, ++revision, mesh =>
        {
            if (ForecastOpen && !HousingOpen && telemetry.Campaign?.ShopOpen != true && telemetry.Campaign?.DispatchOpen != true
                && telemetry.Campaign?.Atmosphere is { } atmosphere)
                DrawGpuHodograph(mesh, w, h, atmosphere);
            else if (MapOpen && World is not null && !HousingOpen && telemetry.Campaign?.DispatchOpen != true)
                DrawWorldMapInstruments(mesh, frame);
            else if (!modal && telemetry.Campaign?.ShopOpen != true)
                DrawInstruments(mesh, frame);
        }, chase && PhoneOpen && layoutForInstruments is { } phoneLayout && telemetryForInstruments is { } phoneTelemetry
            ? mesh => DrawPhoneInstruments(mesh, phoneLayout, phoneTelemetry, frame) : null, chase ? topLayer : int.MaxValue);
    }

    public bool HandleWeatherGuiAction(string action)
    {
        switch (action)
        {
            case "radar/reflectivity": RadarProduct = StormChaseRadarProduct.Reflectivity; return true;
            case "radar/velocity": RadarProduct = StormChaseRadarProduct.RadialVelocity; return true;
            case "radar/toggle": RadarProduct = RadarProduct == StormChaseRadarProduct.Reflectivity
                ? StormChaseRadarProduct.RadialVelocity : StormChaseRadarProduct.Reflectivity; return true;
            case "forecast/toggle": ForecastOpen = !ForecastOpen; return true;
            case "forecast/close": ForecastOpen = false; return true;
            default: return false;
        }
    }

    public void SaveOverlay(string path, int w, int h, StormChaseTelemetry telemetry, RenderFrame frame)
    {
        Rasterize(w, h, telemetry, frame);
        using var image = Image.LoadPixelData<Bgra32>(pixels, width, height);
        image.SaveAsPng(path);
    }

    private void Rasterize(int w, int h, StormChaseTelemetry t, RenderFrame frame)
    {
        observerPosition = frame.Camera.Position;
        // Retained only for the historical diagnostic -instruments PNG export.
        // Live HUD drawing uses GPU geometry and never creates a desktop window.
        if (dc == 0) dc = CreateCompatibleDC(0);
        EnsureBitmap(w, h);
        Array.Clear(pixels);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        LastFrame = layout.BuildFrame(BuildDocument(w, h, t), new(w, h), GuiInputState.Empty);
        SetBkMode(dc, 1);
        foreach (var command in LastFrame.DrawCommands)
        {
            var bounds = new Rect((int)command.Bounds.X, (int)command.Bounds.Y,
                (int)command.Bounds.Right, (int)command.Bounds.Bottom);
            if (command is GuiFillRectangleCommand fill)
            {
                var brush = CreateSolidBrush(Color(fill.Color));
                FillRect(dc, ref bounds, brush);
                DeleteObject(brush);
            }
            else if (command is GuiBorderRectangleCommand border)
            {
                var pen = CreatePen(0, Math.Max(1, (int)border.Thickness), Color(border.Color));
                var oldPen = SelectObject(dc, pen);
                var oldBrush = SelectObject(dc, GetStockObject(5));
                Rectangle(dc, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                SelectObject(dc, oldPen); SelectObject(dc, oldBrush); DeleteObject(pen);
            }
            else if (command is GuiTextCommand text)
            {
                int size = Math.Max(10, (int)Math.Round(text.Style.FontSize * 96 / 72));
                var key = (text.Style.FontFamily, size);
                if (!fonts.TryGetValue(key, out var font))
                {
                    font = CreateFontW(-size, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0,
                        text.Style.FontFamily == "UI" ? "Segoe UI" : text.Style.FontFamily);
                    fonts[key] = font;
                }
                var oldFont = SelectObject(dc, font);
                SetTextColor(dc, Color(text.Style.Color));
                uint flags = text.WrapMode == GuiTextWrapMode.NoWrap ? 0x20u | 0x800u : 0x10u;
                if (text.Alignment == GuiTextAlignment.Center) flags |= 1;
                if (text.Alignment == GuiTextAlignment.Right) flags |= 2;
                DrawTextW(dc, text.Text, text.Text.Length, ref bounds, flags | 0x100);
                SelectObject(dc, oldFont);
            }
        }
        if (w >= 700 && h >= 480) DrawRadar(w - 266, h - 274, 242, frame, t.Campaign);
        GdiFlush();
        Marshal.Copy(bits, pixels, 0, pixels.Length);
        // GDI authors RGB, so explicitly publish alpha for the drawn instrument panels.
        for (int p = 0; p < pixels.Length; p += 4)
            pixels[p + 3] = (pixels[p] | pixels[p + 1] | pixels[p + 2]) != 0 ? (byte)255 : (byte)0;
        Marshal.Copy(pixels, 0, bits, pixels.Length);
    }

    private GuiDocument BuildDocument(int w, int h, StormChaseTelemetry t)
    {
        if (HousingOpen) return BuildHousingDocument(w, h);
        if (MapOpen && World is not null) return BuildWorldMapDocument(w, h, t);
        if (t.Campaign is { DispatchOpen: true } dispatch) return BuildDispatchDocument(w, h, dispatch);
        if (ForecastOpen && t.Campaign is { } forecast) return BuildForecastDocument(w, h, forecast);
        return BuildChaseDocument(w, h, t);
    }

    private void DrawRadar(int x, int y, int size, RenderFrame frame, StormChaseCampaignTelemetry? career)
    {
        var sweep = GetRadarSweep(frame, career?.Atmosphere, career?.Boundary);
        float range = sweep.RangeMeters, centerX = sweep.CenterX, centerZ = sweep.CenterZ;
        int center = size / 2;
        for (int row = 0; row < 48; row++)
        for (int column = 0; column < 48; column++)
        {
            float dx = (column / 47f - 0.5f) * range * 2;
            float dz = (0.5f - row / 47f) * range * 2;
            if (dx * dx + dz * dz > range * range) continue;
            var color = RadarColor(sweep.Values[row * 48 + column], sweep.Product);
            var rect = new Rect(x + column * size / 48, y + row * size / 48,
                x + (column + 1) * size / 48 + 1, y + (row + 1) * size / 48 + 1);
            var brush = CreateSolidBrush(Color(color)); FillRect(dc, ref rect, brush); DeleteObject(brush);
        }
        var pen = CreatePen(0, 1, Color(new(54, 91, 94)));
        var oldPen = SelectObject(dc, pen);
        var oldBrush = SelectObject(dc, GetStockObject(5));
        for (int ring = 1; ring <= 3; ring++)
        {
            int radius = center * ring / 3;
            Ellipse(dc, x + center - radius, y + center - radius, x + center + radius, y + center + radius);
        }
        MoveToEx(dc, x, y + center, 0); LineTo(dc, x + size, y + center);
        MoveToEx(dc, x + center, y, 0); LineTo(dc, x + center, y + size);
        SelectObject(dc, oldPen); SelectObject(dc, oldBrush); DeleteObject(pen);
        bool MapPoint(Shared.Contracts.World.Vector3f position, out int mx, out int my)
        {
            mx = x + center + (int)((position.X - centerX) / range * center);
            my = y + center - (int)((position.Z - centerZ) / range * center);
            return mx >= x && mx < x + size && my >= y && my < y + size;
        }
        foreach (var signal in sweep.Signals)
        {
            if (!MapPoint(signal.Position, out int sx, out int sy)) continue;
            DrawMarker(sx, sy, RotationColor(signal.EstimatedWindMph), signal.SurfaceConfirmed ? 6 : 4);
        }
        if (career is not null)
        {
            var roadPen = CreatePen(0, 1, Color(new(90, 115, 121)));
            var previousRoadPen = SelectObject(dc, roadPen);
            foreach (var route in career.Roads ?? [])
                for (int i = 1; i < route.Points.Count; i++)
                    if (MapPoint(route.Points[i - 1], out int ax, out int ay) &&
                        MapPoint(route.Points[i], out int bx, out int by))
                    { MoveToEx(dc, ax, ay, 0); LineTo(dc, bx, by); }
            SelectObject(dc, previousRoadPen); DeleteObject(roadPen);
            var fontKey = ("Consolas", 11);
            if (!fonts.TryGetValue(fontKey, out var mapFont))
            {
                mapFont = CreateFontW(-11, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Consolas");
                fonts[fontKey] = mapFont;
            }
            var oldMapFont = SelectObject(dc, mapFont);
            SetTextColor(dc, Color(new(219, 231, 235)));
            foreach (var settlement in career.Settlements)
                if (MapPoint(settlement.Position, out int townX, out int townY))
                {
                    DrawMarker(townX, townY, new(162, 183, 190), 3);
                    var label = new Rect(Math.Clamp(townX - 32, x + 2, x + size - 104),
                        townY - 18, x + size - 2, townY - 2);
                    DrawTextW(dc, settlement.Name, settlement.Name.Length, ref label, 0x20 | 0x800 | 0x100);
                }
            SelectObject(dc, oldMapFont);
            foreach (var station in career.Stations)
                if (MapPoint(station.Position, out int sx, out int sy)) DrawMarker(sx, sy, new(255, 193, 67), 4);
            foreach (var probe in career.Probes)
                if (MapPoint(probe.Position, out int pxProbe, out int pyProbe)) DrawMarker(pxProbe, pyProbe,
                    probe.Destroyed ? new(120, 120, 120) : new(70, 220, 255), 3);
            if (MapPoint(career.VehiclePosition, out int vx, out int vy)) DrawMarker(vx, vy, new(235, 235, 255), 3);
        }
        int px = x + center + (int)((frame.Camera.Position.X - centerX) / range * center);
        int py = y + center - (int)((frame.Camera.Position.Z - centerZ) / range * center);
        if (px >= x && px < x + size && py >= y && py < y + size)
        {
            DrawMarker(px, py, new(81, 244, 215), 4);
            var headingPen = CreatePen(0, 2, Color(new(81, 244, 215)));
            var previous = SelectObject(dc, headingPen);
            MoveToEx(dc, px, py, 0);
            LineTo(dc, px + (int)(frame.Camera.Forward.X * 14), py - (int)(frame.Camera.Forward.Z * 14));
            SelectObject(dc, previous); DeleteObject(headingPen);
        }
    }

    private void DrawMarker(int x, int y, GuiColor color, int radius)
    {
        var brush = CreateSolidBrush(Color(color));
        var old = SelectObject(dc, brush);
        Ellipse(dc, x - radius, y - radius, x + radius, y + radius);
        SelectObject(dc, old); DeleteObject(brush);
    }

    private void EnsureBitmap(int w, int h)
    {
        if (width == w && height == h) return;
        if (bitmap != 0) { SelectObject(dc, previousBitmap); DeleteObject(bitmap); }
        width = w; height = h;
        pixels = new byte[checked(w * h * 4)];
        var info = new BitmapInfo { Header = new() { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32, Compression = 0 } };
        bitmap = CreateDIBSection(dc, ref info, 0, out bits, 0, 0);
        if (bitmap == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        previousBitmap = SelectObject(dc, bitmap);
    }

    public void Dispose()
    {
        composer.Dispose();
        foreach (var font in fonts.Values) DeleteObject(font);
        if (bitmap != 0) { SelectObject(dc, previousBitmap); DeleteObject(bitmap); }
        if (dc != 0) DeleteDC(dc);
        dc = bitmap = 0;
    }

    private static uint Color(GuiColor c) => (uint)(c.R | c.G << 8 | c.B << 16);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Size(int width, int height) { public int Width = width, Height = height; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect(int left, int top, int right, int bottom)
    { public int Left = left, Top = top, Right = right, Bottom = bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint Colors; }
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern nint CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charset, uint outprecision, uint clipprecision, uint quality, uint pitch, string face);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(nint dc, string text, int count, ref Rect rect, uint format);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("gdi32.dll")] private static extern bool Rectangle(nint dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(nint dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool MoveToEx(nint dc, int x, int y, nint old);
    [DllImport("gdi32.dll")] private static extern bool LineTo(nint dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
