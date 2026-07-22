namespace Meddle.Plugin.Services;

/// <summary>
/// Versions of the reviewed Blender companions embedded in the plugin package.
/// Keep these values in step with the matching Python source version.
/// </summary>
internal static class XivBlendCompanionVersions
{
    public const string Builder = "0.10.0";
    public const string AnimationBrowser = "0.7.1";
}

internal static class BlenderExecutableDiscovery
{
    public static string? Find(Configuration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.BlenderExecutablePath)
            && File.Exists(configuration.BlenderExecutablePath))
        {
            return Path.GetFullPath(configuration.BlenderExecutablePath);
        }

        var environmentPath = Environment.GetEnvironmentVariable("XIVBLEND_BLENDER");
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
        {
            return Path.GetFullPath(environmentPath);
        }

        var blenderRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Blender Foundation");
        return Directory.Exists(blenderRoot)
            ? Directory.EnumerateFiles(blenderRoot, "blender.exe", SearchOption.AllDirectories)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;
    }
}

/// <summary>
/// Owns one fresh extraction of embedded companion files. Each operation gets
/// a private directory, so files left by an older package can never be loaded.
/// Only the exact, marked directory created by this instance is removed.
/// </summary>
internal sealed class TemporaryAssetExtraction : IDisposable
{
    private const string ExtractionPrefix = ".extract-";
    private const string OwnershipMarker = ".xivblend-owned-extraction";

    private readonly string versionRoot;
    private readonly string extractionName;
    private readonly string markerPath;
    private bool disposed;

    private TemporaryAssetExtraction(string versionRoot, string extractionRoot, string extractionName)
    {
        this.versionRoot = versionRoot;
        Root = extractionRoot;
        this.extractionName = extractionName;
        markerPath = Path.Combine(Root, OwnershipMarker);
    }

    public string Root { get; }

    public static TemporaryAssetExtraction Create(
        DirectoryInfo configDirectory,
        string componentName,
        string version)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);
        if (string.IsNullOrWhiteSpace(componentName)
            || componentName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || componentName.Contains(Path.DirectorySeparatorChar)
            || componentName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("The companion component name is not a safe directory name.", nameof(componentName));
        }

        if (string.IsNullOrWhiteSpace(version)
            || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || version.Contains(Path.DirectorySeparatorChar)
            || version.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("The companion version is not a safe directory name.", nameof(version));
        }

        var configRoot = Path.GetFullPath(configDirectory.FullName);
        var versionRoot = Path.GetFullPath(Path.Combine(configRoot, $"{componentName}-{version}"));
        RequireDirectChild(configRoot, versionRoot, "companion version directory");
        Directory.CreateDirectory(versionRoot);
        if ((File.GetAttributes(versionRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The companion version directory cannot be a filesystem link.");
        }

        var extractionName = ExtractionPrefix + Guid.NewGuid().ToString("N");
        var extractionRoot = Path.GetFullPath(Path.Combine(versionRoot, extractionName));
        RequireDirectChild(versionRoot, extractionRoot, "temporary companion extraction");
        Directory.CreateDirectory(extractionRoot);

        var extraction = new TemporaryAssetExtraction(versionRoot, extractionRoot, extractionName);
        try
        {
            using var marker = new FileStream(
                extraction.markerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            return extraction;
        }
        catch
        {
            extraction.Dispose();
            throw;
        }
    }

    public string ResolveInside(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("A companion asset path must be relative.", nameof(relativePath));
        }

        var resolved = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!resolved.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A companion asset resolved outside its private extraction directory.");
        }

        return resolved;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            if (!Directory.Exists(Root)
                || !File.Exists(markerPath)
                || (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0
                || !Guid.TryParseExact(extractionName[ExtractionPrefix.Length..], "N", out _))
            {
                return;
            }

            RequireDirectChild(versionRoot, Root, "temporary companion extraction");
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Blender has exited before disposal; an antivirus may still hold a
            // file briefly. A private orphan is safer than deleting elsewhere.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best effort and remains limited to our marked folder.
        }
    }

    private static void RequireDirectChild(string expectedParent, string candidate, string description)
    {
        var parent = Path.GetDirectoryName(candidate);
        if (parent is null
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedParent)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The {description} is outside its expected parent directory.");
        }
    }
}
