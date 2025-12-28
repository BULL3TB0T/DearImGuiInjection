using Hexa.NET.ImGui;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace DearImGuiInjection.Textures;

internal abstract class TextureManager<TEntry> : ITextureManager where TEntry : class
{
    private enum ChangeType
    {
        AddOrUpdate,
        Remove
    }

    private string RootDirectory;

    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentQueue<(ChangeType Type, string FullPath)> _queue = new();
    private readonly Dictionary<string, TEntry> _entries = new();

    public TextureManager()
    {
        RootDirectory = Path.Combine(DearImGuiInjectionCore.AssetsPath, "Textures");
        foreach (string file in Directory.EnumerateFiles(RootDirectory, "*.*", SearchOption.AllDirectories))
            _queue.Enqueue((ChangeType.AddOrUpdate, file));
        _watcher = new FileSystemWatcher(RootDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            Filter = "*.*",
            EnableRaisingEvents = true
        };
        _watcher.Created += (_, e) => Enqueue(ChangeType.AddOrUpdate, e.FullPath);
        _watcher.Changed += (_, e) => Enqueue(ChangeType.AddOrUpdate, e.FullPath);
        _watcher.Deleted += (_, e) => Enqueue(ChangeType.Remove, e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            Enqueue(ChangeType.Remove, e.OldFullPath);
            Enqueue(ChangeType.AddOrUpdate, e.FullPath);
        };
    }

    public void Update()
    {
        while (_queue.TryDequeue(out var item))
        {
            string fullPath = item.FullPath;
            string key = MakeRelativeKey(fullPath);
            if (item.Type == ChangeType.Remove)
            {
                RemoveEntry(key);
                continue;
            }
            if (!File.Exists(fullPath))
                continue;
            if (!TryLoadEntry(fullPath, out TEntry entry))
                continue;
            ReplaceEntry(key, entry);
        }
    }

    public unsafe bool TryGetTextureRef(string relativePath, out ImTextureRef textureRef)
    {
        relativePath = NormalizeRelativeKey(relativePath);
        if (_entries.TryGetValue(relativePath, out TEntry entry))
        {
            IntPtr textureId = GetTextureId(entry);
            if (textureId == IntPtr.Zero)
            {
                textureRef = default;
                return false;
            }
            textureRef = default;
            textureRef.TexData = default;
            textureRef.TexID = new ImTextureID(textureId); 
            return true;
        }
        textureRef = default;
        return false;
    }

    public bool TryGetTextureSize(string relativePath, out int width, out int height)
    {
        relativePath = NormalizeRelativeKey(relativePath);
        if (_entries.TryGetValue(relativePath, out TEntry entry))
        {
            GetTextureSize(entry, out width, out height);
            return width > 0 && height > 0;
        }
        width = 0;
        height = 0;
        return false;
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        foreach (var kv in _entries)
            DisposeEntry(kv.Value);
        _entries.Clear();
    }

    public abstract bool TryLoadEntry(string fullPath, out TEntry entry);
    public abstract void DisposeEntry(TEntry entry);
    public abstract IntPtr GetTextureId(TEntry entry);
    public abstract void GetTextureSize(TEntry entry, out int width, out int height);

    private static string NormalizeRelativeKey(string relativePath)
    {
        if (relativePath == null)
            return string.Empty;
        relativePath = relativePath.Replace('\\', '/').TrimStart('/');
        return relativePath;
    }

    private void Enqueue(ChangeType type, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return;
        fullPath = Path.GetFullPath(fullPath);
        if (!fullPath.StartsWith(RootDirectory, StringComparison.OrdinalIgnoreCase))
            return;
        _queue.Enqueue((type, fullPath));
    }

    private void ReplaceEntry(string key, TEntry entry)
    {
        RemoveEntry(key);
        _entries[key] = entry;
    }

    private void RemoveEntry(string key)
    {
        if (_entries.TryGetValue(key, out TEntry entry))
        {
            _entries.Remove(key);
            DisposeEntry(entry);
        }
    }

    private string MakeRelativeKey(string fullPath)
    {
        string rel = GetRelativePathCompat(RootDirectory, fullPath);
        return NormalizeRelativeKey(rel);
    }

    private static string GetRelativePathCompat(string baseDir, string fullPath)
    {
        baseDir = EnsureTrailingSeparator(Path.GetFullPath(baseDir));
        fullPath = Path.GetFullPath(fullPath);
        var baseUri = new Uri(baseDir);
        var fullUri = new Uri(fullPath);
        string rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString());
        return rel.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        char last = path[path.Length - 1];
        if (last != Path.DirectorySeparatorChar && last != Path.AltDirectorySeparatorChar)
            return path + Path.DirectorySeparatorChar;
        return path;
    }
}