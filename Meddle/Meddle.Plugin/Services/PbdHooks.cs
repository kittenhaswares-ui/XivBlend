using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Meddle.Plugin.Models.Structs;
using Microsoft.Extensions.Logging;

namespace Meddle.Plugin.Services;

public sealed class PbdHooks : IDisposable, IService
{
    public const string HumanCreateDeformerSig = "48 89 5C 24 ?? 57 48 83 EC 30 4C 8B C1 ";

    private readonly Dictionary<nint, Dictionary<uint, DeformerCachedStruct>> deformerCache = new();
    private readonly ILogger<PbdHooks> logger;
    private readonly Hook<HumanCreateDeformerDelegate>? humanCreateDeformerHook;

    public PbdHooks(
        ILogger<PbdHooks> logger,
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop)
    {
        this.logger = logger;
        if (!sigScanner.TryScanText(HumanCreateDeformerSig, out var address))
        {
            logger.LogError("Failed to find {DelegateName} signature", nameof(HumanCreateDeformerDelegate));
            return;
        }

        logger.LogDebug(
            "Found {DelegateName} at {Address:X}",
            nameof(HumanCreateDeformerDelegate),
            address);
        humanCreateDeformerHook = gameInterop.HookFromAddress<HumanCreateDeformerDelegate>(
            address,
            Human_CreateDeformerDetour);
        humanCreateDeformerHook.Enable();
    }

    public void Dispose()
    {
        logger.LogDebug("Disposing PbdHooks");
        humanCreateDeformerHook?.Dispose();
        deformerCache.Clear();
    }

    public DeformerCachedStruct? TryGetDeformer(nint humanPtr, uint slot)
    {
        if (!deformerCache.TryGetValue(humanPtr, out var slotCache))
        {
            return null;
        }

        return slotCache.TryGetValue(slot, out var deformer) ? deformer : null;
    }

    private unsafe nint Human_CreateDeformerDetour(nint humanPtr, uint slot)
    {
        var result = humanCreateDeformerHook!.Original(humanPtr, slot);

        var deformer = (DeformerStruct*)result;
        if (deformer != null && deformer->PbdPointer != null)
        {
            if (!deformerCache.TryGetValue(humanPtr, out var slotCache))
            {
                slotCache = new Dictionary<uint, DeformerCachedStruct>();
                deformerCache[humanPtr] = slotCache;
            }

            slotCache[slot] = new DeformerCachedStruct
            {
                DeformerId = deformer->DeformerId,
                RaceSexId = deformer->RaceSexId,
                PbdPath = deformer->PbdPointer->FileName.ToString()
            };
        }
        else if (deformerCache.TryGetValue(humanPtr, out var slotCache))
        {
            slotCache.Remove(slot);
            if (slotCache.Count == 0)
            {
                deformerCache.Remove(humanPtr);
            }
        }

        return result;
    }

    private delegate nint HumanCreateDeformerDelegate(nint humanPtr, uint slot);
}
