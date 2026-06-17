using System;
using System.Collections.Generic;

namespace DamageTracker.Tracking;

public enum TrackingScope
{
    Combat = 0,
    Act = 1,
    Run = 2,
}

public enum SourceKind
{
    Unknown = 0,
    Card = 1,
    Relic = 2,
    Power = 3,
    Potion = 4,
    Enemy = 5,
}

public readonly struct SourceKey : IEquatable<SourceKey>
{
    public SourceKind Kind { get; }
    public string Id { get; }
    public string DisplayName { get; }

    public SourceKey(SourceKind kind, string id, string? displayName = null)
    {
        Kind = kind;
        Id = id ?? string.Empty;
        DisplayName = displayName ?? id ?? string.Empty;
    }

    public bool Equals(SourceKey other) => Kind == other.Kind && Id == other.Id;
    public override bool Equals(object? obj) => obj is SourceKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine((int)Kind, Id);
    public override string ToString() => $"{Kind}:{Id}";

    public static SourceKey Card(string id, string? name = null) => new(SourceKind.Card, id, name);
    public static SourceKey Relic(string id, string? name = null) => new(SourceKind.Relic, id, name);
    public static SourceKey Power(string id, string? name = null) => new(SourceKind.Power, id, name);
    public static SourceKey Potion(string id, string? name = null) => new(SourceKind.Potion, id, name);
    public static SourceKey Enemy(string id, string? name = null) => new(SourceKind.Enemy, id, name);
}

/// <summary>
/// Per-source running totals. A class (not struct) so the dictionaries stay
/// shared across the three scope buckets without the defensive-copy footgun
/// that bit the earlier struct-based version when callers cached a copy.
/// </summary>
public sealed class Totals
{
    public long Damage;        // damage dealt to enemies
    public long Block;         // block gained by the player
    public long AbsorbedBlock;
    public long SelfDamage;    // damage dealt to the player by their own card (e.g. Hemokinesis)
    public long Healing;       // HP restored to the player
    public long CheatedEnergy;
    public int  Hits;          // count of damage-dealing instances (one per AfterDamageGiven)
    public long CardsDrawn;
    public long CardsExhausted;
    public long EnergyGained;
    public long StrengthGained;
    public long StrengthDamage;
    public long AppliesEnemyStrength;
    public long EnemyStrengthDamage;
    public long DamageReduced;
    public long TimesTriggered;
    public long BlockPreserved;
    public long CardsUpgraded;

    // Power id -> net amount summed across the scope. Positive values for the
    // player-owned bucket mean buffs gained (Strength, Dexterity, etc.).
    // Negative values are allowed (debuffs decreased / buffs removed).
    public Dictionary<string, long> PowersGained { get; } = new();

    // Power id -> net amount applied to non-player creatures. This is where
    // Vulnerable/Weak/Poison-style debuffs land.
    public Dictionary<string, long> PowersApplied { get; } = new();

    public Dictionary<string, long> PowersAppliedStacks { get; } = new();

    public Dictionary<string, long> DerivedDamageByPower { get; } = new();
    public Dictionary<string, long> DerivedBlockByPower { get; } = new();
}

public enum BuffEntryKind
{
    AdditiveDamage = 0,
    AdditiveBlock = 1,
    MultiplicativeDamageTaken = 2,
}

public sealed class BlockSupplyEntry
{
    public SourceKey Source { get; }
    public long Stacks { get; internal set; }

    public BlockSupplyEntry(SourceKey source, long stacks)
    {
        Source = source;
        Stacks = stacks;
    }
}

public sealed class BuffLedgerEntry
{
    public SourceKey Source { get; }
    public string PowerId { get; }
    public BuffEntryKind Kind { get; }
    public bool TurnScoped { get; }
    public int CardPlayToken { get; }
    public long Magnitude { get; internal set; }
    public bool Settled { get; internal set; }

    public BuffLedgerEntry(SourceKey source, string powerId, BuffEntryKind kind, bool turnScoped, int cardPlayToken, long magnitude, bool settled)
    {
        Source = source;
        PowerId = powerId ?? string.Empty;
        Kind = kind;
        TurnScoped = turnScoped;
        CardPlayToken = cardPlayToken;
        Magnitude = magnitude;
        Settled = settled;
    }
}

/// <summary>
/// Display-name table for power ids, populated lazily when a power hook fires.
/// Lives at the service layer rather than per-Totals so all scopes share one
/// view of "what's the friendly name for POWER.STRENGTH".
/// </summary>
public sealed class PowerNameMap
{
    private readonly Dictionary<string, string> _names = new();
    private readonly object _lock = new();

    public void Remember(string id, string displayName)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(displayName)) return;
        lock (_lock) _names[id] = displayName;
    }

    public string Resolve(string id)
    {
        lock (_lock) return _names.TryGetValue(id, out var n) ? n : id;
    }
}

/// <summary>
/// Global (non-per-source) per-scope aggregates for multiplicative debuffs.
/// One instance per scope (Combat / Act / Run) lives on the service.
/// </summary>
public sealed class ScopeGlobals
{
    public Dictionary<string, long> DerivedDamageByPower { get; } = new();
    public Dictionary<string, HashSet<SourceKey>> AppliersByPower { get; } = new();
    public Dictionary<(long Owner, string PowerId), long> DerivedDamageByPowerInstance { get; }
        = new(DamageTrackerService.PowerOriginKeyComparer.Instance);

    public void Clear()
    {
        DerivedDamageByPower.Clear();
        AppliersByPower.Clear();
        DerivedDamageByPowerInstance.Clear();
    }
}
