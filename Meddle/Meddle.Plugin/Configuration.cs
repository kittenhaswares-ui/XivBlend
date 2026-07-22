using System.Text.Json.Serialization;
using Dalamud.Configuration;
using Dalamud.IoC;
using Dalamud.Plugin;
using Meddle.Plugin.Models;
using Meddle.Plugin.Utils;
using Meddle.Utils;
using Microsoft.Extensions.Logging;

namespace Meddle.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    private const int CurrentVersion = 5;
    private static readonly string LegacyDefaultExportDirectory =
        Path.Combine(Path.GetTempPath(), "Meddle.Export");

    public const ExportType DefaultExportType = ExportType.GLTF;
    public static string DefaultExportDirectory => GetDefaultExportDirectory();

    [PluginService]
    [JsonIgnore]
    private IDalamudPluginInterface PluginInterface { get; set; } = null!;

    public int Version { get; set; } = CurrentVersion;
    public LogLevel MinimumNotificationLogLevel { get; set; } = LogLevel.Warning;
    public bool OpenOnLoad { get; set; }
    public bool DisableUserUiHide { get; set; }
    public bool DisableAutomaticUiHide { get; set; }
    public bool DisableCutsceneUiHide { get; set; } = true;
    public bool DisableGposeUiHide { get; set; } = true;
    public string ExportDirectory { get; set; } = DefaultExportDirectory;
    public string BlenderExecutablePath { get; set; } = string.Empty;
    public bool OpenFolderOnExport { get; set; } = true;
    public ExportConfiguration ExportConfig { get; set; } = new();
    public RsfConfiguration RsfConfig { get; set; } = new();

    public sealed class RsfConfiguration
    {
        public Dictionary<ulong, string> RsfCache = new();

        public bool SetRsfValue(ulong key, byte[] value)
        {
            var stringValue = BitConverter.ToString(value).Replace("-", " ");
            if (RsfCache.TryGetValue(key, out var existingValue)
                && existingValue == stringValue)
            {
                return false;
            }

            RsfCache[key] = stringValue;
            return true;
        }

        public Dictionary<ulong, byte[]> GetRsfData()
        {
            var result = new Dictionary<ulong, byte[]>();
            foreach (var (key, valueString) in RsfCache)
            {
                var rsfBytes = valueString
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => Convert.ToByte(value, 16))
                    .ToArray();
                var valueDataBuffer = new byte[64];
                rsfBytes.CopyTo(valueDataBuffer);
                result[key] = valueDataBuffer;
            }

            return result;
        }
    }

    public sealed class ExportConfiguration
    {
        public CacheFileType CacheFileTypes { get; set; }
        public ExportType ExportType { get; set; } = DefaultExportType;
        public SkeletonUtils.PoseMode PoseMode { get; set; } = SkeletonUtils.PoseMode.Local;
        public bool ApplyVisibilityFlags { get; set; } = true;
        public bool UseDeformer { get; set; } = true;
        public bool EnableWindingFlip { get; set; }

        public ExportConfiguration Clone()
        {
            return new ExportConfiguration
            {
                CacheFileTypes = CacheFileTypes,
                ExportType = ExportType,
                PoseMode = PoseMode,
                ApplyVisibilityFlags = ApplyVisibilityFlags,
                UseDeformer = UseDeformer,
                EnableWindingFlip = EnableWindingFlip
            };
        }

        public void SetDefaultCloneOptions()
        {
            UseDeformer = true;
            ApplyVisibilityFlags = true;
        }

        public MeshBuilderOptions CreateMeshBuilderOptions()
        {
            return new MeshBuilderOptions
            {
                EnableWindingFlip = EnableWindingFlip
            };
        }
    }

    /// <summary>
    /// Normalizes settings loaded from older Meddle/XivBlend configuration files.
    /// Removed legacy JSON members are intentionally ignored by the serializer.
    /// </summary>
    public void Migrate()
    {
        var changed = false;

        if (Version < 2)
        {
            DisableAutomaticUiHide = true;
            DisableCutsceneUiHide = true;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(ExportDirectory)
            || (Version < 5
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(ExportDirectory),
                    Path.TrimEndingDirectorySeparator(LegacyDefaultExportDirectory),
                    StringComparison.OrdinalIgnoreCase)))
        {
            ExportDirectory = DefaultExportDirectory;
            changed = true;
        }

        if (BlenderExecutablePath is null)
        {
            BlenderExecutablePath = string.Empty;
            changed = true;
        }

        if (ExportConfig is null)
        {
            ExportConfig = new ExportConfiguration();
            changed = true;
        }

        if (RsfConfig is null)
        {
            RsfConfig = new RsfConfiguration();
            changed = true;
        }

        if (Version < CurrentVersion)
        {
            Version = CurrentVersion;
            changed = true;
        }

        if (changed)
        {
            Save();
        }
    }

    public event Action? OnConfigurationSaved;

    public void Save()
    {
        PluginInterface.SavePluginConfig(this);
        OnConfigurationSaved?.Invoke();
    }

    private static string GetDefaultExportDirectory()
    {
        var documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documentsFolder)
            ? LegacyDefaultExportDirectory
            : Path.Combine(documentsFolder, "XivBlend Exports");
    }
}
