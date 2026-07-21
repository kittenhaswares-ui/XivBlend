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
    private readonly PenumbraAnimationModService customMods;
    private string selectedModIdentifier = string.Empty;
    private string selectedImportedModIdentifier = string.Empty;

    public AnimationLibraryTab(
        AnimationLibraryService library,
        BlenderAnimationBrowserInstaller installer,
        PenumbraAnimationModService customMods)
    {
        this.library = library;
        this.installer = installer;
        this.customMods = customMods;
    }

    public string Name => "Animations";
    public int Order => -90;
    public MenuType MenuType => MenuType.Default;

    public void Draw()
    {
        ImGui.TextWrapped(
            "Adds a simple XivBlend sidebar to Blender with the game's own emote icons. " +
            "Clicking an icon loads one synchronized body/face bundle and plays it on a loop.");
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

        DrawCustomAnimations(busy);

        ImGui.Separator();
        ImGui.TextDisabled("In Blender: press N in the 3D View, open XivBlend, then click Player Emotes.");
        ImGui.TextDisabled("Keep XivBlend open in FFXIV the first time a clip is clicked; later plays use the local cache.");
        ImGui.TextDisabled("Included: player emote body/face timing, exact local props, and indexed native AVFX metadata.");
        ImGui.TextDisabled("Combat and weapon actions stay excluded; Blender does not yet simulate Apricot particle playback.");
        ImGui.TextDisabled("Vanilla extraction bypasses Penumbra. Custom imports ask Penumbra only for your active winning PAP paths.");
        ImGui.TextDisabled("Only the selected preview bundle is loaded; its runtime Actions and effects are removed before saving.");
    }

    private void DrawCustomAnimations(bool busy)
    {
        ImGui.Separator();
        ImGui.Text("Custom Animation Mods");
        ImGui.TextWrapped(
            "Choose an installed Penumbra animation mod. XivBlend reads its standard Penumbra option files, " +
            "then asks Penumbra which player-animation PAPs are active and winning for your own current character. " +
            "Standalone pose and loop replacements appear under Custom too; XivBlend does not change the mod or its options.");

        if (ImGui.Button("Refresh Penumbra Mods"))
        {
            customMods.RefreshInstalledMods();
            if (!customMods.InstalledMods.Any(item => item.ModIdentifier == selectedModIdentifier))
            {
                selectedModIdentifier = customMods.InstalledMods.FirstOrDefault()?.ModIdentifier ?? string.Empty;
            }
        }

        var selected = customMods.InstalledMods.FirstOrDefault(
            item => item.ModIdentifier == selectedModIdentifier);
        var preview = selected?.DisplayName ?? "Choose a mod...";
        if (ImGui.BeginCombo("Animation mod", preview))
        {
            foreach (var mod in customMods.InstalledMods)
            {
                var isSelected = mod.ModIdentifier == selectedModIdentifier;
                if (ImGui.Selectable($"{mod.DisplayName}##{mod.ModIdentifier}", isSelected))
                {
                    selectedModIdentifier = mod.ModIdentifier;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.BeginDisabled(
            busy
            || string.IsNullOrWhiteSpace(selectedModIdentifier)
            || !customMods.IsAvailable);
        if (ImGui.Button("Add Active Animation Overrides", new Vector2(-1, 38)))
        {
            if (customMods.ImportActiveOverrides(
                    selectedModIdentifier,
                    library.GetVanillaEntriesSnapshot()))
            {
                library.StartPrepareLibrary();
            }
        }
        ImGui.EndDisabled();

        var importedSources = customMods.ImportedSources;
        if (!importedSources.Any(item => item.ModIdentifier == selectedImportedModIdentifier))
        {
            selectedImportedModIdentifier = importedSources.FirstOrDefault()?.ModIdentifier ?? string.Empty;
        }

        if (importedSources.Count > 0)
        {
            var imported = importedSources.FirstOrDefault(
                item => item.ModIdentifier == selectedImportedModIdentifier);
            if (ImGui.BeginCombo("Saved source", imported?.DisplayName ?? "Choose a saved source..."))
            {
                foreach (var source in importedSources)
                {
                    var isSelected = source.ModIdentifier == selectedImportedModIdentifier;
                    if (ImGui.Selectable($"{source.DisplayName}##saved-{source.ModIdentifier}", isSelected))
                    {
                        selectedImportedModIdentifier = source.ModIdentifier;
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.BeginDisabled(busy || string.IsNullOrWhiteSpace(selectedImportedModIdentifier));
            if (ImGui.Button("Remove Saved Source from XivBlend"))
            {
                if (customMods.RemoveSource(selectedImportedModIdentifier))
                {
                    selectedImportedModIdentifier = string.Empty;
                    library.StartPrepareLibrary();
                }
            }
            ImGui.EndDisabled();
        }

        ImGui.TextWrapped(customMods.Status);
        ImGui.TextDisabled($"Saved custom sources: {customMods.ImportedSourceCount}");
        if (!string.IsNullOrWhiteSpace(customMods.LastError))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, customMods.LastError);
        }
    }

    public void Dispose()
    {
    }
}
