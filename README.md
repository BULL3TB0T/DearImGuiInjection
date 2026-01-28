# DearImGuiInjection
Inject [Dear ImGui](https://github.com/ocornut/imgui) into any unity game.
## Renderers
- DirectX 11
- DirectX 12
- Vulkan
- OpenGLES2
- OpenGLES3
- OpenGLCore
## Mod Developers
To create your own GUI:
```csharp
using DearImGuiInjection;
using DearImGuiInjection.Loader;
using Hexa.NET.ImGui;

private ImGuiModule _module;
private bool _isVisible = true;

public void Awake()
{
    // Register an ImGui module (use a stable unique id, usually your plugin GUID).
    _module = DearImGuiInjectionCore.CreateModule("your.plugin.guid");

    // Hook lifecycle callbacks.
    _module.OnInit = OnInit;                        // Optional.
    _module.OnDispose = OnDispose;                  // Optional.
    _module.OnRender = OnRender;                    // Needed.
    _module.OnWndProcHandler = OnWndProcHandler;    // Optional.
}

private void OnInit()
{
    // Runs once when the module is initializing.
    // Set up appearance and defaults (style, fonts, etc).
    // You can call ImGui APIs here.
}

private void OnDispose()
{
    // Runs once when the module is being disposed/destroyed.
    // Release resources you created (textures, handles, unmanaged allocations, etc).
    // You can call ImGui APIs here.
}

private void OnRender()
{
    // Called every frame to render your UI.
    // You can call ImGui APIs here.
    
    if (!_isVisible)
        return;
    
    if (ImGui.Begin("DearImGuiInjection Example Window", ref _isVisible))
    {
        ImGui.Text("Hello from DearImGuiInjection!");
        ImGui.Separator();
        ImGui.BulletText("Press F2 to toggle this window.");
        if (ImGui.Button("Destroy this module."))
        {
            // UnityMainThreadDispatcher queues work to run on Unity's main thread (via Update).
            // Use it for teardown that may touch Unity/graphics state (safer than calling directly).
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                // Destroy the module. Basically unloading it.
                DearImGuiInjectionCore.DestroyModule(_module.Id);
            });
        }
        ImGui.End();
    }
}

private bool OnWndProcHandler(IntPtr hWnd, WindowMessage uMsg, IntPtr wParam, IntPtr lParam)
{
    // Toggle on F2 (but don’t steal keys while typing in ImGui).
    if (uMsg == WindowMessage.WM_KEYDOWN || uMsg == WindowMessage.WM_SYSKEYDOWN)
    {
        VirtualKey vk = (VirtualKey)wParam;
        if (!_module.IO.WantTextInput && vk == VirtualKey.VK_F2)
        {
            _isVisible = !_isVisible;
            return true; // Consumes key press.
        }
    }

    return false; // Does not consume key press.
}
```
## To-Do
Adding a texture manager that lets you register custom textures at runtime either from raw pixel data (RGBA) or by scanning/loading image files from a specified root folder path.
## Credits
- [Sewer56](https://github.com/Sewer56)
- [xiaoxiao921](https://github.com/xiaoxiao921) for the main repository!