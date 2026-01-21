using DearImGuiInjection.Windows;
using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjection;

internal sealed class MinHookDetour<TDelegate> where TDelegate : Delegate
{
    private static bool _isInitialized;

    private IntPtr _target;
    private TDelegate _detourDelegate;
    private IntPtr _detour;

    private bool _created;
    private bool _enabled;
    private bool _disposed;

    public string Name { get; private set; }
    public TDelegate Original { get; private set; }

    public MinHookDetour(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is null.", nameof(name));
        Name = name;
    }

    public void Create(IntPtr target, TDelegate detour)
    {
        if (_created || _disposed)
            return;
        if (target == IntPtr.Zero)
            throw new ArgumentException("Target is null.", nameof(target));
        if (detour == null)
            throw new ArgumentException("Detour is null.", nameof(detour));
        _target = target;
        _detourDelegate = detour;
        MinHook.Ok(MinHook.CreateHook(_target, Marshal.GetFunctionPointerForDelegate(_detourDelegate), out IntPtr original), $"MH_CreateHook({Name})");
        Original = Marshal.GetDelegateForFunctionPointer<TDelegate>(original);
        _created = true;
    }

    public void Enable()
    {
        if (_enabled || _disposed || !_created)
            return;
        MinHook.Ok(MinHook.EnableHook(_target), $"MH_EnableHook({Name})");
        _enabled = true;
    }

    public void Disable()
    {
        if (!_enabled || _disposed || !_created)
            return;
        MinHook.Ok(MinHook.DisableHook(_target), $"MH_DisableHook({Name})");
        _enabled = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_enabled)
        {
            MinHook.Ok(MinHook.DisableHook(_target), $"MH_DisableHook({Name})");
            _enabled = false;
        }
        if (_created)
        {
            MinHook.Ok(MinHook.RemoveHook(_target), $"MH_RemoveHook({Name})");
            _created = false;
        }
        _disposed = true;
    }
}