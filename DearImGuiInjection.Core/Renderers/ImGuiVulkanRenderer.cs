using DearImGuiInjection.Backends;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using Silk.NET.Core.Native;
using Silk.NET.DXGI;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Renderers;

internal sealed class ImGuiVulkanRenderer : ImGuiRenderer
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate Result QueuePresentKHRDelegate(Queue queue, PresentInfoKHR* pPresentInfo);
    private MinHookDetour<QueuePresentKHRDelegate> _queuePresentKHR;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate Result CreateSwapchainKHRDelegate(Device device, SwapchainCreateInfoKHR* pCreateInfo, AllocationCallbacks* pAllocator, SwapchainKHR* pSwapchain);
    private MinHookDetour<CreateSwapchainKHRDelegate> _createSwapchainKHR;

    public unsafe override void Init()
    {
        IntPtr windowHandle = User32.CreateFakeWindow();
        Vk vk = Vk.GetApi();
        InstanceCreateInfo create_info = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            EnabledExtensionCount = 0
        };
        Instance instance;
        if (vk.CreateInstance(&create_info, null, &instance) != Result.Success)
            throw new InvalidOperationException("Failed to create Vulkan instance.");
        uint device_count = 0;
        vk.EnumeratePhysicalDevices(instance, &device_count, null);
        if (device_count == 0)
        {
            vk.DestroyInstance(instance, null);
            throw new InvalidOperationException("No Vulkan physical devices found.");
        }
        PhysicalDevice* physical_devices = stackalloc PhysicalDevice[(int)device_count];
        vk.EnumeratePhysicalDevices(instance, &device_count, physical_devices);
        PhysicalDevice physical_device = physical_devices[0];
        uint queue_family_index = FindGraphicsQueueFamily(vk, physical_device);
        IntPtr device_extension = Marshal.StringToHGlobalAnsi(KhrSwapchain.ExtensionName);
        byte* device_extension_ptr = (byte*)device_extension;
        DeviceQueueCreateInfo queue_create_info = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = queue_family_index,
            QueueCount = 1
        };
        DeviceCreateInfo device_create_info = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queue_create_info,
            EnabledExtensionCount = 1,
            PpEnabledExtensionNames = &device_extension_ptr
        };
        Device device;
        if (vk.CreateDevice(physical_device, &device_create_info, null, &device) != Result.Success)
        {
            Marshal.FreeHGlobal(device_extension);
            vk.DestroyInstance(instance, null);
            throw new InvalidOperationException("Failed to create Vulkan device.");
        }
        Marshal.FreeHGlobal(device_extension);
        MinHook.Ok(MinHook.Initialize(), "MH_Initialize");
        string process_name = "vkQueuePresentKHR";
        _queuePresentKHR = new(process_name);
        _queuePresentKHR.Create(vk.GetDeviceProcAddr(device, process_name), QueuePresentKHRDetour);
        _queuePresentKHR.Enable();
        process_name = "vkCreateSwapchainKHR";
        _createSwapchainKHR = new(process_name);
        _createSwapchainKHR.Create(vk.GetDeviceProcAddr(device, process_name), CreateSwapchainKHRDetour);
        _createSwapchainKHR.Enable();
        vk.DestroyDevice(device, null);
        vk.DestroyInstance(instance, null);
        User32.DestroyWindow(windowHandle);
    }

    private unsafe uint FindGraphicsQueueFamily(Vk vk, PhysicalDevice physical_device)
    {
        uint queue_family_count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(physical_device, &queue_family_count, null);
        var queue_families = stackalloc QueueFamilyProperties[(int)queue_family_count];
        vk.GetPhysicalDeviceQueueFamilyProperties(physical_device, &queue_family_count, queue_families);
        for (uint i = 0; i < queue_family_count; i++)
            if ((queue_families[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                return i;
        return 0;
    }

    public unsafe override void Dispose()
    {
        _createSwapchainKHR.Dispose();
        _queuePresentKHR.Dispose();
        MinHook.Ok(MinHook.Uninitialize(), "MH_Uninitialize");
        foreach (ImGuiModule module in DearImGuiInjectionCore.MultiContextCompositor.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            Shutdown(module.IsInitialized);
        }
    }

    public override void Shutdown(bool isInitialized)
    {
    }

    private unsafe Result QueuePresentKHRDetour(Queue queue, PresentInfoKHR* pPresentInfo)
    {
        if (!IsInitialized)
        {
            Log.Info(_queuePresentKHR.Name);
        }
        return _queuePresentKHR.Original(queue, pPresentInfo);
    }

    private unsafe Result CreateSwapchainKHRDetour(Device device, SwapchainCreateInfoKHR* pCreateInfo, AllocationCallbacks* pAllocator, 
        SwapchainKHR* pSwapchain)
    {
        Log.Info(_createSwapchainKHR.Name);
        return _createSwapchainKHR.Original(device, pCreateInfo, pAllocator, pSwapchain);
    }
}