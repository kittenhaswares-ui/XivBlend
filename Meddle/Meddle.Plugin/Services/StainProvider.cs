using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Meddle.Plugin.Services;

public class StainProvider : IService
{
    private readonly IReadOnlyDictionary<uint, Stain> stains;

    public StainProvider(IDataManager dataManager)
    {
        stains = dataManager.GetExcelSheet<Stain>().ToDictionary(row => row.RowId, row => row);
    }

    public Stain? GetStain(uint rowId)
    {
        return stains.TryGetValue(rowId, out var stain) ? stain : null;
    }
}
