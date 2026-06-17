using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using Sts2Mods.Common;

namespace EnemyIntentTracker;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const string ModId = "EnemyIntentTracker";
    public const string HarmonyId = "com.augment.sts2.enemyintenttracker";

    internal static Harmony? Harmony { get; private set; }

    public static void Initialize()
    {
        try
        {
            Harmony = new Harmony(HarmonyId);
            HarmonyBootstrap.PatchAllIndividually(Harmony,
                Assembly.GetExecutingAssembly(),
                msg => ModLogger.LogWarn(ModId, $"Patch class skipped: {msg}"));

            ModLogger.Log(ModId, $"Initialized v{Assembly.GetExecutingAssembly().GetName().Version}");
        }
        catch (Exception ex)
        {
            ModLogger.Log(ModId, $"Initialization failed: {ex}");
        }
    }

    internal static void Log(string message)      => ModLogger.Log(ModId, message);
    internal static void LogWarn(string message)  => ModLogger.LogWarn(ModId, message);
    internal static void LogError(string message) => ModLogger.LogError(ModId, message);
}
