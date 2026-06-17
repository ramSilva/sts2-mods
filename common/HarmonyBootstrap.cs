using System;
using System.Reflection;
using HarmonyLib;

namespace Sts2Mods.Common;

public static class HarmonyBootstrap
{
    public static void PatchAllIndividually(Harmony harmony, Assembly assembly, Action<string> warn)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<HarmonyPatch>() == null) continue;
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                warn($"'{type.Name}': {ex.Message}");
            }
        }
    }
}
