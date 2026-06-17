using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Sts2Mods.Common;

public static class Reflect
{
    public static object? FindByTypeName(object?[] args, string simpleTypeName)
    {
        if (args == null) return null;
        foreach (var a in args)
        {
            if (a == null) continue;
            for (var cur = a.GetType(); cur != null; cur = cur.BaseType)
                if (cur.Name == simpleTypeName) return a;
        }
        return null;
    }

    public static object? ReadAny(object? instance, params string[] names)
    {
        if (instance == null) return null;
        var t = instance.GetType();
        foreach (var n in names)
        {
            var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) { var v = p.GetValue(instance); if (v != null) return v; }
            var f = t.GetField(n, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) { var v = f.GetValue(instance); if (v != null) return v; }
        }
        return null;
    }

    public static object? GetStaticProperty(Type? t, string name)
    {
        if (t == null) return null;
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
        if (p != null) { try { return p.GetValue(null); } catch { return null; } }
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.Static);
        if (f != null) { try { return f.GetValue(null); } catch { return null; } }
        return null;
    }

    public static string? AsText(object? value)
    {
        if (value == null) return null;
        if (value is string s) return s;
        var m = value.GetType().GetMethod("GetFormattedText", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (m != null)
        {
            try { return m.Invoke(value, null) as string; } catch { }
        }
        return value.ToString();
    }

    public static int? ReadInt(object? target, string member)
    {
        var v = ReadAny(target, member);
        return v is int i ? i : v == null ? null : (int?)Convert.ToInt32(v);
    }

    public static long? ReadLong(object? target, string member)
    {
        var v = ReadAny(target, member);
        if (v == null) return null;
        try { return Convert.ToInt64(v); } catch { return null; }
    }

    public static bool? ReadBool(object? target, string member)
    {
        var v = ReadAny(target, member);
        return v is bool b ? b : (bool?)null;
    }

    public static object? FindOtherCreature(object?[] args, object? excluded)
    {
        if (args == null) return null;
        foreach (var a in args)
        {
            if (a == null) continue;
            if (ReferenceEquals(a, excluded)) continue;
            for (var cur = a.GetType(); cur != null; cur = cur.BaseType)
                if (cur.Name == "Creature") return a;
        }
        return null;
    }

    public static long? TryReadInstanceId(object? cardModel)
    {
        if (cardModel == null) return null;
        var v = ReadAny(cardModel, "InstanceId", "instanceId", "Uuid", "UUID", "uuid");
        if (v == null)
        {
            try { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(cardModel); }
            catch { return null; }
        }
        try { return Convert.ToInt64(v); } catch { return null; }
    }

    public static IEnumerable<object> IteratePowers(object? creature)
    {
        var seq = ReadAny(creature, "Powers", "powers", "PowersList", "ActivePowers") as IEnumerable;
        if (seq == null) yield break;
        foreach (var p in seq)
        {
            if (p == null) continue;
            yield return p;
        }
    }

    public static long? TryReadPowerAmount(object? creature, string powerId)
    {
        if (string.IsNullOrEmpty(powerId)) return null;
        foreach (var power in IteratePowers(creature))
        {
            var pid = ReadAny(power, "Id")?.ToString();
            if (string.Equals(pid, powerId, StringComparison.OrdinalIgnoreCase))
                return ReadLong(power, "Amount") ?? ReadLong(power, "amount");
        }
        return null;
    }
}
