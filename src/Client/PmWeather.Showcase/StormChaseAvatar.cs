using System.Collections.Concurrent;
using System.Numerics;
using Engine.Animation;
using Engine.Animation.Skinning;
using Engine.Avatar.Generation;
using Engine.Rendering.Assets;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using Tools.Avatar.UMA;

/// <summary>An MMOI/UMA appearance: race (archetype), wardrobe recipes (chest, legs and feet first), age and target height.</summary>
internal sealed record StormChaseAvatarLook(string Key, string Race, string[] Wardrobe, int AgeYears, float HeightMeters)
{
    public static StormChaseAvatarLook Chaser { get; } = new("chaser", "o3n Stunner John",
        ["st_male_shirt_02_long_sleeve_Recipe", "st_male_pants_blue_jeans_01_Recipe", "st_shoes_07_Recipe_black", "st_short_hair_Recipe"], 34, 1.82f);
    public string Schema => Key == "chaser" ? StormChaseAvatarCache.ChaserSchema : "mmoi.storm-npc.v1." + Key;
}

/// <summary>A clothed, GPU-skinned native UMA storm chaser (or NPC look) with native locomotion clips.</summary>
internal sealed class StormChaseAvatar : IDisposable
{
    private static readonly ConcurrentDictionary<string, Lazy<UmaAvatarFactory>> Factories = new(StringComparer.OrdinalIgnoreCase);
    private static int nextInstance;
    private readonly UmaAvatarInstance? avatar;
    private readonly StormChaseAvatarCache.PreparedAvatar? prepared;
    private readonly AnimationSkeleton? skeleton;
    private readonly HumanoidRigProfile? rig;
    private readonly AnimationClip? idle;
    private readonly AnimationClip? walk;
    private readonly string instanceId;
    private readonly float scale;
    private readonly float feetOffset;
    private float previousSeconds = float.NaN;
    private float walkWeight;
    private bool disposed;

    public StormChaseAvatar(MutableRenderAssetCatalog catalog, string? catalogPath = null, StormChaseAvatarLook? look = null)
    {
        look ??= StormChaseAvatarLook.Chaser;
        Look = look;
        instanceId = (look.Key == "chaser" ? "storm-chaser-" : "npc-" + look.Key + "-") + Interlocked.Increment(ref nextInstance);
        string? requested = catalogPath ?? System.Environment.GetEnvironmentVariable("MMOI_UMA_CATALOG");
        bool usePackage = look.Key == "chaser" && (requested is null || string.Equals(Path.GetFullPath(requested), StormChaseAvatarCache.PackagedCatalogMarker, StringComparison.OrdinalIgnoreCase));
        if (usePackage && StormChaseAvatarCache.TryLoadPackaged(catalog, instanceId) is { } packaged)
        {
            CatalogPath = StormChaseAvatarCache.PackagedCatalogMarker; prepared = packaged;
            HeightMeters = prepared.HeightMeters; scale = prepared.Scale; feetOffset = prepared.FeetOffset;
            WardrobeNames = prepared.Wardrobe; PartCount = prepared.PartCount;
            return;
        }
        if (catalogPath is not null && string.Equals(Path.GetFullPath(catalogPath), StormChaseAvatarCache.PackagedCatalogMarker, StringComparison.OrdinalIgnoreCase))
            catalogPath = null;   // NPC looks are cooked from the source catalog; only the chaser ships packaged.
        CatalogPath = Path.GetFullPath(catalogPath ?? System.Environment.GetEnvironmentVariable("MMOI_UMA_CATALOG")
            ?? @"C:\Users\reviv\youradventure\Old Engine\artifacts\avatar\uma\uma-catalog.json");
        if (!File.Exists(CatalogPath)) throw new FileNotFoundException("The native storm chaser requires the exported UMA catalog and its meshes/textures.", CatalogPath);
        prepared = StormChaseAvatarCache.TryLoad(catalog, CatalogPath, instanceId, look.Schema);
        if (prepared is not null)
        {
            HeightMeters = prepared.HeightMeters; scale = prepared.Scale; feetOffset = prepared.FeetOffset;
            WardrobeNames = prepared.Wardrobe; PartCount = prepared.PartCount;
            return;
        }
        var factory = Factories.GetOrAdd(CatalogPath, path => new(() => new UmaAvatarFactory(path))).Value;
        // All selected wardrobe recipes have real exported geometry and are authored
        // for this adult archetype. Missing clothing is an error, never a bare fallback.
        string race = look.Race;
        string[] wardrobe = look.Wardrobe;
        var recipe = new AvatarAppearanceRecipe(factory.ResolveArchetypeId(race), ageYears: look.AgeYears,
            wardrobeItems: wardrobe.Select(factory.ResolveWardrobeItemId));
        avatar = factory.CreateInstance(instanceId, catalog, recipe, gpuSkinningEnabled: true);
        try
        {
            if (!avatar.HasGpuSkinningGeometry) throw new InvalidDataException("The storm chaser has no GPU-skinned geometry.");
            foreach (var name in wardrobe.Take(3))
            {
                var id = factory.ResolveWardrobeItemId(name);
                if (!avatar.Parts.Any(p => p.WardrobeItemId == id))
                    throw new InvalidDataException("Required storm-chasing clothing was not assembled: " + name);
            }
            HeightMeters = look.HeightMeters;
            (skeleton, rig, idle, walk, scale, feetOffset) = PrepareRig(avatar, catalog, HeightMeters);
            WardrobeNames = wardrobe;
            PartCount = avatar.Parts.Count;
            StormChaseAvatarCache.Save(catalog, factory, avatar, recipe, skeleton, rig, idle, walk,
                scale, feetOffset, HeightMeters, wardrobe, look.Schema);
        }
        catch { avatar.Dispose(); throw; }
    }

    /// <summary>
    /// One person's own avatar, built live from their recipe (genetic DNA sliders and colours) through a shared
    /// factory. Never written to the disk cache: with deferred colours, people share every texture and differ only
    /// in their small DNA-deformed meshes and material tints.
    /// </summary>
    public StormChaseAvatar(MutableRenderAssetCatalog catalog, UmaAvatarFactory factory, AvatarAppearanceRecipe recipe,
        float heightMeters, IReadOnlyList<string> wardrobe, string label)
    {
        Look = new(label, "", wardrobe.ToArray(), (int)recipe.AgeYears, heightMeters);
        instanceId = "npc-" + label + "-" + Interlocked.Increment(ref nextInstance);
        CatalogPath = factory.CatalogPath;
        avatar = factory.CreateInstance(instanceId, catalog, recipe, gpuSkinningEnabled: true);
        try
        {
            if (!avatar.HasGpuSkinningGeometry) throw new InvalidDataException("The resident's avatar has no GPU-skinned geometry.");
            HeightMeters = heightMeters;
            (skeleton, rig, idle, walk, scale, feetOffset) = PrepareRig(avatar, catalog, HeightMeters);
            WardrobeNames = wardrobe;
            PartCount = avatar.Parts.Count;
        }
        catch { avatar.Dispose(); throw; }
    }

    /// <summary>Rig, locomotion clips and the scale/feet offset that give an exact standing height.</summary>
    private static (AnimationSkeleton, HumanoidRigProfile, AnimationClip, AnimationClip, float, float) PrepareRig(
        UmaAvatarInstance avatar, MutableRenderAssetCatalog catalog, float heightMeters)
    {
        var importedRig = avatar.HumanoidRigProfile ?? throw new InvalidDataException("The UMA adult has no imported humanoid rig.");
        var skeleton = avatar.Parts.Select(p => catalog.TryGetMesh(p.Mesh, out var mesh) ? mesh?.Skeleton : null)
            .Where(s => s is not null).OrderByDescending(s => s!.JointCount).FirstOrDefault()
            ?? throw new InvalidDataException("The storm chaser skeleton was not registered.");
        var rig = CreateMeshSpaceRigProfile(skeleton, importedRig);
        // The exported UMA catalog contains bind geometry and its humanoid T-pose,
        // but no locomotion clips. Build Engine.Animation curves on that exact rig.
        var idle = CreateClip(skeleton, rig, walking: false);
        var walk = CreateClip(skeleton, rig, walking: true);
        avatar.SetHumanoidAnimationSamples(new(idle, skeleton, rig, 0));
        // Retargeted posture can differ slightly from bind-space bounds. Measure the initial native posed
        // geometry once for an exact meter-world height and planted feet; frames only update GPU palettes.
        var posedBounds = MeasurePoseBounds(avatar, catalog);
        float scale = heightMeters / Math.Max(.1f, posedBounds.Maximum - posedBounds.Minimum);
        return (skeleton, rig, idle, walk, scale, -posedBounds.Minimum * scale);
    }

    public string CatalogPath { get; }
    public StormChaseAvatarLook Look { get; }
    /// <summary>Per-instance size multiplier (an NPC's own standing height over the look's authored height).</summary>
    public float SizeScale { get; set; } = 1;
    public float HeightMeters { get; }
    public int PartCount { get; }
    public IReadOnlyList<string> WardrobeNames { get; }
    public bool UsesCookedCache => prepared is not null;
    public string AnimationSource => "Engine.Animation curves on imported UMA humanoid rig";

    /// <param name="yaw">World yaw in degrees, matching native RenderItem rotations.</param>
    public IReadOnlyList<RenderItem> CreateRenderItems(Vector3f feetPosition, float yaw, float seconds, bool walking)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!float.IsFinite(seconds) || !float.IsFinite(yaw)) throw new ArgumentOutOfRangeException(nameof(seconds));
        float dt = float.IsFinite(previousSeconds) && seconds >= previousSeconds ? Math.Clamp(seconds - previousSeconds, 0, .1f) : 1f / 60f;
        previousSeconds = seconds;
        walkWeight += ((walking ? 1f : 0f) - walkWeight) * (1 - MathF.Exp(-dt * 10));
        if (prepared is not null) return prepared.CreateRenderItems(feetPosition, yaw, seconds, Math.Clamp(walkWeight, 0, 1), SizeScale);
        avatar!.SetHumanoidAnimationSamples(new(idle!, skeleton!, rig!, seconds),
            new(walk!, skeleton!, rig!, seconds, SuppressHorizontalRootMotion: true), Math.Clamp(walkWeight, 0, 1));
        var origin = feetPosition + new Vector3f(0, feetOffset * SizeScale, 0);
        return avatar.CreateRenderItems(instanceId, origin, new(scale * SizeScale, scale * SizeScale, scale * SizeScale), yaw,
            castsShadow: true, receivesShadow: true);
    }

    public IReadOnlyList<RenderSkinPose> CreateSkinPoses(bool resetHistory = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return prepared?.CreateSkinPoses(resetHistory) ?? avatar!.CreateRenderSkinPoses(resetHistory);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        avatar?.Dispose();
    }

    private static (float Minimum, float Maximum) MeasurePoseBounds(UmaAvatarInstance avatar, MutableRenderAssetCatalog catalog)
    {
        var poses = avatar.CreateRenderSkinPoses().ToDictionary(p => p.Id, StringComparer.Ordinal);
        float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
        foreach (var item in avatar.CreateRenderItems("chase/calibrate"))
        {
            if (!catalog.TryGetMesh(item.Mesh, out var mesh) || mesh is null) continue;
            var pose = item.SkinnedPoseId is not null && poses.TryGetValue(item.SkinnedPoseId, out var found) ? found : null;
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                var position = mesh.Vertices[i].Position;
                if (pose is not null && mesh.IsSkinned)
                    position = CpuSkinning.TransformPosition(position, mesh.VertexInfluences[i], pose.JointMatrices);
                minimum = Math.Min(minimum, position.Y);
                maximum = Math.Max(maximum, position.Y);
            }
        }
        if (!float.IsFinite(minimum) || !float.IsFinite(maximum) || maximum - minimum < .1f)
            throw new InvalidDataException("The storm chaser has invalid posed body bounds.");
        return (minimum, maximum);
    }

    private static HumanoidRigProfile CreateMeshSpaceRigProfile(AnimationSkeleton skeleton, HumanoidRigProfile importedRig)
    {
        // The exported Unity humanoid reference is Y-up, while the real UMA mesh
        // skeleton root rotates that reference into Z-up. Native UMA retargeting
        // keeps the mesh root and the exported T-pose rotations on child joints.
        // Generate our model-space animation deltas in that same reference: using
        // the Unity identity root here makes an intended downward arm point forward.
        var reference = skeleton.Joints.ToDictionary(joint => joint.Name, joint =>
        {
            var local = joint.BindLocalTransform;
            if (joint.ParentIndex >= 0 && importedRig.TryGetReferenceLocalTransform(joint.Name, out var exported))
                local = local with { Rotation = exported.Rotation };
            return local;
        }, StringComparer.OrdinalIgnoreCase);
        return new(importedRig.Id + "/chase-mesh-space", importedRig.BoneMappings, reference,
            importedRig.JointLimits, importedRig.MotionRootJointName, importedRig.Settings);
    }

    private static AnimationClip CreateClip(AnimationSkeleton skeleton, HumanoidRigProfile rig, bool walking)
    {
        var reference = skeleton.Joints.Select(j => rig.ReferenceLocalTransforms.TryGetValue(j.Name, out var t) ? t : j.BindLocalTransform).ToArray();
        var referencePose = new AnimationSampler().CreatePose(skeleton, reference);
        var modelRotations = new Matrix4x4[skeleton.JointCount];
        for (int i = 0; i < modelRotations.Length; i++)
        {
            var local = Matrix4x4.CreateFromQuaternion(reference[i].Rotation.Normalized().ToNumerics());
            modelRotations[i] = skeleton.Joints[i].ParentIndex >= 0 ? local * modelRotations[skeleton.Joints[i].ParentIndex] : local;
        }
        var semanticIndices = rig.BoneMappings.Where(p => skeleton.TryGetJointIndex(p.Value, out _))
            .ToDictionary(p => p.Key, p => { skeleton.TryGetJointIndex(p.Value, out int index); return index; });
        Vector3 Position(HumanoidBone bone, Vector3 fallback) => semanticIndices.TryGetValue(bone, out int index)
            ? Vector3.Transform(Vector3.Zero, referencePose.ModelTransforms[index]) : fallback;
        // UMA source coordinates can differ from the renderer. Infer anatomical axes
        // from the imported rig instead of assuming its source skeleton is Y-up.
        var hips = Position(HumanoidBone.Hips, Vector3.Zero);
        var up = Normalize(Position(HumanoidBone.Head, hips + Vector3.UnitZ) - hips, Vector3.UnitZ);
        var right = Normalize(Position(HumanoidBone.RightUpperArm, Vector3.UnitX) - Position(HumanoidBone.LeftUpperArm, -Vector3.UnitX), Vector3.UnitX);
        var forward = Normalize(Vector3.Cross(right, up), -Vector3.UnitY);
        float duration = walking ? 1.05f : 4f;
        const int samples = 32;
        var rotations = Enumerable.Range(0, skeleton.JointCount).Select(_ => new List<AnimationKeyframe<Quaternionf>>()).ToArray();
        var translations = Enumerable.Range(0, skeleton.JointCount).Select(_ => new List<AnimationKeyframe<Vector3f>>()).ToArray();

        for (int sample = 0; sample <= samples; sample++)
        {
            float time = duration * sample / samples;
            float phase = time / duration * MathF.Tau;
            float stride = walking ? MathF.Sin(phase) : 0;
            float breathing = MathF.Sin(phase);
            var desiredModels = new Dictionary<int, Matrix4x4>();

            void PointBone(HumanoidBone bone, HumanoidBone child, Vector3 direction)
            {
                if (!semanticIndices.TryGetValue(bone, out int index) || !semanticIndices.TryGetValue(child, out int childIndex)) return;
                var currentDirection = Normalize(Position(child, Vector3.Zero) - Position(bone, Vector3.Zero), -up);
                var delta = FromTo(currentDirection, Normalize(direction, -up));
                desiredModels[index] = modelRotations[index] * Matrix4x4.CreateFromQuaternion(delta);
            }
            void RotateBone(HumanoidBone bone, Vector3 axis, float radians)
            {
                if (semanticIndices.TryGetValue(bone, out int index))
                    desiredModels[index] = modelRotations[index] * Matrix4x4.CreateFromAxisAngle(axis, radians);
            }
            PointBone(HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm, -up - right * .13f + forward * (-stride * .22f + .05f));
            PointBone(HumanoidBone.RightUpperArm, HumanoidBone.RightLowerArm, -up + right * .13f + forward * (stride * .22f + .05f));
            PointBone(HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand, -up - right * .08f + forward * (.18f - stride * .16f));
            PointBone(HumanoidBone.RightLowerArm, HumanoidBone.RightHand, -up + right * .08f + forward * (.18f + stride * .16f));
            PointBone(HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, -up - right * .015f + forward * stride * .48f);
            PointBone(HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, -up + right * .015f - forward * stride * .48f);
            PointBone(HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot, -up + forward * (stride * .18f - Math.Max(0, -stride) * .42f));
            PointBone(HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot, -up + forward * (-stride * .18f - Math.Max(0, stride) * .42f));
            RotateBone(HumanoidBone.Spine, right, (walking ? .045f : .012f) + breathing * .006f);
            RotateBone(HumanoidBone.Chest, up, stride * .035f);
            RotateBone(HumanoidBone.Head, up, (walking ? .018f : .07f) * MathF.Sin(phase));
            var outputModels = new Matrix4x4[skeleton.JointCount];
            for (int i = 0; i < skeleton.JointCount; i++)
            {
                int parent = skeleton.Joints[i].ParentIndex;
                var parentModel = parent >= 0 ? outputModels[parent] : Matrix4x4.Identity;
                var localRotation = desiredModels.TryGetValue(i, out var desired)
                    ? desired * Matrix4x4.Transpose(parentModel)
                    : Matrix4x4.CreateFromQuaternion(reference[i].Rotation.Normalized().ToNumerics());
                outputModels[i] = localRotation * parentModel;
                var translation = reference[i].Translation;
                if (semanticIndices.TryGetValue(HumanoidBone.Hips, out int hipsIndex) && i == hipsIndex)
                {
                    float bob = walking ? (1 - MathF.Cos(phase * 2)) * .012f : breathing * .004f;
                    var parentReference = parent >= 0 ? modelRotations[parent] : Matrix4x4.Identity;
                    var localUp = Vector3.TransformNormal(up, Matrix4x4.Transpose(parentReference));
                    translation += new Vector3f(localUp.X * bob, localUp.Y * bob, localUp.Z * bob);
                }
                rotations[i].Add(new(time, Quaternionf.FromNumerics(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(localRotation)))));
                translations[i].Add(new(time, translation));
            }
        }
        var channels = skeleton.Joints.Select((joint, i) => new AnimationChannel(joint.Name, i,
            translations[i], rotations[i], [])).ToArray();
        return new(walking ? "chase/walk" : "chase/idle", walking ? "Storm chaser walk" : "Storm chaser idle", duration, channels, loops: true);
    }

    private static Vector3 Normalize(Vector3 value, Vector3 fallback) => value.LengthSquared() > .000001f ? Vector3.Normalize(value) : fallback;
    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        if (dot > .99999f) return Quaternion.Identity;
        if (dot < -.99999f)
        {
            var axis = Vector3.Cross(from, Math.Abs(from.X) < .8f ? Vector3.UnitX : Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Normalize(axis, Vector3.UnitZ), MathF.PI);
        }
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1 + dot));
    }
}
