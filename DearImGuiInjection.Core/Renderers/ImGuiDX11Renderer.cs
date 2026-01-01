using DearImGuiInjection.Handlers;
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

internal class ImGuiDX11Renderer : ImGuiRenderer
{
    // https://github.com/BepInEx/BepInEx/blob/master/Runtimes/Unity/BepInEx.Unity.IL2CPP/Hook/INativeDetour.cs#L54
    // Workaround for CoreCLR collecting all delegates
    private List<object> _cache = new();

    [Reloaded.Hooks.Definitions.X64.Function(Reloaded.Hooks.Definitions.X64.CallingConventions.Microsoft)]
    [Reloaded.Hooks.Definitions.X86.Function(Reloaded.Hooks.Definitions.X86.CallingConventions.Stdcall)]
    private delegate IntPtr CDXGISwapChainPresentDelegate(IntPtr self, uint syncInterval, uint flags);

    private CDXGISwapChainPresentDelegate _swapChainPresentHookDelegate;
    private Hook<CDXGISwapChainPresentDelegate> _swapChainPresentHook;

    [Reloaded.Hooks.Definitions.X64.Function(Reloaded.Hooks.Definitions.X64.CallingConventions.Microsoft)]
    [Reloaded.Hooks.Definitions.X86.Function(Reloaded.Hooks.Definitions.X86.CallingConventions.Stdcall)]
    private delegate IntPtr CDXGISwapChainResizeBuffersDelegate(IntPtr self, uint bufferCount, uint width, uint height, Format newFormat, uint swapChainFlags);

    private CDXGISwapChainResizeBuffersDelegate _swapChainResizeBuffersHookDelegate;
    private Hook<CDXGISwapChainResizeBuffersDelegate> _swapChainResizeBuffersHook;

    public override RendererKind Kind => RendererKind.DX11;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override bool IsSupported()
    {
        bool hasD3D11 = false;
        bool hasD3D12 = false;
        try
        {
            foreach (var module in Process.GetCurrentProcess().Modules.Cast<ProcessModule>())
            {
                var name = module?.ModuleName;
                if (string.IsNullOrWhiteSpace(name))
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

    public override void Init()
    {
        var windowHandle = User32.CreateFakeWindow();
        var description = new SwapChainDescription()
        {
            ModeDescription = new ModeDescription(0, 0, new Rational(0, 0), Format.R8G8B8A8_UNorm),
            SampleDescription = new SampleDescription(1, 0),
            Usage = Usage.RenderTargetOutput,
            BufferCount = 1,
            OutputHandle = windowHandle,
            IsWindowed = true,
            SwapEffect = SwapEffect.Discard
        };
        Device.CreateWithSwapChain(DriverType.Hardware, DeviceCreationFlags.None, description, out var device, out var swapChain);
        var swapChainVTable = 
            VirtualFunctionTable.FromObject((nuint)(nint)swapChain.NativePointer, Enum.GetNames(typeof(IDXGISwapChain)).Length);
        var swapChainPresentFunctionPtr = (nuint)(nint)swapChainVTable.TableEntries[(int)IDXGISwapChain.Present].FunctionPointer;
        var swapChainResizeBuffersFunctionPtr = (nuint)(nint)swapChainVTable.TableEntries[(int)IDXGISwapChain.ResizeBuffers].FunctionPointer;
        device.Dispose();
        swapChain.Dispose();
        User32.DestroyWindow(windowHandle);
        _swapChainPresentHookDelegate = SwapChainPresentHook;
        _cache.Add(_swapChainPresentHookDelegate);
        _swapChainPresentHook = new(_swapChainPresentHookDelegate, swapChainPresentFunctionPtr);
        _swapChainPresentHook.Activate();
        _swapChainResizeBuffersHookDelegate = SwapChainResizeBuffersHook;
        _cache.Add(_swapChainResizeBuffersHookDelegate);
        _swapChainResizeBuffersHook = new(_swapChainResizeBuffersHookDelegate, swapChainResizeBuffersFunctionPtr);
        _swapChainResizeBuffersHook.Activate();
        Handler = new ImGuiDX11Handler();
    }

    public override void Dispose()
    {
        _swapChainPresentHook?.Disable();
        _swapChainPresentHook = null;
        _swapChainResizeBuffersHook?.Disable();
        _swapChainResizeBuffersHook = null;
        Handler?.Dispose();
    }

    private IntPtr SwapChainPresentHook(IntPtr self, uint syncInterval, uint flags)
    {
        ((ImGuiDX11Handler)Handler).OnPresent(new SwapChain(self), syncInterval, flags);
        return _swapChainPresentHook.OriginalFunction(self, syncInterval, flags);
    }

    private IntPtr SwapChainResizeBuffersHook(IntPtr self, uint bufferCount, uint width, uint height, Format newFormat,
        uint swapChainFlags)
    {
        using var swapChain = new SwapChain(self);
        ((ImGuiDX11Handler)Handler).OnPreResizeBuffers(swapChain, bufferCount, width, height, newFormat, swapChainFlags);
        IntPtr result = _swapChainResizeBuffersHook.OriginalFunction(self, bufferCount, width, height, newFormat, swapChainFlags);
        ((ImGuiDX11Handler)Handler).OnPostResizeBuffers(swapChain, bufferCount, width, height, newFormat, swapChainFlags);
        return result;
    }
}