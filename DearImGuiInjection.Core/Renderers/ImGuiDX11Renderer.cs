using DearImGuiInjection.Handlers;
using DearImGuiInjection.Windows;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        return hasD3D11 && !hasD3D12;
    }

    public override unsafe void Init()
    {
        var windowHandle = User32.CreateFakeWindow();
        SwapChainDesc desc = new()
        {
            BufferDesc = new ModeDesc
            {
                Width = 0,
                Height = 0,
                RefreshRate = new Rational(0, 0),
                Format = Format.FormatR8G8B8A8Unorm
            },
            SampleDesc = new SampleDesc(1),
            BufferUsage = DXGI.UsageRenderTargetOutput,
            BufferCount = 1,
            OutputWindow = windowHandle,
            Windowed = true,
            SwapEffect = SwapEffect.Discard
        };
        ID3D11Device* device = null;
        ID3D11DeviceContext* deviceContext = null;
        IDXGISwapChain* swapChain = null;
        int hr = D3D11.GetApi(null).CreateDeviceAndSwapChain(
            pAdapter: null,
            DriverType: D3DDriverType.Hardware,
            Software: 0,
            Flags: 0,
            pFeatureLevels: null,
            FeatureLevels: 0,
            SDKVersion: D3D11.SdkVersion,
            pSwapChainDesc: &desc,
            ppSwapChain: &swapChain,
            ppDevice: &device,
            pFeatureLevel: null,
            ppImmediateContext: &deviceContext);
        if (hr < 0)
        {
            if (deviceContext != null)
                deviceContext->Release();
            if (device != null)
                device->Release();
            if (swapChain != null)
                swapChain->Release();
            throw new InvalidOperationException($"CreateDeviceAndSwapChain failed: 0x{hr:X8}");
        }
        nint* vTable = (nint*)swapChain->LpVtbl;
        IntPtr presentTarget = vTable[8];
        IntPtr resizeBuffersTarget = vTable[13];
        deviceContext->Release();
        device->Release();
        swapChain->Release();
        User32.DestroyWindow(windowHandle);
        Handler = new ImGuiDX11Handler();
        MinHook.OK(MinHook.Initialize(), "MH_Initialize");
        _presentDetour = PresentHook;
        IntPtr presentDetourPtr = Marshal.GetFunctionPointerForDelegate(_presentDetour);
        MinHook.OK(MinHook.CreateHook(presentTarget, presentDetourPtr, out IntPtr presentOriginal),
            "MH_CreateHook(Present)");
        MinHook.OK(MinHook.EnableHook(presentTarget), "MH_EnableHook(Present)");
        _presentOriginal = Marshal.GetDelegateForFunctionPointer<PresentDelegate>(presentOriginal);
        _presentTarget = presentTarget;
        _resizeBuffersDetour = ResizeBuffersHook;
        IntPtr resizeBuffersDetourPtr = Marshal.GetFunctionPointerForDelegate(_resizeBuffersDetour);
        MinHook.OK(MinHook.CreateHook(resizeBuffersTarget, resizeBuffersDetourPtr, out IntPtr resizeBuffersOriginal),
            "MH_CreateHook(ResizeBuffers)");
        MinHook.OK(MinHook.EnableHook(resizeBuffersTarget), "MH_EnableHook(ResizeBuffers)");
        _resizeBuffersOriginal = Marshal.GetDelegateForFunctionPointer<ResizeBuffersDelegate>(resizeBuffersOriginal);
        _resizeBuffersTarget = resizeBuffersTarget;
    }

    public override void Dispose()
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
        Handler?.Dispose();
    }

    private int PresentHook(IntPtr self, uint syncInterval, uint flags)
    {
        ((ImGuiDX11Handler)Handler).OnPresent(self, syncInterval, flags);
        return _presentOriginal(self, syncInterval, flags);
    }

    private int ResizeBuffersHook(IntPtr self, uint bufferCount, uint width, uint height, Format newFormat, uint swapChainFlags)
    {
        ((ImGuiDX11Handler)Handler).OnResizeBuffers(self, bufferCount, width, height, newFormat, swapChainFlags);
        return _resizeBuffersOriginal(self, bufferCount, width, height, newFormat, swapChainFlags);
    }
}