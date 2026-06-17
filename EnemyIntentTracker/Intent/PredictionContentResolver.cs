using System.Globalization;
using System.Reflection;
using System.Text;
using Sts2Mods.Common;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace EnemyIntentTracker.Intent;

internal readonly struct PredictionTip
{
    public readonly LocString SeedLoc;
    public readonly string Id;
    public readonly string Title;
    public readonly string Body;

    public PredictionTip(LocString seedLoc, string id, string title, string body)
    {
        SeedLoc = seedLoc;
        Id = id;
        Title = title;
        Body = body;
    }
}

internal static class PredictionContentResolver
{
    private const BindingFlags AnyInst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly PropertyInfo? _intentTitleProp =
        typeof(AbstractIntent).GetProperty("IntentTitle", AnyInst);
    private static readonly MethodInfo? _getIntentDescription =
        typeof(AbstractIntent).GetMethod("GetIntentDescription", AnyInst, null,
            new[] { typeof(IEnumerable<Creature>), typeof(Creature) }, null);
    private static readonly FieldInfo? _moveStateOnPerformField =
        typeof(MoveState).GetField("_onPerform", BindingFlags.Instance | BindingFlags.NonPublic);

    public static IReadOnlyList<PredictionTip> Build(
        PredictionResult result, Creature owner, IEnumerable<Creature> targets)
    {
        if (result == null || owner == null) return Array.Empty<PredictionTip>();

        var output = new List<PredictionTip>();
        var machine = owner.Monster?.MoveStateMachine;
        if (machine == null || result.NextMoves == null || result.NextMoves.Count == 0)
            return output;

        var safeTargets = targets ?? Array.Empty<Creature>();
        var showProb = result.NextMoves.Count > 1;
        var branchIndex = 0;

        MoveState? currentTurnMoveState = null;
        if (!string.IsNullOrEmpty(result.CurrentMoveId)
            && machine.States.TryGetValue(result.CurrentMoveId, out var curState)
            && curState is MoveState curMs)
        {
            currentTurnMoveState = curMs;
        }

        foreach (var move in result.NextMoves)
        {
            if (!machine.States.TryGetValue(move.StateId, out var state) || state is not MoveState ms) continue;
            var intents = ms.Intents;
            if (intents == null || intents.Count == 0) continue;

            LocString? firstSeed = null;
            var titleParts = new List<string>();
            var body = new StringBuilder();
            foreach (var intent in intents)
            {
                if (intent == null) continue;
                var monsterIdForLog = result.MonsterId ?? "?";
                var seed = ResolveTitleLoc(intent, monsterIdForLog, move.StateId);
                if (seed == null) continue;
                firstSeed ??= seed;
                titleParts.Add(Reflect.AsText(seed) ?? move.StateId);
                AppendIntentBody(body, intent, ms, safeTargets, owner, monsterIdForLog, move.StateId, currentTurnMoveState);
            }
            if (firstSeed == null || titleParts.Count == 0) continue;

            var probSuffix = showProb ? $" ({move.Probability.ToString("P0", CultureInfo.InvariantCulture)})" : string.Empty;
            var title = "next turn" + probSuffix + ": " + string.Join(" + ", titleParts);
            var id = "DamageTrackerPrediction:" + (result.MonsterId ?? "?") + ":" + move.StateId + ":" + branchIndex;
            output.Add(new PredictionTip(firstSeed, id, title, body.ToString().TrimEnd()));
            branchIndex++;
        }

        return output;
    }

    private static LocString? ResolveTitleLoc(AbstractIntent intent, string monsterId, string stateId)
    {
        try
        {
            return _intentTitleProp?.GetValue(intent) as LocString;
        }
        catch (Exception ex)
        {
            ModEntry.LogWarn($"intent-tip-fail: monster={monsterId} state={stateId} kind=title ex={ex.GetType().Name}:{ex.Message}");
            return null;
        }
    }

    private static void AppendIntentBody(StringBuilder sb, AbstractIntent intent, MoveState moveState,
        IEnumerable<Creature> targets, Creature owner, string monsterId, string stateId,
        MoveState? currentTurnMoveState)
    {
        try
        {
            if (_getIntentDescription != null)
            {
                var loc = _getIntentDescription.Invoke(intent, new object?[] { targets, owner }) as LocString;
                if (loc != null)
                {
                    OverridePredictedDamage(loc, intent, moveState, targets, owner, monsterId, stateId, currentTurnMoveState);
                    var desc = Reflect.AsText(loc);
                    if (!string.IsNullOrEmpty(desc)) sb.AppendLine(desc);
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.LogWarn($"intent-tip-fail: monster={monsterId} state={stateId} kind=description ex={ex.GetType().Name}:{ex.Message}");
        }

        AppendPowerEnrichment(sb, intent, moveState, monsterId, stateId);
        AppendStatusCardEnrichment(sb, intent, moveState, monsterId, stateId);
    }

    private static void OverridePredictedDamage(LocString loc, AbstractIntent intent, MoveState moveState,
        IEnumerable<Creature> targets, Creature owner, string monsterId, string stateId,
        MoveState? currentTurnMoveState)
    {
        if (intent is not AttackIntent atk) return;
        var predicted = NextTurnDamageSimulator.TryPredictDamage(atk, owner, targets, moveState, monsterId, stateId, currentTurnMoveState);
        if (predicted == null)
        {
            var calc = atk.DamageCalc;
            if (calc == null) return;
            var baseDamage = Math.Max(0, (int)calc());
            loc.Add("Damage", baseDamage);
            return;
        }
        loc.Add("Damage", predicted.Value);
    }

    private static void AppendPowerEnrichment(StringBuilder sb, AbstractIntent intent, MoveState moveState,
        string monsterId, string stateId)
    {
        var intentFullName = intent.GetType().FullName;
        bool isBuff = intentFullName != null && intentFullName.EndsWith(".BuffIntent");
        bool isDebuff = intentFullName != null && intentFullName.EndsWith(".DebuffIntent");
        if (!isBuff && !isDebuff) return;
        if (_moveStateOnPerformField == null) return;

        MethodInfo? moveMethod;
        try
        {
            var del = _moveStateOnPerformField.GetValue(moveState) as Delegate;
            moveMethod = del?.Method;
        }
        catch (Exception ex)
        {
            ModEntry.LogWarn($"intent-tip-fail: monster={monsterId} state={stateId} kind=move-method ex={ex.GetType().Name}:{ex.Message}");
            return;
        }
        if (moveMethod == null) return;

        var extracted = MoveBodyIntrospector.TryExtractFirstPowerApply(moveMethod, monsterId, stateId);
        if (extracted == null) return;

        var pretty = PrettifyPowerName(extracted.Value.PowerTypeName);
        if (isBuff) sb.AppendLine($"Power: {pretty} +{extracted.Value.Amount}");
        else sb.AppendLine($"Debuff: {pretty} \u00d7{extracted.Value.Amount}");
    }

    private static void AppendStatusCardEnrichment(StringBuilder sb, AbstractIntent intent, MoveState moveState,
        string monsterId, string stateId)
    {
        if (intent is not StatusIntent statusIntent) return;
        if (_moveStateOnPerformField == null) return;

        MethodInfo? moveMethod;
        try
        {
            var del = _moveStateOnPerformField.GetValue(moveState) as Delegate;
            moveMethod = del?.Method;
        }
        catch (Exception ex)
        {
            ModEntry.LogWarn($"intent-tip-fail: monster={monsterId} state={stateId} kind=move-method ex={ex.GetType().Name}:{ex.Message}");
            return;
        }
        if (moveMethod == null) return;

        var cardName = MoveBodyIntrospector.TryExtractFirstStatusCardName(moveMethod, monsterId, stateId);
        if (cardName == null) return;

        sb.AppendLine($"Status: {cardName} \u00d7{statusIntent.CardCount}");
    }

    private static string PrettifyPowerName(string typeName)
    {
        const string suffix = "Power";
        return typeName.EndsWith(suffix) && typeName.Length > suffix.Length
            ? typeName.Substring(0, typeName.Length - suffix.Length)
            : typeName;
    }
}
