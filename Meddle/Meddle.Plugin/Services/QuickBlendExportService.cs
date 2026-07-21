using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Meddle.Plugin.Models;
using Meddle.Plugin.Models.Composer;
using Meddle.Plugin.UI.Layout;
using Meddle.Plugin.Utils;
using Microsoft.Extensions.Logging;
using SharpGLTF.Scenes;

namespace Meddle.Plugin.Services;

/// <summary>
/// Minimal, self-only export pipeline for the XivBlend prototype.
/// The live character is resolved on the framework/UI thread, then the expensive
/// glTF composition and Blender conversion run on a worker thread.
/// </summary>
public sealed class QuickBlendExportService : IService, IDisposable
{
    private const string GlamourerStateLabel = "Glamourer.GetStateBase64";
    private const string PenumbraResourcePathsLabel = "Penumbra.GetGameObjectResourcePaths.V5";
    private static readonly Regex FaceSkeletonFilePattern = new(
        @"skl_(?<race>c\d{4})(?<face>f\d{4})\.sklb(?:$|[|?#&])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ILogger<QuickBlendExportService> log;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ResolverService resolverService;
    private readonly ComposerFactory composerFactory;
    private readonly Configuration configuration;
    private readonly ICallGateSubscriber<int, uint, (int ErrorCode, string? State)> glamourerState;
    private readonly ICallGateSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]> penumbraResourcePaths;
    private readonly CancellationTokenSource disposeToken = new();
    private readonly CancellationToken exportCancellationToken;

    private Task exportTask = Task.CompletedTask;

    public QuickBlendExportService(
        ILogger<QuickBlendExportService> log,
        IObjectTable objectTable,
        IClientState clientState,
        IDalamudPluginInterface pluginInterface,
        ResolverService resolverService,
        ComposerFactory composerFactory,
        Configuration configuration)
    {
        this.log = log;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.pluginInterface = pluginInterface;
        this.resolverService = resolverService;
        this.composerFactory = composerFactory;
        this.configuration = configuration;
        exportCancellationToken = disposeToken.Token;

        glamourerState = pluginInterface.GetIpcSubscriber<int, uint, (int, string?)>(GlamourerStateLabel);
        penumbraResourcePaths = pluginInterface.GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>(
            PenumbraResourcePathsLabel);
    }

    public bool IsRunning => !exportTask.IsCompleted;
    public string Status { get; private set; } = "Ready to export your current character.";
    public string? LastOutputPath { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// Captures only Dalamud's local-player object. There is deliberately no actor argument.
    /// This method must be called from the framework/UI thread.
    /// </summary>
    public unsafe bool StartExport()
    {
        if (IsRunning)
        {
            Status = "An export is already running.";
            return false;
        }

        LastError = null;
        LastOutputPath = null;

        try
        {
            var localPlayer = objectTable.LocalPlayer;
            if (clientState.IsGPosing)
            {
                throw new InvalidOperationException("Exit Group Pose before exporting this prototype.");
            }

            if (localPlayer is null || !localPlayer.IsValid())
            {
                throw new InvalidOperationException("Log in and wait until your character is visible before exporting.");
            }

            var objectIndex = checked((ushort)localPlayer.ObjectIndex);
            var objectAddress = localPlayer.Address;
            var characterName = localPlayer.Name.ToString();
            var character = (Character*)objectAddress;
            if (character == null || character->DrawObject == null)
            {
                throw new InvalidOperationException("Your character is not fully drawn yet. Wait a moment and try again.");
            }
            var drawObjectAddress = (nint)character->DrawObject;

            Status = "Reading Glamourer and Penumbra...";
            var snapshot = CaptureSnapshot(objectIndex, characterName);

            // ResolverService reads the final live draw object. Penumbra redirections and
            // Glamourer changes have already been applied by this point. Keep this call on
            // the framework/UI thread because it also reads live GPU material data.
            Status = "Capturing your live character...";
            var characterInfo = resolverService.ParseCharacter(character)
                ?? throw new InvalidOperationException("The live character could not be resolved.");

            var raceCode = checked((ushort)characterInfo.GenderRace);
            var faceSkeleton = FindFaceSkeletonToken(characterInfo, raceCode);
            snapshot = snapshot with
            {
                RaceCode = raceCode,
                FaceSkeleton = faceSkeleton,
                Warnings = faceSkeleton is null
                    ? snapshot.Warnings.Append(
                        "The captured face skeleton resource did not expose a standard fNNNN token; " +
                        "facial animation browsing will be unavailable for this export.").ToArray()
                    : snapshot.Warnings,
            };

            var currentLocalPlayer = objectTable.LocalPlayer;
            if (currentLocalPlayer is null
                || currentLocalPlayer.ObjectIndex != objectIndex
                || currentLocalPlayer.Address != objectAddress
                || ((Character*)currentLocalPlayer.Address)->DrawObject == null
                || (nint)((Character*)currentLocalPlayer.Address)->DrawObject != drawObjectAddress)
            {
                throw new InvalidOperationException("Your character redrew during capture. Wait a moment and try again.");
            }

            if (snapshot.GlamourerStateBase64 is not null)
            {
                var verification = glamourerState.InvokeFunc(objectIndex, 0u);
                if (verification.ErrorCode != snapshot.GlamourerErrorCode
                    || !string.Equals(verification.State, snapshot.GlamourerStateBase64, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Your Glamourer appearance changed during capture. Wait a moment and try again.");
                }
            }

            var exportConfig = configuration.ExportConfig.Clone();
            exportConfig.SetDefaultCloneOptions();
            exportConfig.ExportType = ExportType.GLTF;

            var safeName = characterName.SanitizeFileName();
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss-fff");
            var outputBaseName = $"XivBlend-{safeName}-{timestamp}";
            var outputDirectory = ReserveUniqueOutputDirectory(configuration.ExportDirectory, outputBaseName);

            exportTask = Task.Run(
                () => ExportCapturedCharacter(characterInfo, snapshot, exportConfig, outputDirectory, safeName),
                exportCancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Status = "Export could not start.";
            log.LogError(exception, "XivBlend export capture failed");
            return false;
        }
    }

    private static string ReserveUniqueOutputDirectory(string exportRoot, string outputBaseName)
    {
        Directory.CreateDirectory(exportRoot);

        // Build the reservation under a private, practically unique name first.
        // Moving a directory within the same parent is atomic on Windows and
        // fails when the destination already exists, so we never adopt or write
        // into a directory created by another process between a check and use.
        var stagingPath = Path.Combine(
            exportRoot,
            $".xivblend-reservation-{Guid.NewGuid():N}");
        var reservationPath = Path.Combine(stagingPath, ".xivblend-reserved");
        var markerCreated = false;

        try
        {
            Directory.CreateDirectory(stagingPath);
            using (new FileStream(
                       reservationPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                markerCreated = true;
            }

            try
            {
                File.SetAttributes(reservationPath, FileAttributes.Hidden);
            }
            catch (IOException)
            {
                // The marker remains permanent even if it cannot be hidden.
            }
            catch (UnauthorizedAccessException)
            {
                // The marker remains permanent even if it cannot be hidden.
            }

            for (var collision = 1; collision <= 10_000; collision++)
            {
                var candidateName = collision == 1 ? outputBaseName : $"{outputBaseName}-{collision}";
                var candidatePath = Path.Combine(exportRoot, candidateName);
                if (Directory.Exists(candidatePath) || File.Exists(candidatePath))
                {
                    continue;
                }

                try
                {
                    Directory.Move(stagingPath, candidatePath);
                    return candidatePath;
                }
                catch (IOException) when (
                    Directory.Exists(candidatePath) || File.Exists(candidatePath))
                {
                    // Another exporter won the atomic move. Try the next suffix.
                }
            }

            throw new IOException("Could not reserve a unique XivBlend export directory after 10,000 attempts.");
        }
        catch
        {
            // Best-effort cleanup of only the private directory created above.
            // Avoid recursive deletion: unexpected third-party content is left
            // untouched rather than being removed with the reservation.
            try
            {
                if (markerCreated)
                {
                    File.Delete(reservationPath);
                }

                Directory.Delete(stagingPath, recursive: false);
            }
            catch (IOException)
            {
                // Preserve the original reservation error.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the original reservation error.
            }

            throw;
        }
    }

    private SnapshotManifest CaptureSnapshot(ushort objectIndex, string characterName)
    {
        var warnings = new List<string>();
        int? glamourerErrorCode = null;
        string? glamourerStateBase64 = null;
        Dictionary<string, HashSet<string>>? resourcePaths = null;

        try
        {
            var result = glamourerState.InvokeFunc(objectIndex, 0u);
            glamourerErrorCode = result.ErrorCode;
            glamourerStateBase64 = result.State;
            if (result.ErrorCode != 0)
            {
                warnings.Add($"Glamourer returned error code {result.ErrorCode}; the live draw-object export is still usable.");
            }
        }
        catch (Exception exception)
        {
            warnings.Add("Glamourer IPC was unavailable. The final live appearance will still be exported.");
            log.LogWarning(exception, "Could not capture Glamourer state");
        }

        try
        {
            var results = penumbraResourcePaths.InvokeFunc([objectIndex]);
            resourcePaths = results.FirstOrDefault();
            if (resourcePaths is null)
            {
                warnings.Add("Penumbra returned no resource-path snapshot; Meddle's live loaded paths remain authoritative.");
            }
        }
        catch (Exception exception)
        {
            warnings.Add("Penumbra IPC was unavailable. Resolved live file handles will still be used.");
            log.LogWarning(exception, "Could not capture Penumbra resource paths");
        }

        return new SnapshotManifest(
            SchemaVersion: 2,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            CharacterName: characterName,
            ObjectIndex: objectIndex,
            RaceCode: 0,
            FaceSkeleton: null,
            Source: "LocalPlayer only",
            GlamourerErrorCode: glamourerErrorCode,
            GlamourerStateBase64: glamourerStateBase64,
            PenumbraResourcePaths: resourcePaths,
            Warnings: warnings);
    }

    private static string? FindFaceSkeletonToken(
        Models.Layout.ParsedCharacterInfo characterInfo,
        ushort raceCode)
    {
        var expectedRace = $"c{raceCode:D4}";
        foreach (var partialSkeleton in characterInfo.Skeleton.PartialSkeletons)
        {
            if (string.IsNullOrWhiteSpace(partialSkeleton.HandlePath))
            {
                continue;
            }

            var match = FaceSkeletonFilePattern.Match(partialSkeleton.HandlePath);
            if (match.Success
                && string.Equals(
                    match.Groups["race"].Value,
                    expectedRace,
                    StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["face"].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    private void ExportCapturedCharacter(
        Models.Layout.ParsedCharacterInfo characterInfo,
        SnapshotManifest snapshot,
        Configuration.ExportConfiguration exportConfig,
        string outputDirectory,
        string safeName)
    {
        try
        {
            exportCancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputDirectory);

            Status = "Writing the character snapshot...";
            var manifestPath = Path.Combine(outputDirectory, "xivblend-manifest.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));

            var characterBlob = JsonSerializer.Serialize(characterInfo, MaterialComposer.JsonOptions);
            File.WriteAllText(Path.Combine(outputDirectory, $"{safeName}-meddle.json"), characterBlob);
            exportCancellationToken.ThrowIfCancellationRequested();

            Status = "Building the rigged glTF...";
            var composer = composerFactory.CreateCharacterComposer(outputDirectory, exportConfig, exportCancellationToken);
            var scene = new SceneBuilder();
            var characterRoot = new NodeBuilder($"Character-{safeName}");
            scene.AddNode(characterRoot);
            var progress = new ExportProgress(characterInfo.Models.Count, "Character");
            var composition = composer.Compose(characterInfo, scene, characterRoot, progress);
            var reportPath = Path.Combine(outputDirectory, "xivblend-export-report.json");
            File.WriteAllText(
                reportPath,
                JsonSerializer.Serialize(composer.Report, new JsonSerializerOptions { WriteIndented = true }));
            if (composition is null || !composer.Report.IsComplete)
            {
                var firstFailure = composer.Report.Failures.FirstOrDefault() ?? "No visible rigged models were exported.";
                throw new InvalidOperationException(
                    $"Character extraction was incomplete ({composer.Report.ExportedVisibleModels}/" +
                    $"{composer.Report.ExpectedVisibleModels} visible models). {firstFailure}");
            }

            exportCancellationToken.ThrowIfCancellationRequested();

            var modelRoot = scene.ToGltf2();
            ExportUtil.SaveAsType(modelRoot, ExportType.GLTF, outputDirectory, safeName);
            var gltfPath = Path.Combine(outputDirectory, safeName + ".gltf");
            if (!File.Exists(gltfPath))
            {
                throw new FileNotFoundException("The glTF exporter did not create its expected output.", gltfPath);
            }
            exportCancellationToken.ThrowIfCancellationRequested();

            Status = "Creating the Blender file...";
            var blendPath = Path.Combine(outputDirectory, safeName + ".blend");
            RunBlender(gltfPath, manifestPath, blendPath, exportCancellationToken);
            exportCancellationToken.ThrowIfCancellationRequested();

            LastOutputPath = blendPath;
            Status = "Export complete.";
            log.LogInformation("XivBlend export complete: {BlendPath}", blendPath);

            if (configuration.OpenFolderOnExport)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    ArgumentList = { outputDirectory },
                    UseShellExecute = true,
                });
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Export cancelled.";
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Status = "Export failed. The intermediate glTF may still be available in the export folder.";
            log.LogError(exception, "XivBlend export failed");
        }
    }

    private void RunBlender(
        string gltfPath,
        string manifestPath,
        string blendPath,
        CancellationToken cancellationToken)
    {
        var blenderPath = FindBlenderExecutable();
        if (blenderPath is null)
        {
            throw new FileNotFoundException(
                "Blender was not found. Set its path in the plugin configuration or install Blender 5.x.");
        }

        var builderDirectory = ExtractBuilderAssets();
        var builderScript = Path.Combine(builderDirectory, "build_blend.py");
        if (!File.Exists(builderScript))
        {
            throw new FileNotFoundException("The bundled Blender builder script is missing.", builderScript);
        }

        var processInfo = new ProcessStartInfo
        {
            FileName = blenderPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        processInfo.ArgumentList.Add("--background");
        processInfo.ArgumentList.Add("--factory-startup");
        processInfo.ArgumentList.Add("--python-exit-code");
        processInfo.ArgumentList.Add("1");
        processInfo.ArgumentList.Add("--python");
        processInfo.ArgumentList.Add(builderScript);
        processInfo.ArgumentList.Add("--");
        processInfo.ArgumentList.Add("--input");
        processInfo.ArgumentList.Add(gltfPath);
        processInfo.ArgumentList.Add("--manifest");
        processInfo.ArgumentList.Add(manifestPath);
        processInfo.ArgumentList.Add("--output");
        processInfo.ArgumentList.Add(blendPath);
        var meddleToolsDirectory = Path.Combine(builderDirectory, "MeddleTools");
        if (Directory.Exists(meddleToolsDirectory))
        {
            processInfo.ArgumentList.Add("--meddle-tools");
            processInfo.ArgumentList.Add(meddleToolsDirectory);
        }

        using var process = new Process { StartInfo = processInfo };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                log.LogDebug("Blender: {Line}", args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                log.LogWarning("Blender: {Line}", args.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Blender could not be started.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception exception)
            {
                log.LogWarning(exception, "Could not stop Blender cleanly after export cancellation");
            }

            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Blender exited with code {process.ExitCode}.");
        }

        if (!File.Exists(blendPath))
        {
            throw new FileNotFoundException("Blender reported success but did not create the .blend file.", blendPath);
        }
    }

    private string? FindBlenderExecutable()
    {
        if (!string.IsNullOrWhiteSpace(configuration.BlenderExecutablePath)
            && File.Exists(configuration.BlenderExecutablePath))
        {
            return configuration.BlenderExecutablePath;
        }

        var environmentPath = Environment.GetEnvironmentVariable("XIVBLEND_BLENDER");
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
        {
            return environmentPath;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var blenderRoot = Path.Combine(programFiles, "Blender Foundation");
        if (!Directory.Exists(blenderRoot))
        {
            return null;
        }

        return Directory.EnumerateFiles(blenderRoot, "blender.exe", SearchOption.AllDirectories)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private string ExtractBuilderAssets()
    {
        const string resourcePrefix = "XivBlendBuilder/";
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal))
            .ToArray();
        if (resourceNames.Length == 0)
        {
            throw new InvalidOperationException("The plugin contains no embedded Blender builder assets.");
        }

        var builderRoot = Path.GetFullPath(Path.Combine(
            pluginInterface.ConfigDirectory.FullName,
            "XivBlendBuilder-0.6.0"));
        Directory.CreateDirectory(builderRoot);
        var requiredPrefix = builderRoot + Path.DirectorySeparatorChar;

        foreach (var resourceName in resourceNames)
        {
            var relativePath = resourceName[resourcePrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(builderRoot, relativePath));
            if (!destination.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("An embedded builder asset resolved outside its extraction directory.");
            }

            var parentDirectory = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("An embedded builder asset has no parent directory.");
            Directory.CreateDirectory(parentDirectory);
            using var input = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Could not open embedded asset {resourceName}.");
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }

        return builderRoot;
    }

    public void Dispose()
    {
        disposeToken.Cancel();
        try
        {
            if (!exportTask.IsCompleted && !exportTask.Wait(TimeSpan.FromSeconds(30)))
            {
                log.LogWarning("XivBlend export did not stop within 30 seconds during plugin unload");
            }
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(inner => inner is OperationCanceledException or TaskCanceledException))
        {
            // Expected when cancellation wins before the background delegate starts.
        }

        if (exportTask.IsCompleted)
        {
            disposeToken.Dispose();
        }
    }

    private sealed record SnapshotManifest(
        int SchemaVersion,
        DateTimeOffset CapturedAtUtc,
        string CharacterName,
        ushort ObjectIndex,
        ushort RaceCode,
        string? FaceSkeleton,
        string Source,
        int? GlamourerErrorCode,
        string? GlamourerStateBase64,
        Dictionary<string, HashSet<string>>? PenumbraResourcePaths,
        IReadOnlyList<string> Warnings);
}
