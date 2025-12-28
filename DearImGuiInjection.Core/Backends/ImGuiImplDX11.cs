using Hexa.NET.ImGui;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;

using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using ImDrawIdx = ushort;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace DearImGuiInjection.Backends;

internal static class ImGuiImplDX11
{
    private static int _nextId = 1;
    private static readonly Dictionary<int, Data> _map = new();

    private const int D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE = 16;
    private static readonly RawRectangle[] _scissorRects = 
        new RawRectangle[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];
    private static readonly RawViewportF[] _viewports = 
        new RawViewportF[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];

    private static readonly int _imDrawVertSizeOf = Marshal.SizeOf<ImDrawVert>();
    private static readonly int _imDrawIdxSizeOf = Marshal.SizeOf<ImDrawIdx>();
    private static readonly int _textureSizeOf = Marshal.SizeOf<Texture>();
    private static readonly int _vertexConstantBufferSizeOf = Marshal.SizeOf<VERTEX_CONSTANT_BUFFER_DX11>();

    private static readonly InputElement[] _inputElements = new InputElement[]
    {
        new("POSITION", 0, Format.R32G32_Float, (int)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.Pos)),
            0, InputClassification.PerVertexData, 0),
        new("TEXCOORD", 0, Format.R32G32_Float, (int)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.Uv)),
            0, InputClassification.PerVertexData, 0),
        new("COLOR", 0, Format.R8G8B8A8_UNorm, (int)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.Col)),
            0, InputClassification.PerVertexData, 0)
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture
    {
        public IntPtr pTexture; // Texture2D
        public IntPtr pTextureView; // ShaderResourceView
    }

    private class Data
    {
        public Device Device;
        public DeviceContext DeviceContext;
        public SamplerState TexSamplerLinear;
        public VertexShader VertexShader;
        public PixelShader PixelShader;
        public InputLayout InputLayout;
        public Buffer VertexConstantBuffer;
        public BlendState BlendState;
        public RasterizerState RasterizerState;
        public DepthStencilState DepthStencilState;
        public Buffer VertexBuffer;
        public Buffer IndexBuffer;
        public int VertexBufferSize;
        public int IndexBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VERTEX_CONSTANT_BUFFER_DX11
    {
        public fixed float mvp[4 * 4];
    }

    private struct BACKUP_DX11_STATE
    {
        public RawRectangle[] ScissorRects;
        public RawViewportF[] Viewports;
        public RasterizerState RS;
        public BlendState BlendState;
        public RawColor4 BlendFactor;
        public int SampleMask;
        public int StencilRef;
        public DepthStencilState DepthStencilState;
        public ShaderResourceView PSShaderResource;
        public SamplerState PSSampler;
        public PixelShader PS;
        public VertexShader VS;
        public GeometryShader GS;
        public PrimitiveTopology PrimitiveTopology;
        public Buffer VSConstantBuffer, IndexBuffer;
        public int IndexBufferOffset;
        public Format IndexBufferFormat;
        public VertexBufferBinding VertexBufferBinding;
        public InputLayout InputLayout;
    }

    // [BETA] Selected render state data shared with callbacks.
    // This is temporarily stored in GetPlatformIO().Renderer_RenderState during the ImGui_ImplDX11_RenderDrawData() call.
    // (Please open an issue if you feel you need access to more data)
    [StructLayout(LayoutKind.Sequential)]
    private struct RenderState
    {
        public IntPtr Device;
        public IntPtr DeviceContext;
        public IntPtr SamplerDefault;
        public IntPtr VertexConstantBuffer;
    }

    private unsafe class ImDrawCallback
    {
        public static void* ResetRenderState = (void*)-8;
        public delegate void Delegate(ImDrawList* parent_list, ImDrawCmd* cmd);
    }

    // Backend data stored in io.BackendRendererUserData to allow support for multiple Dear ImGui contexts
    // It is STRONGLY preferred that you use docking branch with multi-viewports (== single Dear ImGui context + multiple windows) instead of multiple Dear ImGui contexts.
    private unsafe static Data GetBackendData()
    {
        var io = ImGui.GetIO();
        if (io.BackendRendererUserData == null)
            return null;
        int id = Marshal.ReadInt32((IntPtr)io.BackendRendererUserData);
        return _map.TryGetValue(id, out var data) ? data : null;
    }

    private unsafe static Data InitBackendData()
    {
        var io = ImGui.GetIO();
        int id = _nextId++;
        Data data = new Data();
        _map[id] = data;
        IntPtr ptr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(ptr, id);
        io.BackendRendererUserData = (void*)ptr;
        return data;
    }

    private unsafe static void FreeBackendData()
    {
        var io = ImGui.GetIO();
        if (io.BackendRendererUserData == null)
            return;
        IntPtr ptr = (IntPtr)io.BackendRendererUserData;
        int id = Marshal.ReadInt32(ptr);
        _map.Remove(id);
        Marshal.FreeHGlobal(ptr);
        io.BackendRendererUserData = null;
    }

    // Functions
    private unsafe static void SetupRenderState(ImDrawData* draw_data, DeviceContext device_ctx)
    {
        Data bd = GetBackendData();

        // Setup viewport
        RawViewportF vp = new RawViewportF()
        {
            Width = draw_data->DisplaySize.X * draw_data->FramebufferScale.X,
            Height = draw_data->DisplaySize.Y * draw_data->FramebufferScale.Y,
            MinDepth = 0.0f,
            MaxDepth = 1.0f,
            X = 0,
            Y = 0
        };
        device_ctx.Rasterizer.SetViewport(vp);

        // Setup orthographic projection matrix into our constant buffer
        // Our visible imgui space lies from draw_data->DisplayPos (top left) to draw_data->DisplayPos+data_data->DisplaySize (bottom right). DisplayPos is (0,0) for single viewport apps.
        device_ctx.MapSubresource(bd.VertexConstantBuffer, MapMode.WriteDiscard, MapFlags.None, out var mapped_resource);
        VERTEX_CONSTANT_BUFFER_DX11* constant_buffer = (VERTEX_CONSTANT_BUFFER_DX11*)mapped_resource.DataPointer;
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
        device_ctx.UnmapSubresource(bd.VertexConstantBuffer, 0);

        // Setup shader and vertex buffers
        device_ctx.InputAssembler.InputLayout = bd.InputLayout;
        device_ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding()
        {
            Stride = _imDrawVertSizeOf,
            Offset = 0,
            Buffer = bd.VertexBuffer
        });
        device_ctx.InputAssembler.SetIndexBuffer(bd.IndexBuffer, Format.R16_UInt, 0);
        device_ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        device_ctx.VertexShader.SetShader(bd.VertexShader, null, 0);
        device_ctx.VertexShader.SetConstantBuffer(0, bd.VertexConstantBuffer);
        device_ctx.PixelShader.SetShader(bd.PixelShader, null, 0);
        device_ctx.PixelShader.SetSampler(0, bd.TexSamplerLinear);
        device_ctx.GeometryShader.SetShader(null, null, 0);
        device_ctx.HullShader.SetShader(null, null, 0); // In theory we should backup and restore this as well.. very infrequently used..
        device_ctx.DomainShader.SetShader(null, null, 0); // In theory we should backup and restore this as well.. very infrequently used..
        device_ctx.ComputeShader.SetShader(null, null, 0); // In theory we should backup and restore this as well.. very infrequently used..

        // Setup blend state
        device_ctx.OutputMerger.SetBlendState(bd.BlendState, new(0.0f, 0.0f, 0.0f, 0.0f), 0xffffffff);
        device_ctx.OutputMerger.SetDepthStencilState(bd.DepthStencilState, 0);
        device_ctx.Rasterizer.State = bd.RasterizerState;
    }

    // Render function
    public unsafe static void RenderDrawData(ImDrawData* draw_data)
    {
        // Avoid rendering when minimized
        if (draw_data->DisplaySize.X <= 0.0f || draw_data->DisplaySize.Y <= 0.0f)
            return;

        Data bd = GetBackendData();
        DeviceContext device = bd.DeviceContext;

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
        if (bd.VertexBuffer == null || bd.VertexBufferSize < draw_data->TotalVtxCount)
        {
            bd.VertexBuffer?.Dispose();
            bd.VertexBufferSize = draw_data->TotalVtxCount + 5000;
            bd.VertexBuffer = new Buffer(bd.Device, new BufferDescription
            {
                Usage = ResourceUsage.Dynamic,
                SizeInBytes = bd.VertexBufferSize * _imDrawVertSizeOf,
                BindFlags = BindFlags.VertexBuffer,
                CpuAccessFlags = CpuAccessFlags.Write
            });
        }

        if (bd.IndexBuffer == null || bd.IndexBufferSize < draw_data->TotalIdxCount)
        {
            bd.IndexBuffer?.Dispose();
            bd.IndexBufferSize = draw_data->TotalIdxCount + 10000;
            bd.IndexBuffer = new Buffer(bd.Device, new BufferDescription
            {
                Usage = ResourceUsage.Dynamic,
                SizeInBytes = bd.IndexBufferSize * _imDrawIdxSizeOf,
                BindFlags = BindFlags.IndexBuffer,
                CpuAccessFlags = CpuAccessFlags.Write
            });
        }

        // Upload vertex/index data into a single contiguous GPU buffer
        device.MapSubresource(bd.VertexBuffer, MapMode.WriteDiscard, MapFlags.None, out DataStream vtx_resource);
        device.MapSubresource(bd.IndexBuffer, MapMode.WriteDiscard, MapFlags.None, out DataStream idx_resource);
        ImDrawVert* vtx_dst = (ImDrawVert*)vtx_resource.DataPointer;
        ImDrawIdx* idx_dst = (ImDrawIdx*)idx_resource.DataPointer;
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
        device.UnmapSubresource(bd.VertexBuffer, 0);
        device.UnmapSubresource(bd.IndexBuffer, 0);

        // Backup DX state that will be modified to restore it afterwards (unfortunately this is very ugly looking and verbose. Close your eyes!)
        BACKUP_DX11_STATE old = new BACKUP_DX11_STATE()
        {
            ScissorRects = _scissorRects,
            Viewports = _viewports,
        };
        device.Rasterizer.GetScissorRectangles(old.ScissorRects);
        device.Rasterizer.GetViewports(old.Viewports);
        old.RS = device.Rasterizer.State;
        old.BlendState = device.OutputMerger.GetBlendState(out old.BlendFactor, out old.SampleMask);
        old.DepthStencilState = device.OutputMerger.GetDepthStencilState(out old.StencilRef);
        old.PSShaderResource = device.PixelShader.GetShaderResources(0, 1)[0];
        old.PSSampler = device.PixelShader.GetSamplers(0, 1)[0];
        old.PS = device.PixelShader.Get();
        old.VS = device.VertexShader.Get();
        old.VSConstantBuffer = device.VertexShader.GetConstantBuffers(0, 1)[0];
        old.GS = device.GeometryShader.Get();
        old.PrimitiveTopology = device.InputAssembler.PrimitiveTopology;
        device.InputAssembler.GetIndexBuffer(out old.IndexBuffer, out old.IndexBufferFormat, out old.IndexBufferOffset);
        device.InputAssembler.GetVertexBuffers(0, 1, [ old.VertexBufferBinding.Buffer ], [ old.VertexBufferBinding.Stride ],
            [ old.VertexBufferBinding.Offset ]);
        old.InputLayout = device.InputAssembler.InputLayout;

        // Setup desired DX state
        SetupRenderState(draw_data, device);

        // Setup render state structure (for callbacks and custom texture bindings)
        var platform_io = ImGui.GetPlatformIO();
        RenderState renderState;
        renderState.Device = bd.Device.NativePointer;
        renderState.DeviceContext = bd.DeviceContext.NativePointer;
        renderState.SamplerDefault = bd.TexSamplerLinear.NativePointer;
        renderState.VertexConstantBuffer = bd.VertexConstantBuffer.NativePointer;
        platform_io.RendererRenderState = &renderState;

        // Render command lists
        // (Because we merged all buffers into a single one, we maintain our own offset into them)
        int global_idx_offset = 0;
        int global_vtx_offset = 0;
        var clip_off = draw_data->DisplayPos;
        Vector2 clip_scale = draw_data->FramebufferScale;
        for (int n = 0; n < draw_data->CmdListsCount; n++)
        {
            var cmd_list = draw_data->CmdLists[n].Handle;
            for (int cmd_i = 0; cmd_i < cmd_list->CmdBuffer.Size; cmd_i++)
            {
                var pcmd = cmd_list->CmdBuffer[cmd_i];
                if (pcmd.UserCallback != null)
                {
                    // User callback, registered via ImDrawList::AddCallback()
                    // (ImDrawCallback_ResetRenderState is a special callback value used by the user to request the renderer to reset render state.)
                    if (pcmd.UserCallback == ImDrawCallback.ResetRenderState)
                        SetupRenderState(draw_data, device);
                    else
                    {
                        var userCallback = Marshal.GetDelegateForFunctionPointer<ImDrawCallback.Delegate>((IntPtr)pcmd.UserCallback);
                        userCallback(cmd_list, &pcmd);
                    }
                }
                else
                {
                    // Project scissor/clipping rectangles into framebuffer space
                    var clip_min = new Vector2((pcmd.ClipRect.X - clip_off.X) * clip_scale.X, (pcmd.ClipRect.Y - clip_off.Y) * clip_scale.Y);
                    var clip_max = new Vector2((pcmd.ClipRect.Z - clip_off.X) * clip_scale.X, (pcmd.ClipRect.W - clip_off.Y) * clip_scale.Y);
                    if (clip_max.X <= clip_min.X || clip_max.Y <= clip_min.Y)
                        continue;

                    // Apply scissor/clipping rectangle
                    device.Rasterizer.SetScissorRectangle((int)clip_min.X, (int)clip_min.Y, (int)clip_max.X, (int)clip_max.Y);

                    // Bind texture, Draw
                    device.PixelShader.SetShaderResource(0, new(pcmd.GetTexID()));
                    device.DrawIndexed((int)pcmd.ElemCount, (int)(pcmd.IdxOffset + global_idx_offset), (int)(pcmd.VtxOffset + global_vtx_offset));
                }
            }
            global_idx_offset += cmd_list->IdxBuffer.Size;
            global_vtx_offset += cmd_list->VtxBuffer.Size;
        }
        platform_io.RendererRenderState = null;

        // Restore modified DX state
        device.Rasterizer.SetScissorRectangles(old.ScissorRects);
        device.Rasterizer.SetViewports(old.Viewports);
        device.Rasterizer.State = old.RS;
        device.OutputMerger.SetBlendState(old.BlendState, old.BlendFactor, old.SampleMask);
        device.OutputMerger.SetDepthStencilState(old.DepthStencilState, old.StencilRef);
        device.PixelShader.SetShaderResource(0, old.PSShaderResource);
        device.PixelShader.SetSampler(0, old.PSSampler);
        device.PixelShader.Set(old.PS);
        device.VertexShader.Set(old.VS);
        device.VertexShader.SetConstantBuffer(0, old.VSConstantBuffer);
        device.GeometryShader.Set(old.GS);
        device.InputAssembler.PrimitiveTopology = old.PrimitiveTopology;
        device.InputAssembler.SetIndexBuffer(old.IndexBuffer, old.IndexBufferFormat, old.IndexBufferOffset);
        device.InputAssembler.SetVertexBuffers(0, old.VertexBufferBinding);
        device.InputAssembler.InputLayout = old.InputLayout;
    }

    private unsafe static void DestroyTexture(ImTextureData* tex)
    {
        Texture* backend_tex = (Texture*)tex->BackendUserData;
        if (backend_tex != null)
        {
            Debug.Assert(backend_tex->pTextureView == (IntPtr)tex->GetTexID());
            Marshal.Release(backend_tex->pTexture);
            Marshal.Release(backend_tex->pTextureView);
            Marshal.FreeHGlobal((IntPtr)backend_tex);

            // Clear identifiers and mark as destroyed (in order to allow e.g. calling InvalidateDeviceObjects while running)
            tex->SetTexID(ImTextureID.Null);
            tex->BackendUserData = null;
        }
        tex->SetStatus(ImTextureStatus.Destroyed);
    }

    private unsafe static void UpdateTexture(ImTextureData* tex)
    {
        Data bd = GetBackendData();
        if (tex->Status == ImTextureStatus.WantCreate)
        {
            // Create and upload new texture to graphics system
            //Log.Debug(string.Format("UpdateTexture #%03d: WantCreate %dx%d\n", tex->UniqueID, tex->Width, tex->Height));
            Debug.Assert(tex->TexID == ImTextureID.Null && tex->BackendUserData == null);
            Debug.Assert(tex->Format == ImTextureFormat.Rgba32);
            IntPtr pixels = (IntPtr)tex->GetPixels();
            Texture* backend_tex = (Texture*)Marshal.AllocHGlobal(_textureSizeOf);

            // Create texture
            var desc = new Texture2DDescription();
            desc.Width = tex->Width;
            desc.Height = tex->Height;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = Format.R8G8B8A8_UNorm;
            desc.SampleDescription.Count = 1;
            desc.Usage = ResourceUsage.Default;
            desc.BindFlags = BindFlags.ShaderResource;
            desc.CpuAccessFlags = CpuAccessFlags.None;
            Texture2D texture2D = new(bd.Device, desc, new DataRectangle(pixels, desc.Width * 4));
            Debug.Assert(texture2D != null, "Backend failed to create texture!");
            backend_tex->pTexture = texture2D.NativePointer;
            Marshal.AddRef(backend_tex->pTexture);

            // Create texture view
            var srvDesc = new ShaderResourceViewDescription();
            srvDesc.Format = Format.R8G8B8A8_UNorm;
            srvDesc.Dimension = ShaderResourceViewDimension.Texture2D;
            srvDesc.Texture2D.MipLevels = desc.MipLevels;
            srvDesc.Texture2D.MostDetailedMip = 0;
            ShaderResourceView shaderResourceView = new(bd.Device, texture2D, srvDesc);
            Debug.Assert(shaderResourceView != null, "Backend failed to create texture!");
            backend_tex->pTextureView = shaderResourceView.NativePointer;
            Marshal.AddRef(backend_tex->pTextureView);

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
            Debug.Assert(backend_tex->pTextureView == (IntPtr)tex->GetTexID());
            var texture2D = new Texture2D(backend_tex->pTexture);
            for (int n = 0; n < tex->Updates.Size; n++)
            {
                var r = tex->Updates[n];
                var box = new ResourceRegion() 
                {
                    Left = r.X,
                    Top = r.Y,
                    Front = 0,
                    Right = r.X + r.W,
                    Bottom = r.Y + r.H,
                    Back = 1
                };
                bd.DeviceContext.UpdateSubresource(texture2D, 0, box, (IntPtr)tex->GetPixelsAt(r.X, r.Y), tex->GetPitch(), 0);
            }
            tex->SetStatus(ImTextureStatus.Ok);
        }
        if (tex->Status == ImTextureStatus.WantDestroy && tex->UnusedFrames > 0)
            DestroyTexture(tex);
    }

    public static bool CreateDeviceObjects()
    {
        Data bd = GetBackendData();
        if (bd.Device == null)
            return false;
        InvalidateDeviceObjects();

        // By using D3DCompile() from <d3dcompiler.h> / d3dcompiler.lib, we introduce a dependency to a given version of d3dcompiler_XX.dll (see D3DCOMPILER_DLL_A)
        // If you would like to use this DX11 sample code but remove this dependency you can:
        //  1) compile once, save the compiled shader blobs into a file or source code and pass them to CreateVertexShader()/CreatePixelShader() [preferred solution]
        //  2) use code to detect any version of the DLL and grab a pointer to D3DCompile from the DLL.
        // See https://github.com/ocornut/imgui/pull/638 for sources and details.

        // Create the vertex shader
        const string vertexShader = @"
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

        CompilationResult vertexShaderBlob = ShaderBytecode.Compile(vertexShader, "main", "vs_4_0", ShaderFlags.None, EffectFlags.None);
        if (vertexShaderBlob.HasErrors)
        {
            vertexShaderBlob?.Dispose();
            return false;
        }
        bd.VertexShader = new VertexShader(bd.Device, vertexShaderBlob.Bytecode);

        // Create the input layout
        bd.InputLayout = new InputLayout(bd.Device, vertexShaderBlob.Bytecode, _inputElements);
        vertexShaderBlob.Dispose();

        // Create the constant buffer
        bd.VertexConstantBuffer = new Buffer(bd.Device, new BufferDescription
        {
            SizeInBytes = _vertexConstantBufferSizeOf,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CpuAccessFlags = CpuAccessFlags.Write,
            OptionFlags = ResourceOptionFlags.None
        });

        const string pixelShader = @"
            struct PS_INPUT
            {
                float4 pos : SV_POSITION;
                float4 col : COLOR0;
                float2 uv  : TEXCOORD0;
            };

            SamplerState sampler0 : register(s0);
            Texture2D texture0 : register(t0);

            float4 main(PS_INPUT input) : SV_Target
            {
                float4 out_col = input.col * texture0.Sample(sampler0, input.uv);
                return out_col;
            }";

        CompilationResult pixelShaderBlob = ShaderBytecode.Compile(pixelShader, "main", "ps_4_0", ShaderFlags.None, EffectFlags.None);
        if (pixelShaderBlob.HasErrors)
        {
            pixelShaderBlob?.Dispose();
            return false;
        }
        bd.PixelShader = new PixelShader(bd.Device, pixelShaderBlob.Bytecode);
        pixelShaderBlob.Dispose();

        // Create the blending setup
        var blendStateDesc = new BlendStateDescription
        {
            AlphaToCoverageEnable = false
        };
        blendStateDesc.RenderTarget[0].IsBlendEnabled = true;
        blendStateDesc.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
        blendStateDesc.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
        blendStateDesc.RenderTarget[0].BlendOperation = BlendOperation.Add;
        blendStateDesc.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
        blendStateDesc.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
        blendStateDesc.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
        blendStateDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
        bd.BlendState = new BlendState(bd.Device, blendStateDesc);

        // Create the rasterizer state
        bd.RasterizerState = new RasterizerState(bd.Device, new RasterizerStateDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            IsScissorEnabled = true,
            IsDepthClipEnabled = true
        });

        // Create depth-stencil State
        bd.DepthStencilState = new DepthStencilState(bd.Device, new DepthStencilStateDescription
        {
            IsDepthEnabled = false,
            DepthWriteMask = DepthWriteMask.All,
            DepthComparison = Comparison.Always,
            IsStencilEnabled = false,
            FrontFace =
            {
                FailOperation = StencilOperation.Keep,
                DepthFailOperation = StencilOperation.Keep,
                PassOperation = StencilOperation.Keep,
                Comparison = Comparison.Always
            },
            BackFace =
            {
                FailOperation = StencilOperation.Keep,
                DepthFailOperation = StencilOperation.Keep,
                PassOperation = StencilOperation.Keep,
                Comparison = Comparison.Always
            }
        });

        // Create texture sampler
        // (Bilinear sampling is required by default. Set 'io.Fonts->Flags |= ImFontAtlasFlags_NoBakedLines' or 'style.AntiAliasedLinesUseTex = false' to allow point/nearest sampling)
        bd.TexSamplerLinear = new SamplerState(bd.Device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MipLodBias = 0.0f,
            ComparisonFunction = Comparison.Always,
            MinimumLod = 0.0f,
            MaximumLod = 0.0f
        });

        return true;
    }

    public unsafe static void InvalidateDeviceObjects()
    {
        Data bd = GetBackendData();
        if (bd.Device == null)
            return;

        // Destroy all textures
        var textures = ImGui.GetPlatformIO().Textures;
        for (int n = 0; n < textures.Size; n++)
        {
            ImTextureData* tex = textures.Data[n].Handle;
            if (tex->RefCount == 1)
                DestroyTexture(tex);
        }

        bd.TexSamplerLinear?.Dispose();
        bd.TexSamplerLinear = null;
        bd.VertexBuffer?.Dispose();
        bd.VertexBuffer = null;
        bd.IndexBuffer?.Dispose();
        bd.IndexBuffer = null;
        bd.BlendState?.Dispose();
        bd.BlendState = null;
        bd.DepthStencilState?.Dispose();
        bd.DepthStencilState = null;
        bd.RasterizerState?.Dispose();
        bd.RasterizerState = null;
        bd.PixelShader?.Dispose();
        bd.PixelShader = null;
        bd.VertexConstantBuffer?.Dispose();
        bd.VertexConstantBuffer = null;
        bd.InputLayout?.Dispose();
        bd.InputLayout = null;
        bd.VertexShader?.Dispose();
        bd.VertexShader = null;
    }

    internal unsafe static void Init(IntPtr device, IntPtr device_context)
    {
        var io = ImGui.GetIO();
        Debug.Assert(io.BackendRendererUserData == null, "Already initialized a renderer backend!");

        // Setup backend capabilities flags
        Data bd = InitBackendData();
        io.BackendRendererName = (byte*)Marshal.StringToHGlobalAnsi($"imgui_impl_dx11_{DearImGuiInjectionCore.HexaVersion}");
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;  // We can honor the ImDrawCmd::VtxOffset field, allowing for large meshes.
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;   // We can honor ImGuiPlatformIO::Textures[] requests during render.

        var platform_io = ImGui.GetPlatformIO();
        platform_io.RendererTextureMaxWidth = platform_io.RendererTextureMaxHeight = Texture2D.MaximumTexture2DSize;

        bd.Device = new(device);
        bd.DeviceContext = new(device_context);
    }

    public unsafe static void Shutdown()
    {
        Data bd = GetBackendData();
        Debug.Assert(bd != null, "No renderer backend to shutdown, or already shutdown?");
        var io = ImGui.GetIO();
        var platform_io = ImGui.GetPlatformIO();

        InvalidateDeviceObjects();
        // we don't own these, so no Dispose()
        bd.Device = null;
        bd.DeviceContext = null;

        Marshal.FreeHGlobal((IntPtr)io.BackendRendererName);
        io.BackendFlags &= ~(ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures);
        platform_io.ClearRendererHandlers();
        FreeBackendData();
    }

    public static void NewFrame()
    {
        Data bd = GetBackendData();
        Debug.Assert(bd != null, "Context or backend not initialized! Did you call ImGui_ImplDX11_Init()?");
        if (bd.VertexShader == null)
            if (!CreateDeviceObjects())
                Debug.Assert(false, "ImGui_ImplDX11_CreateDeviceObjects() failed!");
    }
}