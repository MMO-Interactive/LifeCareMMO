using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.GUI;
using Shared.Contracts.Rendering;

/// <summary>
/// Converts Engine.GUI commands to ordered GPU triangles. GDI is used only when a
/// new font glyph enters the coverage atlas; live frames never rasterize a canvas.
/// </summary>
internal sealed class GuiDx12Composer : IGuiTextMeasurer, IDisposable
{
    internal const int AtlasSize = 2048;
    internal static readonly Vector2 SolidUv = new(0.5f / AtlasSize, 0.5f / AtlasSize);
    private readonly Dictionary<FontKey, FontData> fonts = [];
    private readonly byte[] atlasPixels = new byte[AtlasSize * AtlasSize * 4];
    private readonly string atlasId = "mmoi/gui-glyphs/" + Guid.NewGuid().ToString("N");
    private readonly nint fontDc;
    private int nextX = 2, nextY = 4, rowHeight;
    private long atlasRevision = 1;
    private RenderUiAtlas? publishedAtlas;
    private bool disposed;

    public GuiDx12Composer()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The UI font atlas requires Windows GDI.");
        fontDc = CreateCompatibleDC(0);
        if (fontDc == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the UI font atlas context.");
        // A stable white texel also supplies every untextured shape in the UI pass.
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
        {
            int p = (y * AtlasSize + x) * 4;
            atlasPixels[p] = atlasPixels[p + 1] = atlasPixels[p + 2] = atlasPixels[p + 3] = 255;
        }
    }

    /// <summary>
    /// Composes the frame. <paramref name="extra"/> GPU geometry is drawn after the commands before
    /// <paramref name="topLayerStart"/> and before the rest, so windows in the top layer cover it; <paramref name="extraTop"/>
    /// is drawn last, for geometry that belongs to a top-layer window.
    /// </summary>
    public RenderUiFrame Build(GuiFrame frame, long revision, Action<GuiDx12MeshBuilder>? extra = null,
        Action<GuiDx12MeshBuilder>? extraTop = null, int topLayerStart = int.MaxValue)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        int width = Math.Max(1, (int)Math.Ceiling(frame.ViewportSize.Width));
        int height = Math.Max(1, (int)Math.Ceiling(frame.ViewportSize.Height));
        var builder = new GuiDx12MeshBuilder(this, width, height);
        int index = 0; bool extraDone = false;
        foreach (var command in frame.DrawCommands)
        {
            if (index++ == topLayerStart) { extra?.Invoke(builder); extraDone = true; }
            switch (command)
            {
                case GuiFillRectangleCommand fill:
                    builder.FillRect(fill.Bounds, fill.Color, fill.CornerRadius);
                    break;
                case GuiBorderRectangleCommand border:
                    builder.BorderRect(border.Bounds, border.Color, border.Thickness, border.CornerRadius);
                    break;
                case GuiTextCommand text:
                    builder.Text(text.Text, text.Bounds, text.Style, text.Alignment, text.WrapMode);
                    break;
                case GuiImageCommand:
                    throw new NotSupportedException("This GPU UI composer supports shapes and font glyphs; image commands need an image atlas.");
            }
        }
        if (!extraDone) extra?.Invoke(builder);
        extraTop?.Invoke(builder);
        if (publishedAtlas is null || publishedAtlas.Revision != atlasRevision)
            publishedAtlas = new(atlasId, atlasRevision, AtlasSize, AtlasSize, (byte[])atlasPixels.Clone());
        return new(width, height, revision, builder.Complete(), publishedAtlas);
    }

    public GuiSize Measure(string text, GuiTextStyle style, GuiSize availableSize, GuiTextWrapMode wrapMode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrEmpty(text)) return GuiSize.Zero;
        var font = GetFont(style);
        var lines = LayoutText(text, font, availableSize.Width, wrapMode);
        float width = lines.Max(line => line.Width);
        float height = lines.Count * LineHeight(font, style);
        return new(availableSize.Width > 0 ? Math.Min(width, availableSize.Width) : width,
            availableSize.Height > 0 ? Math.Min(height, availableSize.Height) : height);
    }

    internal void AppendText(GuiDx12MeshBuilder builder, string text, GuiRect bounds,
        GuiTextStyle style, GuiTextAlignment alignment, GuiTextWrapMode wrapMode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0 || style.Color.A == 0) return;
        var font = GetFont(style);
        var lines = LayoutText(text, font, bounds.Width, wrapMode);
        float lineHeight = LineHeight(font, style), y = bounds.Y;
        foreach (var line in lines)
        {
            if (y >= bounds.Bottom) break;
            float x = bounds.X + (alignment switch
            {
                GuiTextAlignment.Center => (bounds.Width - line.Width) * .5f,
                GuiTextAlignment.Right => bounds.Width - line.Width,
                _ => 0,
            });
            foreach (char character in line.Text)
            {
                var glyph = GetGlyph(font, character);
                if (glyph.Width > 0 && glyph.Height > 0)
                    builder.Glyph(new(x + glyph.BearingX, y + font.Ascent - glyph.BearingY,
                        glyph.Width, glyph.Height), glyph.Uv, style.Color, bounds);
                x += glyph.Advance;
            }
            y += lineHeight;
        }
    }

    private static float LineHeight(FontData font, GuiTextStyle style) =>
        Math.Max(font.Height, font.PixelSize * Math.Max(1, style.LineHeightMultiplier));

    private FontData GetFont(GuiTextStyle style)
    {
        string family = string.IsNullOrWhiteSpace(style.FontFamily) || style.FontFamily.Equals("UI", StringComparison.OrdinalIgnoreCase)
            ? "Segoe UI" : style.FontFamily;
        int pixelSize = Math.Clamp((int)Math.Round(style.FontSize * 96 / 72), 10, 128);
        var key = new FontKey(family, pixelSize);
        if (fonts.TryGetValue(key, out var cached)) return cached;
        nint handle = CreateFontW(-pixelSize, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, family);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create UI font " + family);
        nint previous = SelectObject(fontDc, handle);
        try
        {
            if (!GetTextMetricsW(fontDc, out var metrics))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot measure UI font " + family);
            var font = new FontData(handle, pixelSize, metrics.Ascent, metrics.Height, metrics.AverageCharWidth);
            fonts.Add(key, font);
            return font;
        }
        catch { SelectObject(fontDc, previous); DeleteObject(handle); throw; }
        finally { SelectObject(fontDc, previous); }
    }

    private Glyph GetGlyph(FontData font, char character)
    {
        if (char.IsControl(character) || char.IsSurrogate(character)) character = '?';
        if (font.Glyphs.TryGetValue(character, out var glyph)) return glyph;
        nint previous = SelectObject(fontDc, font.Handle);
        try
        {
            var transform = Matrix2.Identity;
            uint byteCount = GetGlyphOutlineW(fontDc, character, 6, out var metrics, 0, 0, ref transform); // GGO_GRAY8_BITMAP
            if (byteCount == uint.MaxValue)
            {
                if (character != '?') return font.Glyphs[character] = GetGlyph(font, '?');
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot rasterize UI font glyphs.");
            }
            int width = checked((int)metrics.BlackBoxWidth), height = checked((int)metrics.BlackBoxHeight);
            // GDI can report a nominal black box for whitespace with no bitmap.
            if (byteCount == 0) width = height = 0;
            var uv = GuiRect.Empty;
            if (width > 0 && height > 0)
            {
                var location = Allocate(width, height);
                nint buffer = Marshal.AllocHGlobal(checked((int)byteCount));
                try
                {
                    if (GetGlyphOutlineW(fontDc, character, 6, out metrics, byteCount, buffer, ref transform) == uint.MaxValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read UI glyph coverage.");
                    byte[] coverage = new byte[byteCount];
                    Marshal.Copy(buffer, coverage, 0, coverage.Length);
                    int pitch = (width + 3) & ~3;
                    if (coverage.Length < checked(pitch * height))
                        throw new InvalidOperationException($"GDI returned an incomplete coverage bitmap for UI glyph U+{(int)character:X4}.");
                    for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int target = ((location.Y + y) * AtlasSize + location.X + x) * 4;
                        atlasPixels[target] = atlasPixels[target + 1] = atlasPixels[target + 2] = 255;
                        atlasPixels[target + 3] = (byte)Math.Min(255, (coverage[y * pitch + x] * 255 + 32) / 64);
                    }
                    uv = new((float)location.X / AtlasSize, (float)location.Y / AtlasSize,
                        (float)width / AtlasSize, (float)height / AtlasSize);
                    atlasRevision++;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            float advance = metrics.CellAdvanceX;
            if (advance <= 0 && character == ' ') advance = Math.Max(1, font.AverageCharWidth);
            glyph = new(width, height, metrics.Origin.X, metrics.Origin.Y, Math.Max(0, advance), uv);
            font.Glyphs.Add(character, glyph);
            return glyph;
        }
        finally { SelectObject(fontDc, previous); }
    }

    private (int X, int Y) Allocate(int width, int height)
    {
        if (nextX + width + 2 > AtlasSize) { nextX = 2; nextY += rowHeight + 4; rowHeight = 0; }
        if (nextY + height + 2 > AtlasSize)
            throw new InvalidOperationException("The 2048-pixel UI glyph atlas is full; reduce the number of distinct fonts or font sizes.");
        var location = (nextX, nextY);
        nextX += width + 4;
        rowHeight = Math.Max(rowHeight, height);
        return location;
    }

    private List<TextLine> LayoutText(string text, FontData font, float width, GuiTextWrapMode wrap)
    {
        var lines = new List<TextLine>();
        foreach (string paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal).Split('\n'))
        {
            if (paragraph.Length == 0) { lines.Add(new("", 0)); continue; }
            int start = 0;
            while (start < paragraph.Length)
            {
                int end = start, lastSpace = -1;
                float measured = 0;
                while (end < paragraph.Length)
                {
                    float advance = GetGlyph(font, paragraph[end]).Advance;
                    if (wrap == GuiTextWrapMode.Wrap && width > 0 && measured + advance > width && end > start) break;
                    measured += advance;
                    if (char.IsWhiteSpace(paragraph[end])) lastSpace = end;
                    end++;
                }
                int nextStart = end;
                if (end < paragraph.Length && lastSpace > start)
                {
                    end = lastSpace;
                    nextStart = lastSpace + 1;
                }
                string content = paragraph[start..end];
                if (wrap == GuiTextWrapMode.Wrap) content = content.TrimEnd();
                float lineWidth = 0;
                foreach (char character in content) lineWidth += GetGlyph(font, character).Advance;
                lines.Add(new(content, lineWidth));
                start = nextStart;
                if (wrap == GuiTextWrapMode.Wrap)
                    while (start < paragraph.Length && char.IsWhiteSpace(paragraph[start])) start++;
            }
        }
        return lines;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var font in fonts.Values) DeleteObject(font.Handle);
        DeleteDC(fontDc);
        fonts.Clear();
    }

    private readonly record struct FontKey(string Family, int Pixels);
    private sealed record FontData(nint Handle, int PixelSize, int Ascent, int Height, int AverageCharWidth)
    {
        public Dictionary<char, Glyph> Glyphs { get; } = [];
    }
    private readonly record struct Glyph(int Width, int Height, int BearingX, int BearingY, float Advance, GuiRect Uv);
    private readonly record struct TextLine(string Text, float Width);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct GlyphMetrics
    {
        public uint BlackBoxWidth, BlackBoxHeight;
        public Point Origin;
        public short CellAdvanceX, CellAdvanceY;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Fixed { public ushort Fraction; public short Value; }
    [StructLayout(LayoutKind.Sequential)] private struct Matrix2
    {
        public Fixed M11, M12, M21, M22;
        public static Matrix2 Identity => new() { M11 = new() { Value = 1 }, M22 = new() { Value = 1 } };
    }
    [StructLayout(LayoutKind.Sequential)] private struct TextMetrics
    {
        public int Height, Ascent, Descent, InternalLeading, ExternalLeading, AverageCharWidth,
            MaximumCharWidth, Weight, Overhang, DigitizedAspectX, DigitizedAspectY;
        public ushort FirstChar, LastChar, DefaultChar, BreakChar;
        public byte Italic, Underlined, StruckOut, PitchAndFamily, CharSet;
    }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateFontW(
        int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTextMetricsW(nint dc, out TextMetrics metrics);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetGlyphOutlineW(
        nint dc, uint character, uint format, out GlyphMetrics metrics, uint byteCount, nint buffer, ref Matrix2 transform);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}

/// <summary>Ordered pixel-space geometry for an integrated DX12 UI pass.</summary>
internal sealed class GuiDx12MeshBuilder
{
    private readonly GuiDx12Composer composer;
    private readonly List<RenderUiVertex> vertices = [];
    private readonly GuiRect viewport;

    internal GuiDx12MeshBuilder(GuiDx12Composer composer, int width, int height)
    { this.composer = composer; viewport = new(0, 0, width, height); }

    internal RenderUiVertex[] Complete() => vertices.ToArray();

    public void FillRect(float x, float y, float width, float height, GuiColor color) => FillRect(new(x, y, width, height), color);

    public void FillRect(GuiRect bounds, GuiColor color, float cornerRadius = 0)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || color.A == 0) return;
        if (cornerRadius <= 0)
        {
            var clipped = Intersect(bounds, viewport);
            if (clipped.Width > 0 && clipped.Height > 0) Quad(clipped, GuiRect.Empty, color, textured: false);
            return;
        }
        var outline = RoundedOutline(bounds, cornerRadius);
        var center = new Vector2(bounds.X + bounds.Width * .5f, bounds.Y + bounds.Height * .5f);
        for (int i = 0; i < outline.Length; i++)
            Triangle(center, outline[i], outline[(i + 1) % outline.Length], color);
    }

    public void BorderRect(GuiRect bounds, GuiColor color, float thickness = 1, float cornerRadius = 0)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || thickness <= 0 || color.A == 0) return;
        thickness = Math.Min(thickness, Math.Min(bounds.Width, bounds.Height) * .5f);
        if (cornerRadius <= 0)
        {
            FillRect(bounds.X, bounds.Y, bounds.Width, thickness, color);
            FillRect(bounds.X, bounds.Bottom - thickness, bounds.Width, thickness, color);
            FillRect(bounds.X, bounds.Y + thickness, thickness, bounds.Height - thickness * 2, color);
            FillRect(bounds.Right - thickness, bounds.Y + thickness, thickness, bounds.Height - thickness * 2, color);
            return;
        }
        var outside = RoundedOutline(bounds, cornerRadius);
        var inside = RoundedOutline(new(bounds.X + thickness, bounds.Y + thickness,
            bounds.Width - thickness * 2, bounds.Height - thickness * 2), Math.Max(0, cornerRadius - thickness));
        for (int i = 0; i < outside.Length; i++)
        {
            int next = (i + 1) % outside.Length;
            Triangle(outside[i], outside[next], inside[next], color);
            Triangle(outside[i], inside[next], inside[i], color);
        }
    }

    public void Line(float x1, float y1, float x2, float y2, GuiColor color, float thickness = 1)
    {
        if (color.A == 0 || thickness <= 0) return;
        var start = new Vector2(x1, y1); var end = new Vector2(x2, y2);
        var delta = end - start;
        if (delta.LengthSquared() < 0.0001f) { Disc(x1, y1, thickness * .5f, color); return; }
        Vector2 normal = new Vector2(-delta.Y, delta.X) * (thickness * .5f / delta.Length());
        Triangle(start - normal, end - normal, end + normal, color);
        Triangle(start - normal, end + normal, start + normal, color);
    }

    public void Circle(float centerX, float centerY, float radius, GuiColor color, float thickness = 1)
    {
        if (radius <= 0 || thickness <= 0 || color.A == 0) return;
        if (thickness >= radius * 2) { Disc(centerX, centerY, radius, color); return; }
        int segments = Math.Clamp((int)Math.Ceiling(radius * 1.2), 24, 192);
        float inner = Math.Max(0, radius - thickness * .5f), outer = radius + thickness * .5f;
        var center = new Vector2(centerX, centerY);
        for (int i = 0; i < segments; i++)
        {
            var a = Direction(i * MathF.Tau / segments); var b = Direction((i + 1) * MathF.Tau / segments);
            Triangle(center + a * outer, center + b * outer, center + b * inner, color);
            Triangle(center + a * outer, center + b * inner, center + a * inner, color);
        }
    }

    /// <summary>Vertices emitted so far; map layers use it to stay inside the renderer's UI vertex budget.</summary>
    public int VertexCount => vertices.Count;

    /// <summary>A small octagonal dot (24 vertices), for dense map markers where a smooth disc would be wasted.</summary>
    public void Dot(float centerX, float centerY, float radius, GuiColor color)
    {
        if (radius <= 0 || color.A == 0) return;
        var center = new Vector2(centerX, centerY);
        for (int i = 0; i < 8; i++)
            Triangle(center, center + Direction(i * MathF.Tau / 8) * radius, center + Direction((i + 1) * MathF.Tau / 8) * radius, color);
    }

    public void Disc(float centerX, float centerY, float radius, GuiColor color)
    {
        if (radius <= 0 || color.A == 0) return;
        int segments = Math.Clamp((int)Math.Ceiling(radius * 2), 16, 128);
        var center = new Vector2(centerX, centerY);
        for (int i = 0; i < segments; i++)
            Triangle(center, center + Direction(i * MathF.Tau / segments) * radius,
                center + Direction((i + 1) * MathF.Tau / segments) * radius, color);
    }

    public void Text(string text, GuiRect bounds, GuiTextStyle style,
        GuiTextAlignment alignment = GuiTextAlignment.Left, GuiTextWrapMode wrapMode = GuiTextWrapMode.NoWrap)
        => composer.AppendText(this, text, bounds, style, alignment, wrapMode);

    public void Text(string text, float x, float y, float width, float height, float pointSize, GuiColor color,
        string family = "Bahnschrift", GuiTextAlignment alignment = GuiTextAlignment.Left, GuiTextWrapMode wrapMode = GuiTextWrapMode.NoWrap)
        => Text(text, new(x, y, width, height), new(family, pointSize, 1.1f, color), alignment, wrapMode);

    internal void Glyph(GuiRect bounds, GuiRect uv, GuiColor color, GuiRect clip)
    {
        var visible = Intersect(Intersect(bounds, clip), viewport);
        if (visible.Width <= 0 || visible.Height <= 0) return;
        var clippedUv = new GuiRect(uv.X + (visible.X - bounds.X) / bounds.Width * uv.Width,
            uv.Y + (visible.Y - bounds.Y) / bounds.Height * uv.Height,
            visible.Width / bounds.Width * uv.Width, visible.Height / bounds.Height * uv.Height);
        Quad(visible, clippedUv, color, textured: true);
    }

    private void Quad(GuiRect bounds, GuiRect uv, GuiColor color, bool textured)
    {
        var tint = Tint(color);
        var topLeft = new RenderUiVertex(new(bounds.X, bounds.Y), textured ? new(uv.X, uv.Y) : GuiDx12Composer.SolidUv, tint);
        var topRight = new RenderUiVertex(new(bounds.Right, bounds.Y), textured ? new(uv.Right, uv.Y) : GuiDx12Composer.SolidUv, tint);
        var bottomLeft = new RenderUiVertex(new(bounds.X, bounds.Bottom), textured ? new(uv.X, uv.Bottom) : GuiDx12Composer.SolidUv, tint);
        var bottomRight = new RenderUiVertex(new(bounds.Right, bounds.Bottom), textured ? new(uv.Right, uv.Bottom) : GuiDx12Composer.SolidUv, tint);
        vertices.Add(topLeft); vertices.Add(topRight); vertices.Add(bottomRight);
        vertices.Add(topLeft); vertices.Add(bottomRight); vertices.Add(bottomLeft);
    }

    private void Triangle(Vector2 a, Vector2 b, Vector2 c, GuiColor color)
    {
        var tint = Tint(color);
        vertices.Add(new(a, GuiDx12Composer.SolidUv, tint));
        vertices.Add(new(b, GuiDx12Composer.SolidUv, tint));
        vertices.Add(new(c, GuiDx12Composer.SolidUv, tint));
    }

    private static Vector4 Tint(GuiColor color) => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    private static Vector2 Direction(float angle) => new(MathF.Cos(angle), MathF.Sin(angle));
    private static GuiRect Intersect(GuiRect a, GuiRect b)
    {
        float x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        return new(x, y, Math.Max(0, Math.Min(a.Right, b.Right) - x), Math.Max(0, Math.Min(a.Bottom, b.Bottom) - y));
    }
    private static Vector2[] RoundedOutline(GuiRect bounds, float radius)
    {
        radius = Math.Clamp(radius, 0, Math.Max(0, Math.Min(bounds.Width, bounds.Height) * .5f));
        const int steps = 8;
        var outline = new Vector2[4 * (steps + 1)];
        Vector2[] centers = [new(bounds.Right - radius, bounds.Y + radius), new(bounds.Right - radius, bounds.Bottom - radius),
            new(bounds.X + radius, bounds.Bottom - radius), new(bounds.X + radius, bounds.Y + radius)];
        for (int corner = 0; corner < 4; corner++)
        for (int step = 0; step <= steps; step++)
            outline[corner * (steps + 1) + step] = centers[corner] + Direction(-MathF.PI / 2 + corner * MathF.PI / 2 + step * MathF.PI / 2 / steps) * radius;
        return outline;
    }
}
