using Hexa.NET.ImGui;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DearImGuiInjection.Textures;

internal abstract class TextureManager<TEntryData, TEntryFrameData> : ITextureManager
    where TEntryData : struct, TextureManager<TEntryData, TEntryFrameData>.IEntryData
    where TEntryFrameData : struct, TextureManager<TEntryData, TEntryFrameData>.IEntryFrameData
{
    internal interface IEntryData
    {
        public TEntryFrameData[] FrameDatas { get; set; }
        public int FrameIndex { get; set; }
        public float NextFrameInSeconds { get; set; }
        public ITextureManager.TextureData CachedTextureData { get; set; }
        public ITextureManager.TextureData.TextureFrameData[] CachedTextureFrameDatas { get; set; }
    }

    internal interface IEntryFrameData
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int DelayMs { get; set; }
    }

    internal struct DecodedFrame
    {
        public byte[] Pixels;
        public int Width;
        public int Height;
        public int DelayMs;
    }

    private string RootDirectory;

    private readonly Queue<(string FullPath, bool ShouldRemove)> _queue = new();
    private readonly Dictionary<string, DateTime> _knownWriteTimesUtc = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, TEntryData> Entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _registeredOwnerKeys = new();
    internal readonly Dictionary<string, TEntryData> RegisteredEntries = new();

    private float NowSeconds;
    private float _nextScanInSeconds;
    private const float ScanIntervalSeconds = 0.5f;

    public TextureManager() => RootDirectory = Path.Combine(DearImGuiInjectionCore.AssetsPath, "Textures");

    public void Update()
    {
        Directory.CreateDirectory(DearImGuiInjectionCore.AssetsPath);
        Directory.CreateDirectory(RootDirectory);
        string fullPath = Path.Combine(RootDirectory, "INSERT-TEXTURES-HERE");
        if (!File.Exists(fullPath))
            File.WriteAllBytes(fullPath, []);
        NowSeconds = Stopwatch.GetTimestamp() / (float)Stopwatch.Frequency;
        if (NowSeconds >= _nextScanInSeconds)
        {
            _nextScanInSeconds = NowSeconds + ScanIntervalSeconds;
            ScanForChanges();
        }
        string[] keys = Entries.Keys.ToArray();
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (!Entries.TryGetValue(key, out TEntryData entryData))
                continue;
            UpdateEntryData(ref entryData);
            Entries[key] = entryData;
        }
        keys = RegisteredEntries.Keys.ToArray();
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (!RegisteredEntries.TryGetValue(key, out TEntryData entryData))
                continue;
            UpdateEntryData(ref entryData);
            RegisteredEntries[key] = entryData;
        }
        while (_queue.Count > 0)
        {
            var item = _queue.Dequeue();
            fullPath = item.FullPath;
            string key = MakeRelativeKey(fullPath);
            if (item.ShouldRemove)
            {
                if (Entries.TryGetValue(key, out TEntryData queuedEntryData))
                {
                    Entries.Remove(key);
                    DisposeEntryData(queuedEntryData);
                }
                continue;
            }
            if (!File.Exists(fullPath))
                continue;
            if (!TryCreateDecodedFrames(fullPath, out DecodedFrame[] frames))
                continue;
            if (!TryCreateEntryDatas(frames, out TEntryData entryData))
                continue;
            Entries[key] = entryData;
        }
    }

    public void Dispose()
    {
        _knownWriteTimesUtc.Clear();
        foreach (var pair in Entries)
            DisposeEntryData(pair.Value);
        Entries.Clear();
        _registeredOwnerKeys.Clear();
        foreach (var pair in RegisteredEntries)
            DisposeEntryData(pair.Value);
        RegisteredEntries.Clear();
    }

    public abstract void OnDispose();

    public bool TryGetTextureData(string relativePath, out ITextureManager.TextureData textureData)
    {
        textureData = default;
        string key = NormalizeRelativeKey(relativePath);
        if (Entries.TryGetValue(key, out TEntryData entryData))
        {
            textureData = GetTextureData(ref entryData);
            Entries[key] = entryData;
            return true;
        }
        return false;
    }

    public bool RegisterTexture(string ownerId, string key, IntPtr ptr)
    {
        if (!DearImGuiInjectionCore.MultiContextCompositor.Modules.Any(module => module.Id == ownerId)
            || !TryCreateEntryData(ptr, out TEntryData entryData))
            return false;
        if (RegisteredEntries.TryGetValue(key, out TEntryData oldEntryData))
        {
            RegisteredEntries.Remove(key);
            DisposeEntryData(oldEntryData);
        }
        RegisteredEntries[key] = entryData;
        if (!_registeredOwnerKeys.TryGetValue(ownerId, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _registeredOwnerKeys[ownerId] = set;
        }
        set.Add(key);
        return true;
    }

    public bool UnregisterTexture(string ownerId, string key)
    {
        if (!DearImGuiInjectionCore.MultiContextCompositor.Modules.Any(module => module.Id == ownerId)
            || string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(key))
            return false;
        if (!_registeredOwnerKeys.TryGetValue(ownerId, out var set) || !set.Remove(key))
            return false;
        if (RegisteredEntries.TryGetValue(key, out TEntryData entryData))
        {
            RegisteredEntries.Remove(key);
            DisposeEntryData(entryData);
        }
        if (set.Count == 0)
            _registeredOwnerKeys.Remove(ownerId);
        return true;
    }

    public bool TryGetTextureData(string ownerId, string key, out ITextureManager.TextureData textureData)
    {
        textureData = default;
        if (!_registeredOwnerKeys.TryGetValue(ownerId, out var set) || !set.Contains(key))
            return false;
        if (RegisteredEntries.TryGetValue(key, out TEntryData entryData))
        {
            textureData = GetTextureData(ref entryData);
            RegisteredEntries[key] = entryData;
            return true;
        }
        return false;
    }

    public abstract void DisposeEntryData(TEntryData entryData);

    public abstract bool TryCreateEntryData(IntPtr ptr, out TEntryData entryData);
    public abstract bool TryCreateEntryDatas(DecodedFrame[] frames, out TEntryData entryData);

    public abstract ITextureManager.TextureData GetTextureData(ref TEntryData entryData);

    private unsafe static bool TryCreateDecodedFrames(string fullPath, out DecodedFrame[] frames)
    {
        frames = null;
        try
        {
            using var fs = new FileStream(fullPath, 
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Image<Rgba32> image = Image.Load<Rgba32>(fs, out IImageFormat format);
            int frameCount = image.Frames.Count;
            if (frameCount <= 0 || image.Width <= 0 || image.Height <= 0)
                return false;
            bool isGif = format is GifFormat;
            DecodedFrame[] decodedFrames = new DecodedFrame[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                ImageFrame<Rgba32> frame = image.Frames[i];
                int delayMs = 0;
                if (isGif)
                    delayMs = frame.Metadata.GetGifMetadata().FrameDelay * 10;
                byte[] pixels = new byte[frame.Width * frame.Height * 4];
                frame.CopyPixelDataTo(pixels);
                decodedFrames[i] = new DecodedFrame
                {
                    Pixels = pixels,
                    Width = frame.Width,
                    Height = frame.Height,
                    DelayMs = delayMs
                };
            }
            frames = decodedFrames;
            return true;
        }
        catch
        {
            frames = null;
            return false;
        }
    }

    private void UpdateEntryData(ref TEntryData entryData)
    {
        void SyncCachedEntryData(ref TEntryData entryData)
        {
            ITextureManager.TextureData cached = entryData.CachedTextureData;
            cached.FrameIndex = entryData.FrameIndex;
            cached.NextFrameInSeconds = entryData.NextFrameInSeconds;
            entryData.CachedTextureData = cached;
        }
        TEntryFrameData[] frames = entryData.FrameDatas;
        int frameCount = frames.Length;
        if (frameCount == 1)
        {
            entryData.FrameIndex = 0;
            entryData.NextFrameInSeconds = 0;
            SyncCachedEntryData(ref entryData);
            return;
        }
        if (entryData.NextFrameInSeconds <= 0f)
        {
            int firstDelayMs = frames[0].DelayMs;
            if (firstDelayMs <= 0)
                firstDelayMs = 100;
            entryData.FrameIndex = 0;
            entryData.NextFrameInSeconds = NowSeconds + (firstDelayMs / 1000f);
            SyncCachedEntryData(ref entryData);
            return;
        }
        if (NowSeconds < entryData.NextFrameInSeconds)
        {
            SyncCachedEntryData(ref entryData);
            return;
        }
        int nextIndex = entryData.FrameIndex + 1;
        if (nextIndex >= frameCount)
            nextIndex = 0;
        entryData.FrameIndex = nextIndex;
        int delayMs = frames[nextIndex].DelayMs;
        if (delayMs <= 0)
            delayMs = 100;
        entryData.NextFrameInSeconds = NowSeconds + (delayMs / 1000f);
        SyncCachedEntryData(ref entryData);
    }

    private void ScanForChanges()
    {
        if (!Directory.Exists(RootDirectory))
        {
            _knownWriteTimesUtc.Clear();
            foreach (var pair in Entries.ToArray())
                _queue.Enqueue((Path.Combine(RootDirectory, pair.Key), true));
            Entries.Clear();
            return;
        }
        foreach (string fullPath in Directory.EnumerateFiles(RootDirectory, "*.*", SearchOption.AllDirectories))
        {
            if (!TryGetWriteTimeUtc(fullPath, out DateTime writeTimeUtc))
                continue;
            if (_knownWriteTimesUtc.TryGetValue(fullPath, out DateTime knownUtc))
            {
                if (writeTimeUtc <= knownUtc)
                    continue;
                _knownWriteTimesUtc[fullPath] = writeTimeUtc;
                _queue.Enqueue((fullPath, false));
                continue;
            }
            _knownWriteTimesUtc[fullPath] = writeTimeUtc;
            _queue.Enqueue((fullPath, false));
        }
        if (_knownWriteTimesUtc.Count == 0)
            return;
        string[] knownPaths = _knownWriteTimesUtc.Keys.ToArray();
        for (int i = 0; i < knownPaths.Length; i++)
        {
            string fullPath = knownPaths[i];
            if (File.Exists(fullPath))
                continue;
            _knownWriteTimesUtc.Remove(fullPath);
            _queue.Enqueue((fullPath, true));
        }
    }

    private static bool TryGetWriteTimeUtc(string fullPath, out DateTime writeTimeUtc)
    {
        writeTimeUtc = default;
        try
        {
            writeTimeUtc = File.GetLastWriteTimeUtc(fullPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string NormalizeRelativeKey(string relativePath)
    {
        if (relativePath == null)
            return string.Empty;
        relativePath = relativePath.Replace('\\', '/').TrimStart('/');
        return relativePath;
    }

    private string MakeRelativeKey(string fullPath)
    {
        string rel = GetRelativePathCompat(RootDirectory, fullPath);
        return NormalizeRelativeKey(rel);
    }

    private string GetRelativePathCompat(string baseDir, string fullPath)
    {
        baseDir = EnsureTrailingSeparator(Path.GetFullPath(baseDir));
        fullPath = Path.GetFullPath(fullPath);
        var baseUri = new Uri(baseDir);
        var fullUri = new Uri(fullPath);
        string rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString());
        return rel.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private string EnsureTrailingSeparator(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;
        char last = path[path.Length - 1];
        if (last != Path.DirectorySeparatorChar && last != Path.AltDirectorySeparatorChar)
            return path + Path.DirectorySeparatorChar;
        return path;
    }
}