using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace EnemyIntentTracker.Intent;

public sealed class IntentPredictor
{
    private const int DepthBudget = 32;

    public PredictionResult? Predict(Creature monster)
    {
        if (!StateMachineReflection.IsAvailable) return null;
        if (monster == null || !monster.IsMonster) return null;
        try
        {
            var model = monster.Monster!;
            var monId = model.Id.Entry;
            if (monster.IsDead)
                return new PredictionResult(monId, "", false, Array.Empty<PredictedMove>(),
                    new PredictionFailure(PredictionFailureKind.DeadCreature, "creature is dead"));

            var machine = model.MoveStateMachine;
            if (machine == null)
                return new PredictionResult(monId, "", false, Array.Empty<PredictedMove>(),
                    new PredictionFailure(PredictionFailureKind.NoStateMachine, "MoveStateMachine == null"));

            var nextMove = model.NextMove;
            var curId = nextMove.StateId;
            var stunned = curId == MonsterModel.stunnedMoveId;
            if (curId == "UNSET_MOVE")
                return new PredictionResult(monId, curId, stunned, Array.Empty<PredictedMove>(),
                    new PredictionFailure(PredictionFailureKind.EmptyNextMove, "monster has no rolled move yet"));

            var followUpId = nextMove.FollowUpState?.Id ?? nextMove.FollowUpStateId;
            if (string.IsNullOrEmpty(followUpId) || !machine.States.TryGetValue(followUpId, out var start))
                return new PredictionResult(monId, curId, stunned, Array.Empty<PredictedMove>(),
                    new PredictionFailure(PredictionFailureKind.MissingFollowUp, $"no follow-up state for '{curId}'"));

            var raw = new List<PredictedMove>();
            PredictionFailure? failure = null;
            Walk(start, 1f, DepthBudget, machine, monId, raw, new HashSet<string>(), ref failure);

            var aggregated = raw
                .GroupBy(m => m.StateId)
                .Select(g => new PredictedMove(g.Key, g.Sum(m => m.Probability)))
                .OrderByDescending(m => m.Probability)
                .ToList();

            if (aggregated.Count == 0)
                return new PredictionResult(monId, curId, stunned, aggregated,
                    failure ?? new PredictionFailure(PredictionFailureKind.NoConditionMatched, "walk produced no moves"));
            return new PredictionResult(monId, curId, stunned, aggregated, null);
        }
        catch (Exception ex)
        {
            ModEntry.LogWarn($"intent-predict: unexpected exception: {ex.Message}");
            return null;
        }
    }

    private static void Walk(
        MonsterState state, float p, int budget,
        MonsterMoveStateMachine machine, string monId,
        List<PredictedMove> output, HashSet<string> path, ref PredictionFailure? failure)
    {
        if (budget <= 0)
        {
            failure ??= new PredictionFailure(PredictionFailureKind.CycleDetected, $"depth budget exhausted at '{state.Id}'");
            return;
        }
        if (!path.Add(state.Id))
        {
            failure ??= new PredictionFailure(PredictionFailureKind.CycleDetected, $"cycle detected at '{state.Id}'");
            return;
        }
        try
        {
            switch (state)
            {
                case MoveState ms:
                    output.Add(new PredictedMove(ms.Id, p));
                    return;
                case ConditionalBranchState cb:
                {
                    var branches = StateMachineReflection.GetConditionalBranches(cb);
                    if (branches.Count == 0)
                    {
                        failure ??= new PredictionFailure(PredictionFailureKind.NoConditionMatched, $"ConditionalBranchState '{cb.Id}' has no branches");
                        return;
                    }
                    foreach (var (id, lambda) in branches)
                    {
                        bool ok;
                        try { ok = lambda?.Invoke() ?? true; }
                        catch (Exception ex)
                        {
                            ModEntry.LogWarn($"intent-predict: conditional lambda threw on monster='{monId}' state='{cb.Id}' branch='{id}': {ex.Message}");
                            failure ??= new PredictionFailure(PredictionFailureKind.LambdaException, $"{cb.Id}/{id}: {ex.Message}");
                            return;
                        }
                        if (!ok) continue;
                        if (!machine.States.TryGetValue(id, out var child))
                        {
                            failure ??= new PredictionFailure(PredictionFailureKind.MissingFollowUp, $"missing branch target '{id}' on '{cb.Id}'");
                            return;
                        }
                        Walk(child, p, budget - 1, machine, monId, output, path, ref failure);
                        return;
                    }
                    failure ??= new PredictionFailure(PredictionFailureKind.NoConditionMatched, $"no branch matched on '{cb.Id}'");
                    return;
                }
                case RandomBranchState rb:
                {
                    var weights = new List<(RandomBranchState.StateWeight Sw, float W)>();
                    float total = 0f;
                    foreach (var sw in rb.States)
                    {
                        float w = ComputeWeight(sw, machine, monId);
                        if (w > 0f) { weights.Add((sw, w)); total += w; }
                    }
                    if (total <= 0f)
                    {
                        failure ??= new PredictionFailure(PredictionFailureKind.AllWeightsZero, $"all weights zero on '{rb.Id}'");
                        return;
                    }
                    foreach (var (sw, w) in weights)
                    {
                        if (!machine.States.TryGetValue(sw.stateId, out var child))
                        {
                            failure ??= new PredictionFailure(PredictionFailureKind.MissingFollowUp, $"missing weighted target '{sw.stateId}' on '{rb.Id}'");
                            continue;
                        }
                        Walk(child, p * w / total, budget - 1, machine, monId, output, path, ref failure);
                    }
                    return;
                }
                default:
                    failure ??= new PredictionFailure(PredictionFailureKind.UnknownStateType, $"unknown state type {state.GetType().Name} ('{state.Id}')");
                    return;
            }
        }
        finally
        {
            path.Remove(state.Id);
        }
    }

    private static float ComputeWeight(RandomBranchState.StateWeight sw, MonsterMoveStateMachine machine, string monId)
    {
        float gate = 1f;
        if (sw.repeatType == MoveRepeatType.UseOnlyOnce)
        {
            if (machine.States.TryGetValue(sw.stateId, out var item) && machine.StateLog.Contains(item))
                gate = 0f;
        }
        else if (sw.repeatType != MoveRepeatType.CanRepeatForever)
        {
            float n = sw.repeatType == MoveRepeatType.CannotRepeat ? 1f : sw.maxTimes;
            gate = machine.StateLog.Count < n ? 1f : 0f;
            int k = 0;
            while (machine.StateLog.Count >= n && k < n && machine.StateLog.Count - k > 0)
            {
                if (!machine.States.TryGetValue(sw.stateId, out var s)) break;
                if (machine.StateLog[machine.StateLog.Count - 1 - k] != s) { gate = 1f; break; }
                k++;
            }
        }
        if (gate == 0f) return 0f;

        if (sw.cooldown > 0)
        {
            var recent = machine.StateLog.Where(s => s.IsMove).Reverse().Take(sw.cooldown);
            if (recent.Any(m => m.Id == sw.stateId)) return 0f;
        }

        float w;
        try { w = sw.weightLambda?.Invoke() ?? 1f; }
        catch (Exception ex)
        {
            ModEntry.LogWarn($"intent-predict: weightLambda threw on monster='{monId}' state='{sw.stateId}': {ex.Message}");
            return 0f;
        }
        return gate * w;
    }
}
