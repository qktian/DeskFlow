using System.Collections.Concurrent;
using DeskFlow.Core.Models;

namespace DeskFlow.Core.Services;

/// <summary>Short-lived filesystem metadata cache shared by scanning and box sorting.</summary>
public static class FileMetadataCache
{
    private sealed record Entry(FileItem Item, DateTime ExpiresAt);
    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxEntries = 10_000;
    private static long _hits, _misses;
    public static (long Hits, long Misses) Statistics => (Interlocked.Read(ref _hits), Interlocked.Read(ref _misses));

    public static FileItem Get(string path)
    {
        if (Cache.TryGetValue(path, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
        {
            Interlocked.Increment(ref _hits);
            return entry.Item;
        }
        Interlocked.Increment(ref _misses);
        var item = FileItem.FromPath(path);
        Cache[path] = new Entry(item, DateTime.UtcNow.AddSeconds(30));
        if (Cache.Count > MaxEntries)
        {
            foreach (var old in Cache.Where(kv => kv.Value.ExpiresAt <= DateTime.UtcNow).Take(1000)) Cache.TryRemove(old.Key, out _);
            if (Cache.Count > MaxEntries)
                foreach (var old in Cache.Take(Cache.Count - MaxEntries)) Cache.TryRemove(old.Key, out _);
        }
        return item;
    }

    public static void Invalidate(string path) => Cache.TryRemove(path, out _);
    public static void Clear()
    {
        Cache.Clear();
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
    }
}
