using System.Collections.Concurrent;

namespace Meddle.Plugin.Models.Composer;

public sealed class ExportProgress
{
    private int progress;

    public ExportProgress(int total, string? name)
    {
        Total = total;
        Name = name;
    }

    public int Progress => progress;
    public int Total { get; }
    public bool IsComplete { get; set; }
    public string? Name { get; }
    public ExportProgress? Parent { get; set; }
    public ConcurrentBag<ExportProgress> Children { get; } = [];

    public void IncrementProgress(int amount = 1)
    {
        Interlocked.Add(ref progress, amount);
        Parent?.IncrementProgress(amount);
    }
}
