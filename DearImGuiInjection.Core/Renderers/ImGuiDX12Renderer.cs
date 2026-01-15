using DearImGuiInjection.Backends;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Renderers;

internal sealed class ImGuiDX12Renderer : ImGuiRenderer
{
    private class FrameContext
    {
        public unsafe ID3D12CommandAllocator* CommandAllocator;
        public unsafe ID3D12Resource* MainRenderTargetResource;
        public CpuDescriptorHandle MainRenderTargetDescriptor;
        public ulong FenceValue;
    };

    private class DescriptorHeapAllocator
    {
        public unsafe ID3D12DescriptorHeap* Heap;
        public DescriptorHeapType HeapType;
        public CpuDescriptorHandle HeapStartCpu;
        public GpuDescriptorHandle HeapStartGpu;
        public uint HeapHandleIncrement;
        public int[] FreeStack;
        public int FreeCount;

        public unsafe DescriptorHeapAllocator(ID3D12Device* device, ID3D12DescriptorHeap* heap)
        {
            Debug.Assert(Heap == null && FreeCount == 0);
            Heap = heap;
            DescriptorHeapDesc desc = Heap->GetDesc();
            HeapType = desc.Type;
            HeapStartCpu = Heap->GetCPUDescriptorHandleForHeapStart();
            HeapStartGpu = Heap->GetGPUDescriptorHandleForHeapStart();
            HeapHandleIncrement = device->GetDescriptorHandleIncrementSize(HeapType);
            int count = (int)desc.NumDescriptors;
            if (FreeStack == null || FreeStack.Length != count)
                FreeStack = new int[count];
            FreeCount = count;
            for (int i = 0; i < count; i++)
                FreeStack[i] = count - 1 - i;
        }

        public unsafe void Dispose()
        {
            if (Heap != null)
            {
                Heap->Release();
                Heap = null;
            }
            FreeStack = null;
            FreeCount = 0;
        }

        public unsafe void Alloc(CpuDescriptorHandle* out_cpu_desc_handle, GpuDescriptorHandle* out_gpu_desc_handle)
        {
            Debug.Assert(FreeCount > 0);
            int idx = FreeStack[--FreeCount];
            out_cpu_desc_handle->Ptr = HeapStartCpu.Ptr + (uint)(idx * HeapHandleIncrement);
            out_gpu_desc_handle->Ptr = HeapStartGpu.Ptr + (uint)(idx * HeapHandleIncrement);
        }

        public void Free(CpuDescriptorHandle out_cpu_desc_handle, GpuDescriptorHandle out_gpu_desc_handle)
        {
            int cpu_idx = (int)((out_cpu_desc_handle.Ptr - HeapStartCpu.Ptr) / HeapHandleIncrement);
            int gpu_idx = (int)((out_gpu_desc_handle.Ptr - HeapStartGpu.Ptr) / HeapHandleIncrement);
            Debug.Assert(cpu_idx == gpu_idx);
            FreeStack[FreeCount++] = cpu_idx;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate int Present1Delegate(IDXGISwapChain3* swapChain, uint syncInterval, uint presentFlags, PresentParameters* presentParameters);
    private IntPtr _present1Target;
    private Present1Delegate _present1Detour;
    private Present1Delegate _present1Original;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate int ResizeBuffersDelegate(IDXGISwapChain3* swapChain, uint bufferCount, uint width, uint height, Format newFormat, uint swapChainFlags);
    private IntPtr _resizeBuffersTarget;
    private ResizeBuffersDelegate _resizeBuffersDetour;
    private ResizeBuffersDelegate _resizeBuffersOriginal;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate void ExecuteCommandListsDelegate(ID3D12CommandQueue* commandQueue, uint numCommandLists, ID3D12CommandList** ppCommandLists);
    private IntPtr _executeCommandListsTarget;
    private ExecuteCommandListsDelegate _executeCommandListsDetour;
    private ExecuteCommandListsDelegate _executeCommandListsOriginal;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void SrvAllocDelegate(ImGuiImplDX12.InitInfo* info, CpuDescriptorHandle* out_cpu_desc_handle, GpuDescriptorHandle* out_gpu_desc_handle);
    private SrvAllocDelegate _srvAlloc;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void SrvFreeDelegate(ImGuiImplDX12.InitInfo* info, CpuDescriptorHandle cpu_desc_handle, GpuDescriptorHandle gpu_desc_handle);
    private SrvFreeDelegate _srvFree;

    private ImGuiImplDX12.InitInfo g_initInfo;
    private FrameContext[] g_frameContext;

    private unsafe ID3D12Device* g_pd3dDevice;
    private unsafe ID3D12DescriptorHeap* g_pd3dRtvDescHeap;
    private unsafe ID3D12DescriptorHeap* g_pd3dSrvDescHeap;
    private DescriptorHeapAllocator g_pd3dSrvDescHeapAlloc;
    private unsafe ID3D12CommandQueue* g_pd3dCommandQueue;
    private unsafe ID3D12GraphicsCommandList* g_pd3dCommandList;
    private unsafe ID3D12Fence* g_fence;
    private IntPtr g_fenceEvent;
    private ulong g_fenceLastSignaledValue;

    public override RendererKind Kind => RendererKind.DX12;

    public unsafe override void Init()
    {
        IntPtr windowHandle = User32.CreateFakeWindow();
        SwapChainDesc1 sd = new SwapChainDesc1
        {
            BufferCount = 2,
            Width = 0,
            Height = 0,
            Format = Format.FormatR8G8B8A8Unorm,
            BufferUsage = DXGI.UsageRenderTargetOutput,
            SampleDesc = new SampleDesc(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Unspecified,
            Scaling = Scaling.Stretch,
            Stereo = false
        };
        Guid riid = ID3D12Device.Guid;
        ID3D12Device* pd3dDevice;
        int res = D3D12.GetApi().CreateDevice(null, D3DFeatureLevel.Level110, &riid, (void**)&pd3dDevice);
        if (res != 0)
            throw new InvalidOperationException($"CreateDevice failed: 0x{res:X8}");
        riid = ID3D12CommandQueue.Guid;
        ID3D12CommandQueue* pd3dCommandQueue;
        {
            CommandQueueDesc desc = new CommandQueueDesc()
            {
                Type = CommandListType.Direct,
                Flags = CommandQueueFlags.None,
                NodeMask = 1
            };
            res = pd3dDevice->CreateCommandQueue(&desc, &riid, (void**)&pd3dCommandQueue);
            if (res != 0)
            {
                pd3dDevice->Release();
                throw new InvalidOperationException($"CreateCommandQueue failed: 0x{res:X8}");
            }
        }
        riid = IDXGIFactory5.Guid;
        IDXGIFactory5* dxgiFactory;
        res = DXGI.GetApi(null).CreateDXGIFactory1(&riid, (void**)&dxgiFactory);
        if (res != 0)
        {
            pd3dCommandQueue->Release();
            pd3dDevice->Release();
            throw new InvalidOperationException($"CreateDXGIFactory1 failed: 0x{res:X8}");
        }
        riid = IDXGISwapChain1.Guid;
        IDXGISwapChain1* swapChain1;
        res = dxgiFactory->CreateSwapChainForHwnd((IUnknown*)pd3dCommandQueue, windowHandle, &sd, null, null, &swapChain1);
        if (res != 0)
        {
            dxgiFactory->Release();
            pd3dCommandQueue->Release();
            pd3dDevice->Release();
            throw new InvalidOperationException($"CreateSwapChainForHwnd failed: 0x{res:X8}");
        }
        riid = IDXGISwapChain3.Guid;
        IDXGISwapChain3* pSwapChain;
        res = swapChain1->QueryInterface(&riid, (void**)&pSwapChain);
        if (res != 0)
        {
            swapChain1->Release();
            dxgiFactory->Release();
            pd3dCommandQueue->Release();
            pd3dDevice->Release();
            throw new InvalidOperationException($"QueryInterface failed: 0x{res:X8}");
        }
        nint* swapChainVTable = (nint*)pSwapChain->LpVtbl;
        IntPtr present1Target = swapChainVTable[22];
        IntPtr resizeBuffersTarget = swapChainVTable[13];
        nint* commandQueueVTable = (nint*)pd3dCommandQueue->LpVtbl;
        IntPtr executeCommandListsTarget = commandQueueVTable[10];
        pSwapChain->Release();
        swapChain1->Release();
        dxgiFactory->Release();
        pd3dCommandQueue->Release();
        pd3dDevice->Release();
        User32.DestroyWindow(windowHandle);
        MinHook.Ok(MinHook.Initialize(), "MH_Initialize");
        _present1Detour = Present1Hook;
        IntPtr present1DetourPtr = Marshal.GetFunctionPointerForDelegate(_present1Detour);
        MinHook.Ok(MinHook.CreateHook(present1Target, present1DetourPtr, out IntPtr present1Original), "MH_CreateHook(Present1)");
        MinHook.Ok(MinHook.EnableHook(present1Target), "MH_EnableHook(Present1)");
        _present1Original = Marshal.GetDelegateForFunctionPointer<Present1Delegate>(present1Original);
        _present1Target = present1Target;
        _resizeBuffersDetour = ResizeBuffersHook;
        IntPtr resizeBuffersDetourPtr = Marshal.GetFunctionPointerForDelegate(_resizeBuffersDetour);
        MinHook.Ok(MinHook.CreateHook(resizeBuffersTarget, resizeBuffersDetourPtr, out IntPtr resizeBuffersOriginal), "MH_CreateHook(ResizeBuffers)");
        MinHook.Ok(MinHook.EnableHook(resizeBuffersTarget), "MH_EnableHook(ResizeBuffers)");
        _resizeBuffersOriginal = Marshal.GetDelegateForFunctionPointer<ResizeBuffersDelegate>(resizeBuffersOriginal);
        _resizeBuffersTarget = resizeBuffersTarget;
        _executeCommandListsDetour = ExecuteCommandListsHook;
        IntPtr executeDetourPtr = Marshal.GetFunctionPointerForDelegate(_executeCommandListsDetour);
        MinHook.Ok(MinHook.CreateHook(executeCommandListsTarget, executeDetourPtr, out IntPtr executeOriginal), "MH_CreateHook(ExecuteCommandLists)");
        MinHook.Ok(MinHook.EnableHook(executeCommandListsTarget), "MH_EnableHook(ExecuteCommandLists)");
        _executeCommandListsOriginal = Marshal.GetDelegateForFunctionPointer<ExecuteCommandListsDelegate>(executeOriginal);
        _executeCommandListsTarget = executeCommandListsTarget;
    }

    public unsafe override void Dispose()
    {
        if (_executeCommandListsTarget != IntPtr.Zero)
        {
            MinHook.DisableHook(_executeCommandListsTarget);
            MinHook.RemoveHook(_executeCommandListsTarget);
        }
        if (_resizeBuffersTarget != IntPtr.Zero)
        {
            MinHook.DisableHook(_resizeBuffersTarget);
            MinHook.RemoveHook(_resizeBuffersTarget);
        }
        if (_present1Target != IntPtr.Zero)
        {
            MinHook.DisableHook(_present1Target);
            MinHook.RemoveHook(_present1Target);
        }
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
            ImGuiImplDX12.Shutdown();
        ImGuiImplWin32.Shutdown();
        ImGui.DestroyPlatformWindows();
    }

    public override bool IsSupported()
    {
        bool isSupported = false;
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                string name = module?.ModuleName;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                name = name.ToLowerInvariant();
                if (name.Contains("d3d12"))
                    isSupported = true;
            }
        }
        catch
        {
            return false;
        }
        return isSupported;
    }

    private unsafe int Present1Hook(IDXGISwapChain3* g_pSwapChain, uint syncInterval, uint presentFlags, PresentParameters* presentParameters)
    {
        if (g_pd3dCommandQueue == null)
            return _present1Original(g_pSwapChain, syncInterval, presentFlags, presentParameters);
        if (!IsInitialized)
        {
            SwapChainDesc sd;
            g_pSwapChain->GetDesc(&sd);
            uint bufferCount = sd.BufferCount;
            IntPtr windowHandle = sd.OutputWindow;
            Guid riid = IDXGISwapChain1.Guid;
            IDXGISwapChain1* swapChain1;
            g_pSwapChain->QueryInterface(&riid, (void**)&swapChain1);
            riid = ID3D12Device.Guid;
            ID3D12Device* pd3dDevice;
            swapChain1->GetDevice(&riid, (void**)&pd3dDevice);
            swapChain1->Release();
            g_pd3dDevice = pd3dDevice;
            g_frameContext = new FrameContext[bufferCount];
            for (int i = 0; i < bufferCount; i++)
                g_frameContext[i] = new();
            {
                DescriptorHeapDesc desc = new DescriptorHeapDesc
                {
                    Type = DescriptorHeapType.Rtv,
                    NumDescriptors = bufferCount,
                    Flags = DescriptorHeapFlags.None,
                    NodeMask = 0
                };
                riid = ID3D12DescriptorHeap.Guid;
                ID3D12DescriptorHeap* pd3dRtvDescHeap;
                g_pd3dDevice->CreateDescriptorHeap(&desc, &riid, (void**)&pd3dRtvDescHeap);
                g_pd3dRtvDescHeap = pd3dRtvDescHeap;
                uint rtvDescriptorSize = g_pd3dDevice->GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
                CpuDescriptorHandle rtvHandle = g_pd3dRtvDescHeap->GetCPUDescriptorHandleForHeapStart();
                for (int i = 0; i < bufferCount; i++)
                {
                    g_frameContext[i].MainRenderTargetDescriptor = rtvHandle;
                    rtvHandle.Ptr += rtvDescriptorSize;
                }
            }
            {
                DescriptorHeapDesc desc = new DescriptorHeapDesc
                {
                    Type = DescriptorHeapType.CbvSrvUav,
                    NumDescriptors = bufferCount,
                    Flags = DescriptorHeapFlags.ShaderVisible,
                    NodeMask = 0
                };
                riid = ID3D12DescriptorHeap.Guid;
                ID3D12DescriptorHeap* pd3dSrvDescHeap;
                g_pd3dDevice->CreateDescriptorHeap(&desc, &riid, (void**)&pd3dSrvDescHeap);
                g_pd3dSrvDescHeap = pd3dSrvDescHeap;
                g_pd3dSrvDescHeapAlloc = new(g_pd3dDevice, g_pd3dSrvDescHeap);
            }
            riid = ID3D12CommandAllocator.Guid;
            for (int i = 0; i < bufferCount; i++)
            {
                ID3D12CommandAllocator* commandAllocator;
                g_pd3dDevice->CreateCommandAllocator(CommandListType.Direct, &riid, (void**)&commandAllocator);
                g_frameContext[i].CommandAllocator = commandAllocator;
                g_frameContext[i].FenceValue = 0;
            }
            riid = ID3D12GraphicsCommandList.Guid;
            ID3D12GraphicsCommandList* pd3dCommandList;
            g_pd3dDevice->CreateCommandList(0, CommandListType.Direct, g_frameContext[0].CommandAllocator, null, &riid, (void**)&pd3dCommandList);
            pd3dCommandList->Close();
            g_pd3dCommandList = pd3dCommandList;
            riid = ID3D12Fence.Guid;
            ID3D12Fence* fence;
            g_pd3dDevice->CreateFence(0, FenceFlags.None, &riid, (void**)&fence);
            g_fence = fence;
            g_fenceEvent = Kernel32.CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
            _srvAlloc = (ImGuiImplDX12.InitInfo* info, CpuDescriptorHandle* out_cpu_desc_handle, GpuDescriptorHandle* out_gpu_desc_handle) =>
            {
                g_pd3dSrvDescHeapAlloc.Alloc(out_cpu_desc_handle, out_gpu_desc_handle);
            };
            _srvFree = (ImGuiImplDX12.InitInfo* info, CpuDescriptorHandle cpu_desc_handle, GpuDescriptorHandle gpu_desc_handle) =>
            {
                g_pd3dSrvDescHeapAlloc.Free(cpu_desc_handle, gpu_desc_handle);
            };
            g_initInfo = new ImGuiImplDX12.InitInfo
            {
                Device = g_pd3dDevice,
                CommandQueue = g_pd3dCommandQueue,
                NumFramesInFlight = (int)bufferCount,
                RTVFormat = Format.FormatR8G8B8A8Unorm,
                DSVFormat = Format.FormatUnknown,
                SrvDescriptorHeap = g_pd3dSrvDescHeap,
                SrvDescriptorAllocFn = (delegate* unmanaged[Cdecl]<ImGuiImplDX12.InitInfo*, CpuDescriptorHandle*, GpuDescriptorHandle*, void>)Marshal.GetFunctionPointerForDelegate(_srvAlloc),
                SrvDescriptorFreeFn = (delegate* unmanaged[Cdecl]<ImGuiImplDX12.InitInfo*, CpuDescriptorHandle, GpuDescriptorHandle, void>)Marshal.GetFunctionPointerForDelegate(_srvFree),
            };
            AttachToWindow(windowHandle);
            CreateRenderTarget(g_pSwapChain);
        }
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
                ImGuiImplDX12.InitInfo initInfo = g_initInfo;
                ImGuiImplDX12.Init(&initInfo);
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
            ImGuiImplDX12.NewFrame();
            ImGui.NewFrame();
            DearImGuiInjectionCore.MultiContextCompositor.PostNewFrameUpdateOne(module);
            try
            {
                module.OnRender();
                ImGui.Render();
                uint backBufferIdx = g_pSwapChain->GetCurrentBackBufferIndex();
                FrameContext frameCtx = WaitForNextFrameContext(backBufferIdx);
                frameCtx.CommandAllocator->Reset();
                ResourceBarrier barrier = new ResourceBarrier
                {
                    Type = ResourceBarrierType.Transition,
                    Flags = ResourceBarrierFlags.None,
                    Transition = new ResourceTransitionBarrier
                    {
                        PResource = frameCtx.MainRenderTargetResource,
                        StateBefore = ResourceStates.Present,
                        StateAfter = ResourceStates.RenderTarget
                    }
                };
                g_pd3dCommandList->Reset(frameCtx.CommandAllocator, null);
                g_pd3dCommandList->ResourceBarrier(1, &barrier);
                g_pd3dCommandList->OMSetRenderTargets(1, ref frameCtx.MainRenderTargetDescriptor, false, null);
                g_pd3dCommandList->SetDescriptorHeaps(1, ref g_pd3dSrvDescHeap);
                ImGuiImplDX12.RenderDrawData(ImGui.GetDrawData().Handle, g_pd3dCommandList);
                barrier.Transition = new ResourceTransitionBarrier
                {
                    PResource = frameCtx.MainRenderTargetResource,
                    StateBefore = ResourceStates.RenderTarget,
                    StateAfter = ResourceStates.Present
                };
                g_pd3dCommandList->ResourceBarrier(1, &barrier);
                g_pd3dCommandList->Close();
                ID3D12CommandList* pd3dCommandList = (ID3D12CommandList*)g_pd3dCommandList;
                g_pd3dCommandQueue->ExecuteCommandLists(1, &pd3dCommandList);
                g_pd3dCommandQueue->Signal(g_fence, ++g_fenceLastSignaledValue);
                frameCtx.FenceValue = g_fenceLastSignaledValue;
            }
            catch (Exception e)
            {
                ImGui.EndFrame();
                DearImGuiInjectionCore.DestroyModule(module.Id);
                Log.Error($"Module \"{module.Id}\" OnRender threw an exception: {e}");
            }
        }
        DearImGuiInjectionCore.MultiContextCompositor.PostEndFrameUpdateAll();
        return _present1Original(g_pSwapChain, syncInterval, presentFlags, presentParameters);
    }

    private unsafe int ResizeBuffersHook(IDXGISwapChain3* g_pSwapChain, uint bufferCount, uint width, uint height,
        Format newFormat, uint swapChainFlags)
    {
        CleanupRenderTarget();
        int hr = _resizeBuffersOriginal(g_pSwapChain, bufferCount, width, height, newFormat, swapChainFlags);
        CreateRenderTarget(g_pSwapChain);
        return hr;
    }

    private unsafe void ExecuteCommandListsHook(ID3D12CommandQueue* commandQueue, uint numCommandLists,
        ID3D12CommandList** ppCommandLists)
    {
        if (commandQueue != g_pd3dCommandQueue && commandQueue->GetDesc().Type == CommandListType.Direct)
        {
            if (g_pd3dCommandQueue != null)
                g_pd3dCommandQueue->Release();
            g_pd3dCommandQueue = commandQueue;
            g_pd3dCommandQueue->AddRef();
        }
        _executeCommandListsOriginal(commandQueue, numCommandLists, ppCommandLists);
    }

    private unsafe void CreateRenderTarget(IDXGISwapChain3* g_pSwapChain)
    {
        if (g_pd3dDevice == null)
            return;
        for (uint i = 0; i < g_frameContext.Length; i++)
        {
            FrameContext frame_context = g_frameContext[i];
            Guid riid = ID3D12Resource.Guid;
            ID3D12Resource* pBackBuffer = null;
            g_pSwapChain->GetBuffer(i, &riid, (void**)&pBackBuffer);
            g_pd3dDevice->CreateRenderTargetView(pBackBuffer, null, frame_context.MainRenderTargetDescriptor);
            frame_context.MainRenderTargetResource = pBackBuffer;
        }
    }

    private unsafe void CleanupDeviceD3D()
    {
        CleanupRenderTarget();
        for (int i = 0; i < g_frameContext.Length; i++)
        {
            FrameContext frame_context = g_frameContext[i];
            if (frame_context.CommandAllocator != null)
            {
                frame_context.CommandAllocator->Release();
                frame_context.CommandAllocator = null;
            }
        }
        if (g_pd3dCommandQueue != null)
        {
            g_pd3dCommandQueue->Release();
            g_pd3dCommandQueue = null;
        }
        if (g_pd3dCommandList != null)
        {
            g_pd3dCommandList->Release();
            g_pd3dCommandList = null;
        }
        if (g_pd3dRtvDescHeap != null)
        {
            g_pd3dRtvDescHeap->Release();
            g_pd3dRtvDescHeap = null;
        }
        g_pd3dSrvDescHeapAlloc.Dispose();
        if (g_fence != null)
        {
            g_fence->Release();
            g_fence = null;
        }
        if (g_fenceEvent != IntPtr.Zero)
        {
            Kernel32.CloseHandle(g_fenceEvent);
            g_fenceEvent = IntPtr.Zero;
        }
        if (g_pd3dDevice != null)
        {
            g_pd3dDevice->Release();
            g_pd3dDevice = null;
        }
    }

    private unsafe void CleanupRenderTarget()
    {
        WaitForPendingOperations();
        for (int i = 0; i < g_frameContext.Length; i++)
        {
            FrameContext frame_context = g_frameContext[i];
            if (frame_context.MainRenderTargetResource != null)
            {
                frame_context.MainRenderTargetResource->Release();
                frame_context.MainRenderTargetResource = null;
            }
        }
    }

    private unsafe void WaitForPendingOperations()
    {
        g_pd3dCommandQueue->Signal(g_fence, ++g_fenceLastSignaledValue);
        g_fence->SetEventOnCompletion(g_fenceLastSignaledValue, (void*)g_fenceEvent);
        Kernel32.WaitForSingleObject(g_fenceEvent, uint.MaxValue);
    }

    private unsafe FrameContext WaitForNextFrameContext(uint backBufferIdx)
    {
        FrameContext frame_context = g_frameContext[backBufferIdx];
        if (g_fence->GetCompletedValue() < frame_context.FenceValue)
        {
            g_fence->SetEventOnCompletion(frame_context.FenceValue, (void*)g_fenceEvent);
            Kernel32.WaitForSingleObject(g_fenceEvent, uint.MaxValue);
        }
        return frame_context;
    }
}
