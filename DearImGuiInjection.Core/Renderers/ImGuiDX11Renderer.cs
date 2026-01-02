using DearImGuiInjection.Handlers;
using DearImGuiInjection.Windows;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Device = SharpDX.Direct3D11.Device;

namespace DearImGuiInjection.Renderers;

internal class ImGuiDX11Renderer : ImGuiRenderer
{
    internal enum IDXGISwapChain
    {
        QueryInterface,
        AddRef,
        Release,
        SetPrivateData,
        SetPrivateDataInterface,
        GetPrivateData,
        GetParent,
        GetDevice,
        Present,
        GetBuffer,
        SetFullscreenState,
        GetFullscreenState,
        GetDesc,
        ResizeBuffers,
        ResizeTarget,
        GetContainingOutput,
        GetFrameStatistics,
        GetLastPresentCount
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PresentDelegate(IntPtr self, uint syncInterval, uint flags);
    private IntPtr _presentPtr;
    private PresentDelegate _presentHook;
    private PresentDelegate _presentOriginal;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResizeBuffersDelegate(IntPtr self, uint bufferCount, uint width, uint height, Format newFormat, uint swapChainFlags);
    private IntPtr _resizeBuffersPtr;
    private ResizeBuffersDelegate _resizeBuffersHook;
    private ResizeBuffersDelegate _resizeBuffersOriginal;

    private bool _hooksInitialized;

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

    public override unsafe void Init()
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
        nint vTablePtr = *(nint*)swapChain.NativePointer;
        nint* vTable = (nint*)vTablePtr;
        _presentPtr = vTable[(int)IDXGISwapChain.Present];
        _resizeBuffersPtr = vTable[(int)IDXGISwapChain.ResizeBuffers];
        device.Dispose();
        swapChain.Dispose();
        User32.DestroyWindow(windowHandle);
        Handler = new ImGuiDX11Handler();
        MinHook.OK(MinHook.Initialize(), "MH_Initialize");
        _presentHook = PresentHook;
        IntPtr presentDetour = Marshal.GetFunctionPointerForDelegate(_presentHook);
        MinHook.OK(MinHook.CreateHook(_presentPtr, presentDetour, out IntPtr presentOriginalPtr), "MH_CreateHook(Present)");
        MinHook.OK(MinHook.EnableHook(_presentPtr), "MH_EnableHook(Present)");
        _presentOriginal = Marshal.GetDelegateForFunctionPointer<PresentDelegate>(presentOriginalPtr);
        _resizeBuffersHook = ResizeBuffersHook;
        IntPtr resizeBuffersDetour = Marshal.GetFunctionPointerForDelegate(_resizeBuffersHook);
        MinHook.OK(MinHook.CreateHook(_resizeBuffersPtr, resizeBuffersDetour, out IntPtr resizeBuffersOriginalPtr), 
            "MH_CreateHook(ResizeBuffers)");
        MinHook.OK(MinHook.EnableHook(_resizeBuffersPtr), "MH_EnableHook(ResizeBuffers)");
        _resizeBuffersOriginal = Marshal.GetDelegateForFunctionPointer<ResizeBuffersDelegate>(resizeBuffersOriginalPtr);
        _hooksInitialized = true;
    }

    public override void Dispose()
    {
        if (_hooksInitialized)
        {
            MinHook.OK(MinHook.DisableHook(_presentPtr), "MH_DisableHook(Present)");
            MinHook.OK(MinHook.RemoveHook(_presentPtr), "MH_RemoveHook(Present)");
            MinHook.OK(MinHook.DisableHook(_resizeBuffersPtr), "MH_DisableHook(ResizeBuffers)");
            MinHook.OK(MinHook.RemoveHook(_resizeBuffersPtr), "MH_RemoveHook(ResizeBuffers)");
            MinHook.OK(MinHook.Uninitialize(), "MH_Uninitialize");
            _hooksInitialized = false;
        }
        _presentOriginal = null;
        _presentHook = null;
        _resizeBuffersOriginal = null;
        _resizeBuffersHook = null;
        Handler?.Dispose();
    }

    private int PresentHook(IntPtr self, uint syncInterval, uint flags)
    {
        using var swapChain = new SwapChain(self);
        ((ImGuiDX11Handler)Handler).OnPresent(swapChain, syncInterval, flags);
        return _presentOriginal(self, syncInterval, flags);
    }

    private int ResizeBuffersHook(IntPtr self, uint bufferCount, uint width, uint height, Format newFormat, uint swapChainFlags)
    {
        using var swapChain = new SwapChain(self);
        ((ImGuiDX11Handler)Handler).OnPreResizeBuffers(swapChain, bufferCount, width, height, newFormat, swapChainFlags);
        int result = _resizeBuffersOriginal(self, bufferCount, width, height, newFormat, swapChainFlags);
        ((ImGuiDX11Handler)Handler).OnPostResizeBuffers(swapChain, bufferCount, width, height, newFormat, swapChainFlags);
        return result;
    }
}