using System;
using System.Linq;

namespace Sts2Mods.Common;

public static class PatchTargets
{
    public static Type? ResolveType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => { try { return a.GetType(fullName, false); } catch { return null; } })
            .FirstOrDefault(t => t != null);
}
