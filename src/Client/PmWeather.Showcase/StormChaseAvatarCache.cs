using System.Numerics;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using Engine.Animation;
using Engine.Avatar.Generation;
using Engine.Content.IO;
using Engine.Content.Serialization;
using Engine.Core.Diagnostics;
using Engine.Rendering.Animation;
using Engine.Rendering.Assets;
using Engine.Rendering.Importing;
using Engine.Rendering.Loading;
using Engine.Rendering.Publishing;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using Tools.Avatar.UMA;

/// <summary>
/// Cooks the selected real UMA result through the engine's native render bundle
/// publisher. Warm launches retain the exact bind geometry, textures and material
/// settings, and sample native target-rig animation curves without recompositing UMA.
/// </summary>
internal static class StormChaseAvatarCache
{
    // Bump when the recipe or source idle/walk generation changes.
    public const string ChaserSchema = "mmoi.storm-chaser.stunner-john.v4";
    private const string Schema = ChaserSchema;
    private static readonly Matrix4x4 SourceToEngine = Matrix4x4.CreateRotationX(-MathF.PI / 2);
    private static readonly Matrix4x4 EngineToSource = Matrix4x4.CreateRotationX(MathF.PI / 2);
    private static readonly ConditionalWeakTable<MutableRenderAssetCatalog, Dictionary<string, CacheMetadata>> LoadedCatalogs = new();
    public static string PackagedRoot => Path.Combine(AppContext.BaseDirectory, "Assets", "StormChase", "Avatar");
    public static string PackagedCatalogMarker => Path.Combine(PackagedRoot, "avatar.json");

    public static PreparedAvatar? TryLoadPackaged(MutableRenderAssetCatalog catalog, string instanceId)
    {
        var loaded = LoadedCatalogs.GetOrCreateValue(catalog);
        lock (loaded) if (loaded.TryGetValue(PackagedRoot, out var existing) && HasPreparedAssets(catalog, existing)) return new(existing, instanceId);
        string index = Path.Combine(PackagedRoot, "index.json");
        if (!File.Exists(index)) return null;
        var pointer = Read<CachePointer>(index);
        string dataRoot = UnderRoot(PackagedRoot, pointer.Directory);
        var metadata = Read<CacheMetadata>(UnderRoot(dataRoot, "avatar.json"));
        // Cooked content is portable across equivalent build/RID output folders.
        // Producer MVIDs identify warm development caches, not the native bundle ABI.
        if (metadata.Schema != Schema || metadata.Parts.Length < 3 || metadata.Poses.Length == 0)
            throw new InvalidDataException("The packaged avatar content schema is incompatible with this game.");
        foreach (var file in pointer.Files)
            if (!Matches(UnderRoot(dataRoot, file.RelativePath), file)) throw new InvalidDataException("A packaged avatar asset is missing or changed: " + file.RelativePath);
        Console.WriteLine($"Storm chaser: packaged native UMA bundle loaded ({metadata.Parts.Length} parts).");
        return LoadPrepared(catalog, instanceId, dataRoot, metadata, PackagedRoot);
    }

    public static void ExportPackage(string catalogPath, string destination)
    {
        string keyRoot = KeyRoot(catalogPath);
        var pointer = Read<CachePointer>(Path.Combine(keyRoot, "index.json"));
        string source = UnderRoot(keyRoot, pointer.Directory);
        var metadata = Read<CacheMetadata>(UnderRoot(source, "avatar.json"));
        if (metadata.Schema != Schema || metadata.NativePipeline != NativePipelineId())
            throw new InvalidDataException("Load the actual avatar once to cook it for this native version before packaging.");
        string originals = Path.GetDirectoryName(Path.GetFullPath(catalogPath))!;
        foreach (var file in metadata.Sources)
            if (!Matches(UnderRoot(originals, file.RelativePath), file)) throw new InvalidDataException("The source avatar changed after cooking: " + file.RelativePath);
        destination = Path.GetFullPath(destination);
        foreach (var file in pointer.Files)
        {
            string input = UnderRoot(source, file.RelativePath);
            if (!Matches(input, file)) throw new InvalidDataException("The cooked avatar payload changed: " + file.RelativePath);
            string output = UnderRoot(UnderRoot(destination, pointer.Directory), file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(input, output, true);
        }
        Write(Path.Combine(destination, "index.json"), pointer);
        Console.WriteLine($"Packaged actual UMA content: {pointer.Files.Sum(file => file.ByteCount) / 1048576f:F1} MiB at {destination}.");
    }

    public static PreparedAvatar? TryLoad(MutableRenderAssetCatalog catalog, string catalogPath, string instanceId, string schema = ChaserSchema)
    {
        try
        {
            string keyRoot = KeyRoot(catalogPath, schema);
            var loaded = LoadedCatalogs.GetOrCreateValue(catalog);
            lock (loaded) if (loaded.TryGetValue(keyRoot, out var existing) && HasPreparedAssets(catalog, existing)) return new(existing, instanceId);
            string index = Path.Combine(keyRoot, "index.json");
            if (!File.Exists(index)) return null;
            var pointer = Read<CachePointer>(index);
            string dataRoot = UnderRoot(keyRoot, pointer.Directory);
            var metadata = Read<CacheMetadata>(UnderRoot(dataRoot, "avatar.json"));
            if (metadata.Schema != schema || metadata.NativePipeline != NativePipelineId() || metadata.Parts.Length < 3) return null;
            string sourceRoot = Path.GetDirectoryName(Path.GetFullPath(catalogPath))!;
            foreach (var file in metadata.Sources)
                if (!Matches(UnderRoot(sourceRoot, file.RelativePath), file)) return null;
            foreach (var file in pointer.Files)
                if (!Matches(UnderRoot(dataRoot, file.RelativePath), file)) return null;

            return LoadPrepared(catalog, instanceId, dataRoot, metadata, keyRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Console.WriteLine("Storm chaser cache unavailable; rebuilding real UMA content: " + error.Message);
            return null;
        }
    }

    private static PreparedAvatar LoadPrepared(MutableRenderAssetCatalog catalog, string instanceId, string dataRoot, CacheMetadata metadata, string cacheKey)
    {
        var loaded = LoadedCatalogs.GetOrCreateValue(catalog);
        lock (loaded)
        {
            if (loaded.TryGetValue(cacheKey, out var existing) && HasPreparedAssets(catalog, existing)) return new(existing, instanceId);
            var loader = new RenderBundleLoader(new FileSystemContentFileProvider(dataRoot), new CacheLogger());
            var manifest = loader.LoadCurrentManifestAsync(CancellationToken.None).GetAwaiter().GetResult()
                ?? throw new InvalidDataException("The native avatar bundle manifest is invalid.");
            // Full native material objects below preserve skin parameters that the
            // general published material document does not currently carry.
            loader.LoadAssetsIntoCatalogAsync(manifest, manifest.Assets.Where(a => a.Handle.Type != "material").Select(a => a.Handle),
                catalog, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var material in metadata.Materials) catalog.UpsertMaterial(material);
            foreach (var part in metadata.Parts)
            {
                if (!catalog.TryGetMesh(part.Mesh, out var mesh) || mesh is null || mesh.Vertices.Count == 0)
                    throw new InvalidDataException("A cooked UMA mesh is missing: " + part.Mesh);
                if (part.Material is null || !catalog.TryGetMaterial(part.Material.Value, out _))
                    throw new InvalidDataException("A cooked UMA material is missing.");
                if (part.SkinnedPoseId is { } poseId &&
                    (metadata.Poses.FirstOrDefault(pose => pose.Id == poseId) is not { } pose ||
                     mesh.Skeleton?.JointCount != pose.Skeleton.JointCount))
                    throw new InvalidDataException("A cooked UMA mesh does not match its native animation rig.");
            }
            var prepared = new PreparedAvatar(metadata, instanceId);
            prepared.CreateSkinPoses(resetHistory: true); // Validate actual native matrices before admitting a player.
            Console.WriteLine($"Storm chaser: native UMA cache loaded ({metadata.Parts.Length} parts).");
            loaded[cacheKey] = metadata;
            return prepared;
        }
    }
    private static bool HasPreparedAssets(MutableRenderAssetCatalog catalog, CacheMetadata metadata) =>
        metadata.Parts.All(part => catalog.TryGetMesh(part.Mesh, out var mesh) && mesh is not null &&
            part.Material is { } material && catalog.TryGetMaterial(material, out var definition) && definition is not null &&
            new[] { definition.AlbedoTexture, definition.NormalTexture, definition.SurfaceTexture, definition.EmissiveTexture }
                .All(handle => handle is null || catalog.TryGetTexture(handle.Value, out var texture) && texture is not null));

    public static void Save(MutableRenderAssetCatalog catalog, UmaAvatarFactory factory, UmaAvatarInstance avatar,
        AvatarAppearanceRecipe recipe, AnimationSkeleton sourceSkeleton, HumanoidRigProfile sourceRig,
        AnimationClip idle, AnimationClip walk, float scale, float feetOffset, float height, IReadOnlyList<string> wardrobe, string schema = ChaserSchema)
    {
        try
        {
            string sourcePath = factory.CatalogPath;
            string keyRoot = KeyRoot(sourcePath, schema);
            string directory = "native-" + Guid.NewGuid().ToString("N");
            string dataRoot = UnderRoot(keyRoot, directory);
            Directory.CreateDirectory(dataRoot);
            var parts = avatar.CreateRenderItems("chase/cooked").ToArray();
            var meshes = parts.Select(p => p.Mesh).Distinct().Select(handle =>
                catalog.TryGetMesh(handle, out var mesh) && mesh is not null ? mesh : throw new InvalidDataException("UMA mesh was not registered.")).ToArray();
            var materials = parts.Where(p => p.Material is not null).Select(p => p.Material!.Value).Distinct().Select(handle =>
                catalog.TryGetMaterial(handle, out var material) && material is not null ? material : throw new InvalidDataException("UMA material was not registered.")).ToArray();
            var textures = materials.SelectMany(m => new[] { m.AlbedoTexture, m.NormalTexture, m.SurfaceTexture, m.EmissiveTexture })
                .Where(t => t is not null).Select(t => t!.Value).Distinct().Select(handle =>
                {
                    if (!catalog.TryRestoreTexturePayload(handle, out var texture) || texture is null)
                        throw new InvalidDataException("UMA texture payload is unavailable: " + handle);
                    return new ImportedTextureAsset { Texture = texture, PixelFormat = texture.PixelFormat,
                        ColorSpace = texture.ColorSpace, MipCount = (int)texture.MipLevels };
                }).ToArray();

            var poses = CookTargetClips(catalog, avatar, parts, sourceSkeleton, sourceRig, idle, walk);
            new RenderAssetBundlePublisher().Publish(new()
            {
                OutputRootPath = dataRoot, VersionId = "storm-chaser", Meshes = meshes, Materials = materials,
                Textures = textures, UpdateCurrentManifest = true,
            });
            var build = factory.Build(recipe.CreateDescriptor(), recipe.CreateRecipe());
            var sources = SelectedSourceFiles(factory, build.BuildPlan).Select(path => Fingerprint(Path.GetDirectoryName(sourcePath)!, path)).ToArray();
            var metadata = new CacheMetadata(schema, NativePipelineId(), parts, materials, poses, sources,
                scale, feetOffset, height, wardrobe.ToArray());
            Write(Path.Combine(dataRoot, "avatar.json"), metadata);
            var files = Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).Select(path => Fingerprint(dataRoot, path)).ToArray();
            string index = Path.Combine(keyRoot, "index.json");
            string temporary = index + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Write(temporary, new CachePointer(directory, files));
            File.Move(temporary, index, overwrite: true);
            // Leave the instance in a neutral native idle pose after baking both clips.
            avatar.SetHumanoidAnimationSamples(new(idle, sourceSkeleton, sourceRig, 0));
            Console.WriteLine($"Storm chaser: native UMA cache saved ({files.Sum(f => f.ByteCount) / 1048576f:F1} MiB).");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            Console.WriteLine("Storm chaser remains on native UMA; cache could not be written: " + error.Message);
            avatar.SetHumanoidAnimationSamples(new(idle, sourceSkeleton, sourceRig, 0));
        }
    }

    private static CachedPose[] CookTargetClips(MutableRenderAssetCatalog catalog, UmaAvatarInstance avatar,
        RenderItem[] parts, AnimationSkeleton sourceSkeleton, HumanoidRigProfile sourceRig, AnimationClip idle, AnimationClip walk)
    {
        var skeletons = parts.Where(p => p.SkinnedPoseId is not null).GroupBy(p => p.SkinnedPoseId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => catalog.TryGetMesh(g.First().Mesh, out var mesh) && mesh?.Skeleton is not null
                ? mesh.Skeleton : throw new InvalidDataException("A native UMA palette skeleton is missing."), StringComparer.Ordinal);
        var cooked = new Dictionary<string, AnimationClip[]>(StringComparer.Ordinal);
        foreach (var id in skeletons.Keys) cooked.Add(id, new AnimationClip[2]);
        int clipIndex = 0;
        foreach (var clip in new[] { idle, walk })
        {
            const int samples = 64;
            var localKeys = skeletons.ToDictionary(p => p.Key, p => Enumerable.Range(0, p.Value.JointCount)
                .Select(_ => new List<(float Time, AnimationTransform Local)>()).ToArray(), StringComparer.Ordinal);
            for (int sample = 0; sample <= samples; sample++)
            {
                float time = clip.DurationSeconds * sample / samples;
                avatar.SetHumanoidAnimationSamples(new(clip, sourceSkeleton, sourceRig, time));
                foreach (var palette in avatar.CreateRenderSkinPoses())
                {
                    var skeleton = skeletons[palette.Id];
                    var models = new Matrix4x4[skeleton.JointCount];
                    for (int i = 0; i < models.Length; i++)
                    {
                        if (!Matrix4x4.Invert(skeleton.Joints[i].InverseBindMatrix.ToNumerics(), out var bind))
                            throw new InvalidDataException("A native UMA bind matrix cannot be inverted.");
                        var sourceSkin = SourceToEngine * palette.JointMatrices[i] * EngineToSource;
                        models[i] = bind * sourceSkin;
                        int parent = skeleton.Joints[i].ParentIndex;
                        var local = models[i];
                        if (parent >= 0)
                        {
                            if (!Matrix4x4.Invert(models[parent], out var inverseParent))
                                throw new InvalidDataException("A native UMA parent matrix cannot be inverted.");
                            local *= inverseParent;
                        }
                        if (!Matrix4x4.Decompose(local, out var localScale, out var rotation, out var translation))
                            throw new InvalidDataException("A native UMA pose cannot be decomposed.");
                        localKeys[palette.Id][i].Add((time, new(new(translation.X, translation.Y, translation.Z),
                            Quaternionf.FromNumerics(Quaternion.Normalize(rotation)), new(localScale.X, localScale.Y, localScale.Z))));
                    }
                }
            }
            foreach (var (id, skeleton) in skeletons)
            {
                var channels = skeleton.Joints.Select((joint, i) => new AnimationChannel(joint.Name, i,
                    localKeys[id][i].Select(k => new AnimationKeyframe<Vector3f>(k.Time, k.Local.Translation)).ToArray(),
                    localKeys[id][i].Select(k => new AnimationKeyframe<Quaternionf>(k.Time, k.Local.Rotation)).ToArray(),
                    localKeys[id][i].Select(k => new AnimationKeyframe<Vector3f>(k.Time, k.Local.Scale)).ToArray())).ToArray();
                cooked[id][clipIndex] = new(clip.Id + "/native-cooked", clip.Name, clip.DurationSeconds, channels, loops: true);
            }
            clipIndex++;
        }
        return skeletons.Select(p => new CachedPose(p.Key, p.Value, cooked[p.Key][0], cooked[p.Key][1])).ToArray();
    }

    private static IEnumerable<string> SelectedSourceFiles(UmaAvatarFactory factory, AvatarBuildPlan plan)
    {
        var assets = new HashSet<AvatarAssetId>();
        var extra = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(factory.CatalogPath) };
        string sourceRoot = Path.GetDirectoryName(factory.CatalogPath)!;
        foreach (var slot in plan.Slots)
        {
            assets.Add(slot.Slot.MeshAssetId);
            if (factory.Content.SemanticSurfaceFiles.TryGetValue(slot.Slot.Id, out string? semantic)) extra.Add(UnderRoot(sourceRoot, semantic));
            foreach (var overlay in slot.Overlays)
            {
                foreach (var asset in overlay.TextureChannels.Values) assets.Add(asset);
                if (overlay.MaskAssetId is { } mask) assets.Add(mask);
            }
        }
        foreach (var asset in assets)
            if (factory.Content.AssetFiles.TryGetValue(asset, out string? relative) && relative is not null) extra.Add(UnderRoot(sourceRoot, relative));
        return extra.Order(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, long, DateTime), string> SourceHashes = new();
    private static string KeyRoot(string catalogPath, string schema = ChaserSchema)
    {
        // The 17 MB source catalog is hashed once per file version, not once per avatar instance (NPC pools).
        var info = new FileInfo(catalogPath);
        string sourceHash = SourceHashes.GetOrAdd((info.FullName, info.Length, info.LastWriteTimeUtc), _ =>
        {
            using var stream = File.OpenRead(catalogPath);
            return Convert.ToHexString(SHA256.HashData(stream));
        });
        string key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(schema + NativePipelineId() + sourceHash)))[..32];
        string root = System.Environment.GetEnvironmentVariable("MMOI_STORM_CHASER_CACHE")
            ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "MMOI", "StormChase", "AvatarCache");
        return UnderRoot(Path.GetFullPath(root), key);
    }

    private static string NativePipelineId() => string.Join("/", typeof(UmaAvatarFactory).Assembly.ManifestModule.ModuleVersionId,
        typeof(AvatarAppearanceRecipe).Assembly.ManifestModule.ModuleVersionId, typeof(AnimationSampler).Assembly.ManifestModule.ModuleVersionId);

    private static FileFingerprint Fingerprint(string root, string path)
    {
        using var stream = File.OpenRead(path);
        return new(Path.GetRelativePath(root, path).Replace('\\', '/'), stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static bool Matches(string path, FileFingerprint fingerprint)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != fingerprint.ByteCount) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(fingerprint.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string UnderRoot(string root, string relative)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Avatar cache path leaves its content root.");
        return path;
    }

    private static T Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return ContentJsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Invalid avatar cache document: " + path);
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ContentJsonSerializer.SerializeToUtf8Bytes(value));
    }

    internal sealed class PreparedAvatar
    {
        private readonly CacheMetadata metadata;
        private readonly AnimationSampler sampler = new();
        private readonly string instanceId;
        private float seconds;
        private float walkWeight;
        public PreparedAvatar(CacheMetadata metadata, string instanceId) { this.metadata = metadata; this.instanceId = instanceId; }
        public float Scale => metadata.Scale;
        public float FeetOffset => metadata.FeetOffset;
        public float HeightMeters => metadata.HeightMeters;
        public int PartCount => metadata.Parts.Length;
        public IReadOnlyList<string> Wardrobe => metadata.Wardrobe;
        public IReadOnlyList<RenderItem> CreateRenderItems(Vector3f feetPosition, float yaw, float seconds, float walkWeight, float sizeScale = 1)
        {
            this.seconds = seconds; this.walkWeight = walkWeight;
            float scale = Scale * sizeScale;
            return metadata.Parts.Select((p, i) => p with
            {
                MaterialId = instanceId + ":" + i, Position = feetPosition + new Vector3f(0, FeetOffset * sizeScale, 0),
                Scale = new(scale, scale, scale), RotationYawDegrees = yaw,
                SkinnedPoseId = p.SkinnedPoseId is null ? null : PoseId(p.SkinnedPoseId),
            }).ToArray();
        }
        public IReadOnlyList<RenderSkinPose> CreateSkinPoses(bool resetHistory)
        {
            return metadata.Poses.Select(p =>
            {
                var idle = sampler.Sample(p.Skeleton, p.Idle, seconds);
                var walk = sampler.Sample(p.Skeleton, p.Walk, seconds);
                var locals = idle.LocalTransforms.Select((local, i) => AnimationTransform.Lerp(local, walk.LocalTransforms[i], walkWeight)).ToArray();
                var pose = sampler.CreatePose(p.Skeleton, locals);
                var result = new RenderSkinPose(PoseId(p.Id), pose.SkinningMatrices.Select(RenderSkinningMath.ConvertSkinningMatrixToEngineSpace).ToArray(), resetHistory);
                result.Validate();
                return result;
            }).ToArray();
        }
        private string PoseId(string sourceId) => instanceId + "/" + sourceId;
    }

    internal sealed record CachePointer(string Directory, FileFingerprint[] Files);
    internal sealed record FileFingerprint(string RelativePath, long ByteCount, string Sha256);
    internal sealed record CachedPose(string Id, AnimationSkeleton Skeleton, AnimationClip Idle, AnimationClip Walk);
    internal sealed record CacheMetadata(string Schema, string NativePipeline, RenderItem[] Parts, RenderMaterialDefinition[] Materials,
        CachedPose[] Poses, FileFingerprint[] Sources, float Scale, float FeetOffset, float HeightMeters, string[] Wardrobe);
    private sealed class CacheLogger : IRuntimeLogger
    {
        public void Info(string category, string message) { }
        public void Warn(string category, string message) => Console.WriteLine("Avatar bundle: " + message);
        public void Error(string category, string message, Exception? exception = null) => Console.WriteLine("Avatar bundle: " + message);
    }
}
