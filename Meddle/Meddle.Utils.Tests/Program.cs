using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Meddle.Utils.Files;

var tests = new (string Name, Action Body)[]
{
    ("extracts bounded metadata and static mesh", ExtractsBoundedMetadataAndStaticMesh),
    ("classifies the shared sync clip as metadata", ClassifiesSharedSyncClip),
    ("marks dynamic meshless effects as unsupported Apricot", MarksDynamicMeshlessEffectUnsupported),
    ("rejects malformed chunk bounds", RejectsMalformedChunkBounds),
    ("honors configured chunk limit", HonorsConfiguredChunkLimit),
    ("honors decoded geometry memory budget", HonorsDecodedGeometryMemoryBudget),
    ("bounds retained model warnings", BoundsRetainedModelWarnings),
    ("keeps invalid mesh indices out of previews", KeepsInvalidMeshIndicesOutOfPreviews),
    ("discovers bounded Penumbra player PAP keys", DiscoversBoundedPenumbraPlayerPapKeys),
    ("rejects unsafe or non-player Penumbra PAP keys", RejectsUnsafePenumbraPapKeys),
    ("honors Penumbra PAP discovery limit", HonorsPenumbraPapDiscoveryLimit),
    ("honors Penumbra JSON depth limit", HonorsPenumbraJsonDepthLimit),
};

var failed = 0;
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void ExtractsBoundedMetadataAndStaticMesh()
{
    var vertices = DrawVertex(
        new Vector4(1, 2, 3, 1),
        new Vector3(0, 1, 0),
        new Vector3(1, 0, 0),
        new Vector4(1, 0.5f, 0.25f, 1),
        new Vector2(0.25f, 0.75f));
    var draw = vertices.Concat(vertices).Concat(vertices).ToArray();
    var indices = new byte[6];
    BinaryPrimitives.WriteInt16LittleEndian(indices.AsSpan(0, 2), 0);
    BinaryPrimitives.WriteInt16LittleEndian(indices.AsSpan(2, 2), 1);
    BinaryPrimitives.WriteInt16LittleEndian(indices.AsSpan(4, 2), 2);
    var emitter = new byte[28];
    WriteSingle(emitter, 0, 4.0f);
    WriteSingle(emitter, 16, 1.0f);
    emitter[24] = 255;
    emitter[27] = 255;
    var number = new byte[2];

    var bytes = Root(
        Chunk("Ptcl", Chunk("PrVT", Int32((int)AvfxParticleType.Quad))),
        Chunk("Tex", Encoding.ASCII.GetBytes("vfx/test/example.atex\0")),
        Chunk("Modl",
            Chunk("VDrw", draw),
            Chunk("VIdx", indices),
            Chunk("VEmt", emitter),
            Chunk("VNum", number)));

    var analysis = AvfxAnalyzer.Analyze(bytes, "vfx/test/example.avfx");
    Equal(AvfxPreviewStatus.StaticEmbeddedMeshPreview, analysis.PreviewStatus);
    True(analysis.RequiresApricotRuntime, "a particle graph still requires Apricot despite its static mesh preview");
    Equal(1, analysis.RenderableModelCount);
    Equal(1, analysis.ParticleTypeHistogram[(int)AvfxParticleType.Quad]);
    Equal("vfx/test/example.atex", analysis.ReferencedTexturePaths.Single());
    Equal(3, analysis.EmbeddedModels.Single().Vertices.Count);
    Equal(1, analysis.EmbeddedModels.Single().Triangles.Count);
    Equal(1, analysis.EmbeddedModels.Single().EmitterVertices.Count);
    Equal(1, analysis.EmbeddedModels.Single().EmitterVertexNumbers.Count);
    Approximately(new Vector3(0, 1, 0), analysis.EmbeddedModels.Single().Vertices[0].Normal);
    Equal("Ptcl", analysis.Root.Children[0].Name);
    Equal("PrVT", analysis.Root.Children[0].Children.Single().Name);
}

static void ClassifiesSharedSyncClip()
{
    var bytes = Root(Chunk("Emit", Chunk("EmVT", Int32(0))));
    var analysis = AvfxAnalyzer.Analyze(bytes, "VFX\\COMMON\\EFF\\SYNCACTIONTIMELINECLIP01T.AVFX");
    True(analysis.IsSyncControl, "canonical sync path should be recognized case-insensitively");
    Equal(AvfxPreviewStatus.MetadataOnly, analysis.PreviewStatus);
    True(!analysis.RequiresApricotRuntime, "shared sync-control clip must not be advertised as visible VFX");
}

static void MarksDynamicMeshlessEffectUnsupported()
{
    var bytes = Root(Chunk("Ptcl", Chunk("PrVT", Int32((int)AvfxParticleType.Powder))));
    var analysis = AvfxAnalyzer.Analyze(bytes, "vfx/test/powder.avfx");
    Equal(AvfxPreviewStatus.UnsupportedApricot, analysis.PreviewStatus);
    True(analysis.RequiresApricotRuntime, "dynamic effect should require Apricot");
}

static void RejectsMalformedChunkBounds()
{
    var bytes = Root(Chunk("Tex", Encoding.ASCII.GetBytes("x.atex\0")));
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, 4), int.MaxValue);
    True(!AvfxAnalyzer.TryAnalyze(bytes, null, out var analysis, out var error), "malformed size must fail");
    True(analysis is null, "failed parse must not return partial analysis");
    True(!string.IsNullOrWhiteSpace(error), "failed parse should explain the problem");
}

static void HonorsConfiguredChunkLimit()
{
    var bytes = Root(Chunk("Tex", new byte[] { 0 }), Chunk("Tex", new byte[] { 0 }));
    var options = AvfxAnalysisOptions.Default with { MaximumChunkCount = 1 };
    True(!AvfxAnalyzer.TryAnalyze(bytes, null, out _, out var error, options), "chunk budget must be enforced");
    True(error?.Contains("chunk safety limit", StringComparison.Ordinal) == true, "chunk-limit error should be specific");
}

static void HonorsDecodedGeometryMemoryBudget()
{
    var vertex = DrawVertex(
        new Vector4(0, 0, 0, 1),
        Vector3.UnitY,
        Vector3.UnitX,
        Vector4.One,
        Vector2.Zero);
    var bytes = Root(Chunk("Modl", Chunk("VDrw", vertex.Concat(vertex).ToArray())));
    var options = AvfxAnalysisOptions.Default with { MaximumDecodedGeometryBytes = 127 };
    True(!AvfxAnalyzer.TryAnalyze(bytes, null, out _, out var error, options),
        "decoded geometry memory budget must be enforced before allocation");
    True(error?.Contains("decoded geometry", StringComparison.Ordinal) == true,
        "decoded-geometry error should be specific");
}

static void BoundsRetainedModelWarnings()
{
    var invalidVertex = DrawVertex(
        new Vector4(float.NaN, 0, 0, 1),
        Vector3.UnitY,
        Vector3.UnitX,
        Vector4.One,
        Vector2.Zero);
    var draw = invalidVertex.Concat(invalidVertex).Concat(invalidVertex).ToArray();
    var bytes = Root(Chunk("Modl", Chunk("VDrw", draw)));
    var options = AvfxAnalysisOptions.Default with { MaximumWarnings = 1 };

    var analysis = AvfxAnalyzer.Analyze(bytes, "vfx/test/warning-budget.avfx", options);
    var modelWarnings = analysis.EmbeddedModels.Single().Warnings;
    True(modelWarnings.Count <= 2, "model warning retention must stay bounded plus one omission summary");
    True(modelWarnings.Any(warning => warning.Contains("omitted", StringComparison.Ordinal)),
        "bounded model warnings should report their omitted count");
    True(analysis.Warnings.Count <= 1, "top-level warning retention must honor its configured limit");
}

static void KeepsInvalidMeshIndicesOutOfPreviews()
{
    var vertex = DrawVertex(
        new Vector4(0, 0, 0, 1),
        Vector3.UnitY,
        Vector3.UnitX,
        Vector4.One,
        Vector2.Zero);
    var indices = new byte[6];
    BinaryPrimitives.WriteInt16LittleEndian(indices.AsSpan(0, 2), 0);
    BinaryPrimitives.WriteInt16LittleEndian(indices.AsSpan(2, 2), 0);
    BinaryPrimitives.WriteInt16LittleEndian(indices.AsSpan(4, 2), 12);
    var bytes = Root(Chunk("Modl", Chunk("VDrw", vertex), Chunk("VIdx", indices)));

    var analysis = AvfxAnalyzer.Analyze(bytes, "vfx/test/bad-model.avfx");
    Equal(AvfxPreviewStatus.MetadataOnly, analysis.PreviewStatus);
    True(!analysis.EmbeddedModels.Single().IsRenderable, "out-of-range triangles must not reach the writer");
    True(analysis.Warnings.Any(warning => warning.Contains("outside VDrw", StringComparison.Ordinal)),
        "invalid triangle should be reported");
}

static void DiscoversBoundedPenumbraPlayerPapKeys()
{
    const string json = """
        {
          "Options": [
            {
              "Files": {
                "chara/human/c0801/animation/a0001/bt_common/emote/s_pose02_loop.pap": "female top/1/s_pose02_loop.pap",
                "chara\\human\\c0801\\animation\\a0001\\bt_common\\emote\\s_pose01_loop.pap": "female bottom/normal/s_pose01_loop.pap",
                "chara/human/c0201/animation/a0001/bt_common/emote/s_pose01_loop.pap": "other race.pap",
                "chara/human/c0101/animation/a0001/bt_common/emote/s_pose01_loop.pap": "fallback.pap"
              }
            },
            {
              "Files": {
                "CHARA/HUMAN/C0801/ANIMATION/A0001/BT_COMMON/EMOTE/S_POSE01_LOOP.PAP": "duplicate.pap"
              }
            }
          ]
        }
        """;
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
    var paths = PenumbraAnimationManifestDiscovery.ReadBodyPapReferences(stream, 801, 16);

    Equal(3, paths.Count);
    Equal((ushort)801, paths[0].SourceRaceCode);
    True(paths.Any(item => item.AnimationKey == "emote/s_pose01_loop"), "target pose01 path should be discovered");
    True(paths.Any(item => item.AnimationKey == "emote/s_pose02_loop"), "target pose02 path should be discovered");
    True(paths.Any(item => item.SourceRaceCode == 101), "common-rig fallback should be retained");
    True(paths.All(item => item.SourceRaceCode is 801 or 101), "unrelated race paths must be filtered");
}

static void RejectsUnsafePenumbraPapKeys()
{
    var rejected = new[]
    {
        "C:/mods/file.pap",
        "../chara/human/c0801/animation/a0001/bt_common/emote/test.pap",
        "chara/human/c0801/animation/a0001/bt_common/../secret.pap",
        "chara/human/c0801/animation/a0001/bt_common//emote/test.pap",
        "chara/human/c0801/animation/a0001/bt_job/emote/test.pap",
        "chara/human/c0801/animation/a0002/bt_common/emote/test.pap",
        "chara/monster/c0801/animation/a0001/bt_common/emote/test.pap",
        "chara/human/c9999/animation/a0001/bt_common/emote/test.pap",
        "chara/human/c0801/animation/a0001/bt_common/emote/test.avfx",
    };
    foreach (var value in rejected)
    {
        True(
            !PenumbraAnimationManifestDiscovery.TryParseCanonicalBodyPapPath(value, out _),
            $"unsafe or unsupported path should be rejected: {value}");
    }
}

static void HonorsPenumbraPapDiscoveryLimit()
{
    const string json = """
        {
          "chara/human/c0801/animation/a0001/bt_common/emote/one.pap": "one.pap",
          "chara/human/c0801/animation/a0001/bt_common/emote/two.pap": "two.pap"
        }
        """;
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
    try
    {
        _ = PenumbraAnimationManifestDiscovery.ReadBodyPapReferences(stream, 801, 1);
        throw new InvalidOperationException("path limit should have rejected the second unique PAP");
    }
    catch (InvalidDataException exception)
    {
        True(exception.Message.Contains("path safety limit", StringComparison.Ordinal),
            "path-limit error should be specific");
    }
}

static void HonorsPenumbraJsonDepthLimit()
{
    var json = new string('[', PenumbraAnimationManifestDiscovery.MaximumJsonDepth + 1)
               + "0"
               + new string(']', PenumbraAnimationManifestDiscovery.MaximumJsonDepth + 1);
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
    try
    {
        _ = PenumbraAnimationManifestDiscovery.ReadBodyPapReferences(stream, 801, 1);
        throw new InvalidOperationException("JSON depth limit should reject deeply nested metadata");
    }
    catch (System.Text.Json.JsonException)
    {
        // Expected: JsonDocument enforces the same limit used by live imports.
    }
}

static byte[] Root(params byte[][] chunks) => Chunk("AVFX", chunks);

static byte[] Chunk(string name, params byte[][] contentParts)
{
    if (name.Length is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(name));
    var contents = Concat(contentParts);
    var padding = (4 - (contents.Length & 3)) & 3;
    var bytes = new byte[8 + contents.Length + padding];
    var encoded = Encoding.ASCII.GetBytes(name).Reverse().ToArray();
    encoded.CopyTo(bytes, 4 - encoded.Length);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), contents.Length);
    contents.CopyTo(bytes, 8);
    return bytes;
}

static byte[] Concat(params byte[][] values) => values.SelectMany(value => value).ToArray();

static byte[] Int32(int value)
{
    var bytes = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
    return bytes;
}

static byte[] DrawVertex(
    Vector4 position,
    Vector3 normal,
    Vector3 tangent,
    Vector4 color,
    Vector2 uv1)
{
    var bytes = new byte[36];
    WriteHalf(bytes, 0, position.X);
    WriteHalf(bytes, 2, position.Y);
    WriteHalf(bytes, 4, position.Z);
    WriteHalf(bytes, 6, position.W);
    WriteDirection(bytes, 8, normal);
    WriteDirection(bytes, 12, tangent);
    bytes[16] = ToByte(color.X);
    bytes[17] = ToByte(color.Y);
    bytes[18] = ToByte(color.Z);
    bytes[19] = ToByte(color.W);
    for (var offset = 20; offset < 36; offset += 4)
    {
        WriteHalf(bytes, offset, uv1.X);
        WriteHalf(bytes, offset + 2, uv1.Y);
    }

    return bytes;
}

static void WriteDirection(byte[] bytes, int offset, Vector3 value)
{
    value = Vector3.Normalize(value) * 127.0f;
    bytes[offset] = (byte)((int)value.X + 128);
    bytes[offset + 1] = (byte)((int)value.Y + 128);
    bytes[offset + 2] = (byte)((int)value.Z + 128);
    bytes[offset + 3] = 127;
}

static void WriteHalf(byte[] bytes, int offset, float value) =>
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), BitConverter.HalfToUInt16Bits((Half)value));

static void WriteSingle(byte[] bytes, int offset, float value) =>
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));

static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void Approximately(Vector3 expected, Vector3 actual)
{
    if (Vector3.Distance(expected, actual) > 0.001f)
    {
        throw new InvalidOperationException($"Expected approximately '{expected}', got '{actual}'.");
    }
}
