using System;
using System.Collections.Generic;

namespace DamageTracker.Tracking;

[Flags]
public enum CardEffectKind
{
    None                    = 0,
    DealsDamage             = 1 << 0,
    GrantsBlock             = 1 << 1,
    DamageAbsorbed          = 1 << 2,
    AppliesVulnerable       = 1 << 3,
    VulnerableDamage        = 1 << 4,
    AppliesWeak             = 1 << 5,
    WeakDamage              = 1 << 6,
    GrantsTemporaryStrength = 1 << 7,
    SelfDamage              = 1 << 8,
    CheatsEnergy            = 1 << 9,
    CardsDrawn              = 1 << 10,
    CardsExhausted          = 1 << 11,
    EnergyGained            = 1 << 12,
    StrengthGained          = 1 << 13,
    StrengthDamage          = 1 << 14,
    AppliesEnemyStrength    = 1 << 15,
    EnemyStrengthDamage     = 1 << 16,
    DamageReduced           = 1 << 17,
    TimesTriggered          = 1 << 18,
    Healing                 = 1 << 19,
    BlockPreserved          = 1 << 20,
    CardsUpgraded           = 1 << 21,
}

public static class CardEffectManifest
{
    private static readonly IReadOnlyDictionary<string, CardEffectKind> Table =
        new Dictionary<string, CardEffectKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["CARD.STRIKE_IRONCLAD"] = CardEffectKind.DealsDamage,
            ["CARD.DEFEND_IRONCLAD"] = CardEffectKind.GrantsBlock | CardEffectKind.DamageAbsorbed,
            ["CARD.SETUP_STRIKE"]    = CardEffectKind.DealsDamage | CardEffectKind.GrantsTemporaryStrength,
            ["CARD.UPPERCUT"]        = CardEffectKind.DealsDamage
                                       | CardEffectKind.AppliesVulnerable | CardEffectKind.VulnerableDamage
                                       | CardEffectKind.AppliesWeak       | CardEffectKind.WeakDamage,
            ["CARD.BASH"]            = CardEffectKind.DealsDamage
                                       | CardEffectKind.AppliesVulnerable | CardEffectKind.VulnerableDamage,
            ["CARD.ANGER"]            = CardEffectKind.DealsDamage,
            ["CARD.ARMAMENTS"]        = CardEffectKind.GrantsBlock | CardEffectKind.DamageAbsorbed
                                        | CardEffectKind.CardsUpgraded,
            ["CARD.CINDER"]           = CardEffectKind.DealsDamage,
            ["CARD.HEADBUTT"]         = CardEffectKind.DealsDamage,
            ["CARD.IRON_WAVE"]        = CardEffectKind.DealsDamage
                                        | CardEffectKind.GrantsBlock | CardEffectKind.DamageAbsorbed,
            ["CARD.PERFECTED_STRIKE"] = CardEffectKind.DealsDamage,
            ["CARD.POMMEL_STRIKE"]    = CardEffectKind.DealsDamage,
            ["CARD.SHRUG_IT_OFF"]     = CardEffectKind.GrantsBlock | CardEffectKind.DamageAbsorbed,
            ["CARD.SWORD_BOOMERANG"]  = CardEffectKind.DealsDamage,
            ["CARD.THUNDERCLAP"]      = CardEffectKind.DealsDamage
                                        | CardEffectKind.AppliesVulnerable | CardEffectKind.VulnerableDamage,
            ["CARD.TREMBLE"]          = CardEffectKind.AppliesVulnerable | CardEffectKind.VulnerableDamage,
            ["CARD.TRUE_GRIT"]        = CardEffectKind.GrantsBlock | CardEffectKind.DamageAbsorbed,
            ["CARD.TWIN_STRIKE"]      = CardEffectKind.DealsDamage,
            ["CARD.BODY_SLAM"]        = CardEffectKind.DealsDamage,
            ["CARD.BREAKTHROUGH"]     = CardEffectKind.DealsDamage | CardEffectKind.SelfDamage,
            ["CARD.BLOOD_WALL"]       = CardEffectKind.GrantsBlock
                                        | CardEffectKind.DamageAbsorbed | CardEffectKind.SelfDamage,
            ["CARD.BLOODLETTING"]     = CardEffectKind.SelfDamage,
            ["CARD.HAVOC"]            = CardEffectKind.CheatsEnergy,
            ["CARD.MOLTEN_FIST"]      = CardEffectKind.DealsDamage
                                        | CardEffectKind.AppliesVulnerable | CardEffectKind.VulnerableDamage,

            ["CARD.ASHEN_STRIKE"]     = CardEffectKind.DealsDamage,
            ["CARD.BATTLE_TRANCE"]    = CardEffectKind.CardsDrawn,
            ["CARD.BLUDGEON"]         = CardEffectKind.DealsDamage,
            ["CARD.BULLY"]            = CardEffectKind.DealsDamage,
            ["CARD.BURNING_PACT"]     = CardEffectKind.CardsExhausted | CardEffectKind.CardsDrawn,
            ["CARD.COLOSSUS"]         = CardEffectKind.GrantsBlock | CardEffectKind.DamageReduced,
            ["CARD.DEMON_FORM"]       = CardEffectKind.StrengthGained | CardEffectKind.StrengthDamage,
            ["CARD.DISMANTLE"]        = CardEffectKind.DealsDamage | CardEffectKind.TimesTriggered,
            ["CARD.DOMINATE"]         = CardEffectKind.AppliesVulnerable
                                        | CardEffectKind.StrengthGained | CardEffectKind.StrengthDamage,
            ["CARD.DRUM_OF_BATTLE"]   = CardEffectKind.CardsDrawn | CardEffectKind.CardsExhausted,
            ["CARD.EVIL_EYE"]         = CardEffectKind.GrantsBlock | CardEffectKind.TimesTriggered,
            ["CARD.EXPECT_A_FIGHT"]   = CardEffectKind.EnergyGained,
            ["CARD.FEEL_NO_PAIN"]     = CardEffectKind.GrantsBlock,
            ["CARD.FIGHT_ME"]         = CardEffectKind.DealsDamage
                                        | CardEffectKind.StrengthGained | CardEffectKind.StrengthDamage
                                        | CardEffectKind.AppliesEnemyStrength | CardEffectKind.EnemyStrengthDamage,
            ["CARD.FLAME_BARRIER"]    = CardEffectKind.GrantsBlock | CardEffectKind.DealsDamage,
            ["CARD.FORGOTTEN_RITUAL"] = CardEffectKind.EnergyGained,
            ["CARD.HEMOKINESIS"]      = CardEffectKind.DealsDamage | CardEffectKind.SelfDamage,
            ["CARD.HOWL_FROM_BEYOND"] = CardEffectKind.DealsDamage | CardEffectKind.TimesTriggered,
            ["CARD.INFERNAL_BLADE"]   = CardEffectKind.DealsDamage | CardEffectKind.CheatsEnergy,
            ["CARD.INFERNO"]          = CardEffectKind.SelfDamage | CardEffectKind.DealsDamage,
            ["CARD.INFLAME"]          = CardEffectKind.StrengthGained | CardEffectKind.StrengthDamage,
            ["CARD.JUGGLING"]         = CardEffectKind.DealsDamage,
            ["CARD.PILLAGE"]          = CardEffectKind.DealsDamage | CardEffectKind.CardsDrawn,
            ["CARD.RAGE"]             = CardEffectKind.GrantsBlock,
            ["CARD.RAMPAGE"]          = CardEffectKind.DealsDamage,
            ["CARD.RUPTURE"]          = CardEffectKind.StrengthGained | CardEffectKind.StrengthDamage,
            ["CARD.SECOND_WIND"]      = CardEffectKind.CardsExhausted | CardEffectKind.GrantsBlock,
            ["CARD.SPITE"]            = CardEffectKind.DealsDamage | CardEffectKind.TimesTriggered,
            ["CARD.STAMPEDE"]         = CardEffectKind.DealsDamage | CardEffectKind.CheatsEnergy,
            ["CARD.STOMP"]            = CardEffectKind.DealsDamage | CardEffectKind.CheatsEnergy,
            ["CARD.STONE_ARMOR"]      = CardEffectKind.GrantsBlock,
            ["CARD.TAUNT"]            = CardEffectKind.GrantsBlock | CardEffectKind.AppliesVulnerable,
            ["CARD.UNRELENTING"]      = CardEffectKind.DealsDamage | CardEffectKind.CheatsEnergy,
            ["CARD.VICIOUS"]          = CardEffectKind.CardsDrawn,
            ["CARD.WHIRLWIND"]        = CardEffectKind.DealsDamage,

            ["CARD.BRAND"]          = CardEffectKind.SelfDamage | CardEffectKind.CardsExhausted
                                      | CardEffectKind.StrengthGained | CardEffectKind.StrengthDamage,
            ["CARD.BREAK"]          = CardEffectKind.DealsDamage
                                      | CardEffectKind.AppliesVulnerable | CardEffectKind.VulnerableDamage,
            ["CARD.CASCADE"]        = CardEffectKind.EnergyGained,
            ["CARD.CONFLAGRATION"]  = CardEffectKind.DealsDamage,
            ["CARD.CORRUPTION"]     = CardEffectKind.CheatsEnergy | CardEffectKind.CardsExhausted,
            ["CARD.CRIMSON_MANTLE"] = CardEffectKind.SelfDamage | CardEffectKind.GrantsBlock | CardEffectKind.DamageAbsorbed,
            ["CARD.DARK_EMBRACE"]   = CardEffectKind.CardsDrawn,
            ["CARD.FIEND_FIRE"]     = CardEffectKind.DealsDamage | CardEffectKind.CardsExhausted,
            ["CARD.GIANT_ROCK"]     = CardEffectKind.DealsDamage,
            ["CARD.HELLRAISER"]     = CardEffectKind.DealsDamage | CardEffectKind.CheatsEnergy,
            ["CARD.IMPERVIOUS"]     = CardEffectKind.GrantsBlock | CardEffectKind.DamageAbsorbed,
            ["CARD.JUGGERNAUT"]     = CardEffectKind.DealsDamage,
            ["CARD.MANGLE"]         = CardEffectKind.DealsDamage
                                      | CardEffectKind.AppliesEnemyStrength | CardEffectKind.EnemyStrengthDamage,
            ["CARD.OFFERING"]       = CardEffectKind.SelfDamage | CardEffectKind.EnergyGained
                                      | CardEffectKind.CardsDrawn | CardEffectKind.CardsExhausted,
            ["CARD.ONE_TWO_PUNCH"]  = CardEffectKind.DealsDamage | CardEffectKind.CheatsEnergy,
            ["CARD.PACTS_END"]      = CardEffectKind.DealsDamage,
            ["CARD.PRIMAL_FORCE"]   = CardEffectKind.DealsDamage,
            ["CARD.PYRE"]           = CardEffectKind.EnergyGained,
            ["CARD.STOKE"]          = CardEffectKind.CardsExhausted,
            ["CARD.TEAR_ASUNDER"]   = CardEffectKind.DealsDamage,
            ["CARD.THRASH"]         = CardEffectKind.DealsDamage | CardEffectKind.CardsExhausted,
            ["CARD.UNMOVABLE"]      = CardEffectKind.GrantsBlock | CardEffectKind.DamageAbsorbed,

            ["CARD.FEED"]       = CardEffectKind.DealsDamage | CardEffectKind.Healing,
            ["CARD.NOT_YET"]    = CardEffectKind.Healing,
            ["CARD.AGGRESSION"] = CardEffectKind.CardsUpgraded,
            ["CARD.BARRICADE"]  = CardEffectKind.BlockPreserved,
        };

    private static readonly IReadOnlyDictionary<string, string> PowerToCard =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["POWER.STAMPEDE_POWER"]      = "CARD.STAMPEDE",
            ["POWER.JUGGLING_POWER"]      = "CARD.JUGGLING",
            ["POWER.HELLRAISER_POWER"]    = "CARD.HELLRAISER",
            ["POWER.ONE_TWO_PUNCH_POWER"] = "CARD.ONE_TWO_PUNCH",
            ["POWER.AGGRESSION_POWER"]    = "CARD.AGGRESSION",
            ["POWER.BARRICADE_POWER"]     = "CARD.BARRICADE",
            ["POWER.COLOSSUS_POWER"]      = "CARD.COLOSSUS",
            ["POWER.CORRUPTION_POWER"]    = "CARD.CORRUPTION",
            ["POWER.CRIMSON_MANTLE_POWER"] = "CARD.CRIMSON_MANTLE",
            ["POWER.DARK_EMBRACE_POWER"]  = "CARD.DARK_EMBRACE",
            ["POWER.DEMON_FORM_POWER"]    = "CARD.DEMON_FORM",
            ["POWER.FEEL_NO_PAIN_POWER"]  = "CARD.FEEL_NO_PAIN",
            ["POWER.FLAME_BARRIER_POWER"] = "CARD.FLAME_BARRIER",
            ["POWER.INFERNO_POWER"]       = "CARD.INFERNO",
            ["POWER.JUGGERNAUT_POWER"]    = "CARD.JUGGERNAUT",
            ["POWER.MANGLE_POWER"]        = "CARD.MANGLE",
            ["POWER.PYRE_POWER"]          = "CARD.PYRE",
            ["POWER.RUPTURE_POWER"]       = "CARD.RUPTURE",
            ["POWER.SETUP_STRIKE_POWER"]  = "CARD.SETUP_STRIKE",
            ["POWER.UNMOVABLE_POWER"]     = "CARD.UNMOVABLE",
        };

    public static CardEffectKind Get(SourceKey key)
    {
        if (string.IsNullOrEmpty(key.Id)) return CardEffectKind.None;
        string? lookupId = key.Kind switch
        {
            SourceKind.Card => key.Id,
            SourceKind.Power => PowerToCard.TryGetValue(key.Id, out var c) ? c : null,
            _ => null,
        };
        if (lookupId == null) return CardEffectKind.None;
        return Table.TryGetValue(lookupId, out var kinds) ? kinds : CardEffectKind.None;
    }
}
