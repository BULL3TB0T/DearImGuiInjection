using DearImGuiInjection.Backends;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Renderers;

internal sealed class ImGuiVulkanRenderer : ImGuiRenderer
{
    private struct Frame
    {
        public CommandPool CommandPool;
        public CommandBuffer CommandBuffer;
        public Fence Fence;
        public Image Backbuffer;
        public ImageView BackbufferView;
        public Framebuffer Framebuffer;
    }

    private struct FrameSemaphores
    {
        public Semaphore ImageAcquiredSemaphore;
        public Semaphore RenderCompleteSemaphore;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate Result AcquireNextImageKHRDelegate(Device device, SwapchainKHR swapchain, ulong timeout, Semaphore semaphore, Fence fence, uint* pImageIndex);
    private MinHookDetour<AcquireNextImageKHRDelegate> _acquireNextImageKHR;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate Result QueuePresentKHRDelegate(Queue queue, PresentInfoKHR* pPresentInfo);
    private MinHookDetour<QueuePresentKHRDelegate> _queuePresentKHR;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate Result CreateSwapchainKHRDelegate(Device device, SwapchainCreateInfoKHR* pCreateInfo, AllocationCallbacks* pAllocator, SwapchainKHR* pSwapchain);
    private MinHookDetour<CreateSwapchainKHRDelegate> _createSwapchainKHR;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void CheckVkResultDelegate(Result res);
    private CheckVkResultDelegate _checkVkResult;

    private ImGuiImplVulkan.InitInfo g_InitInfo;
    private unsafe AllocationCallbacks* g_Allocator;
    private Instance g_Instance;
    private KhrSwapchain g_KhrSwapchain;
    private PhysicalDevice g_PhysicalDevice;
    private Device g_Device;
    private uint g_QueueFamily;
    private ImVector<QueueFamilyProperties> g_QueueFamilies;
    private DescriptorPool g_DescriptorPool;
    private RenderPass g_RenderPass;
    private Frame[] g_Frames;
    private FrameSemaphores[] g_FrameSemaphores;
    private Extent2D g_ImageExtent;

    public unsafe override void Init()
    {
        CreateDevice();
        MinHook.Ok(MinHook.Initialize(), "MH_Initialize");
        _acquireNextImageKHR = new("vkAcquireNextImageKHR");
        _acquireNextImageKHR.Create(SharedAPI.Vulkan.CurrentVTable.Load(_acquireNextImageKHR.Name), AcquireNextImageKHRDetour);
        _acquireNextImageKHR.Enable();
        _queuePresentKHR = new("vkQueuePresentKHR");
        _queuePresentKHR.Create(SharedAPI.Vulkan.CurrentVTable.Load(_queuePresentKHR.Name), QueuePresentKHRDetour);
        _queuePresentKHR.Enable();
        _createSwapchainKHR = new("vkCreateSwapchainKHR");
        _createSwapchainKHR.Create(SharedAPI.Vulkan.CurrentVTable.Load(_createSwapchainKHR.Name), CreateSwapchainKHRDetour);
        _createSwapchainKHR.Enable();
    }

    public unsafe override void Dispose()
    {
        _createSwapchainKHR.Dispose();
        _queuePresentKHR.Dispose();
        _acquireNextImageKHR.Dispose();
        MinHook.Ok(MinHook.Uninitialize(), "MH_Uninitialize");
        foreach (ImGuiModule module in DearImGuiInjectionCore.MultiContextCompositor.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            Shutdown(module.IsInitialized);
        }
        Result err = SharedAPI.Vulkan.DeviceWaitIdle(g_Device);
        CheckVkResult(err);
        CleanupDevice();
    }

    public override void Shutdown(bool isInitialized)
    {
        if (isInitialized)
            ImGuiImplVulkan.Shutdown();
        ImGuiImplWin32.Shutdown();
    }

    private unsafe Result AcquireNextImageKHRDetour(Device device, SwapchainKHR swapchain, ulong timeout, Semaphore semaphore, Fence fence, uint* pImageIndex)
    {
        g_Device = device;
        return _acquireNextImageKHR.Original(device, swapchain, timeout, semaphore, fence, pImageIndex);
    }

    private unsafe Result QueuePresentKHRDetour(Queue queue, PresentInfoKHR* pPresentInfo)
    {
        FrameRender(queue, pPresentInfo);
        return _queuePresentKHR.Original(queue, pPresentInfo);
    }

    private unsafe Result CreateSwapchainKHRDetour(Device device, SwapchainCreateInfoKHR* pCreateInfo, AllocationCallbacks* pAllocator, SwapchainKHR* pSwapchain)
    {
        CleanupRenderTarget();
        g_ImageExtent = pCreateInfo->ImageExtent;
        return _createSwapchainKHR.Original(device, pCreateInfo, pAllocator, pSwapchain);
    }

    private unsafe void FrameRender(Queue queue, PresentInfoKHR* pPresentInfo)
    {
        if (g_Device.Handle == 0) 
            return;
        bool hasWindowHandle = WindowHandle != IntPtr.Zero;
        if (hasWindowHandle && g_ImageExtent.Width == 0 && g_ImageExtent.Height == 0)
        {
            User32.GetClientRect(WindowHandle, out RECT rect);
            g_ImageExtent.Width = (uint)rect.Width;
            g_ImageExtent.Height = (uint)rect.Height;
        }
        Queue graphicQueue = GetGraphicQueue();
        if (graphicQueue.Handle == 0)
            throw new InvalidOperationException("No queue that has QueueFlags.GraphicsBit has been found.");
        for (int i = 0; i < pPresentInfo->SwapchainCount; i++)
        {
            SwapchainKHR swapchain = pPresentInfo->PSwapchains[i];
            if (g_Frames == null && g_FrameSemaphores == null)
                CreateRenderTarget(g_Device, swapchain);
            int backBufferIndex = (int)pPresentInfo->PImageIndices[i];
            {
                Result err = SharedAPI.Vulkan.WaitForFences(g_Device, 1, ref g_Frames[backBufferIndex].Fence, Vk.True, ulong.MaxValue);
                CheckVkResult(err);
                err = SharedAPI.Vulkan.ResetFences(g_Device, 1, ref g_Frames[backBufferIndex].Fence);
                CheckVkResult(err);
            }
            {
                SharedAPI.Vulkan.ResetCommandBuffer(g_Frames[backBufferIndex].CommandBuffer, CommandBufferResetFlags.None);
                CommandBufferBeginInfo info = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit
                };
                SharedAPI.Vulkan.BeginCommandBuffer(g_Frames[backBufferIndex].CommandBuffer, &info);
            }
            {
                RenderPassBeginInfo info = new RenderPassBeginInfo
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = g_RenderPass,
                    Framebuffer = g_Frames[backBufferIndex].Framebuffer,
                    RenderArea = new Rect2D(null, g_ImageExtent)
                };
                SharedAPI.Vulkan.CmdBeginRenderPass(g_Frames[backBufferIndex].CommandBuffer, &info, SubpassContents.Inline);
            }
            if (CanAttachWindowHandle())
            {
                _checkVkResult = CheckVkResult;
                uint apiVersion;
                SharedAPI.Vulkan.EnumerateInstanceVersion(&apiVersion);
                g_InitInfo = new ImGuiImplVulkan.InitInfo
                {
                    ApiVersion = apiVersion,
                    Instance = g_Instance,
                    PhysicalDevice = g_PhysicalDevice,
                    Device = g_Device,
                    QueueFamily = g_QueueFamily,
                    Queue = graphicQueue,
                    DescriptorPool = g_DescriptorPool,
                    MinImageCount = 2,
                    ImageCount = (uint)g_Frames.Length,
                    Allocator = g_Allocator,
                    PipelineInfoMain = new ImGuiImplVulkan.PipelineInfo
                    {
                        RenderPass = g_RenderPass,
                        Subpass = 0,
                        MSAASamples = SampleCountFlags.Count1Bit
                    },
                    CheckVkResultFn = (delegate* unmanaged[Cdecl]<Result, void>)Marshal.GetFunctionPointerForDelegate(_checkVkResult)
                };
            }
            DearImGuiInjectionCore.MultiContextCompositor.PreNewFrameUpdateAll();
            for (int j = DearImGuiInjectionCore.MultiContextCompositor.ModulesFrontToBack.Count - 1; j >= 0; j--)
            {
                ImGuiModule module = DearImGuiInjectionCore.MultiContextCompositor.ModulesFrontToBack[j];
                ImGui.SetCurrentContext(module.Context);
                if (!module.IsInitialized)
                {
                    if (!ImGuiImplWin32.Init(WindowHandle))
                    {
                        DearImGuiInjectionCore.DestroyModule(module.Id);
                        Log.Error($"Module \"{module.Id}\" ImGuiImplWin32.Init failed. Destroying module.");
                        continue;
                    }
                    ImGuiImplVulkan.InitInfo initInfo = g_InitInfo;
                    ImGuiImplVulkan.Init(&initInfo);
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
                ImGuiImplVulkan.NewFrame();
                ImGui.NewFrame();
                DearImGuiInjectionCore.MultiContextCompositor.PostNewFrameUpdateOne(module);
                try
                {
                    module.OnRender();
                    ImGui.Render();
                    ImGuiImplVulkan.RenderDrawData(ImGui.GetDrawData(), g_Frames[backBufferIndex].CommandBuffer);
                }
                catch (Exception e)
                {
                    ImGui.EndFrame();
                    DearImGuiInjectionCore.DestroyModule(module.Id);
                    Log.Error($"Module \"{module.Id}\" OnRender threw an exception: {e}");
                }
            }
            DearImGuiInjectionCore.MultiContextCompositor.PostEndFrameUpdateAll();
            SharedAPI.Vulkan.CmdEndRenderPass(g_Frames[backBufferIndex].CommandBuffer);
            SharedAPI.Vulkan.EndCommandBuffer(g_Frames[backBufferIndex].CommandBuffer);
            uint waitSemaphoresCount = i == 0 ? pPresentInfo->WaitSemaphoreCount : 0;
            CommandBuffer command_buffer = g_Frames[backBufferIndex].CommandBuffer;
            if (waitSemaphoresCount == 0 && graphicQueue.Handle != 0)
            {
                PipelineStageFlags stages_wait = PipelineStageFlags.AllCommandsBit;
                Semaphore render_complete_semaphore = g_FrameSemaphores[backBufferIndex].RenderCompleteSemaphore;
                {
                    SubmitInfo info = new SubmitInfo
                    {
                        SType = StructureType.SubmitInfo,
                        PWaitDstStageMask = &stages_wait,
                        SignalSemaphoreCount = 1,
                        PSignalSemaphores = &render_complete_semaphore
                    };
                    SharedAPI.Vulkan.QueueSubmit(queue, 1, &info, default);
                }
                {
                    Semaphore image_acquired_semaphore = g_FrameSemaphores[backBufferIndex].ImageAcquiredSemaphore;
                    SubmitInfo info = new SubmitInfo
                    {
                        SType = StructureType.SubmitInfo,
                        CommandBufferCount = 1,
                        PCommandBuffers = &command_buffer,
                        PWaitDstStageMask = &stages_wait,
                        WaitSemaphoreCount = 1,
                        PWaitSemaphores = &render_complete_semaphore,
                        SignalSemaphoreCount = 1,
                        PSignalSemaphores = &image_acquired_semaphore
                    };
                    SharedAPI.Vulkan.QueueSubmit(graphicQueue, 1, &info, g_Frames[backBufferIndex].Fence);
                }
            }
            else
            {
                PipelineStageFlags* stages_wait = stackalloc PipelineStageFlags[(int)waitSemaphoresCount];
                for (int j = 0; j < waitSemaphoresCount; j++)
                    stages_wait[j] = PipelineStageFlags.FragmentShaderBit;
                SubmitInfo info = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &command_buffer,
                    PWaitDstStageMask = stages_wait,
                    WaitSemaphoreCount = waitSemaphoresCount,
                    PWaitSemaphores = pPresentInfo->PWaitSemaphores,
                    SignalSemaphoreCount = waitSemaphoresCount,
                    PSignalSemaphores = pPresentInfo->PWaitSemaphores
                };
                SharedAPI.Vulkan.QueueSubmit(graphicQueue, 1, &info, g_Frames[backBufferIndex].Fence);
            }
        }
        if (!hasWindowHandle)
            CleanupRenderTarget();
    }

    private unsafe void CreateDevice()
    {
        {
            byte* instance_extension = (byte*)Marshal.StringToHGlobalAnsi(KhrSurface.ExtensionName);
            InstanceCreateInfo create_info = new InstanceCreateInfo()
            {
                SType = StructureType.InstanceCreateInfo,
                EnabledExtensionCount = 1,
                PpEnabledExtensionNames = &instance_extension
            };
            SharedAPI.Vulkan = Vk.GetApi();
            Result err = SharedAPI.Vulkan.CreateInstance(&create_info, g_Allocator, out g_Instance);
            CheckVkResult(err);
            Marshal.FreeHGlobal((IntPtr)instance_extension);
            g_KhrSwapchain = new KhrSwapchain(SharedAPI.Vulkan.Context);
        }
        {
            uint count;
            Result err = SharedAPI.Vulkan.EnumeratePhysicalDevices(g_Instance, &count, null);
            CheckVkResult(err);
            if (count <= 0)
                throw new InvalidOperationException("No physical devices were found.");
            PhysicalDevice* physical_devices = stackalloc PhysicalDevice[(int)count];
            err = SharedAPI.Vulkan.EnumeratePhysicalDevices(g_Instance, &count, physical_devices);
            CheckVkResult(err);
            int index = 0;
            for (int i = 0; i < count; i++)
            {
                PhysicalDeviceProperties properties;
                SharedAPI.Vulkan.GetPhysicalDeviceProperties(physical_devices[i], &properties);
                if (properties.DeviceType == PhysicalDeviceType.DiscreteGpu)
                {
                    index = i;
                    break;
                }
            }
            g_PhysicalDevice = physical_devices[index];
        }
        {
            g_QueueFamily = Vk.QueueFamilyIgnored;
            uint count;
            SharedAPI.Vulkan.GetPhysicalDeviceQueueFamilyProperties(g_PhysicalDevice, &count, null);
            g_QueueFamilies.Resize((int)count);
            SharedAPI.Vulkan.GetPhysicalDeviceQueueFamilyProperties(g_PhysicalDevice, &count, g_QueueFamilies.Data);
            for (int i = 0; i < count; i++)
            {
                if ((g_QueueFamilies[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                {
                    g_QueueFamily = (uint)i;
                    break;
                }
            }
            if (g_QueueFamily == Vk.QueueFamilyIgnored)
                throw new InvalidOperationException("No graphics queue family was found.");
        }
    }

    private unsafe void CleanupDevice()
    {
        CleanupRenderTarget();
        if (g_DescriptorPool.Handle != 0)
            SharedAPI.Vulkan.DestroyDescriptorPool(g_Device, g_DescriptorPool, g_Allocator);
        if (g_Instance.Handle != 0)
            SharedAPI.Vulkan.DestroyInstance(g_Instance, g_Allocator);
    }

    private unsafe void CreateRenderTarget(Device device, SwapchainKHR swapchain)
    {
        Format format = Format.R8G8B8A8Unorm;
        uint uImageCount;
        Result err = g_KhrSwapchain.GetSwapchainImages(device, swapchain, &uImageCount, null);
        CheckVkResult(err);
        Image* backbuffers = stackalloc Image[(int)uImageCount];
        err = g_KhrSwapchain.GetSwapchainImages(device, swapchain, &uImageCount, backbuffers);
        CheckVkResult(err);
        g_Frames = new Frame[uImageCount];
        g_FrameSemaphores = new FrameSemaphores[uImageCount];
        for (int i = 0; i < uImageCount; i++)
        {
            g_Frames[i].Backbuffer = backbuffers[i];

            {
                CommandPoolCreateInfo info = new CommandPoolCreateInfo
                {
                    SType = StructureType.CommandPoolCreateInfo,
                    Flags = CommandPoolCreateFlags.None,
                    QueueFamilyIndex = g_QueueFamily
                };
                err = SharedAPI.Vulkan.CreateCommandPool(device, &info, g_Allocator, out g_Frames[i].CommandPool);
                CheckVkResult(err);
            }
            {
                CommandBufferAllocateInfo info = new CommandBufferAllocateInfo
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = g_Frames[i].CommandPool,
                    Level = CommandBufferLevel.Primary,
                    CommandBufferCount = 1
                };
                err = SharedAPI.Vulkan.AllocateCommandBuffers(device, &info, out g_Frames[i].CommandBuffer);
                CheckVkResult(err);
            }
            {
                FenceCreateInfo info = new FenceCreateInfo
                {
                    SType = StructureType.FenceCreateInfo,
                    Flags = FenceCreateFlags.SignaledBit
                };
                err = SharedAPI.Vulkan.CreateFence(device, &info, g_Allocator, out g_Frames[i].Fence);
                CheckVkResult(err);
            }
            {
                SemaphoreCreateInfo info = new SemaphoreCreateInfo
                {
                    SType = StructureType.SemaphoreCreateInfo
                };
                err = SharedAPI.Vulkan.CreateSemaphore(device, &info, g_Allocator, out g_FrameSemaphores[i].ImageAcquiredSemaphore);
                CheckVkResult(err);
                err = SharedAPI.Vulkan.CreateSemaphore(device, &info, g_Allocator, out g_FrameSemaphores[i].RenderCompleteSemaphore);
                CheckVkResult(err);
            }
        }
        {
            AttachmentDescription attachment = new AttachmentDescription
            {
                Format = format,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.DontCare,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.PresentSrcKhr
            };
            AttachmentReference color_attachment = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal
            };
            SubpassDescription subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &color_attachment
            };
            SubpassDependency dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit
            };
            RenderPassCreateInfo info = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &attachment,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency
            };
            err = SharedAPI.Vulkan.CreateRenderPass(device, &info, g_Allocator, out g_RenderPass);
            CheckVkResult(err);
        }
        {
            ImageViewCreateInfo info = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = new ComponentMapping
                {
                    R = ComponentSwizzle.R,
                    G = ComponentSwizzle.G,
                    B = ComponentSwizzle.B,
                    A = ComponentSwizzle.A
                },
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
            };
            for (int i = 0; i < uImageCount; i++)
            {
                info.Image = g_Frames[i].Backbuffer;
                err = SharedAPI.Vulkan.CreateImageView(device, &info, g_Allocator, out g_Frames[i].BackbufferView);
                CheckVkResult(err);
            }
        }
        {
            ImageView* attachment = stackalloc ImageView[1];
            FramebufferCreateInfo info = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = g_RenderPass,
                AttachmentCount = 1,
                PAttachments = attachment,
                Width = g_ImageExtent.Width,
                Height = g_ImageExtent.Height,
                Layers = 1
            };
            for (int i = 0; i < uImageCount; i++)
            {
                attachment[0] = g_Frames[i].BackbufferView;
                err = SharedAPI.Vulkan.CreateFramebuffer(device, &info, g_Allocator, out g_Frames[i].Framebuffer);
                CheckVkResult(err);
            }
        }
        if (g_DescriptorPool.Handle == 0) 
        {
            DescriptorPoolSize* pool_sizes = stackalloc DescriptorPoolSize[1]
            {
                new DescriptorPoolSize()
                {
                    Type = DescriptorType.CombinedImageSampler,
                    DescriptorCount = ImGuiImplVulkan.IMGUI_IMPL_VULKAN_MINIMUM_IMAGE_SAMPLER_POOL_SIZE
                }
            };
            DescriptorPoolCreateInfo pool_info = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                MaxSets = ImGuiImplVulkan.IMGUI_IMPL_VULKAN_MINIMUM_IMAGE_SAMPLER_POOL_SIZE,
                PoolSizeCount = 1,
                PPoolSizes = pool_sizes
            };
            err = SharedAPI.Vulkan.CreateDescriptorPool(device, &pool_info, g_Allocator, out g_DescriptorPool);
            CheckVkResult(err);
        }
    }

    private unsafe void CleanupRenderTarget()
    {
        if (g_Frames != null)
        {
            for (int i = 0; i < g_Frames.Length; i++)
            {
                if (g_Frames[i].Framebuffer.Handle != 0)
                    SharedAPI.Vulkan.DestroyFramebuffer(g_Device, g_Frames[i].Framebuffer, g_Allocator);
                if (g_Frames[i].BackbufferView.Handle != 0)
                    SharedAPI.Vulkan.DestroyImageView(g_Device, g_Frames[i].BackbufferView, g_Allocator);
                if (g_Frames[i].Fence.Handle != 0)
                    SharedAPI.Vulkan.DestroyFence(g_Device, g_Frames[i].Fence, g_Allocator);
                if (g_Frames[i].CommandPool.Handle != 0)
                    SharedAPI.Vulkan.DestroyCommandPool(g_Device, g_Frames[i].CommandPool, g_Allocator);
            }
            g_Frames = null;
        }
        if (g_FrameSemaphores != null)
        {
            for (int i = 0; i < g_FrameSemaphores.Length; i++)
            {
                if (g_FrameSemaphores[i].ImageAcquiredSemaphore.Handle != 0)
                    SharedAPI.Vulkan.DestroySemaphore(g_Device, g_FrameSemaphores[i].ImageAcquiredSemaphore, g_Allocator);
                if (g_FrameSemaphores[i].RenderCompleteSemaphore.Handle != 0)
                    SharedAPI.Vulkan.DestroySemaphore(g_Device, g_FrameSemaphores[i].RenderCompleteSemaphore, g_Allocator);
            }
            g_FrameSemaphores = null;
        }
    }

    private unsafe Queue GetGraphicQueue()
    {
        for (int i = 0; i < g_QueueFamilies.Size; i++)
        {
            QueueFamilyProperties family = g_QueueFamilies[i];
            for (int j = 0; j < family.QueueCount; j++)
            {
                Queue graphicQueue = default;
                SharedAPI.Vulkan.GetDeviceQueue(g_Device, (uint)i, (uint)j, out graphicQueue);
                if ((family.QueueFlags & QueueFlags.GraphicsBit) != 0)
                    return graphicQueue;
            }
        }
        return default;
    }

    private static void CheckVkResult(Result err)
    {
        if (err == Result.Success)
            return;
        if (err < Result.Success)
            throw new InvalidOperationException($"Vulkan failed: {err}");
    }
}
