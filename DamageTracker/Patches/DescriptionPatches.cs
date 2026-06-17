using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using DamageTracker.Tracking;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using Sts2Mods.Common;

namespace DamageTracker.Patches;

/// <summary>
/// Renders the per-card tracker into a HoverTip injected at the end of the
/// card's hover popup. We deliberately avoid touching GetDescriptionForPile on
/// CardModel because in v0.103.3 that surface bleeds onto the card face (where
/// the user does not want extra text). The hover popup is a separate node tree
/// driven by CardModel.HoverTips.
/// </summary>
internal static class DescriptionFormatting
{
    private const string VULN_ID = "POWER.VULNERABLE_POWER";
    private const string WEAK_ID = "POWER.WEAK_POWER";
    private const string TEMP_STR_ID = "POWER.TEMPORARY_STRENGTH_POWER";
    private const string STR_ID = "POWER.STRENGTH_POWER";
    private const string ENEMY_STR_ID = "POWER.ENEMY_STRENGTH";
    private const string COLOSSUS_ID = "POWER.COLOSSUS_POWER";

    public static string BuildTrackerDescription(SourceKey key, long ownerInstanceId = 0L)
    {
        var (c, a, r) = DamageTrackerService.Instance.GetAll(key);
        var sb = new StringBuilder();
        var kinds = CardEffectManifest.Get(key);

        if (key.Kind == SourceKind.Power
            && ownerInstanceId != 0L
            && LedgerPolicy.MultiplicativeEnemyDebuffs.Contains(key.Id))
        {
            var svc = DamageTrackerService.Instance;
            var labelId = key.Id;
            var label = string.Equals(labelId, VULN_ID, StringComparison.OrdinalIgnoreCase) ? "Vulnerable damage"
                      : string.Equals(labelId, WEAK_ID, StringComparison.OrdinalIgnoreCase) ? "Weak damage"
                      : $"{svc.PowerNames.Resolve(labelId)} damage";
            AddLineAlways(sb, label,
                svc.GetGlobalDerivedDamageInstance(ownerInstanceId, labelId, TrackingScope.Combat),
                svc.GetGlobalDerivedDamageInstance(ownerInstanceId, labelId, TrackingScope.Act),
                svc.GetGlobalDerivedDamageInstance(ownerInstanceId, labelId, TrackingScope.Run));
            return "Damage Tracker  (Combat / Act / Run)\n" + sb.ToString().TrimEnd();
        }

        if (kinds == CardEffectKind.None)
        {
            AddLine(sb, "Damage dealt",   c.Damage,     a.Damage,     r.Damage);
            AddLine(sb, "Block gained",   c.Block,      a.Block,      r.Block);
            AddLine(sb, "Damage absorbed", c.AbsorbedBlock, a.AbsorbedBlock, r.AbsorbedBlock);
            AddLine(sb, "Self-damage",    c.SelfDamage, a.SelfDamage, r.SelfDamage);
            AddLine(sb, "Healing",        c.Healing,    a.Healing,    r.Healing);
            AddLine(sb, "Cards drawn",    c.CardsDrawn, a.CardsDrawn, r.CardsDrawn);
            AddLine(sb, "Cards exhausted", c.CardsExhausted, a.CardsExhausted, r.CardsExhausted);
            AddLine(sb, "Energy gained",  c.EnergyGained, a.EnergyGained, r.EnergyGained);
            AddLine(sb, "Strength gained", c.StrengthGained, a.StrengthGained, r.StrengthGained);
            AddLine(sb, "Enemy strength applied", c.AppliesEnemyStrength, a.AppliesEnemyStrength, r.AppliesEnemyStrength);
            AddLine(sb, "Times triggered", c.TimesTriggered, a.TimesTriggered, r.TimesTriggered);

            var nameMap = DamageTrackerService.Instance.PowerNames;
            foreach (var (powerId, label) in DistinctPowerIds(c.DerivedDamageByPower, a.DerivedDamageByPower, r.DerivedDamageByPower))
            {
                var name = nameMap.Resolve(powerId);
                AddLine(sb, $"{name} enabled damage",
                    c.DerivedDamageByPower.GetValueOrDefault(powerId),
                    a.DerivedDamageByPower.GetValueOrDefault(powerId),
                    r.DerivedDamageByPower.GetValueOrDefault(powerId));
            }
            foreach (var (powerId, label) in DistinctPowerIds(c.DerivedBlockByPower, a.DerivedBlockByPower, r.DerivedBlockByPower))
            {
                var name = nameMap.Resolve(powerId);
                AddLine(sb, $"{name} enabled block",
                    c.DerivedBlockByPower.GetValueOrDefault(powerId),
                    a.DerivedBlockByPower.GetValueOrDefault(powerId),
                    r.DerivedBlockByPower.GetValueOrDefault(powerId));
            }
            foreach (var pid in LedgerPolicy.MultiplicativeEnemyDebuffs)
            {
                if (!DamageTrackerService.Instance.IsPowerApplier(key, pid)) continue;
                var combat = DamageTrackerService.Instance.GetGlobalDerivedDamage(pid, TrackingScope.Combat);
                var act    = DamageTrackerService.Instance.GetGlobalDerivedDamage(pid, TrackingScope.Act);
                var run    = DamageTrackerService.Instance.GetGlobalDerivedDamage(pid, TrackingScope.Run);
                var name = nameMap.Resolve(pid);
                AddLine(sb, $"{name} damage", combat, act, run);
            }
        }
        else
        {
            if ((kinds & CardEffectKind.DealsDamage) != 0)
                AddLineAlways(sb, "Damage dealt", c.Damage, a.Damage, r.Damage);

            if ((kinds & CardEffectKind.GrantsBlock) != 0)
                AddLineAlways(sb, "Block gained", c.Block, a.Block, r.Block);

            if ((kinds & CardEffectKind.DamageAbsorbed) != 0)
                AddLineAlways(sb, "Damage absorbed", c.AbsorbedBlock, a.AbsorbedBlock, r.AbsorbedBlock);

            if ((kinds & CardEffectKind.AppliesVulnerable) != 0)
                AddLineAlways(sb, "Vulnerable applied",
                    c.PowersAppliedStacks.GetValueOrDefault(VULN_ID),
                    a.PowersAppliedStacks.GetValueOrDefault(VULN_ID),
                    r.PowersAppliedStacks.GetValueOrDefault(VULN_ID));

            if ((kinds & CardEffectKind.VulnerableDamage) != 0)
                AddLineAlways(sb, "Vulnerable damage",
                    DamageTrackerService.Instance.GetGlobalDerivedDamage(VULN_ID, TrackingScope.Combat),
                    DamageTrackerService.Instance.GetGlobalDerivedDamage(VULN_ID, TrackingScope.Act),
                    DamageTrackerService.Instance.GetGlobalDerivedDamage(VULN_ID, TrackingScope.Run));

            if ((kinds & CardEffectKind.AppliesWeak) != 0)
                AddLineAlways(sb, "Weak applied",
                    c.PowersAppliedStacks.GetValueOrDefault(WEAK_ID),
                    a.PowersAppliedStacks.GetValueOrDefault(WEAK_ID),
                    r.PowersAppliedStacks.GetValueOrDefault(WEAK_ID));

            if ((kinds & CardEffectKind.WeakDamage) != 0)
                AddLineAlways(sb, "Weak damage",
                    DamageTrackerService.Instance.GetGlobalDerivedDamage(WEAK_ID, TrackingScope.Combat),
                    DamageTrackerService.Instance.GetGlobalDerivedDamage(WEAK_ID, TrackingScope.Act),
                    DamageTrackerService.Instance.GetGlobalDerivedDamage(WEAK_ID, TrackingScope.Run));

            if ((kinds & CardEffectKind.GrantsTemporaryStrength) != 0)
            {
                AddLineAlways(sb, "Temporary strength enabled damage",
                    c.DerivedDamageByPower.GetValueOrDefault(TEMP_STR_ID),
                    a.DerivedDamageByPower.GetValueOrDefault(TEMP_STR_ID),
                    r.DerivedDamageByPower.GetValueOrDefault(TEMP_STR_ID));
            }

            if ((kinds & CardEffectKind.SelfDamage) != 0)
                AddLineAlways(sb, "HP spent", c.SelfDamage, a.SelfDamage, r.SelfDamage);

            if ((kinds & CardEffectKind.CheatsEnergy) != 0)
                AddLineAlways(sb, "Cheated energy", c.CheatedEnergy, a.CheatedEnergy, r.CheatedEnergy);

            if ((kinds & CardEffectKind.CardsDrawn) != 0)
                AddLineAlways(sb, "Cards drawn", c.CardsDrawn, a.CardsDrawn, r.CardsDrawn);

            if ((kinds & CardEffectKind.CardsExhausted) != 0)
                AddLineAlways(sb, "Cards exhausted", c.CardsExhausted, a.CardsExhausted, r.CardsExhausted);

            if ((kinds & CardEffectKind.EnergyGained) != 0)
                AddLineAlways(sb, "Energy gained", c.EnergyGained, a.EnergyGained, r.EnergyGained);

            if ((kinds & CardEffectKind.StrengthGained) != 0)
                AddLineAlways(sb, "Strength gained", c.StrengthGained, a.StrengthGained, r.StrengthGained);

            if ((kinds & CardEffectKind.StrengthDamage) != 0)
                AddLineAlways(sb, "Strength damage",
                    c.DerivedDamageByPower.GetValueOrDefault(STR_ID),
                    a.DerivedDamageByPower.GetValueOrDefault(STR_ID),
                    r.DerivedDamageByPower.GetValueOrDefault(STR_ID));

            if ((kinds & CardEffectKind.AppliesEnemyStrength) != 0)
            {
                var enemyStrengthReduced = c.AppliesEnemyStrength < 0 || a.AppliesEnemyStrength < 0 || r.AppliesEnemyStrength < 0;
                var label = enemyStrengthReduced ? "Enemy strength reduced" : "Enemy strength applied";
                AddLineAlways(sb, label,
                    Math.Abs(c.AppliesEnemyStrength), Math.Abs(a.AppliesEnemyStrength), Math.Abs(r.AppliesEnemyStrength));
            }

            if ((kinds & CardEffectKind.EnemyStrengthDamage) != 0)
            {
                var enemyStrengthReduced = c.AppliesEnemyStrength < 0 || a.AppliesEnemyStrength < 0 || r.AppliesEnemyStrength < 0;
                var label = enemyStrengthReduced ? "Damage negated" : "Enemy strength damage";
                AddLineAlways(sb, label,
                    c.DerivedDamageByPower.GetValueOrDefault(ENEMY_STR_ID),
                    a.DerivedDamageByPower.GetValueOrDefault(ENEMY_STR_ID),
                    r.DerivedDamageByPower.GetValueOrDefault(ENEMY_STR_ID));
            }

            if ((kinds & CardEffectKind.DamageReduced) != 0)
                AddLineAlways(sb, "Damage reduced",
                    c.DerivedDamageByPower.GetValueOrDefault(COLOSSUS_ID),
                    a.DerivedDamageByPower.GetValueOrDefault(COLOSSUS_ID),
                    r.DerivedDamageByPower.GetValueOrDefault(COLOSSUS_ID));

            if ((kinds & CardEffectKind.TimesTriggered) != 0)
                AddLineAlways(sb, "Times triggered", c.TimesTriggered, a.TimesTriggered, r.TimesTriggered);

            if ((kinds & CardEffectKind.Healing) != 0)
                AddLineAlways(sb, "Healing", c.Healing, a.Healing, r.Healing);

            if ((kinds & CardEffectKind.BlockPreserved) != 0)
                AddLineAlways(sb, "Block preserved", c.BlockPreserved, a.BlockPreserved, r.BlockPreserved);

            if ((kinds & CardEffectKind.CardsUpgraded) != 0)
                AddLineAlways(sb, "Cards upgraded", c.CardsUpgraded, a.CardsUpgraded, r.CardsUpgraded);
        }

        if (sb.Length == 0)
            return "Damage Tracker\nNo activity yet.";
        return "Damage Tracker  (Combat / Act / Run)\n" + sb.ToString().TrimEnd();
    }

    private static void AddLine(StringBuilder sb, string label, long combat, long act, long run)
    {
        if (combat == 0 && act == 0 && run == 0) return;
        sb.Append(label).Append(": ")
          .Append(combat).Append(" / ")
          .Append(act).Append(" / ")
          .Append(run).Append('\n');
    }

    private static void AddLineAlways(StringBuilder sb, string label, long combat, long act, long run)
    {
        sb.Append(label).Append(": ")
          .Append(combat).Append(" / ")
          .Append(act).Append(" / ")
          .Append(run).Append('\n');
    }

    private static IEnumerable<(string Id, string Label)> DistinctPowerIds(
        Dictionary<string, long> a, Dictionary<string, long> b, Dictionary<string, long> c)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in a.Keys) if (seen.Add(k)) yield return (k, k);
        foreach (var k in b.Keys) if (seen.Add(k)) yield return (k, k);
        foreach (var k in c.Keys) if (seen.Add(k)) yield return (k, k);
    }

    public static SourceKey? KeyForCardModel(object? cardModel)
    {
        if (cardModel == null) return null;
        var idObj = ReadAny(cardModel, "Id", "id");
        var id = idObj?.ToString();
        if (string.IsNullOrEmpty(id)) return null;
        var name = Reflect.AsText(ReadAny(cardModel, "Title", "TitleLocString", "displayName", "name"));
        return SourceKey.Card(id!, string.IsNullOrEmpty(name) ? id! : name!);
    }

    public static SourceKey? KeyForRelicModel(object? relicModel)
    {
        if (relicModel == null) return null;
        var idObj = ReadAny(relicModel, "Id", "id");
        var id = idObj?.ToString();
        if (string.IsNullOrEmpty(id)) return null;
        var name = Reflect.AsText(ReadAny(relicModel, "Title", "TitleLocString", "displayName", "name"));
        return SourceKey.Relic(id!, string.IsNullOrEmpty(name) ? id! : name!);
    }

    public static SourceKey? KeyForPowerModel(object? powerModel)
    {
        if (powerModel == null) return null;
        var idObj = ReadAny(powerModel, "Id", "id");
        var id = idObj?.ToString();
        if (string.IsNullOrEmpty(id)) return null;
        if (LedgerPolicy.SuppressedTrackerTipPowers.Contains(id!)) return null;

        var ownerInstanceId = Reflect.TryReadInstanceId(ReadAny(powerModel, "Owner")) ?? 0L;
        if (ownerInstanceId != 0L
            && !LedgerPolicy.MultiplicativeEnemyDebuffs.Contains(id!)
            && DamageTrackerService.Instance.TryGetPowerOriginCard(ownerInstanceId, id!, out var card))
        {
            return card;
        }

        var name = Reflect.AsText(ReadAny(powerModel, "Title", "TitleLocString", "displayName", "name"));
        return SourceKey.Power(id!, string.IsNullOrEmpty(name) ? id! : name!);
    }

    public static object? ReadAnyPublic(object instance, params string[] names) => ReadAny(instance, names);

    private static object? ReadAny(object instance, params string[] names)
    {

        var t = instance.GetType();
        foreach (var n in names)
        {
            var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) { var v = p.GetValue(instance); if (v != null) return v; }
            var f = t.GetField(n, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) { var v = f.GetValue(instance); if (v != null) return v; }
        }
        return null;
    }
}

/// <summary>
/// Captures the <c>CardModel</c> of the holder whose <c>CreateHoverTips</c>
/// is about to invoke <c>NHoverTipSet.CreateAndShow</c>. Godot's hover routing
/// hands the owner Control to <c>CreateAndShow</c> as a bare <c>Godot.Control</c>
/// wrapper, stripping the C# subclass identity, so reflection on the owner
/// arg cannot reach <c>CardModel</c>. The ThreadStatic slot bridges the two
/// patches reliably because both run on the Godot main thread sequentially.
/// </summary>
[HarmonyPatch]
internal static class CardHolderHoverTipsPatch
{
    [ThreadStatic] internal static object? PendingCardModel;

    static IEnumerable<MethodBase> TargetMethods()
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var tn in new[]
        {
            "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder",
            "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NPreviewCardHolder",
        })
        {
            var t = PatchTargets.ResolveType(tn);
            var m = t?.GetMethod("CreateHoverTips", Flags, null, Type.EmptyTypes, null);
            if (m != null)
            {
                ModEntry.Log($"CardHolderHoverTipsPatch: patching {tn}.CreateHoverTips.");
                yield return m;
            }
            else
            {
                ModEntry.LogWarn($"CardHolderHoverTipsPatch: CreateHoverTips not found on '{tn}'.");
            }
        }
    }

    static void Prefix(object __instance)
    {
        try
        {
            PendingCardModel = null;
            var t = __instance.GetType();
            var cardNodeProp = t.GetProperty("CardNode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var cardNode = cardNodeProp?.GetValue(__instance);
            if (cardNode == null) return;
            var modelProp = cardNode.GetType().GetProperty("Model", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            PendingCardModel = modelProp?.GetValue(cardNode);
        }
        catch (Exception ex) { PendingCardModel = null; ModEntry.LogWarn($"CardHolderHoverTipsPatch Prefix failed: {ex.Message}"); }
    }

    static void Postfix() { PendingCardModel = null; }
}

[HarmonyPatch]
internal static class NPowerHoverTipsPatch
{
    [ThreadStatic] internal static object? PendingPowerModel;

    static IEnumerable<MethodBase> TargetMethods()
    {
        const string tn = "MegaCrit.Sts2.Core.Nodes.Combat.NPower";
        var t = PatchTargets.ResolveType(tn);
        var m = t?.GetMethod("OnHovered", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (m != null)
        {
            ModEntry.Log($"NPowerHoverTipsPatch: patching {tn}.OnHovered.");
            yield return m;
        }
        else
        {
            ModEntry.LogWarn($"NPowerHoverTipsPatch: OnHovered not found on '{tn}'.");
        }
    }

    static void Prefix(object __instance)
    {
        try
        {
            PendingPowerModel = null;
            var t = __instance.GetType();
            var modelProp = t.GetProperty("Model", BindingFlags.Public | BindingFlags.Instance);
            PendingPowerModel = modelProp?.GetValue(__instance);
        }
        catch (Exception ex) { PendingPowerModel = null; ModEntry.LogWarn($"NPowerHoverTipsPatch Prefix failed: {ex.Message}"); }
    }

    static void Postfix() { PendingPowerModel = null; }
}

/// <summary>
/// Injects the tracker hover-tip into the renderer's input enumerable on
/// <c>NHoverTipSet.CreateAndShow(Control, IEnumerable&lt;IHoverTip&gt;, HoverTipAlignment)</c>.
/// All hover sources (NCardHolder, NRelicBasicHolder, NRelicInventoryHolder,
/// NPotionHolder, NOrb, NCreature.ShowHoverTips, etc.) funnel through this
/// static factory before scene instantiation. A prior patch on the instance
/// <c>Init</c> never fired because the IL <c>callvirt</c> on Init routes
/// through Godot's class table and bypasses the Harmony detour. We scan the
/// incoming tips for a card or relic <c>CanonicalModel</c> and append our
/// tracker tip with the matching scope.
///
/// <para>Constructing a <c>HoverTip</c> from <c>new LocString("", "")</c> is
/// unsafe because the ctor eagerly resolves the title and throws on an empty
/// loc table. We pass the model's own (known-valid) Title LocString into the
/// ctor, then overwrite <c>Title</c>/<c>Description</c> via reflection.</para>
/// </summary>
[HarmonyPatch]
internal static class HoverTipSetInitPatch
{
    private const string TypeName = "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet";
    private const string TrackerTitle = "Damage Tracker";

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

    static void Prefix(Godot.Control owner, ref IEnumerable<IHoverTip> hoverTips)
    {
        try
        {
            if (owner == null) return;

            object? cardModel = CardHolderHoverTipsPatch.PendingCardModel ?? TryReadCardModel(owner);
            object? relicModel = cardModel == null ? TryReadRelicModel(owner) : null;
            object? powerModel = cardModel == null && relicModel == null ? NPowerHoverTipsPatch.PendingPowerModel : null;
            object? model = cardModel ?? relicModel ?? powerModel;
            if (model == null) return;

            long powerOwnerInstanceId = 0L;
            if (powerModel != null)
                powerOwnerInstanceId = Reflect.TryReadInstanceId(DescriptionFormatting.ReadAnyPublic(powerModel, "Owner")) ?? 0L;

            SourceKey? key = cardModel != null
                ? DescriptionFormatting.KeyForCardModel(cardModel)
                : (relicModel != null
                    ? DescriptionFormatting.KeyForRelicModel(relicModel)
                    : DescriptionFormatting.KeyForPowerModel(powerModel));
            if (!key.HasValue) return;

            var titleLoc = DescriptionFormatting.ReadAnyPublic(model, "TitleLocString", "Title") as LocString;
            if (titleLoc == null) return;

            var list = hoverTips == null ? new List<IHoverTip>() : hoverTips.ToList();

            var body = DescriptionFormatting.BuildTrackerDescription(key.Value, powerOwnerInstanceId);
            var trackerTip = new HoverTip(titleLoc, body, null!);
            _setTitle?.Invoke(trackerTip, new object?[] { TrackerTitle });
            _setDescription?.Invoke(trackerTip, new object?[] { body });

            list.Add(trackerTip);
            hoverTips = list;
        }
        catch (Exception ex) { ModEntry.LogWarn($"HoverTipSetInitPatch failed: {ex.Message}"); }
    }

    private const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static object? TryReadCardModel(object owner)
    {
        var t = owner.GetType();
        for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
        {
            var p = cur.GetProperty("CardModel", AnyInst);
            if (p != null) { try { return p.GetValue(owner); } catch { } }
        }
        return null;
    }

    private static object? TryReadRelicModel(object owner)
    {
        var t = owner.GetType();
        for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
        {
            var f = cur.GetField("_model", AnyInst);
            if (f != null && f.FieldType.FullName == "MegaCrit.Sts2.Core.Models.RelicModel")
            { try { return f.GetValue(owner); } catch { } }
        }
        return null;
    }

}

[HarmonyPatch]
internal static class HoverTipSetOverflowReclampPatch
{
    private const string TypeName = "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet";

    private static readonly MethodInfo? _correctVertical =
        PatchTargets.ResolveType(TypeName)?.GetMethod(
            "CorrectVerticalOverflow",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null);

    private static readonly MethodInfo? _correctHorizontal =
        PatchTargets.ResolveType(TypeName)?.GetMethod(
            "CorrectHorizontalOverflow",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null);

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
            ModEntry.LogWarn($"HoverTipSetOverflowReclampPatch: CreateAndShow(IEnumerable) not found on '{TypeName}'.");
            yield break;
        }
        ModEntry.Log($"HoverTipSetOverflowReclampPatch: patching {t!.FullName}.CreateAndShow(IEnumerable).");
        yield return m;
    }

    static void Postfix(object __result)
    {
        try
        {
            if (__result is not Godot.Node node) return;
            if (_correctVertical == null && _correctHorizontal == null) return;
            var tree = node.GetTree();
            if (tree == null) return;

            Action? handler = null;
            handler = () =>
            {
                try
                {
                    tree.ProcessFrame -= handler!;
                    if (!Godot.GodotObject.IsInstanceValid(node)) return;
                    _correctVertical?.Invoke(node, null);
                    _correctHorizontal?.Invoke(node, null);
                }
                catch (Exception ex) { ModEntry.LogWarn($"HoverTipSetOverflowReclampPatch deferred reclamp failed: {ex.Message}"); }
            };
            tree.ProcessFrame += handler;
        }
        catch (Exception ex) { ModEntry.LogWarn($"HoverTipSetOverflowReclampPatch Postfix failed: {ex.Message}"); }
    }
}
