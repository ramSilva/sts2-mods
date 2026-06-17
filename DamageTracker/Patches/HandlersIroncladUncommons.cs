using System;
using System.Collections.Generic;
using System.Linq;
using DamageTracker.Tracking;
using Sts2Mods.Common;

namespace DamageTracker.Patches;

internal static class IroncladUncommonsPolicy
{
    internal static readonly HashSet<string> DrawTriggeringPowers = new(StringComparer.OrdinalIgnoreCase)
    {
        "POWER.DRUM_OF_BATTLE_POWER",
        "POWER.VICIOUS_POWER",
    };

    internal static readonly HashSet<string> ExhaustTriggeringPowers = new(StringComparer.OrdinalIgnoreCase)
    {
        "POWER.DRUM_OF_BATTLE_POWER",
    };

    internal static readonly HashSet<string> BlockOnExhaustPowers = new(StringComparer.OrdinalIgnoreCase)
    {
        "POWER.FEEL_NO_PAIN_POWER",
    };

    internal static readonly HashSet<string> RetaliationPowers = new(StringComparer.OrdinalIgnoreCase)
    {
        "POWER.FLAME_BARRIER_POWER",
        "POWER.THORNS_POWER",
    };
}

internal static class DrawHandlers
{
    public static readonly Action<object?[]> OnAfterCardDrawn = args =>
    {
        var svc = DamageTrackerService.Instance;
        var source = svc.CurrentSource();
        if (source.HasValue) svc.RecordCardsDrawn(1, source);

        var combatState = Reflect.FindByTypeName(args, "CombatState");
        var playerCreatures = Reflect.ReadAny(combatState, "PlayerCreatures") as System.Collections.IEnumerable;
        var player = playerCreatures?.Cast<object>().FirstOrDefault();
        var playerInstanceId = Reflect.TryReadInstanceId(player) ?? 0L;
        foreach (var power in Reflect.IteratePowers(player))
        {
            var pid = Reflect.ReadAny(power, "Id")?.ToString();
            if (pid == null || !IroncladUncommonsPolicy.DrawTriggeringPowers.Contains(pid)) continue;
            var name = Reflect.AsText(Reflect.ReadAny(power, "Title", "TitleLocString")) ?? pid;
            svc.RecordOnPowerAndCard(playerInstanceId, pid, name, s => svc.RecordCardsDrawn(1, s));
        }
    };
}

internal static class ExhaustHandlers
{
    public static readonly Action<object?[]> OnAfterCardExhausted = args =>
    {
        var svc = DamageTrackerService.Instance;
        var source = svc.CurrentSource();
        if (source.HasValue) svc.RecordCardsExhausted(1, source);

        var combatState = Reflect.FindByTypeName(args, "CombatState");
        var playerCreatures = Reflect.ReadAny(combatState, "PlayerCreatures") as System.Collections.IEnumerable;
        var player = playerCreatures?.Cast<object>().FirstOrDefault();
        var playerInstanceId = Reflect.TryReadInstanceId(player) ?? 0L;
        foreach (var power in Reflect.IteratePowers(player))
        {
            var pid = Reflect.ReadAny(power, "Id")?.ToString();
            if (pid == null) continue;
            var name = Reflect.AsText(Reflect.ReadAny(power, "Title", "TitleLocString")) ?? pid;

            if (IroncladUncommonsPolicy.ExhaustTriggeringPowers.Contains(pid))
            {
                svc.RecordOnPowerAndCard(playerInstanceId, pid, name, s => svc.RecordCardsExhausted(1, s));
            }

            if (IroncladUncommonsPolicy.BlockOnExhaustPowers.Contains(pid))
            {
                var amt = Reflect.ReadLong(power, "Amount") ?? 0;
                if (amt > 0)
                    svc.RecordOnPowerAndCard(playerInstanceId, pid, name, s => svc.RecordBlock(amt, s));
            }
        }
    };
}

internal static class EnergyHandlers
{
    public static readonly Action<object?[]> OnModifyEnergyGain = args =>
    {
        var delta = DamageReflect.FindFirstNumeric(args);
        if (delta <= 0) return;
        var svc = DamageTrackerService.Instance;
        var source = svc.CurrentSource();
        if (source.HasValue) svc.RecordEnergyGained(delta, source);
    };
}

internal static class SpawnHandlers
{
    public static readonly Action<object?[]> OnAfterCardChangedPiles = args =>
    {
        var card = Reflect.FindByTypeName(args, "CardModel");
        if (card == null) return;

        var pile = Reflect.ReadAny(card, "Pile");
        var pileTypeName = Reflect.ReadAny(pile, "Type")?.ToString();

        string? oldPileName = null;
        if (args != null)
        {
            foreach (var a in args)
            {
                if (a == null) continue;
                if (a.GetType().Name == "PileType") { oldPileName = a.ToString(); break; }
            }
        }

        if (pileTypeName != "Hand" || oldPileName == "Hand") return;

        var instId = Reflect.TryReadInstanceId(card);
        if (!instId.HasValue) return;

        var svc = DamageTrackerService.Instance;
        var active = svc.CurrentSource();
        if (active.HasValue && active.Value.Kind == SourceKind.Card
            && string.Equals(active.Value.Id, "CARD.INFERNAL_BLADE", StringComparison.OrdinalIgnoreCase))
        {
            svc.AddSecondaryCredit(instId.Value, active.Value);
            return;
        }

        var isClone = Reflect.ReadBool(card, "IsClone") ?? false;
        if (isClone)
        {
            var combatState = Reflect.FindByTypeName(args!, "CombatState");
            var playerCreatures = Reflect.ReadAny(combatState, "PlayerCreatures") as System.Collections.IEnumerable;
            var player = playerCreatures?.Cast<object>().FirstOrDefault();
            bool hasJuggling = false;
            foreach (var p in Reflect.IteratePowers(player))
            {
                var pid = Reflect.ReadAny(p, "Id")?.ToString();
                if (string.Equals(pid, "POWER.JUGGLING_POWER", StringComparison.OrdinalIgnoreCase))
                {
                    var amt = Reflect.ReadLong(p, "Amount") ?? 0;
                    if (amt > 0) { hasJuggling = true; break; }
                }
            }
            if (hasJuggling)
            {
                svc.AddSecondaryCredit(instId.Value, SourceKey.Card("CARD.JUGGLING", "Juggling"));
                svc.AddSecondaryCredit(instId.Value, SourceKey.Power("POWER.JUGGLING_POWER", "Juggling"));
                return;
            }
        }

        svc.AttachSpawnerToInstance(instId.Value);
    };
}
