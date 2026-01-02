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
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PresentDelegate(IntPtr self, uint syncInterval, uint flags);
    private IntPtr _presentTarget;
    private PresentDelegate _presentDetour;
    private PresentDelegate _presentOriginal;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResizeBuffersDelegate(IntPtr self, uint bufferCount, uint width, uint height, Format newFormat, uint swapChainFlags);
    private IntPtr _resizeBuffersTarget;
    private ResizeBuffersDelegate _resizeBuffersDetour;
    private ResizeBuffersDelegate _resizeBuffersOriginal;

    private bool _hooksCreated;

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
        nint* vTable = *(nint**)swapChain.NativePointer;
        _presentTarget = vTable[8];
        _resizeBuffersTarget = vTable[13];
        device.Dispose();
        swapChain.Dispose();
        User32.DestroyWindow(windowHandle);
        Handler = new ImGuiDX11Handler();
        MinHook.OK(MinHook.Initialize(), "MH_Initialize");
        _presentDetour = PresentHook;
        IntPtr presentDetourPtr = Marshal.GetFunctionPointerForDelegate(_presentDetour);
        MinHook.OK(MinHook.CreateHook(_presentTarget, presentDetourPtr, out IntPtr presentOriginalPtr),
            "MH_CreateHook(Present)");
        MinHook.OK(MinHook.EnableHook(_presentTarget), "MH_EnableHook(Present)");
        _presentOriginal = Marshal.GetDelegateForFunctionPointer<PresentDelegate>(presentOriginalPtr);
        _resizeBuffersDetour = ResizeBuffersHook;
        IntPtr resizeBuffersDetourPtr = Marshal.GetFunctionPointerForDelegate(_resizeBuffersDetour);
        MinHook.OK(MinHook.CreateHook(_resizeBuffersTarget, resizeBuffersDetourPtr, out IntPtr resizeBuffersOriginalPtr),
            "MH_CreateHook(ResizeBuffers)");
        MinHook.OK(MinHook.EnableHook(_resizeBuffersTarget), "MH_EnableHook(ResizeBuffers)");
        _resizeBuffersOriginal = Marshal.GetDelegateForFunctionPointer<ResizeBuffersDelegate>(resizeBuffersOriginalPtr);
    }

    public override void Dispose()
    {
        if (_hooksCreated)
        {
            if (_resizeBuffersTarget != IntPtr.Zero)
            {
                MinHook.DisableHook(_resizeBuffersTarget);
                MinHook.RemoveHook(_resizeBuffersTarget);
            }
            if (_presentTarget != IntPtr.Zero)
            {
                MinHook.DisableHook(_presentTarget);
                MinHook.RemoveHook(_presentTarget);
            }
            _hooksCreated = false;
        }
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