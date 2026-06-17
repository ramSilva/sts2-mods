using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Sts2Mods.Common;

namespace DamageTracker.Debug;

internal static class DevConsoleBridge
{
    private const string DevConsoleTypeName = "MegaCrit.Sts2.Core.DevConsole.DevConsole";
    private const string CmdBaseTypeName = "MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.AbstractConsoleCmd";
    private const string RunManagerTypeName = "MegaCrit.Sts2.Core.Runs.RunManager";

    private static object? _devConsole;
    private static MethodInfo? _processCommandString;
    private static MethodInfo? _processCommandPlayer;
    private static Assembly? _gameAssembly;
    private static bool _initialized;

    public static string Execute(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return "ERR: empty command";
        EnsureInitialized();
        if (_devConsole == null) return "ERR: dev console unavailable";

        if (_processCommandString != null)
        {
            try
            {
                var result = _processCommandString.Invoke(_devConsole, new object[] { line });
                var (success, msg, hasTask) = ReadCmdResult(result);
                if (success || !LooksLikePlayerError(msg))
                    return Format(success, msg, hasTask);
            }
            catch (Exception ex)
            {
                ModEntry.LogWarn($"DevConsoleBridge: ProcessCommand(string) threw: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        if (_processCommandPlayer != null)
        {
            var player = ResolvePlayer();
            if (player == null) return "ERR: no player available";
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "ERR: empty command";
            var cmdName = parts[0];
            var args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();
            try
            {
                var result = _processCommandPlayer.Invoke(_devConsole, new object?[] { player, cmdName, args });
                var (success, msg, hasTask) = ReadCmdResult(result);
                return Format(success, msg, hasTask);
            }
            catch (Exception ex)
            {
                return $"ERR: {ex.InnerException?.Message ?? ex.Message}";
            }
        }

        return "ERR: no ProcessCommand overload available";
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _gameAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "sts2")
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => SafeGetType(a, DevConsoleTypeName) != null);
            if (_gameAssembly == null) { ModEntry.LogWarn("DevConsoleBridge: game assembly not found"); return; }

            var devConsoleType = _gameAssembly.GetType(DevConsoleTypeName);
            var cmdBaseType = _gameAssembly.GetType(CmdBaseTypeName);
            if (devConsoleType == null || cmdBaseType == null)
            {
                ModEntry.LogWarn("DevConsoleBridge: required game types not found");
                return;
            }

            _devConsole = CreateDevConsole(devConsoleType);
            if (_devConsole == null) { ModEntry.LogWarn("DevConsoleBridge: failed to construct DevConsole"); return; }

            foreach (var m in devConsoleType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "ProcessCommand") continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    _processCommandString = m;
                else if (ps.Length == 3 && ps[1].ParameterType == typeof(string) && ps[2].ParameterType == typeof(string[]))
                    _processCommandPlayer = m;
            }

            RegisterAllCommands(devConsoleType, cmdBaseType);
        }
        catch (Exception ex)
        {
            ModEntry.LogWarn($"DevConsoleBridge: init failed: {ex.Message}");
        }
    }

    private static void RegisterAllCommands(Type devConsoleType, Type cmdBaseType)
    {
        var register = devConsoleType.GetMethod("RegisterCommand", BindingFlags.Public | BindingFlags.Instance);
        if (register == null) { ModEntry.LogWarn("DevConsoleBridge: RegisterCommand not found"); return; }

        int ok = 0, fail = 0;
        foreach (var t in SafeGetTypes(_gameAssembly!))
        {
            if (t.IsAbstract || !cmdBaseType.IsAssignableFrom(t)) continue;
            try
            {
                var cmd = Activator.CreateInstance(t);
                register.Invoke(_devConsole, new[] { cmd });
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                ModEntry.LogWarn($"DevConsoleBridge: register {t.Name} failed: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
        ModEntry.Log($"DevConsoleBridge: registered {ok} commands ({fail} failed)");
    }

    private static object? CreateDevConsole(Type t)
    {
        try { return Activator.CreateInstance(t); } catch { }
#pragma warning disable SYSLIB0050
        try { return FormatterServices.GetUninitializedObject(t); } catch { return null; }
#pragma warning restore SYSLIB0050
    }

    private static (bool success, string? msg, bool hasTask) ReadCmdResult(object? result)
    {
        if (result == null) return (false, "null result", false);
        var t = result.GetType();
        bool success = false;
        string? msg = null;
        bool hasTask = false;
        var sf = t.GetField("success", BindingFlags.Public | BindingFlags.Instance);
        if (sf?.GetValue(result) is bool b) success = b;
        var mf = t.GetField("msg", BindingFlags.Public | BindingFlags.Instance);
        if (mf != null) msg = mf.GetValue(result) as string;
        var tf = t.GetField("task", BindingFlags.Public | BindingFlags.Instance);
        if (tf?.GetValue(result) is Task) hasTask = true;
        return (success, msg, hasTask);
    }

    private static string Format(bool success, string? msg, bool hasTask)
    {
        var prefix = success ? "OK" : "ERR";
        var suffix = hasTask ? " (async, queued)" : string.Empty;
        return $"{prefix}: {msg ?? string.Empty}{suffix}";
    }

    private static bool LooksLikePlayerError(string? msg) =>
        !string.IsNullOrEmpty(msg) && msg!.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0;

    private static object? ResolvePlayer()
    {
        var runManagerType = _gameAssembly?.GetType(RunManagerTypeName);
        var instance = Reflect.GetStaticProperty(runManagerType, "Instance");
        if (instance == null) return null;
        var state = Reflect.ReadAny(instance, "State");
        if (state == null) return null;
        if (Reflect.ReadAny(state, "Players") is IEnumerable players)
            foreach (var p in players) if (p != null) return p;
        return Reflect.ReadAny(state, "Player");
    }

    private static Type? SafeGetType(Assembly a, string name)
    {
        try { return a.GetType(name, false); } catch { return null; }
    }

    private static Type[] SafeGetTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).Select(t => t!).ToArray(); }
        catch { return Array.Empty<Type>(); }
    }
}
