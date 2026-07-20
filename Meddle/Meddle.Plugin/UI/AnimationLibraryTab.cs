using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Meddle.Plugin.Models;
using Meddle.Plugin.Services;

namespace Meddle.Plugin.UI;

/// <summary>
/// Small setup/status surface for XivBlend's optional Blender emote browser.
/// The actual browser lives in Blender and requests only the clip being played.
/// </summary>
public sealed class AnimationLibraryTab : ITab
{
    private readonly AnimationLibraryService library;
    private readonly BlenderAnimationBrowserInstaller installer;

    public AnimationLibraryTab(
        AnimationLibraryService library,
        BlenderAnimationBrowserInstaller installer)
    {
        this.library = library;
        this.installer = installer;
    }

    public string Name => "Animations";
    public int Order => -90;
    public MenuType MenuType => MenuType.Default;

    public void Draw()
    {
        ImGui.TextWrapped(
            "Adds a simple XivBlend sidebar to Blender with the game's own emote icons. " +
            "Clicking an icon loads that one vanilla animation and plays it on a loop.");
        ImGui.Spacing();

        if (library.HasModifiedGameData)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudRed,
                "Dalamud detected modified live SqPack files. Restore TexTools index modifications before setup; " +
                "Penumbra is bypassed and does not need to be disabled.");
            ImGui.Spacing();
        }

        var busy = library.IsPreparing || library.IsServingRequest || installer.IsRunning;
        ImGui.BeginDisabled(busy || library.HasModifiedGameData);
        if (ImGui.Button("Set Up / Update Animation Browser", new Vector2(-1, 46)))
        {
            library.StartPrepareLibrary();
            installer.StartInstall();
        }
        ImGui.EndDisabled();

        if (library.IsPreparing && library.IconTotal > 0)
        {
            var fraction = Math.Clamp(library.IconProgress / (float)library.IconTotal, 0.0f, 1.0f);
            ImGui.ProgressBar(
                fraction,
                new Vector2(-1, 0),
                $"Game icons {library.IconProgress}/{library.IconTotal}");
        }

        ImGui.Spacing();
        ImGui.TextWrapped(library.Status);
        ImGui.TextWrapped(installer.Status);

        if (!string.IsNullOrWhiteSpace(library.LastError))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, library.LastError);
        }

        if (!string.IsNullOrWhiteSpace(installer.LastError))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, installer.LastError);
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(library.IsPreparing || library.IsServingRequest || library.HasModifiedGameData);
        if (ImGui.Button("Refresh Game Catalog"))
        {
            library.StartPrepareLibrary();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(installer.IsRunning);
        if (ImGui.Button("Reinstall Blender Panel"))
        {
            installer.StartInstall();
        }
        ImGui.EndDisabled();

        if (Directory.Exists(AnimationLibraryService.LibraryRoot)
            && ImGui.Button("Open Local Animation Cache"))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { AnimationLibraryService.LibraryRoot },
                UseShellExecute = true,
            });
        }

        ImGui.Separator();
        ImGui.TextDisabled("In Blender: press N in the 3D View, open XivBlend, then click Player Emotes.");
        ImGui.TextDisabled("Keep XivBlend open in FFXIV the first time a clip is clicked; later plays use the local cache.");
        ImGui.TextDisabled("Included: player emotes and facial expressions. Excluded: combat, weapons, VFX, mounts and mods.");
        ImGui.TextDisabled("Penumbra is bypassed. Current Dalamud cannot independently prove TexTools index integrity.");
        ImGui.TextDisabled("Only the currently previewed Action is loaded, and runtime Actions are removed before saving.");
    }

    public void Dispose()
    {
    }
}
