using System.Globalization;

internal sealed record WeatherRenderSettings(string Name, float Scale, int Quality)
{
    public int LightSamples => Quality switch { 0 => 2, 1 => 3, 2 => 4, _ => 6 };

    public static WeatherRenderSettings Read(string[] args)
    {
        var name = Value(args, "--weather-quality")?.ToLowerInvariant() ?? "high";
        var preset = name switch
        {
            "ultra" => new WeatherRenderSettings("Ultra", 0.5f, 3),
            "high" => new WeatherRenderSettings("High", 0.25f, 2),
            "balanced" => new WeatherRenderSettings("Balanced", 0.25f, 1),
            "performance" => new WeatherRenderSettings("Performance", 0.1875f, 0),
            _ => throw new ArgumentException("--weather-quality must be ultra, high, balanced, or performance."),
        };
        if (Value(args, "--weather-scale") is { } scaleText)
        {
            if (!float.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
                || !float.IsFinite(scale) || scale < 0.125f || scale > 1f)
                throw new ArgumentException("--weather-scale must be between 0.125 and 1.");
            preset = preset with { Scale = scale };
        }
        return preset;
    }

    private static string? Value(string[] args, string flag)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {flag}.");
        return args[index + 1];
    }
}
