using DearImGuiInjection.Backends;
using DearImGuiInjection.Textures;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Renderers;

internal sealed class ImGuiDX11Renderer : ImGuiRenderer
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate int PresentDelegate(IDXGISwapChain* swapChain, uint syncInterval, uint presentFlags);
    private MinHookDetour<PresentDelegate> _present;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate int ResizeBuffersDelegate(IDXGISwapChain* swapChain, uint bufferCount, uint width, uint height, Format newFormat, uint swapChainFlags);
    private MinHookDetour<ResizeBuffersDelegate> _resizeBuffers;

    private unsafe ID3D11Device* g_pd3dDevice;
    private unsafe ID3D11DeviceContext* g_pd3dDeviceContext;
    private unsafe ID3D11RenderTargetView* g_mainRenderTargetView;

    public unsafe override void Init()
    {
        IntPtr windowHandle = User32.CreateFakeWindow();
        SwapChainDesc sd = new SwapChainDesc
        {
            BufferDesc = new ModeDesc
            {
                Width = 0,
                Height = 0,
                RefreshRate = new Rational(0, 0),
                Format = Format.FormatR8G8B8A8Unorm
            },
            BufferUsage = DXGI.UsageRenderTargetOutput,
            OutputWindow = windowHandle,
            BufferCount = 1,
            SampleDesc = new SampleDesc(1, 0),
            Windowed = true,
            SwapEffect = SwapEffect.Discard
        };
        uint createDeviceFlags = 0;
        IDXGISwapChain* swapChain = null;
        ID3D11Device* device = null;
        const int FeatureLevels = 2;
        D3DFeatureLevel* featureLevelArray = stackalloc D3DFeatureLevel[2]
        {
            D3DFeatureLevel.Level110,
            D3DFeatureLevel.Level100,
        };
        D3DFeatureLevel* featureLevel = null;
        ID3D11DeviceContext* deviceContext = null;
        D3D11 API = D3D11.GetApi(null);
        int res = API.CreateDeviceAndSwapChain(null, D3DDriverType.Hardware, 0, createDeviceFlags, featureLevelArray,
            FeatureLevels, D3D11.SdkVersion, &sd, &swapChain, &device, featureLevel, &deviceContext);
        if (res == unchecked((int)0x887A0004)) // DXGI_ERROR_UNSUPPORTED
            res = API.CreateDeviceAndSwapChain(null, D3DDriverType.Warp, 0, createDeviceFlags, featureLevelArray,
                FeatureLevels, D3D11.SdkVersion, &sd, &swapChain, &device, featureLevel, &deviceContext);
        if (res != 0)
            throw new InvalidOperationException($"CreateDeviceAndSwapChain failed: 0x{res:X8}");
        MinHook.Ok(MinHook.Initialize(), "MH_Initialize");
        nint* vTable = (nint*)swapChain->LpVtbl;
        _present = new("Present");
        _present.Create(vTable[8], PresentDetour);
        _present.Enable();
        _resizeBuffers = new("ResizeBuffers");
        _resizeBuffers.Create(vTable[13], ResizeBuffersDetour);
        _resizeBuffers.Enable();
        deviceContext->Release();
        device->Release();
        swapChain->Release();
        User32.DestroyWindow(windowHandle);
    }

    public unsafe override void Dispose()
    {
        _resizeBuffers.Dispose();
        _present.Dispose();
        MinHook.Ok(MinHook.Uninitialize(), "MH_Uninitialize");
        foreach (ImGuiModule module in DearImGuiInjectionCore.MultiContextCompositor.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            Shutdown(module.IsInitialized);
        }
        CleanupDeviceD3D();
    }

    public override void Shutdown(bool isInitialized)
    {
        if (isInitialized)
            ImGuiImplDX11.Shutdown();
        ImGuiImplWin32.Shutdown();
        ImGui.DestroyPlatformWindows();
    }

    private unsafe int PresentDetour(IDXGISwapChain* g_pSwapChain, uint syncInterval, uint presentFlags)
    {
        if (!IsInitialized)
        {
            Guid riid = ID3D11Device.Guid;
            ID3D11Device* device = null;
            g_pSwapChain->GetDevice(&riid, (void**)&device);
            g_pd3dDevice = device;
            g_pd3dDevice->GetImmediateContext(ref g_pd3dDeviceContext);
            SwapChainDesc sd;
            g_pSwapChain->GetDesc(&sd);
            AttachToWindow(sd.OutputWindow);
            //DearImGuiInjectionCore.TextureManager = new DX11TextureManager(g_pd3dDevice);
            CreateRenderTarget(g_pSwapChain);
        }
        //DearImGuiInjectionCore.TextureManager.Update();
        DearImGuiInjectionCore.MultiContextCompositor.PreNewFrameUpdateAll();
        for (int i = DearImGuiInjectionCore.MultiContextCompositor.ModulesFrontToBack.Count - 1; i >= 0; i--)
        {
            ImGuiModule module = DearImGuiInjectionCore.MultiContextCompositor.ModulesFrontToBack[i];
            ImGui.SetCurrentContext(module.Context);
            if (!module.IsInitialized)
            {
                if (!ImGuiImplWin32.Init(WindowHandle))
                {
                    DearImGuiInjectionCore.DestroyModule(module.Id);
                    Log.Error($"Module \"{module.Id}\" ImGuiImplWin32.Init failed. Destroying module.");
                    continue;
                }
                ImGuiImplDX11.Init(g_pd3dDevice, g_pd3dDeviceContext);
                module.IsInitialized = true;
                try
                {
                    module.OnInit?.Invoke();
                }
                catch (Exception e)
                {
                    DearImGuiInjectionCore.DestroyModule(module.Id);
                    Log.Error($"Module \"{module.Id}\" OnInit threw an exception: {e}");
                    continue;
                }
            }
            ImGuiImplWin32.NewFrame();
            ImGuiImplDX11.NewFrame();
            ImGui.NewFrame();
            DearImGuiInjectionCore.MultiContextCompositor.PostNewFrameUpdateOne(module);
            try
            {
                module.OnRender();
                ImGui.Render();
                g_pd3dDeviceContext->OMSetRenderTargets(1, ref g_mainRenderTargetView, null);
                ImGuiImplDX11.RenderDrawData(ImGui.GetDrawData().Handle);
            }
            catch (Exception e)
            {
                ImGui.EndFrame();
                DearImGuiInjectionCore.DestroyModule(module.Id);
                Log.Error($"Module \"{module.Id}\" OnRender threw an exception: {e}");
            }
        }
        DearImGuiInjectionCore.MultiContextCompositor.PostEndFrameUpdateAll();
        return _present.Original(g_pSwapChain, syncInterval, presentFlags);
    }

    private unsafe int ResizeBuffersDetour(IDXGISwapChain* g_pSwapChain, uint bufferCount, uint width, uint height,
        Format newFormat, uint swapChainFlags)
    {
        CleanupRenderTarget();
        int hr = _resizeBuffers.Original(g_pSwapChain, bufferCount, width, height, newFormat, swapChainFlags);
        CreateRenderTarget(g_pSwapChain);
        return hr;
    }

    private unsafe void CleanupDeviceD3D()
    {
        CleanupRenderTarget();
        if (g_pd3dDeviceContext != null)
        {
            g_pd3dDeviceContext->Release();
            g_pd3dDeviceContext = null;
        }
        if (g_pd3dDevice != null)
        {
            g_pd3dDevice->Release();
            g_pd3dDevice = null;
        }
    }

    private unsafe void CreateRenderTarget(IDXGISwapChain* g_pSwapChain)
    {
        if (g_pd3dDevice == null)
            return;
        Guid riid = ID3D11Texture2D.Guid;
        ID3D11Texture2D* pBackBuffer = null;
        g_pSwapChain->GetBuffer(0, &riid, (void**)&pBackBuffer);
        g_pd3dDevice->CreateRenderTargetView((ID3D11Resource*)pBackBuffer, null, ref g_mainRenderTargetView);
        pBackBuffer->Release();
    }

    private unsafe void CleanupRenderTarget()
    {
        if (g_mainRenderTargetView != null)
        {
            g_mainRenderTargetView->Release();
            g_mainRenderTargetView = null;
        }
    }
}