using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Meddle.Plugin.Models.Layout;
using Meddle.Plugin.Utils;

namespace Meddle.Plugin.Services;

/// <summary>
/// Captures the live model, material, skeleton, and attachment data for a character.
/// </summary>
public class ResolverService : IService
{
    private readonly PbdHooks pbdHooks;

    // ParseMaterialUtil reads the dye sheet cached by StainProvider. Keeping this
    // dependency explicit guarantees that the sheet is loaded before capture.
    private readonly StainProvider stainProvider;

    public ResolverService(PbdHooks pbdHooks, StainProvider stainProvider)
    {
        this.pbdHooks = pbdHooks;
        this.stainProvider = stainProvider;
    }

    public unsafe ParsedCharacterInfo? ParseCharacter(Character* character)
    {
        if (character == null)
        {
            return null;
        }

        var drawObject = character->DrawObject;
        if (drawObject == null)
        {
            return null;
        }

        var characterInfo = ParseMaterialUtil.ParseDrawObject(drawObject, pbdHooks, stainProvider);
        if (characterInfo == null)
        {
            return null;
        }

        var attaches = new List<ParsedCharacterInfo>();

        foreach (var weapon in character->DrawData.WeaponData)
        {
            if (weapon.DrawObject == null || weapon.IsHidden)
            {
                continue;
            }

            var weaponInfo = ParseMaterialUtil.ParseDrawObject(weapon.DrawObject, pbdHooks, stainProvider);
            if (weaponInfo != null)
            {
                attaches.Add(weaponInfo);
            }
        }

        characterInfo.Attaches = attaches.ToArray();

        return characterInfo;
    }
}
