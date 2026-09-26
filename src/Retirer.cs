using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using MelonLoader;

namespace HotReload;

/// <summary>
/// "Retires" an old build after a reload: every method of it that the game or the runtime can still call on its own is
/// patched with a prefix that skips the body and returns the default value. That covers
///  - methods turned into delegates (found by scanning the IL for ldftn/ldvirtftn): lambdas, event handlers,
///    UI listeners, timer and thread-pool callbacks, Il2Cpp delegates handed to the game;
///  - iterator and async state machines (MoveNext): coroutines end on their next step, pending async work stops.
/// Stale behaviour stops instead of running old code against state that is gone. A method that is executing at that
/// moment (e.g. a loop on a background thread) finishes its current call.
/// </summary>
internal static class Retirer
{
    // Created on first use: method discovery must not initialize Harmony (it also runs in the offline test harness).
    private static HarmonyLib.Harmony? _harmony;
    private static HarmonyLib.HarmonyMethod? _skipPrefix;
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    // Harmony: returning false skips the original; __result keeps its default (null / 0 / false).
    private static bool Skip() => false;

    public static (int retired, int failed, long ms) Retire(Assembly asm, IEnumerable<Type> wholeTypes, MelonLogger.Instance log)
    {
        var sw = Stopwatch.StartNew();
        _harmony ??= new HarmonyLib.Harmony("HotReload.retire");
        _skipPrefix ??= new HarmonyLib.HarmonyMethod(typeof(Retirer).GetMethod(nameof(Skip), BindingFlags.Static | BindingFlags.NonPublic));
        int ok = 0, failed = 0;
        foreach (var m in ReachableMethods(asm, wholeTypes))
        {
            try { _harmony.Patch(m, prefix: _skipPrefix); ok++; }
            catch (Exception e)
            {
                failed++;
                if (failed <= 3) log.Warning("Could not retire " + m.DeclaringType?.FullName + "." + m.Name + ": " + e.Message);
            }
        }
        return (ok, failed, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Methods of <paramref name="asm"/> that outside code can still invoke after the melons are unregistered. Every
    /// method of <paramref name="wholeTypes"/> is included: old injected Il2Cpp classes, whose Unity messages (Update,
    /// OnGUI, ...) the game calls directly until their instances are destroyed at the end of the frame.
    /// </summary>
    internal static IEnumerable<MethodInfo> ReachableMethods(Assembly asm, IEnumerable<Type>? wholeTypes = null)
    {
        var result = new HashSet<MethodInfo>();
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var t in wholeTypes ?? Array.Empty<Type>())
            foreach (var m in t.GetMethods(declared))
                Add(result, m);
        foreach (var type in SafeTypes(asm))
        {
            // Iterator / async state machines: coroutines and pending async continuations.
            if (typeof(System.Collections.IEnumerator).IsAssignableFrom(type) || typeof(IAsyncStateMachine).IsAssignableFrom(type))
            {
                var moveNext = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (moveNext != null && moveNext.DeclaringType == type) Add(result, moveNext);
            }

            // Delegate targets: every ldftn / ldvirtftn in every method body of the assembly.
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                foreach (var target in DelegateTargets(method))
                    if (target is MethodInfo mi && mi.DeclaringType?.Assembly == asm) Add(result, mi);
            }
        }
        return result;
    }

    private static void Add(HashSet<MethodInfo> set, MethodInfo m)
    {
        // Open generics cannot be patched; abstract/extern methods have no body.
        if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType == null || m.DeclaringType.ContainsGenericParameters) return;
        if (m.GetMethodBody() == null) return;
        set.Add(m);
    }

    private static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
    }

    /// <summary>Walks the IL of <paramref name="method"/> and resolves the operand of every ldftn/ldvirtftn.</summary>
    private static IEnumerable<MethodBase> DelegateTargets(MethodBase method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { yield break; }
        if (il == null) yield break;

        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        Type[]? methodArgs = method is MethodInfo { IsGenericMethod: true } gm ? gm.GetGenericArguments() : null;

        int i = 0;
        while (i < il.Length)
        {
            short value = il[i] == 0xFE && i + 1 < il.Length ? (short)(0xFE00 | il[i + 1]) : il[i];
            i += value > 0xFF || value < 0 ? 2 : 1;
            if (!OpCodesByValue.TryGetValue(value, out var op)) yield break; // unknown opcode: stop rather than misread

            if ((op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn) && i + 4 <= il.Length)
            {
                MethodBase? target = null;
                try { target = method.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs); }
                catch { /* token into a generic context we cannot resolve */ }
                if (target != null) yield return target;
            }
            i += OperandSize(op, il, i);
        }
    }

    private static int OperandSize(OpCode op, byte[] il, int at) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * (at + 4 <= il.Length ? BitConverter.ToInt32(il, at) : 0),
        _ => 4,
    };
}
