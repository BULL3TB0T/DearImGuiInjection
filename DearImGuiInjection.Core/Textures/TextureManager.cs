using Hexa.NET.ImGui;
using SharpDX.WIC;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DearImGuiInjection.Textures;

internal abstract class TextureManager<TEntryData> : ITextureManager where TEntryData : struct
{
    private enum ChangeType
    {
        AddOrUpdate,
        Remove
    }

    internal struct DecodedFrame
    {
        public byte[] Rgba;
        public int Width;
        public int Height;
        public int DelayMs;
    }

    private string RootDirectory;

    internal float NowSeconds;

    private float _nextScanInSeconds;
    private const float ScanIntervalSeconds = 0.5f;

    private readonly Queue<(ChangeType Type, string FullPath)> _queue = new();
    private readonly Dictionary<string, DateTime> _knownWriteTimesUtc = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, TEntryData> Entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _registeredOwnerKeys = new();
    internal readonly Dictionary<string, TEntryData> RegisteredEntries = new();

    public TextureManager() => RootDirectory = Path.Combine(DearImGuiInjectionCore.AssetsPath, "Textures");

    public void Update()
    {
        RecreateRootDirectory();
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
            string fullPath = item.FullPath;
            string key = MakeRelativeKey(fullPath);
            if (item.Type == ChangeType.Remove)
            {
                RemoveEntry(key);
                continue;
            }
            if (!File.Exists(fullPath))
                continue;
            if (!TryDecodeFramesRgba32Wic(fullPath, out DecodedFrame[] frames))
                continue;
            if (!TryCreateEntryData(fullPath, frames, out TEntryData entryData))
                continue;
            ReplaceEntry(key, entryData);
        }
    }

    public bool TryGetTextureData(string relativePath, out ITextureManager.TextureData textureData)
    {
        textureData = default;
        if (Entries.TryGetValue(NormalizeRelativeKey(relativePath), out TEntryData entryData))
        {
            textureData = GetTextureData(entryData);
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
            textureData = GetTextureData(entryData);
            return true;
        }
        return false;
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

    public abstract void UpdateEntryData(ref TEntryData entryData);
    public abstract void DisposeEntryData(TEntryData entryData);

    public abstract bool TryCreateEntryData(string fullPath, DecodedFrame[] frames, out TEntryData entryData);
    public abstract bool TryCreateEntryData(IntPtr ptr, out TEntryData entryData);

    public abstract ITextureManager.TextureData GetTextureData(TEntryData entryData);

    private static bool TryDecodeFramesRgba32Wic(string fullPath, out DecodedFrame[] frames)
    {
        frames = null;
        try
        {
            using var factory = new ImagingFactory2();
            using var fs = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var wicStream = new WICStream(factory, fs);
            using var decoder = new BitmapDecoder(factory, wicStream, DecodeOptions.CacheOnLoad);
            int frameCount = decoder.FrameCount;
            if (frameCount <= 0)
                return false;
            int canvasWidth = 0;
            int canvasHeight = 0;
            for (int i = 0; i < frameCount; i++)
            {
                using var frame = decoder.GetFrame(i);
                int width = frame.Size.Width;
                int height = frame.Size.Height;
                if (width <= 0 || height <= 0)
                    return false;
                int left = ReadMetaInt(frame, "/imgdesc/Left", 0);
                int top = ReadMetaInt(frame, "/imgdesc/Top", 0);
                int right = left + width;
                int bottom = top + height;
                if (right > canvasWidth)
                    canvasWidth = right;
                if (bottom > canvasHeight)
                    canvasHeight = bottom;
            }
            if (canvasWidth <= 0 || canvasHeight <= 0)
                return false;
            frames = new DecodedFrame[frameCount];
            byte[] canvas = new byte[canvasWidth * canvasHeight * 4];
            int prevLeft = 0;
            int prevTop = 0;
            int prevWidth = 0;
            int prevHeight = 0;
            int prevDisposal = 0;
            for (int i = 0; i < frameCount; i++)
            {
                using var frame = decoder.GetFrame(i);
                int width = frame.Size.Width;
                int height = frame.Size.Height;
                if (width <= 0 || height <= 0)
                    return false;
                int left = ReadMetaInt(frame, "/imgdesc/Left", 0);
                int top = ReadMetaInt(frame, "/imgdesc/Top", 0);
                int disposal = ReadMetaInt(frame, "/grctlext/Disposal", 0);
                int delayMs = 0;
                if (frameCount > 1)
                    delayMs = TryReadGifDelayMs(frame);
                if (i > 0 && prevDisposal == 2)
                    ClearRect(canvas, canvasWidth, canvasHeight, prevLeft, prevTop, prevWidth, prevHeight);
                using var converter = new FormatConverter(factory);
                converter.Initialize(frame, PixelFormat.Format32bppRGBA);
                byte[] patch = new byte[width * height * 4];
                converter.CopyPixels(patch, width * 4);
                BlitRgbaAlpha(patch, width, height, canvas, canvasWidth, canvasHeight, left, top);
                byte[] outRgba = new byte[canvas.Length];
                Buffer.BlockCopy(canvas, 0, outRgba, 0, canvas.Length);
                frames[i] = new DecodedFrame
                {
                    Rgba = outRgba,
                    Width = canvasWidth,
                    Height = canvasHeight,
                    DelayMs = delayMs
                };
                prevLeft = left;
                prevTop = top;
                prevWidth = width;
                prevHeight = height;
                prevDisposal = disposal;
            }
            return true;
        }
        catch
        {
            frames = null;
            return false;
        }
    }

    private static int ReadMetaInt(BitmapFrameDecode frame, string name, int fallback)
    {
        try
        {
            var reader = frame.MetadataQueryReader;
            if (reader == null)
                return fallback;
            object value = reader.GetMetadataByName(name);
            return value switch
            {
                byte b => b,
                sbyte sb => sb,
                short s => s,
                ushort us => us,
                int i => i,
                uint ui => (int)ui,
                _ => fallback
            };
        }
        catch
        {
            return fallback;
        }
    }

    private static int TryReadGifDelayMs(BitmapFrameDecode frame)
    {
        try
        {
            var reader = frame.MetadataQueryReader;
            if (reader == null)
                return 100;
            object delayObj = reader.GetMetadataByName("/grctlext/Delay");
            int delayCs = 0;
            if (delayObj is ushort u16)
                delayCs = u16;
            else if (delayObj is short s16)
                delayCs = s16;
            else if (delayObj is uint u32)
                delayCs = (int)u32;
            else if (delayObj is int i32)
                delayCs = i32;
            int ms = delayCs * 10;
            if (ms <= 0)
                ms = 100;
            return ms;
        }
        catch
        {
            return 100;
        }
    }

    private static void ClearRect(byte[] canvas, int canvasW, int canvasH, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0)
            return;
        if (x < 0)
        {
            w += x;
            x = 0;
        }
        if (y < 0)
        {
            h += y;
            y = 0;
        }
        if (x >= canvasW || y >= canvasH)
            return;
        int maxW = canvasW - x;
        int maxH = canvasH - y;
        if (w > maxW)
            w = maxW;
        if (h > maxH)
            h = maxH;
        for (int yy = 0; yy < h; yy++)
        {
            int row = ((y + yy) * canvasW + x) * 4;
            Array.Clear(canvas, row, w * 4);
        }
    }

    private static void BlitRgbaAlpha(byte[] src, int srcW, int srcH, byte[] dst, int dstW, int dstH, int dstX, int dstY)
    {
        for (int y = 0; y < srcH; y++)
        {
            int dstY2 = dstY + y;
            if ((uint)dstY2 >= (uint)dstH)
                continue;
            int srcRow = y * srcW * 4;
            for (int x = 0; x < srcW; x++)
            {
                int dstX2 = dstX + x;
                if ((uint)dstX2 >= (uint)dstW)
                    continue;
                int srcIndex = srcRow + x * 4;
                byte alpha = src[srcIndex + 3];
                if (alpha == 0)
                    continue;
                int dstIndex = (dstY2 * dstW + dstX2) * 4;
                dst[dstIndex + 0] = src[srcIndex + 0];
                dst[dstIndex + 1] = src[srcIndex + 1];
                dst[dstIndex + 2] = src[srcIndex + 2];
                dst[dstIndex + 3] = alpha;
            }
        }
    }

    private static string NormalizeRelativeKey(string relativePath)
    {
        if (relativePath == null)
            return string.Empty;
        relativePath = relativePath.Replace('\\', '/').TrimStart('/');
        return relativePath;
    }

    private void ReplaceEntry(string key, TEntryData entryData)
    {
        RemoveEntry(key);
        Entries[key] = entryData;
    }

    private void RemoveEntry(string key)
    {
        if (Entries.TryGetValue(key, out TEntryData entryData))
        {
            Entries.Remove(key);
            DisposeEntryData(entryData);
        }
    }

    private void ScanForChanges()
    {
        if (!Directory.Exists(RootDirectory))
        {
            _knownWriteTimesUtc.Clear();
            foreach (var pair in Entries.ToArray())
                _queue.Enqueue((ChangeType.Remove, Path.Combine(RootDirectory, pair.Key)));
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
                _queue.Enqueue((ChangeType.AddOrUpdate, fullPath));
                continue;
            }
            _knownWriteTimesUtc[fullPath] = writeTimeUtc;
            _queue.Enqueue((ChangeType.AddOrUpdate, fullPath));
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
            _queue.Enqueue((ChangeType.Remove, fullPath));
        }
    }

    private void TryRememberWriteTime(string fullPath)
    {
        if (TryGetWriteTimeUtc(fullPath, out DateTime writeTimeUtc))
            _knownWriteTimesUtc[fullPath] = writeTimeUtc;
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
        return rel.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;
        char last = path[path.Length - 1];
        if (last != Path.DirectorySeparatorChar && last != Path.AltDirectorySeparatorChar)
            return path + Path.DirectorySeparatorChar;
        return path;
    }

    private void RecreateRootDirectory()
    {
        Directory.CreateDirectory(DearImGuiInjectionCore.AssetsPath);
        Directory.CreateDirectory(RootDirectory);
        string fullPath = Path.Combine(RootDirectory, "INSERT-TEXTURES-HERE");
        if (!File.Exists(fullPath))
            File.WriteAllBytes(fullPath, []);
    }
}