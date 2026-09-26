using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

namespace HotReload;

/// <summary>
/// IL2CPP games (.NET 6): each reloaded build is loaded into its own AssemblyLoadContext, because the default context
/// refuses a second assembly with the same name. Its references must resolve to the newest reloaded build of a mod or
/// library before anything else, which takes an override of AssemblyLoadContext.Load: the public Resolving event only
/// fires after the default context has already answered with the old build.
///
/// HotReload is one net472 DLL for Mono and IL2CPP, and net472 has no AssemblyLoadContext to subclass at compile time.
/// So the subclass is emitted at runtime: a constructor calling base(name, isCollectible: false) and a Load override
/// that calls <see cref="LoadContextHook.Load"/>. Everything else is reached through reflection.
/// </summary>
internal static class LoadContexts
{
    private static bool _initialized;
    private static Type? _contextType;
    private static object? _default;
    private static PropertyInfo? _assemblies;
    private static MethodInfo? _loadFromAssemblyName, _loadFromPath, _loadFromStream;

    private static string? _problem;

    /// <summary>Null when load contexts can be used, else why not. Only meaningful on IL2CPP (.NET); builds the context type on first use.</summary>
    public static string? Problem
    {
        get { Init(); return _problem; }
    }

    private static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var alc = typeof(object).Assembly.GetType("System.Runtime.Loader.AssemblyLoadContext", throwOnError: true)!;
            _default = alc.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!.GetValue(null, null);
            _assemblies = alc.GetProperty("Assemblies", BindingFlags.Public | BindingFlags.Instance)!;
            _loadFromAssemblyName = alc.GetMethod("LoadFromAssemblyName", new[] { typeof(AssemblyName) })!;
            _loadFromPath = alc.GetMethod("LoadFromAssemblyPath", new[] { typeof(string) })!;
            _loadFromStream = alc.GetMethod("LoadFromStream", new[] { typeof(Stream), typeof(Stream) })!;
            _contextType = EmitContextType(alc);
        }
        catch (Exception e)
        {
            _problem = e.GetType().Name + ": " + e.Message;
        }
    }

    private static Type EmitContextType(Type alc)
    {
        var asm = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("HotReload.LoadContexts"), AssemblyBuilderAccess.Run);
        var module = asm.DefineDynamicModule("HotReload.LoadContexts");
        var type = module.DefineType("HotReload.ModLoadContext", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, alc);

        // public ModLoadContext(string name) : base(name, false)
        var baseCtor = alc.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(bool) }, null)
                       ?? throw new MissingMethodException(alc.FullName, ".ctor(string, bool)");
        var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(string) });
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, baseCtor);
        il.Emit(OpCodes.Ret);

        // protected override Assembly Load(AssemblyName name) => LoadContextHook.Load(name);
        var baseLoad = alc.GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(AssemblyName) }, null)
                       ?? throw new MissingMethodException(alc.FullName, "Load(AssemblyName)");
        var load = type.DefineMethod("Load", MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(Assembly), new[] { typeof(AssemblyName) });
        il = load.GetILGenerator();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, typeof(LoadContextHook).GetMethod(nameof(LoadContextHook.Load))!);
        il.Emit(OpCodes.Ret);
        type.DefineMethodOverride(load, baseLoad);

        return type.CreateTypeInfo()!.AsType();
    }

    /// <summary>A new load context named <paramref name="name"/> whose references prefer the newest reloaded builds.</summary>
    public static object Create(string name)
    {
        Init();
        if (_contextType == null) throw new InvalidOperationException("AssemblyLoadContext unavailable: " + _problem);
        return Activator.CreateInstance(_contextType, "HotReload: " + name)!;
    }

    public static Assembly LoadFromPath(object context, string path) => (Assembly)Invoke(_loadFromPath!, context, path);

    public static Assembly LoadFromStream(object context, Stream dll, Stream? pdb) => (Assembly)Invoke(_loadFromStream!, context, dll, pdb);

    /// <summary>Resolution for every HotReload context: newest reloaded build, else what the default context has or can load.</summary>
    internal static Assembly? Resolve(AssemblyName assemblyName)
    {
        var n = assemblyName.Name;
        if (n == null) return null;
        if (Reloader.Latest.TryGetValue(n, out var reloaded)) return reloaded;
        if (_default == null) return null;
        foreach (var a in (IEnumerable<Assembly>)_assemblies!.GetValue(_default, null)!)
            if (string.Equals(a.GetName().Name, n, StringComparison.OrdinalIgnoreCase)) return a;
        try { return (Assembly?)_loadFromAssemblyName!.Invoke(_default, new object[] { assemblyName }); } // MelonLoader's resolvers (interop, UserLibs)
        catch { return null; }
    }

    private static object Invoke(MethodInfo m, object target, params object?[] args)
    {
        try { return m.Invoke(target, args)!; }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>Called by the emitted load context. Public, because code in a dynamic assembly may only call public members.</summary>
public static class LoadContextHook
{
    public static Assembly? Load(AssemblyName assemblyName) => LoadContexts.Resolve(assemblyName);
}
