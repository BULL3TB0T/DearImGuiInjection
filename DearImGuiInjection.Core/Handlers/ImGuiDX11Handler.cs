using DearImGuiInjection.Backends;
using DearImGuiInjection.Renderers;
using DearImGuiInjection.Textures;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Handlers;

internal sealed unsafe class ImGuiDX11Handler : ImGuiHandler
{
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _deviceContext;
    private ID3D11RenderTargetView* _renderTargetView;

    public override void OnShutdown(bool isInitialized)
    {
        if (isInitialized)
            ImGuiImplDX11.Shutdown();
        ImGuiImplWin32.Shutdown();
        ImGui.DestroyPlatformWindows();
    }

    public override void OnDispose()
    {
        for (int i = DearImGuiInjectionCore.MultiContextCompositor.ModulesFrontToBack.Count - 1; i >= 0; i--)
        {
            ImGuiModule module = DearImGuiInjectionCore.MultiContextCompositor.ModulesFrontToBack[i];
            ImGui.SetCurrentContext(module.Context);
            OnShutdown(module.IsInitialized);
        }
        if (_renderTargetView != null)
            _renderTargetView->Release();
        if (_deviceContext != null)
            _deviceContext->Release();
        if (_device != null)
            _device->Release();
    }

    internal void OnPresent(IntPtr self, uint syncInterval, uint flags)
    {
        IDXGISwapChain* swapChain = (IDXGISwapChain*)self;
        if (!IsInitialized)
        {
            void* device = null;
            Guid riid = ID3D11Device.Guid;
            swapChain->GetDevice(&riid, &device);
            _device = (ID3D11Device*)device;
            _device->GetImmediateContext(ref _deviceContext);
            SwapChainDesc desc;
            swapChain->GetDesc(&desc);
            Init(desc.OutputWindow);
            DearImGuiInjectionCore.TextureManager = new DX11TextureManager(_device);
            IsInitialized = true;
        }
        if (_renderTargetView == null)
        {
            void* backBuffer = null;
            Guid riid = ID3D11Texture2D.Guid;
            swapChain->GetBuffer(0, &riid, &backBuffer);
            ID3D11Texture2D* texture = (ID3D11Texture2D*)backBuffer;
            _device->CreateRenderTargetView((ID3D11Resource*)texture, null, ref _renderTargetView);
            texture->Release();
        }
        _deviceContext->OMSetRenderTargets(1, ref _renderTargetView, null);
        DearImGuiInjectionCore.TextureManager.Update();
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
                    Log.Error($"Module \"{module.Id}\" ImGuiImplWin32.Init() failed. Destroying module.");
                    continue;
                }
                ImGuiImplDX11.Init(_device, _deviceContext);
                module.IsInitialized = true;
                try
                {
                    module.OnInit?.Invoke();
                }
                catch (Exception e)
                {
                    Log.Error($"Module \"{module.Id}\" OnInit threw an exception. Destroying module: {e}");
                    DearImGuiInjectionCore.DestroyModule(module.Id);
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
                ImGuiImplDX11.RenderDrawData(ImGui.GetDrawData().Handle);
            }
            catch (Exception e)
            {
                ImGui.EndFrame();
                Log.Error($"Module \"{module.Id}\" OnRender threw an exception. Destroying module: {e}");
                DearImGuiInjectionCore.DestroyModule(module.Id);
            }
        }
        DearImGuiInjectionCore.MultiContextCompositor.PostEndFrameUpdateAll();
    }

    internal void OnResizeBuffers(IntPtr self, uint bufferCount, uint width, uint height, Format newFormat,
        uint swapChainFlags)
    {
        if (!IsInitialized)
            return;
        if (_renderTargetView != null)
        {
            _renderTargetView->Release();
            _renderTargetView = null;
        }
        for (int i = DearImGuiInjectionCore.MultiContextCompositor.ModulesFrontToBack.Count - 1; i >= 0; i--)
        {
            ImGuiModule module = DearImGuiInjectionCore.MultiContextCompositor.ModulesFrontToBack[i];
            ImGui.SetCurrentContext(module.Context);
            ImGuiImplDX11.InvalidateDeviceObjects();
        }
    }
}