using MegaCrit.Sts2.Core.Entities.Creatures;

namespace EnemyIntentTracker.Intent;

internal readonly struct IntentHoverContext
{
    public readonly PredictionResult Prediction;
    public readonly Creature Owner;
    public readonly IEnumerable<Creature> Targets;

    public IntentHoverContext(PredictionResult prediction, Creature owner, IEnumerable<Creature> targets)
    {
        Prediction = prediction;
        Owner = owner;
        Targets = targets;
    }
}

internal static class IntentOverlayBridge
{
    [ThreadStatic] internal static IntentHoverContext? Pending;
}
