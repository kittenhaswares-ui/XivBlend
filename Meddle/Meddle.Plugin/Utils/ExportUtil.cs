using System.Numerics;
using Meddle.Plugin.Models;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;

namespace Meddle.Plugin.Utils;

public static class ExportUtil
{
    private static readonly WriteSettings WriteSettings = new WriteSettings
    {
        Validation = ValidationMode.TryFix,
        JsonIndented = false,
    };
    
    public static string SanitizeFileName(this string name)
    {
        // convert name to A-Z0-9_-
        name = string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')).Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = "model";
        }
        return name;
    }
    
    public static void SaveAsType(ModelRoot? gltf, ExportType typeFlags, string path, string name)
    {
        name = name.SanitizeFileName();
        
        if (typeFlags.HasFlag(ExportType.GLTF))
        {
            gltf?.SaveGLTF(Path.Combine(path, name + ".gltf"), WriteSettings);
        }
        
        if (typeFlags.HasFlag(ExportType.GLB))
        {
            gltf?.SaveGLB(Path.Combine(path, name + ".glb"), WriteSettings);
        }
        
        if (typeFlags.HasFlag(ExportType.OBJ))
        {
            // sanitize obj name
            var objName = name.Replace(" ", "_");
            gltf?.SaveAsWavefront(Path.Combine(path, objName + ".obj"));
        }
    }
    
    public static float[] AsFloatArray(this Vector4 v) => new[] { v.X, v.Y, v.Z, v.W };
    public static float[] AsFloatArray(this Vector3 v) => new[] { v.X, v.Y, v.Z };
    public static float[] AsFloatArray(this Vector2 v) => new[] { v.X, v.Y };
}
