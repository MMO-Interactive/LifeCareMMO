/// <summary>
/// The few world constants the weather model reads, without pulling the renderer-backed world into the climate process.
/// They must equal the constants in Client/PmWeather.Showcase/StormChaseWorldMap.cs; the climate probe checks this.
/// </summary>
internal static class StormChaseWorld
{
    public const float PlayableExtentMeters = 49152;
    public const float PlayableCenterZ = 1000;
    public const float GullHarborX = 13800, GullHarborZ = -18340;
}
