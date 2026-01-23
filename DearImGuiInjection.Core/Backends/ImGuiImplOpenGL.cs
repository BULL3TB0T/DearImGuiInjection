using DearImGuiInjection.Renderers;
using Hexa.NET.ImGui;
using Silk.NET.OpenGL;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Backends;

using ImDrawIdx = ushort;

internal static class ImGuiImplOpenGL
{
    private static bool _isInitialized;

    private static bool _glProfileIsES2;
    private static bool _glProfileIsES3;
    private static bool _glProfileIsCompat;

    // Extracted at runtime using GL_MAJOR_VERSION, GL_MINOR_VERSION queries (e.g. 320 for GL 3.2)
    private static int _glVersion;
    private static string _glslVersion;

    // Vertex arrays are not supported on ES2/WebGL1
    private static bool _useVertexArray;

    // Desktop GL 2.0+ has extension and glPolygonMode() which GL ES and WebGL don't have..
    // A desktop ES context can technically compile fine with our loader, so we also perform a runtime checks
    private static bool _hasExtensionsEnum;
    private static bool _mayHavePolygonMode;

    // Desktop GL 2.1+ and GL ES 3.0+ have glBindBuffer() with GL_PIXEL_UNPACK_BUFFER target.
    private static bool _mayHaveBindBufferPixelUnpack;

    // Desktop GL 3.1+ has GL_PRIMITIVE_RESTART state
    private static bool _mayHavePrimitiveRestart;

    // Desktop GL 3.2+ has glDrawElementsBaseVertex() which GL ES and WebGL don't have.
    private static bool _mayHaveVtxOffset;

    // Desktop GL 3.3+ and GL ES 3.0+ have glBindSampler()
    private static bool _mayHaveBindSampler;

    private static bool _unpackRowLength;

    private static int _maxTextureSize;
    private static bool _useBufferSubData;
    private static bool _hasClipOrigin;

    private const string _vertexShaderGlsl120 = @"
        uniform mat4 ProjMtx;
        attribute vec2 Position;
        attribute vec2 UV;
        attribute vec4 Color;
        varying vec2 Frag_UV;
        varying vec4 Frag_Color;
        void main()
        {
            Frag_UV = UV;
            Frag_Color = Color;
            gl_Position = ProjMtx * vec4(Position.xy,0,1);
        }";

    private const string _vertexShaderGlsl130 = @"
        uniform mat4 ProjMtx;
        in vec2 Position;
        in vec2 UV;
        in vec4 Color;
        out vec2 Frag_UV;
        out vec4 Frag_Color;
        void main()
        {
            Frag_UV = UV;
            Frag_Color = Color;
            gl_Position = ProjMtx * vec4(Position.xy,0,1);
        }";

    private const string _vertexShaderGlsl300ES = @"
        precision highp float;
        layout (location = 0) in vec2 Position;
        layout (location = 1) in vec2 UV;
        layout (location = 2) in vec4 Color;
        uniform mat4 ProjMtx;
        out vec2 Frag_UV;
        out vec4 Frag_Color;
        void main()
        {
            Frag_UV = UV;
            Frag_Color = Color;
            gl_Position = ProjMtx * vec4(Position.xy,0,1);
        }";

    private const string _vertexShaderGlsl410Core = @"
        layout (location = 0) in vec2 Position;
        layout (location = 1) in vec2 UV;
        layout (location = 2) in vec4 Color;
        uniform mat4 ProjMtx;
        out vec2 Frag_UV;
        out vec4 Frag_Color;
        void main()
        {
            Frag_UV = UV;
            Frag_Color = Color;
            gl_Position = ProjMtx * vec4(Position.xy,0,1);
        }";

    private const string _fragmentShaderGlsl120 = @"
        #ifdef GL_ES
            precision mediump float;
        #endif
        uniform sampler2D Texture;
        varying vec2 Frag_UV;
        varying vec4 Frag_Color;
        void main()
        {
            gl_FragColor = Frag_Color * texture2D(Texture, Frag_UV.st);
        }";

    private const string _fragmentShaderGlsl130 = @"
        uniform sampler2D Texture;
        in vec2 Frag_UV;
        in vec4 Frag_Color;
        out vec4 Out_Color;
        void main()
        {
            Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
        }";

    private const string _fragmentShaderGlsl300ES = @"
        precision mediump float;
        uniform sampler2D Texture;
        in vec2 Frag_UV;
        in vec4 Frag_Color;
        layout (location = 0) out vec4 Out_Color;
        void main()
        {
            Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
        }";

    private const string _fragmentShaderGlsl410Core = @"
        in vec2 Frag_UV;
        in vec4 Frag_Color;
        uniform sampler2D Texture;
        layout (location = 0) out vec4 Out_Color;
        void main()
        {
            Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
        }";

    // OpenGL Data
    private unsafe struct Data
    {
        public uint ShaderHandle;
        public int AttribLocationTex;     // Uniforms location
        public int AttribLocationProjMtx;
        public uint AttribLocationVtxPos; // Vertex attributes location
        public uint AttribLocationVtxUV;
        public uint AttribLocationVtxColor;
        public uint VboHandle, ElementsHandle;
        public nuint VertexBufferSize;
        public nuint IndexBufferSize;
        public ImVector<byte> TempBuffer;
    }

    // OpenGL vertex attribute state (for ES 1.0 and ES 2.0 only)
    private unsafe struct VtxAttribState
    {
        public int Enabled, Size, Type, Normalized, Stride;
        public void* Ptr;

        public unsafe void GetState(int index)
        {
            SharedAPI.GL.GetVertexAttrib((uint)index, GLEnum.VertexAttribArrayEnabled, out Enabled);
            SharedAPI.GL.GetVertexAttrib((uint)index, GLEnum.VertexAttribArraySize, out Size);
            SharedAPI.GL.GetVertexAttrib((uint)index, GLEnum.VertexAttribArrayType, out Type);
            SharedAPI.GL.GetVertexAttrib((uint)index, GLEnum.VertexAttribArrayNormalized, out Normalized);
            SharedAPI.GL.GetVertexAttrib((uint)index, GLEnum.VertexAttribArrayStride, out Stride);
            SharedAPI.GL.GetVertexAttribPointer((uint)index, GLEnum.VertexAttribArrayPointer, out Ptr);
        }

        public unsafe void SetState(int index)
        {
            SharedAPI.GL.VertexAttribPointer((uint)index, Size, (GLEnum)Type, Normalized != 0, (uint)Stride, Ptr);
            if (Enabled != 0)
                SharedAPI.GL.EnableVertexAttribArray((uint)index);
            else
                SharedAPI.GL.DisableVertexAttribArray((uint)index);
        }
    }

    // Backend data stored in io.BackendRendererUserData to allow support for multiple Dear ImGui contexts
    // It is STRONGLY preferred that you use docking branch with multi-viewports (== single Dear ImGui context + multiple windows) instead of multiple Dear ImGui contexts.
    private unsafe static Data* GetBackendData() => (Data*)ImGui.GetIO().BackendRendererUserData;

    // Functions
    public unsafe static bool Init(string glsl_version = null)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        if (io.BackendRendererUserData != null)
            throw new InvalidOperationException("Already initialized a renderer backend!");

        // Setup backend capabilities flags
        Data* bd = (Data*)ImGui.MemAlloc((nuint)sizeof(Data));
        *bd = default;
        io.BackendRendererUserData = bd;
        io.BackendRendererName = (byte*)Marshal.StringToHGlobalAnsi($"imgui_impl_opengl3_{DearImGuiInjectionCore.BackendVersion}");

        if (!_isInitialized)
        {
            // Query for GL version (e.g. 320 for GL 3.2)
            string gl_version_str = SharedAPI.GL.GetStringS(GLEnum.Version);
            _glProfileIsES2 = gl_version_str.StartsWith("OpenGL ES 2", StringComparison.OrdinalIgnoreCase);
            _glProfileIsES3 = gl_version_str.StartsWith("OpenGL ES 3", StringComparison.OrdinalIgnoreCase);
            if (_glProfileIsES2)
            {
                // GLES 2
                _glVersion = 200;
            }
            else
            {
                // Desktop or GLES 3
                SharedAPI.GL.GetInteger(GLEnum.MajorVersion, out int major);
                SharedAPI.GL.GetInteger(GLEnum.MinorVersion, out int minor);
                if (major == 0 && minor == 0)
                {
                    // Query GL_VERSION in desktop GL 2.x, the string will start with "<major>.<minor>"
                    var parts = gl_version_str.Split('.');
                    if (parts.Length >= 2)
                    {
                        string left = parts[0];
                        int last_space = left.LastIndexOf(' ');
                        if (last_space >= 0)
                            left = left.Substring(last_space + 1);
                        if (int.TryParse(left, out major))
                        {
                            string right = parts[1];
                            int end = 0;
                            while (end < right.Length && right[end] >= '0' && right[end] <= '9')
                                end++;
                            if (end > 0)
                                int.TryParse(right.Substring(0, end), out minor);
                        }
                    }
                }
                _glVersion = major * 100 + minor * 10;
                SharedAPI.GL.GetInteger(GLEnum.MaxTextureSize, out _maxTextureSize);
            }

            _useVertexArray = !_glProfileIsES2;
            _hasExtensionsEnum = !_glProfileIsES2 && !_glProfileIsES3;
            _mayHavePolygonMode = !_glProfileIsES2 && !_glProfileIsES3;
            _mayHaveBindBufferPixelUnpack = !_glProfileIsES2 && _glVersion >= 210;
            _mayHavePrimitiveRestart = !_glProfileIsES2 && !_glProfileIsES3 && _glVersion >= 310;
            _mayHaveVtxOffset = !_glProfileIsES2 && !_glProfileIsES3 && _glVersion >= 320;
            _mayHaveBindSampler = !_glProfileIsES2 && (_glProfileIsES3 || _glVersion >= 330);
            _unpackRowLength = !_glProfileIsES2 && !_glProfileIsES3;

            int profileMask = 0;
            if (!_glProfileIsES3 && _glVersion >= 320)
                SharedAPI.GL.GetInteger(GLEnum.ContextProfileMask, out profileMask);
            _glProfileIsCompat = (profileMask & (int)GLEnum.ContextCompatibilityProfileBit) != 0;

            _useBufferSubData = false;
            /*
            // Query vendor to enable glBufferSubData kludge
            string vendor = SharedAPI.GL.GetStringS(GLEnum.Vendor);
            if (vendor != null)
            {
                if (vendor.StartsWith("Intel", StringComparison.OrdinalIgnoreCase))
                    _useBufferSubData = true;
            }
            */

            // Store GLSL version string so we can refer to it later in case we recreate shaders.
            // Note: GLSL version is NOT the same as GL version. Leave this to nullptr if unsure.
            if (glsl_version == null)
            {
                if (_glProfileIsES2)
                    glsl_version = "#version 100";
                else if (_glProfileIsES3)
                    glsl_version = "#version 300 es";
                else
                    glsl_version = "#version 130";
            }
            _glslVersion = glsl_version;

            // Make an arbitrary GL call (we don't actually need the result)
            // IF YOU GET A CRASH HERE: it probably means the OpenGL function loader didn't do its job. Let us know!
            SharedAPI.GL.GetInteger(GLEnum.TextureBinding2D, out _);

            // Detect extensions we support
            _hasClipOrigin = _glVersion >= 450;
            if (!_hasClipOrigin && _hasExtensionsEnum)
            {
                SharedAPI.GL.GetInteger(GLEnum.NumExtensions, out int num_extensions);
                for (uint i = 0; i < num_extensions; i++)
                {
                    string extension = SharedAPI.GL.GetStringS(GLEnum.Extensions, i);
                    if (extension != null && extension == "GL_ARB_clip_control")
                        _hasClipOrigin = true;
                }
            }

            /*
            Log.Info(
                $"\n  GlVersion = {_glVersion}, \"{gl_version_str}\"\n" +
                $"  GlslVersion = \"{_glslVersion}\"\n" +
                $"  GlProfileIsCompat = {_glProfileIsCompat}\n" +
                $"  GlProfileMask = 0x{profileMask:X}\n" +
                $"  GlProfileIsES2/IsEs3 = {_glProfileIsES2}/{_glProfileIsES3}\n" +
                $"  MaxTextureSize = {_maxTextureSize}\n" +
                $"  UseBufferSubData = {_useBufferSubData}\n" +
                $"  HasClipOrigin = {_hasClipOrigin}\n" +
                $"  GL_VENDOR = '{SharedAPI.GL.GetStringS(GLEnum.Vendor)}'\n" +
                $"  GL_RENDERER = '{SharedAPI.GL.GetStringS(GLEnum.Renderer)}'\n\n" +
                $"  OpenGL backend flags:\n" +
                $"  _useVertexArray={_useVertexArray}\n" +
                $"  _hasExtensionsEnum={_hasExtensionsEnum}\n" +
                $"  _mayHavePolygonMode={_mayHavePolygonMode}\n" +
                $"  _mayHaveBindBufferPixelUnpack={_mayHaveBindBufferPixelUnpack}\n" +
                $"  _mayHavePrimitiveRestart={_mayHavePrimitiveRestart}\n" +
                $"  _mayHaveVtxOffset={_mayHaveVtxOffset}\n" +
                $"  _mayHaveBindSampler={_mayHaveBindSampler}" +
                $"  _unpackRowLength={_unpackRowLength}"
            );
            */

            _isInitialized = true;
        }

        if (_mayHaveVtxOffset)
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;  // We can honor the ImDrawCmd::VtxOffset field, allowing for large meshes.
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;       // We can honor ImGuiPlatformIO::Textures[] requests during render.

        ImGuiPlatformIOPtr platform_io = ImGui.GetPlatformIO();
        platform_io.RendererTextureMaxWidth = platform_io.RendererTextureMaxHeight = _maxTextureSize;

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
            throw new InvalidOperationException("Context or backend not initialized! Did you call ImGui_ImplOpenGL3_Init()?");

        if (bd->ShaderHandle == 0)
            if (!CreateDeviceObjects())
                throw new InvalidOperationException("ImGui_ImplOpenGL3_CreateDeviceObjects() failed!");
    }

    private unsafe static void SetupRenderState(ImDrawData* draw_data, int fb_width, int fb_height, uint vertex_array_object)
    {
        Data* bd = GetBackendData();

        // Setup render state: alpha-blending enabled, no face culling, no depth testing, scissor enabled, polygon fill
        SharedAPI.GL.Enable(GLEnum.Blend);
        SharedAPI.GL.BlendEquation(GLEnum.FuncAdd);
        SharedAPI.GL.BlendFuncSeparate(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha, GLEnum.One, GLEnum.OneMinusSrcAlpha);
        SharedAPI.GL.Disable(GLEnum.CullFace);
        SharedAPI.GL.Disable(GLEnum.DepthTest);
        SharedAPI.GL.Disable(GLEnum.StencilTest);
        SharedAPI.GL.Enable(GLEnum.ScissorTest);
        if (_mayHavePrimitiveRestart)
            SharedAPI.GL.Disable(GLEnum.PrimitiveRestart);
        if (_mayHavePolygonMode)
            SharedAPI.GL.PolygonMode(GLEnum.FrontAndBack, GLEnum.Fill);

        // Support for GL 4.5 rarely used glClipControl(GL_UPPER_LEFT)
        bool clip_origin_lower_left = true;
        if (_hasClipOrigin)
        {
            SharedAPI.GL.GetInteger(GLEnum.ClipOrigin, out int current_clip_origin);
            if (current_clip_origin == (int)GLEnum.UpperLeft)
                clip_origin_lower_left = false;
        }

        // Setup viewport, orthographic projection matrix
        // Our visible imgui space lies from draw_data->DisplayPos (top left) to draw_data->DisplayPos+data_data->DisplaySize (bottom right). DisplayPos is (0,0) for single viewport apps.
        SharedAPI.GL.Viewport(0, 0, (uint)fb_width, (uint)fb_height);
        float L = draw_data->DisplayPos.X;
        float R = draw_data->DisplayPos.X + draw_data->DisplaySize.X;
        float T = draw_data->DisplayPos.Y;
        float B = draw_data->DisplayPos.Y + draw_data->DisplaySize.Y;
        if (!clip_origin_lower_left)
        {
            float tmp = T;
            T = B;
            B = tmp;
        } // Swap top and bottom if origin is upper left
        float* ortho_projection = stackalloc float[ImGuiImpl.VERTEX_CONSTANT_BUFFER.ElementCount]
        {
            2.0f/(R-L),   0.0f,           0.0f,       0.0f,
            0.0f,         2.0f/(T-B),     0.0f,       0.0f,
            0.0f,         0.0f,           0.5f,       0.0f,
            (R+L)/(L-R),  (T+B)/(B-T),    0.5f,       1.0f,
        };
        SharedAPI.GL.UseProgram(bd->ShaderHandle);
        SharedAPI.GL.Uniform1(bd->AttribLocationTex, 0);
        SharedAPI.GL.UniformMatrix4(bd->AttribLocationProjMtx, 1, false, ortho_projection);

        if (_mayHaveBindSampler)
            SharedAPI.GL.BindSampler(0, 0); // We use combined texture/sampler state. Applications using GL 3.3 and GL ES 3.0 may set that otherwise.

        if (_useVertexArray)
            SharedAPI.GL.BindVertexArray(vertex_array_object);

        // Bind vertex/index buffers and setup attributes for ImDrawVert
        SharedAPI.GL.BindBuffer(GLEnum.ArrayBuffer, bd->VboHandle);
        SharedAPI.GL.BindBuffer(GLEnum.ElementArrayBuffer, bd->ElementsHandle);
        SharedAPI.GL.EnableVertexAttribArray(bd->AttribLocationVtxPos);
        SharedAPI.GL.EnableVertexAttribArray(bd->AttribLocationVtxUV);
        SharedAPI.GL.EnableVertexAttribArray(bd->AttribLocationVtxColor);
        SharedAPI.GL.VertexAttribPointer(bd->AttribLocationVtxPos, 2, GLEnum.Float, false, (uint)sizeof(ImDrawVert), (void*)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.Pos)));
        SharedAPI.GL.VertexAttribPointer(bd->AttribLocationVtxUV, 2, GLEnum.Float, false, (uint)sizeof(ImDrawVert), (void*)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.Uv)));
        SharedAPI.GL.VertexAttribPointer(bd->AttribLocationVtxColor, 4, GLEnum.UnsignedByte, true, (uint)sizeof(ImDrawVert), (void*)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.Col)));
    }

    // OpenGL3 Render function.
    // Note that this implementation is little overcomplicated because we are saving/setting up/restoring every OpenGL state explicitly.
    // This is in order to be able to run within an OpenGL engine that doesn't do so.
    public unsafe static void RenderDrawData(ImDrawData* draw_data)
    {
        // Avoid rendering when minimized, scale coordinates for retina displays (screen coordinates != framebuffer coordinates)
        int fb_width = (int)(draw_data->DisplaySize.X * draw_data->FramebufferScale.X);
        int fb_height = (int)(draw_data->DisplaySize.Y * draw_data->FramebufferScale.Y);
        if (fb_width <= 0 || fb_height <= 0)
            return;

        Data* bd = GetBackendData();

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

        // Backup GL state
        int last_active_texture = 0;
        SharedAPI.GL.GetInteger(GLEnum.ActiveTexture, out last_active_texture);
        SharedAPI.GL.ActiveTexture(GLEnum.Texture0);
        int last_program = 0;
        SharedAPI.GL.GetInteger(GLEnum.CurrentProgram, out last_program);
        int last_texture = 0;
        SharedAPI.GL.GetInteger(GLEnum.TextureBinding2D, out last_texture);
        int last_sampler;
        if (_mayHaveBindSampler)
        {
            last_sampler = 0;
            SharedAPI.GL.GetInteger(GLEnum.SamplerBinding, out last_sampler);
        }
        else
        {
            last_sampler = 0;
        }
        int last_array_buffer = 0;
        SharedAPI.GL.GetInteger(GLEnum.ArrayBufferBinding, out last_array_buffer);
        int last_element_array_buffer = 0;
        VtxAttribState last_vtx_attrib_state_pos = default;
        VtxAttribState last_vtx_attrib_state_uv = default;
        VtxAttribState last_vtx_attrib_state_color = default;
        if (!_useVertexArray)
        {
            // This is part of VAO on OpenGL 3.0+ and OpenGL ES 3.0+.
            SharedAPI.GL.GetInteger(GLEnum.ElementArrayBufferBinding, out last_element_array_buffer);
            last_vtx_attrib_state_pos.GetState((int)bd->AttribLocationVtxPos);
            last_vtx_attrib_state_uv.GetState((int)bd->AttribLocationVtxUV);
            last_vtx_attrib_state_color.GetState((int)bd->AttribLocationVtxColor);
        }
        int last_vertex_array_object = 0;
        if (_useVertexArray)
            SharedAPI.GL.GetInteger(GLEnum.VertexArrayBinding, out last_vertex_array_object);
        int* last_polygon_mode = stackalloc int[2];
        if (_mayHavePolygonMode)
            SharedAPI.GL.GetInteger(GLEnum.PolygonMode, last_polygon_mode);
        int* last_viewport = stackalloc int[4];
        SharedAPI.GL.GetInteger(GLEnum.Viewport, last_viewport);
        int* last_scissor_box = stackalloc int[4];
        SharedAPI.GL.GetInteger(GLEnum.ScissorBox, last_scissor_box);
        SharedAPI.GL.GetInteger(GLEnum.BlendSrcRgb, out int last_blend_src_rgb);
        SharedAPI.GL.GetInteger(GLEnum.BlendDstRgb, out int last_blend_dst_rgb);
        SharedAPI.GL.GetInteger(GLEnum.BlendSrcAlpha, out int last_blend_src_alpha);
        SharedAPI.GL.GetInteger(GLEnum.BlendDstAlpha, out int last_blend_dst_alpha);
        SharedAPI.GL.GetInteger(GLEnum.BlendEquationRgb, out int last_blend_equation_rgb);
        SharedAPI.GL.GetInteger(GLEnum.BlendEquationAlpha, out int last_blend_equation_alpha);
        bool last_enable_blend = SharedAPI.GL.IsEnabled(GLEnum.Blend);
        bool last_enable_cull_face = SharedAPI.GL.IsEnabled(GLEnum.CullFace);
        bool last_enable_depth_test = SharedAPI.GL.IsEnabled(GLEnum.DepthTest);
        bool last_enable_stencil_test = SharedAPI.GL.IsEnabled(GLEnum.StencilTest);
        bool last_enable_scissor_test = SharedAPI.GL.IsEnabled(GLEnum.ScissorTest);
        bool last_enable_primitive_restart = !_glProfileIsES3 && _glVersion >= 310 ? SharedAPI.GL.IsEnabled(GLEnum.PrimitiveRestart) : false;
        bool last_enable_framebuffer_srgb = !_glProfileIsES2 && SharedAPI.GL.IsEnabled(GLEnum.FramebufferSrgb);
        if (last_enable_framebuffer_srgb)
            SharedAPI.GL.Disable(GLEnum.FramebufferSrgb);

        // Setup desired GL state
        // Recreate the VAO every time (this is to easily allow multiple GL contexts to be rendered to. VAO are not shared among GL contexts)
        // The renderer would actually work without any VAO bound, but then our VertexAttrib calls would overwrite the default one currently bound.
        uint vertex_array_object = 0;
        if (_useVertexArray)
            SharedAPI.GL.GenVertexArrays(1, &vertex_array_object);
        SetupRenderState(draw_data, fb_width, fb_height, vertex_array_object);

        // Will project scissor/clipping rectangles into framebuffer space
        Vector2 clip_off = draw_data->DisplayPos;         // (0,0) unless using multi-viewports
        Vector2 clip_scale = draw_data->FramebufferScale; // (1,1) unless using retina display which are often (2,2)

        // Render command lists
        for (int n = 0; n < draw_data->CmdListsCount; n++)
        {
            ImDrawList* draw_list = draw_data->CmdLists.Data[n];

            // Upload vertex/index buffers
            // - OpenGL drivers are in a very sorry state nowadays....
            //   During 2021 we attempted to switch from glBufferData() to orphaning+glBufferSubData() following reports
            //   of leaks on Intel GPU when using multi-viewports on Windows.
            // - After this we kept hearing of various display corruptions issues. We started disabling on non-Intel GPU, but issues still got reported on Intel.
            // - We are now back to using exclusively glBufferData(). So bd->UseBufferSubData IS ALWAYS FALSE in this code.
            //   We are keeping the old code path for a while in case people finding new issues may want to test the bd->UseBufferSubData path.
            // - See https://github.com/ocornut/imgui/issues/4468 and please report any corruption issues.
            nuint vtx_buffer_size = (nuint)(draw_list->VtxBuffer.Size * sizeof(ImDrawVert));
            nuint idx_buffer_size = (nuint)(draw_list->IdxBuffer.Size * sizeof(ImDrawIdx));
            if (_useBufferSubData)
            {
                if (bd->VertexBufferSize < vtx_buffer_size)
                {
                    bd->VertexBufferSize = vtx_buffer_size;
                    SharedAPI.GL.BufferData(GLEnum.ArrayBuffer, bd->VertexBufferSize, null, GLEnum.StreamDraw);
                }
                if (bd->IndexBufferSize < idx_buffer_size)
                {
                    bd->IndexBufferSize = idx_buffer_size;
                    SharedAPI.GL.BufferData(GLEnum.ElementArrayBuffer, bd->IndexBufferSize, null, GLEnum.StreamDraw);
                }
                SharedAPI.GL.BufferSubData(GLEnum.ArrayBuffer, 0, vtx_buffer_size, draw_list->VtxBuffer.Data);
                SharedAPI.GL.BufferSubData(GLEnum.ElementArrayBuffer, 0, idx_buffer_size, draw_list->IdxBuffer.Data);
            }
            else
            {
                SharedAPI.GL.BufferData(GLEnum.ArrayBuffer, vtx_buffer_size, draw_list->VtxBuffer.Data, GLEnum.StreamDraw);
                SharedAPI.GL.BufferData(GLEnum.ElementArrayBuffer, idx_buffer_size, draw_list->IdxBuffer.Data, GLEnum.StreamDraw);
            }

            for (int cmd_i = 0; cmd_i < draw_list->CmdBuffer.Size; cmd_i++)
            {
                ImDrawCmd* pcmd = &draw_list->CmdBuffer.Data[cmd_i];
                if (pcmd->UserCallback != null)
                {
                    // User callback, registered via ImDrawList::AddCallback()
                    // (ImDrawCallback_ResetRenderState is a special callback value used by the user to request the renderer to reset render state.)
                    if (pcmd->UserCallback == (void*)ImGui.ImDrawCallbackResetRenderState)
                        SetupRenderState(draw_data, fb_width, fb_height, vertex_array_object);
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

                    // Apply scissor/clipping rectangle (Y is inverted in OpenGL)
                    SharedAPI.GL.Scissor((int)clip_min.X, fb_height - (int)clip_max.Y, (uint)(clip_max.X - clip_min.X), (uint)(clip_max.Y - clip_min.Y));

                    // Bind texture, Draw
                    SharedAPI.GL.BindTexture(GLEnum.Texture2D, (uint)pcmd->GetTexID());
                    GLEnum indexType = sizeof(ImDrawIdx) == 2 ? GLEnum.UnsignedShort : GLEnum.UnsignedInt;
                    void* index_offset = (void*)(nint)(pcmd->IdxOffset * sizeof(ImDrawIdx));
                    if (_mayHaveVtxOffset)
                        SharedAPI.GL.DrawElementsBaseVertex(GLEnum.Triangles, (uint)pcmd->ElemCount, indexType, index_offset, (int)pcmd->VtxOffset);
                    else
                        SharedAPI.GL.DrawElements(GLEnum.Triangles, (uint)pcmd->ElemCount, indexType, index_offset);
                }
            }
        }

        // Destroy the temporary VAO
        if (_useVertexArray)
            SharedAPI.GL.DeleteVertexArrays(1, &vertex_array_object);

        // Restore modified GL state
        // This "glIsProgram()" check is required because if the program is "pending deletion" at the time of binding backup, it will have been deleted by now and will cause an OpenGL error. See #6220.
        if (last_program == 0 || SharedAPI.GL.IsProgram((uint)last_program))
            SharedAPI.GL.UseProgram((uint)last_program);
        SharedAPI.GL.BindTexture(GLEnum.Texture2D, (uint)last_texture);
        if (_mayHaveBindSampler)
            SharedAPI.GL.BindSampler(0, (uint)last_sampler);
        SharedAPI.GL.ActiveTexture((GLEnum)last_active_texture);
        if (_useVertexArray)
            SharedAPI.GL.BindVertexArray((uint)last_vertex_array_object);
        SharedAPI.GL.BindBuffer(GLEnum.ArrayBuffer, (uint)last_array_buffer);
        if (!_useVertexArray)
        {
            SharedAPI.GL.BindBuffer(GLEnum.ElementArrayBuffer, (uint)last_element_array_buffer);
            last_vtx_attrib_state_pos.SetState((int)bd->AttribLocationVtxPos);
            last_vtx_attrib_state_uv.SetState((int)bd->AttribLocationVtxUV);
            last_vtx_attrib_state_color.SetState((int)bd->AttribLocationVtxColor);
        }
        SharedAPI.GL.BlendEquationSeparate((GLEnum)last_blend_equation_rgb, (GLEnum)last_blend_equation_alpha);
        SharedAPI.GL.BlendFuncSeparate((GLEnum)last_blend_src_rgb, (GLEnum)last_blend_dst_rgb, (GLEnum)last_blend_src_alpha, (GLEnum)last_blend_dst_alpha);
        if (last_enable_blend) SharedAPI.GL.Enable(GLEnum.Blend); else SharedAPI.GL.Disable(GLEnum.Blend);
        if (last_enable_cull_face) SharedAPI.GL.Enable(GLEnum.CullFace); else SharedAPI.GL.Disable(GLEnum.CullFace);
        if (last_enable_depth_test) SharedAPI.GL.Enable(GLEnum.DepthTest); else SharedAPI.GL.Disable(GLEnum.DepthTest);
        if (last_enable_stencil_test) SharedAPI.GL.Enable(GLEnum.StencilTest); else SharedAPI.GL.Disable(GLEnum.StencilTest);
        if (last_enable_scissor_test) SharedAPI.GL.Enable(GLEnum.ScissorTest); else SharedAPI.GL.Disable(GLEnum.ScissorTest);
        if (_mayHavePrimitiveRestart)
        {
            if (last_enable_primitive_restart)
                SharedAPI.GL.Enable(GLEnum.PrimitiveRestart);
            else
                SharedAPI.GL.Disable(GLEnum.PrimitiveRestart);
        }

        // Desktop OpenGL 3.0 and OpenGL 3.1 had separate polygon draw modes for front-facing and back-facing faces of polygons
        if (_mayHavePolygonMode)
        {
            if (_glVersion <= 310 || _glProfileIsCompat)
            {
                SharedAPI.GL.PolygonMode(GLEnum.Front, (GLEnum)last_polygon_mode[0]);
                SharedAPI.GL.PolygonMode(GLEnum.Back, (GLEnum)last_polygon_mode[1]);
            }
            else
                SharedAPI.GL.PolygonMode(GLEnum.FrontAndBack, (GLEnum)last_polygon_mode[0]);
        }

        if (!_glProfileIsES2)
        {
            if (last_enable_framebuffer_srgb)
                SharedAPI.GL.Enable(GLEnum.FramebufferSrgb);
            else
                SharedAPI.GL.Disable(GLEnum.FramebufferSrgb);
        }

        SharedAPI.GL.Viewport(last_viewport[0], last_viewport[1], (uint)last_viewport[2], (uint)last_viewport[3]);
        SharedAPI.GL.Scissor(last_scissor_box[0], last_scissor_box[1], (uint)last_scissor_box[2], (uint)last_scissor_box[3]);
    }

    private unsafe static void DestroyTexture(ImTextureData* tex, bool disposing)
    {
        uint gl_tex_id = (uint)tex->TexID;
        if (!disposing)
            SharedAPI.GL.DeleteTextures(1, &gl_tex_id);

        // Clear identifiers and mark as destroyed (in order to allow e.g. calling InvalidateDeviceObjects while running)
        tex->SetTexID(ImTextureID.Null);
        tex->SetStatus(ImTextureStatus.Destroyed);
    }

    private unsafe static void UpdateTexture(ImTextureData* tex)
    {
        // FIXME: Consider backing up and restoring
        if (tex->Status == ImTextureStatus.WantCreate || tex->Status == ImTextureStatus.WantUpdates)
        {
            if (_unpackRowLength) // Not on WebGL/ES
                SharedAPI.GL.PixelStore(GLEnum.UnpackRowLength, 0);
            SharedAPI.GL.PixelStore(GLEnum.UnpackAlignment, 1);
        }

        if (tex->Status == ImTextureStatus.WantCreate)
        {
            // Create and upload new texture to graphics system
            //Log.Debug(string.Format("UpdateTexture #%03d: WantCreate %dx%d\n", tex->UniqueID, tex->Width, tex->Height));
            if (!tex->TexID.IsNull || tex->BackendUserData != null)
                throw new InvalidOperationException("Expected TexID to be null and BackendUserData to be null.");
            if (tex->Format != ImTextureFormat.Rgba32)
                throw new InvalidOperationException("Expected texture format RGBA32.");
            void* pixels = tex->Pixels;
            uint gl_texture_id = 0;

            // Upload texture to graphics system
            // (Bilinear sampling is required by default. Set 'io.Fonts->Flags |= ImFontAtlasFlags_NoBakedLines' or 'style.AntiAliasedLinesUseTex = false' to allow point/nearest sampling)
            SharedAPI.GL.GetInteger(GLEnum.TextureBinding2D, out int last_texture);
            SharedAPI.GL.GenTextures(1, &gl_texture_id);
            SharedAPI.GL.BindTexture(GLEnum.Texture2D, gl_texture_id);
            SharedAPI.GL.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
            SharedAPI.GL.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
            SharedAPI.GL.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
            SharedAPI.GL.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);
            SharedAPI.GL.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba, (uint)tex->Width, (uint)tex->Height, 0, GLEnum.Rgba, GLEnum.UnsignedByte, pixels);

            // Store identifiers
            tex->SetTexID(gl_texture_id);
            tex->SetStatus(ImTextureStatus.Ok);

            // Restore state
            SharedAPI.GL.BindTexture(GLEnum.Texture2D, (uint)last_texture);
        }
        else if (tex->Status == ImTextureStatus.WantUpdates)
        {
            // Update selected blocks. We only ever write to textures regions which have never been used before!
            // This backend choose to use tex->Updates[] but you can use tex->UpdateRect to upload a single region.
            int last_texture = 0;
            SharedAPI.GL.GetInteger(GLEnum.TextureBinding2D, &last_texture);

            uint gl_tex_id = (uint)tex->TexID;
            SharedAPI.GL.BindTexture(GLEnum.Texture2D, gl_tex_id);
            if (_unpackRowLength) // Not on WebGL/ES
            {
                SharedAPI.GL.PixelStore(GLEnum.UnpackRowLength, tex->Width);
                for (int i = 0; i < tex->Updates.Size; i++)
                {
                    ImTextureRect r = tex->Updates[i];
                    SharedAPI.GL.TexSubImage2D(GLEnum.Texture2D, 0, r.X, r.Y, (uint)r.W, (uint)r.H, GLEnum.Rgba, GLEnum.UnsignedByte, tex->GetPixelsAt(r.X, r.Y));
                }
                SharedAPI.GL.PixelStore(GLEnum.UnpackRowLength, 0);
            }
            else
            {
                // GL ES doesn't have GL_UNPACK_ROW_LENGTH, so we need to (A) copy to a contiguous buffer or (B) upload line by line.
                Data* bd = GetBackendData();
                for (int i = 0; i < tex->Updates.Size; i++)
                {
                    ImTextureRect r = tex->Updates[i];
                    int src_pitch = r.W * tex->BytesPerPixel;
                    int total_size = r.H * src_pitch;
                    bd->TempBuffer.Resize(total_size);
                    byte* dst_start = bd->TempBuffer.Data;
                    byte* dst = dst_start;
                    for (int y = 0; y < r.H; y++)
                    {
                        System.Buffer.MemoryCopy(tex->GetPixelsAt(r.X, r.Y + y), dst, src_pitch, src_pitch);
                        dst += src_pitch;
                    }
                    if (dst != dst_start + total_size)
                        throw new InvalidOperationException("TempBuffer copy size mismatch.");
                    SharedAPI.GL.TexSubImage2D(GLEnum.Texture2D, 0, r.X, r.Y, (uint)r.W, (uint)r.H, GLEnum.Rgba, GLEnum.UnsignedByte, bd->TempBuffer.Data);
                }
            }
            tex->SetStatus(ImTextureStatus.Ok);
            SharedAPI.GL.BindTexture(GLEnum.Texture2D, (uint)last_texture); // Restore state
        }
        else if (tex->Status == ImTextureStatus.WantDestroy && tex->UnusedFrames > 0)
            DestroyTexture(tex, false);
    }

    // If you get an error please report on github. You may try different GL context version or GLSL version. See GL<>GLSL version table at the top of this file.
    private unsafe static bool CheckShader(uint handle, string desc)
    {
        Data* bd = GetBackendData();
        int status = 0;
        int log_length = 0;
        SharedAPI.GL.GetShader(handle, GLEnum.CompileStatus, &status);
        SharedAPI.GL.GetShader(handle, GLEnum.InfoLogLength, &log_length);
        if (status == 0)
        {
            Log.Error($"ERROR: ImGui_ImplOpenGL3_CreateDeviceObjects: failed to compile {desc}! With GLSL: {_glslVersion}");
        }
        if (log_length > 1)
        {
            ImVector<byte> buf = default;
            buf.Resize(log_length + 1);
            SharedAPI.GL.GetShaderInfoLog(handle, (uint)log_length, null, buf.Data);
            Log.Error(Marshal.PtrToStringAnsi((IntPtr)buf.Data));
        }
        return status != 0;
    }

    // If you get an error please report on GitHub. You may try different GL context version or GLSL version.
    private unsafe static bool CheckProgram(uint handle, string desc)
    {
        Data* bd = GetBackendData();
        int status = 0, log_length = 0;
        SharedAPI.GL.GetProgram(handle, GLEnum.LinkStatus, &status);
        SharedAPI.GL.GetProgram(handle, GLEnum.InfoLogLength, &log_length);
        if (status == 0)
        {
            Log.Error($"ERROR: ImGui_ImplOpenGL3_CreateDeviceObjects: failed to link {desc}! With GLSL {_glslVersion}");
        }
        if (log_length > 1)
        {
            ImVector<char> buf = default;
            buf.Resize(log_length + 1);
            SharedAPI.GL.GetProgramInfoLog(handle, (uint)log_length, null, (byte*)buf.Data);
            Log.Error(Marshal.PtrToStringAnsi((IntPtr)buf.Data));
        }
        return status != 0;
    }

    private unsafe static bool CreateDeviceObjects()
    {
        Data* bd = GetBackendData();

        // Backup GL state
        SharedAPI.GL.GetInteger(GLEnum.TextureBinding2D, out int last_texture);
        SharedAPI.GL.GetInteger(GLEnum.ArrayBufferBinding, out int last_array_buffer);
        int last_pixel_unpack_buffer = 0;
        if (_mayHaveBindBufferPixelUnpack)
        {
            SharedAPI.GL.GetInteger(GLEnum.PixelUnpackBufferBinding, out last_pixel_unpack_buffer);
            SharedAPI.GL.BindBuffer(GLEnum.PixelUnpackBuffer, 0);
        }
        int last_vertex_array = 0;
        if (_useVertexArray)
            SharedAPI.GL.GetInteger(GLEnum.VertexArrayBinding, out last_vertex_array);

        // Parse GLSL version string
        int glsl_version = 130;
        if (_glslVersion != null && _glslVersion.StartsWith("#version "))
        {
            string numberPart = _glslVersion.Substring(9).Split(' ')[0];
            int.TryParse(numberPart, out glsl_version);
        }

        // Select shaders matching our GLSL versions
        string vertex_shader = null;
        string fragment_shader = null;
        if (glsl_version == 300)
        {
            vertex_shader = _vertexShaderGlsl300ES;
            fragment_shader = _fragmentShaderGlsl300ES;
        }
        else if (glsl_version < 130)
        {
            vertex_shader = _vertexShaderGlsl120;
            fragment_shader = _fragmentShaderGlsl120;
        }
        else if (glsl_version >= 410)
        {
            vertex_shader = _vertexShaderGlsl410Core;
            fragment_shader = _fragmentShaderGlsl410Core;
        }
        else
        {
            vertex_shader = _vertexShaderGlsl130;
            fragment_shader = _fragmentShaderGlsl130;
        }

        // Create shaders
        string[] vertex_shader_with_version = { _glslVersion, vertex_shader };
        uint vert_handle = SharedAPI.GL.CreateShader(GLEnum.VertexShader);
        SharedAPI.GL.ShaderSource(vert_handle, 2, vertex_shader_with_version, null);
        SharedAPI.GL.CompileShader(vert_handle);
        if (!CheckShader(vert_handle, "vertex shader"))
            return false;

        string[] fragment_shader_with_version = { _glslVersion, fragment_shader };
        uint frag_handle = SharedAPI.GL.CreateShader(GLEnum.FragmentShader);
        SharedAPI.GL.ShaderSource(frag_handle, 2, fragment_shader_with_version, null);
        SharedAPI.GL.CompileShader(frag_handle);
        if (!CheckShader(frag_handle, "fragment shader"))
            return false;

        // Link
        bd->ShaderHandle = SharedAPI.GL.CreateProgram();
        SharedAPI.GL.AttachShader(bd->ShaderHandle, vert_handle);
        SharedAPI.GL.AttachShader(bd->ShaderHandle, frag_handle);
        SharedAPI.GL.LinkProgram(bd->ShaderHandle);
        if (!CheckProgram(bd->ShaderHandle, "shader program"))
            return false;

        SharedAPI.GL.DetachShader(bd->ShaderHandle, vert_handle);
        SharedAPI.GL.DetachShader(bd->ShaderHandle, frag_handle);
        SharedAPI.GL.DeleteShader(vert_handle);
        SharedAPI.GL.DeleteShader(frag_handle);

        bd->AttribLocationTex = SharedAPI.GL.GetUniformLocation(bd->ShaderHandle, "Texture");
        bd->AttribLocationProjMtx = SharedAPI.GL.GetUniformLocation(bd->ShaderHandle, "ProjMtx");
        bd->AttribLocationVtxPos = (uint)SharedAPI.GL.GetAttribLocation(bd->ShaderHandle, "Position");
        bd->AttribLocationVtxUV = (uint)SharedAPI.GL.GetAttribLocation(bd->ShaderHandle, "UV");
        bd->AttribLocationVtxColor = (uint)SharedAPI.GL.GetAttribLocation(bd->ShaderHandle, "Color");

        // Create buffers
        SharedAPI.GL.GenBuffers(1, out bd->VboHandle);
        SharedAPI.GL.GenBuffers(1, out bd->ElementsHandle);

        // Restore modified GL state
        SharedAPI.GL.BindTexture(GLEnum.Texture2D, (uint)last_texture);
        SharedAPI.GL.BindBuffer(GLEnum.ArrayBuffer, (uint)last_array_buffer);
        if (_mayHaveBindBufferPixelUnpack)
            SharedAPI.GL.BindBuffer(GLEnum.PixelUnpackBuffer, (uint)last_pixel_unpack_buffer);
        if (_useVertexArray)
            SharedAPI.GL.BindVertexArray((uint)last_vertex_array);

        return true;
    }

    private unsafe static void DestroyDeviceObjects()
    {
        // Cannot call OpenGL here: Unity has already destroyed the current context.
        Data* bd = GetBackendData();
        if (bd->VboHandle != 0)
        {
            //SharedAPI.GL.DeleteBuffers(1, ref bd->VboHandle);
            bd->VboHandle = 0;
        }
        if (bd->ElementsHandle != 0)
        {
            //SharedAPI.GL.DeleteBuffers(1, ref bd->ElementsHandle);
            bd->ElementsHandle = 0;
        }
        if (bd->ShaderHandle != 0)
        {
            //SharedAPI.GL.DeleteProgram(bd->ShaderHandle);
            bd->ShaderHandle = 0;
        }

        // Destroy all textures
        ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
        ImVector<ImTextureDataPtr> textures = platformIO.Textures;
        for (int i = 0; i < textures.Size; i++)
        {
            ImTextureDataPtr tex = textures.Data[i];
            if (tex.RefCount == 1)
                DestroyTexture(tex, true);
        }
    }
}
