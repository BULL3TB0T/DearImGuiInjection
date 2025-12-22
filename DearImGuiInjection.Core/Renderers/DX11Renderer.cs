using DearImGuiInjection;
using DearImGuiInjection.Backends;
using DearImGuiInjection.Windows;
using Reloaded.Hooks;
using Reloaded.Hooks.Tools;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

using Device = SharpDX.Direct3D11.Device;

namespace DearImGuiInjection.Renderers;

/// <summary>
/// Contains a full list of IDXGISwapChain functions to be used
/// as an indexer into the SwapChain Virtual Function Table entries.
/// </summary>
internal enum IDXGISwapChain
{
    // IUnknown
    QueryInterface = 0,
    AddRef = 1,
    Release = 2,

    // IDXGIObject
    SetPrivateData = 3,
    SetPrivateDataInterface = 4,
    GetPrivateData = 5,
    GetParent = 6,

    // IDXGIDeviceSubObject
    GetDevice = 7,

    // IDXGISwapChain
    Present = 8,
    GetBuffer = 9,
    SetFullscreenState = 10,
    GetFullscreenState = 11,
    GetDesc = 12,
    ResizeBuffers = 13,
    ResizeTarget = 14,
    GetContainingOutput = 15,
    GetFrameStatistics = 16,
    GetLastPresentCount = 17,
}

internal class DX11Renderer : IRenderer
{
    // https://github.com/BepInEx/BepInEx/blob/master/Runtimes/Unity/BepInEx.Unity.IL2CPP/Hook/INativeDetour.cs#L54
    // Workaround for CoreCLR collecting all delegates
    private static List<object> _cache = new();

    [Reloaded.Hooks.Definitions.X64.Function(Reloaded.Hooks.Definitions.X64.CallingConventions.Microsoft)]
    [Reloaded.Hooks.Definitions.X86.Function(Reloaded.Hooks.Definitions.X86.CallingConventions.Stdcall)]
    private delegate IntPtr CDXGISwapChainPresentDelegate(IntPtr self, uint syncInterval, uint flags);

    private static CDXGISwapChainPresentDelegate _swapChainPresentHookDelegate = new(SwapChainPresentHook);
    private static Hook<CDXGISwapChainPresentDelegate> _swapChainPresentHook;

    public static event Action<SwapChain, uint, uint> OnPresent { add { _onPresent += value; } remove { _onPresent -= value; } }
    private static Action<SwapChain, uint, uint> _onPresent;

    [Reloaded.Hooks.Definitions.X64.Function(Reloaded.Hooks.Definitions.X64.CallingConventions.Microsoft)]
    [Reloaded.Hooks.Definitions.X86.Function(Reloaded.Hooks.Definitions.X86.CallingConventions.Stdcall)]
    private delegate IntPtr CDXGISwapChainResizeBuffersDelegate(IntPtr self, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags);

    private static CDXGISwapChainResizeBuffersDelegate _swapChainResizeBuffersHookDelegate = new(SwapChainResizeBuffersHook);
    private static Hook<CDXGISwapChainResizeBuffersDelegate> _swapChainResizeBuffersHook;

    public static event Action<SwapChain, uint, uint, uint, Format, uint> OnPreResizeBuffers { add { _onPreResizeBuffers += value; } remove { _onPreResizeBuffers -= value; } }
    private static Action<SwapChain, uint, uint, uint, Format, uint> _onPreResizeBuffers;

    public static event Action<SwapChain, uint, uint, uint, Format, uint> OnPostResizeBuffers { add { _onPostResizeBuffers += value; } remove { _onPostResizeBuffers -= value; } }
    private static Action<SwapChain, uint, uint, uint, Format, uint> _onPostResizeBuffers;

    public RendererKind Kind => RendererKind.DX11;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool IsSupported()
    {
        bool hasD3D11 = false;
        bool hasD3D12 = false;
        try
        {
            foreach (var module in Process.GetCurrentProcess().Modules.Cast<ProcessModule>())
            {
                var name = module?.ModuleName;
                if (string.IsNullOrEmpty(name))
                    continue;
                name = name.ToLowerInvariant();
                if (name.Contains("d3d11"))
                    hasD3D11 = true;
                else if (name.Contains("d3d12"))
                    hasD3D12 = true;
            }
        }
        catch
        {
            return false;
        }
        return hasD3D11 || !hasD3D12;
    }

    public void Init()
    {
        var windowHandle = User32.CreateFakeWindow();
        var desc = new SwapChainDescription()
        {
            ModeDescription = new ModeDescription(0, 0, new Rational(0, 0), Format.R8G8B8A8_UNorm),
            SampleDescription = new SampleDescription(1, 0),
            Usage = Usage.RenderTargetOutput,
            BufferCount = 1,
            OutputHandle = windowHandle,
            IsWindowed = true,
            SwapEffect = SwapEffect.Discard
        };
        Device.CreateWithSwapChain(DriverType.Hardware, DeviceCreationFlags.None, desc, out var device, out var swapChain);
        var swapChainVTable = 
            VirtualFunctionTable.FromObject((nuint)(nint)swapChain.NativePointer, Enum.GetNames(typeof(IDXGISwapChain)).Length);
        var swapChainPresentFunctionPtr = 
            (nuint)(nint)swapChainVTable.TableEntries[(int)IDXGISwapChain.Present].FunctionPointer;
        var swapChainResizeBuffersFunctionPtr = 
            (nuint)(nint)swapChainVTable.TableEntries[(int)IDXGISwapChain.ResizeBuffers].FunctionPointer;
        device.Dispose();
        swapChain.Dispose();
        User32.DestroyWindow(windowHandle);

        _cache.Add(_swapChainPresentHookDelegate);
        _swapChainPresentHook = new(_swapChainPresentHookDelegate, swapChainPresentFunctionPtr);
        _swapChainPresentHook.Activate();

        _cache.Add(_swapChainResizeBuffersHookDelegate);
        _swapChainResizeBuffersHook = new(_swapChainResizeBuffersHookDelegate, swapChainResizeBuffersFunctionPtr);
        _swapChainResizeBuffersHook.Activate();

        ImGuiDX11.Init();
    }

    public void Dispose()
    {
        _swapChainResizeBuffersHook?.Disable();
        _swapChainResizeBuffersHook = null;

        _swapChainPresentHook?.Disable();
        _swapChainPresentHook = null;

        _onPresent = null;
        _onPreResizeBuffers = null;
        _onPostResizeBuffers = null;

        ImGuiDX11.Dispose();
    }

    private static IntPtr SwapChainPresentHook(IntPtr self, uint syncInterval, uint flags)
    {
        var swapChain = new SwapChain(self);
        if (_onPresent != null)
        {
            foreach (Action<SwapChain, uint, uint> item in _onPresent.GetInvocationList())
            {
                try
                {
                    item(swapChain, syncInterval, flags);
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
        }
        return _swapChainPresentHook.OriginalFunction(self, syncInterval, flags);
    }

    private static IntPtr SwapChainResizeBuffersHook(IntPtr self, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags)
    {
        var swapChain = new SwapChain(self);
        if (_onPreResizeBuffers != null)
        {
            foreach (Action<SwapChain, uint, uint, uint, Format, uint> item in _onPreResizeBuffers.GetInvocationList())
            {
                try
                {
                    item(swapChain, bufferCount, width, height, newFormat, swapchainFlags);
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
        }
        var result = _swapChainResizeBuffersHook.OriginalFunction(self, bufferCount, width, height, newFormat, swapchainFlags);
        if (_onPostResizeBuffers != null)
        {
            foreach (Action<SwapChain, uint, uint, uint, Format, uint> item in _onPostResizeBuffers.GetInvocationList())
            {
                try
                {
                    item(swapChain, bufferCount, width, height, newFormat, swapchainFlags);
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
        }
        return result;
    }
}