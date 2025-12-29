using Hexa.NET.ImGui;
using SharpDX.WIC;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DearImGuiInjection.Textures;

internal abstract class TextureManager<TEntry> : ITextureManager where TEntry : class
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

    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentQueue<(ChangeType Type, string FullPath)> _queue = new();
    internal readonly Dictionary<string, TEntry> Entries = new();
    private readonly Dictionary<string, HashSet<string>> _registeredOwnerKeys = new();
    internal readonly Dictionary<string, TEntry> RegisteredEntries = new();

    public TextureManager()
    {
        RootDirectory = Path.Combine(DearImGuiInjectionCore.AssetsPath, "Textures");
        RecreateRootDirectory();
        foreach (string fullPath in Directory.EnumerateFiles(RootDirectory, "*.*", SearchOption.AllDirectories))
            _queue.Enqueue((ChangeType.AddOrUpdate, fullPath));
        _watcher = new FileSystemWatcher(RootDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            Filter = "*.*",
            EnableRaisingEvents = true
        };
        _watcher.Created += (_, args) => Enqueue(ChangeType.AddOrUpdate, args.FullPath);
        _watcher.Changed += (_, args) => Enqueue(ChangeType.AddOrUpdate, args.FullPath);
        _watcher.Deleted += (_, args) => Enqueue(ChangeType.Remove, args.FullPath);
        _watcher.Renamed += (_, args) =>
        {
            Enqueue(ChangeType.Remove, args.OldFullPath);
            Enqueue(ChangeType.AddOrUpdate, args.FullPath);
        };
    }

    public void Update()
    {
        RecreateRootDirectory();
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
            if (!TryDecodeFramesRgba32Wic(fullPath, out DecodedFrame[] frames))
                continue;
            if (!TryLoadEntry(fullPath, frames, out TEntry entry))
                continue;
            ReplaceEntry(key, entry);
        }
        UpdateFrames(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }

    public bool TryGetTextureRef(string relativePath, out ImTextureRef textureRef)
    {
        textureRef = default;
        if (Entries.TryGetValue(NormalizeRelativeKey(relativePath), out TEntry entry))
        {
            ImTextureID textureId = GetTextureId(entry);
            if (textureId == IntPtr.Zero)
                return false;
            textureRef.TexID = textureId;
            return true;
        }
        return false;
    }

    public bool TryGetTextureRefForFrame(string relativePath, int frame, out ImTextureRef textureRef)
    {
        textureRef = default;
        if (Entries.TryGetValue(NormalizeRelativeKey(relativePath), out TEntry entry))
        {
            ImTextureID textureId = GetTextureId(entry, frame);
            if (textureId == IntPtr.Zero)
                return false;
            textureRef.TexID = textureId;
            return true;
        }
        return false;
    }

    public bool TryGetTextureSize(string relativePath, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (Entries.TryGetValue(NormalizeRelativeKey(relativePath), out TEntry entry))
        {
            GetTextureSize(entry, out width, out height);
            return width > 0 && height > 0;
        }
        return false;
    }

    public bool RegisterTexture(string ownerId, string key, IntPtr ptr)
    {
        if (!DearImGuiInjectionCore.MultiContextCompositor.Modules.Any(module => module.Id == ownerId)
            || !TryCreateEntryFromNative(ptr, out TEntry entry))
            return false;
        if (RegisteredEntries.TryGetValue(key, out TEntry oldEntry))
        {
            RegisteredEntries.Remove(key);
            DisposeEntry(oldEntry);
        }
        RegisteredEntries[key] = entry;
        if (!_registeredOwnerKeys.TryGetValue(ownerId, out var set))
        {
            set = new HashSet<string>();
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
        if (RegisteredEntries.TryGetValue(key, out TEntry entry))
        {
            RegisteredEntries.Remove(key);
            DisposeEntry(entry);
        }
        if (set.Count == 0)
            _registeredOwnerKeys.Remove(ownerId);
        return true;
    }

    public bool TryGetRegisteredTextureRef(string ownerId, string key, out ImTextureRef textureRef)
    {
        textureRef = default;
        if (!_registeredOwnerKeys.TryGetValue(ownerId, out var set) || !set.Contains(key))
            return false;
        if (RegisteredEntries.TryGetValue(key, out TEntry entry))
        {
            ImTextureID textureId = GetTextureId(entry);
            if (textureId == IntPtr.Zero)
                return false;
            textureRef.TexID = textureId;
            return true;
        }
        return false;
    }

    public bool TryGetRegisteredTextureRefForFrame(string ownerId, string key, int frame, out ImTextureRef textureRef)
    {
        textureRef = default;
        if (!_registeredOwnerKeys.TryGetValue(ownerId, out var set) || !set.Contains(key))
            return false;
        if (RegisteredEntries.TryGetValue(key, out TEntry entry))
        {
            ImTextureID textureId = GetTextureId(entry, frame);
            if (textureId == IntPtr.Zero)
                return false;
            textureRef.TexID = textureId;
            return true;
        }
        return false;
    }

    public bool TryGetRegisteredTextureSize(string ownerId, string key, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!_registeredOwnerKeys.TryGetValue(ownerId, out var set) || !set.Contains(key))
            return false;
        if (RegisteredEntries.TryGetValue(key, out TEntry entry))
        {
            GetTextureSize(entry, out width, out height);
            return width > 0 && height > 0;
        }
        return false;
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        foreach (var pair in Entries)
            DisposeEntry(pair.Value);
        Entries.Clear();
        _registeredOwnerKeys.Clear();
        foreach (var pair in RegisteredEntries)
            DisposeEntry(pair.Value);
        RegisteredEntries.Clear();
    }

    public abstract void UpdateFrames(double nowSeconds);

    public abstract bool TryCreateEntryFromNative(IntPtr ptr, out TEntry entry);
    public abstract bool TryLoadEntry(string fullPath, DecodedFrame[] frames, out TEntry entry);
    public abstract void DisposeEntry(TEntry entry);

    public abstract ImTextureID GetTextureId(TEntry entry, int frame = -1);
    public abstract void GetTextureSize(TEntry entry, out int width, out int height);

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
                int delayMs = TryReadGifDelayMs(frame);
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
        Entries[key] = entry;
    }

    private void RemoveEntry(string key)
    {
        if (Entries.TryGetValue(key, out TEntry entry))
        {
            Entries.Remove(key);
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