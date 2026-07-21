using System.Diagnostics;
using System.Reflection;
using System.Text;
using Dalamud.Plugin;
using Microsoft.Extensions.Logging;

namespace Meddle.Plugin.Services;

/// <summary>
/// Installs the asset-free XivBlend animation browser into the user's selected
/// Blender profile.  The installer copies only the reviewed add-on source; game
/// icons and animation data remain in XivBlend's local on-demand cache.
/// </summary>
public sealed class BlenderAnimationBrowserInstaller : IService, IDisposable
{
    public const string BrowserVersion = "0.2.0";

    private const string ResourcePrefix = "XivBlendBuilder/";
    private const string InstallerResource = ResourcePrefix + "install_animation_browser.py";
    private const string AddonResource = ResourcePrefix + "xivblend_animation_browser/__init__.py";
    private const string SuccessMarker = "XIVBLEND_ANIMATION_BROWSER_INSTALL=";

    private readonly ILogger<BlenderAnimationBrowserInstaller> logger;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Configuration configuration;
    private readonly CancellationTokenSource disposeToken = new();
    private Task installTask = Task.CompletedTask;
    private bool disposed;

    public BlenderAnimationBrowserInstaller(
        ILogger<BlenderAnimationBrowserInstaller> logger,
        IDalamudPluginInterface pluginInterface,
        Configuration configuration)
    {
        this.logger = logger;
        this.pluginInterface = pluginInterface;
        this.configuration = configuration;
    }

    public bool IsRunning => !installTask.IsCompleted;
    public string Status { get; private set; } = "Blender animation browser is not installed by XivBlend yet.";
    public string? LastError { get; private set; }

    public bool StartInstall()
    {
        if (disposed)
        {
            return false;
        }

        if (IsRunning)
        {
            Status = "The Blender animation browser is already being installed.";
            return false;
        }

        LastError = null;
        Status = "Installing the XivBlend panel into Blender...";
        installTask = Task.Run(() => InstallAsync(disposeToken.Token), disposeToken.Token);
        return true;
    }

    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            var blenderPath = FindBlenderExecutable()
                ?? throw new FileNotFoundException(
                    "Blender was not found. Select Blender 4.2 or newer in the Export tab first.");
            var installerPath = ExtractReviewedAssets();
            cancellationToken.ThrowIfCancellationRequested();

            var output = new StringBuilder();
            var errors = new StringBuilder();
            var processInfo = new ProcessStartInfo
            {
                FileName = blenderPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            processInfo.ArgumentList.Add("--background");
            // Deliberately do not use --factory-startup: the installer enables
            // one add-on and then saves the user's existing Blender preferences.
            processInfo.ArgumentList.Add("--python-exit-code");
            processInfo.ArgumentList.Add("1");
            processInfo.ArgumentList.Add("--python");
            processInfo.ArgumentList.Add(installerPath);

            using var process = new Process { StartInfo = processInfo };
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    lock (output)
                    {
                        output.AppendLine(args.Data);
                    }

                    logger.LogDebug("Blender add-on installer: {Line}", args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    lock (errors)
                    {
                        errors.AppendLine(args.Data);
                    }

                    logger.LogWarning("Blender add-on installer: {Line}", args.Data);
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
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                process.WaitForExit();
            }
            catch (OperationCanceledException)
            {
                TryStopProcess(process);
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();
            string outputText;
            string errorText;
            lock (output)
            {
                outputText = output.ToString();
            }

            lock (errors)
            {
                errorText = errors.ToString();
            }

            if (process.ExitCode != 0)
            {
                var detail = LastUsefulLine(errorText) ?? LastUsefulLine(outputText);
                throw new InvalidOperationException(
                    detail is null
                        ? $"Blender exited with code {process.ExitCode} while installing the browser."
                        : $"Blender could not install the browser: {detail}");
            }

            if (!outputText.Contains(SuccessMarker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Blender exited without confirming that the XivBlend animation browser was enabled.");
            }

            Status = "Animation browser installed. Restart an already-open Blender window, then open the XivBlend sidebar.";
            logger.LogInformation("Installed XivBlend Blender animation browser {Version}", BrowserVersion);
        }
        catch (OperationCanceledException)
        {
            Status = "Blender animation browser installation cancelled.";
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Status = "Blender animation browser installation failed.";
            logger.LogError(exception, "Could not install the XivBlend Blender animation browser");
        }
    }

    private string ExtractReviewedAssets()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var destinationRoot = Path.GetFullPath(Path.Combine(
            pluginInterface.ConfigDirectory.FullName,
            $"XivBlendAnimationBrowser-{BrowserVersion}"));
        Directory.CreateDirectory(destinationRoot);

        var availableResources = assembly.GetManifestResourceNames()
            .ToDictionary(
                name => name.Replace('\\', '/'),
                name => name,
                StringComparer.Ordinal);

        foreach (var normalizedResourceName in new[] { InstallerResource, AddonResource })
        {
            if (!availableResources.TryGetValue(normalizedResourceName, out var resourceName))
            {
                throw new InvalidOperationException($"The plugin package is missing {normalizedResourceName}.");
            }

            var relativePath = normalizedResourceName[ResourcePrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var destination = ResolveInside(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var input = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"The plugin package could not open {normalizedResourceName}.");
            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }

                File.Move(temporary, destination, true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }

        var installerPath = ResolveInside(destinationRoot, "install_animation_browser.py");
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("The bundled Blender animation browser installer is missing.", installerPath);
        }

        return installerPath;
    }

    private string? FindBlenderExecutable()
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

    private static string ResolveInside(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var resolved = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!resolved.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A Blender browser asset resolved outside its private directory.");
        }

        return resolved;
    }

    private static string? LastUsefulLine(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault();

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Cancellation still wins if Blender cannot be stopped cleanly.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Private temporary cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Private temporary cleanup is best effort.
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        disposeToken.Cancel();
        try
        {
            installTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(inner => inner is OperationCanceledException or TaskCanceledException))
        {
            // Expected when the plugin is unloaded during installation.
        }

        if (installTask.IsCompleted)
        {
            disposeToken.Dispose();
        }
    }
}
