using Engine.GUI;
using Theme = StormChaseHud.Theme;

/// <summary>
/// Shared look for the account/front-end pages and the pause menu, matching the chase HUD:
/// Bahnschrift type, translucent glass cards, a filled primary action and quiet ghost actions.
/// Sizes are authored in logical pixels and multiplied by <see cref="K"/> so large displays get
/// proportionally larger, still pixel-sharp controls.
/// </summary>
internal sealed class ChaseMenuKit(float k, List<GuiElement> items)
{
    public enum Kind { Primary, Secondary, Ghost }
    public float K { get; } = k;
    public List<GuiElement> Items { get; } = items;

    public static float ScaleFor(int width, int height) => Math.Clamp(Math.Min(width / 1440f, height / 810f), 1f, 2.2f);

    public static GuiElement Fill(float x, float y, float width, float height, GuiColor color, float radius = 0) => new GuiOverlayPanel
    {
        Translation = new(x, y), Width = GuiLength.Pixels(width), Height = GuiLength.Pixels(height), BackgroundColor = color, CornerRadius = radius,
    };

    public GuiLabel Text(string text, float x, float y, float width, float size, GuiColor color, bool bold = false,
        GuiTextAlignment align = GuiTextAlignment.Left, bool wrap = false, float lines = 1)
    {
        var label = new GuiLabel
        {
            Text = text, Translation = new(x * K, y * K), Width = GuiLength.Pixels(width * K), Height = GuiLength.Pixels(size * 1.75f * lines * K),
            TextAlignment = align, WrapMode = wrap ? GuiTextWrapMode.Wrap : GuiTextWrapMode.NoWrap,
            Style = new(bold ? Theme.Bold : Theme.Font, size * K, 1.2f, color),
        };
        Items.Add(label);
        return label;
    }

    public GuiButton Button(string id, string text, float x, float y, float width, Kind kind = Kind.Secondary, float height = 46,
        bool enabled = true, bool selected = false, GuiTextAlignment align = GuiTextAlignment.Center, float size = 11.5f)
    {
        GuiColor background = kind switch { Kind.Primary => Theme.Accent, Kind.Ghost => new(255, 255, 255, 0), _ => new(255, 255, 255, 16) };
        GuiColor hover = kind switch { Kind.Primary => new(124, 240, 216), Kind.Ghost => new(255, 255, 255, 18), _ => new(255, 255, 255, 34) };
        GuiColor border = selected ? Theme.Accent : kind switch { Kind.Primary => Theme.Accent, Kind.Ghost => new(255, 255, 255, 34), _ => new(255, 255, 255, 40) };
        var ink = kind == Kind.Primary ? new GuiColor(6, 20, 22) : kind == Kind.Ghost ? Theme.Muted : Theme.Ink;
        if (!enabled) { background = new(255, 255, 255, 8); hover = background; border = new(255, 255, 255, 20); ink = Theme.Faint; }
        var button = new GuiButton
        {
            Id = id, Text = text, Translation = new(x * K, y * K), Width = GuiLength.Pixels(width * K), Height = GuiLength.Pixels(height * K),
            // GuiButton draws its label at the top padding; centre it vertically.
            IsEnabled = enabled, TextAlignment = align, Padding = new(18 * K, Math.Max(2, (height - size * 1.45f) / 2) * K), CornerRadius = 10 * K, BorderThickness = Math.Max(1, K),
            TextStyle = new(Theme.Bold, size * K, 1.1f, ink), BackgroundColor = selected && kind != Kind.Primary ? new(84, 226, 198, 30) : background,
            HoverBackgroundColor = hover, PressedBackgroundColor = new(84, 226, 198, 120), BorderColor = border,
        };
        Items.Add(button);
        return button;
    }

    /// <summary>A labelled text field; the caret overlay uses <see cref="FieldTextInset"/> and <see cref="FieldTextSize"/>.</summary>
    public void Field(string id, string caption, string value, float x, float y, float width, bool password = false, bool enabled = true)
    {
        Text(caption.ToUpperInvariant(), x, y, width, 8.5f, Theme.Muted, bold: true);
        Items.Add(new GuiTextField
        {
            Id = id, Text = value, PlaceholderText = caption, IsPassword = password, IsEnabled = enabled,
            Translation = new(x * K, (y + 20) * K), Width = GuiLength.Pixels(width * K), Height = GuiLength.Pixels(46 * K),
            Padding = new(FieldTextInset * K, 12 * K), CornerRadius = 10 * K, BorderThickness = Math.Max(1, K),
            TextStyle = new(Theme.Font, FieldTextSize * K, 1.1f, Theme.Ink), BackgroundColor = new(2, 8, 13, 210), BorderColor = new(255, 255, 255, 46),
        });
    }

    public const float FieldTextInset = 16, FieldTextSize = 12;

    /// <summary>A full-width selectable row with left and right text (server or character lists).</summary>
    public void Row(string id, string left, string right, float x, float y, float width, bool selected, GuiColor? rightColor = null)
    {
        Button(id, "", x, y, width, Kind.Secondary, 46, selected: selected);
        Text(left, x + 18, y + 12, width * .62f, 11.5f, Theme.Ink, bold: true);
        Text(right, x + width * .62f, y + 13, width * .38f - 18, 10, rightColor ?? Theme.Muted, align: GuiTextAlignment.Right);
    }

    /// <summary>The live backdrop is darkened, more strongly on the brand side, with a smooth fade.</summary>
    public static IEnumerable<GuiElement> Backdrop(float width, float height, bool brandSide)
    {
        yield return Fill(0, 0, width, height, new(2, 8, 14, 96));
        if (!brandSide) yield break;
        // Non-overlapping strips on whole-pixel edges: overlapping translucent strips showed seams.
        const int strips = 32;
        float band = width * .62f;
        for (int i = 0; i < strips; i++)
        {
            float t = i / (float)strips;
            int x0 = (int)MathF.Round(band * i / strips), x1 = (int)MathF.Round(band * (i + 1) / strips);
            yield return Fill(x0, 0, x1 - x0, height, new(2, 8, 14, (byte)(150 * (1 - t) * (1 - t))));
        }
    }
}
