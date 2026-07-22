using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using Meddle.Plugin.Models.Composer;
using Meddle.Plugin.Models.Skeletons;
using Meddle.Plugin.Models.Structs;
using Meddle.Plugin.Utils;
using Meddle.Utils.Constants;
using Meddle.Utils.Export;
using Meddle.Utils.Files;
using Meddle.Utils.Files.Structs.Material;
using CustomizeData = Meddle.Utils.Export.CustomizeData;
using CustomizeParameter = Meddle.Utils.Export.CustomizeParameter;

namespace Meddle.Plugin.Models.Layout;

public class ParsedTextureInfo(string path, string pathFromMaterial, TextureResource resource)
{
    public HandleString Path { get; } = new() { FullPath = path, GamePath = pathFromMaterial };
    public TextureResource Resource { get; } = resource;
}

public class ParsedMaterialInfo(
    string path,
    string pathFromModel,
    string shpk,
    OnRenderMaterialOutput? renderMaterialOutput,
    IColorTableSet? colorTable,
    ParsedTextureInfo[] textures)
{
    public HandleString Path { get; } = new() { FullPath = path, GamePath = pathFromModel };
    public string Shpk { get; } = shpk;
    public OnRenderMaterialOutput? RenderMaterialOutput { get; } = renderMaterialOutput;
    public ParsedStain? Stain0 { get; init; }
    public ParsedStain? Stain1 { get; init; }

    [JsonIgnore]
    public IColorTableSet? ColorTable { get; } = colorTable;

    public object? ColorTableBlob => ColorTable switch
    {
        ColorTableSet colorTableSet => colorTableSet.ToObject(),
        LegacyColorTableSet legacyColorTableSet => legacyColorTableSet.ToObject(),
        _ => null
    };

    public ParsedTextureInfo[] Textures { get; } = textures;

    public string GetHash()
    {
        var hash = new StringBuilder();
        hash.Append($"FullPath: {Path.FullPath} GamePath: {Path.GamePath} ");
        hash.Append($"Shpk: {Shpk} ");
        for (var i = 0; i < Textures.Length; i++)
        {
            var texture = Textures[i];
            hash.Append($"Texture{i}: {texture.Path.FullPath} {texture.Path.GamePath}");
        }

        hash.Append($"Stain0: {Stain0?.RowId}");
        hash.Append($"Stain1: {Stain1?.RowId}");
        if (ColorTableBlob != null)
        {
            hash.Append($"ColorTable: {JsonSerializer.Serialize(ColorTableBlob, MaterialComposer.JsonOptions)}");
        }

        return hash.ToString();
    }
}

public class ParsedModelInfo(
    string path,
    string pathFromCharacter,
    bool enabled,
    DeformerCachedStruct? deformer,
    Model.ShapeAttributeGroup? shapeAttributeGroup,
    ParsedMaterialInfo?[] materials,
    Stain? stain0,
    Stain? stain1)
{
    public HandleString Path { get; } = new() { FullPath = path, GamePath = pathFromCharacter };
    public bool Enabled { get; } = enabled;
    public ParsedStain? Stain0 { get; } = stain0;
    public ParsedStain? Stain1 { get; } = stain1;
    public DeformerCachedStruct? Deformer { get; } = deformer;
    public Model.ShapeAttributeGroup? ShapeAttributeGroup { get; } = shapeAttributeGroup;
    public ParsedMaterialInfo?[] Materials { get; } = materials;
    public nint ModelAddress { get; set; }
}

public struct HandleString
{
    public string FullPath;
    public string GamePath;

    public static implicit operator HandleString(string path) => new() { FullPath = path, GamePath = path };
}

public record ParsedStain
{
    public ParsedStain(Stain stain)
    {
        SeColor = stain.Color;
        Color = new Vector4(
            ((SeColor >> 16) & 0xFF) / 255f,
            ((SeColor >> 8) & 0xFF) / 255f,
            (SeColor & 0xFF) / 255f,
            1f);
        RowId = stain.RowId;
        Name = stain.Name.ExtractText();
        Shade = stain.Shade;
        SubOrder = stain.SubOrder;
    }

    public uint SeColor { get; }
    public Vector4 Color { get; }
    public uint RowId { get; }
    public string Name { get; }
    public uint Shade { get; }
    public uint SubOrder { get; }

    public static implicit operator ParsedStain(Stain stain) => new(stain);
    public static implicit operator ParsedStain?(Stain? stain) => stain == null ? null : new ParsedStain(stain.Value);
}

public record ParsedCharacterInfo
{
    public IReadOnlyList<ParsedModelInfo> Models;
    public readonly ParsedSkeleton Skeleton;
    public readonly ParsedAttach Attach;
    public readonly ParsedHumanInfo HumanInfo;
    public IReadOnlyList<ParsedCharacterInfo> Attaches = [];
    public readonly DateTime ParsedAt = DateTime.UtcNow;
    public CustomizeData? CustomizeData => HumanInfo.CustomizeData;
    public CustomizeParameter? CustomizeParameter => HumanInfo.CustomizeParameter;
    public IReadOnlyList<EquipmentModelId> EquipmentModelIds => HumanInfo.EquipmentModelIds;
    public GenderRace GenderRace => HumanInfo.GenderRace;

    public ParsedCharacterInfo(
        ParsedModelInfo[] models,
        ParsedSkeleton skeleton,
        ParsedAttach attach,
        ParsedHumanInfo humanInfo)
    {
        Models = models;
        Skeleton = skeleton;
        Attach = attach;
        HumanInfo = humanInfo;
    }
}
