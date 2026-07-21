using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;

namespace Meddle.Utils.Files;

/// <summary>
/// The deliberately limited result of inspecting an AVFX file.  This is not an
/// Apricot particle simulation and must not be presented as one.
/// </summary>
public enum AvfxPreviewStatus
{
    /// <summary>The file carries metadata/control state, but no visual preview is produced.</summary>
    MetadataOnly,

    /// <summary>
    /// One or more embedded draw meshes can be exported as static reference
    /// geometry.  Scheduling, particles, shaders and binders are not evaluated.
    /// </summary>
    StaticEmbeddedMeshPreview,

    /// <summary>The visual result depends on FFXIV's Apricot runtime.</summary>
    UnsupportedApricot,
}

/// <summary>Known values of the AVFX <c>PrVT</c> particle-type field.</summary>
public enum AvfxParticleType
{
    Parameter = 0,
    Powder = 1,
    Windmill = 2,
    Line = 3,
    Laser = 4,
    Model = 5,
    Polyline = 6,
    Reserve0 = 7,
    Quad = 8,
    Polygon = 9,
    Decal = 10,
    DecalRing = 11,
    Disc = 12,
    LightModel = 13,
    ModelSkin = 14,
    Dissolve = 15,
}

/// <summary>Hard limits used while reading untrusted or mod-provided AVFX bytes.</summary>
public sealed record AvfxAnalysisOptions
{
    public static AvfxAnalysisOptions Default { get; } = new();

    public int MaximumFileBytes { get; init; } = 32 * 1024 * 1024;
    public int MaximumChunkCount { get; init; } = 65_536;
    public int MaximumDepth { get; init; } = 4;
    public int MaximumModels { get; init; } = 512;
    public int MaximumVertices { get; init; } = 250_000;
    public int MaximumTriangles { get; init; } = 500_000;
    public int MaximumEmitterVertices { get; init; } = 250_000;
    public long MaximumDecodedGeometryBytes { get; init; } = 64L * 1024 * 1024;
    public int MaximumTexturePaths { get; init; } = 4_096;
    public int MaximumTexturePathBytes { get; init; } = 4_096;
    public int MaximumWarnings { get; init; } = 1_024;

    internal void Validate()
    {
        if (MaximumFileBytes < 8 || MaximumChunkCount < 1 || MaximumDepth < 1
            || MaximumModels < 1 || MaximumVertices < 1 || MaximumTriangles < 1
            || MaximumEmitterVertices < 1 || MaximumDecodedGeometryBytes < 1
            || MaximumTexturePaths < 1
            || MaximumTexturePathBytes < 1 || MaximumWarnings < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(AvfxAnalysisOptions), "All AVFX analysis limits must be positive.");
        }
    }
}

/// <summary>A structurally validated AVFX chunk and its recognized child chunks.</summary>
public sealed record AvfxChunkNode(
    string Name,
    int HeaderOffset,
    int ContentOffset,
    int ContentSize,
    int PaddingSize,
    IReadOnlyList<AvfxChunkNode> Children);

/// <summary>
/// A 36-byte vertex from an AVFX <c>VDrw</c> block.  Normal and tangent are
/// normalized for consumers; all four UV sets and vertex color are preserved.
/// </summary>
public sealed record AvfxEmbeddedVertex(
    Vector4 Position,
    Vector3 Normal,
    Vector4 Tangent,
    Vector4 Color,
    Vector2 Uv1,
    Vector2 Uv2,
    Vector2 Uv3,
    Vector2 Uv4);

public sealed record AvfxEmbeddedTriangle(int A, int B, int C);

/// <summary>A 28-byte vertex used to distribute particles over an emitter model.</summary>
public sealed record AvfxEmitterVertex(Vector3 Position, Vector3 Normal, Vector4 Color);

/// <summary>
/// Geometry embedded in one AVFX <c>Modl</c> chunk.  Draw geometry is suitable
/// only for a static preview; emitter vertices are metadata for future Apricot
/// simulation and are not themselves rendered.
/// </summary>
public sealed record AvfxEmbeddedModel(
    int ModelIndex,
    IReadOnlyList<AvfxEmbeddedVertex> Vertices,
    IReadOnlyList<AvfxEmbeddedTriangle> Triangles,
    IReadOnlyList<AvfxEmitterVertex> EmitterVertices,
    IReadOnlyList<short> EmitterVertexNumbers,
    bool IsRenderable,
    IReadOnlyList<string> Warnings);

public sealed record AvfxAnalysis(
    string? GamePath,
    bool IsSyncControl,
    AvfxPreviewStatus PreviewStatus,
    bool RequiresApricotRuntime,
    AvfxChunkNode Root,
    IReadOnlyDictionary<string, int> TopLevelChunkHistogram,
    IReadOnlyDictionary<int, int> ParticleTypeHistogram,
    IReadOnlyList<string> ReferencedTexturePaths,
    IReadOnlyList<AvfxEmbeddedModel> EmbeddedModels,
    IReadOnlyList<string> Warnings)
{
    public int RenderableModelCount => EmbeddedModels.Count(model => model.IsRenderable);
}

/// <summary>
/// Bounded, read-only inspection of FFXIV AVFX containers.  It extracts only
/// facts which are directly encoded in the file: chunk structure, particle
/// type IDs, texture paths and embedded model buffers.  It intentionally does
/// not evaluate schedulers, timelines, emitters, binders, curves or shaders.
/// </summary>
public static class AvfxAnalyzer
{
    public const string SyncControlGamePath = "vfx/common/eff/syncactiontimelineclip01t.avfx";

    private static readonly HashSet<string> NestedTopLevelChunks = new(StringComparer.Ordinal)
    {
        "Schd", "TmLn", "Emit", "Ptcl", "Efct", "Bind", "Modl",
    };

    public static AvfxAnalysis Analyze(
        ReadOnlySpan<byte> data,
        string? gamePath = null,
        AvfxAnalysisOptions? options = null)
    {
        options ??= AvfxAnalysisOptions.Default;
        options.Validate();

        if (data.Length < 8)
        {
            throw new InvalidDataException("AVFX data is shorter than its root header.");
        }

        if (data.Length > options.MaximumFileBytes)
        {
            throw new InvalidDataException($"AVFX data exceeds the {options.MaximumFileBytes:N0}-byte safety limit.");
        }

        var context = new ParseContext(options);
        var rootName = DecodeName(data[..4]);
        if (!string.Equals(rootName, "AVFX", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected AVFX root chunk, found '{rootName}'.");
        }

        var rootSize = ReadNonNegativeSize(data, 4, "AVFX");
        var rootPadding = PaddingFor(rootSize);
        var expectedLength = CheckedEnd(8, rootSize, rootPadding, "AVFX");
        if (expectedLength != data.Length)
        {
            throw new InvalidDataException(
                $"AVFX root declares {rootSize:N0} content bytes, but the file is {data.Length:N0} bytes long.");
        }

        var children = ParseSequence(data, 8, rootSize, 1, context);
        var root = new AvfxChunkNode("AVFX", 0, 8, rootSize, rootPadding, children);

        var topLevelHistogram = children
            .GroupBy(node => node.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var particleHistogram = new Dictionary<int, int>();
        foreach (var particle in children.Where(node => node.Name == "Ptcl"))
        {
            var typeNode = particle.Children.FirstOrDefault(node => node.Name == "PrVT");
            if (typeNode is null)
            {
                context.Warn("A Ptcl chunk has no PrVT particle type.");
                continue;
            }

            if (typeNode.ContentSize < sizeof(int))
            {
                context.Warn("A PrVT chunk is shorter than four bytes and was ignored.");
                continue;
            }

            var value = BinaryPrimitives.ReadInt32LittleEndian(
                data.Slice(typeNode.ContentOffset, sizeof(int)));
            particleHistogram[value] = particleHistogram.GetValueOrDefault(value) + 1;
            if (!Enum.IsDefined(typeof(AvfxParticleType), value))
            {
                context.Warn($"Unknown AVFX particle type {value} was retained as a numeric ID.");
            }
        }

        var textures = ExtractTexturePaths(data, children, context);
        var models = ExtractModels(data, children, context);
        var isSyncControl = IsSyncControlPath(gamePath);
        var hasApricotGraph = children.Any(node => node.Name is "Schd" or "TmLn" or "Emit" or "Ptcl" or "Efct" or "Bind");
        var hasRenderableModel = models.Any(model => model.IsRenderable);
        var previewStatus = isSyncControl
            ? AvfxPreviewStatus.MetadataOnly
            : hasRenderableModel
                ? AvfxPreviewStatus.StaticEmbeddedMeshPreview
                : hasApricotGraph
                    ? AvfxPreviewStatus.UnsupportedApricot
                    : AvfxPreviewStatus.MetadataOnly;

        return new AvfxAnalysis(
            NormalizeGamePath(gamePath),
            isSyncControl,
            previewStatus,
            !isSyncControl && hasApricotGraph,
            root,
            new ReadOnlyDictionary<string, int>(topLevelHistogram),
            new ReadOnlyDictionary<int, int>(particleHistogram),
            textures,
            models,
            context.Warnings.ToArray());
    }

    public static bool TryAnalyze(
        ReadOnlySpan<byte> data,
        string? gamePath,
        out AvfxAnalysis? analysis,
        out string? error,
        AvfxAnalysisOptions? options = null)
    {
        try
        {
            analysis = Analyze(data, gamePath, options);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
        {
            analysis = null;
            error = exception.Message;
            return false;
        }
    }

    public static bool IsSyncControlPath(string? gamePath) =>
        string.Equals(NormalizeGamePath(gamePath), SyncControlGamePath, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeGamePath(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return null;
        return gamePath.Trim().Replace('\\', '/').TrimStart('/');
    }

    private static IReadOnlyList<AvfxChunkNode> ParseSequence(
        ReadOnlySpan<byte> data,
        int offset,
        int size,
        int depth,
        ParseContext context)
    {
        if (depth > context.Options.MaximumDepth)
        {
            throw new InvalidDataException($"AVFX nesting exceeds the depth limit of {context.Options.MaximumDepth}.");
        }

        var end = CheckedEnd(offset, size, 0, "chunk sequence");
        if (end > data.Length)
        {
            throw new InvalidDataException("An AVFX chunk sequence extends beyond the file.");
        }

        var nodes = new List<AvfxChunkNode>();
        var cursor = offset;
        while (cursor < end)
        {
            if (end - cursor < 8)
            {
                throw new InvalidDataException("An AVFX chunk sequence ends inside a child header.");
            }

            context.CountChunk();
            var name = DecodeName(data.Slice(cursor, 4));
            if (name.Length == 0)
            {
                throw new InvalidDataException($"An AVFX child at byte {cursor:N0} has an empty name.");
            }

            var contentSize = ReadNonNegativeSize(data, cursor + 4, name);
            var contentOffset = checked(cursor + 8);
            var padding = PaddingFor(contentSize);
            var next = CheckedEnd(contentOffset, contentSize, padding, name);
            if (next > end)
            {
                throw new InvalidDataException($"AVFX chunk '{name}' extends beyond its parent.");
            }

            IReadOnlyList<AvfxChunkNode> children = Array.Empty<AvfxChunkNode>();
            if (NestedTopLevelChunks.Contains(name) && contentSize > 0)
            {
                children = ParseSequence(data, contentOffset, contentSize, depth + 1, context);
            }

            nodes.Add(new AvfxChunkNode(name, cursor, contentOffset, contentSize, padding, children));
            cursor = next;
        }

        if (cursor != end)
        {
            throw new InvalidDataException("AVFX child chunks do not exactly fill their parent.");
        }

        return nodes.ToArray();
    }

    private static IReadOnlyList<string> ExtractTexturePaths(
        ReadOnlySpan<byte> data,
        IReadOnlyList<AvfxChunkNode> children,
        ParseContext context)
    {
        var paths = new List<string>();
        foreach (var texture in children.Where(node => node.Name == "Tex"))
        {
            if (paths.Count >= context.Options.MaximumTexturePaths)
            {
                throw new InvalidDataException(
                    $"AVFX texture references exceed the {context.Options.MaximumTexturePaths:N0}-path safety limit.");
            }

            if (texture.ContentSize > context.Options.MaximumTexturePathBytes)
            {
                context.Warn($"A {texture.ContentSize:N0}-byte AVFX texture path exceeded the safety limit and was ignored.");
                continue;
            }

            var bytes = data.Slice(texture.ContentOffset, texture.ContentSize);
            var terminator = bytes.IndexOf((byte)0);
            if (terminator < 0)
            {
                context.Warn("A Tex chunk had no null terminator and was ignored.");
                continue;
            }

            bytes = bytes[..terminator];
            if (bytes.Length == 0) continue;
            if (bytes.ContainsAnyExceptInRange((byte)0x20, (byte)0x7e))
            {
                context.Warn("A Tex chunk contained a non-ASCII path and was ignored.");
                continue;
            }

            var path = Encoding.ASCII.GetString(bytes).Replace('\\', '/').TrimStart('/');
            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
        }

        return paths.ToArray();
    }

    private static IReadOnlyList<AvfxEmbeddedModel> ExtractModels(
        ReadOnlySpan<byte> data,
        IReadOnlyList<AvfxChunkNode> children,
        ParseContext context)
    {
        var modelNodes = children.Where(node => node.Name == "Modl").ToArray();
        if (modelNodes.Length > context.Options.MaximumModels)
        {
            throw new InvalidDataException(
                $"AVFX models exceed the {context.Options.MaximumModels:N0}-model safety limit.");
        }

        var models = new List<AvfxEmbeddedModel>(modelNodes.Length);
        for (var index = 0; index < modelNodes.Length; index++)
        {
            var node = modelNodes[index];
            var warnings = new List<string>();
            var vertices = new List<AvfxEmbeddedVertex>();
            var triangles = new List<AvfxEmbeddedTriangle>();
            var emitterVertices = new List<AvfxEmitterVertex>();
            var emitterNumbers = new List<short>();
            var structurallyRenderable = true;
            var omittedModelWarnings = 0;

            void WarnModel(string warning)
            {
                if (context.TryRetainModelWarning())
                {
                    warnings.Add(warning);
                }
                else
                {
                    omittedModelWarnings++;
                }
            }

            foreach (var vertexChunk in node.Children.Where(child => child.Name == "VDrw"))
            {
                if (vertexChunk.ContentSize % 36 != 0)
                {
                    WarnModel("VDrw size is not a multiple of the 36-byte AVFX vertex layout.");
                    structurallyRenderable = false;
                    continue;
                }

                var count = vertexChunk.ContentSize / 36;
                context.ReserveVertices(count);
                for (var vertexIndex = 0; vertexIndex < count; vertexIndex++)
                {
                    var vertexOffset = checked(vertexChunk.ContentOffset + (vertexIndex * 36));
                    var vertex = ReadDrawVertex(data.Slice(vertexOffset, 36));
                    if (!IsFinite(vertex))
                    {
                        if (context.TryRetainModelWarning())
                        {
                            warnings.Add($"VDrw vertex {vertexIndex} contains a non-finite value.");
                        }
                        else
                        {
                            omittedModelWarnings++;
                        }
                        structurallyRenderable = false;
                    }

                    vertices.Add(vertex);
                }
            }

            foreach (var indexChunk in node.Children.Where(child => child.Name == "VIdx"))
            {
                if (indexChunk.ContentSize % 6 != 0)
                {
                    WarnModel("VIdx size is not a multiple of the six-byte AVFX triangle layout.");
                    structurallyRenderable = false;
                    continue;
                }

                var count = indexChunk.ContentSize / 6;
                context.ReserveTriangles(count);
                for (var triangleIndex = 0; triangleIndex < count; triangleIndex++)
                {
                    var triangleOffset = checked(indexChunk.ContentOffset + (triangleIndex * 6));
                    triangles.Add(new AvfxEmbeddedTriangle(
                        BinaryPrimitives.ReadInt16LittleEndian(data.Slice(triangleOffset, 2)),
                        BinaryPrimitives.ReadInt16LittleEndian(data.Slice(triangleOffset + 2, 2)),
                        BinaryPrimitives.ReadInt16LittleEndian(data.Slice(triangleOffset + 4, 2))));
                }
            }

            foreach (var emitterChunk in node.Children.Where(child => child.Name == "VEmt"))
            {
                if (emitterChunk.ContentSize % 28 != 0)
                {
                    WarnModel("VEmt size is not a multiple of the 28-byte AVFX emitter-vertex layout.");
                    continue;
                }

                var count = emitterChunk.ContentSize / 28;
                context.ReserveEmitterVertices(count);
                for (var emitterIndex = 0; emitterIndex < count; emitterIndex++)
                {
                    var emitterOffset = checked(emitterChunk.ContentOffset + (emitterIndex * 28));
                    emitterVertices.Add(ReadEmitterVertex(data.Slice(emitterOffset, 28)));
                }
            }

            foreach (var numberChunk in node.Children.Where(child => child.Name == "VNum"))
            {
                if (numberChunk.ContentSize % 2 != 0)
                {
                    WarnModel("VNum size is not a multiple of two bytes.");
                    continue;
                }

                var count = numberChunk.ContentSize / 2;
                context.ReserveEmitterVertices(count);
                for (var numberIndex = 0; numberIndex < count; numberIndex++)
                {
                    var numberOffset = checked(numberChunk.ContentOffset + (numberIndex * 2));
                    emitterNumbers.Add(BinaryPrimitives.ReadInt16LittleEndian(data.Slice(numberOffset, 2)));
                }
            }

            if (emitterVertices.Count != emitterNumbers.Count && emitterVertices.Count + emitterNumbers.Count > 0)
            {
                WarnModel("VEmt and VNum counts differ; emitter distribution metadata is incomplete.");
            }

            if (triangles.Any(triangle => triangle.A < 0 || triangle.B < 0 || triangle.C < 0
                                          || triangle.A >= vertices.Count || triangle.B >= vertices.Count
                                          || triangle.C >= vertices.Count))
            {
                WarnModel("At least one VIdx triangle references a vertex outside VDrw.");
                structurallyRenderable = false;
            }

            if (omittedModelWarnings > 0)
            {
                warnings.Add($"{omittedModelWarnings:N0} additional model warning(s) were omitted by the safety limit.");
            }

            foreach (var warning in warnings) context.Warn($"AVFX model {index}: {warning}");
            models.Add(new AvfxEmbeddedModel(
                index,
                vertices.ToArray(),
                triangles.ToArray(),
                emitterVertices.ToArray(),
                emitterNumbers.ToArray(),
                structurallyRenderable && vertices.Count > 0 && triangles.Count > 0,
                warnings.ToArray()));
        }

        return models.ToArray();
    }

    private static AvfxEmbeddedVertex ReadDrawVertex(ReadOnlySpan<byte> bytes)
    {
        var position = new Vector4(
            ReadHalf(bytes, 0), ReadHalf(bytes, 2), ReadHalf(bytes, 4), ReadHalf(bytes, 6));
        var normal = NormalizeOrZero(new Vector3(
            bytes[8] - 128, bytes[9] - 128, bytes[10] - 128));
        var tangentDirection = NormalizeOrZero(new Vector3(
            bytes[12] - 128, bytes[13] - 128, bytes[14] - 128));
        var tangent = new Vector4(tangentDirection, 1.0f);
        var color = new Vector4(bytes[16], bytes[17], bytes[18], bytes[19]) / 255.0f;

        return new AvfxEmbeddedVertex(
            position,
            normal,
            tangent,
            color,
            new Vector2(ReadHalf(bytes, 20), ReadHalf(bytes, 22)),
            new Vector2(ReadHalf(bytes, 24), ReadHalf(bytes, 26)),
            new Vector2(ReadHalf(bytes, 28), ReadHalf(bytes, 30)),
            new Vector2(ReadHalf(bytes, 32), ReadHalf(bytes, 34)));
    }

    private static AvfxEmitterVertex ReadEmitterVertex(ReadOnlySpan<byte> bytes) => new(
        new Vector3(ReadSingle(bytes, 0), ReadSingle(bytes, 4), ReadSingle(bytes, 8)),
        new Vector3(ReadSingle(bytes, 12), ReadSingle(bytes, 16), ReadSingle(bytes, 20)),
        new Vector4(bytes[24], bytes[25], bytes[26], bytes[27]) / 255.0f);

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4)));

    private static float ReadHalf(ReadOnlySpan<byte> bytes, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)));

    private static Vector3 NormalizeOrZero(Vector3 value) =>
        value.LengthSquared() > 0.000001f ? Vector3.Normalize(value) : Vector3.Zero;

    private static bool IsFinite(AvfxEmbeddedVertex vertex) =>
        IsFinite(vertex.Position) && IsFinite(vertex.Normal) && IsFinite(vertex.Tangent)
        && IsFinite(vertex.Color) && IsFinite(vertex.Uv1) && IsFinite(vertex.Uv2)
        && IsFinite(vertex.Uv3) && IsFinite(vertex.Uv4);

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static int ReadNonNegativeSize(ReadOnlySpan<byte> data, int offset, string name)
    {
        if (offset < 0 || offset > data.Length - sizeof(int))
        {
            throw new InvalidDataException($"AVFX chunk '{name}' has a truncated size field.");
        }

        var size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(int)));
        if (size < 0) throw new InvalidDataException($"AVFX chunk '{name}' declares a negative size.");
        return size;
    }

    private static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        Span<byte> decoded = stackalloc byte[4];
        var count = 0;
        for (var index = 3; index >= 0; index--)
        {
            var value = bytes[index];
            if (value == 0) continue;
            if (value is < 0x20 or > 0x7e)
            {
                throw new InvalidDataException("An AVFX chunk name contains a non-ASCII byte.");
            }

            decoded[count++] = value;
        }

        return Encoding.ASCII.GetString(decoded[..count]);
    }

    private static int PaddingFor(int size) => (4 - (size & 3)) & 3;

    private static int CheckedEnd(int offset, int size, int padding, string name)
    {
        try
        {
            return checked(offset + size + padding);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"AVFX chunk '{name}' size overflows its address range.", exception);
        }
    }

    private sealed class ParseContext(AvfxAnalysisOptions options)
    {
        private int chunkCount;
        private int vertexCount;
        private int triangleCount;
        private int emitterVertexCount;
        private long decodedGeometryBytes;
        private int retainedModelWarningCount;

        public AvfxAnalysisOptions Options { get; } = options;
        public List<string> Warnings { get; } = [];

        public void CountChunk()
        {
            chunkCount = checked(chunkCount + 1);
            if (chunkCount > Options.MaximumChunkCount)
            {
                throw new InvalidDataException(
                    $"AVFX chunks exceed the {Options.MaximumChunkCount:N0}-chunk safety limit.");
            }
        }

        public void ReserveVertices(int count)
        {
            vertexCount = checked(vertexCount + count);
            if (vertexCount > Options.MaximumVertices)
            {
                throw new InvalidDataException(
                    $"AVFX draw vertices exceed the {Options.MaximumVertices:N0}-vertex safety limit.");
            }

            ReserveDecodedBytes(count, 128, "draw vertices");
        }

        public void ReserveTriangles(int count)
        {
            triangleCount = checked(triangleCount + count);
            if (triangleCount > Options.MaximumTriangles)
            {
                throw new InvalidDataException(
                    $"AVFX triangles exceed the {Options.MaximumTriangles:N0}-triangle safety limit.");
            }

            ReserveDecodedBytes(count, 48, "triangles");
        }

        public void ReserveEmitterVertices(int count)
        {
            emitterVertexCount = checked(emitterVertexCount + count);
            if (emitterVertexCount > Options.MaximumEmitterVertices)
            {
                throw new InvalidDataException(
                    $"AVFX emitter metadata exceeds the {Options.MaximumEmitterVertices:N0}-entry safety limit.");
            }

            ReserveDecodedBytes(count, 64, "emitter metadata");
        }

        private void ReserveDecodedBytes(int count, int bytesPerEntry, string kind)
        {
            decodedGeometryBytes = checked(decodedGeometryBytes + checked((long)count * bytesPerEntry));
            if (decodedGeometryBytes > Options.MaximumDecodedGeometryBytes)
            {
                throw new InvalidDataException(
                    $"AVFX decoded geometry exceeds the {Options.MaximumDecodedGeometryBytes:N0}-byte safety budget while reading {kind}.");
            }
        }

        public bool TryRetainModelWarning()
        {
            if (retainedModelWarningCount >= Options.MaximumWarnings) return false;
            retainedModelWarningCount++;
            return true;
        }

        public void Warn(string warning)
        {
            if (Warnings.Count < Options.MaximumWarnings) Warnings.Add(warning);
        }
    }
}
