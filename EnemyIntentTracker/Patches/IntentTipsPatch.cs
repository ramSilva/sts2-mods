using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EnemyIntentTracker.Intent;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using Sts2Mods.Common;

namespace EnemyIntentTracker.Patches;

[HarmonyPatch]
internal static class HoverTipSetInitPatch
{
    private const string TypeName = "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet";

    static IEnumerable<MethodBase> TargetMethods()
    {
        var t = PatchTargets.ResolveType(TypeName);
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo? m = null;
        if (t != null)
        {
            foreach (var cand in t.GetMethods(Flags))
            {
                if (cand.Name != "CreateAndShow") continue;
                var ps = cand.GetParameters();
                if (ps.Length == 3 && typeof(IEnumerable<IHoverTip>).IsAssignableFrom(ps[1].ParameterType))
                { m = cand; break; }
            }
        }
        if (m == null)
        {
            var names = t == null ? "(type not found)" : string.Join(",", t.GetMethods(Flags).Where(x => x.Name == "CreateAndShow").Select(x => $"{x.Name}({string.Join(",", x.GetParameters().Select(p => p.ParameterType.Name))})"));
            ModEntry.LogWarn($"HoverTipSetInitPatch: no CreateAndShow(IEnumerable) method on '{TypeName}'; candidates=[{names}]");
            yield break;
        }

        ModEntry.Log($"HoverTipSetInitPatch: patching {t!.FullName}.CreateAndShow(IEnumerable).");
        yield return m;
    }

    private static readonly MethodInfo? _setTitle =
        typeof(HoverTip).GetProperty("Title", BindingFlags.Public | BindingFlags.Instance)?
            .GetSetMethod(nonPublic: true);
    private static readonly MethodInfo? _setDescription =
        typeof(HoverTip).GetProperty("Description", BindingFlags.Public | BindingFlags.Instance)?
            .GetSetMethod(nonPublic: true);
    private static readonly MethodInfo? _setId =
        typeof(HoverTip).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?
            .GetSetMethod(nonPublic: false);

    static void Prefix(Godot.Control owner, ref IEnumerable<IHoverTip> hoverTips)
    {
        try
        {
            var intentCtx = IntentOverlayBridge.Pending;
            if (!intentCtx.HasValue) return;
            AppendIntentPredictionTips(intentCtx.Value, ref hoverTips);
            IntentOverlayBridge.Pending = null;
        }
        catch (Exception ex) { ModEntry.LogWarn($"HoverTipSetInitPatch failed: {ex.Message}"); }
        finally { IntentOverlayBridge.Pending = null; }
    }

    private static readonly Dictionary<string, (long Ticks, string Sig)> _tipsEmitLast = new();
    private const long TipsEmitDedupeTicks = TimeSpan.TicksPerMillisecond * 500L;

    private static void AppendIntentPredictionTips(IntentHoverContext ctx, ref IEnumerable<IHoverTip> hoverTips)
    {
        var tips = PredictionContentResolver.Build(ctx.Prediction, ctx.Owner, ctx.Targets);
        LogTipsEmit(ctx.Prediction, tips);
        if (tips.Count == 0) return;

        var list = hoverTips == null ? new List<IHoverTip>() : hoverTips.ToList();
        foreach (var t in tips)
        {
            object boxed = new HoverTip(t.SeedLoc, t.Body, null!);
            _setTitle?.Invoke(boxed, new object?[] { t.Title });
            _setDescription?.Invoke(boxed, new object?[] { t.Body });
            _setId?.Invoke(boxed, new object?[] { t.Id });
            list.Add((IHoverTip)boxed);
        }
        hoverTips = list;
    }

    private static void LogTipsEmit(PredictionResult result, IReadOnlyList<PredictionTip> tips)
    {
        try
        {
            var nextRaw = result?.NextMoves == null ? "null" : string.Join(",", result.NextMoves.Select(m => $"{m.StateId}:{(m.Probability*100f).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}%"));
            var emitted = string.Join(" | ", tips.Select(t => $"\"{t.Title}\" body={t.Body?.Length ?? 0}c"));
            var sig = $"next=[{nextRaw}] emitted={tips.Count}: {emitted}";
            var key = result?.MonsterId ?? "?";
            var now = DateTime.UtcNow.Ticks;
            lock (_tipsEmitLast)
            {
                if (_tipsEmitLast.TryGetValue(key, out var prev) && prev.Sig == sig && now - prev.Ticks < TipsEmitDedupeTicks) return;
                _tipsEmitLast[key] = (now, sig);
            }
            ModEntry.Log($"intent-tips-emit: monster={key} {sig}");
        }
        catch (Exception ex) { ModEntry.LogWarn($"intent-tips-emit-fail: {ex.GetType().Name}:{ex.Message}"); }
    }
}
