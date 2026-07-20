using System.Text;
using FFXIVClientStructs.STD;

namespace Meddle.Plugin.Utils;

public static class StringUtil
{
    public static string ParseString(this StdString stdString)
    {
        var data = stdString.ToArray();
        if (data.Length == 0)
            return string.Empty;

        return Encoding.UTF8.GetString(data);
    }
}
