using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DamageTracker.Tracking;
using Sts2Mods.Common;

namespace DamageTracker.Patches;

internal static class LedgerPolicy
{
    internal static readonly HashSet<string> LedgerEligiblePowers = new(StringComparer.OrdinalIgnoreCase)
    {
        "POWER.STRENGTH_POWER", "POWER.DEXTERITY_POWER",
    };

    internal static readonly HashSet<string> MultiplicativeEnemyDebuffs = new(StringComparer.OrdinalIgnoreCase)
    {
        "POWER.VULNERABLE_POWER",
        "POWER.WEAK_POWER",
    };

    internal static readonly HashSet<string> MultiplicativeReceiverBuffs = new(StringComparer.OrdinalIgnoreCase)
    {
        "POWER.COLOSSUS_POWER",
    };

    internal static readonly HashSet<string> SuppressedTrackerTipPowers = new(StringComparer.OrdinalIgnoreCase) { "POWER.STRENGTH_POWER" };
    internal static readonly HashSet<string> EnergyOnlyAutoPlayOuterCards = new(StringComparer.OrdinalIgnoreCase) { "CARD.CASCADE" };

    internal static readonly HashSet<string> TurnScopedSourceCards = new(StringComparer.OrdinalIgnoreCase)
    {
        "CARD.SETUP_STRIKE",
    };

    internal static BuffEntryKind? KindForPowerId(string powerId)
    {
        if (string.IsNullOrEmpty(powerId)) return null;
        if (string.Equals(powerId, "POWER.STRENGTH_POWER", StringComparison.OrdinalIgnoreCase))
            return BuffEntryKind.AdditiveDamage;
        if (string.Equals(powerId, "POWER.DEXTERITY_POWER", StringComparison.OrdinalIgnoreCase))
            return BuffEntryKind.AdditiveBlock;
        return null;
    }

    internal static bool IsTurnScoped(string powerId, SourceKey source)
    {
        return TurnScopedSourceCards.Contains(source.Id);
    }
}

/// <summary>
/// Handlers reflect over their incoming objects rather than assuming a shape
/// so a minor signature drift between game patches won't crash the mod. Most
/// hooks on v0.103.3 carry the originating CardModel directly (cardSource);
/// where they don't, we fall back to the active play-scope stack pushed by
/// BeforeCardPlayed / AfterCardPlayed.
/// </summary>
internal static class LifecycleHandlers
{
    private static int _lastSeenAct = -1;

    public static readonly Action<object?[]> OnBeforeCombatStart = _ =>
    {
        DamageTrackerService.Instance.ResetCombat();
        DamageHandlers.ClearPreMultCache();
        CardUpgradeTrigger.ClearForNewCardPlay();
        AttributionHandlers.OtpPendingConsumption = false;
        AttributionHandlers.OtpDoubledInstances.Clear();
        AttributionHandlers.OtpReadyInstances.Clear();
    };

    public static readonly Action<object?[]> OnAfterCombatEnd = _ =>
    {
        AttributionHandlers.OtpPendingConsumption = false;
        AttributionHandlers.OtpDoubledInstances.Clear();
        AttributionHandlers.OtpReadyInstances.Clear();
    };

    public static readonly Action<object?[]> OnAfterRoomEntered = args =>
    {
        var runState = Reflect.FindByTypeName(args, "RunState");
        var act = Reflect.ReadInt(runState, "act")
                  ?? Reflect.ReadInt(runState, "actNumber")
                  ?? Reflect.ReadInt(runState, "currentAct");
        if (act.HasValue && act.Value != _lastSeenAct)
        {
            if (_lastSeenAct >= 0) DamageTrackerService.Instance.ResetAct();
            _lastSeenAct = act.Value;
        }
    };
}

internal static class AttributionHandlers
{
    [ThreadStatic] private static IDisposable? _currentScope;
    [ThreadStatic] private static long? _currentPlayInstanceId;
    private static readonly HashSet<string> _loggedCardIds = new(StringComparer.OrdinalIgnoreCase);
    internal static bool OtpPendingConsumption;
    internal static readonly HashSet<long> OtpDoubledInstances = new();
    internal static readonly HashSet<long> OtpReadyInstances = new();

    public static readonly Action<object?[]> OnBeforeCardPlayed = args =>
    {
        CardUpgradeTrigger.ClearForNewCardPlay();
        var cardPlay = Reflect.FindByTypeName(args, "CardPlay");
        var cardObj = cardPlay == null ? null
            : Reflect.ReadAny(cardPlay, "card", "cardModel", "Card", "CardModel");
        var key = DamageReflect.ToCardKey(cardObj);
        _currentPlayInstanceId = cardObj == null ? null : Reflect.TryReadInstanceId(cardObj);
        if (key.HasValue)
        {
            _currentScope = DamageTrackerService.Instance.PushSource(key.Value);
            if (_loggedCardIds.Add(key.Value.Id))
                ModEntry.Log($"card-play: id={key.Value.Id}");
        }

        if (key.HasValue && cardObj != null
            && string.Equals(key.Value.Id, "CARD.GIANT_ROCK", StringComparison.OrdinalIgnoreCase))
        {
            var instId = Reflect.TryReadInstanceId(cardObj);
            if (instId.HasValue)
                DamageTrackerService.Instance.AddSecondaryCredit(instId.Value, SourceKey.Card("CARD.PRIMAL_FORCE", "Primal Force"));
        }

        if (OtpPendingConsumption && key.HasValue && cardObj != null && IsAttackCard(cardObj)
            && !string.Equals(key.Value.Id, "CARD.ONE_TWO_PUNCH", StringComparison.OrdinalIgnoreCase))
        {
            var instId = Reflect.TryReadInstanceId(cardObj);
            if (instId.HasValue)
            {
                var svc = DamageTrackerService.Instance;
                var otpCard = SourceKey.Card("CARD.ONE_TWO_PUNCH", "One-Two Punch");
                var otpPower = SourceKey.Power("POWER.ONE_TWO_PUNCH_POWER", "One-Two Punch");
                OtpDoubledInstances.Add(instId.Value);

                var ec = Reflect.ReadAny(cardObj, "EnergyCost");
                int? canonical = ec == null ? null : Reflect.ReadInt(ec, "Canonical");
                if (canonical.HasValue && canonical.Value > 0)
                {
                    svc.RecordCheatedEnergy((long)canonical.Value, otpCard);
                    svc.RecordCheatedEnergy((long)canonical.Value, otpPower);
                }

                ModEntry.Log($"otp-probe: consumed flag instId={instId.Value} card={key.Value.Id} canonical={(canonical?.ToString() ?? "null")}");
            }
            OtpPendingConsumption = false;
        }

        if (key.HasValue && cardObj != null && cardPlay != null)
        {
            var ec = Reflect.ReadAny(cardObj, "EnergyCost");
            if (ec != null)
            {
                int? canonical = Reflect.ReadInt(ec, "Canonical");
                int? energySpent = DamageReflect.TryReadEnergySpent(cardPlay);
                if (canonical.HasValue && energySpent.HasValue)
                {
                    int savings = Math.Max(0, canonical.Value - energySpent.Value);
                    if (savings > 0)
                    {
                        var svc = DamageTrackerService.Instance;
                        bool originatorCredited = false;

                        if (IsAttackCard(cardObj))
                        {
                            var player = TryFindPlayer(args, cardObj);
                            var playerInstId = Reflect.TryReadInstanceId(player) ?? 0L;
                            var hasFap = playerInstId != 0 && svc.TakeFapConsumed(playerInstId) && svc.TryGetPowerOriginCard(playerInstId, "POWER.FREE_ATTACK_POWER", out _);
                            if (hasFap)
                            {
                                svc.RecordOnPowerAndCard(playerInstId, "POWER.FREE_ATTACK_POWER", "Free Attack", s => svc.RecordCheatedEnergy(savings, s));
                                originatorCredited = true;
                            }
                        }

                        var instId = Reflect.TryReadInstanceId(cardObj);
                        if (instId.HasValue)
                        {
                            foreach (var sec in svc.GetSecondaryCredits(instId.Value))
                            {
                                svc.RecordCheatedEnergy(savings, sec);
                                originatorCredited = true;
                            }
                        }

                        if (!originatorCredited)
                            svc.RecordCheatedEnergy(savings, key.Value);
                    }
                }
            }
        }
    };

    private static object? TryFindPlayer(object?[] args, object? cardObj)
    {
        var owner = Reflect.ReadAny(cardObj, "Owner");
        var creature = Reflect.ReadAny(owner, "Creature");
        if (creature != null) return creature;
        var combatState = Reflect.FindByTypeName(args, "CombatState");
        var playerCreatures = Reflect.ReadAny(combatState, "PlayerCreatures") as System.Collections.IEnumerable;
        return playerCreatures?.Cast<object>().FirstOrDefault();
    }

    private static bool IsAttackCard(object card)
    {
        var typeObj = Reflect.ReadAny(card, "CardType", "Type", "type");
        var s = typeObj?.ToString();
        return s != null && s.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static readonly Action<object?[]> OnAfterCardPlayedLate = _ =>
    {
        var svc = DamageTrackerService.Instance;
        var token = svc.CurrentCardPlayToken;
        if (_currentPlayInstanceId.HasValue)
        {
            var instId = _currentPlayInstanceId.Value;
            svc.ClearSecondaryCredits(instId);
            if (OtpDoubledInstances.Remove(instId))
                OtpReadyInstances.Add(instId);
            else
                OtpReadyInstances.Remove(instId);
        }
        _currentPlayInstanceId = null;
        _currentScope?.Dispose();
        _currentScope = null;
        if (token > 0) svc.SettleLedgerForCardPlay(token);
    };
}

internal static class AutoPlayHandlers
{
    public static readonly Action<object?[]> OnBeforeCardAutoPlayed = args =>
    {
        try
        {
            var probeInner = Reflect.FindByTypeName(args, "CardModel");
            var probeInnerId = Reflect.ReadAny(probeInner, "Id")?.ToString() ?? "<none>";
            var probeOuterId = DamageTrackerService.Instance.CurrentSource()?.Id ?? "<none>";
            var probeAutoPlayType = args.FirstOrDefault(a => a != null && a.GetType().Name == "AutoPlayType")?.ToString() ?? "<none>";
            var probeCombatState = Reflect.FindByTypeName(args, "CombatState");
            var probePlayerCreatures = Reflect.ReadAny(probeCombatState, "PlayerCreatures") as System.Collections.IEnumerable;
            var probePlayer = probePlayerCreatures?.Cast<object>().FirstOrDefault();
            var probeWatch = new List<string>();
            foreach (var pp in Reflect.IteratePowers(probePlayer))
            {
                var ppid = Reflect.ReadAny(pp, "Id")?.ToString();
                if (string.IsNullOrEmpty(ppid)) continue;
                if (ppid!.IndexOf("HELLRAISER", StringComparison.OrdinalIgnoreCase) < 0
                    && ppid.IndexOf("ONE_TWO", StringComparison.OrdinalIgnoreCase) < 0
                    && ppid.IndexOf("ONETWO", StringComparison.OrdinalIgnoreCase) < 0
                    && ppid.IndexOf("STAMPEDE", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var pamt = Reflect.ReadLong(pp, "Amount") ?? Reflect.ReadLong(pp, "amount") ?? 0;
                probeWatch.Add($"{ppid}:{pamt}");
            }
            ModEntry.Log($"autoplay-probe: inner={probeInnerId} outer={probeOuterId} autoPlayType={probeAutoPlayType} watchlist=[{string.Join(",", probeWatch)}]");
        }
        catch (Exception ex) { ModEntry.LogWarn($"autoplay-probe failed: {ex.Message}"); }

        var innerCard = Reflect.FindByTypeName(args, "CardModel");
        if (innerCard == null) return;
        var svc = DamageTrackerService.Instance;

        var combatState = Reflect.FindByTypeName(args, "CombatState");
        var playerCreatures = Reflect.ReadAny(combatState, "PlayerCreatures") as System.Collections.IEnumerable;
        var player = playerCreatures?.Cast<object>().FirstOrDefault();
        if (player == null) return;

        var pile = Reflect.ReadAny(innerCard, "Pile");
        var pileType = Reflect.ReadAny(pile, "Type")?.ToString();
        var autoPlayType = args.FirstOrDefault(a => a != null && a.GetType().Name == "AutoPlayType")?.ToString();

        bool hasStampede = false;
        bool hasHellraiser = false;
        foreach (var p in Reflect.IteratePowers(player))
        {
            var pid = Reflect.ReadAny(p, "Id")?.ToString();
            var amt = Reflect.ReadLong(p, "Amount") ?? 0;
            if (amt <= 0) continue;
            if (string.Equals(pid, "POWER.STAMPEDE_POWER", StringComparison.OrdinalIgnoreCase))
                hasStampede = true;
            else if (string.Equals(pid, "POWER.HELLRAISER_POWER", StringComparison.OrdinalIgnoreCase))
                hasHellraiser = true;
        }

        if (autoPlayType != "Default") return;

        var instId = Reflect.TryReadInstanceId(innerCard);
        if (!instId.HasValue) return;

        if (hasStampede && pileType == "Hand")
        {
            svc.AddSecondaryCredit(instId.Value, SourceKey.Card("CARD.STAMPEDE", "Stampede"));
            svc.AddSecondaryCredit(instId.Value, SourceKey.Power("POWER.STAMPEDE_POWER", "Stampede"));
            return;
        }

        if (hasHellraiser)
        {
            svc.AddSecondaryCredit(instId.Value, SourceKey.Card("CARD.HELLRAISER", "Hellraiser"));
            svc.AddSecondaryCredit(instId.Value, SourceKey.Power("POWER.HELLRAISER_POWER", "Hellraiser"));
            return;
        }

        var outerSource = svc.CurrentSource();
        if (outerSource.HasValue && outerSource.Value.Kind == SourceKind.Card)
        {
            if (LedgerPolicy.EnergyOnlyAutoPlayOuterCards.Contains(outerSource.Value.Id))
            {
                var ec = Reflect.ReadAny(innerCard, "EnergyCost");
                int? canonical = ec == null ? null : Reflect.ReadInt(ec, "Canonical");
                if (canonical.HasValue && canonical.Value > 0)
                    svc.RecordEnergyGained((long)canonical.Value, outerSource.Value);
            }
            else
            {
                svc.AddSecondaryCredit(instId.Value, outerSource.Value);
            }
        }
    };
}

internal static class TurnLifecycleHandlers
{
    public static readonly Action<object?[]> OnTurnEnd = _ =>
    {
        var removed = DamageTrackerService.Instance.ConsumeTurnScopedBuffs();
        if (removed > 0) ModEntry.Log($"turn end: cleared {removed} turn-scoped ledger entries");
        AttributionHandlers.OtpPendingConsumption = false;
        AttributionHandlers.OtpDoubledInstances.Clear();
        AttributionHandlers.OtpReadyInstances.Clear();
    };

    public static readonly Action<object?[]> OnSideTurnStart = args =>
    {
        var side = args.FirstOrDefault(a => a?.GetType().Name == "CombatSide");
        if (side?.ToString() != "Player") return;

        CardUpgradeTrigger.PendingTurnStartUpgradeSource = null;
        AttributionHandlers.OtpPendingConsumption = false;
        AttributionHandlers.OtpDoubledInstances.Clear();
        AttributionHandlers.OtpReadyInstances.Clear();

        var svc = DamageTrackerService.Instance;
        var combatState = Reflect.FindByTypeName(args, "CombatState");
        var playerCreatures = Reflect.ReadAny(combatState, "PlayerCreatures") as System.Collections.IEnumerable;
        var player = playerCreatures?.Cast<object>().FirstOrDefault();
        if (player == null) { svc.ClearPlayerBlockSupply(); return; }

        var playerInstanceId = Reflect.TryReadInstanceId(player) ?? 0L;
        bool barricadeActive = false;
        bool aggressionActive = false;
        string? aggressionPid = null;
        var observed = new List<string>();
        foreach (var p in Reflect.IteratePowers(player))
        {
            var pid = Reflect.ReadAny(p, "Id")?.ToString();
            var amt = Reflect.ReadLong(p, "Amount") ?? Reflect.ReadLong(p, "amount") ?? 0;
            observed.Add($"{pid}:{amt}");
            if (!barricadeActive && string.Equals(pid, "POWER.BARRICADE_POWER", StringComparison.OrdinalIgnoreCase))
            {
                if (amt > 0) barricadeActive = true;
            }
            if (pid?.IndexOf("AGGRESSION", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (amt > 0) { aggressionActive = true; aggressionPid = pid; }
            }
            if (pid?.IndexOf("UNMOVABLE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ModEntry.Log($"unmovable-probe: detected player power pid={pid} amount={amt}");
            }
        }
        ModEntry.Log($"turn-start-probe: side=Player powers=[{string.Join(",", observed)}]");

        if (barricadeActive)
        {
            var block = Reflect.ReadLong(player, "BlockAmount")
                        ?? Reflect.ReadLong(player, "Block")
                        ?? Reflect.ReadLong(player, "block")
                        ?? Reflect.ReadLong(player, "blockAmount")
                        ?? 0;
            if (block > 0)
            {
                svc.RecordOnPowerAndCard(playerInstanceId, "POWER.BARRICADE_POWER", "Barricade",
                    s => svc.RecordBlockPreserved(block, s));
            }
        }

        if (aggressionActive && aggressionPid != null)
        {
            SourceKey aggressionCard = svc.TryGetPowerOriginCard(playerInstanceId, aggressionPid, out var found)
                ? found
                : SourceKey.Card("CARD.AGGRESSION", "Aggression");
            CardUpgradeTrigger.PendingTurnStartUpgradeSource = aggressionCard;
        }

        ModEntry.Log($"turn-start-probe: pendingTurnStartUpgradeSource={CardUpgradeTrigger.PendingTurnStartUpgradeSource?.Id ?? "null"}");

        svc.ClearPlayerBlockSupply();
    };
}

internal static class DamageHandlers
{
    private static readonly object _preMultLock = new();
    private static readonly Dictionary<object, decimal> _preMultDamage = new();

    public static void ClearPreMultCache()
    {
        lock (_preMultLock) _preMultDamage.Clear();
    }

    public static void OnBeforeDamageReceived(object?[] args)
    {
        try
        {
            var target = Reflect.FindByTypeName(args, "Creature");
            if (target == null) return;
            var amount = args.OfType<decimal>().FirstOrDefault();
            lock (_preMultLock) _preMultDamage[target] = amount;
        }
        catch (Exception ex)
        {
            ModEntry.LogWarn($"OnBeforeDamageReceived failed: {ex.Message}");
        }
    }

    public static readonly Action<object?[]> OnAfterDamageGiven = args =>
    {
        var dr = Reflect.FindByTypeName(args, "DamageResult");

        if (ReceiverIsPlayer(dr))
        {
            var blockedObj = Reflect.ReadAny(dr, "BlockedDamage");
            if (blockedObj is IConvertible bc)
            {
                var blocked = Convert.ToInt64(bc);
                if (blocked > 0)
                {
                    var credited = DamageTrackerService.Instance.ConsumePlayerBlockAndAttribute(blocked);
                    ModEntry.Log($"absorbed-block: hit blocked={blocked} credited={credited}");
                }
            }
        }

        var cardSource = Reflect.FindByTypeName(args, "CardModel");
        ModEntry.Log($"mangle-probe: afterDamageGiven cardSourceId={Reflect.ReadAny(cardSource, "Id")} cardSourceInstId={(Reflect.TryReadInstanceId(cardSource)?.ToString() ?? "null")} secCreds={(Reflect.TryReadInstanceId(cardSource) is long sid ? DamageTrackerService.Instance.GetSecondaryCredits(sid).Count : 0)} currentScope={DamageTrackerService.Instance.CurrentSource()?.Id ?? "<none>"}");
        var key = DamageReflect.ToCardKey(cardSource) ?? DamageTrackerService.Instance.CurrentSource();

        var amount = (long?)Reflect.ReadLong(dr, "TotalDamage")
                     ?? Reflect.ReadLong(dr, "UnblockedDamage") ?? 0;
        if (amount <= 0) return;

        var target = Reflect.ReadAny(dr, "Receiver", "Target");
        var dealer = Reflect.FindOtherCreature(args, target);
        var props = Reflect.FindByTypeName(args, "ValueProp");

        decimal preFloor = 0m;
        if (target != null)
        {
            lock (_preMultLock)
            {
                if (_preMultDamage.TryGetValue(target, out var v))
                {
                    preFloor = v;
                    _preMultDamage.Remove(target);
                }
            }
        }

        if (ReceiverIsPlayer(dr))
        {
            var svc = DamageTrackerService.Instance;
            if (key.HasValue) svc.RecordSelfDamage(amount, key);
            CreditMultiplicativeDebuffs(
                Reflect.ReadAny(dealer, "Powers") as System.Collections.IEnumerable,
                dealer, target, props, cardSource, amount, preFloor);
            CreditEnemyStrength(dealer, target, props, cardSource, amount);
            CreditMultiplicativeReceiverBuffs(target, dealer, props, cardSource, amount, preFloor);
            return;
        }

        if (!key.HasValue)
        {
            if (dealer == null) return;
            var dealerInstanceId = Reflect.TryReadInstanceId(dealer) ?? 0L;
            if (dealerInstanceId == 0L) return;
            foreach (var power in Reflect.IteratePowers(dealer))
            {
                var pid = Reflect.ReadAny(power, "Id")?.ToString();
                if (pid == null) continue;
                if (!IroncladUncommonsPolicy.RetaliationPowers.Contains(pid)) continue;
                var name = Reflect.AsText(Reflect.ReadAny(power, "Title", "TitleLocString")) ?? pid;
                var amt = amount;
                DamageTrackerService.Instance.RecordOnPowerAndCard(dealerInstanceId, pid, name, s => DamageTrackerService.Instance.RecordDamage(amt, s));
                return;
            }
            return;
        }

        DamageTrackerService.Instance.RecordDamage(amount, key);
        var damageSource = key.Value;

        var multiplier = DamageMath.MultiplierForHit(dealer, target, props, cardSource);

        foreach (var entry in DamageTrackerService.Instance.SnapshotLedger())
        {
            if (entry.Kind != BuffEntryKind.AdditiveDamage) continue;
            if (entry.Magnitude <= 0) continue;
            if (entry.Source.Equals(damageSource) && !entry.Settled) continue;
            long realized;
            if (preFloor > 0m)
            {
                var withoutPre = preFloor - (decimal)entry.Magnitude * multiplier;
                var withoutFloored = withoutPre > 0m ? (long)Math.Floor(withoutPre) : 0L;
                realized = amount - withoutFloored;
            }
            else
            {
                realized = (long)Math.Floor((decimal)entry.Magnitude * multiplier);
            }
            if (realized <= 0) continue;
            if (string.Equals(entry.PowerId, "POWER.STRENGTH_POWER", StringComparison.OrdinalIgnoreCase))
                DamageTrackerService.Instance.RecordStrengthDamage(realized, entry.Source);
            else
                DamageTrackerService.Instance.RecordDerivedDamage(entry.Source, entry.PowerId, realized);
        }

        if (target != null)
        {
            CreditMultiplicativeDebuffs(
                Reflect.ReadAny(target, "Powers") as System.Collections.IEnumerable,
                dealer, target, props, cardSource, amount, preFloor);
        }

        FanOutSecondaryCredits(cardSource, sec => DamageTrackerService.Instance.RecordDamage(amount, sec));

        var fanInstId = Reflect.TryReadInstanceId(cardSource);
        if (fanInstId.HasValue && AttributionHandlers.OtpReadyInstances.Contains(fanInstId.Value))
        {
            var svc2 = DamageTrackerService.Instance;
            svc2.RecordDamage(amount, SourceKey.Card("CARD.ONE_TWO_PUNCH", "One-Two Punch"));
            svc2.RecordDamage(amount, SourceKey.Power("POWER.ONE_TWO_PUNCH_POWER", "One-Two Punch"));
        }
    };

    private static void CreditEnemyStrength(object? dealer, object? target, object? props, object? cardSource, long amount)
    {
        if (dealer == null || amount <= 0) return;
        var dealerInstanceId = Reflect.TryReadInstanceId(dealer) ?? 0L;
        foreach (var power in Reflect.IteratePowers(dealer))
        {
            var pid = Reflect.ReadAny(power, "Id")?.ToString();
            if (!string.Equals(pid, "POWER.STRENGTH_POWER", StringComparison.OrdinalIgnoreCase)) continue;

            var strength = Reflect.ReadLong(power, "Amount") ?? Reflect.ReadLong(power, "amount")
                           ?? Reflect.ReadLong(power, "Stacks") ?? Reflect.ReadLong(power, "stacks") ?? 0;
            var baseDamage = Reflect.ReadLong(props, "Amount") ?? Reflect.ReadLong(props, "BaseValue")
                             ?? Reflect.ReadLong(props, "Value") ?? Reflect.ReadLong(props, "BaseDamage") ?? 0;
            ModEntry.Log($"mangle-probe: creditEnemyStrength dealer={dealerInstanceId} strength={strength} baseDamage={baseDamage}");
            if (baseDamage <= 0) continue;
            if (baseDamage + strength <= 0) continue;

            var withoutStrength = (long)Math.Floor((decimal)amount * baseDamage / (baseDamage + strength));
            var bonus = amount - withoutStrength;
            ModEntry.Log($"mangle-probe: creditEnemyStrength amount={amount} withoutStrength={withoutStrength} bonus={bonus}");
            if (bonus == 0) continue;
            var magnitude = Math.Abs(bonus);
            var powerKey = SourceKey.Power("POWER.ENEMY_STRENGTH", "Enemy Strength");
            DamageTrackerService.Instance.RecordEnemyStrengthDamage(magnitude, powerKey);
            bool originFound = false;
            SourceKey originCard = default;
            if (dealerInstanceId != 0L)
                originFound = DamageTrackerService.Instance.TryGetPowerOriginCard(dealerInstanceId, "POWER.STRENGTH_POWER", out originCard);
            ModEntry.Log($"mangle-probe: creditEnemyStrength originLookup={originFound} originCardId={(originFound ? originCard.Id : "<none>")}");
            if (originFound)
            {
                DamageTrackerService.Instance.RecordEnemyStrengthDamage(magnitude, originCard);
            }
        }
    }

    private static void CreditMultiplicativeReceiverBuffs(object? receiver, object? dealer, object? props, object? cardSource, long amount, decimal preFloor)
    {
        if (receiver == null || amount <= 0) return;
        var svc = DamageTrackerService.Instance;
        foreach (var power in Reflect.IteratePowers(receiver))
        {
            var pid = Reflect.ReadAny(power, "Id")?.ToString();
            if (string.IsNullOrEmpty(pid)) continue;
            if (!LedgerPolicy.MultiplicativeReceiverBuffs.Contains(pid!)) continue;

            var singleMult = DamageMath.SinglePowerMultiplier(power, dealer, receiver, props, cardSource);
            if (singleMult >= 1m || singleMult < 0m) continue;
            if (preFloor <= 0m) continue;
            var saved = (long)preFloor - amount;
            if (saved <= 0) continue;

            var name = Reflect.AsText(Reflect.ReadAny(power, "Title", "TitleLocString")) ?? pid!;
            var receiverInstanceId = Reflect.TryReadInstanceId(receiver) ?? 0L;
            svc.RecordOnPowerAndCard(receiverInstanceId, pid!, name, s => svc.RecordDamageReduced(saved, s));
        }
    }

    private static void FanOutSecondaryCredits(object? cardSource, Action<SourceKey> action)
    {
        if (cardSource == null) return;
        var instId = Reflect.TryReadInstanceId(cardSource);
        if (!instId.HasValue) return;
        foreach (var sec in DamageTrackerService.Instance.GetSecondaryCredits(instId.Value))
            action(sec);
    }

    private static void CreditMultiplicativeDebuffs(
        System.Collections.IEnumerable? powers,
        object? dealer, object? target, object? props, object? cardSource,
        long amount, decimal preFloor)
    {
        if (powers == null || amount <= 0) return;
        foreach (var power in powers)
        {
            if (power == null) continue;
            var pid = Reflect.ReadAny(power, "Id")?.ToString();
            if (string.IsNullOrEmpty(pid)) continue;
            if (!LedgerPolicy.MultiplicativeEnemyDebuffs.Contains(pid!)) continue;

            var singleMult = DamageMath.SinglePowerMultiplier(power, dealer, target, props, cardSource);
            if (singleMult <= 0m || singleMult == 1m) continue;

            long bonus;
            if (preFloor > 0m)
            {
                var without = preFloor / singleMult;
                var withoutFloored = (long)Math.Floor(without);
                bonus = singleMult > 1m ? (amount - withoutFloored) : (withoutFloored - amount);
            }
            else
            {
                if (singleMult > 1m)
                    bonus = amount - (long)Math.Floor((decimal)amount / singleMult);
                else
                    bonus = (long)Math.Ceiling((decimal)amount / singleMult) - amount;
            }

            ModEntry.Log($"mult-debuff: pid={pid} mult={singleMult} pre={preFloor} realized={amount} bonus={bonus}");

            if (bonus <= 0) continue;
            DamageTrackerService.Instance.RecordGlobalDerivedDamage(pid!, bonus);

            var ownerInstId = Reflect.TryReadInstanceId(Reflect.ReadAny(power, "Owner")) ?? 0L;
            if (ownerInstId != 0L)
                DamageTrackerService.Instance.RecordGlobalDerivedDamageInstance(ownerInstId, pid!, bonus);
        }
    }

    public static readonly Action<object?[]> OnAfterModifyingBlockAmount = args =>
    {
        var cardSource = Reflect.FindByTypeName(args, "CardModel");
        ModEntry.Log($"unmovable-probe: cardSource={Reflect.ReadAny(cardSource, "Id")} amount={DamageReflect.FindFirstNumeric(args)} currentScope={DamageTrackerService.Instance.CurrentSource()?.Id ?? "<none>"} argShape=[{string.Join(",", args.Select(a => a?.GetType().Name ?? "null"))}]");
        var key = DamageReflect.ToCardKey(cardSource) ?? DamageTrackerService.Instance.CurrentSource();
        if (!key.HasValue) return;

        var amount = DamageReflect.FindFirstNumeric(args);
        if (amount <= 0) return;
        DamageTrackerService.Instance.RecordBlock(amount, key);

        var blockSource = key.Value;
        foreach (var entry in DamageTrackerService.Instance.SnapshotLedger())
        {
            if (entry.Kind != BuffEntryKind.AdditiveBlock) continue;
            if (entry.Magnitude <= 0) continue;
            if (entry.Source.Equals(blockSource) && !entry.Settled) continue;
            DamageTrackerService.Instance.RecordDerivedBlock(entry.Source, entry.PowerId, entry.Magnitude);
        }

        DamageTrackerService.Instance.RegisterPlayerBlockSupply(blockSource, amount);
    };

    public static readonly Action<object?[]> OnAfterPowerAmountChanged = args =>
    {
        var power = Reflect.FindByTypeName(args, "PowerModel");
        if (power == null) return;

        var amount = DamageReflect.FindFirstNumeric(args);
        if (amount == 0) return;

        var probePowerId = Reflect.ReadAny(power, "Id")?.ToString() ?? "POWER.UNKNOWN";
        var probeOwner = Reflect.ReadAny(power, "Owner") as object;
        var probeOwnerIsPlayer = probeOwner != null && Reflect.ReadBool(probeOwner, "IsPlayer") == true;
        var probeCard = Reflect.FindByTypeName(args, "CardModel");
        var probeScope = DamageTrackerService.Instance.CurrentSource();

        var cardSource = probeCard;
        var key = DamageReflect.ToCardKey(cardSource) ?? probeScope;
        var powerId = probePowerId;
        var ownerIsPlayer = probeOwnerIsPlayer;
        var ownerInstanceId = Reflect.TryReadInstanceId(probeOwner) ?? 0L;

        if (probePowerId.IndexOf("HELLRAISER", StringComparison.OrdinalIgnoreCase) >= 0
            || probePowerId.IndexOf("ONE_TWO_PUNCH", StringComparison.OrdinalIgnoreCase) >= 0
            || probePowerId.IndexOf("ONETWO", StringComparison.OrdinalIgnoreCase) >= 0
            || probePowerId.IndexOf("AGGRESSION", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var probeCurAmt = Reflect.ReadLong(power, "Amount") ?? Reflect.ReadLong(power, "amount") ?? 0;
            var probeOwnerKind = probeOwner != null && Reflect.ReadBool(probeOwner, "IsPlayer") == true
                ? "Player"
                : (probeOwner != null ? "Enemy" : "<none>");
            var probeSrcId = Reflect.ReadAny(probeCard, "Id")?.ToString() ?? "null";
            ModEntry.Log($"power-probe: pid={probePowerId} amt={probeCurAmt} delta={amount} owner={probeOwnerKind} source={probeSrcId}");
        }

        if (probeOwnerIsPlayer && amount < 0
            && (string.Equals(probePowerId, "POWER.ONE_TWO_PUNCH_POWER", StringComparison.OrdinalIgnoreCase)
                || probePowerId.IndexOf("ONETWO", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            AttributionHandlers.OtpPendingConsumption = true;
        }

        if (string.Equals(probePowerId, "POWER.STRENGTH_POWER", StringComparison.OrdinalIgnoreCase))
        {
            ModEntry.Log($"mangle-probe: powerAmtChanged owner={ownerInstanceId} isPlayer={probeOwnerIsPlayer} amount={amount} cardScope={(key?.Id ?? "<none>")} hasSource={(probeCard != null)}");
        }

        if (amount < 0 && ownerIsPlayer
            && string.Equals(powerId, "POWER.FREE_ATTACK_POWER", StringComparison.OrdinalIgnoreCase))
        {
            DamageTrackerService.Instance.MarkFapConsumed(ownerInstanceId);
        }

        if (!key.HasValue)
        {
            if (ownerIsPlayer && amount > 0
                && string.Equals(powerId, "POWER.STRENGTH_POWER", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var p in Reflect.IteratePowers(probeOwner))
                {
                    var pid = Reflect.ReadAny(p, "Id")?.ToString();
                    if (!string.Equals(pid, "POWER.DEMON_FORM_POWER", StringComparison.OrdinalIgnoreCase)) continue;
                    var dfAmount = Reflect.ReadLong(p, "Amount") ?? Reflect.ReadLong(p, "amount") ?? 0;
                    if (dfAmount > 0 && amount == dfAmount)
                    {
                        key = SourceKey.Card("CARD.DEMON_FORM", "Demon Form");
                        break;
                    }
                }
            }
            if (!key.HasValue) return;
        }

        var powerName = Reflect.AsText(Reflect.ReadAny(power, "Title", "TitleLocString")) ?? powerId;
        DamageTrackerService.Instance.PowerNames.Remember(powerId, powerName);

        if (ownerIsPlayer)
            DamageTrackerService.Instance.RecordPowerOnSelf(powerId, amount, key);
        else
            DamageTrackerService.Instance.RecordPowerOnEnemy(powerId, amount, key);

        if (key.Value.Kind == SourceKind.Card)
        {
            if (amount > 0)
                DamageTrackerService.Instance.NotePowerOrigin(ownerInstanceId, powerId, key.Value);
            else if (amount < 0)
                DamageTrackerService.Instance.NotePowerOriginIfAbsent(ownerInstanceId, powerId, key.Value);
        }

        if (ownerIsPlayer && amount > 0
            && string.Equals(powerId, "POWER.STRENGTH_POWER", StringComparison.OrdinalIgnoreCase))
        {
            DamageTrackerService.Instance.RecordStrengthGained(amount, key);
        }

        if (!ownerIsPlayer && amount != 0
            && string.Equals(powerId, "POWER.STRENGTH_POWER", StringComparison.OrdinalIgnoreCase))
        {
            DamageTrackerService.Instance.RecordAppliesEnemyStrength(amount, key);
        }

        if (ownerIsPlayer && amount > 0 && LedgerPolicy.LedgerEligiblePowers.Contains(powerId))
        {
            var kind = LedgerPolicy.KindForPowerId(powerId);
            if (kind.HasValue)
            {
                var src = key.Value;
                var turnScoped = LedgerPolicy.IsTurnScoped(powerId, src);
                DamageTrackerService.Instance.RegisterBuff(src, powerId, amount, kind.Value, turnScoped);
                ModEntry.Log($"ledger: +{amount} {powerId} from {src.DisplayName} (turnScoped={turnScoped}, token={DamageTrackerService.Instance.CurrentCardPlayToken})");
            }
        }

        if (!ownerIsPlayer && amount > 0 && LedgerPolicy.MultiplicativeEnemyDebuffs.Contains(powerId))
        {
            DamageTrackerService.Instance.RegisterPowerApplier(powerId, key.Value);
            DamageTrackerService.Instance.RecordPowerAppliedStacks(powerId, amount, key.Value);
            ModEntry.Log($"power-applied: pid={powerId} stacks={amount} src={key.Value}");
        }
    };

    public static readonly Action<object?[]> OnAfterCurrentHpChanged = args =>
    {
        // No cardSource on this hook; rely on the active play scope. The hook
        // fires for every HP change (damage too), so we filter on delta > 0 and
        // target.IsPlayer to count only player healing.
        var target = Reflect.FindByTypeName(args, "Creature");
        if (target == null) return;
        if (Reflect.ReadBool(target, "IsPlayer") != true) return;

        var delta = DamageReflect.FindFirstNumeric(args);
        if (delta <= 0) return;

        DamageTrackerService.Instance.RecordHealing(delta);
    };

    private static bool ReceiverIsPlayer(object? damageResult)
    {
        if (damageResult == null) return false;
        var receiver = Reflect.ReadAny(damageResult, "Receiver", "Target");
        return receiver != null && Reflect.ReadBool(receiver, "IsPlayer") == true;
    }
}

internal static class DamageMath
{
    public static decimal MultiplierForHit(object? dealer, object? target, object? props, object? cardSource)
    {
        var m = 1m;
        m *= ProductOfMultipliers(dealer, target, props, dealer, cardSource);
        m *= ProductOfMultipliers(target, target, props, dealer, cardSource);
        return m;
    }

    public static decimal SinglePowerMultiplier(object power, object? dealer, object? target, object? props, object? cardSource)
    {
        var m = power.GetType().GetMethod(
            "ModifyDamageMultiplicative",
            BindingFlags.Public | BindingFlags.Instance);
        if (m == null) return 1m;
        var ps = m.GetParameters();
        if (ps.Length != 5) return 1m;
        object?[] callArgs = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var pt = ps[i].ParameterType;
            var pn = ps[i].Name ?? string.Empty;
            if (pt == typeof(decimal)) callArgs[i] = 1m;
            else if (pn.Equals("target", StringComparison.OrdinalIgnoreCase)) callArgs[i] = target;
            else if (pn.Equals("dealer", StringComparison.OrdinalIgnoreCase)) callArgs[i] = dealer;
            else if (pt.Name == "ValueProp") callArgs[i] = props;
            else if (pt.Name == "CardModel") callArgs[i] = cardSource;
            else if (pt.Name == "Creature") callArgs[i] = target;
            else callArgs[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
        }
        try
        {
            var result = m.Invoke(power, callArgs);
            if (result is decimal d && d >= 0m) return d;
        }
        catch { }
        return 1m;
    }

    private static decimal ProductOfMultipliers(object? owner, object? target, object? props, object? dealer, object? cardSource)
    {
        if (owner == null) return 1m;
        var product = 1m;
        foreach (var power in Reflect.IteratePowers(owner))
        {
            var m = power.GetType().GetMethod(
                "ModifyDamageMultiplicative",
                BindingFlags.Public | BindingFlags.Instance);
            if (m == null) continue;
            var ps = m.GetParameters();
            if (ps.Length != 5) continue;
            object?[] callArgs = new object?[ps.Length];
            for (var i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                var pn = ps[i].Name ?? string.Empty;
                if (pt == typeof(decimal)) callArgs[i] = 1m;
                else if (pn.Equals("target", StringComparison.OrdinalIgnoreCase)) callArgs[i] = target;
                else if (pn.Equals("dealer", StringComparison.OrdinalIgnoreCase)) callArgs[i] = dealer;
                else if (pt.Name == "ValueProp") callArgs[i] = props;
                else if (pt.Name == "CardModel") callArgs[i] = cardSource;
                else if (pt.Name == "Creature") callArgs[i] = ReferenceEquals(power, owner) ? owner : target;
                else callArgs[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
            }
            try
            {
                var result = m.Invoke(power, callArgs);
                if (result is decimal d && d >= 0m) product *= d;
            }
            catch { }
        }
        return product;
    }
}

internal static class DamageReflect
{
    public static long FindFirstNumeric(object?[] args)
    {
        if (args == null) return 0;
        foreach (var a in args)
        {
            if (a == null) continue;
            switch (a)
            {
                case int i:     return i;
                case long l:    return l;
                case short s:   return s;
                case decimal d: return (long)d;
                case float f:   return (long)f;
                case double db: return (long)db;
            }
        }
        return 0;
    }

    public static SourceKey? ToCardKey(object? cardModel)
    {
        if (cardModel == null) return null;
        var idObj = Reflect.ReadAny(cardModel, "Id", "id");
        var id = idObj?.ToString();
        if (string.IsNullOrEmpty(id)) return null;
        var name = Reflect.AsText(Reflect.ReadAny(cardModel, "Title", "TitleLocString", "displayName", "name"));
        return SourceKey.Card(id!, string.IsNullOrEmpty(name) ? id! : name!);
    }

    public static int? TryReadEnergySpent(object? cardPlay)
    {
        var resources = Reflect.ReadAny(cardPlay, "Resources", "resources");
        if (resources == null) return null;
        var v = Reflect.ReadAny(resources, "EnergySpent", "energySpent");
        if (v == null) return null;
        try { return Convert.ToInt32(v); } catch { return null; }
    }
}
