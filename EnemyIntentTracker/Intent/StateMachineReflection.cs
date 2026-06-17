using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace EnemyIntentTracker.Intent;

internal static class StateMachineReflection
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags InstancePublic = BindingFlags.Instance | BindingFlags.Public;

    private static readonly FieldInfo? _currentStateField;
    private static readonly PropertyInfo? _branchStatesProp;
    private static readonly Type? _conditionalBranchType;
    private static readonly FieldInfo? _branchIdField;
    private static readonly FieldInfo? _branchLambdaField;
    private static readonly FieldInfo? _intentOwnerField;
    private static readonly FieldInfo? _intentTargetsField;

    internal static bool IsAvailable { get; }

    static StateMachineReflection()
    {
        try
        {
            _currentStateField = typeof(MonsterMoveStateMachine).GetField("_currentState", Instance);
            _branchStatesProp = typeof(ConditionalBranchState).GetProperty("States", Instance);
            _conditionalBranchType = typeof(ConditionalBranchState).GetNestedType("ConditionalBranch", BindingFlags.NonPublic);
            _branchIdField = _conditionalBranchType?.GetField("id", InstancePublic);
            _branchLambdaField = _conditionalBranchType?.GetField("_conditionalLambda", Instance);
            _intentOwnerField = typeof(NIntent).GetField("_owner", Instance);
            _intentTargetsField = typeof(NIntent).GetField("_targets", Instance);

            var missing = new List<string>();
            if (_currentStateField == null) missing.Add("MonsterMoveStateMachine._currentState");
            if (_branchStatesProp == null) missing.Add("ConditionalBranchState.States");
            if (_conditionalBranchType == null) missing.Add("ConditionalBranchState+ConditionalBranch");
            if (_branchIdField == null) missing.Add("ConditionalBranch.id");
            if (_branchLambdaField == null) missing.Add("ConditionalBranch._conditionalLambda");
            if (_intentOwnerField == null) missing.Add("NIntent._owner");
            if (_intentTargetsField == null) missing.Add("NIntent._targets");

            if (missing.Count > 0)
            {
                ModEntry.LogError($"StateMachineReflection: missing members [{string.Join(", ", missing)}] — predictor disabled.");
                IsAvailable = false;
            }
            else
            {
                IsAvailable = true;
            }
        }
        catch (Exception ex)
        {
            ModEntry.LogError($"StateMachineReflection: init failed: {ex.Message} — predictor disabled.");
            IsAvailable = false;
        }
    }

    internal static MonsterState? GetCurrentState(MonsterMoveStateMachine m)
    {
        if (!IsAvailable || m == null) return null;
        return _currentStateField!.GetValue(m) as MonsterState;
    }

    internal static IReadOnlyList<(string Id, Func<bool>? Lambda)> GetConditionalBranches(ConditionalBranchState s)
    {
        if (!IsAvailable || s == null) return Array.Empty<(string, Func<bool>?)>();
        if (_branchStatesProp!.GetValue(s) is not System.Collections.IEnumerable raw)
            return Array.Empty<(string, Func<bool>?)>();
        var result = new List<(string Id, Func<bool>? Lambda)>();
        foreach (var branch in raw)
        {
            var id = _branchIdField!.GetValue(branch) as string ?? "";
            var lambda = _branchLambdaField!.GetValue(branch) as Func<bool>;
            result.Add((id, lambda));
        }
        return result;
    }

    internal static Creature? GetOwner(NIntent n)
    {
        if (!IsAvailable || n == null) return null;
        return _intentOwnerField!.GetValue(n) as Creature;
    }

    internal static IEnumerable<Creature> GetTargets(NIntent n)
    {
        if (!IsAvailable || n == null) return Array.Empty<Creature>();
        return _intentTargetsField!.GetValue(n) as IEnumerable<Creature> ?? Array.Empty<Creature>();
    }
}
