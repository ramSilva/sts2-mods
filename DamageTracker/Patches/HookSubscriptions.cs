using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DamageTracker.Tracking;
using HarmonyLib;

namespace DamageTracker.Patches;

/// <summary>
/// Subscribes to <c>MegaCrit.Sts2.Core.Hooks.Hook</c> entry points using
/// reflection so the mod degrades gracefully if a hook name or signature drifts
/// between game patches.
///
/// As of v0.103.3 the hook surface is no longer events/fields but plain public
/// static (async-iterator) methods on the Hook class. We support three shapes
/// in priority order: public static event, public static field holding a
/// HookList-like collection, and public static method (bridged via Harmony
/// postfix into <see cref="HookBridge"/>).
/// </summary>
internal static class HookSubscriptions
{
    private const string HookTypeName = "MegaCrit.Sts2.Core.Hooks.Hook";

    public static void Wire()
    {
        var hookType = ResolveHookType();
        if (hookType == null)
        {
            ModEntry.LogWarn($"Could not resolve {HookTypeName}; falling back to Harmony-only path.");
            return;
        }

        TryBind(hookType, "BeforeCombatStart", LifecycleHandlers.OnBeforeCombatStart);
        TryBind(hookType, "AfterCombatEnd",    LifecycleHandlers.OnAfterCombatEnd);
        TryBind(hookType, "AfterRoomEntered",  LifecycleHandlers.OnAfterRoomEntered);

        // Play-scope stack is still wired as a fallback for hooks that don't
        // expose a cardSource (e.g. AfterCurrentHpChanged for healing).
        TryBind(hookType, "BeforeCardPlayed",  AttributionHandlers.OnBeforeCardPlayed);
        TryBind(hookType, "BeforeCardAutoPlayed", AutoPlayHandlers.OnBeforeCardAutoPlayed);
        TryBind(hookType, "AfterCardPlayed",   AttributionHandlers.OnAfterCardPlayedLate);

        TryBind(hookType, "AfterDamageGiven",        DamageHandlers.OnAfterDamageGiven);
        TryBind(hookType, "BeforeDamageReceived",    DamageHandlers.OnBeforeDamageReceived);
        TryBind(hookType, "AfterModifyingBlockAmount", DamageHandlers.OnAfterModifyingBlockAmount);
        TryBind(hookType, "AfterPowerAmountChanged", DamageHandlers.OnAfterPowerAmountChanged);
        TryBind(hookType, "AfterCurrentHpChanged",   DamageHandlers.OnAfterCurrentHpChanged);

        TryBind(hookType, "BeforeTurnEnd",       TurnLifecycleHandlers.OnTurnEnd);
        TryBind(hookType, "AfterTurnEnd",        TurnLifecycleHandlers.OnTurnEnd);
        TryBind(hookType, "OnTurnEnd",           TurnLifecycleHandlers.OnTurnEnd);
        TryBind(hookType, "EndTurn",             TurnLifecycleHandlers.OnTurnEnd);
        TryBind(hookType, "BeforePlayerTurnEnd", TurnLifecycleHandlers.OnTurnEnd);
        TryBind(hookType, "AfterPlayerTurnEnd",  TurnLifecycleHandlers.OnTurnEnd);
        TryBind(hookType, "BeforeSideTurnStart", TurnLifecycleHandlers.OnSideTurnStart);

        TryBind(hookType, "AfterCardDrawn",            DrawHandlers.OnAfterCardDrawn);
        TryBind(hookType, "AfterCardExhausted",        ExhaustHandlers.OnAfterCardExhausted);
        TryBind(hookType, "ModifyEnergyGain",          EnergyHandlers.OnModifyEnergyGain);
        TryBind(hookType, "AfterCardChangedPiles",     SpawnHandlers.OnAfterCardChangedPiles);
    }

    private static Type? ResolveHookType()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => SafeGetType(a, HookTypeName))
            .FirstOrDefault(t => t != null);
    }

    private static Type? SafeGetType(Assembly a, string n)
    {
        try { return a.GetType(n, throwOnError: false); } catch { return null; }
    }

    /// <summary>
    /// Try, in order: public static event, public static field holding a
    /// HookList-like collection (with an Add method), and public static method
    /// (which we bridge via a Harmony postfix). Any failure is logged and
    /// non-fatal.
    /// </summary>
    private static void TryBind(Type hookType, string name, Delegate handler)
    {
        var ev = hookType.GetEvent(name, BindingFlags.Public | BindingFlags.Static);
        if (ev != null)
        {
            try
            {
                ev.AddEventHandler(null, ConvertDelegate(handler, ev.EventHandlerType!));
                ModEntry.Log($"Bound event Hook.{name}");
                return;
            }
            catch (Exception ex) { ModEntry.LogWarn($"Hook.{name} event bind failed: {ex.Message}"); }
        }

        var field = hookType.GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (field != null)
        {
            try
            {
                var holder = field.GetValue(null);
                var add = holder?.GetType().GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
                if (add != null && holder != null)
                {
                    var paramType = add.GetParameters()[0].ParameterType;
                    add.Invoke(holder, new object[] { ConvertDelegate(handler, paramType) });
                    ModEntry.Log($"Bound field Hook.{name}");
                    return;
                }
            }
            catch (Exception ex) { ModEntry.LogWarn($"Hook.{name} field bind failed: {ex.Message}"); }
        }

        var method = hookType.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        if (method != null && ModEntry.Harmony != null)
        {
            if (method.GetParameters().Any(p => p.ParameterType.IsByRef))
            {
                ModEntry.LogWarn($"Hook.{name} has byref parameters; skipping HookBridge postfix to avoid clobbering out/ref values.");
                return;
            }
            try
            {
                HookBridge.Register(method, handler);
                var postfix = new HarmonyMethod(typeof(HookBridge).GetMethod(nameof(HookBridge.Postfix), BindingFlags.Public | BindingFlags.Static)!);
                ModEntry.Harmony.Patch(method, postfix: postfix);
                ModEntry.Log($"Bound method Hook.{name} via Harmony postfix");
                return;
            }
            catch (Exception ex) { ModEntry.LogWarn($"Hook.{name} method patch failed: {ex.Message}"); }
        }

        ModEntry.LogWarn($"Hook.{name} not found; relevant feature may be incomplete.");
    }

    private static Delegate ConvertDelegate(Delegate src, Type targetType)
    {
        if (targetType.IsAssignableFrom(src.GetType())) return src;
        // Last-resort: rebind to the target delegate signature via MethodInfo.
        return Delegate.CreateDelegate(targetType, src.Target, src.Method);
    }
}

/// <summary>
/// Adapts arbitrary game-side static hook methods to the mod's handler
/// delegates. Registered handlers are invoked from a single shared Harmony
/// postfix; the original method's positional arguments are passed to the
/// handler up to the handler's parameter count. Argument shapes that don't
/// line up are tolerated by the handlers, which reflect over their inputs
/// by member name.
/// </summary>
internal static class HookBridge
{
    private static readonly Dictionary<MethodBase, Delegate> _registry = new();
    private static readonly HashSet<MethodBase> _logged = new();

    public static void Register(MethodBase method, Delegate handler) => _registry[method] = handler;

    public static void Postfix(MethodBase __originalMethod, object[] __args)
    {
        if (!_registry.TryGetValue(__originalMethod, out var handler)) return;
        try
        {
            if (_logged.Add(__originalMethod))
            {
                var shape = __args == null
                    ? "<null>"
                    : string.Join(", ", __args.Select((a, i) => $"[{i}]={(a == null ? "null" : a.GetType().FullName)}"));
                ModEntry.Log($"HookBridge first-fire {__originalMethod.Name}: argc={__args?.Length ?? 0} {shape}");
            }

            var payload = __args == null ? Array.Empty<object?>() : __args.Cast<object?>().ToArray();
            handler.DynamicInvoke(new object?[] { payload });
        }
        catch (Exception ex)
        {
            try { ModEntry.LogWarn($"Hook bridge invoke for {__originalMethod.Name} failed: {ex.Message}"); } catch { }
        }
    }
}
