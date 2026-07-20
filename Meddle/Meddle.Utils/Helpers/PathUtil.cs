using Meddle.Utils.Files.SqPack;

namespace Meddle.Utils.Helpers;

public static class PathUtil
{
    public static byte[]? GetFileOrReadFromDisk(this SqPack pack, string path)
    {
        path = path.TrimHandlePath();
        
        // if path is rooted, get from disk
        if (Path.IsPathRooted(path))
        {
            var data = File.ReadAllBytes(path);
            return data;
        }

        var fileDescriptor = pack.GetFile(path);
        return fileDescriptor?.File.RawData.ToArray();
    }
    
    public static string TrimHandlePath(this string path)
    {
        // if path is in format |...|path/to/file, trim the |...| part
        if (path[0] == '|')
        {
            path = path[(path.IndexOf('|', 1) + 1)..];
        }

        return path;
    }
}
