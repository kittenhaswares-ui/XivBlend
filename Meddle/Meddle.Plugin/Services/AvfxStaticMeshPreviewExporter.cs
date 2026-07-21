using System.Numerics;
using Meddle.Utils.Files;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Meddle.Plugin.Services;

public sealed record AvfxStaticMeshPreviewExportResult(
    string OutputPath,
    int ModelCount,
    int VertexCount,
    int TriangleCount);

/// <summary>
/// Writes only the static draw buffers embedded in an AVFX file.  The output is
/// reference geometry, not an emote VFX: no Apricot scheduler, emitter, binder,
/// material, texture, curve, billboard or depth behavior is evaluated here.
/// </summary>
public static class AvfxStaticMeshPreviewExporter
{
    public static AvfxStaticMeshPreviewExportResult WriteGlb(AvfxAnalysis analysis, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!string.Equals(Path.GetExtension(outputPath), ".glb", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The static AVFX mesh preview output must use the .glb extension.", nameof(outputPath));
        }

        var renderable = analysis.EmbeddedModels.Where(model => model.IsRenderable).ToArray();
        if (analysis.PreviewStatus != AvfxPreviewStatus.StaticEmbeddedMeshPreview || renderable.Length == 0)
        {
            throw new InvalidOperationException("This AVFX analysis has no validated static embedded mesh preview.");
        }

        var scene = new SceneBuilder();
        var vertexCount = 0;
        var triangleCount = 0;
        foreach (var model in renderable)
        {
            var mesh = new MeshBuilder<VertexPositionNormalTangent, VertexColor1Texture4>(
                $"AVFX Static Preview {model.ModelIndex}");
            var material = new MaterialBuilder($"AVFX Preview Placeholder {model.ModelIndex}");
            var primitive = mesh.UsePrimitive(material);
            var vertices = model.Vertices.Select(ToVertex).ToArray();
            foreach (var triangle in model.Triangles)
            {
                primitive.AddTriangle(vertices[triangle.A], vertices[triangle.B], vertices[triangle.C]);
            }

            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            vertexCount += model.Vertices.Count;
            triangleCount += model.Triangles.Count;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        scene.ToGltf2().SaveGLB(outputPath);
        return new AvfxStaticMeshPreviewExportResult(outputPath, renderable.Length, vertexCount, triangleCount);
    }

    private static (VertexPositionNormalTangent Geometry, VertexColor1Texture4 Material) ToVertex(
        AvfxEmbeddedVertex vertex) =>
        (
            new VertexPositionNormalTangent(
                new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z),
                vertex.Normal,
                vertex.Tangent),
            new VertexColor1Texture4(
                vertex.Color,
                vertex.Uv1,
                vertex.Uv2,
                vertex.Uv3,
                vertex.Uv4)
        );
}
