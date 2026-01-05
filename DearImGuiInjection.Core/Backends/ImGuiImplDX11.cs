using Hexa.NET.ImGui;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

using ImDrawIdx = ushort;

namespace DearImGuiInjection.Backends;

internal static class ImGuiImplDX11
{
    private unsafe static readonly int _dataSizeOf = sizeof(Data);
    private unsafe static readonly uint _imDrawVertSizeOf = (uint)sizeof(ImDrawVert);
    private static readonly uint _imDrawVertOffset = 0;
    private unsafe static readonly uint _imDrawIdxSizeOf = (uint)sizeof(ImDrawIdx);
    private static readonly Format _imDrawIdxFormat =
        _imDrawIdxSizeOf == 2 ? Format.FormatR16Uint : Format.FormatR32Uint;
    private unsafe static readonly nuint _textureSizeOf = (nuint)sizeof(Texture);
    private unsafe static readonly uint _vertexConstantBufferSizeOf = (uint)sizeof(VERTEX_CONSTANT_BUFFER_DX11);

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
    private static readonly IntPtr _vsTarget = Marshal.StringToHGlobalAnsi("vs_4_0");

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
        sampler sampler0;\
        Texture2D texture0;\
        \
        float4 main(PS_INPUT input) : SV_Target\
        {\
        float4 out_col = input.col * texture0.Sample(sampler0, input.uv); \
        return out_col; \
        }";
    private static readonly IntPtr _psSrc = Marshal.StringToHGlobalAnsi(_pixelShader);
    private static readonly IntPtr _psTarget = Marshal.StringToHGlobalAnsi("ps_4_0");

    // [BETA] Selected render state data shared with callbacks.
    // This is temporarily stored in GetPlatformIO().Renderer_RenderState during the ImGui_ImplDX11_RenderDrawData() call.
    // (Please open an issue if you feel you need access to more data)
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct RenderState
    {
        public ID3D11Device* Device;
        public ID3D11DeviceContext* DeviceContext;
        public ID3D11SamplerState* SamplerDefault;
        public ID3D11Buffer* VertexConstantBuffer;
    }

    private unsafe class ImDrawCallback
    {
        public static void* ResetRenderState = (void*)-8;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void UserCallback(ImDrawList* parent_list, ImDrawCmd* cmd);
    }

    // DirectX11 data
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Texture
    {
        public ID3D11Texture2D* pTexture;
        public ID3D11ShaderResourceView* pTextureView;
    }

    private unsafe struct Data
    {
        public ID3D11Device* Device;
        public ID3D11DeviceContext* DeviceContext;
        public ID3D11Buffer* VertexBuffer;
        public ID3D11Buffer* IndexBuffer;
        public ID3D11VertexShader* VertexShader;
        public ID3D11InputLayout* InputLayout;
        public ID3D11Buffer* VertexConstantBuffer;
        public ID3D11PixelShader* PixelShader;
        public ID3D11SamplerState* TexSamplerLinear;
        public ID3D11RasterizerState* RasterizerState;
        public ID3D11BlendState* BlendState;
        public ID3D11DepthStencilState* DepthStencilState;
        public int VertexBufferSize;
        public int IndexBufferSize;

        public Data()
        {
            VertexBufferSize = 5000;
            IndexBufferSize = 10000;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VERTEX_CONSTANT_BUFFER_DX11
    {
        public fixed float mvp[4 * 4];
    }

    private unsafe struct BACKUP_DX11_STATE
    {
        public uint ScissorRectsCount, ViewportsCount;
        public Box2D<int>* ScissorRects;
        public Viewport* Viewports;
        public ID3D11RasterizerState* RS;
        public ID3D11BlendState* BlendState;
        public fixed float BlendFactor[4];
        public uint SampleMask;
        public uint StencilRef;
        public ID3D11DepthStencilState* DepthStencilState;
        public ID3D11ShaderResourceView* PSShaderResource;
        public ID3D11SamplerState* PSSampler;
        public ID3D11PixelShader* PS;
        public ID3D11VertexShader* VS;
        public ID3D11GeometryShader* GS;
        public uint PSInstancesCount, VSInstancesCount, GSInstancesCount;
        public ID3D11ClassInstance** PSInstances, VSInstances, GSInstances;   // 256 is max according to PSSetShader documentation
        public D3DPrimitiveTopology PrimitiveTopology;
        public ID3D11Buffer* IndexBuffer, VertexBuffer, VSConstantBuffer;
        public uint IndexBufferOffset, VertexBufferStride, VertexBufferOffset;
        public Format IndexBufferFormat;
        public ID3D11InputLayout* InputLayout;
    }

    // Backend data stored in io.BackendRendererUserData to allow support for multiple Dear ImGui contexts
    // It is STRONGLY preferred that you use docking branch with multi-viewports (== single Dear ImGui context + multiple windows) instead of multiple Dear ImGui contexts.
    private unsafe static Data* GetBackendData() => (Data*)ImGui.GetIO().BackendRendererUserData;

    // Functions
    private unsafe static void SetupRenderState(ImDrawData* draw_data, ID3D11DeviceContext* device_ctx)
    {
        Data* bd = GetBackendData();

        // Setup viewport
        Viewport vp = new()
        {
            Width = draw_data->DisplaySize.X * draw_data->FramebufferScale.X,
            Height = draw_data->DisplaySize.Y * draw_data->FramebufferScale.Y,
            MinDepth = 0.0f,
            MaxDepth = 1.0f,
            TopLeftX = 0,
            TopLeftY = 0
        };
        device_ctx->RSSetViewports(1, &vp);

        // Setup orthographic projection matrix into our constant buffer
        // Our visible imgui space lies from draw_data->DisplayPos (top left) to draw_data->DisplayPos+data_data->DisplaySize (bottom right). DisplayPos is (0,0) for single viewport apps.
        MappedSubresource mapped_resource = default;
        if (device_ctx->Map((ID3D11Resource*)bd->VertexConstantBuffer, 0, Map.WriteDiscard, 0, &mapped_resource) >= 0)
        {
            VERTEX_CONSTANT_BUFFER_DX11* constant_buffer = (VERTEX_CONSTANT_BUFFER_DX11*)mapped_resource.PData;
            float L = draw_data->DisplayPos.X;
            float R = draw_data->DisplayPos.X + draw_data->DisplaySize.X;
            float T = draw_data->DisplayPos.Y;
            float B = draw_data->DisplayPos.Y + draw_data->DisplaySize.Y;
            constant_buffer->mvp[0] = 2.0f / (R - L);
            constant_buffer->mvp[1] = 0.0f;
            constant_buffer->mvp[2] = 0.0f;
            constant_buffer->mvp[3] = 0.0f;
            constant_buffer->mvp[4] = 0.0f;
            constant_buffer->mvp[5] = 2.0f / (T - B);
            constant_buffer->mvp[6] = 0.0f;
            constant_buffer->mvp[7] = 0.0f;
            constant_buffer->mvp[8] = 0.0f;
            constant_buffer->mvp[9] = 0.0f;
            constant_buffer->mvp[10] = 0.5f;
            constant_buffer->mvp[11] = 0.0f;
            constant_buffer->mvp[12] = (R + L) / (L - R);
            constant_buffer->mvp[13] = (T + B) / (B - T);
            constant_buffer->mvp[14] = 0.5f;
            constant_buffer->mvp[15] = 1.0f;
            device_ctx->Unmap((ID3D11Resource*)bd->VertexConstantBuffer, 0);
        }

        // Setup shader and vertex buffers
        device_ctx->IASetInputLayout(bd->InputLayout);
        device_ctx->IASetVertexBuffers(0, 1, &bd->VertexBuffer, in _imDrawVertSizeOf, in _imDrawVertOffset);
        device_ctx->IASetIndexBuffer(bd->IndexBuffer, _imDrawIdxFormat, 0);
        device_ctx->IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        device_ctx->VSSetShader(bd->VertexShader, null, 0);
        device_ctx->VSSetConstantBuffers(0, 1, &bd->VertexConstantBuffer);
        device_ctx->PSSetShader(bd->PixelShader, null, 0);
        device_ctx->PSSetSamplers(0, 1, &bd->TexSamplerLinear);
        device_ctx->GSSetShader(null, null, 0);
        device_ctx->HSSetShader(null, null, 0); // In theory we should backup and restore this as well.. very infrequently used..
        device_ctx->DSSetShader(null, null, 0); // In theory we should backup and restore this as well.. very infrequently used..
        device_ctx->CSSetShader(null, null, 0); // In theory we should backup and restore this as well.. very infrequently used..

        // Setup blend state
        float[] _blend_factor = { 0f, 0f, 0f, 0f };
        fixed (float* blend_factor = _blend_factor)
            device_ctx->OMSetBlendState(bd->BlendState, blend_factor, 0xffffffff);
        device_ctx->OMSetDepthStencilState(bd->DepthStencilState, 0);
        device_ctx->RSSetState(bd->RasterizerState);
    }

    // Render function
    public unsafe static void RenderDrawData(ImDrawData* draw_data)
    {
        // Avoid rendering when minimized
        if (draw_data->DisplaySize.X <= 0.0f || draw_data->DisplaySize.Y <= 0.0f)
            return;

        Data* bd = GetBackendData();
        ID3D11DeviceContext* device = bd->DeviceContext;

        // Catch up with texture updates. Most of the times, the list will have 1 element with an OK status, aka nothing to do.
        // (This almost always points to ImGui::GetPlatformIO().Textures[] but is part of ImDrawData to allow overriding or disabling texture updates).
        if (draw_data->Textures != null)
        {
            for (int n = 0; n < draw_data->Textures->Size; n++)
            {
                ImTextureData* tex = draw_data->Textures->Data[n].Handle;
                if (tex->Status != ImTextureStatus.Ok)
                    UpdateTexture(tex);
            }
        }

        // Create and grow vertex/index buffers if needed
        if (bd->VertexBuffer == null || bd->VertexBufferSize < draw_data->TotalVtxCount)
        {
            if (bd->VertexBuffer != null)
            {
                bd->VertexBuffer->Release();
                bd->VertexBuffer = null;
            }
            bd->VertexBufferSize = draw_data->TotalVtxCount + 5000;
            BufferDesc desc = new()
            {
                Usage = Usage.Dynamic,
                ByteWidth = (uint)bd->VertexBufferSize * _imDrawVertSizeOf,
                BindFlags = (uint)BindFlag.VertexBuffer,
                CPUAccessFlags = (uint)CpuAccessFlag.Write,
                MiscFlags = 0
            };
            if (bd->Device->CreateBuffer(&desc, null, &bd->VertexBuffer) < 0)
                return;
        }
        if (bd->IndexBuffer == null || bd->IndexBufferSize < draw_data->TotalIdxCount)
        {
            if (bd->IndexBuffer != null)
            {
                bd->IndexBuffer->Release();
                bd->IndexBuffer = null;
            }
            bd->IndexBufferSize = draw_data->TotalIdxCount + 10000;
            BufferDesc desc = new()
            {
                Usage = Usage.Dynamic,
                ByteWidth = (uint)bd->IndexBufferSize * _imDrawIdxSizeOf,
                BindFlags = (uint)BindFlag.IndexBuffer,
                CPUAccessFlags = (uint)CpuAccessFlag.Write,
                MiscFlags = 0
            };
            if (bd->Device->CreateBuffer(&desc, null, &bd->IndexBuffer) < 0)
                return;
        }

        // Upload vertex/index data into a single contiguous GPU buffer
        MappedSubresource vtx_resource, idx_resource;
        if (device->Map((ID3D11Resource*)bd->VertexBuffer, 0, Map.WriteDiscard, 0, &vtx_resource) < 0)
            return;
        if (device->Map((ID3D11Resource*)bd->IndexBuffer, 0, Map.WriteDiscard, 0, &idx_resource) < 0)
            return;
        ImDrawVert* vtx_dst = (ImDrawVert*)vtx_resource.PData;
        ImDrawIdx* idx_dst = (ImDrawIdx*)idx_resource.PData;
        for (int n = 0; n < draw_data->CmdListsCount; n++)
        {
            var cmd_list = draw_data->CmdLists[n].Handle;
            var len = cmd_list->VtxBuffer.Size * _imDrawVertSizeOf;
            System.Buffer.MemoryCopy(cmd_list->VtxBuffer.Data, vtx_dst, len, len);
            len = cmd_list->IdxBuffer.Size * _imDrawIdxSizeOf;
            System.Buffer.MemoryCopy(cmd_list->IdxBuffer.Data, idx_dst, len, len);
            vtx_dst += cmd_list->VtxBuffer.Size;
            idx_dst += cmd_list->IdxBuffer.Size;
        }
        device->Unmap((ID3D11Resource*)bd->VertexBuffer, 0);
        device->Unmap((ID3D11Resource*)bd->IndexBuffer, 0);

        // Backup DX state that will be modified to restore it afterwards (unfortunately this is very ugly looking and verbose. Close your eyes!)
        BACKUP_DX11_STATE old = new();
        old.ScissorRectsCount = old.ViewportsCount = D3D11.ViewportAndScissorrectObjectCountPerPipeline;
        Box2D<int>* scissorRects = stackalloc Box2D<int>[(int)old.ScissorRectsCount];
        old.ScissorRects = scissorRects;
        device->RSGetScissorRects(&old.ScissorRectsCount, old.ScissorRects);
        Viewport* viewports = stackalloc Viewport[(int)old.ViewportsCount];
        old.Viewports = viewports;
        device->RSGetViewports(&old.ViewportsCount, old.Viewports);
        device->RSGetState(&old.RS);
        device->OMGetBlendState(&old.BlendState, old.BlendFactor, &old.SampleMask);
        device->OMGetDepthStencilState(&old.DepthStencilState, &old.StencilRef);
        device->PSGetShaderResources(0, 1, &old.PSShaderResource);
        device->PSGetSamplers(0, 1, &old.PSSampler);
        old.PSInstancesCount = old.VSInstancesCount = old.GSInstancesCount = 256;
        device->PSGetShader(&old.PS, old.PSInstances, &old.PSInstancesCount);
        device->VSGetShader(&old.VS, old.VSInstances, &old.VSInstancesCount);
        device->VSGetConstantBuffers(0, 1, &old.VSConstantBuffer);
        device->GSGetShader(&old.GS, old.GSInstances, &old.GSInstancesCount);

        device->IAGetPrimitiveTopology(&old.PrimitiveTopology);
        device->IAGetIndexBuffer(&old.IndexBuffer, &old.IndexBufferFormat, &old.IndexBufferOffset);
        device->IAGetVertexBuffers(0, 1, &old.VertexBuffer, &old.VertexBufferStride, &old.VertexBufferOffset);
        device->IAGetInputLayout(&old.InputLayout);

        // Setup desired DX state
        SetupRenderState(draw_data, device);

        // Setup render state structure (for callbacks and custom texture bindings)
        var platform_io = ImGui.GetPlatformIO();
        RenderState renderState;
        renderState.Device = bd->Device;
        renderState.DeviceContext = bd->DeviceContext;
        renderState.SamplerDefault = bd->TexSamplerLinear;
        renderState.VertexConstantBuffer = bd->VertexConstantBuffer;
        platform_io.RendererRenderState = &renderState;

        // Render command lists
        // (Because we merged all buffers into a single one, we maintain our own offset into them)
        int global_idx_offset = 0;
        int global_vtx_offset = 0;
        var clip_off = draw_data->DisplayPos;
        Vector2 clip_scale = draw_data->FramebufferScale;
        for (int n = 0; n < draw_data->CmdListsCount; n++)
        {
            ImDrawList* cmd_list = draw_data->CmdLists[n].Handle;
            for (int cmd_i = 0; cmd_i < cmd_list->CmdBuffer.Size; cmd_i++)
            {
                ImDrawCmd pcmd = cmd_list->CmdBuffer[cmd_i];
                if (pcmd.UserCallback != null)
                {
                    // User callback, registered via ImDrawList::AddCallback()
                    // (ImDrawCallback_ResetRenderState is a special callback value used by the user to request the renderer to reset render state.)
                    if (pcmd.UserCallback == ImDrawCallback.ResetRenderState)
                        SetupRenderState(draw_data, device);
                    else
                        Marshal.GetDelegateForFunctionPointer<ImDrawCallback.UserCallback>((IntPtr)pcmd.UserCallback)(cmd_list, &pcmd);
                }
                else
                {
                    // Project scissor/clipping rectangles into framebuffer space
                    Vector2 clip_min = new((pcmd.ClipRect.X - clip_off.X) * clip_scale.X, (pcmd.ClipRect.Y - clip_off.Y) * clip_scale.Y);
                    Vector2 clip_max = new((pcmd.ClipRect.Z - clip_off.X) * clip_scale.X, (pcmd.ClipRect.W - clip_off.Y) * clip_scale.Y);
                    if (clip_max.X <= clip_min.X || clip_max.Y <= clip_min.Y)
                        continue;

                    // Apply scissor/clipping rectangle
                    Box2D<int> r = new(new((int)clip_min.X, (int)clip_min.Y), new((int)clip_max.X, (int)clip_max.Y));
                    device->RSSetScissorRects(1, &r);

                    // Bind texture, Draw
                    ID3D11ShaderResourceView* texture_srv = (ID3D11ShaderResourceView*)pcmd.GetTexID();
                    device->PSSetShaderResources(0, 1, &texture_srv);
                    device->DrawIndexed(pcmd.ElemCount, pcmd.IdxOffset + (uint)global_idx_offset, (int)pcmd.VtxOffset + global_vtx_offset);
                }
            }
            global_idx_offset += cmd_list->IdxBuffer.Size;
            global_vtx_offset += cmd_list->VtxBuffer.Size;
        }
        platform_io.RendererRenderState = null;

        // Restore modified DX state
        device->RSSetScissorRects(old.ScissorRectsCount, old.ScissorRects);
        device->RSSetViewports(old.ViewportsCount, old.Viewports);
        device->RSSetState(old.RS);
        if (old.RS != null)
            old.RS->Release();
        device->OMSetBlendState(old.BlendState, old.BlendFactor, old.SampleMask);
        if (old.BlendState != null)
            old.BlendState->Release();
        device->OMSetDepthStencilState(old.DepthStencilState, old.StencilRef);
        if (old.DepthStencilState != null)
            old.DepthStencilState->Release();
        device->PSSetShaderResources(0, 1, &old.PSShaderResource);
        if (old.PSShaderResource != null)
            old.PSShaderResource->Release();
        device->PSSetSamplers(0, 1, &old.PSSampler);
        if (old.PSSampler != null)
            old.PSSampler->Release();
        device->PSSetShader(old.PS, old.PSInstances, old.PSInstancesCount);
        if (old.PS != null)
            old.PS->Release();
        for (uint i = 0; i < old.PSInstancesCount; i++)
            if (old.PSInstances[i] != null)
                old.PSInstances[i]->Release();
        device->VSSetShader(old.VS, old.VSInstances, old.VSInstancesCount);
        if (old.VS != null)
            old.VS->Release();
        device->VSSetConstantBuffers(0, 1, &old.VSConstantBuffer);
        if (old.VSConstantBuffer != null)
            old.VSConstantBuffer->Release();
        device->GSSetShader(old.GS, old.GSInstances, old.GSInstancesCount);
        if (old.GS != null)
            old.GS->Release();
        for (uint i = 0; i < old.VSInstancesCount; i++)
            if (old.VSInstances[i] != null)
                old.VSInstances[i]->Release();
        device->IASetPrimitiveTopology(old.PrimitiveTopology);
        device->IASetIndexBuffer(old.IndexBuffer, old.IndexBufferFormat, old.IndexBufferOffset);
        if (old.IndexBuffer != null)
            old.IndexBuffer->Release();
        device->IASetVertexBuffers(0, 1, &old.VertexBuffer, &old.VertexBufferStride, &old.VertexBufferOffset);
        if (old.VertexBuffer != null)
            old.VertexBuffer->Release();
        device->IASetInputLayout(old.InputLayout);
        if (old.InputLayout != null)
            old.InputLayout->Release();
    }

    private unsafe static void DestroyTexture(ImTextureData* tex)
    {
        Texture* backend_tex = (Texture*)tex->BackendUserData;
        if (backend_tex != null)
        {
            Debug.Assert(backend_tex->pTextureView == (ID3D11ShaderResourceView*)tex->GetTexID());
            backend_tex->pTextureView->Release();
            backend_tex->pTexture->Release();
            ImGui.MemFree(backend_tex);

            // Clear identifiers and mark as destroyed (in order to allow e.g. calling InvalidateDeviceObjects while running)
            tex->SetTexID(ImTextureID.Null);
            tex->BackendUserData = null;
        }
        tex->SetStatus(ImTextureStatus.Destroyed);
    }

    private unsafe static void UpdateTexture(ImTextureData* tex)
    {
        Data* bd = GetBackendData();
        if (tex->Status == ImTextureStatus.WantCreate)
        {
            // Create and upload new texture to graphics system
            //Log.Debug(string.Format("UpdateTexture #%03d: WantCreate %dx%d\n", tex->UniqueID, tex->Width, tex->Height));
            Debug.Assert(tex->TexID == ImTextureID.Null && tex->BackendUserData == null);
            Debug.Assert(tex->Format == ImTextureFormat.Rgba32);
            IntPtr pixels = (IntPtr)tex->GetPixels();
            Texture* backend_tex = (Texture*)ImGui.MemAlloc(_textureSizeOf);

            // Create texture
            Texture2DDesc desc = new()
            {
                Width = (uint)tex->Width,
                Height = (uint)tex->Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.FormatR8G8B8A8Unorm,
                SampleDesc = new SampleDesc(1),
                Usage = Usage.Default,
                BindFlags = (uint)BindFlag.ShaderResource,
                CPUAccessFlags = (uint)BindFlag.None
            };
            SubresourceData subResource = new()
            {
                PSysMem = (void*)pixels,
                SysMemPitch = desc.Width * 4,
                SysMemSlicePitch = 0
            };
            bd->Device->CreateTexture2D(&desc, &subResource, &backend_tex->pTexture);
            Debug.Assert(backend_tex->pTexture != null, "Backend failed to create texture!");

            // Create texture view
            ShaderResourceViewDesc srvDesc = new()
            {
                Format = Format.FormatR8G8B8A8Unorm,
                ViewDimension = D3DSrvDimension.D3D11SrvDimensionTexture2D,
                Texture2D = new()
                {
                    MipLevels = desc.MipLevels,
                    MostDetailedMip = 0
                }
            };
            bd->Device->CreateShaderResourceView((ID3D11Resource*)backend_tex->pTexture, &srvDesc, &backend_tex->pTextureView);
            Debug.Assert(backend_tex->pTextureView != null, "Backend failed to create texture!");

            // Store identifiers
            tex->SetTexID(backend_tex->pTextureView);
            tex->SetStatus(ImTextureStatus.Ok);
            tex->BackendUserData = backend_tex;
        }
        else if (tex->Status == ImTextureStatus.WantUpdates)
        {
            // Update selected blocks. We only ever write to textures regions which have never been used before!
            // This backend choose to use tex->Updates[] but you can use tex->UpdateRect to upload a single region.
            Texture* backend_tex = (Texture*)tex->BackendUserData;
            Debug.Assert(backend_tex->pTextureView == (ID3D11ShaderResourceView*)tex->GetTexID());
            for (int n = 0; n < tex->Updates.Size; n++)
            {
                ImTextureRect r = tex->Updates[n];
                Box box = new(r.X, r.Y, 0, (uint)(r.X + r.W), (uint)(r.Y + r.H), 1);
                bd->DeviceContext->UpdateSubresource((ID3D11Resource*)backend_tex->pTexture, 0, &box, tex->GetPixelsAt(r.X, r.Y), (uint)tex->GetPitch(), 0);
            }
            tex->SetStatus(ImTextureStatus.Ok);
        }
        if (tex->Status == ImTextureStatus.WantDestroy && tex->UnusedFrames > 0)
            DestroyTexture(tex);
    }

    public unsafe static bool CreateDeviceObjects()
    {
        Data* bd = GetBackendData();
        if (bd->Device == null)
            return false;
        InvalidateDeviceObjects();
        D3DCompiler D3D = D3DCompiler.GetApi();

        // By using D3DCompile() from <d3dcompiler.h> / d3dcompiler.lib, we introduce a dependency to a given version of d3dcompiler_XX.dll (see D3DCOMPILER_DLL_A)
        // If you would like to use this DX11 sample code but remove this dependency you can:
        //  1) compile once, save the compiled shader blobs into a file or source code and pass them to CreateVertexShader()/CreatePixelShader() [preferred solution]
        //  2) use code to detect any version of the DLL and grab a pointer to D3DCompile from the DLL.
        // See https://github.com/ocornut/imgui/pull/638 for sources and details.

        // Create the vertex shader
        ID3D10Blob* vertexShaderBlob;
        if (D3D.Compile((void*)_vsSrc, (nuint)_vertexShader.Length, (byte*)0, null, null, (byte*)_entryMain, (byte*)_vsTarget, 0, 0, &vertexShaderBlob, null) < 0)
            return false; // NB: Pass ID3DBlob* pErrorBlob to D3DCompile() to get error showing in (const char*)pErrorBlob->GetBufferPointer(). Make sure to Release() the blob!
        if (bd->Device->CreateVertexShader(vertexShaderBlob->GetBufferPointer(), vertexShaderBlob->GetBufferSize(), null, &bd->VertexShader) < 0)
        {
            vertexShaderBlob->Release();
            return false;
        }

        // Create the input layout
        fixed (InputElementDesc* inputElements = _inputElements)
        {
            if (bd->Device->CreateInputLayout(inputElements, (uint)_inputElements.Length, vertexShaderBlob->GetBufferPointer(), vertexShaderBlob->GetBufferSize(), &bd->InputLayout) < 0)
            {
                vertexShaderBlob->Release();
                return false;
            }
            vertexShaderBlob->Release();
        }

        // Create the constant buffer
        {
            BufferDesc desc = new()
            {
                ByteWidth = _vertexConstantBufferSizeOf,    
                Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.ConstantBuffer,
                CPUAccessFlags = (uint)CpuAccessFlag.Write,
                MiscFlags = 0
            };
            bd->Device->CreateBuffer(&desc, null, &bd->VertexConstantBuffer);
        }

        // Create the pixel shader
        ID3D10Blob* pixelShaderBlob;
        if (D3D.Compile((void*)_psSrc, (nuint)_pixelShader.Length, (byte*)0, null, null, (byte*)_entryMain, (byte*)_psTarget, 0, 0, &pixelShaderBlob, null) < 0)
            return false; // NB: Pass ID3DBlob* pErrorBlob to D3DCompile() to get error showing in (const char*)pErrorBlob->GetBufferPointer(). Make sure to Release() the blob!
        if (bd->Device->CreatePixelShader(pixelShaderBlob->GetBufferPointer(), pixelShaderBlob->GetBufferSize(), null, &bd->PixelShader) < 0)
        {
            pixelShaderBlob->Release();
            return false;
        }
        pixelShaderBlob->Release();

        // Create the blending setup
        {
            BlendDesc desc = new()
            {
                AlphaToCoverageEnable = false,
                RenderTarget = new()
                {
                    Element0 = new()
                    {
                        BlendEnable = true,
                        SrcBlend = Blend.SrcAlpha,
                        DestBlend = Blend.InvSrcAlpha,
                        BlendOp = BlendOp.Add,
                        SrcBlendAlpha = Blend.One,
                        DestBlendAlpha = Blend.InvSrcAlpha,
                        BlendOpAlpha = BlendOp.Add,
                        RenderTargetWriteMask = (byte)ColorWriteEnable.All
                    }
                }
            };
            bd->Device->CreateBlendState(&desc, &bd->BlendState);
        }

        // Create the rasterizer state
        {
            RasterizerDesc desc = new()
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                ScissorEnable = true,
                DepthClipEnable = true
            };
            bd->Device->CreateRasterizerState(&desc, &bd->RasterizerState);
        }

        // Create depth-stencil State
        {
            DepthStencilopDesc frontFace = new()
            {
                StencilFailOp = StencilOp.Keep,
                StencilDepthFailOp = StencilOp.Keep,
                StencilPassOp = StencilOp.Keep,
                StencilFunc = ComparisonFunc.Always
            };
            DepthStencilDesc desc = new()
            {
                DepthEnable = false,
                DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunc.Always,
                StencilEnable = false,
                FrontFace = frontFace,
                BackFace = frontFace
            };
            bd->Device->CreateDepthStencilState(&desc, &bd->DepthStencilState);
        }

        // Create texture sampler
        // (Bilinear sampling is required by default. Set 'io.Fonts->Flags |= ImFontAtlasFlags_NoBakedLines' or 'style.AntiAliasedLinesUseTex = false' to allow point/nearest sampling)
        {
            SamplerDesc desc = new()
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MipLODBias = 0,
                ComparisonFunc = ComparisonFunc.Always,
                MinLOD = 0,
                MaxLOD = 0
            };
            bd->Device->CreateSamplerState(&desc, &bd->TexSamplerLinear);
        }

        return true;
    }

    public unsafe static void InvalidateDeviceObjects()
    {
        Data* bd = GetBackendData();
        if (bd->Device == null)
            return;

        // Destroy all textures
        var textures = ImGui.GetPlatformIO().Textures;
        for (int n = 0; n < textures.Size; n++)
        {
            ImTextureData* tex = textures.Data[n].Handle;
            if (tex->RefCount == 1)
                DestroyTexture(tex);
        }

        if (bd->TexSamplerLinear != null)
        {
            bd->TexSamplerLinear->Release();
            bd->TexSamplerLinear = null;
        }
        if (bd->IndexBuffer != null)
        {
            bd->IndexBuffer->Release();
            bd->IndexBuffer = null;
        }
        if (bd->VertexBuffer != null)
        {
            bd->VertexBuffer->Release();
            bd->VertexBuffer = null;
        }
        if (bd->BlendState != null)
        {
            bd->BlendState->Release();
            bd->BlendState = null;
        }
        if (bd->DepthStencilState != null)
        { 
            bd->DepthStencilState->Release();
            bd->DepthStencilState = null;
        }
        if (bd->RasterizerState != null)
        {
            bd->RasterizerState->Release();
            bd->RasterizerState = null;
        }
        if (bd->PixelShader != null)
        {
            bd->PixelShader->Release();
            bd->PixelShader = null;
        }
        if (bd->VertexConstantBuffer != null)
        {
            bd->VertexConstantBuffer->Release();
            bd->VertexConstantBuffer = null;
        }
        if (bd->InputLayout != null)
        {
            bd->InputLayout->Release();
            bd->InputLayout = null;
        }
        if (bd->VertexShader != null) 
        {
            bd->VertexShader->Release();
            bd->VertexShader = null;
        }
    }

    internal unsafe static void Init(ID3D11Device* device, ID3D11DeviceContext* device_context)
    {
        var io = ImGui.GetIO();
        Debug.Assert(io.BackendRendererUserData == null, "Already initialized a renderer backend!");

        // Setup backend capabilities flags
        Data* bd = (Data*)Marshal.AllocHGlobal(_dataSizeOf);
        *bd = new();
        io.BackendRendererUserData = bd;
        io.BackendRendererName = (byte*)Marshal.StringToHGlobalAnsi($"imgui_impl_dx11_{DearImGuiInjectionCore.HexaVersion}");
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;  // We can honor the ImDrawCmd::VtxOffset field, allowing for large meshes.
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;   // We can honor ImGuiPlatformIO::Textures[] requests during render.

        var platform_io = ImGui.GetPlatformIO();
        platform_io.RendererTextureMaxWidth = platform_io.RendererTextureMaxHeight = D3D11.ReqTexture2DUOrVDimension;

        bd->Device = device;
        bd->DeviceContext = device_context;
    }

    public unsafe static void Shutdown()
    {
        Data* bd = GetBackendData();
        Debug.Assert(bd != null, "No renderer backend to shutdown, or already shutdown?");
        var io = ImGui.GetIO();
        var platform_io = ImGui.GetPlatformIO();

        InvalidateDeviceObjects();
        Marshal.FreeHGlobal(_entryMain);
        Marshal.FreeHGlobal(_vsSrc);
        Marshal.FreeHGlobal(_vsTarget);
        Marshal.FreeHGlobal(_inputElementPos);
        Marshal.FreeHGlobal(_inputElementUv);
        Marshal.FreeHGlobal(_inputElementCol);
        Marshal.FreeHGlobal(_psSrc);
        Marshal.FreeHGlobal(_psTarget);
        // we don't own these, so no Dispose()
        bd->Device = null;
        bd->DeviceContext = null;

        Marshal.FreeHGlobal((IntPtr)io.BackendRendererName);
        io.BackendRendererName = null;
        Marshal.FreeHGlobal((IntPtr)io.BackendRendererUserData);
        io.BackendRendererUserData = null;
        io.BackendFlags &= ~(ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures);
        platform_io.ClearRendererHandlers();
    }

    public unsafe static void NewFrame()
    {
        Data* bd = GetBackendData();
        Debug.Assert(bd != null, "Context or backend not initialized! Did you call ImGui_ImplDX11_Init()?");
        if (bd->VertexShader == null)
            if (!CreateDeviceObjects())
                Debug.Assert(false, "ImGui_ImplDX11_CreateDeviceObjects() failed!");
    }
}