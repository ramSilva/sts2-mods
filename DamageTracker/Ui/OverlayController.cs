using System;
using DamageTracker.Tracking;
using Godot;

namespace DamageTracker.Ui;

/// <summary>
/// Top-level controller for the floating damage table. Owns the current
/// <see cref="TrackingScope"/> shown by the overlay and is responsible for
/// attaching the overlay scene to a runtime UI host once the SceneTree is
/// available.
/// </summary>
public static class OverlayController
{
    public static TrackingScope CurrentScope { get; private set; } = TrackingScope.Combat;
    public static bool Visible { get; private set; } = true;

    private static DamageTableOverlay? _overlay;
    private static DebugConsoleOverlay? _debugConsole;

    public static event Action? ScopeChanged;

    public static void Install()
    {
        // Mod init runs while the scene tree root is mid-setup, so a synchronous
        // AddChild from here is rejected by Godot ("Parent node is busy setting
        // up children"). Defer both the timer attachment and the eventual
        // overlay attachment so they happen on the next idle frame.
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            ModEntry.LogWarn("Overlay install: no SceneTree available yet.");
            return;
        }

        var timer = new Godot.Timer { WaitTime = 0.25, OneShot = true, Autostart = true };
        timer.Timeout += () =>
        {
            try { AttachOverlay(); }
            catch (Exception ex) { ModEntry.LogWarn($"Overlay attach failed: {ex.Message}"); }
            finally { timer.QueueFree(); }
        };
        tree.Root.CallDeferred(Node.MethodName.AddChild, timer);
    }

    private static void AttachOverlay()
    {
        if (_overlay != null) return;
        if (Engine.GetMainLoop() is not SceneTree tree) return;

        // The Timer.Timeout callback runs on the main thread at a safe point in
        // the frame, well past the mid-startup window that originally forced
        // CallDeferred. AddChild synchronously here so the overlay enters the
        // tree before we run its setup.
        var canvas = new CanvasLayer { Layer = 1000, Name = "DamageTrackerCanvas" };
        tree.Root.AddChild(canvas);
        ModEntry.Log($"Overlay canvas added: layer={canvas.Layer} inTree={canvas.IsInsideTree()}");

        _overlay = new DamageTableOverlay { Name = "DamageTrackerOverlay" };
        canvas.AddChild(_overlay);
        _overlay.Build();
        ModEntry.Log($"Overlay added to canvas: inTree={_overlay.IsInsideTree()} visible={_overlay.Visible}");

        _debugConsole = new DebugConsoleOverlay { Name = "DamageTrackerDebugConsole" };
        canvas.AddChild(_debugConsole);
        _debugConsole.Build();

        InstallToggleHotkey(tree);
        InstallDebugConsoleHotkey(tree);
    }

    private static bool _f10Down;
    private static bool _backtickDown;

    // Poll Input each frame via the SceneTree's process_frame signal. Virtual
    // overrides like _Input aren't dispatched on nodes from a mod DLL (Godot's
    // C# bridge only registers types known at engine startup), so we route
    // hotkeys through a signal subscription instead.
    private static void InstallToggleHotkey(SceneTree tree)
    {
        tree.ProcessFrame += () =>
        {
            var nowDown = Input.IsKeyPressed(Key.F10);
            if (nowDown && !_f10Down) ToggleVisibility();
            _f10Down = nowDown;
        };
    }

    private static void InstallDebugConsoleHotkey(SceneTree tree)
    {
        tree.ProcessFrame += () =>
        {
            var nowDown = Input.IsKeyPressed(Key.Quoteleft);
            if (nowDown && !_backtickDown) _debugConsole?.Toggle();
            _backtickDown = nowDown;
        };
    }

    public static void SetScope(TrackingScope scope)
    {
        if (CurrentScope == scope) return;
        CurrentScope = scope;
        ScopeChanged?.Invoke();
    }

    public static void ToggleVisibility()
    {
        Visible = !Visible;
        if (_overlay != null) _overlay.Visible = Visible;
    }
}
