using System.Reflection;
using Sts2Mods.Common;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.ValueProps;

namespace EnemyIntentTracker.Intent;

internal static class NextTurnDamageSimulator
{
    private const string StrengthPowerId = "POWER.STRENGTH_POWER";
    private const string VigorPowerId = "POWER.VIGOR_POWER";
    private const string IntangiblePowerId = "POWER.INTANGIBLE_POWER";

    private static readonly HashSet<string> DecayingDebuffPowerIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "POWER.WEAK_POWER", "POWER.FRAIL_POWER", "POWER.VULNERABLE_POWER",
    };

    private static readonly Dictionary<string, (string PowerId, decimal Multiplier)> TargetIncomingDebuffs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VulnerablePower"] = ("POWER.VULNERABLE_POWER", 1.5m),
    };

    private static readonly HashSet<string> RecurringStrengthGrowthPowerIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "POWER.TERRITORIAL_POWER",
    };

    private static readonly FieldInfo? _moveStateOnPerformField =
        typeof(MoveState).GetField("_onPerform", BindingFlags.Instance | BindingFlags.NonPublic);

    public static int? TryPredictDamage(
        AttackIntent intent,
        Creature owner,
        IEnumerable<Creature> targets,
        MoveState? nextTurnMoveState,
        string monsterIdForLog,
        string stateIdForLog,
        MoveState? currentTurnMoveState = null)
    {
        try
        {
            var calc = intent?.DamageCalc;
            decimal baseDamage = calc == null ? 0m : Convert.ToDecimal(calc());
            if (calc == null || baseDamage <= 0m)
                return (int)Math.Max(0m, baseDamage);

            Creature? target = null;
            if (targets != null)
            {
                foreach (var t in targets)
                {
                    if (t == null) continue;
                    if (!ReferenceEquals(t, owner)) { target = t; break; }
                }
                if (target == null)
                {
                    foreach (var t in targets) { if (t != null) { target = t; break; } }
                }
            }

            long dealerStrength = ReadStrength(owner);
            long dealerStrengthDelta = 0;
            if (nextTurnMoveState != null) dealerStrengthDelta += ReadStrengthDeltaFromMove(nextTurnMoveState, monsterIdForLog, stateIdForLog);
            if (currentTurnMoveState != null) dealerStrengthDelta += ReadStrengthDeltaFromMove(currentTurnMoveState, monsterIdForLog, stateIdForLog);
            long recurringStrengthDelta = ReadRecurringStrengthDelta(owner, monsterIdForLog);
            dealerStrengthDelta += recurringStrengthDelta;

            long predictedStrength = dealerStrength + dealerStrengthDelta;

            bool currentMoveAttacks = CurrentMovePerformsAttack(currentTurnMoveState, monsterIdForLog, stateIdForLog);
            long dealerVigorBaseline = currentMoveAttacks ? 0 : ReadVigor(owner);
            long dealerVigorDelta = 0;
            if (currentTurnMoveState != null) dealerVigorDelta += ReadVigorDeltaFromMove(currentTurnMoveState, monsterIdForLog, stateIdForLog);
            long predictedVigor = dealerVigorBaseline + dealerVigorDelta;

            var targetIncoming = target == null
                ? null
                : ResolveTargetIncomingDebuffsFromCombat(owner, monsterIdForLog, stateIdForLog);
            decimal dealerMult = ComputeSideMult(owner, owner, target, null);
            decimal targetMult = target == null ? 1m : ComputeSideMult(target, owner, target, targetIncoming);

            decimal predicted = (baseDamage + predictedStrength + predictedVigor) * dealerMult * targetMult;
            bool targetIntangibleNextTurn = target != null && PredictsIntangibleNextTurn(target);
            if (targetIntangibleNextTurn && predicted > 1m) predicted = 1m;
            ModEntry.Log($"predict-damage: monster={monsterIdForLog} state={stateIdForLog} base={baseDamage} str={predictedStrength} strRecur={recurringStrengthDelta} vig={predictedVigor} vigBase={dealerVigorBaseline} vigDelta={dealerVigorDelta} currentAttacks={currentMoveAttacks} dealerMult={dealerMult} targetMult={targetMult} intangible={targetIntangibleNextTurn} predicted={predicted}");
            return predicted > 0m ? (int)Math.Floor(predicted) : 0;
        }
        catch (Exception ex)
        {
            ModEntry.LogWarn($"predict-damage-fail: monster={monsterIdForLog} state={stateIdForLog} ex={ex.GetType().Name}:{ex.Message}");
            return null;
        }
    }

    private static long ReadStrengthDeltaFromMove(MoveState moveState, string monsterIdForLog, string stateIdForLog)
    {
        if (_moveStateOnPerformField == null) return 0;
        MethodInfo? moveMethod = null;
        try
        {
            var del = _moveStateOnPerformField.GetValue(moveState) as Delegate;
            moveMethod = del?.Method;
        }
        catch { moveMethod = null; }
        if (moveMethod == null) return 0;

        var extracted = MoveBodyIntrospector.TryExtractFirstPowerApply(moveMethod, monsterIdForLog, stateIdForLog);
        if (extracted != null && extracted.Value.PowerTypeName != null
            && extracted.Value.PowerTypeName.EndsWith("StrengthPower", StringComparison.OrdinalIgnoreCase))
        {
            return extracted.Value.Amount;
        }
        return 0;
    }

    private static long ReadStrength(Creature? creature)
        => Reflect.TryReadPowerAmount(creature, StrengthPowerId) ?? 0;

    private static long ReadRecurringStrengthDelta(Creature? creature, string monsterIdForLog)
    {
        if (creature == null || RecurringStrengthGrowthPowerIds.Count == 0) return 0;
        long total = 0;
        foreach (var power in Reflect.IteratePowers(creature))
        {
            var pid = Reflect.ReadAny(power, "Id")?.ToString();
            if (pid == null || !RecurringStrengthGrowthPowerIds.Contains(pid)) continue;
            long amount = Reflect.ReadLong(power, "Amount") ?? Reflect.ReadLong(power, "amount") ?? 0;
            if (amount == 0) continue;
            total += amount;
            ModEntry.Log($"recurring-str-growth: monster={monsterIdForLog} pid={pid} amount={amount}");
        }
        return total;
    }

    private static long ReadVigorDeltaFromMove(MoveState moveState, string monsterIdForLog, string stateIdForLog)
    {
        if (_moveStateOnPerformField == null) return 0;
        MethodInfo? moveMethod = null;
        try
        {
            var del = _moveStateOnPerformField.GetValue(moveState) as Delegate;
            moveMethod = del?.Method;
        }
        catch { moveMethod = null; }
        if (moveMethod == null) return 0;

        var extracted = MoveBodyIntrospector.TryExtractFirstPowerApply(moveMethod, monsterIdForLog, stateIdForLog);
        if (extracted != null && extracted.Value.PowerTypeName != null
            && extracted.Value.PowerTypeName.EndsWith("VigorPower", StringComparison.OrdinalIgnoreCase))
        {
            return extracted.Value.Amount;
        }
        return 0;
    }

    private static long ReadVigor(Creature? creature)
        => Reflect.TryReadPowerAmount(creature, VigorPowerId) ?? 0;

    private static bool PredictsIntangibleNextTurn(Creature creature)
    {
        foreach (var power in Reflect.IteratePowers(creature))
        {
            var pid = Reflect.ReadAny(power, "Id")?.ToString();
            if (!string.Equals(pid, IntangiblePowerId, StringComparison.OrdinalIgnoreCase)) continue;
            long amount = Reflect.ReadLong(power, "Amount") ?? Reflect.ReadLong(power, "amount") ?? 0;
            bool skipTick = ReadSkipNextDurationTick(power);
            long predicted = skipTick ? amount : amount - 1;
            return predicted > 0;
        }
        return false;
    }

    private static bool CurrentMovePerformsAttack(MoveState? ms, string monsterIdForLog, string stateIdForLog)
    {
        if (ms == null) return false;
        try
        {
            var intents = ms.Intents;
            if (intents == null) return false;
            foreach (var intent in intents)
            {
                if (intent is AttackIntent) return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            ModEntry.LogWarn($"current-move-attack-check-fail: monster={monsterIdForLog} state={stateIdForLog} ex={ex.GetType().Name}:{ex.Message}");
            return false;
        }
    }

    private static decimal ComputeSideMult(Creature side, Creature? dealer, Creature? target,
        IReadOnlyDictionary<string, (long Stacks, decimal SyntheticMultiplier)>? incoming)
    {
        var powers = Reflect.ReadAny(side, "Powers", "powers", "PowersList", "ActivePowers") as System.Collections.IEnumerable;
        var product = 1m;
        int seen = 0;
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (powers != null)
        {
            foreach (var power in powers)
            {
                if (power == null) continue;
                seen++;
                var pid = Reflect.ReadAny(power, "Id")?.ToString();
                long currentAmount = Reflect.ReadLong(power, "Amount") ?? Reflect.ReadLong(power, "amount") ?? 0;
                bool isDecaying = pid != null && DecayingDebuffPowerIds.Contains(pid);
                bool skipTick = ReadSkipNextDurationTick(power);
                long incomingStacks = 0;
                if (pid != null && incoming != null && incoming.TryGetValue(pid, out var inc))
                {
                    incomingStacks = inc.Stacks;
                    if (incomingStacks > 0) skipTick = true;
                    processed.Add(pid);
                }
                long totalAmount = currentAmount + incomingStacks;
                long predictedAmount = isDecaying && !skipTick ? totalAmount - 1 : totalAmount;
                if (predictedAmount <= 0) { ModEntry.Log($"side-mult-skip: side={SideTag(side, dealer)} pid={pid} cur={currentAmount} inc={incomingStacks} pred={predictedAmount} skipTick={skipTick}"); continue; }
                var m = InvokeModifyDamageMultiplicative(power, side, dealer, target);
                ModEntry.Log($"side-mult-apply: side={SideTag(side, dealer)} pid={pid} cur={currentAmount} inc={incomingStacks} pred={predictedAmount} skipTick={skipTick} m={m}");
                product *= m;
            }
        }
        else
        {
            ModEntry.Log($"side-mult: side={SideTag(side, dealer)} powers=null");
        }

        if (incoming != null)
        {
            foreach (var kv in incoming)
            {
                if (processed.Contains(kv.Key)) continue;
                var pid = kv.Key;
                long incomingStacks = kv.Value.Stacks;
                bool isDecaying = DecayingDebuffPowerIds.Contains(pid);
                bool skipTick = incomingStacks > 0;
                long predictedAmount = isDecaying && !skipTick ? incomingStacks - 1 : incomingStacks;
                if (predictedAmount <= 0) { ModEntry.Log($"side-mult-skip-incoming: side={SideTag(side, dealer)} pid={pid} inc={incomingStacks} pred={predictedAmount}"); continue; }
                var m = kv.Value.SyntheticMultiplier;
                ModEntry.Log($"side-mult-apply-incoming: side={SideTag(side, dealer)} pid={pid} inc={incomingStacks} pred={predictedAmount} m={m}");
                product *= m;
            }
        }

        if (seen == 0 && (incoming == null || incoming.Count == 0))
            ModEntry.Log($"side-mult: side={SideTag(side, dealer)} powers-empty");
        return product;
    }

    private static IReadOnlyDictionary<string, (long Stacks, decimal SyntheticMultiplier)>? ResolveTargetIncomingDebuffsFromCombat(
        Creature owner, string monsterIdForLog, string stateIdForLog)
    {
        if (owner == null || _moveStateOnPerformField == null) return null;
        var combat = owner.CombatState;
        if (combat == null) return null;

        Dictionary<string, (long Stacks, decimal SyntheticMultiplier)>? map = null;
        foreach (var enemy in combat.Enemies)
        {
            if (enemy == null || enemy.IsDead) continue;
            var enemyModel = enemy.Monster;
            if (enemyModel == null) continue;
            var enemyNextStateId = enemyModel.NextMove.StateId;
            if (string.IsNullOrEmpty(enemyNextStateId) || enemyNextStateId == "UNSET_MOVE") continue;
            var machine = enemyModel.MoveStateMachine;
            if (machine == null) continue;
            if (!machine.States.TryGetValue(enemyNextStateId, out var enemyState) || enemyState is not MoveState enemyMove) continue;
            AccumulateIncomingFromMove(enemyMove, enemyModel.Id.Entry ?? "?", enemyNextStateId, ref map);
        }
        return map;
    }

    private static void AccumulateIncomingFromMove(MoveState move, string monsterIdForLog, string stateIdForLog,
        ref Dictionary<string, (long Stacks, decimal SyntheticMultiplier)>? map)
    {
        if (_moveStateOnPerformField == null) return;
        MethodInfo? moveMethod = null;
        try
        {
            var del = _moveStateOnPerformField.GetValue(move) as Delegate;
            moveMethod = del?.Method;
        }
        catch { moveMethod = null; }
        if (moveMethod == null) return;

        var applies = MoveBodyIntrospector.TryExtractAllPowerApplies(moveMethod, monsterIdForLog, stateIdForLog);
        if (applies.Count == 0) return;

        foreach (var apply in applies)
        {
            if (!TargetIncomingDebuffs.TryGetValue(apply.PowerTypeName, out var info)) continue;
            map ??= new Dictionary<string, (long, decimal)>(StringComparer.OrdinalIgnoreCase);
            if (map.TryGetValue(info.PowerId, out var existing))
                map[info.PowerId] = (existing.Stacks + apply.Amount, info.Multiplier);
            else
                map[info.PowerId] = (apply.Amount, info.Multiplier);
            ModEntry.Log($"incoming-debuff: applier={monsterIdForLog} state={stateIdForLog} pid={info.PowerId} amount={apply.Amount}");
        }
    }

    private static string SideTag(Creature side, Creature? dealer) => ReferenceEquals(side, dealer) ? "dealer" : "target";

    private static bool ReadSkipNextDurationTick(object power)
    {
        try
        {
            var v = Reflect.ReadAny(power, "SkipNextDurationTick");
            return v is bool b && b;
        }
        catch { return false; }
    }

    private static decimal InvokeModifyDamageMultiplicative(object power, object? owner, object? dealer, object? target)
    {
        try
        {
            var m = power.GetType().GetMethod("ModifyDamageMultiplicative",
                BindingFlags.Public | BindingFlags.Instance);
            if (m == null) return 1m;
            var ps = m.GetParameters();
            if (ps.Length != 5) return 1m;
            var callArgs = new object?[ps.Length];
            for (var i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                var pn = ps[i].Name ?? string.Empty;
                if (pt == typeof(decimal)) callArgs[i] = 1m;
                else if (pn.Equals("target", StringComparison.OrdinalIgnoreCase)) callArgs[i] = target;
                else if (pn.Equals("dealer", StringComparison.OrdinalIgnoreCase)) callArgs[i] = dealer;
                else if (pt.Name == "ValueProp") callArgs[i] = ValueProp.Move;
                else if (pt.Name == "CardModel") callArgs[i] = null;
                else if (typeof(Creature).IsAssignableFrom(pt))
                    callArgs[i] = ReferenceEquals(power, owner) ? owner : target;
                else callArgs[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
            }
            var result = m.Invoke(power, callArgs);
            if (result == null) return 1m;
            try { return Convert.ToDecimal(result); } catch { return 1m; }
        }
        catch { return 1m; }
    }
}
