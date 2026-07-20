using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Meddle.Plugin.Models;
using Meddle.Plugin.Services;

namespace Meddle.Plugin.UI;

/// <summary>
/// Deliberately small self-only UI for the first XivBlend prototype.
/// </summary>
public sealed class QuickBlendExportTab : ITab
{
    private readonly QuickBlendExportService exporter;
    private readonly Configuration config;
    private readonly FileDialogManager fileDialog = new()
    {
        AddedWindowFlags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking
    };

    public QuickBlendExportTab(QuickBlendExportService exporter, Configuration config)
    {
        this.exporter = exporter;
        this.config = config;
    }

    public string Name => "Export My Character";
    public int Order => -100;
    public MenuType MenuType => MenuType.Default;

    public void Draw()
    {
        ImGui.TextWrapped(
            "Exports only your own currently displayed character. Body, face, hair, visible clothing, " +
            "weapons, live Penumbra-resolved materials, textures and the FFXIV deformation rig are included.");
        ImGui.Spacing();

        DrawBlenderPath();
        ImGui.Spacing();

        ImGui.BeginDisabled(exporter.IsRunning);
        if (ImGui.Button("Export My Character to Blender", new System.Numerics.Vector2(-1, 46)))
        {
            exporter.StartExport();
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextWrapped(exporter.Status);

        if (!string.IsNullOrWhiteSpace(exporter.LastError))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, exporter.LastError);
        }

        if (!string.IsNullOrWhiteSpace(exporter.LastOutputPath))
        {
            ImGui.TextWrapped(exporter.LastOutputPath);
            if (ImGui.Button("Open Export Folder"))
            {
                var directory = Path.GetDirectoryName(exporter.LastOutputPath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        ArgumentList = { directory },
                        UseShellExecute = true,
                    });
                }
            }
        }

        ImGui.Separator();
        ImGui.TextDisabled("Prototype scope: static rigged character only; animation export is intentionally deferred.");

        fileDialog.Draw();
    }

    private void DrawBlenderPath()
    {
        ImGui.TextUnformatted("Blender executable");

        var path = config.BlenderExecutablePath;
        var browseButtonWidth = ImGui.CalcTextSize("Browse...").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetNextItemWidth(Math.Max(100, ImGui.GetContentRegionAvail().X - browseButtonWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputText("##BlenderExecutablePath", ref path, 1024, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();

        if (ImGui.Button("Browse..."))
        {
            var startDirectory = Path.GetDirectoryName(config.BlenderExecutablePath);
            if (string.IsNullOrWhiteSpace(startDirectory) || !Directory.Exists(startDirectory))
            {
                startDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            }

            fileDialog.OpenFileDialog(
                "Select Blender executable",
                "Executable (*.exe){.exe}",
                (success, paths) =>
                {
                    if (!success || paths.Count != 1)
                    {
                        return;
                    }

                    config.BlenderExecutablePath = Path.GetFullPath(paths[0]);
                    config.Save();
                },
                1,
                startDirectory,
                true);
        }

        if (string.IsNullOrWhiteSpace(config.BlenderExecutablePath))
        {
            ImGui.TextDisabled("Automatic detection will be used if no path is selected.");
        }
        else if (!File.Exists(config.BlenderExecutablePath))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "The selected executable does not exist.");
        }
    }

    public void Dispose()
    {
    }
}
