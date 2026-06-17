namespace EnemyIntentTracker.Intent;

public sealed record PredictionResult(
    string MonsterId,
    string CurrentMoveId,
    bool IsStunnedThisTurn,
    IReadOnlyList<PredictedMove> NextMoves,
    PredictionFailure? Failure);

public sealed record PredictedMove(string StateId, float Probability);

public sealed record PredictionFailure(PredictionFailureKind Kind, string Detail);

public enum PredictionFailureKind
{
    NoStateMachine,
    EmptyNextMove,
    DeadCreature,
    AllWeightsZero,
    NoConditionMatched,
    MissingFollowUp,
    LambdaException,
    UnknownStateType,
    ReflectionUnavailable,
    CycleDetected,
}
