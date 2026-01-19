using Hexa.NET.ImGui;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

using ImDrawIdx = ushort;
using DeviceSize = ulong;
using Buffer = Silk.NET.Vulkan.Buffer;
using System.Numerics;

namespace DearImGuiInjection.Backends;

internal static class ImGuiImplVulkan
{
    internal const uint IMGUI_IMPL_VULKAN_MINIMUM_IMAGE_SAMPLER_POOL_SIZE = 8;

    private static readonly IntPtr _entryMain = Marshal.StringToHGlobalAnsi("main");

    // Structs
    internal struct InitInfo
    {
        public uint ApiVersion;                 // Fill with API version of Instance, e.g. VK_API_VERSION_1_3 or your value of VkApplicationInfo::apiVersion. May be lower than header version (VK_HEADER_VERSION_COMPLETE)
        public Instance Instance;
        public PhysicalDevice PhysicalDevice;
        public Device Device;
        public uint QueueFamily;
        public Queue Queue;
        public DescriptorPool DescriptorPool;   // See requirements in note above; ignored if using DescriptorPoolSize > 0
        public uint DescriptorPoolSize;         // Optional: set to create internal descriptor pool automatically instead of using DescriptorPool.
        public uint MinImageCount;              // >= 2
        public uint ImageCount;                 // >= MinImageCount
        public PipelineCache PipelineCache;     // Optional

        // Pipeline
        public PipelineInfo PipelineInfoMain;   // Infos for Main Viewport (created by app/user)
        //VkRenderPass                  RenderPass; // --> Since 2025/09/26: set 'PipelineInfoMain.RenderPass' instead
        //uint32_t                      Subpass; // --> Since 2025/09/26: set 'PipelineInfoMain.Subpass' instead
        //VkSampleCountFlagBits         MSAASamples; // --> Since 2025/09/26: set 'PipelineInfoMain.MSAASamples' instead
        //VkPipelineRenderingCreateInfoKHR PipelineRenderingCreateInfo; // Since 2025/09/26: set 'PipelineInfoMain.PipelineRenderingCreateInfo' instead
    
        // (Optional) Dynamic Rendering
        // Need to explicitly enable VK_KHR_dynamic_rendering extension to use this, even for Vulkan 1.3 + setup PipelineInfoMain.PipelineRenderingCreateInfo.
        public bool UseDynamicRendering;

        // (Optional) Allocation, Debugging
        public unsafe AllocationCallbacks* Allocator;
        public unsafe delegate* unmanaged[Cdecl]<Result, void> CheckVkResultFn;
        public ulong MinAllocationSize;          // Minimum allocation size. Set to 1024*1024 to satisfy zealous best practices validation layer and waste a little memory.

        // (Optional) Customize default vertex/fragment shaders.
        // - if .sType == VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO we use specified structs, otherwise we use defaults.
        // - Shader inputs/outputs need to match ours. Code/data pointed to by the structure needs to survive for whole during of backend usage.
        public ShaderModuleCreateInfo CustomShaderVertCreateInfo;
        public ShaderModuleCreateInfo CustomShaderFragCreateInfo;
    }

    private unsafe struct ImDrawCallback
    {
        public static void* ResetRenderState = (void*)ImGui.ImDrawCallbackResetRenderState;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void UserCallback(ImDrawList* parent_list, ImDrawCmd* cmd);
    }

    internal struct PipelineInfo
    {
        public RenderPass RenderPass;
        public uint Subpass;
        public SampleCountFlags MSAASamples;
    }

    private struct RenderState
    {
        public CommandBuffer CommandBuffer;
        public Pipeline Pipeline;
        public PipelineLayout PipelineLayout;
    }

    // Reusable buffers used for rendering 1 current in-flight frame, for ImGui_ImplVulkan_RenderDrawData()
    // [Please zero-clear before use!]
    private struct FrameRenderBuffers
    {
        public DeviceMemory VertexBufferMemory;
        public DeviceMemory IndexBufferMemory;
        public DeviceSize VertexBufferSize;
        public DeviceSize IndexBufferSize;
        public Buffer VertexBuffer;
        public Buffer IndexBuffer;
    }

    // Each viewport will hold 1 ImGui_ImplVulkanH_WindowRenderBuffers
    // [Please zero-clear before use!]
    private unsafe struct WindowRenderBuffers
    {
        public uint Index;
        public uint Count;
        public ImVector<FrameRenderBuffers> FrameRenderBuffers;
    }

    private struct Texture
    {
        public DeviceMemory Memory;
        public Image Image;
        public ImageView ImageView;
        public DescriptorSet DescriptorSet;
    }

    // Vulkan data
    private unsafe struct Data
    {
        public InitInfo VulkanInitInfo;
        public DeviceSize BufferMemoryAlignment;
        public DeviceSize NonCoherentAtomSize;
        public PipelineCreateFlags PipelineCreateFlags;
        public DescriptorSetLayout DescriptorSetLayout;
        public PipelineLayout PipelineLayout;
        public Pipeline Pipeline;
        public ShaderModule ShaderModuleVert;
        public ShaderModule ShaderModuleFrag;
        public DescriptorPool DescriptorPool;
        public ImVector<Format> PipelineRenderingCreateInfoColorAttachmentFormats; // Deep copy of format array

        // Texture management
        public Sampler TexSamplerLinear;
        public CommandPool TexCommandPool;
        public CommandBuffer TexCommandBuffer;

        // Render buffers for main window
        public WindowRenderBuffers MainWindowRenderBuffers;

        public Data()
        {
            BufferMemoryAlignment = 256;
            NonCoherentAtomSize = 64;
        }
    }

    //-----------------------------------------------------------------------------
    // SHADERS
    //-----------------------------------------------------------------------------

    // backends/vulkan/glsl_shader.vert, compiled with:
    // # glslangValidator -V -x -o glsl_shader.vert.u32 glsl_shader.vert
    /*
    #version 450 core
    layout(location = 0) in vec2 aPos;
    layout(location = 1) in vec2 aUV;
    layout(location = 2) in vec4 aColor;
    layout(push_constant) uniform uPushConstant { vec2 uScale; vec2 uTranslate; } pc;

    out gl_PerVertex { vec4 gl_Position; };
    layout(location = 0) out struct { vec4 Color; vec2 UV; } Out;

    void main()
    {
        Out.Color = aColor;
        Out.UV = aUV;
        gl_Position = vec4(aPos * pc.uScale + pc.uTranslate, 0, 1);
    }
    */
    private static readonly uint[] __glsl_shader_vert_spv =
    {
        0x07230203,0x00010000,0x00080001,0x0000002e,0x00000000,0x00020011,0x00000001,0x0006000b,
        0x00000001,0x4c534c47,0x6474732e,0x3035342e,0x00000000,0x0003000e,0x00000000,0x00000001,
        0x000a000f,0x00000000,0x00000004,0x6e69616d,0x00000000,0x0000000b,0x0000000f,0x00000015,
        0x0000001b,0x0000001c,0x00030003,0x00000002,0x000001c2,0x00040005,0x00000004,0x6e69616d,
        0x00000000,0x00030005,0x00000009,0x00000000,0x00050006,0x00000009,0x00000000,0x6f6c6f43,
        0x00000072,0x00040006,0x00000009,0x00000001,0x00005655,0x00030005,0x0000000b,0x0074754f,
        0x00040005,0x0000000f,0x6c6f4361,0x0000726f,0x00030005,0x00000015,0x00565561,0x00060005,
        0x00000019,0x505f6c67,0x65567265,0x78657472,0x00000000,0x00060006,0x00000019,0x00000000,
        0x505f6c67,0x7469736f,0x006e6f69,0x00030005,0x0000001b,0x00000000,0x00040005,0x0000001c,
        0x736f5061,0x00000000,0x00060005,0x0000001e,0x73755075,0x6e6f4368,0x6e617473,0x00000074,
        0x00050006,0x0000001e,0x00000000,0x61635375,0x0000656c,0x00060006,0x0000001e,0x00000001,
        0x61725475,0x616c736e,0x00006574,0x00030005,0x00000020,0x00006370,0x00040047,0x0000000b,
        0x0000001e,0x00000000,0x00040047,0x0000000f,0x0000001e,0x00000002,0x00040047,0x00000015,
        0x0000001e,0x00000001,0x00050048,0x00000019,0x00000000,0x0000000b,0x00000000,0x00030047,
        0x00000019,0x00000002,0x00040047,0x0000001c,0x0000001e,0x00000000,0x00050048,0x0000001e,
        0x00000000,0x00000023,0x00000000,0x00050048,0x0000001e,0x00000001,0x00000023,0x00000008,
        0x00030047,0x0000001e,0x00000002,0x00020013,0x00000002,0x00030021,0x00000003,0x00000002,
        0x00030016,0x00000006,0x00000020,0x00040017,0x00000007,0x00000006,0x00000004,0x00040017,
        0x00000008,0x00000006,0x00000002,0x0004001e,0x00000009,0x00000007,0x00000008,0x00040020,
        0x0000000a,0x00000003,0x00000009,0x0004003b,0x0000000a,0x0000000b,0x00000003,0x00040015,
        0x0000000c,0x00000020,0x00000001,0x0004002b,0x0000000c,0x0000000d,0x00000000,0x00040020,
        0x0000000e,0x00000001,0x00000007,0x0004003b,0x0000000e,0x0000000f,0x00000001,0x00040020,
        0x00000011,0x00000003,0x00000007,0x0004002b,0x0000000c,0x00000013,0x00000001,0x00040020,
        0x00000014,0x00000001,0x00000008,0x0004003b,0x00000014,0x00000015,0x00000001,0x00040020,
        0x00000017,0x00000003,0x00000008,0x0003001e,0x00000019,0x00000007,0x00040020,0x0000001a,
        0x00000003,0x00000019,0x0004003b,0x0000001a,0x0000001b,0x00000003,0x0004003b,0x00000014,
        0x0000001c,0x00000001,0x0004001e,0x0000001e,0x00000008,0x00000008,0x00040020,0x0000001f,
        0x00000009,0x0000001e,0x0004003b,0x0000001f,0x00000020,0x00000009,0x00040020,0x00000021,
        0x00000009,0x00000008,0x0004002b,0x00000006,0x00000028,0x00000000,0x0004002b,0x00000006,
        0x00000029,0x3f800000,0x00050036,0x00000002,0x00000004,0x00000000,0x00000003,0x000200f8,
        0x00000005,0x0004003d,0x00000007,0x00000010,0x0000000f,0x00050041,0x00000011,0x00000012,
        0x0000000b,0x0000000d,0x0003003e,0x00000012,0x00000010,0x0004003d,0x00000008,0x00000016,
        0x00000015,0x00050041,0x00000017,0x00000018,0x0000000b,0x00000013,0x0003003e,0x00000018,
        0x00000016,0x0004003d,0x00000008,0x0000001d,0x0000001c,0x00050041,0x00000021,0x00000022,
        0x00000020,0x0000000d,0x0004003d,0x00000008,0x00000023,0x00000022,0x00050085,0x00000008,
        0x00000024,0x0000001d,0x00000023,0x00050041,0x00000021,0x00000025,0x00000020,0x00000013,
        0x0004003d,0x00000008,0x00000026,0x00000025,0x00050081,0x00000008,0x00000027,0x00000024,
        0x00000026,0x00050051,0x00000006,0x0000002a,0x00000027,0x00000000,0x00050051,0x00000006,
        0x0000002b,0x00000027,0x00000001,0x00070050,0x00000007,0x0000002c,0x0000002a,0x0000002b,
        0x00000028,0x00000029,0x00050041,0x00000011,0x0000002d,0x0000001b,0x0000000d,0x0003003e,
        0x0000002d,0x0000002c,0x000100fd,0x00010038
    };

    // backends/vulkan/glsl_shader.frag, compiled with:
    // # glslangValidator -V -x -o glsl_shader.frag.u32 glsl_shader.frag
    /*
    #version 450 core
    layout(location = 0) out vec4 fColor;
    layout(set=0, binding=0) uniform sampler2D sTexture;
    layout(location = 0) in struct { vec4 Color; vec2 UV; } In;
    void main()
    {
        fColor = In.Color * texture(sTexture, In.UV.st);
    }
    */
    private static readonly uint[] __glsl_shader_frag_spv =
    {
        0x07230203,0x00010000,0x00080001,0x0000001e,0x00000000,0x00020011,0x00000001,0x0006000b,
        0x00000001,0x4c534c47,0x6474732e,0x3035342e,0x00000000,0x0003000e,0x00000000,0x00000001,
        0x0007000f,0x00000004,0x00000004,0x6e69616d,0x00000000,0x00000009,0x0000000d,0x00030010,
        0x00000004,0x00000007,0x00030003,0x00000002,0x000001c2,0x00040005,0x00000004,0x6e69616d,
        0x00000000,0x00040005,0x00000009,0x6c6f4366,0x0000726f,0x00030005,0x0000000b,0x00000000,
        0x00050006,0x0000000b,0x00000000,0x6f6c6f43,0x00000072,0x00040006,0x0000000b,0x00000001,
        0x00005655,0x00030005,0x0000000d,0x00006e49,0x00050005,0x00000016,0x78655473,0x65727574,
        0x00000000,0x00040047,0x00000009,0x0000001e,0x00000000,0x00040047,0x0000000d,0x0000001e,
        0x00000000,0x00040047,0x00000016,0x00000022,0x00000000,0x00040047,0x00000016,0x00000021,
        0x00000000,0x00020013,0x00000002,0x00030021,0x00000003,0x00000002,0x00030016,0x00000006,
        0x00000020,0x00040017,0x00000007,0x00000006,0x00000004,0x00040020,0x00000008,0x00000003,
        0x00000007,0x0004003b,0x00000008,0x00000009,0x00000003,0x00040017,0x0000000a,0x00000006,
        0x00000002,0x0004001e,0x0000000b,0x00000007,0x0000000a,0x00040020,0x0000000c,0x00000001,
        0x0000000b,0x0004003b,0x0000000c,0x0000000d,0x00000001,0x00040015,0x0000000e,0x00000020,
        0x00000001,0x0004002b,0x0000000e,0x0000000f,0x00000000,0x00040020,0x00000010,0x00000001,
        0x00000007,0x00090019,0x00000013,0x00000006,0x00000001,0x00000000,0x00000000,0x00000000,
        0x00000001,0x00000000,0x0003001b,0x00000014,0x00000013,0x00040020,0x00000015,0x00000000,
        0x00000014,0x0004003b,0x00000015,0x00000016,0x00000000,0x0004002b,0x0000000e,0x00000018,
        0x00000001,0x00040020,0x00000019,0x00000001,0x0000000a,0x00050036,0x00000002,0x00000004,
        0x00000000,0x00000003,0x000200f8,0x00000005,0x00050041,0x00000010,0x00000011,0x0000000d,
        0x0000000f,0x0004003d,0x00000007,0x00000012,0x00000011,0x0004003d,0x00000014,0x00000017,
        0x00000016,0x00050041,0x00000019,0x0000001a,0x0000000d,0x00000018,0x0004003d,0x0000000a,
        0x0000001b,0x0000001a,0x00050057,0x00000007,0x0000001c,0x00000017,0x0000001b,0x00050085,
        0x00000007,0x0000001d,0x00000012,0x0000001c,0x0003003e,0x00000009,0x0000001d,0x000100fd,
        0x00010038
    };

    //-----------------------------------------------------------------------------
    // FUNCTIONS
    //-----------------------------------------------------------------------------

    // Backend data stored in io.BackendRendererUserData to allow support for multiple Dear ImGui contexts
    // It is STRONGLY preferred that you use docking branch with multi-viewports (== single Dear ImGui context + multiple windows) instead of multiple Dear ImGui contexts.
    // FIXME: multi-context support is not tested and probably dysfunctional in this backend.
    private unsafe static Data* GetBackendData() => (Data*)ImGui.GetIO().BackendRendererUserData;

    private unsafe static uint MemoryType(MemoryPropertyFlags properties, uint type_bits)
    {
        Data* bd = GetBackendData();
        InitInfo* v = &bd->VulkanInitInfo;
        PhysicalDeviceMemoryProperties prop;
        SharedAPI.Vulkan.GetPhysicalDeviceMemoryProperties(v->PhysicalDevice, &prop);
        for (uint i = 0; i < prop.MemoryTypeCount; i++)
            if ((prop.MemoryTypes[(int)i].PropertyFlags & properties) == properties && (type_bits & (1 << (int)i)) != 0)
                return i;
        return uint.MaxValue; // Unable to find memoryType
    }

    private unsafe static void CheckVkResult(Result err)
    {
        Data* bd = GetBackendData();
        if (bd == null)
            return;
        InitInfo* v = &bd->VulkanInitInfo;
        if (v->CheckVkResultFn != null)
            v->CheckVkResultFn(err);
    }

    // Same as IM_MEMALIGN(). 'alignment' must be a power of two.
    private static DeviceSize AlignBufferSize(DeviceSize size, DeviceSize alignment) => (size + alignment - 1) & ~(alignment - 1);

    private unsafe static void CreateOrResizeBuffer(ref Buffer buffer, ref DeviceMemory buffer_memory, ref DeviceSize buffer_size, DeviceSize new_size, BufferUsageFlags usage)
    {
        Data* bd = GetBackendData();
        InitInfo* v = &bd->VulkanInitInfo;
        Result err;
        if (buffer.Handle != 0)
            SharedAPI.Vulkan.DestroyBuffer(v->Device, buffer, v->Allocator);
        if (buffer_memory.Handle != 0)
            SharedAPI.Vulkan.FreeMemory(v->Device, buffer_memory, v->Allocator);

        DeviceSize buffer_size_aligned = AlignBufferSize(Math.Max(v->MinAllocationSize, new_size), bd->BufferMemoryAlignment);
        BufferCreateInfo buffer_info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = buffer_size_aligned,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        err = SharedAPI.Vulkan.CreateBuffer(v->Device, &buffer_info, v->Allocator, out buffer);
        CheckVkResult(err);

        MemoryRequirements req;
        SharedAPI.Vulkan.GetBufferMemoryRequirements(v->Device, buffer, &req);
        bd->BufferMemoryAlignment = bd->BufferMemoryAlignment > req.Alignment ? bd->BufferMemoryAlignment : req.Alignment;
        MemoryAllocateInfo alloc_info = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = MemoryType(MemoryPropertyFlags.HostVisibleBit, req.MemoryTypeBits)
        };
        err = SharedAPI.Vulkan.AllocateMemory(v->Device, &alloc_info, v->Allocator, out buffer_memory);
        CheckVkResult(err);

        err = SharedAPI.Vulkan.BindBufferMemory(v->Device, buffer, buffer_memory, 0);
        CheckVkResult(err);
        buffer_size = buffer_size_aligned;
    }

    private unsafe static void SetupRenderState(ImDrawData* draw_data, Pipeline pipeline, CommandBuffer command_buffer, FrameRenderBuffers* rb, int fb_width, int fb_height)
    {
        Data* bd = GetBackendData();

        // Bind pipeline:
        {
            SharedAPI.Vulkan.CmdBindPipeline(command_buffer, PipelineBindPoint.Graphics, pipeline);
        }

        // Bind Vertex And Index Buffer:
        if (draw_data->TotalVtxCount > 0)
        {
            Buffer* vertex_buffers = stackalloc Buffer[1] { rb->VertexBuffer };
            DeviceSize* vertex_offset = stackalloc DeviceSize[1] { 0 };
            SharedAPI.Vulkan.CmdBindVertexBuffers(command_buffer, 0, 1, vertex_buffers, vertex_offset);
            SharedAPI.Vulkan.CmdBindIndexBuffer(command_buffer, rb->IndexBuffer, 0, sizeof(ImDrawIdx) == 2 ? IndexType.Uint16 : IndexType.Uint32);
        }

        // Setup viewport:
        {
            Viewport viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = (float)fb_width,
                Height = (float)fb_height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
            SharedAPI.Vulkan.CmdSetViewport(command_buffer, 0, 1, &viewport);
        }

        // Setup scale and translation:
        // Our visible imgui space lies from draw_data->DisplayPps (top left) to draw_data->DisplayPos+data_data->DisplaySize (bottom right). DisplayPos is (0,0) for single viewport apps.
        {
            float* scale = stackalloc float[2];
            scale[0] = 2.0f / draw_data->DisplaySize.X;
            scale[1] = 2.0f / draw_data->DisplaySize.Y;
            float* translate = stackalloc float[2];
            translate[0] = -1.0f - draw_data->DisplayPos.X * scale[0];
            translate[1] = -1.0f - draw_data->DisplayPos.Y * scale[1];
            SharedAPI.Vulkan.CmdPushConstants(command_buffer, bd->PipelineLayout, ShaderStageFlags.VertexBit, sizeof(float) * 0, sizeof(float) * 2, scale);
            SharedAPI.Vulkan.CmdPushConstants(command_buffer, bd->PipelineLayout, ShaderStageFlags.VertexBit, sizeof(float) * 2, sizeof(float) * 2, translate);
        }
    }

    // Render function
    public unsafe static void RenderDrawData(ImDrawData* draw_data, CommandBuffer command_buffer, Pipeline pipeline = default)
    {
        // Avoid rendering when minimized, scale coordinates for retina displays (screen coordinates != framebuffer coordinates)
        int fb_width = (int)(draw_data->DisplaySize.X * draw_data->FramebufferScale.X);
        int fb_height = (int)(draw_data->DisplaySize.Y * draw_data->FramebufferScale.Y);
        if (fb_width <= 0 || fb_height <= 0)
            return;

        // Catch up with texture updates. Most of the times, the list will have 1 element with an OK status, aka nothing to do.
        // (This almost always points to ImGui::GetPlatformIO().Textures[] but is part of ImDrawData to allow overriding or disabling texture updates).
        if (draw_data->Textures != null)
        {
            ImVector<ImTextureDataPtr>* textures = draw_data->Textures;
            for (int i = 0; i < textures->Size; i++)
            {
                ImTextureDataPtr tex = textures->Data[i];
                if (tex.Status != ImTextureStatus.Ok)
                    UpdateTexture(tex);
            }
        }

        Data* bd = GetBackendData();
        InitInfo* v = &bd->VulkanInitInfo;
        if (pipeline.Handle == 0)
            pipeline = bd->Pipeline;

        // Allocate array to store enough vertex/index buffers
        WindowRenderBuffers* wrb = &bd->MainWindowRenderBuffers;
        if (wrb->FrameRenderBuffers.Size == 0)
        {
            wrb->Index = 0;
            wrb->Count = v->ImageCount;
            wrb->FrameRenderBuffers.Resize((int)wrb->Count);
            int sizeInBytes = wrb->FrameRenderBuffers.Size * sizeof(FrameRenderBuffers);
            byte* ptr = (byte*)wrb->FrameRenderBuffers.Data;
            for (int i = 0; i < sizeInBytes; i++)
                ptr[i] = 0;
        }
        if (wrb->Count != v->ImageCount)
            throw new InvalidOperationException($"WindowRenderBuffers.Count mismatch: expected {v->ImageCount} (ImageCount) but got {wrb->Count}.");
        wrb->Index = (wrb->Index + 1) % wrb->Count;
        FrameRenderBuffers* rb = &wrb->FrameRenderBuffers.Data[wrb->Index];

        if (draw_data->TotalVtxCount > 0)
        {
            // Create or resize the vertex/index buffers
            DeviceSize vertex_size = AlignBufferSize((DeviceSize)(draw_data->TotalVtxCount * sizeof(ImDrawVert)), bd->BufferMemoryAlignment);
            DeviceSize index_size = AlignBufferSize((DeviceSize)(draw_data->TotalIdxCount * sizeof(ImDrawIdx)), bd->BufferMemoryAlignment);
            if (rb->VertexBuffer.Handle == 0 || rb->VertexBufferSize < vertex_size)
                CreateOrResizeBuffer(ref rb->VertexBuffer, ref rb->VertexBufferMemory, ref rb->VertexBufferSize, vertex_size, BufferUsageFlags.VertexBufferBit);
            if (rb->IndexBuffer.Handle == 0 || rb->IndexBufferSize < index_size)
                CreateOrResizeBuffer(ref rb->IndexBuffer, ref rb->IndexBufferMemory, ref rb->IndexBufferSize, index_size, BufferUsageFlags.IndexBufferBit);

            // Upload vertex/index data into a single contiguous GPU buffer
            ImDrawVert* vtx_dst = null;
            ImDrawIdx* idx_dst = null;
            Result err = SharedAPI.Vulkan.MapMemory(v->Device, rb->VertexBufferMemory, 0, vertex_size, 0, (void**)&vtx_dst);
            CheckVkResult(err);
            err = SharedAPI.Vulkan.MapMemory(v->Device, rb->IndexBufferMemory, 0, index_size, 0, (void**)&idx_dst);
            CheckVkResult(err);
            for (int n = 0; n < draw_data->CmdListsCount; n++)
            {
                ImDrawList* draw_list = draw_data->CmdLists.Data[n];
                System.Buffer.MemoryCopy(draw_list->VtxBuffer.Data, vtx_dst, (long)vertex_size, draw_list->VtxBuffer.Size * sizeof(ImDrawVert));
                System.Buffer.MemoryCopy(draw_list->IdxBuffer.Data, idx_dst, (long)index_size, draw_list->IdxBuffer.Size * sizeof(ImDrawIdx));
                vtx_dst += draw_list->VtxBuffer.Size;
                idx_dst += draw_list->IdxBuffer.Size;
            }
            MappedMemoryRange* range = stackalloc MappedMemoryRange[2]
            {
                new MappedMemoryRange
                {
                    SType = StructureType.MappedMemoryRange,
                    Memory = rb->VertexBufferMemory,
                    Size = Vk.WholeSize
                },
                new MappedMemoryRange
                {
                    SType = StructureType.MappedMemoryRange,
                    Memory = rb->IndexBufferMemory,
                    Size = Vk.WholeSize
                }
            };
            err = SharedAPI.Vulkan.FlushMappedMemoryRanges(v->Device, 2, range);
            CheckVkResult(err);
            SharedAPI.Vulkan.UnmapMemory(v->Device, rb->VertexBufferMemory);
            SharedAPI.Vulkan.UnmapMemory(v->Device, rb->IndexBufferMemory);
        }

        // Setup desired Vulkan state
        SetupRenderState(draw_data, pipeline, command_buffer, rb, fb_width, fb_height);

        // Setup render state structure (for callbacks and custom texture bindings)
        ImGuiPlatformIOPtr platform_io = ImGui.GetPlatformIO();
        RenderState render_state;
        render_state.CommandBuffer = command_buffer;
        render_state.Pipeline = pipeline;
        render_state.PipelineLayout = bd->PipelineLayout;
        platform_io.RendererRenderState = &render_state;

        // Will project scissor/clipping rectangles into framebuffer space
        Vector2 clip_off = draw_data->DisplayPos;         // (0,0) unless using multi-viewports
        Vector2 clip_scale = draw_data->FramebufferScale; // (1,1) unless using retina display which are often (2,2)

        // Render command lists
        // (Because we merged all buffers into a single one, we maintain our own offset into them)
        DescriptorSet last_desc_set = default;
        int global_vtx_offset = 0;
        int global_idx_offset = 0;
        for (int n = 0; n < draw_data->CmdListsCount; n++)
        {
            ImDrawList* draw_list = draw_data->CmdLists.Data[n];
            for (int cmd_i = 0; cmd_i < draw_list->CmdBuffer.Size; cmd_i++)
            {
                ImDrawCmd* pcmd = &draw_list->CmdBuffer.Data[cmd_i];
                if (pcmd->UserCallback != null)
                {
                    // User callback, registered via ImDrawList::AddCallback()
                    // (ImDrawCallback_ResetRenderState is a special callback value used by the user to request the renderer to reset render state.)
                    if (pcmd->UserCallback == ImDrawCallback.ResetRenderState)
                        SetupRenderState(draw_data, pipeline, command_buffer, rb, fb_width, fb_height);
                    else
                        ((delegate* unmanaged[Cdecl]<ImDrawList*, ImDrawCmd*, void>)pcmd->UserCallback)(draw_list, pcmd);
                    last_desc_set = default;
                }
                else
                {
                    // Project scissor/clipping rectangles into framebuffer space
                    Vector2 clip_min = new Vector2((pcmd->ClipRect.X - clip_off.X) * clip_scale.X, (pcmd->ClipRect.Y - clip_off.Y) * clip_scale.Y);
                    Vector2 clip_max = new Vector2((pcmd->ClipRect.Z - clip_off.X) * clip_scale.X, (pcmd->ClipRect.W - clip_off.Y) * clip_scale.Y);

                    // Clamp to viewport as vkCmdSetScissor() won't accept values that are off bounds
                    if (clip_min.X < 0.0f) { clip_min.X = 0.0f; }
                    if (clip_min.Y < 0.0f) { clip_min.Y = 0.0f; }
                    if (clip_max.X > fb_width) { clip_max.X = (float)fb_width; }
                    if (clip_max.Y > fb_height) { clip_max.Y = (float)fb_height; }
                    if (clip_max.X <= clip_min.X || clip_max.Y <= clip_min.Y)
                        continue;

                    // Apply scissor/clipping rectangle
                    Rect2D scissor = new Rect2D
                    {
                        Offset = new Offset2D
                        {
                            X = (int)clip_min.X,
                            Y = (int)clip_min.Y
                        },
                        Extent = new Extent2D
                        {
                            Width = (uint)(clip_max.X - clip_min.X),
                            Height = (uint)(clip_max.Y - clip_min.Y)
                        }
                    };
                    SharedAPI.Vulkan.CmdSetScissor(command_buffer, 0, 1, &scissor);

                    // Bind DescriptorSet with font or user texture
                    DescriptorSet desc_set = new DescriptorSet(pcmd->GetTexID());
                    if (desc_set.Handle != last_desc_set.Handle)
                        SharedAPI.Vulkan.CmdBindDescriptorSets(command_buffer, PipelineBindPoint.Graphics, bd->PipelineLayout, 0, 1, &desc_set, 0, null);
                    last_desc_set = desc_set;

                    // Draw
                    SharedAPI.Vulkan.CmdDrawIndexed(command_buffer, pcmd->ElemCount, 1, pcmd->IdxOffset + (uint)global_idx_offset, (int)pcmd->VtxOffset + global_vtx_offset, 0);
                }
            }
            global_idx_offset += draw_list->IdxBuffer.Size;
            global_vtx_offset += draw_list->VtxBuffer.Size;
        }
        platform_io.RendererRenderState = null;

        // Note: at this point both vkCmdSetViewport() and vkCmdSetScissor() have been called.
        // Our last values will leak into user/application rendering IF:
        // - Your app uses a pipeline with VK_DYNAMIC_STATE_VIEWPORT or VK_DYNAMIC_STATE_SCISSOR dynamic state
        // - And you forgot to call vkCmdSetViewport() and vkCmdSetScissor() yourself to explicitly set that state.
        // If you use VK_DYNAMIC_STATE_VIEWPORT or VK_DYNAMIC_STATE_SCISSOR you are responsible for setting the values before rendering.
        // In theory we should aim to backup/restore those values but I am not sure this is possible.
        // We perform a call to vkCmdSetScissor() to set back a full viewport which is likely to fix things for 99% users but technically this is not perfect. (See github #4644)
        Rect2D scissor_final = new Rect2D
        {
            Offset = new Offset2D 
            {
                X = 0,
                Y = 0
            },
            Extent = new Extent2D
            {
                Width = (uint)fb_width,
                Height = (uint)fb_height
            }
        };
        SharedAPI.Vulkan.CmdSetScissor(command_buffer, 0, 1, &scissor_final);
    }

    private unsafe static void DestroyTexture(ImTextureData* tex)
    {
        Texture* backend_tex = (Texture*)tex->BackendUserData;
        if (backend_tex != null)
        {
            if (backend_tex->DescriptorSet.Handle != tex->TexID)
                throw new InvalidOperationException("Texture ID mismatch while destroying texture.");
            Data* bd = GetBackendData();
            InitInfo* v = &bd->VulkanInitInfo;
            RemoveTexture(backend_tex->DescriptorSet);
            AllocationCallbacks* allocator = v->Allocator;
            SharedAPI.Vulkan.DestroyImageView(v->Device, backend_tex->ImageView, allocator);
            SharedAPI.Vulkan.DestroyImage(v->Device, backend_tex->Image, allocator);
            SharedAPI.Vulkan.FreeMemory(v->Device, backend_tex->Memory, allocator);
            ImGui.MemFree(backend_tex);

            // Clear identifiers and mark as destroyed (in order to allow e.g. calling InvalidateDeviceObjects while running)
            tex->SetTexID(ImTextureID.Null);
            tex->BackendUserData = null;
        }
        tex->SetStatus(ImTextureStatus.Destroyed);
    }

    private unsafe static void UpdateTexture(ImTextureData* tex)
    {
        if (tex->Status == ImTextureStatus.Ok)
            return;
        Data* bd = GetBackendData();
        InitInfo* v = &bd->VulkanInitInfo;
        Result err;

        if (tex->Status == ImTextureStatus.WantCreate)
        {
            // Create and upload new texture to graphics system
            //Log.Debug(string.Format("UpdateTexture #%03d: WantCreate %dx%d\n", tex->UniqueID, tex->Width, tex->Height));
            if (!tex->TexID.IsNull || tex->BackendUserData != null)
                throw new InvalidOperationException("Expected TexID to be null and BackendUserData to be null.");
            if (tex->Format != ImTextureFormat.Rgba32)
                throw new InvalidOperationException("Expected texture format RGBA32.");
            Texture* backend_tex = (Texture*)ImGui.MemAlloc((uint)sizeof(Texture));
            *backend_tex = default;

            // Create the Image:
            {
                ImageCreateInfo info = new ImageCreateInfo
                {
                    SType = StructureType.ImageCreateInfo,
                    ImageType = ImageType.Type2D,
                    Format = Format.R8G8B8A8Unorm,
                    Extent = new Extent3D
                    {
                        Width = (uint)tex->Width,
                        Height = (uint)tex->Height,
                        Depth = 1
                    },
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Samples = SampleCountFlags.Count1Bit,
                    Tiling = ImageTiling.Optimal,
                    Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                    SharingMode = SharingMode.Exclusive,
                    InitialLayout = ImageLayout.Undefined
                };
                err = SharedAPI.Vulkan.CreateImage(v->Device, &info, v->Allocator, &backend_tex->Image);
                CheckVkResult(err);
                MemoryRequirements req;
                SharedAPI.Vulkan.GetImageMemoryRequirements(v->Device, backend_tex->Image, &req);
                MemoryAllocateInfo alloc_info = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = Math.Max(v->MinAllocationSize, req.Size),
                    MemoryTypeIndex = MemoryType(MemoryPropertyFlags.DeviceLocalBit, req.MemoryTypeBits)
                };
                err = SharedAPI.Vulkan.AllocateMemory(v->Device, &alloc_info, v->Allocator, &backend_tex->Memory);
                CheckVkResult(err);
                err = SharedAPI.Vulkan.BindImageMemory(v->Device, backend_tex->Image, backend_tex->Memory, 0);
                CheckVkResult(err);
            }

            // Create the Image View:
            {
                ImageViewCreateInfo info = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = backend_tex->Image,
                    ViewType = ImageViewType.Type2D,
                    Format = Format.R8G8B8A8Unorm,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LevelCount = 1,
                        LayerCount = 1
                    }
                };
                err = SharedAPI.Vulkan.CreateImageView(v->Device, &info, v->Allocator, &backend_tex->ImageView);
                CheckVkResult(err);
            }

            // Create the Descriptor Set
            backend_tex->DescriptorSet = AddTexture(bd->TexSamplerLinear, backend_tex->ImageView, ImageLayout.ShaderReadOnlyOptimal);

            // Store identifiers
            tex->SetTexID((ImTextureID)backend_tex->DescriptorSet.Handle);
            tex->BackendUserData = backend_tex;
        }

        if (tex->Status == ImTextureStatus.WantCreate || tex->Status == ImTextureStatus.WantUpdates)
        {
            Texture* backend_tex = (Texture*)tex->BackendUserData;

            // Update full texture or selected blocks. We only ever write to textures regions which have never been used before!
            // This backend choose to use tex->UpdateRect but you can use tex->Updates[] to upload individual regions.
            // We could use the smaller rect on _WantCreate but using the full rect allows us to clear the texture.
            int upload_x = tex->Status == ImTextureStatus.WantCreate ? 0 : tex->UpdateRect.X;
            int upload_y = tex->Status == ImTextureStatus.WantCreate ? 0 : tex->UpdateRect.Y;
            int upload_w = tex->Status == ImTextureStatus.WantCreate ? tex->Width : tex->UpdateRect.W;
            int upload_h = tex->Status == ImTextureStatus.WantCreate ? tex->Height : tex->UpdateRect.H;

            // Create the Upload Buffer:
            DeviceMemory upload_buffer_memory;

            Buffer upload_buffer;
            DeviceSize upload_pitch = (DeviceSize)(upload_w * tex->BytesPerPixel);
            DeviceSize upload_size = AlignBufferSize((DeviceSize)upload_h * upload_pitch, bd->NonCoherentAtomSize);
            {
                BufferCreateInfo buffer_info = new BufferCreateInfo
                {
                    SType = StructureType.BufferCreateInfo,
                    Size = upload_size,
                    Usage = BufferUsageFlags.TransferSrcBit,
                    SharingMode = SharingMode.Exclusive
                };
                err = SharedAPI.Vulkan.CreateBuffer(v->Device, &buffer_info, v->Allocator, &upload_buffer);
                CheckVkResult(err);
                MemoryRequirements req;
                SharedAPI.Vulkan.GetBufferMemoryRequirements(v->Device, upload_buffer, &req);
                bd->BufferMemoryAlignment = bd->BufferMemoryAlignment > req.Alignment ? bd->BufferMemoryAlignment : req.Alignment;
                MemoryAllocateInfo alloc_info = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = Math.Max(v->MinAllocationSize, req.Size),
                    MemoryTypeIndex = MemoryType(MemoryPropertyFlags.HostVisibleBit, req.MemoryTypeBits)
                };
                err = SharedAPI.Vulkan.AllocateMemory(v->Device, &alloc_info, v->Allocator, &upload_buffer_memory);
                CheckVkResult(err);
                err = SharedAPI.Vulkan.BindBufferMemory(v->Device, upload_buffer, upload_buffer_memory, 0);
                CheckVkResult(err);
            }

            // Upload to Buffer:
            {
                byte* map = null;
                err = SharedAPI.Vulkan.MapMemory(v->Device, upload_buffer_memory, 0, upload_size, 0, (void**)(&map));
                CheckVkResult(err);
                for (int y = 0; y < upload_h; y++)
                {
                    void* src = tex->GetPixelsAt(upload_x, upload_y + y);
                    void* dst = map + upload_pitch * (ulong)y;
                    System.Buffer.MemoryCopy(src, dst, (long)upload_pitch, (long)upload_pitch);
                }
                MappedMemoryRange range = new MappedMemoryRange
                {
                    SType = StructureType.MappedMemoryRange,
                    Memory = upload_buffer_memory,
                    Size = upload_size
                };
                err = SharedAPI.Vulkan.FlushMappedMemoryRanges(v->Device, 1, &range);
                CheckVkResult(err);
                SharedAPI.Vulkan.UnmapMemory(v->Device, upload_buffer_memory);
            }

            // Start command buffer
            {
                err = SharedAPI.Vulkan.ResetCommandPool(v->Device, bd->TexCommandPool, 0);
                CheckVkResult(err);
                CommandBufferBeginInfo begin_info = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit
                };
                err = SharedAPI.Vulkan.BeginCommandBuffer(bd->TexCommandBuffer, &begin_info);
                CheckVkResult(err);
            }

            // Copy to Image:
            {
                BufferMemoryBarrier upload_barrier = new BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = AccessFlags.HostWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = upload_buffer,
                    Offset = 0,
                    Size = upload_size
                };

                ImageMemoryBarrier copy_barrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = tex->Status == ImTextureStatus.WantCreate ? ImageLayout.Undefined : ImageLayout.ShaderReadOnlyOptimal,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = backend_tex->Image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LevelCount = 1,
                        LayerCount = 1
                    }
                };
                SharedAPI.Vulkan.CmdPipelineBarrier(bd->TexCommandBuffer, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.HostBit, PipelineStageFlags.TransferBit, 0, 0, null, 1, &upload_barrier, 1, &copy_barrier);

                BufferImageCopy region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1
                    },
                    ImageExtent = new Extent3D
                    {
                        Width = (uint)upload_w,
                        Height = (uint)upload_h,
                        Depth = 1
                    },
                    ImageOffset = new Offset3D
                    {
                        X = upload_x,
                        Y = upload_y
                    }
                };
                SharedAPI.Vulkan.CmdCopyBufferToImage(bd->TexCommandBuffer, upload_buffer, backend_tex->Image, ImageLayout.TransferDstOptimal, 1, &region);

                ImageMemoryBarrier use_barrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = backend_tex->Image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LevelCount = 1,
                        LayerCount = 1
                    }
                };
                SharedAPI.Vulkan.CmdPipelineBarrier(bd->TexCommandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, &use_barrier);
            }

            // End command buffer
            {
                SubmitInfo end_info = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &bd->TexCommandBuffer
                };
                err = SharedAPI.Vulkan.EndCommandBuffer(bd->TexCommandBuffer);
                CheckVkResult(err);
                err = SharedAPI.Vulkan.QueueSubmit(v->Queue, 1, &end_info, default);
                CheckVkResult(err);
            }

            err = SharedAPI.Vulkan.QueueWaitIdle(v->Queue); // FIXME-OPT: Suboptimal!
            CheckVkResult(err);
            SharedAPI.Vulkan.DestroyBuffer(v->Device, upload_buffer, v->Allocator);
            SharedAPI.Vulkan.FreeMemory(v->Device, upload_buffer_memory, v->Allocator);

            tex->SetStatus(ImTextureStatus.Ok);
        }

        if (tex->Status == ImTextureStatus.WantDestroy && tex->UnusedFrames >= (int)bd->VulkanInitInfo.ImageCount)
            DestroyTexture(tex);
    }

    private unsafe static void CreateShaderModules(Device device, AllocationCallbacks* allocator)
    {
        Data* bd = GetBackendData();
        InitInfo* v = &bd->VulkanInitInfo;
        if (bd->ShaderModuleVert.Handle == 0)
        {
            ShaderModuleCreateInfo default_vert_info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(sizeof(uint) * __glsl_shader_vert_spv.Length)
            };
            fixed (uint* pCode = __glsl_shader_vert_spv)
                default_vert_info.PCode = pCode;
            ShaderModuleCreateInfo* p_vert_info = v->CustomShaderVertCreateInfo.SType == StructureType.ShaderModuleCreateInfo ? &v->CustomShaderVertCreateInfo : &default_vert_info;
            Result err = SharedAPI.Vulkan.CreateShaderModule(device, p_vert_info, allocator, &bd->ShaderModuleVert);
            CheckVkResult(err);
        }
        if (bd->ShaderModuleFrag.Handle == 0)
        {
            ShaderModuleCreateInfo default_frag_info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(sizeof(uint) * __glsl_shader_frag_spv.Length)
            };
            fixed (uint* pCode = __glsl_shader_frag_spv)
                default_frag_info.PCode = pCode;
            ShaderModuleCreateInfo* p_frag_info = v->CustomShaderFragCreateInfo.SType == StructureType.ShaderModuleCreateInfo ? &v->CustomShaderFragCreateInfo : &default_frag_info;
            Result err = SharedAPI.Vulkan.CreateShaderModule(device, p_frag_info, allocator, &bd->ShaderModuleFrag);
            CheckVkResult(err);
        }
    }

    private static unsafe Pipeline CreatePipeline(Device device, AllocationCallbacks* allocator, PipelineCache pipelineCache, PipelineInfo* info)
    {
        Data* bd = GetBackendData();
        CreateShaderModules(device, allocator);

        PipelineShaderStageCreateInfo* stage = stackalloc PipelineShaderStageCreateInfo[2]
        {
            new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = bd->ShaderModuleVert,
                PName = (byte*)_entryMain
            },
            new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = bd->ShaderModuleFrag,
                PName = (byte*)_entryMain
            }
        };

        VertexInputBindingDescription* binding_desc = stackalloc VertexInputBindingDescription[1]
        {
            new VertexInputBindingDescription
            {
                Stride = (uint)sizeof(ImDrawVert),
                InputRate = VertexInputRate.Vertex
            }
        };

        VertexInputAttributeDescription* attribute_desc = stackalloc VertexInputAttributeDescription[3]
        {
            new VertexInputAttributeDescription
            {
                Location = 0,
                Binding = binding_desc[0].Binding,
                Format = Format.R32G32Sfloat,
                Offset = (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.Pos))
            },
            new VertexInputAttributeDescription
            {
                Location = 1,
                Binding = binding_desc[0].Binding,
                Format = Format.R32G32Sfloat,
                Offset = (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.Uv))
            },
            new VertexInputAttributeDescription
            {
                Location = 2,
                Binding = binding_desc[0].Binding,
                Format = Format.R8G8B8A8Unorm,
                Offset = (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.Col))
            }
        };

        PipelineVertexInputStateCreateInfo vertex_info = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = binding_desc,
            VertexAttributeDescriptionCount = 3,
            PVertexAttributeDescriptions = attribute_desc
        };

        PipelineInputAssemblyStateCreateInfo ia_info = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList
        };

        PipelineViewportStateCreateInfo viewport_info = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1
        };

        PipelineRasterizationStateCreateInfo raster_info = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            LineWidth = 1.0f
        };

        PipelineMultisampleStateCreateInfo ms_info = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = info->MSAASamples != 0 ? info->MSAASamples : SampleCountFlags.Count1Bit
        };

        PipelineColorBlendAttachmentState* color_attachment = stackalloc PipelineColorBlendAttachmentState[1]
        {
            new PipelineColorBlendAttachmentState
            {
                BlendEnable = Vk.True,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
            }
        };

        PipelineDepthStencilStateCreateInfo depth_info = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo
        };

        PipelineColorBlendStateCreateInfo blend_info = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = color_attachment
        };

        DynamicState* dynamic_states = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
        PipelineDynamicStateCreateInfo dynamic_state = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamic_states
        };

        GraphicsPipelineCreateInfo create_info = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            Flags = bd->PipelineCreateFlags,
            StageCount = 2,
            PStages = stage,
            PVertexInputState = &vertex_info,
            PInputAssemblyState = &ia_info,
            PViewportState = &viewport_info,
            PRasterizationState = &raster_info,
            PMultisampleState = &ms_info,
            PDepthStencilState = &depth_info,
            PColorBlendState = &blend_info,
            PDynamicState = &dynamic_state,
            Layout = bd->PipelineLayout,
            RenderPass = info->RenderPass,
            Subpass = info->Subpass
        };

        Pipeline pipeline;
        Result err = SharedAPI.Vulkan.CreateGraphicsPipelines(device, pipelineCache, 1, &create_info, allocator, &pipeline);
        CheckVkResult(err);
        return pipeline;
    }

    private static unsafe bool CreateDeviceObjects()
    {
        Data* bd = GetBackendData();
        InitInfo* v = &bd->VulkanInitInfo;
        Result err;

        if (bd->TexSamplerLinear.Handle == 0)
        {
            // Bilinear sampling is required by default. Set 'io.Fonts->Flags |= ImFontAtlasFlags_NoBakedLines' or 'style.AntiAliasedLinesUseTex = false' to allow point/nearest sampling.
            SamplerCreateInfo info = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Linear,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MinLod = -1000,
                MaxLod = 1000,
                MaxAnisotropy = 1.0f
            };
            err = SharedAPI.Vulkan.CreateSampler(v->Device, &info, v->Allocator, out bd->TexSamplerLinear);
            CheckVkResult(err);
        }

        if (bd->DescriptorSetLayout.Handle == 0)
        {
            DescriptorSetLayoutBinding* binding = stackalloc DescriptorSetLayoutBinding[1]
            {
                new DescriptorSetLayoutBinding
                {
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = 1,
                    StageFlags = ShaderStageFlags.FragmentBit
                }
            };
            DescriptorSetLayoutCreateInfo info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = binding
            };
            err = SharedAPI.Vulkan.CreateDescriptorSetLayout(v->Device, &info, v->Allocator, out bd->DescriptorSetLayout);
            CheckVkResult(err);
        }

        if (v->DescriptorPoolSize != 0)
        {
            if (v->DescriptorPoolSize < IMGUI_IMPL_VULKAN_MINIMUM_IMAGE_SAMPLER_POOL_SIZE)
                throw new InvalidOperationException($"DescriptorPoolSize must be at least {IMGUI_IMPL_VULKAN_MINIMUM_IMAGE_SAMPLER_POOL_SIZE}.");
            DescriptorPoolSize pool_size = new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = v->DescriptorPoolSize
            };
            DescriptorPoolCreateInfo pool_info = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                MaxSets = v->DescriptorPoolSize,
                PoolSizeCount = 1,
                PPoolSizes = &pool_size
            };

            err = SharedAPI.Vulkan.CreateDescriptorPool(v->Device, &pool_info, v->Allocator, out bd->DescriptorPool);
            CheckVkResult(err);
        }

        if (bd->PipelineLayout.Handle == 0)
        {
            // Constants: we are using 'vec2 offset' and 'vec2 scale' instead of a full 3d projection matrix
            PushConstantRange* push_constants = stackalloc PushConstantRange[1]
            {
                new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.VertexBit,
                    Offset = sizeof(float) * 0,
                    Size = sizeof(float) * 4
                }
            };
            DescriptorSetLayout* set_layout = stackalloc DescriptorSetLayout[1] { bd->DescriptorSetLayout };
            PipelineLayoutCreateInfo layout_info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = set_layout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = push_constants
            };
            err = SharedAPI.Vulkan.CreatePipelineLayout(v->Device, &layout_info, v->Allocator, out bd->PipelineLayout);
            CheckVkResult(err);
        }

        // Create pipeline
        if (v->PipelineInfoMain.RenderPass.Handle != 0)
            CreateMainPipeline(&v->PipelineInfoMain);

        // Create command pool/buffer for texture upload
        if (bd->TexCommandPool.Handle == 0)
        {
            CommandPoolCreateInfo info = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = 0,
                QueueFamilyIndex = v->QueueFamily
            };
            err = SharedAPI.Vulkan.CreateCommandPool(v->Device, &info, v->Allocator, out bd->TexCommandPool);
            CheckVkResult(err);
        }
        if (bd->TexCommandBuffer.Handle == 0)
        {
            CommandBufferAllocateInfo info = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = bd->TexCommandPool,
                CommandBufferCount = 1
            };
            err = SharedAPI.Vulkan.AllocateCommandBuffers(v->Device, &info, out bd->TexCommandBuffer);
            CheckVkResult(err);
        }

        return true;
    }

    private unsafe static void CreateMainPipeline(PipelineInfo* pipeline_info_in)
    {
        Data* bd = GetBackendData();
        InitInfo* v = &bd->VulkanInitInfo;
        if (bd->Pipeline.Handle != 0)
        {
            SharedAPI.Vulkan.DestroyPipeline(v->Device, bd->Pipeline, v->Allocator);
            bd->Pipeline = default;
        }

        PipelineInfo* pipeline_info = &v->PipelineInfoMain;
        if (pipeline_info != pipeline_info_in)
            *pipeline_info = *pipeline_info_in;

        bd->Pipeline = CreatePipeline(v->Device, v->Allocator, v->PipelineCache, pipeline_info);
    }

    private unsafe static void DestroyDeviceObjects()
    {
        Data* bd = GetBackendData();
        InitInfo* v = &bd->VulkanInitInfo;
        DestroyWindowRenderBuffers(v->Device, &bd->MainWindowRenderBuffers, v->Allocator);

        // Destroy all textures
        ImGuiPlatformIOPtr platform_io = ImGui.GetPlatformIO();
        for (int i = 0; i < platform_io.Textures.Size; i++)
        {
            ImTextureData* tex = platform_io.Textures.Data[i];
            if (tex->RefCount == 1)
                DestroyTexture(tex);
        }

        if (bd->TexCommandBuffer.Handle != 0) { SharedAPI.Vulkan.FreeCommandBuffers(v->Device, bd->TexCommandPool, 1, &bd->TexCommandBuffer); bd->TexCommandBuffer = default; }
        if (bd->TexCommandPool.Handle != 0) { SharedAPI.Vulkan.DestroyCommandPool(v->Device, bd->TexCommandPool, v->Allocator); bd->TexCommandPool = default; }
        if (bd->TexSamplerLinear.Handle != 0) { SharedAPI.Vulkan.DestroySampler(v->Device, bd->TexSamplerLinear, v->Allocator); bd->TexSamplerLinear = default; }
        if (bd->ShaderModuleVert.Handle != 0) { SharedAPI.Vulkan.DestroyShaderModule(v->Device, bd->ShaderModuleVert, v->Allocator); bd->ShaderModuleVert = default; }
        if (bd->ShaderModuleFrag.Handle != 0) { SharedAPI.Vulkan.DestroyShaderModule(v->Device, bd->ShaderModuleFrag, v->Allocator); bd->ShaderModuleFrag = default; }
        if (bd->DescriptorSetLayout.Handle != 0) { SharedAPI.Vulkan.DestroyDescriptorSetLayout(v->Device, bd->DescriptorSetLayout, v->Allocator); bd->DescriptorSetLayout = default; }
        if (bd->PipelineLayout.Handle != 0) { SharedAPI.Vulkan.DestroyPipelineLayout(v->Device, bd->PipelineLayout, v->Allocator); bd->PipelineLayout = default; }
        if (bd->Pipeline.Handle != 0) { SharedAPI.Vulkan.DestroyPipeline(v->Device, bd->Pipeline, v->Allocator); bd->Pipeline = default; }
        if (bd->DescriptorPool.Handle != 0) { SharedAPI.Vulkan.DestroyDescriptorPool(v->Device, bd->DescriptorPool, v->Allocator); bd->DescriptorPool = default; }
    }

    // If unspecified by user, assume that ApiVersion == HeaderVersion
    // We don't care about other versions than 1.3 for our checks, so don't need to make this exhaustive (e.g. with all #ifdef VK_VERSION_1_X checks)
    public static uint GetDefaultApiVersion() => Vk.Version10;

    public unsafe static bool Init(InitInfo* info)
    {
        if (info->ApiVersion == 0)
            info->ApiVersion = GetDefaultApiVersion();



        ImGuiIOPtr io = ImGui.GetIO();
        if (io.BackendRendererUserData != null)
            throw new InvalidOperationException("Already initialized a renderer backend!");

        // Setup backend capabilities flags
        Data* bd = (Data*)ImGui.MemAlloc((uint)sizeof(Data));
        *bd = new();
        io.BackendRendererUserData = bd;
        io.BackendRendererName = (byte*)Marshal.StringToHGlobalAnsi($"imgui_impl_vulkan_{DearImGuiInjectionCore.BackendVersion}");
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;  // We can honor the ImDrawCmd::VtxOffset field, allowing for large meshes.
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;   // We can honor ImGuiPlatformIO::Textures[] requests during render.

        // Sanity checks
        if (info->Instance.Handle == 0)
            throw new InvalidOperationException("Vulkan instance must be a valid, non-null handle.");
        if (info->PhysicalDevice.Handle == 0)
            throw new InvalidOperationException("Vulkan physical device must be a valid, non-null handle.");
        if (info->Device.Handle == 0)
            throw new InvalidOperationException("Vulkan device must be a valid, non-null handle.");
        if (info->Queue.Handle == 0)
            throw new InvalidOperationException("Vulkan queue must be a valid, non-null handle.");
        if (info->MinImageCount < 2)
            throw new InvalidOperationException("MinImageCount must be at least 2.");
        if (info->ImageCount < info->MinImageCount)
            throw new InvalidOperationException("ImageCount must be greater than or equal to MinImageCount.");
        if (info->DescriptorPool.Handle != 0) // Either DescriptorPool or DescriptorPoolSize must be set, not both!
        {
            if (info->DescriptorPoolSize != 0)
                throw new InvalidOperationException("DescriptorPool and DescriptorPoolSize are mutually exclusive; only one may be specified.");
        }
        else
        {
            if (info->DescriptorPoolSize == 0)
                throw new InvalidOperationException("DescriptorPoolSize must be greater than zero when no DescriptorPool is provided.");
        }

        bd->VulkanInitInfo = *info;

        PhysicalDeviceProperties properties;
        SharedAPI.Vulkan.GetPhysicalDeviceProperties(info->PhysicalDevice, &properties);
        bd->NonCoherentAtomSize = properties.Limits.NonCoherentAtomSize;

        if (!CreateDeviceObjects())
            throw new InvalidOperationException("ImGui_ImplVulkan_CreateDeviceObjects() failed!"); // <- Can't be hit yet.

        return true;
    }

    public unsafe static void Shutdown()
    {
        Data* bd = GetBackendData();
        if (bd == null)
            throw new InvalidOperationException("No renderer backend to shutdown, or already shutdown?");
        ImGuiIOPtr io = ImGui.GetIO();
        ImGuiPlatformIOPtr platform_io = ImGui.GetPlatformIO();

        DestroyDeviceObjects();
        Marshal.FreeHGlobal(_entryMain);

        Marshal.FreeHGlobal((IntPtr)io.BackendRendererName);
        io.BackendRendererName = null;
        io.BackendRendererUserData = null;
        io.BackendFlags &= ~(ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures);
        platform_io.ClearRendererHandlers();
        ImGui.MemFree(bd);
    }

    public unsafe static void NewFrame()
    {
        Data* bd = GetBackendData();
        if (bd == null)
            throw new InvalidOperationException("Context or backend not initialized! Did you call ImGui_ImplVulkan_Init()?");
        _ = bd;
    }

    // Register a texture by creating a descriptor
    // FIXME: This is experimental in the sense that we are unsure how to best design/tackle this problem, please post to https://github.com/ocornut/imgui/pull/914 if you have suggestions.
    unsafe static DescriptorSet AddTexture(Sampler sampler, ImageView image_view, ImageLayout image_layout)
    {
        Data* bd = GetBackendData();
        InitInfo* v = &bd->VulkanInitInfo;
        DescriptorPool pool = bd->DescriptorPool.Handle != 0 ? bd->DescriptorPool : v->DescriptorPool;

        // Create Descriptor Set:
        DescriptorSet descriptor_set;
        {
            DescriptorSetAllocateInfo alloc_info = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &bd->DescriptorSetLayout
            };
            Result err = SharedAPI.Vulkan.AllocateDescriptorSets(v->Device, &alloc_info, &descriptor_set);
            CheckVkResult(err);
        }

        // Update the Descriptor Set:
        {
            DescriptorImageInfo* desc_image = stackalloc DescriptorImageInfo[1]
            {
                new DescriptorImageInfo
                {
                    Sampler = sampler,
                    ImageView = image_view,
                    ImageLayout = image_layout
                }
            };
            WriteDescriptorSet* write_desc = stackalloc WriteDescriptorSet[1]
            {
                new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptor_set,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = desc_image
                }
            };
            SharedAPI.Vulkan.UpdateDescriptorSets(v->Device, 1, write_desc, 0, null);
        }
        return descriptor_set;
    }

    private unsafe static void RemoveTexture(DescriptorSet descriptor_set)
    {
        Data* bd = GetBackendData();
        InitInfo* v = &bd->VulkanInitInfo;
        DescriptorPool pool = bd->DescriptorPool.Handle != 0 ? bd->DescriptorPool : v->DescriptorPool;
        SharedAPI.Vulkan.FreeDescriptorSets(v->Device, pool, 1, &descriptor_set);
    }

    private unsafe static void DestroyFrameRenderBuffers(Device device, FrameRenderBuffers* buffers, AllocationCallbacks* allocator)
    {
        if (buffers->VertexBuffer.Handle != 0) { SharedAPI.Vulkan.DestroyBuffer(device, buffers->VertexBuffer, allocator); buffers->VertexBuffer = default; }
        if (buffers->VertexBufferMemory.Handle != 0) { SharedAPI.Vulkan.FreeMemory(device, buffers->VertexBufferMemory, allocator); buffers->VertexBufferMemory = default; }
        if (buffers->IndexBuffer.Handle != 0) { SharedAPI.Vulkan.DestroyBuffer(device, buffers->IndexBuffer, allocator); buffers->IndexBuffer = default; }
        if (buffers->IndexBufferMemory.Handle != 0) { SharedAPI.Vulkan.FreeMemory(device, buffers->IndexBufferMemory, allocator); buffers->IndexBufferMemory = default; }
        buffers->VertexBufferSize = 0;
        buffers->IndexBufferSize = 0;
    }

    private unsafe static void DestroyWindowRenderBuffers(Device device, WindowRenderBuffers* buffers, AllocationCallbacks* allocator)
    {
        FrameRenderBuffers* frb = buffers->FrameRenderBuffers.Data;
        for (int n = 0; n < buffers->Count; n++)
            DestroyFrameRenderBuffers(device, frb + n, allocator);
        buffers->FrameRenderBuffers.Clear();
        buffers->Index = 0;
        buffers->Count = 0;
    }
}
