using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Engine.Rendering.Backend;
using Shared.Contracts.Rendering;

internal static partial class Showcase
{
    private static string? OptionalValue(string[] args, string option)
    {
        int index = Array.FindIndex(args, a => a.Equals(option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>One requested native PNG readback; all renderer state changes occur on its owning thread.</summary>
internal sealed class StormChasePhotos(string output, string staging, Action<ChasePhotoReceipt>? onSaved = null)
{
    private static readonly FieldInfo Completed = typeof(Dx12RenderBackend).GetField("frameExportCompleted", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private int pending, count;
    public int Count => Volatile.Read(ref count);
    public void Request() => Interlocked.CompareExchange(ref pending, 1, 0);
    public void BeforeFrame(IRenderBackend backend)
    {
        if (Interlocked.CompareExchange(ref pending, 2, 1) == 1) Completed.SetValue(backend, false);
    }
    public void AfterFrame(IRenderBackend backend, RenderFrame frame)
    {
        if (Volatile.Read(ref pending) != 2 || !(bool)Completed.GetValue(backend)!) return;
        int number = Interlocked.Increment(ref count);
        string directory = Path.Combine(output, "photographs");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"storm-chase-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{number:000}.png");
        File.Copy(staging, path, overwrite: true);
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
        {
            frame.FrameNumber, frame.AnimationTimeSeconds, frame.Camera, frame.Weather,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Saved storm photograph: " + path);
        onSaved?.Invoke(new(path, frame));
        Volatile.Write(ref pending, 0);
    }
}
