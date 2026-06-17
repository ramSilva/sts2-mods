using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DamageTracker.Tracking;
using HarmonyLib;
using Sts2Mods.Common;

namespace DamageTracker.Patches;

internal static class TriggeredConditionTargets
{
    public static Type? TryResolveCardType(string simpleName)
    {
        var asms = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var asm in asms)
        {
            try
            {
                var t = asm.GetType($"MegaCrit.Sts2.Core.Models.Cards.{simpleName}", throwOnError: false);
                if (t != null) return t;
            }
            catch { }
        }
        foreach (var asm in asms)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }
            var match = types.FirstOrDefault(t => string.Equals(t.Name, simpleName, StringComparison.Ordinal));
            if (match != null) return match;
        }
        return null;
    }
}

[HarmonyPatch]
internal static class DismantleTriggerPatch
{
    static MethodBase? TargetMethod()
    {
        var t = TriggeredConditionTargets.TryResolveCardType("Dismantle");
        if (t == null) { ModEntry.LogWarn("DismantleTriggerPatch: type not found"); return null; }
        var m = t.GetMethod("OnPlay", BindingFlags.NonPublic | BindingFlags.Instance);
        if (m == null) { ModEntry.LogWarn("DismantleTriggerPatch: OnPlay not found"); return null; }
        ModEntry.Log($"DismantleTriggerPatch: patching {t.FullName}.OnPlay");
        return m;
    }

    static void Postfix(object[] __args)
    {
        try
        {
            var cardPlay = __args.FirstOrDefault(a => a != null && a.GetType().Name == "CardPlay");
            if (cardPlay == null) return;
            var target = Reflect.ReadAny(cardPlay, "Target");
            if (target == null) return;
            bool isVulnerable = false;
            foreach (var p in Reflect.IteratePowers(target))
            {
                var pid = Reflect.ReadAny(p, "Id")?.ToString();
                if (!string.Equals(pid, "POWER.VULNERABLE_POWER", StringComparison.OrdinalIgnoreCase)) continue;
                var amt = Reflect.ReadLong(p, "Amount") ?? Reflect.ReadLong(p, "amount") ?? 0;
                if (amt > 0) { isVulnerable = true; break; }
            }
            if (isVulnerable)
                DamageTrackerService.Instance.RecordTimesTriggered(1, SourceKey.Card("CARD.DISMANTLE", "Dismantle"));
        }
        catch (Exception ex) { ModEntry.LogWarn($"DismantleTriggerPatch postfix failed: {ex.Message}"); }
    }
}

[HarmonyPatch]
internal static class SpiteTriggerPatch
{
    static MethodBase? TargetMethod()
    {
        var t = TriggeredConditionTargets.TryResolveCardType("Spite");
        if (t == null) { ModEntry.LogWarn("SpiteTriggerPatch: type not found"); return null; }
        var m = t.GetMethod("OnPlay", BindingFlags.NonPublic | BindingFlags.Instance);
        if (m == null) { ModEntry.LogWarn("SpiteTriggerPatch: OnPlay not found"); return null; }
        ModEntry.Log($"SpiteTriggerPatch: patching {t.FullName}.OnPlay");
        return m;
    }

    static void Postfix(object __instance)
    {
        try
        {
            var owner = Reflect.ReadAny(__instance, "Owner");
            var creature = Reflect.ReadAny(owner, "Creature");
            if (creature == null) return;
            var t = __instance.GetType();
            var lostMethod = t.GetMethod("LostHpThisTurn", BindingFlags.NonPublic | BindingFlags.Static);
            if (lostMethod == null) return;
            bool lost = (bool)(lostMethod.Invoke(null, new object?[] { creature }) ?? false);
            if (lost)
                DamageTrackerService.Instance.RecordTimesTriggered(1, SourceKey.Card("CARD.SPITE", "Spite"));
        }
        catch (Exception ex) { ModEntry.LogWarn($"SpiteTriggerPatch postfix failed: {ex.Message}"); }
    }
}

[HarmonyPatch]
internal static class EvilEyeTriggerPatch
{
    static MethodBase? TargetMethod()
    {
        var t = TriggeredConditionTargets.TryResolveCardType("EvilEye");
        if (t == null) { ModEntry.LogWarn("EvilEyeTriggerPatch: type not found"); return null; }
        var m = t.GetMethod("OnPlay", BindingFlags.NonPublic | BindingFlags.Instance);
        if (m == null) { ModEntry.LogWarn("EvilEyeTriggerPatch: OnPlay not found"); return null; }
        ModEntry.Log($"EvilEyeTriggerPatch: patching {t.FullName}.OnPlay");
        return m;
    }

    static void Postfix(object __instance)
    {
        try
        {
            var t = __instance.GetType();
            var prop = t.GetProperty("WasCardExhaustedThisTurn", BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop == null) return;
            var v = prop.GetValue(__instance);
            if (v is bool b && b)
                DamageTrackerService.Instance.RecordTimesTriggered(1, SourceKey.Card("CARD.EVIL_EYE", "Evil Eye"));
        }
        catch (Exception ex) { ModEntry.LogWarn($"EvilEyeTriggerPatch postfix failed: {ex.Message}"); }
    }
}

[HarmonyPatch]
internal static class HowlFromBeyondTriggerPatch
{
    static MethodBase? TargetMethod()
    {
        var t = TriggeredConditionTargets.TryResolveCardType("HowlFromBeyond");
        if (t == null) { ModEntry.LogWarn("HowlFromBeyondTriggerPatch: type not found"); return null; }
        var m = t.GetMethod("BeforeHandDraw", BindingFlags.Public | BindingFlags.Instance);
        if (m == null) { ModEntry.LogWarn("HowlFromBeyondTriggerPatch: BeforeHandDraw not found"); return null; }
        ModEntry.Log($"HowlFromBeyondTriggerPatch: patching {t.FullName}.BeforeHandDraw");
        return m;
    }

    static void Postfix(object __instance, object[] __args)
    {
        try
        {
            var pile = Reflect.ReadAny(__instance, "Pile");
            var pileType = Reflect.ReadAny(pile, "Type")?.ToString();
            if (pileType != "Exhaust") return;
            var owner = Reflect.ReadAny(__instance, "Owner");
            var player = __args.FirstOrDefault(a => a != null && a.GetType().Name == "Player");
            if (!ReferenceEquals(player, owner)) return;
            DamageTrackerService.Instance.RecordTimesTriggered(1, SourceKey.Card("CARD.HOWL_FROM_BEYOND", "Howl from Beyond"));
        }
        catch (Exception ex) { ModEntry.LogWarn($"HowlFromBeyondTriggerPatch postfix failed: {ex.Message}"); }
    }
}
