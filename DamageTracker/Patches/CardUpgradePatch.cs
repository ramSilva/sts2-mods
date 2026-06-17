using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DamageTracker.Tracking;
using HarmonyLib;
using Sts2Mods.Common;

namespace DamageTracker.Patches;

[HarmonyPatch]
internal static class CardUpgradeTrigger
{
    private static readonly HashSet<(int token, int instanceId)> _credited = new();

    internal static SourceKey? PendingTurnStartUpgradeSource;

    static MethodBase? TargetMethod()
    {
        var t = TriggeredConditionTargets.TryResolveCardType("Card")
             ?? TriggeredConditionTargets.TryResolveCardType("CardModel");
        if (t == null) { ModEntry.LogWarn("CardUpgradeTrigger: card type not found"); return null; }

        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var all = new System.Collections.Generic.List<Type>();
        for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType) all.Add(cur);
        var candidates = all
            .SelectMany(ty => ty.GetMethods(BF))
            .Where(mi => mi.Name.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0)
            .Where(mi => !mi.IsAbstract && !mi.ContainsGenericParameters)
            .ToList();

        ModEntry.Log($"CardUpgradeTrigger: candidate Upgrade-named methods on {t.FullName} (and bases): "
            + string.Join(", ", candidates.Select(c => $"{c.DeclaringType?.Name}.{c.Name}({string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name))})->{c.ReturnType.Name}")));

        MethodInfo? pick = candidates.FirstOrDefault(c => c.Name == "Upgrade" && c.GetParameters().Length == 0 && c.ReturnType == typeof(void))
                       ?? candidates.FirstOrDefault(c => c.Name == "Upgrade" && c.GetParameters().Length == 0)
                       ?? candidates.FirstOrDefault(c => c.Name == "UpgradeCard" && c.GetParameters().Length == 0)
                       ?? candidates.FirstOrDefault(c => c.Name == "Upgrade")
                       ?? candidates.FirstOrDefault(c => c.Name == "UpgradeCard")
                       ?? candidates.FirstOrDefault(c => c.Name.StartsWith("Upgrade", StringComparison.OrdinalIgnoreCase)
                                                        && c.GetParameters().Length == 0);
        if (pick == null) { ModEntry.LogWarn($"CardUpgradeTrigger: no patchable Upgrade method found on {t.FullName}"); return null; }
        ModEntry.Log($"CardUpgradeTrigger: patching {pick.DeclaringType?.FullName}.{pick.Name}");
        return pick;
    }

    static bool ReadIsUpgraded(object instance) =>
        Reflect.ReadBool(instance, "IsUpgraded")
        ?? Reflect.ReadBool(instance, "Upgraded")
        ?? Reflect.ReadBool(instance, "isUpgraded")
        ?? false;

    static void Prefix(object __instance, out bool __state)
    {
        __state = ReadIsUpgraded(__instance);
    }

    static void Postfix(object __instance, bool __state)
    {
        try
        {
            var probeCardId = Reflect.ReadAny(__instance, "Id")?.ToString() ?? "<none>";
            var probeInstId = Reflect.TryReadInstanceId(__instance) ?? -1L;
            var probeToken  = DamageTrackerService.Instance.CurrentCardPlayToken;
            ModEntry.Log($"CardUpgradeTrigger fire: cardId={probeCardId} instId={probeInstId} token={probeToken} pendingTurnStartUpgradeSource={PendingTurnStartUpgradeSource?.Id ?? "null"}");
            if (__state) return;
            if (!ReadIsUpgraded(__instance)) return;

            var svc = DamageTrackerService.Instance;
            var src = svc.CurrentSource();
            var token = svc.CurrentCardPlayToken;

            if (token <= 0 && !PendingTurnStartUpgradeSource.HasValue) return;

            if (Reflect.ReadAny(__instance, "Pile", "pile") == null) return;

            var instId = Reflect.TryReadInstanceId(__instance) ?? RuntimeHelpers.GetHashCode(__instance);
            if (token > 0 && src.HasValue && src.Value.Kind == SourceKind.Card)
            {
                var key = (token, (int)(instId & 0x7FFFFFFF));
                lock (_credited)
                {
                    if (!_credited.Add(key)) return;
                }
                svc.RecordCardsUpgraded(1, src);
            }
            else if (token <= 0 && PendingTurnStartUpgradeSource.HasValue)
            {
                var key = (0, (int)(instId & 0x7FFFFFFF));
                lock (_credited)
                {
                    if (!_credited.Add(key)) return;
                }
                svc.RecordCardsUpgraded(1, PendingTurnStartUpgradeSource.Value);
                PendingTurnStartUpgradeSource = null;
            }
        }
        catch (Exception ex) { ModEntry.LogWarn($"CardUpgradeTrigger postfix failed: {ex.Message}"); }
    }

    internal static void ClearForNewCardPlay()
    {
        lock (_credited) _credited.Clear();
    }
}
