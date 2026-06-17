using System;
using System.Collections.Generic;
using System.Linq;

namespace DamageTracker.Tracking;

/// <summary>
/// Aggregates per-source metrics across three scopes (combat, act, run).
///
/// Two attribution paths:
///  - Direct: the caller passes an <see cref="SourceKey"/>, which is what the
///    hooks that carry a CardModel cardSource (AfterDamageGiven, AfterBlockGained,
///    AfterPowerAmountChanged) use.
///  - Stack: legacy fallback for hooks that don't expose a source. Push the
///    active card via <see cref="PushSource"/> from BeforeCardPlayed and pop it
///    in AfterCardPlayed; record-* methods without explicit source fall back to
///    the top of the stack.
/// </summary>
public sealed class DamageTrackerService
{
    public static DamageTrackerService Instance { get; } = new();

    private readonly object _lock = new();
    private readonly Dictionary<SourceKey, Totals> _combat = new();
    private readonly Dictionary<SourceKey, Totals> _act = new();
    private readonly Dictionary<SourceKey, Totals> _run = new();
    private readonly Stack<SourceKey> _attribution = new();
    private readonly List<BuffLedgerEntry> _ledger = new();
    private readonly ScopeGlobals _globalCombat = new();
    private readonly ScopeGlobals _globalAct = new();
    private readonly ScopeGlobals _globalRun = new();
    private readonly List<BlockSupplyEntry> _playerBlockFifo = new();
    private int _cardPlayToken = 0;

    public PowerNameMap PowerNames { get; } = new();

    public event Action? Changed;

    public int CurrentCardPlayToken
    {
        get { lock (_lock) return _cardPlayToken; }
    }

    public IDisposable PushSource(SourceKey key)
    {
        lock (_lock)
        {
            if (_attribution.Count == 0) _cardPlayToken++;
            _attribution.Push(key);
        }
        return new Pop(this, key);
    }

    private void PopExpected(SourceKey key)
    {
        lock (_lock)
        {
            if (_attribution.Count == 0) return;
            if (EqualityComparer<SourceKey>.Default.Equals(_attribution.Peek(), key))
            {
                _attribution.Pop();
                if (_attribution.Count == 0) _cardPlayToken = 0;
                return;
            }
            var rebuilt = new Stack<SourceKey>();
            foreach (var item in _attribution)
                if (!EqualityComparer<SourceKey>.Default.Equals(item, key))
                    rebuilt.Push(item);
            _attribution.Clear();
            foreach (var item in rebuilt) _attribution.Push(item);
            if (_attribution.Count == 0) _cardPlayToken = 0;
        }
    }

    public SourceKey? CurrentSource()
    {
        lock (_lock) return _attribution.Count == 0 ? null : _attribution.Peek();
    }

    private bool TryResolveKey(SourceKey? explicitSource, out SourceKey key)
    {
        if (explicitSource.HasValue) { key = explicitSource.Value; return true; }
        var top = CurrentSource();
        if (top.HasValue) { key = top.Value; return true; }
        key = default;
        return false;
    }

    public void RecordDamage(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => { t.Damage += amount; t.Hits += 1; });
    }

    public void RecordSelfDamage(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => t.SelfDamage += amount);
    }

    public void RecordCheatedEnergy(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => t.CheatedEnergy += amount);
    }

    public void RecordCardsDrawn(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => t.CardsDrawn += amount);
    }

    public void RecordCardsExhausted(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => t.CardsExhausted += amount);
    }

    public void RecordBlockPreserved(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => t.BlockPreserved += amount);
    }

    public void RecordCardsUpgraded(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => t.CardsUpgraded += amount);
    }

    public void RecordEnergyGained(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => t.EnergyGained += amount);
    }

    public void RecordStrengthGained(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => t.StrengthGained += amount);
    }

    public void RecordAppliesEnemyStrength(long amount, SourceKey? source = null)
    {
        if (amount == 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => t.AppliesEnemyStrength += amount);
    }

    public void RecordTimesTriggered(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => t.TimesTriggered += amount);
    }

    public void RecordStrengthDamage(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        RecordDerivedDamage(key, "POWER.STRENGTH_POWER", amount);
        RecordGlobalDerivedDamage("POWER.STRENGTH_POWER", amount);
        Mutate(key, t => t.StrengthDamage += amount);
    }

    public void RecordEnemyStrengthDamage(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        RecordDerivedDamage(key, "POWER.ENEMY_STRENGTH", amount);
        RecordGlobalDerivedDamage("POWER.ENEMY_STRENGTH", amount);
        Mutate(key, t => t.EnemyStrengthDamage += amount);
    }

    public void RecordDamageReduced(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        RecordDerivedDamage(key, "POWER.COLOSSUS_POWER", amount);
        RecordGlobalDerivedDamage("POWER.COLOSSUS_POWER", amount);
        Mutate(key, t => t.DamageReduced += amount);
    }

    internal sealed class PowerOriginKeyComparer : IEqualityComparer<(long Owner, string PowerId)>
    {
        public static readonly PowerOriginKeyComparer Instance = new();
        public bool Equals((long Owner, string PowerId) x, (long Owner, string PowerId) y) =>
            x.Owner == y.Owner && StringComparer.OrdinalIgnoreCase.Equals(x.PowerId, y.PowerId);
        public int GetHashCode((long Owner, string PowerId) obj) =>
            HashCode.Combine(obj.Owner, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.PowerId ?? string.Empty));
    }

    private readonly Dictionary<(long Owner, string PowerId), SourceKey> _powerOriginCard = new(PowerOriginKeyComparer.Instance);
    private readonly HashSet<long> _fapConsumedOwners = new();

    public void MarkFapConsumed(long ownerInstanceId)
    {
        if (ownerInstanceId == 0) return;
        lock (_lock) _fapConsumedOwners.Add(ownerInstanceId);
    }

    public bool TakeFapConsumed(long ownerInstanceId)
    {
        if (ownerInstanceId == 0) return false;
        lock (_lock) return _fapConsumedOwners.Remove(ownerInstanceId);
    }

    public void NotePowerOrigin(long ownerInstanceId, string powerId, SourceKey card)
    {
        if (string.IsNullOrEmpty(powerId) || card.Kind != SourceKind.Card) return;
        lock (_lock) _powerOriginCard[(ownerInstanceId, powerId)] = card;
    }

    public void NotePowerOriginIfAbsent(long ownerInstanceId, string powerId, SourceKey card)
    {
        if (string.IsNullOrEmpty(powerId) || card.Kind != SourceKind.Card) return;
        lock (_lock) _powerOriginCard.TryAdd((ownerInstanceId, powerId), card);
    }

    public bool TryGetPowerOriginCard(long ownerInstanceId, string powerId, out SourceKey card)
    {
        if (string.IsNullOrEmpty(powerId)) { card = default; return false; }
        lock (_lock) return _powerOriginCard.TryGetValue((ownerInstanceId, powerId), out card);
    }

    public void RecordOnPowerAndCard(long ownerInstanceId, string powerId, string? powerDisplayName, Action<SourceKey> action)
    {
        if (string.IsNullOrEmpty(powerId) || action == null) return;
        action(SourceKey.Power(powerId, string.IsNullOrEmpty(powerDisplayName) ? powerId : powerDisplayName));
        if (TryGetPowerOriginCard(ownerInstanceId, powerId, out var card))
            action(card);
    }

    private readonly Dictionary<long, List<SourceKey>> _secondaryCredits = new();
    [ThreadStatic] private static SourceKey? _currentSpawner;

    public void BeginSpawnContext(SourceKey spawner) => _currentSpawner = spawner;
    public void EndSpawnContext() => _currentSpawner = null;

    public void AttachSpawnerToInstance(long instanceId)
    {
        if (!_currentSpawner.HasValue) return;
        AddSecondaryCredit(instanceId, _currentSpawner.Value);
    }

    public void AddSecondaryCredit(long instanceId, SourceKey credit)
    {
        lock (_lock)
        {
            if (!_secondaryCredits.TryGetValue(instanceId, out var list))
                _secondaryCredits[instanceId] = list = new List<SourceKey>();
            if (!list.Contains(credit)) list.Add(credit);
        }
    }

    public IReadOnlyList<SourceKey> GetSecondaryCredits(long instanceId)
    {
        lock (_lock)
        {
            return _secondaryCredits.TryGetValue(instanceId, out var list)
                ? list.ToArray()
                : Array.Empty<SourceKey>();
        }
    }

    public void ClearSecondaryCredits(long instanceId)
    {
        lock (_lock) { _secondaryCredits.Remove(instanceId); }
    }

    public void RecordBlock(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => t.Block += amount);
    }

    public void RecordHealing(long amount, SourceKey? source = null)
    {
        if (amount <= 0 || !TryResolveKey(source, out var key)) return;
        Mutate(key, t => t.Healing += amount);
    }

    public void RecordPowerOnSelf(string powerId, long delta, SourceKey? source = null)
    {
        if (delta == 0 || string.IsNullOrEmpty(powerId)) return;
        if (!TryResolveKey(source, out var key)) return;
        Mutate(key, t => Bump(t.PowersGained, powerId, delta));
    }

    public void RecordPowerOnEnemy(string powerId, long delta, SourceKey? source = null)
    {
        if (delta == 0 || string.IsNullOrEmpty(powerId)) return;
        if (!TryResolveKey(source, out var key)) return;
        Mutate(key, t => Bump(t.PowersApplied, powerId, delta));
    }

    public void RecordPowerAppliedStacks(string powerId, long positiveStacks, SourceKey? source = null)
    {
        if (string.IsNullOrEmpty(powerId) || positiveStacks <= 0) return;
        if (!TryResolveKey(source, out var key)) return;
        Mutate(key, t => Bump(t.PowersAppliedStacks, powerId, positiveStacks));
    }

    public void RegisterBuff(SourceKey source, string powerId, long magnitude, BuffEntryKind kind, bool turnScoped)
    {
        if (string.IsNullOrEmpty(powerId)) return;
        lock (_lock)
        {
            _ledger.Add(new BuffLedgerEntry(source, powerId, kind, turnScoped, _cardPlayToken, magnitude, settled: false));
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<BuffLedgerEntry> SnapshotLedger()
    {
        lock (_lock) return _ledger.ToArray();
    }

    public int SettleLedgerForCardPlay(int token)
    {
        if (token <= 0) return 0;
        var count = 0;
        lock (_lock)
        {
            foreach (var entry in _ledger)
            {
                if (entry.CardPlayToken == token && !entry.Settled)
                {
                    entry.Settled = true;
                    count++;
                }
            }
        }
        return count;
    }

    public int ConsumeTurnScopedBuffs()
    {
        int removed;
        lock (_lock)
        {
            removed = _ledger.RemoveAll(e => e.TurnScoped);
        }
        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    public void RegisterPlayerBlockSupply(SourceKey source, long realizedBlock)
    {
        if (realizedBlock <= 0) return;
        lock (_lock) _playerBlockFifo.Add(new BlockSupplyEntry(source, realizedBlock));
        Changed?.Invoke();
    }

    public long ConsumePlayerBlockAndAttribute(long absorbedAmount)
    {
        if (absorbedAmount <= 0) return 0;
        var attributions = new List<(SourceKey src, long chunk)>();
        long credited = 0;
        lock (_lock)
        {
            var remaining = absorbedAmount;
            while (remaining > 0 && _playerBlockFifo.Count > 0)
            {
                var head = _playerBlockFifo[0];
                if (head.Stacks <= remaining)
                {
                    attributions.Add((head.Source, head.Stacks));
                    credited += head.Stacks;
                    remaining -= head.Stacks;
                    _playerBlockFifo.RemoveAt(0);
                }
                else
                {
                    attributions.Add((head.Source, remaining));
                    credited += remaining;
                    head.Stacks -= remaining;
                    remaining = 0;
                }
            }
        }
        foreach (var (src, chunk) in attributions)
            Mutate(src, t => t.AbsorbedBlock += chunk);
        return credited;
    }

    public void ClearPlayerBlockSupply()
    {
        bool fire;
        lock (_lock) { fire = _playerBlockFifo.Count > 0; _playerBlockFifo.Clear(); }
        if (fire) Changed?.Invoke();
    }

    public void RecordDerivedDamage(SourceKey source, string powerId, long amount)
    {
        if (amount <= 0 || string.IsNullOrEmpty(powerId)) return;
        Mutate(source, t => Bump(t.DerivedDamageByPower, powerId, amount));
    }

    public void RecordDerivedBlock(SourceKey source, string powerId, long amount)
    {
        if (amount <= 0 || string.IsNullOrEmpty(powerId)) return;
        Mutate(source, t => Bump(t.DerivedBlockByPower, powerId, amount));
    }

    public void RegisterPowerApplier(string powerId, SourceKey source)
    {
        if (string.IsNullOrEmpty(powerId)) return;
        lock (_lock)
        {
            AddApplier(_globalCombat, powerId, source);
            AddApplier(_globalAct, powerId, source);
            AddApplier(_globalRun, powerId, source);
        }
        Changed?.Invoke();
    }

    public void RecordGlobalDerivedDamage(string powerId, long bonus)
    {
        if (bonus <= 0 || string.IsNullOrEmpty(powerId)) return;
        lock (_lock)
        {
            Bump(_globalCombat.DerivedDamageByPower, powerId, bonus);
            Bump(_globalAct.DerivedDamageByPower, powerId, bonus);
            Bump(_globalRun.DerivedDamageByPower, powerId, bonus);
        }
        Changed?.Invoke();
    }

    public void RecordGlobalDerivedDamageInstance(long ownerInstanceId, string powerId, long bonus)
    {
        if (bonus <= 0 || string.IsNullOrEmpty(powerId) || ownerInstanceId == 0L) return;
        var key = (ownerInstanceId, powerId);
        lock (_lock)
        {
            Bump(_globalCombat.DerivedDamageByPowerInstance, key, bonus);
            Bump(_globalAct.DerivedDamageByPowerInstance, key, bonus);
            Bump(_globalRun.DerivedDamageByPowerInstance, key, bonus);
        }
        Changed?.Invoke();
    }

    public long GetGlobalDerivedDamageInstance(long ownerInstanceId, string powerId, TrackingScope scope)
    {
        if (string.IsNullOrEmpty(powerId) || ownerInstanceId == 0L) return 0;
        var key = (ownerInstanceId, powerId);
        lock (_lock)
        {
            var g = scope switch
            {
                TrackingScope.Combat => _globalCombat,
                TrackingScope.Act    => _globalAct,
                TrackingScope.Run    => _globalRun,
                _ => _globalCombat,
            };
            return g.DerivedDamageByPowerInstance.TryGetValue(key, out var v) ? v : 0L;
        }
    }

    public bool IsPowerApplier(SourceKey source, string powerId)
    {
        if (string.IsNullOrEmpty(powerId)) return false;
        lock (_lock)
        {
            return HasApplier(_globalCombat, powerId, source)
                || HasApplier(_globalAct, powerId, source)
                || HasApplier(_globalRun, powerId, source);
        }
    }

    public long GetGlobalDerivedDamage(string powerId, TrackingScope scope)
    {
        if (string.IsNullOrEmpty(powerId)) return 0;
        lock (_lock)
        {
            var g = PickGlobal(scope);
            return g.DerivedDamageByPower.TryGetValue(powerId, out var v) ? v : 0;
        }
    }

    private static void AddApplier(ScopeGlobals g, string powerId, SourceKey source)
    {
        if (!g.AppliersByPower.TryGetValue(powerId, out var set))
        {
            set = new HashSet<SourceKey>();
            g.AppliersByPower[powerId] = set;
        }
        set.Add(source);
    }

    private static bool HasApplier(ScopeGlobals g, string powerId, SourceKey source) =>
        g.AppliersByPower.TryGetValue(powerId, out var set) && set.Contains(source);

    private ScopeGlobals PickGlobal(TrackingScope scope) => scope switch
    {
        TrackingScope.Combat => _globalCombat,
        TrackingScope.Act => _globalAct,
        TrackingScope.Run => _globalRun,
        _ => _globalCombat,
    };

    private static void Bump(Dictionary<string, long> dict, string id, long delta)
    {
        dict.TryGetValue(id, out var cur);
        dict[id] = cur + delta;
    }

    private static void Bump<TKey>(Dictionary<TKey, long> dict, TKey id, long delta) where TKey : notnull
    {
        dict.TryGetValue(id, out var cur);
        dict[id] = cur + delta;
    }

    private void Mutate(SourceKey key, Action<Totals> mutator)
    {
        lock (_lock)
        {
            mutator(GetOrAdd(_combat, key));
            mutator(GetOrAdd(_act, key));
            mutator(GetOrAdd(_run, key));
        }
        Changed?.Invoke();
    }

    private static Totals GetOrAdd(Dictionary<SourceKey, Totals> bucket, SourceKey key)
    {
        if (!bucket.TryGetValue(key, out var t)) bucket[key] = t = new Totals();
        return t;
    }

    public Totals GetTotals(TrackingScope scope, SourceKey key)
    {
        lock (_lock)
        {
            var bucket = Pick(scope);
            return bucket.TryGetValue(key, out var t) ? t : new Totals();
        }
    }

    public (Totals Combat, Totals Act, Totals Run) GetAll(SourceKey key)
    {
        lock (_lock)
        {
            return (
                _combat.TryGetValue(key, out var c) ? c : new Totals(),
                _act.TryGetValue(key, out var a) ? a : new Totals(),
                _run.TryGetValue(key, out var r) ? r : new Totals());
        }
    }

    public IReadOnlyList<(SourceKey Key, Totals Totals)> Ranked(TrackingScope scope)
    {
        lock (_lock)
        {
            var bucket = Pick(scope);
            return bucket
                .Select(kv => (kv.Key, kv.Value))
                .OrderByDescending(x => x.Value.Damage)
                .ThenByDescending(x => x.Value.Block)
                .ThenBy(x => x.Key.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void ResetCombat()
    {
        lock (_lock) { _combat.Clear(); _attribution.Clear(); _ledger.Clear(); _playerBlockFifo.Clear(); _globalCombat.Clear(); _cardPlayToken = 0; _powerOriginCard.Clear(); _fapConsumedOwners.Clear(); _secondaryCredits.Clear(); }
        _currentSpawner = null;
        Changed?.Invoke();
    }

    public void ResetAct()
    {
        lock (_lock) { _act.Clear(); _globalAct.Clear(); }
        Changed?.Invoke();
    }

    public void ResetRun()
    {
        lock (_lock) { _combat.Clear(); _act.Clear(); _run.Clear(); _attribution.Clear(); _ledger.Clear(); _playerBlockFifo.Clear(); _globalCombat.Clear(); _globalAct.Clear(); _globalRun.Clear(); _cardPlayToken = 0; _powerOriginCard.Clear(); _fapConsumedOwners.Clear(); _secondaryCredits.Clear(); }
        _currentSpawner = null;
        Changed?.Invoke();
    }

    private Dictionary<SourceKey, Totals> Pick(TrackingScope scope) => scope switch
    {
        TrackingScope.Combat => _combat,
        TrackingScope.Act => _act,
        TrackingScope.Run => _run,
        _ => _combat,
    };

    private sealed class Pop : IDisposable
    {
        private readonly DamageTrackerService _svc;
        private readonly SourceKey _key;
        private bool _done;
        public Pop(DamageTrackerService svc, SourceKey key) { _svc = svc; _key = key; }
        public void Dispose() { if (_done) return; _done = true; _svc.PopExpected(_key); }
    }
}
