using DearImGuiInjection;
using DearImGuiInjection.Backends;
using DearImGuiInjection.Renderers;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using Silk.NET.OpenGL;
using System;
using System.Runtime.InteropServices;
using System.Threading;

internal sealed class ImGuiOpenGLRenderer : ImGuiRenderer
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool WglSwapBuffersDelegate(IntPtr hdc);
    private MinHookDetour<WglSwapBuffersDelegate> _wglSwapBuffers;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WglGetProcAddressDelegate([MarshalAs(UnmanagedType.LPStr)] string name);

    public override void Init()
    {
        IntPtr openGL32 = TryLoadOpenGL();
        MinHook.Ok(MinHook.Initialize(), "MH_Initialize");
        _wglSwapBuffers = new("wglSwapBuffers");
        _wglSwapBuffers.Create(Kernel32.GetProcAddress(openGL32, _wglSwapBuffers.Name), WglSwapBuffersDetour);
        _wglSwapBuffers.Enable();
    }

    public override void Dispose()
    {
        _wglSwapBuffers.Dispose();
        MinHook.Ok(MinHook.Uninitialize(), "MH_Uninitialize");
        foreach (ImGuiModule module in DearImGuiInjectionCore.MultiContextCompositor.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            Shutdown(module.IsInitialized);
        }
        SharedAPI.GL.Dispose();
    }

    public override void Shutdown(bool isInitialized)
    {
        if (isInitialized)
            ImGuiImplOpenGL.Shutdown();
        ImGuiImplWin32.Shutdown();
    }

    private unsafe bool WglSwapBuffersDetour(IntPtr hdc)
    {
        CanAttachWindowHandle();
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
                ImGuiImplOpenGL.Init();
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
            ImGuiImplOpenGL.NewFrame();
            ImGui.NewFrame();
            DearImGuiInjectionCore.MultiContextCompositor.PostNewFrameUpdateOne(module);
            try
            {
                module.OnRender();
                ImGui.Render();
                ImGuiImplOpenGL.RenderDrawData(ImGui.GetDrawData());
            }
            catch (Exception e)
            {
                ImGui.EndFrame();
                DearImGuiInjectionCore.DestroyModule(module.Id);
                Log.Error($"Module \"{module.Id}\" OnRender threw an exception: {e}");
            }
        }
        DearImGuiInjectionCore.MultiContextCompositor.PostEndFrameUpdateAll();
        return _wglSwapBuffers.Original(hdc);
    }

    private IntPtr TryLoadOpenGL()
    {
        string libraryName = "opengl32.dll";
        IntPtr openGL32 = Kernel32.LoadLibrary(libraryName);
        if (openGL32 == IntPtr.Zero)
            throw new InvalidOperationException($"{libraryName} is not loaded.");
        IntPtr pWglGetProcAddress = Kernel32.GetProcAddress(openGL32, "wglGetProcAddress");
        if (pWglGetProcAddress == IntPtr.Zero)
            throw new InvalidOperationException($"wglGetProcAddress not found in {libraryName}.");
        WglGetProcAddressDelegate wglGetProcAddress = Marshal.GetDelegateForFunctionPointer<WglGetProcAddressDelegate>(pWglGetProcAddress);
        SharedAPI.GL = GL.GetApi(name =>
        {
            IntPtr procAddress = wglGetProcAddress(name);
            if (procAddress != IntPtr.Zero)
            {
                long p = procAddress.ToInt64();
                if (p != 0 && p != 1 && p != 2 && p != 3 && p != 4 && p != -1)
                    return procAddress;
            }
            return Kernel32.GetProcAddress(openGL32, name);
        });
        return openGL32;
    }
}
