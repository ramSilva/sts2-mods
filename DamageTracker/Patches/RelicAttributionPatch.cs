using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DamageTracker.Tracking;
using HarmonyLib;

namespace DamageTracker.Patches;

/// <summary>
/// Relics don't have a dedicated "before relic effect" hook in the documented
/// Hook surface, so we wrap their trigger methods with a Harmony prefix/postfix
/// that pushes/pops the relic onto the attribution stack. Any damage or block
/// recorded between Prefix and Postfix is credited to the relic.
///
/// The set of trigger method names is intentionally a hint list, matched
/// case-insensitively. Relic types that don't define any of these names are
/// simply skipped — those relics will not contribute to per-relic totals until
/// the list is extended.
/// </summary>
[HarmonyPatch]
internal static class RelicAttributionPatch
{
    private static readonly string[] TriggerHints =
    {
        "OnCardPlayed", "OnCardDrawn", "OnTurnStart", "OnTurnEnd",
        "OnCombatStart", "OnDamageGiven", "OnDamageReceived",
        "OnEnemyDeath", "Trigger", "OnUse",
    };

    private const string RelicBaseTypeName = "MegaCrit.Sts2.Core.Models.RelicModel";

    static IEnumerable<MethodBase> TargetMethods()
    {
        var relicBase = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => SafeGetType(a, RelicBaseTypeName))
            .FirstOrDefault(t => t != null);

        if (relicBase == null)
        {
            ModEntry.LogWarn($"Relic base type {RelicBaseTypeName} not found; relic attribution disabled.");
            yield break;
        }

        var asm = relicBase.Assembly;
        var subclasses = asm.GetTypes().Where(t => !t.IsAbstract && relicBase.IsAssignableFrom(t));

        foreach (var subclass in subclasses)
        {
            foreach (var name in TriggerHints)
            {
                var method = subclass.GetMethod(
                    name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (method != null && !method.IsAbstract && method.GetMethodBody() != null)
                    yield return method;
            }
        }
    }

    static void Prefix(object __instance, out IDisposable? __state)
    {
        __state = null;
        try
        {
            var key = ExtractRelicKey(__instance);
            if (key.HasValue) __state = DamageTrackerService.Instance.PushSource(key.Value);
        }
        catch (Exception ex)
        {
            ModEntry.LogWarn($"Relic prefix attribution failed: {ex.Message}");
        }
    }

    static void Postfix(IDisposable? __state)
    {
        __state?.Dispose();
    }

    private static SourceKey? ExtractRelicKey(object? relic)
    {
        if (relic == null) return null;
        var t = relic.GetType();
        var idProp = t.GetProperty("id", BindingFlags.Public | BindingFlags.Instance)
                  ?? t.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var nameProp = t.GetProperty("displayName", BindingFlags.Public | BindingFlags.Instance)
                    ?? t.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
        var id = idProp?.GetValue(relic)?.ToString() ?? t.Name;
        var displayName = nameProp?.GetValue(relic)?.ToString() ?? id;
        return SourceKey.Relic(id!, displayName);
    }

    private static Type? SafeGetType(Assembly a, string n)
    {
        try { return a.GetType(n, throwOnError: false); } catch { return null; }
    }
}
