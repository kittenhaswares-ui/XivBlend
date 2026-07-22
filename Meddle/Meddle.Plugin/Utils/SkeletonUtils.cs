using System.ComponentModel;
using Meddle.Plugin.Models.Skeletons;
using Meddle.Utils;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;

namespace Meddle.Plugin.Utils;

public static class SkeletonUtils
{
    public enum PoseMode
    {
        [Description("Reference Pose")]
        None,
        [Description("Reference Pose with Scale")]
        LocalScaleOnly,
        [Description("Pose")]
        Local
    }

    public static (List<BoneNodeBuilder> List, BoneNodeBuilder Root)[] GetBoneMaps(
        ParsedSkeleton skeleton,
        PoseMode poseMode)
    {
        List<BoneNodeBuilder> boneMap = [];
        List<BoneNodeBuilder> rootList = [];

        for (var partialIdx = 0; partialIdx < skeleton.PartialSkeletons.Count; partialIdx++)
        {
            var partial = skeleton.PartialSkeletons[partialIdx];
            var hkSkeleton = partial.HkSkeleton;
            if (hkSkeleton == null)
            {
                continue;
            }

            var skeletonBones = new BoneNodeBuilder[hkSkeleton.BoneNames.Count];
            for (var boneIndex = 0; boneIndex < hkSkeleton.BoneNames.Count; boneIndex++)
            {
                var name = hkSkeleton.BoneNames[boneIndex];
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (boneMap.FirstOrDefault(bone =>
                        bone.BoneName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } duplicate)
                {
                    skeletonBones[boneIndex] = duplicate;
                    continue;
                }

                var bone = new BoneNodeBuilder(name)
                {
                    BoneIndex = boneIndex,
                    PartialSkeletonHandle = partial.HandlePath
                        ?? throw new InvalidOperationException(
                            $"No handle path for {name} [{partialIdx},{boneIndex}]"),
                    PartialSkeletonIndex = partialIdx,
                };

                bone.SetLocalTransform(hkSkeleton.ReferencePose[boneIndex].AffineTransform, false);

                var parentIndex = hkSkeleton.BoneParents[boneIndex];
                if (parentIndex != -1)
                {
                    skeletonBones[parentIndex].AddNode(bone);
                }
                else
                {
                    rootList.Add(bone);
                }

                skeletonBones[boneIndex] = bone;
                boneMap.Add(bone);
            }
        }

        var boneMaps = rootList.Select(root =>
        {
            var bones = NodeBuilder.Flatten(root).Cast<BoneNodeBuilder>().ToList();
            if (!NodeBuilder.IsValidArmature(bones))
            {
                throw new InvalidOperationException(
                    $"Armature is invalid, {string.Join(", ", bones.Select(bone => bone.BoneName))}");
            }

            return (List: bones, Root: root);
        }).ToArray();

        if (poseMode == PoseMode.None)
        {
            return boneMaps;
        }

        foreach (var map in boneMaps)
        {
            foreach (var bone in map.List)
            {
                var transform = GetBoneTransform(skeleton, bone);
                if (transform != null)
                {
                    AddBoneKeyframe(bone, poseMode, 0, transform.Value);
                }
            }
        }

        return boneMaps;
    }

    public static List<BoneNodeBuilder> GetBoneMap(
        ParsedSkeleton skeleton,
        PoseMode poseMode,
        out BoneNodeBuilder? root)
    {
        var maps = GetBoneMaps(skeleton, poseMode);
        if (maps.Length == 0)
        {
            root = null;
            return [];
        }

        // The Air-Wheeler A9 mount has a second n_pluslayer root. Prefer the
        // ordinary n_root armature whenever a skeleton exposes multiple roots.
        var rootMap = maps.FirstOrDefault(map =>
            map.Root.BoneName.Equals("n_root", StringComparison.OrdinalIgnoreCase));
        if (rootMap != default)
        {
            root = rootMap.Root;
            return rootMap.List;
        }

        root = maps[0].Root;
        return maps[0].List;
    }

    public static AffineTransform? GetBoneTransform(ParsedSkeleton skeleton, BoneNodeBuilder bone)
    {
        var partial = skeleton.PartialSkeletons[bone.PartialSkeletonIndex];
        if (partial.Poses.Count == 0)
        {
            return null;
        }

        var transform = partial.Poses[0].Pose[bone.BoneIndex].AffineTransform;
        if (bone.Parent is BoneNodeBuilder)
        {
            return transform;
        }

        return new AffineTransform(
            transform.Scale * skeleton.Transform.Scale,
            transform.Rotation,
            transform.Translation);
    }

    private static void AddBoneKeyframe(
        BoneNodeBuilder bone,
        PoseMode poseMode,
        float time,
        AffineTransform transform)
    {
        bone.UseScale().UseTrackBuilder("pose").WithPoint(time, transform.Scale);
        if (poseMode == PoseMode.LocalScaleOnly)
        {
            return;
        }

        bone.UseRotation().UseTrackBuilder("pose").WithPoint(time, transform.Rotation);
        bone.UseTranslation().UseTrackBuilder("pose").WithPoint(time, transform.Translation);
    }
}
