using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Playback;
using FFXIVClientStructs.Havok.Animation.Playback.Control;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.Quaternion;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Object;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using FFXIVClientStructs.Havok.Common.Serialize.Util;
using Meddle.Utils.Files;
using Microsoft.Extensions.Logging;
using SharpGLTF.Animations;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;

namespace Meddle.Plugin.Services;

/// <summary>
/// Samples a PAP read directly from the live SqPack through the Havok runtime
/// already loaded by FFXIV, then writes an animation-only GLB.  No Havok or
/// game binary is redistributed by XivBlend.
/// </summary>
/// <remarks>
/// The loading/sampling approach is independently adapted from VFXEditor's
/// MIT-licensed PapMotion/GltfAnimation implementation.  See
/// NOTICE-XIVBLEND.md for attribution and the pinned source revision.
/// </remarks>
public sealed class VanillaPapAnimationExporter : IService
{
    public const int ConverterVersion = 1;
    public const int FramesPerSecond = 30;

    private const float TranslationEpsilon = 1.0e-7f;
    private const float ScaleEpsilon = 1.0e-7f;
    private const float RotationDotEpsilon = 1.0e-7f;
    private const float MaximumDurationSeconds = 300.0f;

    private readonly ILogger<VanillaPapAnimationExporter> logger;

    public VanillaPapAnimationExporter(ILogger<VanillaPapAnimationExporter> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Must be called on Dalamud's framework thread because it invokes FFXIV's
    /// Havok member functions.
    /// </summary>
    public unsafe SampledAnimation Sample(
        byte[] papBytes,
        byte[] skeletonBytes,
        string actionName,
        string animationKey,
        bool faceAnimation)
    {
        ArgumentNullException.ThrowIfNull(papBytes);
        ArgumentNullException.ThrowIfNull(skeletonBytes);

        var pap = new PapFile(papBytes);
        if (pap.FileHeader.Magic != PapFile.PapMagic)
        {
            throw new InvalidDataException("The requested animation is not a PAP file.");
        }

        var selected = SelectAnimation(pap, animationKey, faceAnimation);
        if (selected.HavokIndex < 0)
        {
            throw new InvalidDataException($"PAP animation '{selected.GetName}' has no Havok track.");
        }

        var sklb = new SklbFile(skeletonBytes);
        using var papResource = new HavokContainerResource(pap.HavokData, "PAP Havok data");
        using var skeletonResource = new HavokContainerResource(sklb.Skeleton, "SKLB Havok data");

        var skeletonContainer = skeletonResource.AnimationContainer;
        if (skeletonContainer == null || skeletonContainer->Skeletons.Length == 0)
        {
            throw new InvalidDataException("The target SKLB contains no Havok skeleton.");
        }

        var animationContainer = papResource.AnimationContainer;
        if (animationContainer == null)
        {
            throw new InvalidDataException("The PAP contains no Havok animation container.");
        }

        // PAP HavokIndex addresses the binding/motion table. Animation and
        // binding arrays are commonly parallel, but that is not a format
        // guarantee and multi-clip containers need not preserve that shape.
        var havokIndex = selected.HavokIndex;
        if (havokIndex >= animationContainer->Bindings.Length)
        {
            throw new InvalidDataException(
                $"PAP animation '{selected.GetName}' refers to missing Havok binding {havokIndex}.");
        }

        var binding = animationContainer->Bindings[havokIndex].ptr;
        if (binding == null)
        {
            throw new InvalidDataException("The selected PAP Havok binding is null.");
        }

        if (binding->Animation.ptr == null)
        {
            throw new InvalidDataException("The selected PAP Havok binding has no animation.");
        }

        return SampleBoundAnimation(
            skeletonContainer->Skeletons[0].ptr,
            binding,
            actionName,
            selected.GetName);
    }

    /// <summary>
    /// Pure managed GLB construction; safe to run away from the framework
    /// thread after <see cref="Sample"/> has returned.
    /// </summary>
    public void WriteGlb(SampledAnimation sampled, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(sampled);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("Animation output has no parent directory.");
        Directory.CreateDirectory(outputDirectory);

        var scene = new SceneBuilder();
        var nodes = new List<NodeBuilder>(sampled.Bones.Count);
        var roots = new List<NodeBuilder>();

        foreach (var bone in sampled.Bones)
        {
            var node = new NodeBuilder(bone.Name);
            node.SetLocalTransform(
                new AffineTransform(bone.ReferenceScale, bone.ReferenceRotation, bone.ReferenceTranslation),
                false);
            nodes.Add(node);
        }

        for (var index = 0; index < sampled.Bones.Count; index++)
        {
            var parent = sampled.Bones[index].ParentIndex;
            if (parent >= 0)
            {
                if (parent >= nodes.Count)
                {
                    throw new InvalidDataException($"Bone {index} has invalid parent index {parent}.");
                }

                nodes[parent].AddNode(nodes[index]);
            }
            else
            {
                roots.Add(nodes[index]);
            }
        }

        scene.AddSkinnedMesh(
            AnimationExportService.GetDummyMesh("XIVBLEND_ANIMATION_BINDER"),
            Matrix4x4.Identity,
            nodes.ToArray());
        var armature = new NodeBuilder("Armature");
        roots.ForEach(armature.AddNode);
        scene.AddNode(armature);

        var model = scene.ToGltf2();
        var animation = model.UseAnimation(sampled.ActionName);
        var logicalNodes = model.LogicalNodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Name))
            .GroupBy(node => node.Name!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var channelCount = 0;
        foreach (var bone in sampled.Bones.Where(bone => bone.Frames is not null))
        {
            if (!logicalNodes.TryGetValue(bone.Name, out var node))
            {
                continue;
            }

            var frames = bone.Frames!;
            if (ShouldWriteTranslation(frames, bone.ReferenceTranslation))
            {
                animation.CreateTranslationChannel(node, ToTranslationKeys(frames), true);
                channelCount++;
            }

            if (ShouldWriteRotation(frames, bone.ReferenceRotation))
            {
                animation.CreateRotationChannel(node, ToRotationKeys(frames), true);
                channelCount++;
            }

            if (ShouldWriteScale(frames, bone.ReferenceScale))
            {
                animation.CreateScaleChannel(node, ToScaleKeys(frames), true);
                channelCount++;
            }
        }

        if (channelCount == 0)
        {
            throw new InvalidDataException("The selected PAP produced no animated transform channels.");
        }

        model.SaveGLB(outputPath);
        logger.LogInformation(
            "Saved vanilla PAP animation {Action} ({Frames} frames, {Bones} animated bones) to {Path}",
            sampled.ActionName,
            sampled.FrameCount,
            sampled.AnimatedBoneCount,
            outputPath);
    }

    private static PapFile.PapAnimation SelectAnimation(
        PapFile pap,
        string animationKey,
        bool faceAnimation)
    {
        if (pap.Animations.Length == 0)
        {
            throw new InvalidDataException("The PAP contains no animations.");
        }

        if (pap.Animations.Length == 1)
        {
            return pap.Animations[0];
        }

        var leaf = animationKey.Replace('\\', '/').Split('/').LastOrDefault() ?? animationKey;
        var candidates = new[]
        {
            leaf,
            $"cfxf_{leaf}",
            $"cbem_{leaf}",
        };

        foreach (var candidate in candidates)
        {
            var match = pap.Animations.FirstOrDefault(
                animation => animation.HavokIndex >= 0
                             && string.Equals(animation.GetName, candidate, StringComparison.OrdinalIgnoreCase));
            if (match.HavokIndex >= 0 && !string.IsNullOrWhiteSpace(match.GetName))
            {
                return match;
            }
        }

        // A small number of normal player emotes use a TMB name that does not
        // exactly match the body motion stored in the multi-clip PAP (for
        // example dance_male_loop -> cbem_dance_male_2lp).  V1 deliberately
        // does not evaluate TMB/C010 layers, but the primary type-0 skeletal
        // body track is still deterministic.  Likewise, prefer an explicitly
        // facial track when resolving a face pack.
        var fallback = faceAnimation
            ? pap.Animations.FirstOrDefault(animation =>
                animation.HavokIndex >= 0
                && (animation.IsFace
                    || animation.GetName.StartsWith("cfxf_", StringComparison.OrdinalIgnoreCase)))
            : pap.Animations.FirstOrDefault(animation =>
                animation.HavokIndex >= 0
                && animation.Type == 0
                && !animation.IsFace);
        if (fallback.HavokIndex >= 0 && !string.IsNullOrWhiteSpace(fallback.GetName))
        {
            return fallback;
        }

        throw new InvalidDataException(
            $"PAP contains {pap.Animations.Length} animations but none matches '{animationKey}'.");
    }

    private static unsafe SampledAnimation SampleBoundAnimation(
        hkaSkeleton* skeleton,
        hkaAnimationBinding* binding,
        string actionName,
        string sourceAnimationName)
    {
        if (skeleton == null)
        {
            throw new InvalidDataException("The target Havok skeleton is null.");
        }

        var animation = binding->Animation.ptr;
        if (animation == null)
        {
            throw new InvalidDataException("The Havok animation binding has no animation.");
        }

        var duration = animation->Duration;
        if (!float.IsFinite(duration) || duration < 0 || duration > MaximumDurationSeconds)
        {
            throw new InvalidDataException($"The PAP animation duration {duration} is invalid.");
        }

        var boneCount = skeleton->Bones.Length;
        if (boneCount <= 0 || boneCount > 4096)
        {
            throw new InvalidDataException($"The target Havok skeleton has invalid bone count {boneCount}.");
        }

        var frameCount = Math.Max(2, checked((int)MathF.Ceiling(duration * FramesPerSecond) + 1));
        var animatedIndices = new HashSet<int>();
        for (var index = 0; index < binding->TransformTrackToBoneIndices.Length; index++)
        {
            var boneIndex = binding->TransformTrackToBoneIndices[index];
            if (boneIndex >= 0 && boneIndex < boneCount)
            {
                animatedIndices.Add(boneIndex);
            }
        }

        if (animatedIndices.Count == 0)
        {
            throw new InvalidDataException("The selected PAP binding animates no target bones.");
        }

        var bones = new List<SampledBone>(boneCount);
        for (var index = 0; index < boneCount; index++)
        {
            var reference = skeleton->ReferencePose[index];
            var name = skeleton->Bones[index].Name.String;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"bone_{index:D4}";
            }

            bones.Add(new SampledBone(
                name,
                skeleton->ParentIndices[index],
                ToVector3(reference.Translation),
                NormalizeFinite(ToQuaternion(reference.Rotation), $"reference rotation for {name}"),
                ToVector3(reference.Scale),
                animatedIndices.Contains(index) ? new SampledTransform[frameCount] : null));
        }

        var animatedSkeleton = (hkaAnimatedSkeleton*)Marshal.AllocHGlobal(Marshal.SizeOf<hkaAnimatedSkeleton>());
        var animationControl = (hkaAnimationControl*)Marshal.AllocHGlobal(Marshal.SizeOf<hkaAnimationControl>());
        var animatedSkeletonConstructed = false;
        var animationControlConstructed = false;
        var controlAdded = false;

        try
        {
            animationControl->Ctor1(binding);
            animationControlConstructed = true;
            animatedSkeleton->Ctor1(skeleton);
            animatedSkeletonConstructed = true;
            animatedSkeleton->addAnimationControl(animationControl);
            controlAdded = true;

            var transforms = (hkQsTransformf*)Marshal.AllocHGlobal(boneCount * sizeof(hkQsTransformf));
            var floatCount = skeleton->FloatSlots.Length;
            var floats = floatCount > 0
                ? (float*)Marshal.AllocHGlobal(floatCount * sizeof(float))
                : null;

            try
            {
                foreach (var frameIndex in Enumerable.Range(0, frameCount))
                {
                    var time = Math.Min(duration, frameIndex / (float)FramesPerSecond);
                    animationControl->LocalTime = time;
                    animatedSkeleton->sampleAndCombineAnimations(transforms, floats);

                    foreach (var boneIndex in animatedIndices)
                    {
                        var transform = transforms[boneIndex];
                        var bone = bones[boneIndex];
                        var rotation = NormalizeFinite(
                            ToQuaternion(transform.Rotation),
                            $"rotation for {bone.Name} at frame {frameIndex}");

                        if (frameIndex > 0)
                        {
                            var previous = bone.Frames![frameIndex - 1].Rotation;
                            if (Quaternion.Dot(previous, rotation) < 0)
                            {
                                rotation = new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W);
                            }
                        }

                        var sampled = new SampledTransform(
                            ToVector3(transform.Translation),
                            rotation,
                            ToVector3(transform.Scale));
                        ValidateFinite(sampled, bone.Name, frameIndex);
                        bone.Frames![frameIndex] = sampled;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal((nint)transforms);
                if (floats != null)
                {
                    Marshal.FreeHGlobal((nint)floats);
                }
            }
        }
        finally
        {
            try
            {
                if (controlAdded)
                {
                    animatedSkeleton->removeAnimationControl(animationControl);
                }
            }
            finally
            {
                try
                {
                    if (animatedSkeletonConstructed)
                    {
                        animatedSkeleton->Dtor();
                    }
                }
                finally
                {
                    try
                    {
                        if (animationControlConstructed)
                        {
                            // This object was placement-constructed into HGlobal.
                            // Havok's virtual destructor flag 0 releases its
                            // internals without freeing caller-owned storage.
                            animationControl->VirtDtor(0);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal((nint)animatedSkeleton);
                        Marshal.FreeHGlobal((nint)animationControl);
                    }
                }
            }
        }

        return new SampledAnimation(
            actionName,
            sourceAnimationName,
            duration,
            FramesPerSecond,
            frameCount,
            animatedIndices.Count,
            bones);
    }

    private static Dictionary<float, Vector3> ToTranslationKeys(IReadOnlyList<SampledTransform> frames) =>
        Enumerable.Range(0, frames.Count)
            .ToDictionary(index => index / (float)FramesPerSecond, index => frames[index].Translation);

    private static Dictionary<float, Quaternion> ToRotationKeys(IReadOnlyList<SampledTransform> frames) =>
        Enumerable.Range(0, frames.Count)
            .ToDictionary(index => index / (float)FramesPerSecond, index => frames[index].Rotation);

    private static Dictionary<float, Vector3> ToScaleKeys(IReadOnlyList<SampledTransform> frames) =>
        Enumerable.Range(0, frames.Count)
            .ToDictionary(index => index / (float)FramesPerSecond, index => frames[index].Scale);

    private static bool ShouldWriteTranslation(IReadOnlyList<SampledTransform> frames, Vector3 reference)
    {
        var first = frames[0].Translation;
        return Vector3.DistanceSquared(first, reference) > TranslationEpsilon * TranslationEpsilon
               || frames.Skip(1).Any(frame =>
                   Vector3.DistanceSquared(frame.Translation, first) > TranslationEpsilon * TranslationEpsilon);
    }

    private static bool ShouldWriteScale(IReadOnlyList<SampledTransform> frames, Vector3 reference)
    {
        var first = frames[0].Scale;
        return Vector3.DistanceSquared(first, reference) > ScaleEpsilon * ScaleEpsilon
               || frames.Skip(1).Any(frame =>
                   Vector3.DistanceSquared(frame.Scale, first) > ScaleEpsilon * ScaleEpsilon);
    }

    private static bool ShouldWriteRotation(IReadOnlyList<SampledTransform> frames, Quaternion reference)
    {
        var first = frames[0].Rotation;
        return 1.0f - MathF.Abs(Quaternion.Dot(first, reference)) > RotationDotEpsilon
               || frames.Skip(1).Any(frame =>
                   1.0f - MathF.Abs(Quaternion.Dot(frame.Rotation, first)) > RotationDotEpsilon);
    }

    private static Vector3 ToVector3(FFXIVClientStructs.Havok.Common.Base.Math.Vector.hkVector4f value) =>
        new(value.X, value.Y, value.Z);

    private static Quaternion ToQuaternion(hkQuaternionf value) =>
        new(value.X, value.Y, value.Z, value.W);

    private static Quaternion NormalizeFinite(Quaternion value, string label)
    {
        if (!float.IsFinite(value.X)
            || !float.IsFinite(value.Y)
            || !float.IsFinite(value.Z)
            || !float.IsFinite(value.W)
            || value.LengthSquared() < 1.0e-12f)
        {
            throw new InvalidDataException($"PAP produced an invalid {label}.");
        }

        return Quaternion.Normalize(value);
    }

    private static void ValidateFinite(SampledTransform value, string boneName, int frame)
    {
        if (!float.IsFinite(value.Translation.X)
            || !float.IsFinite(value.Translation.Y)
            || !float.IsFinite(value.Translation.Z)
            || !float.IsFinite(value.Scale.X)
            || !float.IsFinite(value.Scale.Y)
            || !float.IsFinite(value.Scale.Z))
        {
            throw new InvalidDataException(
                $"PAP produced a non-finite transform for {boneName} at frame {frame}.");
        }
    }

    private sealed unsafe class HavokContainerResource : IDisposable
    {
        private hkResource* resource;

        public hkaAnimationContainer* AnimationContainer { get; }

        public HavokContainerResource(ReadOnlySpan<byte> data, string label)
        {
            if (data.Length < 8)
            {
                throw new InvalidDataException($"{label} is empty or truncated.");
            }

            try
            {
                var loadOptions = stackalloc hkSerializeUtil.LoadOptions[1];
                loadOptions->TypeInfoRegistry = hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry();
                loadOptions->ClassNameRegistry = hkBuiltinTypeRegistry.Instance()->GetClassNameRegistry();
                loadOptions->Flags = new hkFlags<hkSerializeUtil.LoadOptionBits, int>
                {
                    Storage = (int)hkSerializeUtil.LoadOptionBits.Default,
                };

                fixed (byte* dataPointer = data)
                {
                    resource = hkSerializeUtil.LoadFromBuffer(dataPointer, data.Length, null, loadOptions);
                }

                if (resource == null)
                {
                    throw new InvalidDataException($"FFXIV's Havok runtime could not load {label}.");
                }

                var rootLevelName = @"hkRootLevelContainer"u8;
                fixed (byte* rootName = rootLevelName)
                {
                    var container = (hkRootLevelContainer*)resource->GetContentsPointer(
                        rootName,
                        hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry());
                    if (container == null)
                    {
                        throw new InvalidDataException("Havok file has no root-level container.");
                    }

                    var animationName = @"hkaAnimationContainer"u8;
                    fixed (byte* name = animationName)
                    {
                        AnimationContainer = (hkaAnimationContainer*)container->findObjectByName(name, null);
                    }
                }

                if (AnimationContainer == null)
                {
                    throw new InvalidDataException("Havok file has no animation container.");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (resource == null)
            {
                return;
            }

            ((hkReferencedObject*)resource)->RemoveReference();
            resource = null;
        }
    }
}

public sealed record SampledAnimation(
    string ActionName,
    string SourceAnimationName,
    float DurationSeconds,
    int FramesPerSecond,
    int FrameCount,
    int AnimatedBoneCount,
    IReadOnlyList<SampledBone> Bones);

public sealed record SampledBone(
    string Name,
    int ParentIndex,
    Vector3 ReferenceTranslation,
    Quaternion ReferenceRotation,
    Vector3 ReferenceScale,
    SampledTransform[]? Frames);

public readonly record struct SampledTransform(
    Vector3 Translation,
    Quaternion Rotation,
    Vector3 Scale);
