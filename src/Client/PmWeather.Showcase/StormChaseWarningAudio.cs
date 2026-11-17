using Engine.Audio.Assets;
using Engine.Audio.Runtime;
using Engine.Audio.Spatial;
using Shared.Contracts.World;

/// <summary>
/// Plays downloaded historical weather-radio speech and a recorded outdoor siren.
/// Uses the existing AudioEngine and its worker; it does not own an output device.
/// </summary>
internal sealed class StormChaseWarningAudio
{
    public const string RadioClipId = "storm-chase/warnings/radio-tornado-oklahoma";
    public const string SirenClipId = "storm-chase/warnings/siren-st-paul";
    private readonly AudioEngine engine;
    private AudioPlaybackHandle radio = AudioPlaybackHandle.None;
    private AudioPlaybackHandle siren = AudioPlaybackHandle.None;
    private bool previousWarning;
    private string? previousEpisodeId;
    private float previousSeconds = float.NaN;
    private float nextRadioSeconds = float.NegativeInfinity;
    private int radioWarningsPlayed;
    private int sirenActivations;

    public StormChaseWarningAudio(AudioEngine engine) => this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public int RadioWarningsPlayed => Volatile.Read(ref radioWarningsPlayed);
    public int SirenActivations => Volatile.Read(ref sirenActivations);
    public bool SirenActive => siren.IsValid;

    /// <summary>Register before initializing the shared engine. contentRoot may be the warning folder or workspace root.</summary>
    public static int Register(InMemoryAudioClipCatalog catalog, string? contentRoot = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        string root = RequireWarningRoot(contentRoot);
        RegisterClip(catalog, RadioClipId, Path.Combine(root, "WarningRadioOklahoma.wav"));
        RegisterClip(catalog, SirenClipId, Path.Combine(root, "WarningSirenStPaul.wav"));
        return 2;
    }

    /// <summary>
    /// Call from the audio worker using actual simulation warning status. episodeId
    /// distinguishes successive tornadoes from one parent storm. wallSeconds continues
    /// advancing when the simulation is paused, so a recording plays at normal speed.
    /// </summary>
    public void Update(bool warningActive, Vector3f sirenPosition, float wallSeconds, string? episodeId = null, bool sirenAllowed = true)
    {
        if (!float.IsFinite(wallSeconds)) return;
        if (float.IsFinite(previousSeconds) && wallSeconds < previousSeconds) Reset();
        previousSeconds = wallSeconds;
        bool newEpisode = warningActive && episodeId is not null && !string.Equals(previousEpisodeId, episodeId, StringComparison.Ordinal);
        if (warningActive && (!previousWarning || newEpisode) && wallSeconds >= nextRadioSeconds)
        { PlayRadioWarning(); nextRadioSeconds = wallSeconds + 30; previousEpisodeId = episodeId; }
        SetSirenActive(warningActive && sirenAllowed, sirenPosition);
        previousWarning = warningActive;
    }

    public AudioPlaybackHandle PlayRadioWarning()
    {
        // A new bulletin replaces the previous bulletin instead of queuing competing speech.
        if (radio.IsValid) engine.Stop(radio);
        radio = engine.PlayVoice(AudioClipReference.FromAssetId(RadioClipId), new()
        {
            Volume = .9f, Priority = 2f, Loop = false, Pitch = 1f,
            // Flat voice bus sounds like the chaser's receiver and remains intelligible.
            Emitter = null,
        });
        Interlocked.Increment(ref radioWarningsPlayed);
        return radio;
    }

    public void SetSirenActive(bool active, Vector3f position)
    {
        if (!active)
        {
            if (siren.IsValid) engine.Stop(siren, TimeSpan.FromSeconds(.8));
            siren = AudioPlaybackHandle.None;
            return;
        }
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) return;
        var emitter = new AudioEmitterState
        {
            Position = position, MinDistance = 180, MaxDistance = 5500,
            DistanceModel = AudioDistanceModel.InverseDistanceClamped,
            RolloffFactor = .45f, StereoPanAmount = .8f, DopplerFactor = 0,
        };
        // SetEmitter also detects a voice evicted by the shared engine's voice limit.
        if (siren.IsValid && engine.SetEmitter(siren, emitter)) return;
        siren = engine.PlayAmbient(AudioClipReference.FromAssetId(SirenClipId), new()
        {
            Volume = .68f, Priority = 1.2f, Loop = true, Pitch = 1f,
            FadeInDuration = TimeSpan.FromSeconds(1), Emitter = emitter,
        });
        Interlocked.Increment(ref sirenActivations);
    }

    /// <summary>Clear on a manual scenario reset or before disposing the owning audio engine.</summary>
    public void Reset()
    {
        if (radio.IsValid) engine.Stop(radio);
        if (siren.IsValid) engine.Stop(siren);
        radio = AudioPlaybackHandle.None;
        siren = AudioPlaybackHandle.None;
        previousWarning = false;
        previousEpisodeId = null;
        previousSeconds = float.NaN;
        nextRadioSeconds = float.NegativeInfinity;
    }

    private static string RequireWarningRoot(string? contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(contentRoot))
        {
            string explicitRoot = Path.GetFullPath(contentRoot);
            foreach (string candidate in new[] { explicitRoot,
                         Path.Combine(explicitRoot, "Assets", "StormChase", "Audio", "Warnings"),
                         Path.Combine(explicitRoot, "Audio", "Warnings") })
                if (HasClips(candidate)) return candidate;
            throw new DirectoryNotFoundException($"Downloaded storm warning recordings were not found in '{explicitRoot}'.");
        }
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            string? directory = start;
            for (int level = 0; level < 8 && directory is not null; level++, directory = Path.GetDirectoryName(directory))
            {
                string candidate = Path.Combine(directory, "Assets", "StormChase", "Audio", "Warnings");
                if (HasClips(candidate)) return candidate;
            }
        }
        throw new DirectoryNotFoundException("Downloaded storm warning recordings were not found. Run tools/download-warning-audio.py.");
    }

    private static bool HasClips(string root) => File.Exists(Path.Combine(root, "WarningRadioOklahoma.wav")) && File.Exists(Path.Combine(root, "WarningSirenStPaul.wav"));

    private static void RegisterClip(InMemoryAudioClipCatalog catalog, string id, string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException($"Warning recording is not a RIFF WAV: {path}");
        _ = reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException($"Warning recording is not a WAVE: {path}");
        int sampleRate = 0, channels = 0, blockAlign = 0;
        uint dataBytes = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            string chunk = new(reader.ReadChars(4));
            uint size = reader.ReadUInt32();
            long next = stream.Position + size + (size & 1);
            if (next > stream.Length) throw new InvalidDataException($"Truncated warning recording: {path}");
            if (chunk == "fmt " && size >= 16)
            {
                ushort format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                _ = reader.ReadUInt32();
                blockAlign = reader.ReadUInt16();
                ushort bits = reader.ReadUInt16();
                if (format != 1 || bits != 16 || channels != 1 || sampleRate != 44100 || blockAlign != 2)
                    throw new InvalidDataException($"Warning recording must be mono 44100-Hz PCM16: {path}");
            }
            else if (chunk == "data") dataBytes = size;
            stream.Position = next;
        }
        if (sampleRate <= 0 || blockAlign <= 0 || dataBytes == 0) throw new InvalidDataException($"Empty warning recording: {path}");
        catalog.Register(new AudioClipDescriptor(AudioClipReference.FromAssetId(id),
            TimeSpan.FromSeconds(dataBytes / (double)(sampleRate * blockAlign)),
            SourcePath: Path.GetFullPath(path), SourceKind: AudioClipSourceKind.FilePath,
            IsStreaming: false, Channels: channels, SampleRate: sampleRate));
    }
}
