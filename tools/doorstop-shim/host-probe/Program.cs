// Self-test host: loads the *deployed* GakumasDoorstopShim the way doorstop does - by name, from
// a directory this process does not probe - and runs its Doorstop.Entrypoint.Start().
//
// Nothing of the shim lives next to this exe, so every LibCpp2IL / MonoMod.RuntimeDetour reference
// can only resolve through the shim's own core-directory resolver (Entrypoint's module
// initializer). That is the invariant that broke once: with the shim deployed to BepInEx\core\shim
// the CLR's default probing path (doorstop sets APP_PATHS = <corlib_dir>;<target dir>) never sees
// BepInEx\core, and JIT of a method whose callee signature mentions MonoMod would fail before a
// single line of the shim ran.
//
// usage: shim-host-probe <gameRoot> <shimDllPath> [logPath]
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

internal static class Program
{
    private static int Main(string[] args)
    {
        var gameRoot = args.Length > 0 ? args[0] : @"E:\DMM\gakumas";
        var shimPath = args.Length > 1 ? args[1] : Path.Combine(gameRoot, @"BepInEx\core\shim\GakumasDoorstopShim.dll");
        var logPath = args.Length > 2 ? args[2] : Path.Combine(AppContext.BaseDirectory, "probe-shim.log");

        Environment.SetEnvironmentVariable("DOORSTOP_PROCESS_PATH", Path.Combine(gameRoot, "gakumas.exe"));
        Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", shimPath);
        Environment.SetEnvironmentVariable("GAKUMAS_SHIM_LOG", logPath);
        // This process has no IL2CPP runtime, so BepInEx's preloader would wait for the game forever.
        Environment.SetEnvironmentVariable("GAKUMAS_SHIM_SKIP_HANDOVER", "1");

        Console.WriteLine("[probe] loading " + shimPath);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(shimPath);
        var type = assembly.GetType("Doorstop.Entrypoint", throwOnError: true);
        var start = type.GetMethod("Start", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        Console.WriteLine("[probe] invoking Doorstop.Entrypoint.Start()");

        try
        {
            start.Invoke(null, null);
        }
        catch (TargetInvocationException e)
        {
            Console.WriteLine("[probe] Start() threw: " + e.InnerException);
            return 1;
        }

        // The shim installs MonoMod detours, and MonoMod freezes its platform cache on first read.
        // BepInEx's preloader begins with PlatformUtils.SetPlatform() - if that cannot set the
        // platform (IL2CPP), the preloader aborts on a modal error dialog. Check it here instead.
        var preloaderPath = Path.Combine(gameRoot, @"BepInEx\core\BepInEx.Preloader.Core.dll");
        if (!File.Exists(preloaderPath))
        {
            Console.WriteLine("[probe] BepInEx.Preloader.Core.dll not found; skipping SetPlatform check");
            return 0;
        }

        var preloader = Assembly.LoadFrom(preloaderPath);
        var platformUtils = preloader.GetType("BepInEx.Preloader.Core.PlatformUtils", throwOnError: true);
        var setPlatform = platformUtils.GetMethod("SetPlatform",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        try
        {
            setPlatform.Invoke(null, null);
        }
        catch (TargetInvocationException e)
        {
            Console.WriteLine("[probe] PlatformUtils.SetPlatform() FAILED: " + e.InnerException);
            return 2;
        }

        Console.WriteLine("[probe] PlatformUtils.SetPlatform() OK, PlatformHelper.Current = " + CurrentPlatform());
        Console.WriteLine("[probe] Start() returned; shim log: " + logPath);
        return 0;
    }

    private static string CurrentPlatform()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != "MonoMod.Utils") continue;
            var helper = assembly.GetType("MonoMod.Utils.PlatformHelper");
            var current = helper == null ? null : helper.GetProperty("Current",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            return current == null ? "<unknown>" : Convert.ToString(current.GetValue(null));
        }
        return "<MonoMod.Utils not loaded>";
    }
}
