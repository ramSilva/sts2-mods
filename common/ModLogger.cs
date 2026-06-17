namespace Sts2Mods.Common;

public static class ModLogger
{
    public static void Log(string modId, string message)
    {
        try { Godot.GD.Print($"[{modId}] {message}"); } catch { }
    }

    public static void LogWarn(string modId, string message)
    {
        var formatted = $"[{modId}] WARN: {message}";
        try { Godot.GD.PushWarning(formatted); return; } catch { }
        try { Godot.GD.Print(formatted); } catch { }
    }

    public static void LogError(string modId, string message)
    {
        var formatted = $"[{modId}] ERROR: {message}";
        try { Godot.GD.PushError(formatted); return; } catch { }
        try { Godot.GD.Print(formatted); } catch { }
    }
}
