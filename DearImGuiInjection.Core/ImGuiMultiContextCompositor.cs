using Hexa.NET.ImGui;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace DearImGuiInjection;

// Multi-Context Compositor v0.11, for Dear ImGui
// Get latest version at http://www.github.com/ocornut/imgui_club
// Slightly modified by BULLETBOT

public sealed class ImGuiMultiContextCompositor
{
    internal readonly List<ImGuiModule> Modules = new();
    internal readonly List<ImGuiModule> ModulesMouseOwnerLast = new();
    internal readonly List<ImGuiModule> ModulesFrontToBack = new();

    private ImGuiContextPtr _ctxMouseFirst = null;
    private ImGuiContextPtr _ctxMouseShape = null;
    private ImGuiContextPtr _ctxKeyboardExclusive = null;
    private ImGuiContextPtr _ctxDragDropSrc = null;
    private ImGuiContextPtr _ctxDragDropDst = null;
    private ImGuiPayload _dragDropPayload;

    internal void AddModule(ImGuiModule module)
    {
        Debug.Assert(!Modules.Contains(module));
        Modules.Add(module);
        ModulesMouseOwnerLast.Add(module);
        ModulesFrontToBack.Add(module);
    }

    internal void RemoveModule(ImGuiModule module)
    {
        Modules.Remove(module);
        ModulesMouseOwnerLast.Remove(module);
        ModulesFrontToBack.Remove(module);
    }

    private void BringModuleToFront(ImGuiModule module, ImGuiModule module_to_keep_inputs_for)
    {
        ModulesFrontToBack.Remove(module);
        ModulesFrontToBack.Insert(0, module);
        for (int i = 0; i < ModulesFrontToBack.Count; i++)
        {
            ImGuiModule other = ModulesFrontToBack[i];
            if (other != module && other != module_to_keep_inputs_for)
                other.IO.ClearInputKeys();
        }
    }

    private unsafe bool DragDropGetPayloadFromSourceContext()
    {
        ImGuiContextPtr src_ctx = _ctxDragDropSrc;
        fixed (ImGuiPayload* dst_payload = &_dragDropPayload)
        {
            if (!src_ctx.DragDropActive)
                return false;
            if ((src_ctx.DragDropSourceFlags & ImGuiDragDropFlags.PayloadNoCrossContext) != 0)
                return false;
            fixed (ImGuiPayload* src_payload = &src_ctx.DragDropPayload)
            {
                *dst_payload = *src_payload;
                dst_payload->Data = ImGui.MemAlloc((nuint)src_payload->DataSize);
                Buffer.MemoryCopy(src_payload->Data, dst_payload->Data, src_payload->DataSize, src_payload->DataSize);
            }
            return true;
        }
    }

    private unsafe void DragDropSetPayloadToDestContext(ImGuiContextPtr dstCtx)
    {
        Debug.Assert(dstCtx == ImGui.GetCurrentContext());
        fixed (ImGuiPayload* src_payload = &_dragDropPayload)
        {
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceExtern | ImGuiDragDropFlags.SourceNoPreviewTooltip))
            {
                byte* type = stackalloc byte[33];
                type[0] = src_payload->DataType_0;
                type[1] = src_payload->DataType_1;
                type[2] = src_payload->DataType_2;
                type[3] = src_payload->DataType_3;
                type[4] = src_payload->DataType_4;
                type[5] = src_payload->DataType_5;
                type[6] = src_payload->DataType_6;
                type[7] = src_payload->DataType_7;
                type[8] = src_payload->DataType_8;
                type[9] = src_payload->DataType_9;
                type[10] = src_payload->DataType_10;
                type[11] = src_payload->DataType_11;
                type[12] = src_payload->DataType_12;
                type[13] = src_payload->DataType_13;
                type[14] = src_payload->DataType_14;
                type[15] = src_payload->DataType_15;
                type[16] = src_payload->DataType_16;
                type[17] = src_payload->DataType_17;
                type[18] = src_payload->DataType_18;
                type[19] = src_payload->DataType_19;
                type[20] = src_payload->DataType_20;
                type[21] = src_payload->DataType_21;
                type[22] = src_payload->DataType_22;
                type[23] = src_payload->DataType_23;
                type[24] = src_payload->DataType_24;
                type[25] = src_payload->DataType_25;
                type[26] = src_payload->DataType_26;
                type[27] = src_payload->DataType_27;
                type[28] = src_payload->DataType_28;
                type[29] = src_payload->DataType_29;
                type[30] = src_payload->DataType_30;
                type[31] = src_payload->DataType_31;
                type[32] = src_payload->DataType_32;
                ImGui.SetDragDropPayload(type, src_payload->Data, (nuint)src_payload->DataSize);
                ImGui.EndDragDropSource();
            }
        }
    }

    private unsafe void DragDropFreePayload(ImGuiPayload* payload)
    {
        ImGui.MemFree(payload->Data);
        payload->Data = null;
    }

    private ImGuiModule FindModuleByContext(ImGuiContextPtr ctx)
    {
        for (int i = 0; i < Modules.Count; i++)
        {
            ImGuiModule module = Modules[i];
            if (module.Context == ctx)
                return module;
        }
        return null;
    }

    internal unsafe void PreNewFrameUpdateAll()
    {
        // Early out when there are no modules
        if (Modules.Count <= 0)
            return;

        // Clear transient data
        _ctxMouseFirst = null;
        _ctxMouseShape = null;
        _ctxKeyboardExclusive = null;
        _ctxDragDropSrc = null;
        _ctxDragDropDst = null;
        _dragDropPayload.Clear();

        // Sync point (before NewFrame calls)
        // PASS 1:
        // - Find out who will receive mouse position (one or multiple contexts)
        // - FInd out who will change mouse cursor (one context)
        // - Find out who has an active drag and drop
        for (int i = 0; i < ModulesFrontToBack.Count; i++)
        {
            ImGuiModule module = ModulesFrontToBack[i];
            ImGuiContextPtr ctx = module.Context;
            ImGuiIOPtr io = module.IO;

            // When hovering a main/shared viewport,
            // - feed mouse front-to-back until reaching context that has io.WantCaptureMouse.
            // - track second context to pass drag and drop payload
            if (io.WantCaptureMouse && _ctxMouseFirst.IsNull)
                _ctxMouseFirst = ctx;
            if (!ctx.HoveredWindowBeforeClear.IsNull && _ctxDragDropDst.IsNull)
                _ctxDragDropDst = ctx;

            // Who owns mouse shape?
            if (_ctxMouseShape.IsNull && ctx.MouseCursor != ImGuiMouseCursor.Arrow)
                _ctxMouseShape = ctx;

            // Who owns drag and drop source?
            if (ctx.DragDropActive && (ctx.DragDropSourceFlags & ImGuiDragDropFlags.SourceExtern) == 0 && _ctxDragDropSrc.IsNull)
                _ctxDragDropSrc = ctx;
            else if (!ctx.DragDropActive && _ctxDragDropSrc == ctx)
                _ctxDragDropSrc = null;
        }

        // If no secondary viewport are focused, we'll keep keyboard to top-most context
        if (_ctxKeyboardExclusive.IsNull)
            _ctxKeyboardExclusive = ModulesFrontToBack[0].Context;

        // Deep copy payload for replication
        if (!_ctxDragDropSrc.IsNull)
            DragDropGetPayloadFromSourceContext();
        if (!_ctxDragDropDst.IsNull && _dragDropPayload.Data == null)
            _ctxDragDropDst = null;

        // Bring drag target context to front when using DragDropHold press
        // FIXME-MULTICONTEXT: Works but change of order means source tooltip not visible anymore...
        // - Solution 1 ? if user code always submitted drag and drop tooltip derived from payload data
        //   instead of submitting at drag source location, this wouldn't be a problem at the front
        //   most context could always display the tooltip. But it's a constraint.
        // - Solution 2 ? would be a more elaborate composited rendering, where top layer (tooltip)
        //   of one ImDrawData would be moved to another ImDrawData.
        // - Solution 3 ? somehow find a way to enforce tooltip always on own viewport, always on top?
        // Ultimately this is not so important, it's already quite a fun luxury to have cross context DND.
#if false
        if (!_ctxDragDropDst.IsNull && _ctxDragDropDst != ModulesFrontToBack.First().Context)
            if (_ctxDragDropDst.DragDropHoldJustPressedId != 0)
                BringModuleToFront(FindModuleByContext(_ctxDragDropDst), ModulesFrontToBack[0]);
#endif

        // PASS 2:
        // - Enable/disable mouse interactions on selected contexts.
        // - Enable/disable mouse cursor change so only 1 context can do it.
        // - Bring a context to front whenever clicked any of its windows.
        // - Select a single mouse-owning context to draw the ImGui cursor.
        bool mouse_draw_cursor = DearImGuiInjectionCore.MouseDrawCursor.GetValue();
        bool is_above_ctx_with_mouse_first = true;
        for (int i = 0; i < ModulesFrontToBack.Count; i++)
        {
            ImGuiModule module = ModulesFrontToBack[i];
            ImGuiContextPtr ctx = module.Context;
            ImGuiIOPtr io = module.IO;
            bool ctx_is_front = i == 0;

            // Focused secondary viewport or top-most context in shared viewport gets keyboard
            if (_ctxKeyboardExclusive == ctx)
                io.ConfigFlags &= ~ImGuiConfigFlags.NoKeyboard; // Allow keyboard interactions
            else
                io.ConfigFlags |= ImGuiConfigFlags.NoKeyboard; // Disable keyboard interactions

            // Top-most context with MouseCursor shape request gets it
            if (_ctxMouseShape.IsNull || _ctxMouseShape == ctx)
                io.ConfigFlags &= ~ImGuiConfigFlags.NoMouseCursorChange; // Allow mouse cursor changes
            else
                io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange; // Disable mouse cursor changes

            // Top-most io.WantCaptureMouse context & anything above it gets mouse interactions
            if (is_above_ctx_with_mouse_first || _ctxDragDropDst == ctx)
                io.ConfigFlags &= ~ImGuiConfigFlags.NoMouse; // Allow mouse interactions
            else
                io.ConfigFlags |= ImGuiConfigFlags.NoMouse; // Disable mouse interactions

            // Bring to front on click
            if (_ctxMouseFirst == ctx && !ctx_is_front)
            {
                /*
                bool any_mouse_clicked = false; // conceptually a ~ImGui::IsAnyMouseClicked(), not worth adding to API.
                for (int n = 0; n < io.MouseClicked.Length; n++)
                    any_mouse_clicked |= io.MouseClicked[n];
                if (any_mouse_clicked)
                */
                if (io.MouseClicked[0])
                    BringModuleToFront(module, null);
            }

            // Top-most io.WantCaptureMouse context owns cursor drawing when mouseDrawCursor is on
            if (mouse_draw_cursor)
            {
                io.MouseDrawCursor = _ctxMouseFirst == ctx;

                // Put cursor-drawing context always last (WndProc uses this order for cursor ownership)
                if (io.MouseDrawCursor
                    && module != ModulesMouseOwnerLast[ModulesMouseOwnerLast.Count - 1])
                {
                    ModulesMouseOwnerLast.Remove(module);
                    ModulesMouseOwnerLast.Add(module);
                }
            }

            if (_ctxMouseFirst == ctx)
                is_above_ctx_with_mouse_first = false;
        }
    }

    // This could technically be registered as a hook, but it would make things too magical.
    internal void PostNewFrameUpdateOne(ImGuiModule module)
    {
        // Propagate drag and drop
        // (against all odds since we are only READING from 'mcc' and writing to our target
        // context this should be parallel/threading friendly)
        ImGuiContextPtr ctx = module.Context;
        if (_ctxDragDropDst == ctx && _ctxDragDropDst != _ctxDragDropSrc)
            DragDropSetPayloadToDestContext(ctx);
    }

    internal unsafe void PostEndFrameUpdateAll()
    {
        // Clear drag and drop payload
        fixed (ImGuiPayload* dragDropPayload = &_dragDropPayload)
            if (dragDropPayload->Data != null)
                DragDropFreePayload(dragDropPayload);
    }

    public void ShowDebugWindow()
    {
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().Pos);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(1.0f, 1.0f, 1.0f, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
        ImGui.Begin("Multi-Context Compositor Overlay", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoInputs);
        ImGui.SeparatorText("Multi-Context Compositor");
        ImGui.Text("Front: " + (ModulesFrontToBack.Count > 0 ? ModulesFrontToBack[0].Id : null));
        ImGui.Text("MousePos first: " + (!_ctxMouseFirst.IsNull ? FindModuleByContext(_ctxMouseFirst).Id : ""));
        ImGui.Text("Keyboard excl.: " + (!_ctxKeyboardExclusive.IsNull ? FindModuleByContext(_ctxKeyboardExclusive).Id : ""));
        ImGui.Text("DragDrop src: " + (!_ctxDragDropSrc.IsNull ? FindModuleByContext(_ctxDragDropSrc).Id : ""));
        ImGui.Text("DragDrop dst: " + (!_ctxDragDropDst.IsNull ? FindModuleByContext(_ctxDragDropDst).Id : ""));
        ImGui.End();
        ImGui.PopStyleColor(2);
    }
}
