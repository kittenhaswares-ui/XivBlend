using System.Text.Json;
using System.Text.Json.Serialization;
using Meddle.Utils.Constants;
using Meddle.Utils.Export;
using Meddle.Utils.Helpers;
using Microsoft.Extensions.Logging;
using SharpGLTF.Geometry;
using SharpGLTF.Materials;

namespace Meddle.Utils;

public static class ModelBuilder
{
    private static JsonSerializerOptions SerializerOptions => new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    
    public static IReadOnlyList<MeshExport> BuildMeshes(
        Model model,
        IReadOnlyList<MaterialBuilder?> materials,
        IReadOnlyList<BoneNodeBuilder>? boneMap,
        (GenderRace fromDeform, GenderRace toDeform, RaceDeformer deformer)? raceDeformer,
        MeshBuilderOptions? options = null,
        Action<int, int, bool>? onMissingMaterial = null)
    {
        var meshes = new List<MeshExport>();

        var modelPathName = Path.GetFileNameWithoutExtension(model.HandlePath.TrimHandlePath());
        const int maxNodeNameLength = 63 - 16; // to account for suffixes
        if (modelPathName.Length > maxNodeNameLength)
        {
            var segments = modelPathName.Split('/');
            if (segments.Length > 0)
            {
                var fileSegment = segments.Last();
                modelPathName = fileSegment;
            }
        }
        foreach (var mesh in model.Meshes)
        {
            MeshBuilder meshBuilder;
            MaterialBuilder material;
            if (mesh.MaterialIdx >= materials.Count)
            {
                Global.Logger.LogWarning("[{Path}] Skipping mesh {MeshIdx} with invalid material index {MaterialIdx}",
                                         model.HandlePath,
                                         mesh.MeshIdx,
                                         mesh.MaterialIdx);
                onMissingMaterial?.Invoke(mesh.MaterialIdx, mesh.MeshIdx, true);
                continue;
            }
            else
            {
                var resolvedMaterial = materials[mesh.MaterialIdx];
                if (resolvedMaterial is null)
                {
                    Global.Logger.LogWarning(
                        "[{Path}] Skipping mesh {MeshIdx} because material slot {MaterialIdx} is not loaded",
                        model.HandlePath,
                        mesh.MeshIdx,
                        mesh.MaterialIdx);
                    onMissingMaterial?.Invoke(mesh.MaterialIdx, mesh.MeshIdx, false);
                    continue;
                }

                material = resolvedMaterial;
            }
            
            if (mesh.BoneTable != null && boneMap != null)
            {
                meshBuilder = new MeshBuilder(mesh, boneMap, material, raceDeformer, options);
            }
            else if (mesh.BoneTable != null && boneMap == null)
            {
                // Global.Logger.LogWarning("[{Path}] Mesh {MeshIdx} has bone table but no bone map was provided",
                //                          model.HandlePath,
                //                          mesh.MeshIdx);
                meshBuilder = new MeshBuilder(mesh, null, material, raceDeformer, options);
            }
            else
            {
                meshBuilder = new MeshBuilder(mesh, null, material, raceDeformer, options);
            }

            Global.Logger.LogDebug("[{Path}] Building mesh {MeshIdx}\n{Mesh}",
                                   model.HandlePath,
                                   mesh.MeshIdx,
                                   JsonSerializer.Serialize(new
                                   {
                                       Material = material.Name,
                                       GeometryType = meshBuilder.GeometryT.Name,
                                       MaterialType = meshBuilder.MaterialT.Name,
                                       SkinningType = meshBuilder.SkinningT.Name,
                                       Vertex = (Vertex?)(mesh.Vertices.Count == 0 ? null : mesh.Vertices[0]),
                                   }, SerializerOptions));
            
            if (mesh.SubMeshes.Count == 0)
            {
                var mb = meshBuilder.BuildMesh();
                mb.Name = modelPathName;
                meshes.Add(new MeshExport(mb, null, null));
                continue;
            }

            for (var i = 0; i < mesh.SubMeshes.Count; i++)
            {
                var modelSubMesh = mesh.SubMeshes[i];
                var (subMesh, indexMapping) = meshBuilder.BuildSubMesh(modelSubMesh);
                subMesh.Name = $"{modelPathName}_{i}";
                var subMeshStart = (int)modelSubMesh.IndexOffset;
                var subMeshEnd = subMeshStart + (int)modelSubMesh.IndexCount;

                var shapeNames = meshBuilder.BuildShapes(model.Shapes, subMesh, indexMapping, subMeshStart, subMeshEnd);

                meshes.Add(new MeshExport(subMesh, modelSubMesh, shapeNames.ToArray()));
            }
        }

        return meshes;
    }

    public record MeshExport(
        IMeshBuilder<MaterialBuilder> Mesh,
        SubMesh? Submesh,
        IReadOnlyList<string>? Shapes);
}
