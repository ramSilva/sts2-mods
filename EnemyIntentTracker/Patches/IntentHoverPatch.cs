using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using EnemyIntentTracker.Intent;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace EnemyIntentTracker.Patches;

[HarmonyPatch]
internal static class IntentHoverPatch
{
    private const long DedupeIntervalTicks = TimeSpan.TicksPerMillisecond * 250L;
    internal static readonly IntentPredictor _predictor = new();
    private static readonly ConditionalWeakTable<Creature, EmitStamp> _lastEmit = new();

    static MethodBase? TargetMethod()
    {
        var t = typeof(NIntent);
        var mi = t.GetMethod("OnHovered", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (mi == null)
        {
            ModEntry.LogWarn($"IntentHoverPatch: OnHovered not found on {t.FullName}");
            return null;
        }
        ModEntry.Log($"IntentHoverPatch: patching {t.FullName}.OnHovered");
        return mi;
    }

    static void Prefix(NIntent __instance)
    {
        IntentOverlayBridge.Pending = null;
        try
        {
            var owner = StateMachineReflection.GetOwner(__instance);
            if (owner == null) return;
            var result = _predictor.Predict(owner);
            if (result == null) return;
            var targets = StateMachineReflection.GetTargets(__instance);
            IntentOverlayBridge.Pending = new IntentHoverContext(result, owner, targets);
            if (ShouldEmit(owner)) LogResult(result);
        }
        catch (Exception ex)
        {
            IntentOverlayBridge.Pending = null;
            ModEntry.LogWarn($"IntentHoverPatch prefix failed: {ex.Message}");
        }
    }

    private static bool ShouldEmit(Creature owner)
    {
        var now = DateTime.UtcNow.Ticks;
        if (_lastEmit.TryGetValue(owner, out var stamp))
        {
            if (now - stamp.Ticks < DedupeIntervalTicks) return false;
            stamp.Ticks = now;
            return true;
        }
        _lastEmit.Add(owner, new EmitStamp { Ticks = now });
        return true;
    }

    private static void LogResult(PredictionResult r)
    {
        var stunnedTag = r.IsStunnedThisTurn ? " stunned" : "";
        if (r.Failure != null)
        {
            var line = $"intent-predict: monster={r.MonsterId} current={r.CurrentMoveId}{stunnedTag} next={r.Failure.Kind}({r.Failure.Detail})";
            var kind = r.Failure.Kind;
            if (kind == PredictionFailureKind.DeadCreature || kind == PredictionFailureKind.EmptyNextMove)
                ModEntry.Log(line);
            else
                ModEntry.LogWarn(line);
            return;
        }
        var moves = string.Join(", ", r.NextMoves.Select(m =>
            $"{m.StateId}:{(m.Probability * 100f).ToString("F1", CultureInfo.InvariantCulture)}%"));
        ModEntry.Log($"intent-predict: monster={r.MonsterId} current={r.CurrentMoveId}{stunnedTag} next={{{moves}}}");
    }

    private sealed class EmitStamp
    {
        public long Ticks;
    }
}

[HarmonyPatch]
internal static class IntentUnhoverPatch
{
    static MethodBase? TargetMethod()
    {
        var t = typeof(NIntent);
        var mi = t.GetMethod("OnUnhovered", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (mi == null)
        {
            ModEntry.LogWarn($"IntentUnhoverPatch: OnUnhovered not found on {t.FullName}");
            return null;
        }
        ModEntry.Log($"IntentUnhoverPatch: patching {t.FullName}.OnUnhovered");
        return mi;
    }

    static void Postfix() { IntentOverlayBridge.Pending = null; }
}

[HarmonyPatch]
internal static class CreatureBodyHoverPatch
{
    static MethodBase? TargetMethod()
    {
        var t = typeof(NCreature);
        var mi = t.GetMethod("OnFocus", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (mi == null)
        {
            ModEntry.LogWarn($"CreatureBodyHoverPatch: OnFocus not found on {t.FullName}");
            return null;
        }
        ModEntry.Log($"CreatureBodyHoverPatch: patching {t.FullName}.OnFocus");
        return mi;
    }

    static void Prefix(NCreature __instance)
    {
        IntentOverlayBridge.Pending = null;
        try
        {
            var entity = __instance.Entity;
            if (entity == null || !entity.IsMonster) return;
            var result = IntentHoverPatch._predictor.Predict(entity);
            if (result == null) return;
            var targets = ResolvePlayerTargets(entity);
            IntentOverlayBridge.Pending = new IntentHoverContext(result, entity, targets);
        }
        catch (Exception ex)
        {
            IntentOverlayBridge.Pending = null;
            ModEntry.LogWarn($"creature-hover-patch failed: {ex.Message}");
        }
    }

    private static IEnumerable<Creature> ResolvePlayerTargets(Creature monster)
    {
        try
        {
            var combat = monster.CombatState;
            if (combat == null) return Array.Empty<Creature>();
            var list = new List<Creature>();
            foreach (var pc in combat.PlayerCreatures)
            {
                if (pc != null && pc.IsPlayer) list.Add(pc);
            }
            return list.Count == 0 ? Array.Empty<Creature>() : list;
        }
        catch { return Array.Empty<Creature>(); }
    }
}

[HarmonyPatch]
internal static class CreatureBodyUnhoverPatch
{
    static MethodBase? TargetMethod()
    {
        var t = typeof(NCreature);
        var mi = t.GetMethod("OnUnfocus", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (mi == null)
        {
            ModEntry.LogWarn($"CreatureBodyUnhoverPatch: OnUnfocus not found on {t.FullName}");
            return null;
        }
        ModEntry.Log($"CreatureBodyUnhoverPatch: patching {t.FullName}.OnUnfocus");
        return mi;
    }

    static void Postfix() { IntentOverlayBridge.Pending = null; }
}

[HarmonyPatch]
internal static class HealthBarHoverPatch
{
    private static readonly FieldInfo? _creatureField =
        typeof(NCreatureStateDisplay).GetField("_creature", BindingFlags.Instance | BindingFlags.NonPublic);

    static MethodBase? TargetMethod()
    {
        var t = typeof(NCreatureStateDisplay);
        var mi = t.GetMethod("OnHovered", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (mi == null)
        {
            ModEntry.LogWarn($"HealthBarHoverPatch: OnHovered not found on {t.FullName}");
            return null;
        }
        ModEntry.Log($"HealthBarHoverPatch: patching {t.FullName}.OnHovered");
        return mi;
    }

    static void Prefix(NCreatureStateDisplay __instance)
    {
        IntentOverlayBridge.Pending = null;
        try
        {
            var creature = _creatureField?.GetValue(__instance) as Creature;
            if (creature == null || !creature.IsMonster) return;
            var result = IntentHoverPatch._predictor.Predict(creature);
            if (result == null) return;
            var targets = ResolvePlayerTargets(creature);
            IntentOverlayBridge.Pending = new IntentHoverContext(result, creature, targets);
        }
        catch (Exception ex)
        {
            IntentOverlayBridge.Pending = null;
            ModEntry.LogWarn($"hp-bar-hover failed: {ex.Message}");
        }
    }

    private static IEnumerable<Creature> ResolvePlayerTargets(Creature monster)
    {
        try
        {
            var combat = monster.CombatState;
            if (combat == null) return Array.Empty<Creature>();
            var list = new List<Creature>();
            foreach (var pc in combat.PlayerCreatures)
            {
                if (pc != null && pc.IsPlayer) list.Add(pc);
            }
            return list.Count == 0 ? Array.Empty<Creature>() : list;
        }
        catch { return Array.Empty<Creature>(); }
    }
}

[HarmonyPatch]
internal static class HealthBarUnhoverPatch
{
    static MethodBase? TargetMethod()
    {
        var t = typeof(NCreatureStateDisplay);
        var mi = t.GetMethod("OnUnhovered", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (mi == null)
        {
            ModEntry.LogWarn($"HealthBarUnhoverPatch: OnUnhovered not found on {t.FullName}");
            return null;
        }
        ModEntry.Log($"HealthBarUnhoverPatch: patching {t.FullName}.OnUnhovered");
        return mi;
    }

    static void Postfix() { IntentOverlayBridge.Pending = null; }
}
