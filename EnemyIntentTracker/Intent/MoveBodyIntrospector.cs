using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace EnemyIntentTracker.Intent;

internal readonly struct PowerApplication(string powerTypeName, int amount)
{
    public readonly string PowerTypeName = powerTypeName;
    public readonly int Amount = amount;
}

internal static class MoveBodyIntrospector
{
    private static readonly Dictionary<ushort, OpCode> _opcodes = BuildOpcodeMap();
    private static readonly HashSet<(string, string)> _logged = new();
    private static readonly object _logLock = new();

    /// <summary>
    /// Scans the IL of <paramref name="moveMethod"/> (resolving the async
    /// state-machine MoveNext when applicable) for the first call to
    /// <c>PowerCmd.Apply&lt;TPower&gt;(...)</c> and returns the power type's
    /// short name plus the decimal amount literal pushed immediately before
    /// the call, when both can be resolved. Returns null on any failure.
    /// </summary>
    public static PowerApplication? TryExtractFirstPowerApply(MethodInfo moveMethod, string monsterId, string stateId)
    {
        try { return TryExtractCore(moveMethod, monsterId, stateId); }
        catch (Exception ex)
        {
            LogOnce(monsterId, stateId, success: false,
                $"power-apply-extract-fail: monster={monsterId} state={stateId} reason=ex:{ex.GetType().Name}:{ex.Message}");
            return null;
        }
    }

    private static PowerApplication? TryExtractCore(MethodInfo moveMethod, string monsterId, string stateId)
    {
        if (moveMethod == null) { LogFail(monsterId, stateId, "null-method"); return null; }
        var target = ResolveBodyHolder(moveMethod);
        var il = target?.GetMethodBody()?.GetILAsByteArray();
        if (target == null || il == null || il.Length == 0) { LogFail(monsterId, stateId, "no-body"); return null; }

        var gta = target.DeclaringType?.IsGenericType == true ? target.DeclaringType.GetGenericArguments() : null;
        var gma = target.IsGenericMethod ? target.GetGenericArguments() : null;
        var ops = WalkIL(il);

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op.OpCode != OpCodes.Call && op.OpCode != OpCodes.Callvirt) continue;
            MethodBase? resolved;
            try { resolved = target.Module.ResolveMethod(BitConverter.ToInt32(il, op.OperandOffset), gta, gma); }
            catch { continue; }
            if (resolved is not MethodInfo mi || mi.Name != "Apply" || !mi.IsGenericMethod) continue;
            var declName = mi.DeclaringType?.FullName;
            if (declName == null || !declName.EndsWith(".PowerCmd")) continue;
            var args = mi.GetGenericArguments();
            if (args.Length == 0) continue;
            var powerName = args[0].Name;
            var amount = ExtractDecimalAmount(target, il, ops, i);
            if (amount == null) { LogFail(monsterId, stateId, $"no-decimal-literal type={powerName}"); return null; }
            LogOnce(monsterId, stateId, success: true,
                $"power-apply-extracted: monster={monsterId} state={stateId} type={powerName} amount={amount.Value}");
            return new PowerApplication(powerName, amount.Value);
        }
        LogFail(monsterId, stateId, "no-apply-call");
        return null;
    }

    public static IReadOnlyList<PowerApplication> TryExtractAllPowerApplies(MethodInfo moveMethod, string monsterId, string stateId)
    {
        try { return TryExtractAllCore(moveMethod, monsterId, stateId) ?? Array.Empty<PowerApplication>(); }
        catch (Exception ex)
        {
            LogOnce(monsterId, stateId + "#all", success: false,
                $"power-apply-extract-all-fail: monster={monsterId} state={stateId} reason=ex:{ex.GetType().Name}:{ex.Message}");
            return Array.Empty<PowerApplication>();
        }
    }

    private static IReadOnlyList<PowerApplication>? TryExtractAllCore(MethodInfo moveMethod, string monsterId, string stateId)
    {
        if (moveMethod == null) return null;
        var target = ResolveBodyHolder(moveMethod);
        var il = target?.GetMethodBody()?.GetILAsByteArray();
        if (target == null || il == null || il.Length == 0) return null;

        var gta = target.DeclaringType?.IsGenericType == true ? target.DeclaringType.GetGenericArguments() : null;
        var gma = target.IsGenericMethod ? target.GetGenericArguments() : null;
        var ops = WalkIL(il);
        var found = new List<PowerApplication>();

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op.OpCode != OpCodes.Call && op.OpCode != OpCodes.Callvirt) continue;
            MethodBase? resolved;
            try { resolved = target.Module.ResolveMethod(BitConverter.ToInt32(il, op.OperandOffset), gta, gma); }
            catch { continue; }
            if (resolved is not MethodInfo mi || mi.Name != "Apply" || !mi.IsGenericMethod) continue;
            var declName = mi.DeclaringType?.FullName;
            if (declName == null || !declName.EndsWith(".PowerCmd")) continue;
            var args = mi.GetGenericArguments();
            if (args.Length == 0) continue;
            var powerName = args[0].Name;
            var amount = ExtractDecimalAmount(target, il, ops, i);
            if (amount == null) continue;
            found.Add(new PowerApplication(powerName, amount.Value));
        }
        return found;
    }

    public static string? TryExtractFirstStatusCardName(MethodInfo moveMethod, string monsterId, string stateId)
    {
        try { return TryExtractStatusCore(moveMethod, monsterId, stateId); }
        catch (Exception ex)
        {
            LogOnce(monsterId, stateId, success: false,
                $"status-card-extract-fail: monster={monsterId} state={stateId} reason=ex:{ex.GetType().Name}:{ex.Message}");
            return null;
        }
    }

    private static string? TryExtractStatusCore(MethodInfo moveMethod, string monsterId, string stateId)
    {
        if (moveMethod == null) { LogStatusFail(monsterId, stateId, "null-method"); return null; }
        var target = ResolveBodyHolder(moveMethod);
        var il = target?.GetMethodBody()?.GetILAsByteArray();
        if (target == null || il == null || il.Length == 0) { LogStatusFail(monsterId, stateId, "no-body"); return null; }

        var gta = target.DeclaringType?.IsGenericType == true ? target.DeclaringType.GetGenericArguments() : null;
        var gma = target.IsGenericMethod ? target.GetGenericArguments() : null;
        var ops = WalkIL(il);

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op.OpCode != OpCodes.Call && op.OpCode != OpCodes.Callvirt) continue;
            MethodBase? resolved;
            try { resolved = target.Module.ResolveMethod(BitConverter.ToInt32(il, op.OperandOffset), gta, gma); }
            catch { continue; }
            if (resolved is not MethodInfo mi || !mi.IsGenericMethod) continue;
            var declName = mi.DeclaringType?.FullName;
            if (declName == null) continue;
            bool isAddToCombatAndPreview = mi.Name == "AddToCombatAndPreview" && declName.EndsWith(".CardPileCmd");
            bool isCreateCard = mi.Name == "CreateCard" && declName.EndsWith(".CombatState");
            if (!isAddToCombatAndPreview && !isCreateCard) continue;
            var args = mi.GetGenericArguments();
            if (args.Length == 0) continue;
            var cardName = args[0].Name;
            LogOnce(monsterId, stateId, success: true,
                $"status-card-extracted: monster={monsterId} state={stateId} card={cardName}");
            return cardName;
        }
        LogStatusFail(monsterId, stateId, "no-add-call");
        return null;
    }

    private static MethodInfo? ResolveBodyHolder(MethodInfo moveMethod)
    {
        var asm = moveMethod.GetCustomAttribute<AsyncStateMachineAttribute>();
        if (asm?.StateMachineType == null) return moveMethod;
        return asm.StateMachineType.GetMethod("MoveNext",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? moveMethod;
    }

    private static int? ExtractDecimalAmount(MethodInfo target, byte[] il, IReadOnlyList<ParsedOp> ops, int callIdx)
    {
        var gta = target.DeclaringType?.IsGenericType == true ? target.DeclaringType.GetGenericArguments() : null;
        var gma = target.IsGenericMethod ? target.GetGenericArguments() : null;

        for (int j = callIdx - 1; j >= 0; j--)
        {
            var op = ops[j];

            if (op.OpCode == OpCodes.Newobj)
            {
                MethodBase? ctor;
                try { ctor = target.Module.ResolveMethod(BitConverter.ToInt32(il, op.OperandOffset), gta, gma); }
                catch { continue; }
                if (ctor?.DeclaringType?.FullName != "System.Decimal" || ctor.Name != ".ctor") continue;
                for (int k = j - 1; k >= 0; k--)
                {
                    var prev = ops[k];
                    if (prev.OpCode == OpCodes.Conv_U1) continue;
                    if (TryReadInt(il, prev, out var n)) return n;
                    break;
                }
                return null;
            }

            if (op.OpCode == OpCodes.Ldsfld)
            {
                FieldInfo? field;
                try { field = target.Module.ResolveField(BitConverter.ToInt32(il, op.OperandOffset), gta, gma); }
                catch { continue; }
                if (field?.DeclaringType?.FullName != "System.Decimal") continue;
                return field.Name switch { "One" => 1, "Zero" => 0, "MinusOne" => -1, _ => null };
            }

            if (op.OpCode == OpCodes.Call)
            {
                MethodBase? conv;
                try { conv = target.Module.ResolveMethod(BitConverter.ToInt32(il, op.OperandOffset), gta, gma); }
                catch { continue; }
                if (conv?.DeclaringType?.FullName != "System.Decimal" || conv.Name != "op_Implicit") continue;
                return ResolveIntSource(target, il, ops, j - 1, gta, gma);
            }
        }
        return null;
    }

    private static int? ResolveIntSource(MethodInfo target, byte[] il, IReadOnlyList<ParsedOp> ops, int idx,
        Type[]? gta, Type[]? gma)
    {
        for (int k = idx; k >= 0; k--)
        {
            var op = ops[k];
            if (TryReadInt(il, op, out var literal)) return literal;
            if (op.OpCode == OpCodes.Ldarg_0) continue;
            if (op.OpCode == OpCodes.Ldsfld)
            {
                FieldInfo? field;
                try { field = target.Module.ResolveField(BitConverter.ToInt32(il, op.OperandOffset), gta, gma); }
                catch { return null; }
                if (field?.IsLiteral == true && field.FieldType == typeof(int))
                    return (int?)field.GetRawConstantValue();
                return null;
            }
            if (op.OpCode == OpCodes.Call || op.OpCode == OpCodes.Callvirt)
            {
                MethodBase? method;
                try { method = target.Module.ResolveMethod(BitConverter.ToInt32(il, op.OperandOffset), gta, gma); }
                catch { return null; }
                if (method is MethodInfo mi && mi.ReturnType == typeof(int)) return ReadIntConstantGetter(mi);
                return null;
            }
            return null;
        }
        return null;
    }

    private static int? ReadIntConstantGetter(MethodInfo getter)
    {
        var bytes = getter.GetMethodBody()?.GetILAsByteArray();
        if (bytes == null || bytes.Length == 0) return null;
        var ops = WalkIL(bytes);
        if (ops.Count == 0) return null;
        return TryReadInt(bytes, ops[0], out var n) ? n : null;
    }

    private static bool TryReadInt(byte[] il, ParsedOp op, out int value)
    {
        var v = (ushort)op.OpCode.Value;
        if (v == 0x15) { value = -1; return true; }
        if (v >= 0x16 && v <= 0x1E) { value = v - 0x16; return true; }
        if (v == 0x1F) { value = (sbyte)il[op.OperandOffset]; return true; }
        if (v == 0x20) { value = BitConverter.ToInt32(il, op.OperandOffset); return true; }
        value = 0; return false;
    }

    private readonly struct ParsedOp(OpCode op, int offset)
    {
        public readonly OpCode OpCode = op;
        public readonly int OperandOffset = offset;
    }

    private static List<ParsedOp> WalkIL(byte[] il)
    {
        var result = new List<ParsedOp>(il.Length / 2);
        int ip = 0;
        while (ip < il.Length)
        {
            ushort key; int opSize;
            if (il[ip] == 0xFE && ip + 1 < il.Length) { key = (ushort)(0xFE00 | il[ip + 1]); opSize = 2; }
            else { key = il[ip]; opSize = 1; }
            if (!_opcodes.TryGetValue(key, out var oc)) break;
            int operandOff = ip + opSize;
            int operandSize = OperandSize(oc, il, operandOff);
            if (operandSize < 0 || operandOff + operandSize > il.Length) break;
            result.Add(new ParsedOp(oc, operandOff));
            ip = operandOff + operandSize;
        }
        return result;
    }

    private static int OperandSize(OpCode oc, byte[] il, int operandOff) => oc.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
            or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => operandOff + 4 > il.Length ? -1 : 4 + BitConverter.ToInt32(il, operandOff) * 4,
        _ => -1,
    };

    private static Dictionary<ushort, OpCode> BuildOpcodeMap()
    {
        var map = new Dictionary<ushort, OpCode>();
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.GetValue(null) is OpCode op) map[(ushort)op.Value] = op;
        return map;
    }

    private static void LogFail(string monsterId, string stateId, string reason)
        => LogOnce(monsterId, stateId, success: false,
            $"power-apply-extract-fail: monster={monsterId} state={stateId} reason={reason}");

    private static void LogStatusFail(string monsterId, string stateId, string reason)
        => LogOnce(monsterId, stateId, success: false,
            $"status-card-extract-fail: monster={monsterId} state={stateId} reason={reason}");

    private static void LogOnce(string monsterId, string stateId, bool success, string message)
    {
        lock (_logLock) { if (!_logged.Add((monsterId, stateId))) return; }
        if (success) ModEntry.Log(message); else ModEntry.LogWarn(message);
    }
}
