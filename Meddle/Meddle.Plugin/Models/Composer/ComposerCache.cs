using System.Collections.Concurrent;
using Meddle.Plugin.Models.Layout;
using Meddle.Plugin.Utils;
using Meddle.Utils;
using Meddle.Utils.Constants;
using Meddle.Utils.Export;
using Meddle.Utils.Files;
using Meddle.Utils.Files.SqPack;
using Meddle.Utils.Files.Structs.Material;
using Meddle.Utils.Helpers;
using Microsoft.Extensions.Logging;
using SharpGLTF.Materials;
using SkiaSharp;

namespace Meddle.Plugin.Models.Composer;

public class ComposerCache
{
    private readonly PbdFile defaultPbdFile;
    private readonly ConcurrentDictionary<string, ShaderPackage> shpkCache = new();
    private readonly ConcurrentDictionary<string, RefCounter<MtrlFile>> mtrlCache = new();
    private readonly ConcurrentDictionary<string, string> mtrlPathCache = new();
    private readonly ConcurrentDictionary<string, PbdFile> pbdCache = new();
    private readonly ConcurrentDictionary<string, RefCounter<MdlFile>> mdlCache = new();
    private readonly ConcurrentDictionary<string, byte> trackedCacheFiles = new(StringComparer.OrdinalIgnoreCase);
    
    private sealed class RefCounter<T>(T obj)
    {
        public T Object { get; } = obj;
        public DateTime LastAccess { get; set; } = DateTime.UtcNow;
    }
    
    private readonly SqPack pack;
    private readonly string cacheDir;
    private readonly Configuration.ExportConfiguration exportConfig;
    private readonly bool refreshExistingFiles;

    public ComposerCache(
        SqPack pack,
        string cacheDir,
        Configuration.ExportConfiguration exportConfig,
        bool refreshExistingFiles = false)
    {
        this.pack = pack;
        this.cacheDir = Path.GetFullPath(cacheDir);
        this.exportConfig = exportConfig;
        this.refreshExistingFiles = refreshExistingFiles;
        defaultPbdFile = GetPbdFile("chara/xls/boneDeformer/human.pbd");
    }

    /// <summary>
    /// Returns the concrete cache files used by this composer instance. This
    /// lets the on-demand prop exporter publish a bounded integrity manifest
    /// without scanning or claiming unrelated files in the shared cache.
    /// </summary>
    public IReadOnlyList<string> GetTrackedCacheFiles()
    {
        return trackedCacheFiles.Keys
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string TrackCacheFile(string path)
    {
        var resolved = Path.GetFullPath(path);
        trackedCacheFiles.TryAdd(resolved, 0);
        return resolved;
    }

    private void TrackPngFiles(params string[] directories)
    {
        foreach (var directory in directories.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(
                         directory,
                         "*.png",
                         SearchOption.TopDirectoryOnly))
            {
                TrackCacheFile(file);
            }
        }
    }

    private static void WriteAllBytesAtomic(string path, ReadOnlySpan<byte> data)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The cache output has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.SequentialScan))
            {
                stream.Write(data);
                stream.Flush(true);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a private cache staging file.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup of a private cache staging file.
            }
        }
    }
    
    public PbdFile GetDefaultPbdFile()
    {
        return defaultPbdFile;
    }
    
    public PbdFile GetPbdFile(string path)
    {
        var item = pbdCache.GetOrAdd(path, key =>
        {
            var pbdData = pack.GetFileOrReadFromDisk(key);
            if (pbdData == null) throw new Exception($"Failed to load pbd file: {key}");

            if (exportConfig.CacheFileTypes.HasFlag(CacheFileType.Pbd))
            {
                CacheFile(key);
            }
            
            return new PbdFile(pbdData);
        });
        
        return item;
    }
    
    
    private bool arrayTexturesSaved;
    public void SaveArrayTextures()
    {
        if (arrayTexturesSaved) return;
        arrayTexturesSaved = true;
        
        Directory.CreateDirectory(cacheDir);
        ArrayTextureUtil.SaveSphereTextures(pack, cacheDir);
        ArrayTextureUtil.SaveTileTextures(pack, cacheDir);
        ArrayTextureUtil.SaveBgSphereTextures(pack, cacheDir);
        ArrayTextureUtil.SaveBgDetailTextures(pack, cacheDir);
        TrackPngFiles(Directory.GetDirectories(
            Path.Combine(cacheDir, "array_textures"),
            "*",
            SearchOption.AllDirectories));
    }

    private bool characterArrayTexturesSaved;
    public void SaveCharacterArrayTextures()
    {
        if (characterArrayTexturesSaved) return;
        characterArrayTexturesSaved = true;

        var root = Path.Combine(cacheDir, "array_textures", "chara", "common", "texture");
        var normDirectory = Path.Combine(root, "tile_norm_array");
        var orbDirectory = Path.Combine(root, "tile_orb_array");
        if (!refreshExistingFiles
            && HasVerticalArrayTexture(normDirectory, "tile_norm_array")
            && HasVerticalArrayTexture(orbDirectory, "tile_orb_array"))
        {
            TrackPngFiles(normDirectory, orbDirectory);
            return;
        }

        Directory.CreateDirectory(cacheDir);
        ArrayTextureUtil.SaveTileTextures(pack, cacheDir);
        TrackPngFiles(normDirectory, orbDirectory);
    }

    private static bool HasVerticalArrayTexture(string directory, string fileName)
    {
        try
        {
            return Directory.Exists(directory)
                   && Directory.EnumerateFiles(
                           directory,
                           $"{fileName}.*.vertical.png",
                           SearchOption.TopDirectoryOnly)
                       .Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
    
    public MdlFile GetMdlFile(string path)
    {
        var item = mdlCache.GetOrAdd(path, key =>
        {
            var mdlData = pack.GetFileOrReadFromDisk(path);
            if (mdlData == null) throw new Exception($"Failed to load model file: {path}");
            var mdlFile = new MdlFile(mdlData);
            
            if (exportConfig.CacheFileTypes.HasFlag(CacheFileType.Mdl))
            {
                CacheFile(path);
            }
            
            if (mdlCache.Count > 1000)
            {
                var toRemove = mdlCache.OrderBy(x => x.Value.LastAccess).First();
                mdlCache.TryRemove(toRemove.Key, out _);
                Plugin.Logger.LogDebug($"Evicting model file: {toRemove.Key}");
            }
            
            return new RefCounter<MdlFile>(mdlFile);
        });
        
        item.LastAccess = DateTime.UtcNow;
        return item.Object;
    }
    
    public MtrlFile GetMtrlFile(string path, out string? cachePath)
    {
        var item = mtrlCache.GetOrAdd(path, key =>
        {
            var mtrlData = pack.GetFileOrReadFromDisk(path);
            if (mtrlData == null) throw new Exception($"Failed to load material file: {path}");
            var mtrlFile = new MtrlFile(mtrlData);
            
            if (exportConfig.CacheFileTypes.HasFlag(CacheFileType.Mtrl))
            {
                var cachePath = CacheFile(path);
                mtrlPathCache.TryAdd(path, cachePath);
            }
            
            if (mtrlCache.Count > 1000)
            {
                // evict least recently accessed
                var toRemove = mtrlCache.OrderBy(x => x.Value.LastAccess).First();
                mtrlCache.TryRemove(toRemove.Key, out _);
                Plugin.Logger.LogDebug("Evicting material file: {toRemove}", toRemove.Key);
            }
            
            return new RefCounter<MtrlFile>(mtrlFile);
        });
        
        cachePath = mtrlPathCache.GetValueOrDefault(path);
        
        item.LastAccess = DateTime.UtcNow;
        return item.Object;
    }
    
    public ShaderPackage GetShaderPackage(string shpkName)
    {
        var shpkPath = $"shader/sm5/shpk/{shpkName}";
        return shpkCache.GetOrAdd(shpkPath, key =>
        {
            var shpkData = pack.GetFileOrReadFromDisk(key);
            if (shpkData == null) throw new Exception($"Failed to load shader package file: {key}");
            var shpkFile = new ShpkFile(shpkData);
            
            if (exportConfig.CacheFileTypes.HasFlag(CacheFileType.Shpk))
            {
                CacheFile(key);
            }
            
            return new ShaderPackage(shpkFile, shpkName);
        });
    }

    private string GetCacheFilePath(string fullPath)
    {
        var cleanPath = fullPath.TrimHandlePath();
        var rooted = Path.IsPathRooted(cleanPath);

        if (rooted)
        {
            var pathRoot = Path.GetPathRoot(cleanPath) ?? string.Empty;
            cleanPath = cleanPath[pathRoot.Length..];
        }

        // modded files are stored in a separate directory to prevent conflict if the same file is modded and unmodded.
        var basePath = rooted ? Path.Combine(cacheDir, "modded") : cacheDir;

        const int maxCharacters = 255;
        var charactersAvailable = maxCharacters - basePath.Length;
        var len = cleanPath.Length + 5; // +5 is only because we may add a suffix.
        if (len >= charactersAvailable)
        {
            // Trim path to a suitable length for the cache directory if it exceeds the max length.
            var parts = cleanPath.Replace('\\', '/').Split('/');
            var dirHash = Crc32.GetHash(string.Join("/", parts[..^1]));
            var fileName = parts[^1];
            
            var trimmed = $"{dirHash}/{fileName}";
            Plugin.Logger.LogDebug("Cache path too long ({len} > {available}), using hash: {trimmed} for {fullPath}", len, charactersAvailable, trimmed, fullPath);
            cleanPath = trimmed;
        }
        
        var cachePath = Path.Combine(basePath, cleanPath);
        return cachePath;
    }
    
    private string CacheFile(string fullPath)
    {
       var cachePath = GetCacheFilePath(fullPath);
        
        if (File.Exists(cachePath) && !refreshExistingFiles)
        {
            return TrackCacheFile(cachePath);
        }
        
        var fileData = pack.GetFileOrReadFromDisk(fullPath);
        if (fileData == null) throw new Exception($"Failed to load file: {fullPath}");
        
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        WriteAllBytesAtomic(cachePath, fileData);
        return TrackCacheFile(cachePath);
    }
    
    public string CacheTexture(string fullPath)
    {
        var cachePath = GetCacheFilePath(fullPath);
        var pngCachePath = cachePath + ".png";
        
        // inner skip if the png cache exists.
        if (File.Exists(pngCachePath) && !refreshExistingFiles)
        {
            return TrackCacheFile(pngCachePath);
        }
        
        var fileData = pack.GetFileOrReadFromDisk(fullPath);
        if (fileData == null) throw new Exception($"Failed to load file: {fullPath}");
        
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        if (exportConfig.CacheFileTypes.HasFlag(CacheFileType.Tex))
        {
            WriteAllBytesAtomic(cachePath, fileData);
        }
        
        var tex = new TexFile(fileData);
        if (tex.Header.Type.HasFlag(TexFile.Attribute.TextureTypeCube))
        {
            // Export each cube face
            var basePath = Path.Combine(
                Path.GetDirectoryName(cachePath)!,
                Path.GetFileNameWithoutExtension(cachePath)
            );
        
            var faceNames = new[] { "px", "nx", "py", "ny", "pz", "nz" };
            for (int face = 0; face < 6; face++)
            {
                var img = ImageUtils.GetTexData(tex, face, 0, 0);
                var data = img.ImageAsPng();
                var facePath = $"{basePath}_{faceNames[face]}.png";
                var outputPath = Path.Combine(Path.GetDirectoryName(cachePath)!, facePath);
                WriteAllBytesAtomic(outputPath, data);
                TrackCacheFile(outputPath);
            }
        
            return basePath; // Return base path since multiple files were created
        }
        
        var texture = tex.ToResource().ToTexture();
        using var memoryStream = new MemoryStream();
        texture.Bitmap.Encode(memoryStream, SKEncodedImageFormat.Png, 100);
        var textureBytes = memoryStream.ToArray();
        WriteAllBytesAtomic(pngCachePath, textureBytes);
        return TrackCacheFile(pngCachePath);
    }
    
    public MaterialBuilder ComposeMaterial(string mtrlPath, 
                                           ParsedMaterialInfo? materialInfo = null,
                                           IStainableInstance? stainInstance = null, 
                                           ParsedCharacterInfo? characterInfo = null)
    {
        var mtrlFile = GetMtrlFile(mtrlPath, out var mtrlCachePath);
        var shaderPackage = GetShaderPackage(mtrlFile.GetShaderPackageName());
        var material = new MaterialComposer(mtrlFile, mtrlPath, shaderPackage);
        if (stainInstance != null)
        {
            material.SetPropertiesFromInstance(stainInstance);
        }
        
        if (mtrlCachePath != null)
        {
            material.SetProperty("MtrlCachePath", Path.GetRelativePath(cacheDir, mtrlCachePath));
        }
        
        if (characterInfo != null)
        {
            material.SetPropertiesFromCharacterInfo(characterInfo);
        }

        if (materialInfo != null)
        {
            // kinda janky, but we set skin data first so that the keys are still available to be overridden by the main material info.
            if (materialInfo.RenderMaterialOutput != null)
            {
                var render = materialInfo.RenderMaterialOutput;
                if (render.DecalTexturePath != null)
                {
                    var decalCachePath = CacheTexture(render.DecalTexturePath);
                    material.SetProperty("Decal_PngCachePath", Path.GetRelativePath(cacheDir, decalCachePath));
                    material.SetProperty("DecalPath", render.DecalTexturePath);
                }
                else if (render.DecalTexture != null)
                {
                    var tex = render.DecalTexture;
                    var decalCachePath = SaveInMemoryTex(tex, "decals");
                    material.SetProperty("Decal_PngCachePath", Path.GetRelativePath(cacheDir, decalCachePath));
                    material.SetProperty("DecalPath", $"InMemoryTexture_{tex.GetHashCode()}");
                }

                if (render.SkinMaterialTextures.Count > 0)
                {
                    foreach (var  texture in render.SkinMaterialTextures)
                    {
                        var fullPath = texture.TexturePath;
                        var match = materialInfo.Textures.FirstOrDefault(x => x.Path.GamePath == texture.TexturePathFromMaterial);
                        if (match != null)
                        {
                            fullPath = match.Path.FullPath;
                        }

                        var cachePath = CacheTexture(fullPath);
                        // var keyUsage = $"{key}".Replace("g_Sampler", "g_SamplerSkin");
                        if (shaderPackage.Textures.TryGetValue(texture.TargetSamplerCrc, out var samplerName))
                        {
                            material.SetProperty($"{samplerName}", texture.TexturePath);
                            material.SetProperty($"{samplerName}_PngCachePath", Path.GetRelativePath(cacheDir, cachePath));
                        }
                    }
                }
            }
            
            material.SetPropertiesFromMaterialInfo(materialInfo);
        }

        // A live material can supply a GPU-resolved (and therefore dyed)
        // table. Static assets such as emote props have no live material, so
        // preserve the table authored directly in their MTRL instead of
        // silently rendering them with the node group's defaults.
        var colorTable = materialInfo?.ColorTable ?? mtrlFile.GetColorTable();
        if (colorTable != null)
        {
            material.SetPropertiesFromColorTable(colorTable);
            if (colorTable is ColorTableSet colorTableSet)
            {
                var tex = colorTableSet.ColorTable.ToTexture();
                var colorTablePath = SaveInMemoryTex(tex, "color_tables");
                material.SetProperty("ColorTable_PngCachePath", Path.GetRelativePath(cacheDir, colorTablePath));
            }
            else if (colorTable is LegacyColorTableSet legacyColorTableSet)
            {
                var tex = legacyColorTableSet.ColorTable.ToTexture();
                var colorTablePath = SaveInMemoryTex(tex, "color_tables");
                material.SetProperty("LegacyColorTable_PngCachePath", Path.GetRelativePath(cacheDir, colorTablePath));
            }
        }

        string SaveInMemoryTex(SkTexture tex, string type)
        {
            var texCacheDir = Path.Combine(cacheDir, type);
            Directory.CreateDirectory(texCacheDir);
            var buf = tex.Bitmap.Bytes;
            var hash = System.Security.Cryptography.SHA256.HashData(buf);
            var hashStr = Convert.ToHexStringLower(hash);
            // truncate the hash to 8 characters for the filename.
            if (hashStr.Length > 8)
            {
                hashStr = hashStr[..8];
            }
            var mtrlPathWithoutExtension = Path.GetFileNameWithoutExtension(mtrlPath);
            var shaderName = materialInfo?.Shpk ?? mtrlFile.GetShaderPackageName();
            var colorTablePath = Path.Combine(texCacheDir, $"{mtrlPathWithoutExtension}_{shaderName}_{hashStr}.png");
            if (!File.Exists(colorTablePath) || refreshExistingFiles)
            {
                using var memoryStream = new MemoryStream();
                tex.Bitmap.Encode(memoryStream, SKEncodedImageFormat.Png, 100);
                WriteAllBytesAtomic(colorTablePath, memoryStream.ToArray());
            }
            return TrackCacheFile(colorTablePath);
        }
        
        string materialName;
        if (materialInfo != null)
        {
            materialName = $"{Path.GetFileNameWithoutExtension(materialInfo.Path.GamePath)}_{materialInfo.Shpk}";
        }
        else
        {
            materialName = $"{Path.GetFileNameWithoutExtension(mtrlPath)}_{mtrlFile.GetShaderPackageName()}";
        }

        var materialBuilder = new MaterialBuilder(materialName);
        foreach (var texture in material.TextureUsageDict)
        {
            // ensure texture gets saved to cache dir.
            var fullPath = texture.Value.FullPath;
            var match = materialInfo?.Textures.FirstOrDefault(x => x.Path.GamePath == texture.Value.GamePath);
            if (match != null)
            {
                fullPath = match.Path.FullPath;
            }

            var cachePath = CacheTexture(fullPath);
            
            // remove full path prefix, get only dir below cache dir.
            material.SetProperty($"{texture.Key}_PngCachePath", Path.GetRelativePath(cacheDir, cachePath));
        }
        //
        // if (characterInfo != null)
        // {
        //     if (characterInfo.CustomizeData?.DecalPath != null && materialInfo?.ApplyDecal == true)
        //     {
        //         var decalCachePath = CacheTexture(characterInfo.CustomizeData.DecalPath);
        //         material.SetProperty("Decal_PngCachePath", Path.GetRelativePath(cacheDir, decalCachePath));
        //         material.SetProperty("DecalPath", characterInfo.CustomizeData.DecalPath ?? "");
        //     }
        //     
        //     if (characterInfo.CustomizeData?.LegacyBodyDecalPath != null && materialInfo?.ApplyLegacyDecal == true)
        //     {
        //         var legacyDecalCachePath = CacheTexture(characterInfo.CustomizeData.LegacyBodyDecalPath);
        //         material.SetProperty("LegacyBodyDecal_PngCachePath", Path.GetRelativePath(cacheDir, legacyDecalCachePath));;
        //         material.SetProperty("LegacyBodyDecalPath", characterInfo.CustomizeData.LegacyBodyDecalPath ?? "");
        //     }
        // }

        materialBuilder.Extras = material.ExtrasNode;
        
        return materialBuilder;
    }
}
