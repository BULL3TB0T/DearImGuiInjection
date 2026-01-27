using Hexa.NET.ImGui;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

using Buffer = System.Buffer;
using ImDrawIdx = ushort;

namespace DearImGuiInjection.Backends;

internal static class ImGuiImplDX11
{
    private static readonly IntPtr _entryMain = Marshal.StringToHGlobalAnsi("main");

    private const string _vertexShader = @"
        cbuffer vertexBuffer : register(b0)
        {
            float4x4 ProjectionMatrix;
        };
        struct VS_INPUT
        {
            float2 pos : POSITION;
            float4 col : COLOR0;
            float2 uv  : TEXCOORD0;
        };
        struct PS_INPUT
        {
            float4 pos : SV_POSITION;
            float4 col : COLOR0;
            float2 uv  : TEXCOORD0;
        };
        PS_INPUT main(VS_INPUT input)
        {
            PS_INPUT output;
            output.pos = mul(ProjectionMatrix, float4(input.pos.xy, 0.f, 1.f));
            output.col = input.col;
            output.uv  = input.uv;
            return output;
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

    private const string _pixelShader = @"
        struct PS_INPUT
        {
            float4 pos : SV_POSITION;
            float4 col : COLOR0;
            float2 uv  : TEXCOORD0;
        };
        sampler sampler0;
        Texture2D texture0;
        float4 main(PS_INPUT input) : SV_Target
        {
            float4 out_col = input.col * texture0.Sample(sampler0, input.uv);
            return out_col;
        }";
    private static readonly IntPtr _psSrc = Marshal.StringToHGlobalAnsi(_pixelShader);
    private static readonly IntPtr _psTarget = Marshal.StringToHGlobalAnsi("ps_4_0");

    // DirectX11 data
    private unsafe struct RenderState
    {
        public ID3D11Device* Device;
        public ID3D11DeviceContext* DeviceContext;
        public ID3D11SamplerState* SamplerDefault;
        public ID3D11Buffer* VertexConstantBuffer;
    }

    private unsafe struct Texture
    {
        public ID3D11Texture2D* pTexture;
        public ID3D11ShaderResourceView* pTextureView;
    }

    private unsafe struct Data
    {
        public ID3D11Device* pd3dDevice;
        public ID3D11DeviceContext* pd3dDeviceContext;
        public ID3D11Buffer* pVB;
        public ID3D11Buffer* pIB;
        public ID3D11VertexShader* pVertexShader;
        public ID3D11InputLayout* pInputLayout;
        public ID3D11Buffer* pVertexConstantBuffer;
        public ID3D11PixelShader* pPixelShader;
        public ID3D11SamplerState* pTexSamplerLinear;
        public ID3D11RasterizerState* pRasterizerState;
        public ID3D11BlendState* pBlendState;
        public ID3D11DepthStencilState* pDepthStencilState;
        public int VertexBufferSize;
        public int IndexBufferSize;

        public Data()
        {
            VertexBufferSize = 5000;
            IndexBufferSize = 10000;
        }
    }

    // Backend data stored in io.BackendRendererUserData to allow support for multiple Dear ImGui contexts
    // It is STRONGLY preferred that you use docking branch with multi-viewports (== single Dear ImGui context + multiple windows) instead of multiple Dear ImGui contexts.
    private unsafe static Data* GetBackendData() => (Data*)ImGui.GetIO().BackendRendererUserData;


    // Functions
    private unsafe static void SetupRenderState(ImDrawData* draw_data, ID3D11DeviceContext* device_ctx)
    {
        Data* bd = GetBackendData();

        // Setup viewport
        Viewport vp = default;
        vp.Width = draw_data->DisplaySize.X * draw_data->FramebufferScale.X;
        vp.Height = draw_data->DisplaySize.Y * draw_data->FramebufferScale.Y;
        vp.MinDepth = 0.0f;
        vp.MaxDepth = 1.0f;
        vp.TopLeftX = 0;
        vp.TopLeftY = 0;
        device_ctx->RSSetViewports(1, &vp);

        // Setup orthographic projection matrix into our constant buffer
        // Our visible imgui space lies from draw_data->DisplayPos (top left) to draw_data->DisplayPos+data_data->DisplaySize (bottom right). DisplayPos is (0,0) for single viewport apps.
        MappedSubresource mapped_resource;
        if (device_ctx->Map((ID3D11Resource*)bd->pVertexConstantBuffer, 0, Map.WriteDiscard, 0, &mapped_resource) == 0)
        {
            ImGuiImpl.VERTEX_CONSTANT_BUFFER* constant_buffer = (ImGuiImpl.VERTEX_CONSTANT_BUFFER*)mapped_resource.PData;
            float L = draw_data->DisplayPos.X;
            float R = draw_data->DisplayPos.X + draw_data->DisplaySize.X;
            float T = draw_data->DisplayPos.Y;
            float B = draw_data->DisplayPos.Y + draw_data->DisplaySize.Y;
            float* mvp = stackalloc float[ImGuiImpl.VERTEX_CONSTANT_BUFFER.ElementCount]
            {
                2.0f/(R-L),   0.0f,           0.0f,       0.0f,
                0.0f,         2.0f/(T-B),     0.0f,       0.0f,
                0.0f,         0.0f,           0.5f,       0.0f,
                (R+L)/(L-R),  (T+B)/(B-T),    0.5f,       1.0f,
            };
            Buffer.MemoryCopy(mvp, constant_buffer->mvp, ImGuiImpl.VERTEX_CONSTANT_BUFFER.ByteWidth, ImGuiImpl.VERTEX_CONSTANT_BUFFER.ByteWidth);
            device_ctx->Unmap((ID3D11Resource*)bd->pVertexConstantBuffer, 0);
        }

        // Setup shader and vertex buffers
        uint stride = (uint)sizeof(ImDrawVert);
        uint offset = 0;
        device_ctx->IASetInputLayout(bd->pInputLayout);
        device_ctx->IASetVertexBuffers(0, 1, &bd->pVB, &stride, &offset);
        device_ctx->IASetIndexBuffer(bd->pIB, sizeof(ImDrawIdx) == 2 ? Format.FormatR16Uint : Format.FormatR32Uint, 0);
        device_ctx->IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        device_ctx->VSSetShader(bd->pVertexShader, null, 0);
        device_ctx->VSSetConstantBuffers(0, 1, &bd->pVertexConstantBuffer);
        device_ctx->PSSetShader(bd->pPixelShader, null, 0);
        device_ctx->PSSetSamplers(0, 1, &bd->pTexSamplerLinear);
        device_ctx->GSSetShader(null, null, 0);
        device_ctx->HSSetShader(null, null, 0); // In theory we should backup and restore this as well.. very infrequently used..
        device_ctx->DSSetShader(null, null, 0); // In theory we should backup and restore this as well.. very infrequently used..
        device_ctx->CSSetShader(null, null, 0); // In theory we should backup and restore this as well.. very infrequently used..

        // Setup render state
        float* blend_factor = stackalloc float[4] { 0.0f, 0.0f, 0.0f, 0.0f };
        device_ctx->OMSetBlendState(bd->pBlendState, blend_factor, 0xffffffff);
        device_ctx->OMSetDepthStencilState(bd->pDepthStencilState, 0);
        device_ctx->RSSetState(bd->pRasterizerState);
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

    // Render function
    public unsafe static void RenderDrawData(ImDrawData* draw_data)
    {
        // Avoid rendering when minimized
        if (draw_data->DisplaySize.X <= 0.0f || draw_data->DisplaySize.Y <= 0.0f)
            return;

        Data* bd = GetBackendData();
        ID3D11DeviceContext* device = bd->pd3dDeviceContext;

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

        // Create and grow vertex/index buffers if needed
        if (bd->pVB == null || bd->VertexBufferSize < draw_data->TotalVtxCount)
        {
            if (bd->pVB != null) { bd->pVB->Release(); bd->pVB = null; }
            bd->VertexBufferSize = draw_data->TotalVtxCount + 5000;
            BufferDesc desc = default;
            desc.Usage = Usage.Dynamic;
            desc.ByteWidth = (uint)(bd->VertexBufferSize * sizeof(ImDrawVert));
            desc.BindFlags = (uint)BindFlag.VertexBuffer;
            desc.CPUAccessFlags = (uint)CpuAccessFlag.Write;
            desc.MiscFlags = 0;
            if (bd->pd3dDevice->CreateBuffer(&desc, null, &bd->pVB) < 0)
                return;
        }
        if (bd->pIB == null || bd->IndexBufferSize < draw_data->TotalIdxCount)
        {
            if (bd->pIB != null) { bd->pIB->Release(); bd->pIB = null; }
            bd->IndexBufferSize = draw_data->TotalIdxCount + 10000;
            BufferDesc desc = default;
            desc.Usage = Usage.Dynamic;
            desc.ByteWidth = (uint)(bd->IndexBufferSize * sizeof(ImDrawIdx));
            desc.BindFlags = (uint)BindFlag.IndexBuffer;
            desc.CPUAccessFlags = (uint)CpuAccessFlag.Write;
            if (bd->pd3dDevice->CreateBuffer(&desc, null, &bd->pIB) < 0)
                return;
        }

        // Upload vertex/index data into a single contiguous GPU buffer
        MappedSubresource vtx_resource, idx_resource;
        if (device->Map((ID3D11Resource*)bd->pVB, 0, Map.WriteDiscard, 0, &vtx_resource) != 0)
            return;
        if (device->Map((ID3D11Resource*)bd->pIB, 0, Map.WriteDiscard, 0, &idx_resource) != 0)
            return;
        ImDrawVert* vtx_dst = (ImDrawVert*)vtx_resource.PData;
        ImDrawIdx* idx_dst = (ImDrawIdx*)idx_resource.PData;
        for (int n = 0; n < draw_data->CmdListsCount; n++)
        {
            ImDrawList* draw_list = draw_data->CmdLists.Data[n];
            Buffer.MemoryCopy(draw_list->VtxBuffer.Data, vtx_dst, (long)draw_list->VtxBuffer.Size * sizeof(ImDrawVert), (long)draw_list->VtxBuffer.Size * sizeof(ImDrawVert));
            Buffer.MemoryCopy(draw_list->IdxBuffer.Data, idx_dst, (long)draw_list->IdxBuffer.Size * sizeof(ImDrawIdx), (long)draw_list->IdxBuffer.Size * sizeof(ImDrawIdx));
            vtx_dst += draw_list->VtxBuffer.Size;
            idx_dst += draw_list->IdxBuffer.Size;
        }
        device->Unmap((ID3D11Resource*)bd->pVB, 0);
        device->Unmap((ID3D11Resource*)bd->pIB, 0);

        // Backup DX state that will be modified to restore it afterwards (unfortunately this is very ugly looking and verbose. Close your eyes!)
        BACKUP_DX11_STATE old = default;
        old.ScissorRectsCount = old.ViewportsCount = D3D11.ViewportAndScissorrectObjectCountPerPipeline;
        Box2D<int>* scissorRects = stackalloc Box2D<int>[(int)old.ScissorRectsCount];
        old.ScissorRects = scissorRects;
        device->RSGetScissorRects(&old.ScissorRectsCount, old.ScissorRects);
        Viewport* viewports = stackalloc Viewport[(int)old.ViewportsCount];
        old.Viewports = viewports;
        device->RSGetScissorRects(&old.ScissorRectsCount, old.ScissorRects);
        device->RSGetViewports(&old.ViewportsCount, old.Viewports);
        device->RSGetState(&old.RS);
        device->OMGetBlendState(&old.BlendState, old.BlendFactor, &old.SampleMask);
        device->OMGetDepthStencilState(&old.DepthStencilState, &old.StencilRef);
        device->PSGetShaderResources(0, 1, &old.PSShaderResource);
        device->PSGetSamplers(0, 1, &old.PSSampler);
        old.PSInstancesCount = old.VSInstancesCount = old.GSInstancesCount = 256;
        ID3D11ClassInstance** psInstances = stackalloc ID3D11ClassInstance*[(int)old.PSInstancesCount];
        old.PSInstances = psInstances;
        device->PSGetShader(&old.PS, old.PSInstances, &old.PSInstancesCount);
        ID3D11ClassInstance** vsInstances = stackalloc ID3D11ClassInstance*[(int)old.VSInstancesCount];
        old.VSInstances = vsInstances;
        device->VSGetShader(&old.VS, old.VSInstances, &old.VSInstancesCount);
        device->VSGetConstantBuffers(0, 1, &old.VSConstantBuffer);
        ID3D11ClassInstance** gsInstances = stackalloc ID3D11ClassInstance*[(int)old.GSInstancesCount];
        old.GSInstances = gsInstances;
        device->GSGetShader(&old.GS, old.GSInstances, &old.GSInstancesCount);

        device->IAGetPrimitiveTopology(&old.PrimitiveTopology);
        device->IAGetIndexBuffer(&old.IndexBuffer, &old.IndexBufferFormat, &old.IndexBufferOffset);
        device->IAGetVertexBuffers(0, 1, &old.VertexBuffer, &old.VertexBufferStride, &old.VertexBufferOffset);
        device->IAGetInputLayout(&old.InputLayout);

        // Setup desired DX state
        SetupRenderState(draw_data, device);

        // Setup render state structure (for callbacks and custom texture bindings)
        ImGuiPlatformIOPtr platform_io = ImGui.GetPlatformIO();
        RenderState render_state = default;
        render_state.Device = bd->pd3dDevice;
        render_state.DeviceContext = bd->pd3dDeviceContext;
        render_state.SamplerDefault = bd->pTexSamplerLinear;
        render_state.VertexConstantBuffer = bd->pVertexConstantBuffer;
        platform_io.RendererRenderState = &render_state;

        // Render command lists
        // (Because we merged all buffers into a single one, we maintain our own offset into them)
        int global_idx_offset = 0;
        int global_vtx_offset = 0;
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
                    if (pcmd->UserCallback == (void*)ImGui.ImDrawCallbackResetRenderState)
                        SetupRenderState(draw_data, device);
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
                    device->RSSetScissorRects(1, &r);

                    // Bind texture, Draw
                    ID3D11ShaderResourceView* texture_srv = (ID3D11ShaderResourceView*)pcmd->GetTexID();
                    device->PSSetShaderResources(0, 1, &texture_srv);
                    device->DrawIndexed(pcmd->ElemCount, pcmd->IdxOffset + (uint)global_idx_offset, (int)pcmd->VtxOffset + global_vtx_offset);
                }
            }
            global_idx_offset += draw_list->IdxBuffer.Size;
            global_vtx_offset += draw_list->VtxBuffer.Size;
        }
        platform_io.RendererRenderState = null;

        // Restore modified DX state
        device->RSSetScissorRects(old.ScissorRectsCount, old.ScissorRects);
        device->RSSetViewports(old.ViewportsCount, old.Viewports);
        device->RSSetState(old.RS); if (old.RS != null) old.RS->Release();
        device->OMSetBlendState(old.BlendState, old.BlendFactor, old.SampleMask); if (old.BlendState != null) old.BlendState->Release();
        device->OMSetDepthStencilState(old.DepthStencilState, old.StencilRef); if (old.DepthStencilState != null) old.DepthStencilState->Release();
        device->PSSetShaderResources(0, 1, &old.PSShaderResource); if (old.PSShaderResource != null) old.PSShaderResource->Release();
        device->PSSetSamplers(0, 1, &old.PSSampler); if (old.PSSampler != null) old.PSSampler->Release();
        device->PSSetShader(old.PS, old.PSInstances, old.PSInstancesCount); if (old.PS != null) old.PS->Release();
        for (uint i = 0; i < old.PSInstancesCount; i++) if (old.PSInstances[i] != null) old.PSInstances[i]->Release();
        device->VSSetShader(old.VS, old.VSInstances, old.VSInstancesCount); if (old.VS != null) old.VS->Release();
        device->VSSetConstantBuffers(0, 1, &old.VSConstantBuffer); if (old.VSConstantBuffer != null) old.VSConstantBuffer->Release();
        device->GSSetShader(old.GS, old.GSInstances, old.GSInstancesCount); if (old.GS != null) old.GS->Release();
        for (uint i = 0; i < old.VSInstancesCount; i++) if (old.VSInstances[i] != null) old.VSInstances[i]->Release();
        device->IASetPrimitiveTopology(old.PrimitiveTopology);
        device->IASetIndexBuffer(old.IndexBuffer, old.IndexBufferFormat, old.IndexBufferOffset); if (old.IndexBuffer != null) old.IndexBuffer->Release();
        device->IASetVertexBuffers(0, 1, &old.VertexBuffer, &old.VertexBufferStride, &old.VertexBufferOffset); if (old.VertexBuffer != null) old.VertexBuffer->Release();
        device->IASetInputLayout(old.InputLayout); if (old.InputLayout != null) old.InputLayout->Release();
    }

    private unsafe static void DestroyTexture(ImTextureData* tex)
    {
        Texture* backend_tex = (Texture*)tex->BackendUserData;
        if (backend_tex != null)
        {
            if (backend_tex->pTextureView != (ID3D11ShaderResourceView*)tex->TexID)
                throw new InvalidOperationException("TextureView mismatch: backend_tex->pTextureView != tex->TexID.");
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
            //Log.Debug(string.Format("UpdateTexture #{0:000}: WantCreate {1}x{2}", tex->UniqueID, tex->Width, tex->Height));
            if (tex->TexID != ImTextureID.Null || tex->BackendUserData != null)
                throw new InvalidOperationException("Expected TexID to be null and BackendUserData to be null.");
            if (tex->Format != ImTextureFormat.Rgba32)
                throw new InvalidOperationException("Expected texture format RGBA32.");
            uint* pixels = (uint*)tex->GetPixels();
            Texture* backend_tex = (Texture*)ImGui.MemAlloc((nuint)sizeof(Texture));
            *backend_tex = default;

            // Create texture
            Texture2DDesc desc = default;
            desc.Width = (uint)tex->Width;
            desc.Height = (uint)tex->Height;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = Format.FormatR8G8B8A8Unorm;
            desc.SampleDesc = new SampleDesc(1, 0);
            desc.Usage = Usage.Default;
            desc.BindFlags = (uint)BindFlag.ShaderResource;
            desc.CPUAccessFlags = 0;
            SubresourceData subResource = new SubresourceData
            {
                PSysMem = pixels,
                SysMemPitch = desc.Width * 4,
                SysMemSlicePitch = 0
            };
            bd->pd3dDevice->CreateTexture2D(&desc, &subResource, &backend_tex->pTexture);
            if (backend_tex->pTexture == null)
                throw new InvalidOperationException("Backend failed to create texture!");

            // Create texture view
            ShaderResourceViewDesc srvDesc = new ShaderResourceViewDesc
            {
                Format = Format.FormatR8G8B8A8Unorm,
                ViewDimension = D3DSrvDimension.D3D11SrvDimensionTexture2D,
                Texture2D = new Tex2DSrv
                {
                    MipLevels = desc.MipLevels,
                    MostDetailedMip = 0
                }
            };
            bd->pd3dDevice->CreateShaderResourceView((ID3D11Resource*)backend_tex->pTexture, &srvDesc, &backend_tex->pTextureView);
            if (backend_tex->pTextureView == null)
                throw new InvalidOperationException("Backend failed to create texture!");

            // Store identifiers
            tex->SetTexID((nint)backend_tex->pTextureView);
            tex->SetStatus(ImTextureStatus.Ok);
            tex->BackendUserData = backend_tex;
        }
        else if (tex->Status == ImTextureStatus.WantUpdates)
        {
            // Update selected blocks. We only ever write to textures regions which have never been used before!
            // This backend choose to use tex->Updates[] but you can use tex->UpdateRect to upload a single region.
            Texture* backend_tex = (Texture*)tex->BackendUserData;
            if (backend_tex->pTextureView != (ID3D11ShaderResourceView*)tex->TexID)
                throw new InvalidOperationException("TextureView mismatch: backend_tex->pTextureView != tex->TexID.");
            for (int i = 0; i < tex->Updates.Size; i++)
            {
                ImTextureRect* r = &tex->Updates.Data[i];
                Box box = new(r->X, r->Y, 0, (uint)(r->X + r->W), (uint)(r->Y + r->H), 1);
                bd->pd3dDeviceContext->UpdateSubresource((ID3D11Resource*)backend_tex->pTexture, 0, &box, tex->GetPixelsAt(r->X, r->Y), (uint)tex->GetPitch(), 0);
            }
            tex->SetStatus(ImTextureStatus.Ok);
        }
        if (tex->Status == ImTextureStatus.WantDestroy && tex->UnusedFrames > 0)
            DestroyTexture(tex);
    }

    private unsafe static bool CreateDeviceObjects()
    {
        Data* bd = GetBackendData();
        if (bd->pd3dDevice == null)
            return false;
        InvalidateDeviceObjects();

        // By using D3DCompile() from <d3dcompiler.h> / d3dcompiler.lib, we introduce a dependency to a given version of d3dcompiler_XX.dll (see D3DCOMPILER_DLL_A)
        // If you would like to use this DX11 sample code but remove this dependency you can:
        //  1) compile once, save the compiled shader blobs into a file or source code and pass them to CreateVertexShader()/CreatePixelShader() [preferred solution]
        //  2) use code to detect any version of the DLL and grab a pointer to D3DCompile from the DLL.
        // See https://github.com/ocornut/imgui/pull/638 for sources and details.

        // Create the vertex shader
        {
            ID3D10Blob* vertexShaderBlob;
            if (SharedAPI.D3DCompiler.Compile((void*)_vsSrc, (nuint)_vertexShader.Length, (byte*)0, null, null, (byte*)_entryMain, (byte*)_vsTarget, 0, 0, &vertexShaderBlob, null) < 0)
                return false; // NB: Pass ID3DBlob* pErrorBlob to D3DCompile() to get error showing in (const char*)pErrorBlob->GetBufferPointer(). Make sure to Release() the blob!
            if (bd->pd3dDevice->CreateVertexShader(vertexShaderBlob->GetBufferPointer(), vertexShaderBlob->GetBufferSize(), null, &bd->pVertexShader) != 0)
            {
                vertexShaderBlob->Release();
                return false;
            }

            // Create the input layout
            fixed (InputElementDesc* inputElements = _inputElements)
                if (bd->pd3dDevice->CreateInputLayout(inputElements, 3, vertexShaderBlob->GetBufferPointer(), vertexShaderBlob->GetBufferSize(), &bd->pInputLayout) != 0)
                {
                    vertexShaderBlob->Release();
                    return false;
                }
            vertexShaderBlob->Release();

            // Create the constant buffer
            {
                BufferDesc desc = new BufferDesc
                {
                    ByteWidth = ImGuiImpl.VERTEX_CONSTANT_BUFFER.ByteWidth,
                    Usage = Usage.Dynamic,
                    BindFlags = (uint)BindFlag.ConstantBuffer,
                    CPUAccessFlags = (uint)CpuAccessFlag.Write,
                    MiscFlags = 0
                };
                bd->pd3dDevice->CreateBuffer(&desc, null, &bd->pVertexConstantBuffer);
            }
        }

        // Create the pixel shader
        {
            ID3D10Blob* pixelShaderBlob;
            if (SharedAPI.D3DCompiler.Compile((void*)_psSrc, (nuint)_pixelShader.Length, (byte*)0, null, null, (byte*)_entryMain, (byte*)_psTarget, 0, 0, &pixelShaderBlob, null) < 0)
                return false; // NB: Pass ID3DBlob* pErrorBlob to D3DCompile() to get error showing in (const char*)pErrorBlob->GetBufferPointer(). Make sure to Release() the blob!
            if (bd->pd3dDevice->CreatePixelShader(pixelShaderBlob->GetBufferPointer(), pixelShaderBlob->GetBufferSize(), null, &bd->pPixelShader) != 0)
            {
                pixelShaderBlob->Release();
                return false;
            }
            pixelShaderBlob->Release();
        }

        // Create the blending setup
        {
            BlendDesc desc = new BlendDesc
            {
                AlphaToCoverageEnable = 0,
                IndependentBlendEnable = 0,
                RenderTarget =
                {
                    Element0 = new RenderTargetBlendDesc
                    {
                        BlendEnable = 1,
                        SrcBlend = Blend.SrcAlpha,
                        DestBlend = Blend.InvSrcAlpha,
                        BlendOp = BlendOp.Add,
                        SrcBlendAlpha = Blend.One,
                        DestBlendAlpha = Blend.InvSrcAlpha,
                        BlendOpAlpha = BlendOp.Add,
                        RenderTargetWriteMask = (byte)ColorWriteEnable.All,
                    }
                }
            };
            bd->pd3dDevice->CreateBlendState(&desc, &bd->pBlendState);
        }

        // Create the rasterizer state
        {
            RasterizerDesc desc = new RasterizerDesc
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                ScissorEnable = 1,
                DepthClipEnable = 1,
            };
            bd->pd3dDevice->CreateRasterizerState(&desc, &bd->pRasterizerState);
        }

        // Create depth-stencil State
        {
            DepthStencilDesc desc = new DepthStencilDesc
            {
                DepthEnable = 0,
                DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunc.Always,
                StencilEnable = 0,
                FrontFace = new DepthStencilopDesc
                {
                    StencilFailOp = StencilOp.Keep,
                    StencilDepthFailOp = StencilOp.Keep,
                    StencilPassOp = StencilOp.Keep,
                    StencilFunc = ComparisonFunc.Always,
                },
                BackFace = new DepthStencilopDesc
                {
                    StencilFailOp = StencilOp.Keep,
                    StencilDepthFailOp = StencilOp.Keep,
                    StencilPassOp = StencilOp.Keep,
                    StencilFunc = ComparisonFunc.Always,
                },
            };
            bd->pd3dDevice->CreateDepthStencilState(&desc, &bd->pDepthStencilState);
        }

        // Create texture sampler
        // (Bilinear sampling is required by default. Set 'io.Fonts->Flags |= ImFontAtlasFlags_NoBakedLines' or 'style.AntiAliasedLinesUseTex = false' to allow point/nearest sampling)
        {
            SamplerDesc desc = new SamplerDesc
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MipLODBias = 0.0f,
                ComparisonFunc = ComparisonFunc.Always,
                MinLOD = 0.0f,
                MaxLOD = 0.0f,
            };
            bd->pd3dDevice->CreateSamplerState(&desc, &bd->pTexSamplerLinear);
        }

        return true;
    }

    private unsafe static void InvalidateDeviceObjects()
    {
        Data* bd = GetBackendData();
        if (bd->pd3dDevice == null)
            return;

        // Destroy all textures
        ImGuiPlatformIOPtr platform_io = ImGui.GetPlatformIO();
        ImVector<ImTextureDataPtr> textures = platform_io.Textures;
        for (int i = 0; i < textures.Size; i++)
        {
            ImTextureData* tex = textures.Data[i];
            if (tex->RefCount == 1)
                DestroyTexture(tex);
        }

        if (bd->pTexSamplerLinear != null) { bd->pTexSamplerLinear->Release(); bd->pTexSamplerLinear = null; }
        if (bd->pIB != null) { bd->pIB->Release(); bd->pIB = null; }
        if (bd->pVB != null) { bd->pVB->Release(); bd->pVB = null; }
        if (bd->pBlendState != null) { bd->pBlendState->Release(); bd->pBlendState = null; }
        if (bd->pDepthStencilState != null) { bd->pDepthStencilState->Release(); bd->pDepthStencilState = null; }
        if (bd->pRasterizerState != null) { bd->pRasterizerState->Release(); bd->pRasterizerState = null; }
        if (bd->pPixelShader != null) { bd->pPixelShader->Release(); bd->pPixelShader = null; }
        if (bd->pVertexConstantBuffer != null) { bd->pVertexConstantBuffer->Release(); bd->pVertexConstantBuffer = null; }
        if (bd->pInputLayout != null) { bd->pInputLayout->Release(); bd->pInputLayout = null; }
        if (bd->pVertexShader != null) { bd->pVertexShader->Release(); bd->pVertexShader = null; }
    }

    public unsafe static bool Init(ID3D11Device* device, ID3D11DeviceContext* device_context)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        if (io.BackendRendererUserData != null)
            throw new InvalidOperationException("Already initialized a renderer backend!");

        // Setup backend capabilities flags
        Data* bd = (Data*)ImGui.MemAlloc((nuint)sizeof(Data));
        *bd = new();
        io.BackendRendererUserData = bd;
        io.BackendRendererName = (byte*)Marshal.StringToHGlobalAnsi($"imgui_impl_dx11_{DearImGuiInjectionCore.BackendVersion}");
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;  // We can honor the ImDrawCmd::VtxOffset field, allowing for large meshes.
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;   // We can honor ImGuiPlatformIO::Textures[] requests during render.

        ImGuiPlatformIOPtr platform_io = ImGui.GetPlatformIO();
        platform_io.RendererTextureMaxWidth = platform_io.RendererTextureMaxHeight = D3D11.ReqTexture2DUOrVDimension; // D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION

        bd->pd3dDevice = device;
        bd->pd3dDevice->AddRef();
        bd->pd3dDeviceContext = device_context;
        bd->pd3dDeviceContext->AddRef();

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
        Marshal.FreeHGlobal(_entryMain);
        Marshal.FreeHGlobal(_vsSrc);
        Marshal.FreeHGlobal(_vsTarget);
        Marshal.FreeHGlobal(_inputElementPos);
        Marshal.FreeHGlobal(_inputElementUv);
        Marshal.FreeHGlobal(_inputElementCol);
        Marshal.FreeHGlobal(_psSrc);
        Marshal.FreeHGlobal(_psTarget);
        if (bd->pd3dDevice != null)
            bd->pd3dDevice->Release();
        if (bd->pd3dDeviceContext != null)
            bd->pd3dDeviceContext->Release();

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
            throw new InvalidOperationException("Context or backend not initialized! Did you call ImGui_ImplDX11_Init()?");

        if (bd->pVertexShader == null)
            if (!CreateDeviceObjects())
                throw new InvalidOperationException("ImGui_ImplDX11_CreateDeviceObjects() failed!");
    }
}