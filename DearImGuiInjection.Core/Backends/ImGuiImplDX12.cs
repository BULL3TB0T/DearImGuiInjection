using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

using ImDrawIdx = ushort;
using Buffer = System.Buffer;
using Feature = Silk.NET.DXGI.Feature;

namespace DearImGuiInjection.Backends;

internal static class ImGuiImplDX12
{
    private const uint D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING = 5768;

    private static readonly IntPtr _entryMain = Marshal.StringToHGlobalAnsi("main");

    private const string _vertexShader =
        @"cbuffer vertexBuffer : register(b0) \
        {\
            float4x4 ProjectionMatrix; \
        };\
        struct VS_INPUT\
        {\
            float2 pos : POSITION;\
            float4 col : COLOR0;\
            float2 uv  : TEXCOORD0;\
        };\
        \
        struct PS_INPUT\
        {\
            float4 pos : SV_POSITION;\
            float4 col : COLOR0;\
            float2 uv  : TEXCOORD0;\
        };\
        \
        PS_INPUT main(VS_INPUT input)\
        {\
            PS_INPUT output;\
            output.pos = mul( ProjectionMatrix, float4(input.pos.xy, 0.f, 1.f));\
            output.col = input.col;\
            output.uv  = input.uv;\
            return output;\
        }";
    private static readonly IntPtr _vsSrc = Marshal.StringToHGlobalAnsi(_vertexShader);
    private static readonly IntPtr _vsTarget = Marshal.StringToHGlobalAnsi("vs_5_0");

    private static readonly IntPtr _inputElementPos = Marshal.StringToHGlobalAnsi("POSITION");
    private static readonly IntPtr _inputElementUv = Marshal.StringToHGlobalAnsi("TEXCOORD");
    private static readonly IntPtr _inputElementCol = Marshal.StringToHGlobalAnsi("COLOR");
    private unsafe static readonly InputElementDesc[] _inputElements =
    {
        new((byte*)_inputElementPos, 0, Format.FormatR32G32Float, 0,
            (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.Pos)), InputClassification.PerVertexData, 0),
        new((byte*)_inputElementUv, 0, Format.FormatR32G32Float, 0,
            (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.Uv)), InputClassification.PerVertexData, 0),
        new((byte*)_inputElementCol, 0, Format.FormatR8G8B8A8Unorm, 0,
            (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.Col)), InputClassification.PerVertexData, 0),
    };

    private const string _pixelShader =
        @"struct PS_INPUT\
        {\
            float4 pos : SV_POSITION;\
            float4 col : COLOR0;\
            float2 uv  : TEXCOORD0;\
        };\
        SamplerState sampler0 : register(s0);\
        Texture2D texture0 : register(t0);\
        \
        float4 main(PS_INPUT input) : SV_Target\
        {\
            float4 out_col = input.col * texture0.Sample(sampler0, input.uv); \
            return out_col; \
        }";
    private static readonly IntPtr _psSrc = Marshal.StringToHGlobalAnsi(_pixelShader);
    private static readonly IntPtr _psTarget = Marshal.StringToHGlobalAnsi("ps_5_0");

    // Initialization data, for ImGui_ImplDX12_Init()
    public unsafe struct InitInfo
    {
        public ID3D12Device* Device;
        public ID3D12CommandQueue* CommandQueue;       // Command queue used for queuing texture uploads.
        public int NumFramesInFlight;
        public Format RTVFormat;          // RenderTarget format.
        public Format DSVFormat;          // DepthStencilView format.
        public void* UserData;

        // Allocating SRV descriptors for textures is up to the application, so we provide callbacks.
        // (current version of the backend will only allocate one descriptor, from 1.92 the backend will need to allocate more)
        public ID3D12DescriptorHeap* SrvDescriptorHeap;
        public delegate* unmanaged[Cdecl]<InitInfo*, CpuDescriptorHandle*, GpuDescriptorHandle*, void> SrvDescriptorAllocFn;
        public delegate* unmanaged[Cdecl]<InitInfo*, CpuDescriptorHandle, GpuDescriptorHandle, void> SrvDescriptorFreeFn;
    }

    // DirectX12 data
    private unsafe struct ImDrawCallback
    {
        public static void* ResetRenderState = (void*)ImGui.ImDrawCallbackResetRenderState;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void UserCallback(ImDrawList* parent_list, ImDrawCmd* cmd);
    }

    private unsafe struct RenderState
    {
        public ID3D12Device* Device;
        public ID3D12GraphicsCommandList* CommandList;
    }

    private unsafe struct RenderBuffers
    {
        public ID3D12Resource* IndexBuffer;
        public ID3D12Resource* VertexBuffer;
        public int IndexBufferSize;
        public int VertexBufferSize;
    }

    private unsafe struct Texture
    {
        public ID3D12Resource* pTextureResource;
        public CpuDescriptorHandle hFontSrvCpuDescHandle;
        public GpuDescriptorHandle hFontSrvGpuDescHandle;
    }

    private unsafe struct Data
    {
        public InitInfo InitInfo;
        public ID3D12Device* pd3dDevice;
        public ID3D12RootSignature* pRootSignature;
        public ID3D12PipelineState* pPipelineState;
        public ID3D12CommandQueue* pCommandQueue;
        public bool commandQueueOwned;
        public Format RTVFormat;
        public Format DSVFormat;
        public ID3D12DescriptorHeap* pd3dSrvDescHeap;
        public ID3D12Fence* Fence;
        public ulong FenceLastSignaledValue;
        public IntPtr FenceEvent;
        public uint numFramesInFlight;

        public ID3D12CommandAllocator* pTexCmdAllocator;
        public ID3D12GraphicsCommandList* pTexCmdList;
        public ID3D12Resource* pTexUploadBuffer;
        public uint pTexUploadBufferSize;
        public void* pTexUploadBufferMapped;

        public RenderBuffers* pFrameResources;
        public uint frameIndex;
    }

    // Backend data stored in io.BackendRendererUserData to allow support for multiple Dear ImGui contexts
    // It is STRONGLY preferred that you use docking branch with multi-viewports (== single Dear ImGui context + multiple windows) instead of multiple Dear ImGui contexts.
    private unsafe static Data* GetBackendData() => (Data*)ImGui.GetIO().BackendRendererUserData;

    private unsafe struct VERTEX_CONSTANT_BUFFER
    {
        public const int ElementCount = 4 * 4;
        public const int ByteWidth = ElementCount * sizeof(float);

        public fixed float mvp[ElementCount];
    }

    // Functions
    private unsafe static void SetupRenderState(ImDrawData* draw_data, ID3D12GraphicsCommandList* command_list, RenderBuffers* fr)
    {
        Data* bd = GetBackendData();

        // Setup orthographic projection matrix into our constant buffer
        // Our visible imgui space lies from draw_data->DisplayPos (top left) to draw_data->DisplayPos+data_data->DisplaySize (bottom right).
        VERTEX_CONSTANT_BUFFER vertex_constant_buffer;
        {
            float L = draw_data->DisplayPos.X;
            float R = draw_data->DisplayPos.X + draw_data->DisplaySize.X;
            float T = draw_data->DisplayPos.Y;
            float B = draw_data->DisplayPos.Y + draw_data->DisplaySize.Y;
            float* mvp = stackalloc float[VERTEX_CONSTANT_BUFFER.ElementCount]
            {
                2.0f/(R-L),   0.0f,           0.0f,       0.0f,
                0.0f,         2.0f/(T-B),     0.0f,       0.0f,
                0.0f,         0.0f,           0.5f,       0.0f,
                (R+L)/(L-R),  (T+B)/(B-T),    0.5f,       1.0f,
            };
            Buffer.MemoryCopy(mvp, vertex_constant_buffer.mvp, VERTEX_CONSTANT_BUFFER.ByteWidth, VERTEX_CONSTANT_BUFFER.ByteWidth);
        }

        // Setup viewport
        Viewport vp = default;
        vp.Width = draw_data->DisplaySize.X * draw_data->FramebufferScale.X;
        vp.Height = draw_data->DisplaySize.Y * draw_data->FramebufferScale.Y;
        vp.MinDepth = 0.0f;
        vp.MaxDepth = 1.0f;
        vp.TopLeftX = vp.TopLeftY = 0.0f;
        command_list->RSSetViewports(1, &vp);

        // Bind shader and vertex buffers
        uint stride = (uint)sizeof(ImDrawVert);
        uint offset = 0;
        VertexBufferView vbv = default;
        vbv.BufferLocation = ((ID3D12Resource*)fr->VertexBuffer)->GetGPUVirtualAddress() + offset;
        vbv.SizeInBytes = (uint)(fr->VertexBufferSize * stride);
        vbv.StrideInBytes = stride;
        command_list->IASetVertexBuffers(0, 1, &vbv);
        IndexBufferView ibv = default;
        ibv.BufferLocation = ((ID3D12Resource*)fr->IndexBuffer)->GetGPUVirtualAddress();
        ibv.SizeInBytes = (uint)(fr->IndexBufferSize * sizeof(ImDrawIdx));
        ibv.Format = sizeof(ImDrawIdx) == 2 ? Format.FormatR16Uint : Format.FormatR32Uint;
        command_list->IASetIndexBuffer(&ibv);
        command_list->IASetPrimitiveTopology(Silk.NET.Core.Native.D3DPrimitiveTopology.D3D10PrimitiveTopologyTrianglelist);
        command_list->SetPipelineState(bd->pPipelineState);
        command_list->SetGraphicsRootSignature(bd->pRootSignature);
        command_list->SetGraphicsRoot32BitConstants(0, 16, &vertex_constant_buffer, 0);

        // Setup blend factor
        float* blend_factor = stackalloc float[4] { 0.0f, 0.0f, 0.0f, 0.0f };
        command_list->OMSetBlendFactor(blend_factor);
    }

    private unsafe static void SafeRelease<T>(ref T* res) where T : unmanaged
    {
        if (res != null)
            ((IUnknown*)res)->Release();
        res = null;
    }

    // Render function
    public unsafe static void RenderDrawData(ImDrawData* draw_data, ID3D12GraphicsCommandList* command_list)
    {
        // Avoid rendering when minimized
        if (draw_data->DisplaySize.X <= 0.0f || draw_data->DisplaySize.Y <= 0.0f)
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

        // FIXME: We are assuming that this only gets called once per frame!
        Data* bd = GetBackendData();
        bd->frameIndex = bd->frameIndex + 1;
        RenderBuffers* fr = &bd->pFrameResources[bd->frameIndex % bd->numFramesInFlight];

        // Create and grow vertex/index buffers if needed
        if (fr->VertexBuffer == null || fr->VertexBufferSize < draw_data->TotalVtxCount)
        {
            SafeRelease(ref fr->VertexBuffer);
            fr->VertexBufferSize = draw_data->TotalVtxCount + 5000;
            HeapProperties props = default;
            props.Type = HeapType.Upload;
            props.CPUPageProperty = CpuPageProperty.Unknown;
            props.MemoryPoolPreference = MemoryPool.Unknown;
            ResourceDesc desc = new ResourceDesc
            {
                Dimension = ResourceDimension.Buffer,
                Width = (ulong)(fr->VertexBufferSize * sizeof(ImDrawVert)),
                Height = 1,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = Format.FormatUnknown,
                SampleDesc = new SampleDesc(1),
                Layout = TextureLayout.LayoutRowMajor,
                Flags = ResourceFlags.None
            };
            ID3D12Resource* vertexBuffer;
            Guid riid = ID3D12Resource.Guid;
            if (bd->pd3dDevice->CreateCommittedResource(&props, HeapFlags.None, &desc, ResourceStates.GenericRead, null, &riid, (void**)&vertexBuffer) < 0)
                return;
            fr->VertexBuffer = vertexBuffer;
        }
        if (fr->IndexBuffer == null || fr->IndexBufferSize < draw_data->TotalIdxCount)
        {
            SafeRelease(ref fr->IndexBuffer);
            fr->IndexBufferSize = draw_data->TotalIdxCount + 10000;
            HeapProperties props = default;
            props.Type = HeapType.Upload;
            props.CPUPageProperty = CpuPageProperty.Unknown;
            props.MemoryPoolPreference = MemoryPool.Unknown;
            ResourceDesc desc = new ResourceDesc
            {
                Dimension = ResourceDimension.Buffer,
                Width = (ulong)(fr->IndexBufferSize * sizeof(ImDrawIdx)),
                Height = 1,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = Format.FormatUnknown,
                SampleDesc = new SampleDesc(1),
                Layout = TextureLayout.LayoutRowMajor,
                Flags = ResourceFlags.None
            };
            ID3D12Resource* indexBuffer;
            Guid riid = ID3D12Resource.Guid;
            if (bd->pd3dDevice->CreateCommittedResource(&props, HeapFlags.None, &desc, ResourceStates.GenericRead, null, &riid, (void**)&indexBuffer) < 0)
                return;
            fr->IndexBuffer = indexBuffer;
        }

        // Upload vertex/index data into a single contiguous GPU buffer
        // During Map() we specify a null read range (as per DX12 API, this is informational and for tooling only)
        void* vtx_resource, idx_resource;
        Range range = new Range
        {
            Begin = 0,
            End = 0
        };
        if (((ID3D12Resource*)fr->VertexBuffer)->Map(0, &range, &vtx_resource) != 0)
            return;
        if (((ID3D12Resource*)fr->IndexBuffer)->Map(0, &range, &idx_resource) != 0)
            return;
        ImDrawVert* vtx_dst = (ImDrawVert*)vtx_resource;
        ImDrawIdx* idx_dst = (ImDrawIdx*)idx_resource;
        for (int n = 0; n < draw_data->CmdListsCount; n++)
        {
            ImDrawList* draw_list = draw_data->CmdLists.Data[n];
            Buffer.MemoryCopy(draw_list->VtxBuffer.Data, vtx_dst, draw_list->VtxBuffer.Size * sizeof(ImDrawVert), draw_list->VtxBuffer.Size * sizeof(ImDrawVert));
            Buffer.MemoryCopy(draw_list->IdxBuffer.Data, idx_dst, draw_list->IdxBuffer.Size * sizeof(ImDrawIdx), draw_list->IdxBuffer.Size * sizeof(ImDrawIdx));
            vtx_dst += draw_list->VtxBuffer.Size;
            idx_dst += draw_list->IdxBuffer.Size;
        }

        // During Unmap() we specify the written range (as per DX12 API, this is informational and for tooling only)
        range.End = (nuint)((nint)vtx_dst - (nint)vtx_resource);
        if (range.End != (nuint)(draw_data->TotalVtxCount * sizeof(ImDrawVert)))
            throw new InvalidOperationException("Vertex buffer upload size mismatch.");
        ((ID3D12Resource*)fr->VertexBuffer)->Unmap(0, &range);
        range.End = (nuint)((nint)idx_dst - (nint)idx_resource);
        if (range.End != (nuint)(draw_data->TotalIdxCount * sizeof(ImDrawIdx)))
            throw new InvalidOperationException("Index buffer upload size mismatch.");
        ((ID3D12Resource*)fr->IndexBuffer)->Unmap(0, &range);

        // Setup desired DX state
        SetupRenderState(draw_data, command_list, fr);

        // Setup render state structure (for callbacks and custom texture bindings)
        ImGuiPlatformIOPtr platform_io = ImGui.GetPlatformIO();
        RenderState render_state = default;
        render_state.Device = bd->pd3dDevice;
        render_state.CommandList = command_list;
        platform_io.RendererRenderState = &render_state;

        // Render command lists
        // (Because we merged all buffers into a single one, we maintain our own offset into them)
        int global_vtx_offset = 0;
        int global_idx_offset = 0;
        Vector2 clip_off = draw_data->DisplayPos;
        Vector2 clip_scale = draw_data->FramebufferScale;
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
                        SetupRenderState(draw_data, command_list, fr);
                    else
                        ((delegate* unmanaged[Cdecl]<ImDrawList*, ImDrawCmd*, void>)pcmd->UserCallback)(draw_list, pcmd);
                }
                else
                {
                    // Project scissor/clipping rectangles into framebuffer space
                    Vector2 clip_min = new Vector2((pcmd->ClipRect.X - clip_off.X) * clip_scale.X, (pcmd->ClipRect.Y - clip_off.Y) * clip_scale.Y);
                    Vector2 clip_max = new Vector2((pcmd->ClipRect.Z - clip_off.X) * clip_scale.X, (pcmd->ClipRect.W - clip_off.Y) * clip_scale.Y);
                    if (clip_max.X <= clip_min.X || clip_max.Y <= clip_min.Y)
                        continue;

                    // Apply scissor/clipping rectangle
                    Box2D<int> r = new Box2D<int>((int)clip_min.X, (int)clip_min.Y, (int)clip_max.X, (int)clip_max.Y);
                    command_list->RSSetScissorRects(1, (Box2D<int>*)&r);

                    // Bind texture, Draw
                    GpuDescriptorHandle texture_handle = default;
                    texture_handle.Ptr = pcmd->GetTexID();
                    command_list->SetGraphicsRootDescriptorTable(1, texture_handle);
                    command_list->DrawIndexedInstanced(pcmd->ElemCount, 1, pcmd->IdxOffset + (uint)global_idx_offset, (int)pcmd->VtxOffset + global_vtx_offset, 0);
                }
            }
            global_idx_offset += draw_list->IdxBuffer.Size;
            global_vtx_offset += draw_list->VtxBuffer.Size;
        }
        platform_io.RendererRenderState = null;
    }

    private unsafe static void DestroyTexture(ImTextureData* tex)
    {
        Texture* backend_tex = (Texture*)tex->BackendUserData;
        if (backend_tex != null)
        {
            if (backend_tex->hFontSrvGpuDescHandle.Ptr != tex->GetTexID())
                throw new InvalidOperationException("Texture ID mismatch while destroying texture.");
            Data* bd = GetBackendData();
            bd->InitInfo.SrvDescriptorFreeFn(&bd->InitInfo, backend_tex->hFontSrvCpuDescHandle, backend_tex->hFontSrvGpuDescHandle);
            SafeRelease(ref backend_tex->pTextureResource);
            backend_tex->hFontSrvCpuDescHandle = new CpuDescriptorHandle
            {
                Ptr = 0
            };
            backend_tex->hFontSrvGpuDescHandle = new GpuDescriptorHandle
            {
                Ptr = 0
            };
            ImGui.MemFree(backend_tex);

            // Clear identifiers and mark as destroyed (in order to allow e.g. calling InvalidateDeviceObjects while running)
            tex->SetTexID(ImTextureID.Null);
            tex->BackendUserData = null;
        }
        tex->Status = ImTextureStatus.Destroyed;
    }

    private unsafe static void UpdateTexture(ImTextureData* tex)
    {
        Data* bd = GetBackendData();
        bool need_barrier_before_copy = true; // Do we need a resource barrier before we copy new data in?

        if (tex->Status == ImTextureStatus.WantCreate)
        {
            // Create and upload new texture to graphics system
            //Log.Debug(string.Format("UpdateTexture #%03d: WantCreate %dx%d\n", tex->UniqueID, tex->Width, tex->Height));
            if (!tex->TexID.IsNull || tex->BackendUserData != null)
                throw new InvalidOperationException("Expected TexID to be null and BackendUserData to be null.");
            if (tex->Format != ImTextureFormat.Rgba32)
                throw new InvalidOperationException("Expected texture format RGBA32.");
            Texture* backend_tex = (Texture*)ImGui.MemAlloc((nuint)sizeof(Texture));
            *backend_tex = default;
            bd->InitInfo.SrvDescriptorAllocFn(&bd->InitInfo, &backend_tex->hFontSrvCpuDescHandle, &backend_tex->hFontSrvGpuDescHandle);  // Allocate a desctriptor handle

            HeapProperties props = new HeapProperties
            {
                Type = HeapType.Default,
                CPUPageProperty = CpuPageProperty.Unknown,
                MemoryPoolPreference = MemoryPool.Unknown
            };

            ResourceDesc desc = new ResourceDesc
            {
                Dimension = ResourceDimension.Texture2D,
                Alignment = 0,
                Width = (ulong)tex->Width,
                Height = (uint)tex->Height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = Format.FormatR8G8B8A8Unorm,
                SampleDesc = new SampleDesc(1, 0),
                Layout = TextureLayout.LayoutUnknown,
                Flags = ResourceFlags.None
            };

            ID3D12Resource* pTexture = null;
            Guid riid = ID3D12Resource.Guid;
            bd->pd3dDevice->CreateCommittedResource(&props, HeapFlags.None, &desc,
                ResourceStates.CopyDest, null, &riid, (void**)&pTexture);

            ShaderResourceViewDesc srvDesc = new ShaderResourceViewDesc
            {
                Format = Format.FormatR8G8B8A8Unorm,
                ViewDimension = SrvDimension.Texture2D,
                Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING,
                Anonymous = new ShaderResourceViewDescUnion
                {
                    Texture2D = new Tex2DSrv
                    {
                        MipLevels = desc.MipLevels,
                        MostDetailedMip = 0
                    }
                }
            };
            bd->pd3dDevice->CreateShaderResourceView(pTexture, &srvDesc, backend_tex->hFontSrvCpuDescHandle);
            SafeRelease(ref backend_tex->pTextureResource);
            backend_tex->pTextureResource = pTexture;

            // Store identifiers
            tex->SetTexID(backend_tex->hFontSrvGpuDescHandle.Ptr);
            tex->BackendUserData = backend_tex;
            need_barrier_before_copy = false; // Because this is a newly-created texture it will be in D3D12_RESOURCE_STATE_COMMON and thus we don't need a barrier
            // We don't set tex->Status to ImTextureStatus_OK to let the code fallthrough below.
        }

        if (tex->Status == ImTextureStatus.WantCreate || tex->Status == ImTextureStatus.WantUpdates)
        {
            Texture* backend_tex = (Texture*)tex->BackendUserData;
            if (tex->Format != ImTextureFormat.Rgba32)
                throw new InvalidOperationException("Expected texture format RGBA32.");

            // We could use the smaller rect on _WantCreate but using the full rect allows us to clear the texture.
            // FIXME-OPT: Uploading single box even when using ImTextureStatus_WantUpdates. Could use tex->Updates[]
            // - Copy all blocks contiguously in upload buffer.
            // - Barrier before copy, submit all CopyTextureRegion(), barrier after copy.
            int upload_x = (tex->Status == ImTextureStatus.WantCreate) ? 0 : tex->UpdateRect.X;
            int upload_y = (tex->Status == ImTextureStatus.WantCreate) ? 0 : tex->UpdateRect.Y;
            int upload_w = (tex->Status == ImTextureStatus.WantCreate) ? tex->Width : tex->UpdateRect.W;
            int upload_h = (tex->Status == ImTextureStatus.WantCreate) ? tex->Height : tex->UpdateRect.H;

            // Update full texture or selected blocks. We only ever write to textures regions which have never been used before!
            // This backend choose to use tex->UpdateRect but you can use tex->Updates[] to upload individual regions.
            uint upload_pitch_src = (uint)(upload_w * tex->BytesPerPixel);
            uint upload_pitch_dst = (upload_pitch_src + 256u - 1u) & ~(256u - 1u);
            uint upload_size = upload_pitch_dst * (uint)upload_h;

            if (bd->pTexUploadBuffer == null || upload_size > bd->pTexUploadBufferSize)
            {
                Range range;
                if (bd->pTexUploadBufferMapped != null)
                {
                    range = new Range
                    {
                        Begin = 0,
                        End = bd->pTexUploadBufferSize
                    };
                    bd->pTexUploadBuffer->Unmap(0, &range);
                    bd->pTexUploadBufferMapped = null;
                }
                SafeRelease(ref bd->pTexUploadBuffer);

                ResourceDesc desc = new ResourceDesc
                {
                    Dimension = ResourceDimension.Buffer,
                    Alignment = 0,
                    Width = upload_size,
                    Height = 1,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    Format = Format.FormatUnknown,
                    SampleDesc = new SampleDesc(1, 0),
                    Layout = TextureLayout.LayoutRowMajor,
                    Flags = ResourceFlags.None
                };

                HeapProperties props = new HeapProperties
                {
                    Type = HeapType.Upload,
                    CPUPageProperty = CpuPageProperty.Unknown,
                    MemoryPoolPreference = MemoryPool.Unknown
                };

                Guid riid = ID3D12Resource.Guid;
                int hr = bd->pd3dDevice->CreateCommittedResource(&props, HeapFlags.None, &desc,
                    ResourceStates.GenericRead, null, &riid, (void**)&bd->pTexUploadBuffer);
                if (hr < 0)
                    throw new InvalidOperationException($"Texture upload buffer CreateCommittedResource() failed: 0x{hr:X8}");

                range = new Range
                {
                    Begin = 0,
                    End = upload_size
                };
                hr = bd->pTexUploadBuffer->Map(0, &range, &bd->pTexUploadBufferMapped);
                if (hr < 0)
                    throw new InvalidOperationException($"Texture upload buffer Map() failed: 0x{hr:X8}");
                bd->pTexUploadBufferSize = upload_size;
            }

            bd->pTexCmdAllocator->Reset();
            bd->pTexCmdList->Reset(bd->pTexCmdAllocator, null);
            ID3D12GraphicsCommandList* cmdList = bd->pTexCmdList;

            // Copy to upload buffer
            for (int y = 0; y < upload_h; y++)
            {
                void* dst = (void*)((nuint)bd->pTexUploadBufferMapped + (nuint)(y * upload_pitch_dst));
                void* src = tex->GetPixelsAt(upload_x, upload_y + y);
                Buffer.MemoryCopy(src, dst, upload_pitch_src, upload_pitch_src);
            }

            if (need_barrier_before_copy)
            {
                ResourceBarrier barrier = default;
                barrier.Type = ResourceBarrierType.Transition;
                barrier.Flags = ResourceBarrierFlags.None;
                barrier.Anonymous = new ResourceBarrierUnion
                {
                    Transition = new ResourceTransitionBarrier
                    {
                        PResource = backend_tex->pTextureResource,
                        Subresource = uint.MaxValue,
                        StateBefore = ResourceStates.PixelShaderResource,
                        StateAfter = ResourceStates.CopyDest
                    }
                };
                cmdList->ResourceBarrier(1, &barrier);
            }

            TextureCopyLocation srcLocation = default;
            TextureCopyLocation dstLocation = default;
            {
                srcLocation.PResource = bd->pTexUploadBuffer;
                srcLocation.Type = TextureCopyType.PlacedFootprint;
                srcLocation.Anonymous = new TextureCopyLocationUnion
                {
                    PlacedFootprint = new PlacedSubresourceFootprint
                    {
                        Footprint = new SubresourceFootprint
                        {
                            Format = Format.FormatR8G8B8A8Unorm,
                            Width = (uint)upload_w,
                            Height = (uint)upload_h,
                            Depth = 1,
                            RowPitch = upload_pitch_dst
                        }
                    }
                };
                dstLocation.PResource = backend_tex->pTextureResource;
                dstLocation.Type = TextureCopyType.SubresourceIndex;
                dstLocation.Anonymous = new TextureCopyLocationUnion
                {
                    SubresourceIndex = 0
                };
            }
            cmdList->CopyTextureRegion(&dstLocation, (uint)upload_x, (uint)upload_y, 0, &srcLocation, null);

            {
                ResourceBarrier barrier = default;
                barrier.Type = ResourceBarrierType.Transition;
                barrier.Flags = ResourceBarrierFlags.None;
                barrier.Anonymous = new ResourceBarrierUnion
                {
                    Transition = new ResourceTransitionBarrier
                    {
                        PResource = backend_tex->pTextureResource,
                        Subresource = uint.MaxValue,
                        StateBefore = ResourceStates.CopyDest,
                        StateAfter = ResourceStates.PixelShaderResource
                    }
                };
                cmdList->ResourceBarrier(1, &barrier);
            }

            int hr2 = cmdList->Close();
            if (hr2 < 0)
                throw new InvalidOperationException($"Texture upload command list Close() failed: 0x{hr2:X8}");
            ID3D12CommandQueue* cmdQueue = bd->pCommandQueue;
            ID3D12CommandList* cmdListPtr = (ID3D12CommandList*)cmdList;
            cmdQueue->ExecuteCommandLists(1, &cmdListPtr);
            hr2 = cmdQueue->Signal(bd->Fence, ++bd->FenceLastSignaledValue);
            if (hr2 < 0)
                throw new InvalidOperationException($"CommandQueue Signal() failed: 0x{hr2:X8}");

            // FIXME-OPT: Suboptimal?
            // - To remove this may need to create NumFramesInFlight x ImGui_ImplDX12_FrameContext in backend data (mimick docking version)
            // - Store per-frame in flight: upload buffer?
            // - Where do cmdList and cmdAlloc fit?
            bd->Fence->SetEventOnCompletion(bd->FenceLastSignaledValue, (void*)bd->FenceEvent);
            Kernel32.WaitForSingleObject(bd->FenceEvent, uint.MaxValue);

            tex->Status = ImTextureStatus.Ok;
        }

        if (tex->Status == ImTextureStatus.WantDestroy && tex->UnusedFrames >= (int)bd->numFramesInFlight)
            DestroyTexture(tex);
    }

    private unsafe static bool CreateDeviceObjects()
    {
        Data* bd = GetBackendData();
        if (bd == null || bd->pd3dDevice == null)
            return false;
        if (bd->pPipelineState != null)
            InvalidateDeviceObjects();

        // Create the root signature
        DescriptorRange descRange = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 0,
            RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 0
        };

        RootParameter* param = stackalloc RootParameter[2]
        {
            new RootParameter
            {
                ParameterType = RootParameterType.Type32BitConstants,
                Constants = new RootConstants
                {
                    ShaderRegister = 0,
                    RegisterSpace = 0,
                    Num32BitValues = 16
                },
                ShaderVisibility = ShaderVisibility.Vertex
            },
            new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = &descRange
                },
                ShaderVisibility = ShaderVisibility.Pixel
            }
        };

        // Bilinear sampling is required by default. Set 'io.Fonts->Flags |= ImFontAtlasFlags_NoBakedLines' or 'style.AntiAliasedLinesUseTex = false' to allow point/nearest sampling.
        StaticSamplerDesc* staticSampler = stackalloc StaticSamplerDesc[1];
        staticSampler[0] = new StaticSamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0.0f,
            MaxAnisotropy = 0,
            ComparisonFunc = ComparisonFunc.Always,
            BorderColor = StaticBorderColor.TransparentBlack,
            MinLOD = 0.0f,
            MaxLOD = 3.402823466e+38f,
            ShaderRegister = 0,
            RegisterSpace = 0,
            ShaderVisibility = ShaderVisibility.Pixel
        };

        RootSignatureDesc desc = new RootSignatureDesc
        {
            NumParameters = 2,
            PParameters = param,
            NumStaticSamplers = 1,
            PStaticSamplers = staticSampler,
            Flags =
                RootSignatureFlags.AllowInputAssemblerInputLayout |
                RootSignatureFlags.DenyHullShaderRootAccess |
                RootSignatureFlags.DenyDomainShaderRootAccess |
                RootSignatureFlags.DenyGeometryShaderRootAccess
        };

        ID3D10Blob* blob = null;
        if (SharedAPI.D3D12.SerializeRootSignature(&desc, D3DRootSignatureVersion.Version1, &blob, null) != 0)
            return false;

        Guid riid = ID3D12RootSignature.Guid;
        bd->pd3dDevice->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), &riid, (void**)&bd->pRootSignature);
        blob->Release();

        // By using D3DCompile() from <d3dcompiler.h> / d3dcompiler.lib, we introduce a dependency to a given version of d3dcompiler_XX.dll (see D3DCOMPILER_DLL_A)
        // If you would like to use this DX12 sample code but remove this dependency you can:
        //  1) compile once, save the compiled shader blobs into a file or source code and assign them to psoDesc.VS/PS [preferred solution]
        //  2) use code to detect any version of the DLL and grab a pointer to D3DCompile from the DLL.
        // See https://github.com/ocornut/imgui/pull/638 for sources and details.

        GraphicsPipelineStateDesc psoDesc = new GraphicsPipelineStateDesc
        {
            NodeMask = 1,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            PRootSignature = bd->pRootSignature,
            SampleMask = uint.MaxValue,
            NumRenderTargets = 1,
            DSVFormat = bd->DSVFormat,
            SampleDesc = new SampleDesc(1, 0),
            Flags = PipelineStateFlags.None
        };
        psoDesc.RTVFormats[0] = bd->RTVFormat;
        ID3D10Blob* vertexShaderBlob = null;
        ID3D10Blob* pixelShaderBlob = null;

        // Create the vertex shader
        {
            if (SharedAPI.D3DCompiler.Compile((void*)_vsSrc, (nuint)_vertexShader.Length, (byte*)0, null, null, (byte*)_entryMain, (byte*)_vsTarget, 0, 0, &vertexShaderBlob, null) < 0)
                return false; // NB: Pass ID3DBlob* pErrorBlob to D3DCompile() to get error showing in (const char*)pErrorBlob->GetBufferPointer(). Make sure to Release() the blob!

            psoDesc.VS = new ShaderBytecode
            {
                PShaderBytecode = vertexShaderBlob->GetBufferPointer(),
                BytecodeLength = vertexShaderBlob->GetBufferSize()
            };

            // Create the input layout
            fixed (InputElementDesc* inputElements = _inputElements)
                psoDesc.InputLayout = new InputLayoutDesc
                {
                    PInputElementDescs = inputElements,
                    NumElements = (uint)_inputElements.Length
                };
        }

        // Create the pixel shader
        {
            if (SharedAPI.D3DCompiler.Compile((void*)_psSrc, (nuint)_pixelShader.Length, (byte*)0, null, null, (byte*)_entryMain, (byte*)_psTarget, 0, 0, &pixelShaderBlob, null) < 0)
            {
                vertexShaderBlob->Release();
                return false; // NB: Pass ID3DBlob* pErrorBlob to D3DCompile() to get error showing in (const char*)pErrorBlob->GetBufferPointer(). Make sure to Release() the blob!
            }
            psoDesc.PS = new ShaderBytecode
            {
                PShaderBytecode = pixelShaderBlob->GetBufferPointer(),
                BytecodeLength = pixelShaderBlob->GetBufferSize()
            };
        }

        // Create the blending setup
        {
            psoDesc.BlendState = new BlendDesc
            {
                AlphaToCoverageEnable = 0,
                IndependentBlendEnable = 0
            };
            psoDesc.BlendState.RenderTarget[0] = new RenderTargetBlendDesc
            {
                BlendEnable = 1,
                SrcBlend = Blend.SrcAlpha,
                DestBlend = Blend.InvSrcAlpha,
                BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One,
                DestBlendAlpha = Blend.InvSrcAlpha,
                BlendOpAlpha = BlendOp.Add,
                RenderTargetWriteMask = (byte)ColorWriteEnable.All
            };
        }

        // Create the rasterizer state
        {
            psoDesc.RasterizerState = new RasterizerDesc
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                FrontCounterClockwise = 0,
                DepthBias = -8,
                DepthBiasClamp = 0.0f,
                SlopeScaledDepthBias = 0.0f,
                DepthClipEnable = 1,
                MultisampleEnable = 0,
                AntialiasedLineEnable = 0,
                ForcedSampleCount = 0,
                ConservativeRaster = ConservativeRasterizationMode.Off
            };
        }

        // Create depth-stencil State
        {
            psoDesc.DepthStencilState = new DepthStencilDesc
            {
                DepthEnable = 0,
                DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunc.Always,
                StencilEnable = 0
            };
            psoDesc.DepthStencilState.FrontFace = new DepthStencilopDesc
            {
                StencilFailOp = StencilOp.Keep,
                StencilDepthFailOp = StencilOp.Keep,
                StencilPassOp = StencilOp.Keep,
                StencilFunc = ComparisonFunc.Always
            };
            psoDesc.DepthStencilState.BackFace = psoDesc.DepthStencilState.FrontFace;
        }

        riid = ID3D12PipelineState.Guid;
        int result_pipeline_state = bd->pd3dDevice->CreateGraphicsPipelineState(&psoDesc, &riid, (void**)&bd->pPipelineState);
        vertexShaderBlob->Release();
        pixelShaderBlob->Release();
        if (result_pipeline_state != 0)
            return false;

        // Create command allocator and command list for ImGui_ImplDX12_UpdateTexture()
        riid = ID3D12CommandAllocator.Guid;
        int hr = bd->pd3dDevice->CreateCommandAllocator(CommandListType.Direct, &riid, (void**)&bd->pTexCmdAllocator);
        if (hr < 0)
            throw new InvalidOperationException($"CreateCommandAllocator failed: 0x{hr:X8}");
        riid = ID3D12GraphicsCommandList.Guid;
        hr = bd->pd3dDevice->CreateCommandList(0, CommandListType.Direct, bd->pTexCmdAllocator, null, &riid, (void**)&bd->pTexCmdList);
        if (hr < 0)
            throw new InvalidOperationException($"CreateCommandList failed: 0x{hr:X8}");
        hr = bd->pTexCmdList->Close();
        if (hr < 0)
            throw new InvalidOperationException($"Texture command list Close() failed: 0x{hr:X8}");

        // Create fence.
        riid = ID3D12Fence.Guid;
        hr = bd->pd3dDevice->CreateFence(0, FenceFlags.None, &riid, (void**)&bd->Fence);
        if (hr != 0)
            throw new InvalidOperationException($"CreateFence failed: 0x{hr:X8}");
        bd->FenceEvent = Kernel32.CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
        if (bd->FenceEvent == IntPtr.Zero)
            throw new InvalidOperationException("CreateEvent failed (FenceEvent is null).");

        return true;
    }

    private unsafe static void InvalidateDeviceObjects()
    {
        Data* bd = GetBackendData();
        if (bd == null || bd->pd3dDevice == null)
            return;

        if (bd->commandQueueOwned)
            SafeRelease(ref bd->pCommandQueue);
        bd->commandQueueOwned = false;
        SafeRelease(ref bd->pRootSignature);
        SafeRelease(ref bd->pPipelineState);
        if (bd->pTexUploadBufferMapped != null)
        {
            Range range = new Range
            {
                Begin = 0,
                End = bd->pTexUploadBufferSize
            };
            bd->pTexUploadBuffer->Unmap(0, &range);
            bd->pTexUploadBufferMapped = null;
        }
        SafeRelease(ref bd->pTexUploadBuffer);
        SafeRelease(ref bd->pTexCmdList);
        SafeRelease(ref bd->pTexCmdAllocator);
        SafeRelease(ref bd->Fence);
        Kernel32.CloseHandle(bd->FenceEvent);
        bd->FenceEvent = IntPtr.Zero;

        // Destroy all textures
        ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
        for (int i = 0; i < platformIO.Textures.Size; i++)
        {
            ImTextureData* tex = platformIO.Textures.Data[i];
            if (tex->RefCount == 1)
                DestroyTexture(tex);
        }

        for (uint i = 0; i < bd->numFramesInFlight; i++)
        {
            RenderBuffers* fr = &bd->pFrameResources[i];
            SafeRelease(ref fr->IndexBuffer);
            SafeRelease(ref fr->VertexBuffer);
        }
    }

    public unsafe static bool Init(InitInfo* init_info)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        if (io.BackendRendererUserData != null)
            throw new InvalidOperationException("Already initialized a renderer backend!");

        // Setup backend capabilities flags
        Data* bd = (Data*)ImGui.MemAlloc((uint)sizeof(Data));
        *bd = default;
        bd->InitInfo = *init_info; // Deep copy
        init_info = &bd->InitInfo;

        bd->pd3dDevice = init_info->Device;
        if (init_info->CommandQueue == null)
            throw new InvalidOperationException("InitInfo.CommandQueue must not be null.");
        bd->pCommandQueue = init_info->CommandQueue;
        bd->commandQueueOwned = true;
        bd->RTVFormat = init_info->RTVFormat;
        bd->DSVFormat = init_info->DSVFormat;
        bd->numFramesInFlight = (uint)init_info->NumFramesInFlight;
        bd->pd3dSrvDescHeap = init_info->SrvDescriptorHeap;

        io.BackendRendererUserData = bd;
        io.BackendRendererName = (byte*)Marshal.StringToHGlobalAnsi($"imgui_impl_dx12_{DearImGuiInjectionCore.BackendVersion}");
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;  // We can honor the ImDrawCmd::VtxOffset field, allowing for large meshes.
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;   // We can honor ImGuiPlatformIO::Textures[] requests during render.

        if (init_info->SrvDescriptorAllocFn == null || init_info->SrvDescriptorFreeFn == null)
            throw new InvalidOperationException("Expected SrvDescriptorAllocFn and  SrvDescriptorFreeFn must not be null.");

        // Create buffers with a default size (they will later be grown as needed)
        bd->frameIndex = uint.MaxValue;
        bd->pFrameResources = (RenderBuffers*)ImGui.MemAlloc((nuint)sizeof(RenderBuffers) * bd->numFramesInFlight);
        for (int i = 0; i < (int)bd->numFramesInFlight; i++)
        {
            RenderBuffers* fr = &bd->pFrameResources[i];
            fr->IndexBuffer = null;
            fr->VertexBuffer = null;
            fr->IndexBufferSize = 10000;
            fr->VertexBufferSize = 5000;
        }

        return true;
    }

    public unsafe static void Shutdown()
    {
        Data* bd = GetBackendData();
        if (bd == null)
            throw new InvalidOperationException("No renderer backend to shutdown, or already shutdown?");
        ImGuiIOPtr io = ImGui.GetIO();
        ImGuiPlatformIOPtr platform_io = ImGui.GetPlatformIO();

        InvalidateDeviceObjects();
        ImGui.MemFree(bd->pFrameResources);
        Marshal.FreeHGlobal(_entryMain);
        Marshal.FreeHGlobal(_vsSrc);
        Marshal.FreeHGlobal(_vsTarget);
        Marshal.FreeHGlobal(_inputElementPos);
        Marshal.FreeHGlobal(_inputElementUv);
        Marshal.FreeHGlobal(_inputElementCol);
        Marshal.FreeHGlobal(_psSrc);
        Marshal.FreeHGlobal(_psTarget);

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
            throw new InvalidOperationException("Context or backend not initialized! Did you call ImGui_ImplDX12_Init()?");

        if (bd->pPipelineState == null)
            if (!CreateDeviceObjects())
                throw new InvalidOperationException("ImGui_ImplDX12_CreateDeviceObjects() failed!");
    }
}