// Offline self-test for the shim. Runs outside the game so the derived constants and
// the LibCpp2IL hook can be validated before anything touches the game install.
//
//   GakumasDoorstopShim --scan <image> [--cache <path>] [--expect gmpRva=0x..,gmpCount=..,cgrRva=0x..,cgmCount=..]
//   GakumasDoorstopShim --hook-test <image> <metadata> [--unity 6000.0.77f1] [--cache <path>]
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace GakumasDoorstopShim
{
    internal static class SelfTest
    {
        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("EXCEPTION: " + e);
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
            {
                Console.WriteLine("usage:");
                Console.WriteLine("  GakumasDoorstopShim --scan <image> [--cache <path>] [--expect gmpRva=0x..,gmpCount=..,cgrRva=0x..,cgmCount=..]");
                Console.WriteLine("  GakumasDoorstopShim --hook-test <image> <metadata> [--unity 6000.0.77f1] [--cache <path>] [--core <dir>] [--no-hook]");
                Console.WriteLine("  GakumasDoorstopShim --hash <gameAssemblyPath> <bepInExRoot> [--core <dir>] [--expect <hex>]");
                return 2;
            }

            ShimLog.Init(null, true);

            switch (args[0])
            {
                case "--scan":
                    return Scan(args);
                case "--hook-test":
                    return HookTest(args);
                case "--hash":
                    return Hash(args);
                case "--bridge-test":
                    return BridgeTest(args);
                default:
                    Console.Error.WriteLine("unknown mode: " + args[0]);
                    return 2;
            }
        }

        /// <summary>Reproduces BepInEx's assembly-hash.txt value for a given game assembly path,
        /// so the interop gate can be reasoned about offline.
        /// usage: --hash &lt;gameAssemblyPath&gt; &lt;bepInExRoot&gt; [&lt;coreDir&gt;] [--expect &lt;hex&gt;]</summary>
        private static int Hash(string[] args)
        {
            var gameAssembly = Arg(args, 1);
            var bepInExRoot = Arg(args, 2);
            var coreDir = OptionValue(args, "--core") ?? Path.Combine(bepInExRoot, "core");
            var expected = OptionValue(args, "--expect");

            BepInExInteropBridge.Configure(
                imagePath: gameAssembly, imageUsable: true, runtimePath: null,
                defaultRuntimePath: gameAssembly, bepInExRoot: bepInExRoot, coreDir: coreDir);

            var hash = BepInExInteropBridge.ComputeHash(gameAssembly);
            Console.WriteLine("HASH " + hash);
            if (string.IsNullOrEmpty(expected)) return 0;
            var match = string.Equals(hash, expected.Trim(), StringComparison.OrdinalIgnoreCase);
            Console.WriteLine("CHECK hash=" + (match ? "OK" : "MISMATCH") + " (expected " + expected + ")");
            return match ? 0 : 3;
        }

        private static int Scan(string[] args)
        {
            var image = Arg(args, 1);
            var cachePath = OptionValue(args, "--cache");
            var expect = OptionValue(args, "--expect");

            var constants = CodeRegScanner.GetOrScan(image, cachePath);
            Console.WriteLine("RESULT " + constants.Describe());

            if (string.IsNullOrEmpty(expect)) return 0;

            var expected = ParseExpect(expect);
            int failures = 0;
            foreach (var pair in expected)
            {
                ulong actual;
                switch (pair.Key)
                {
                    case "gmpRva": actual = constants.GenericMethodPointersRva; break;
                    case "gmpCount": actual = constants.GenericMethodPointersCount; break;
                    case "cgrRva": actual = constants.AddrCodeGenModulePtrsRva; break;
                    case "cgmCount": actual = constants.CodeGenModulesCount; break;
                    case "imageBase": actual = constants.ImageBase; break;
                    default:
                        Console.Error.WriteLine("CHECK " + pair.Key + "=UNKNOWN");
                        failures++;
                        continue;
                }
                bool ok = actual == pair.Value;
                if (!ok) failures++;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "CHECK {0}={1} (expected 0x{2:X}/{2})", pair.Key, ok ? "OK" : "MISMATCH", pair.Value));
            }
            return failures == 0 ? 0 : 3;
        }

        private static Dictionary<string, ulong> ParseExpect(string spec)
        {
            var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (var item in spec.Split(','))
            {
                var trimmed = item.Trim();
                if (trimmed.Length == 0) continue;
                int eq = trimmed.IndexOf('=');
                if (eq <= 0) throw new ArgumentException("bad --expect item: " + trimmed);
                var key = trimmed.Substring(0, eq).Trim();
                var value = trimmed.Substring(eq + 1).Trim();
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    result[key] = ulong.Parse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                else
                    result[key] = ulong.Parse(value, CultureInfo.InvariantCulture);
            }
            return result;
        }

        private static int HookTest(string[] args)
        {
            var image = Arg(args, 1);
            var metadata = Arg(args, 2);
            var unityVersionText = OptionValue(args, "--unity") ?? "6000.0.77f1";
            var cachePath = OptionValue(args, "--cache");
            var useHook = !HasFlag(args, "--no-hook");

            var coreDir = OptionValue(args, "--core") ?? ResolveCoreDirArg();
            var libCpp2IlPath = Path.Combine(coreDir, "LibCpp2IL.dll");
            if (!File.Exists(libCpp2IlPath)) throw new FileNotFoundException("LibCpp2IL.dll not found", libCpp2IlPath);

            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                var candidate = Path.Combine(coreDir, name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };

            Console.WriteLine("[test] LibCpp2IL from " + coreDir);
            Assembly.LoadFrom(Path.Combine(coreDir, "AssetRipper.Primitives.dll"));
            Assembly.LoadFrom(libCpp2IlPath);

            if (useHook)
            {
                CodeRegFallback.PreDerive(image, cachePath);
                CodeRegFallback.Install();
            }
            else
            {
                Console.WriteLine("[test] hook disabled (negative control)");
            }

            var binaryRegistryType = Type.GetType("LibCpp2IL.LibCpp2IlBinaryRegistry, LibCpp2IL", throwOnError: true);
            binaryRegistryType.GetMethod("RegisterBuiltInBinarySupport", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);

            var unityVersionType = Type.GetType("AssetRipper.Primitives.UnityVersion, AssetRipper.Primitives", throwOnError: true);
            var unityVersion = unityVersionType.GetMethod("Parse", new[] { typeof(string) }).Invoke(null, new object[] { unityVersionText });

            var mainType = Type.GetType("LibCpp2IL.LibCpp2IlMain, LibCpp2IL", throwOnError: true);
            var loadFromFile = mainType.GetMethod("LoadFromFile", new[] { typeof(string), typeof(string), unityVersionType });

            Console.WriteLine("[test] LibCpp2IL.init(" + image + ", " + metadata + ", " + unityVersionText + ")");
            try
            {
                loadFromFile.Invoke(null, new[] { image, metadata, unityVersion });
            }
            catch (TargetInvocationException e)
            {
                var inner = e.InnerException ?? e;
                Console.WriteLine("RESULT=" + (useHook ? "FAIL" : "CONTROL-FAIL-AS-EXPECTED") +
                                  " exception=" + inner.GetType().Name + ": " + inner.Message);
                Console.WriteLine("INVOCATIONS=" + CodeRegFallback.HandlerInvocations + " INJECTED=" + CodeRegFallback.Injected);
                if (useHook)
                {
                    Console.Error.WriteLine(inner);
                    return 4;
                }
                return 0; // negative control behaved as expected: without the shim LibCpp2IL cannot init
            }

            Console.WriteLine("RESULT=" + (useHook ? "OK" : "CONTROL-UNEXPECTED-SUCCESS"));
            Console.WriteLine("INVOCATIONS=" + CodeRegFallback.HandlerInvocations + " INJECTED=" + CodeRegFallback.Injected);

            try { mainType.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null); }
            catch { }

            return useHook && CodeRegFallback.Injected == 1 ? 0 : 6;
        }

        private static string ResolveCoreDirArg()
        {
            var candidates = new List<string>();
            var root = Environment.GetEnvironmentVariable("GAKUMAS_ROOT");
            if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "BepInEx", "core"));
            candidates.Add(AppContext.BaseDirectory);
            candidates.Add(@"E:\DMM\gakumas\BepInEx\core");

            foreach (var candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, "LibCpp2IL.dll"))) return candidate;
            }
            return @"E:\DMM\gakumas\BepInEx\core";
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (var arg in args)
            {
                if (arg == name) return true;
            }
            return false;
        }

        /// <summary>Loads BepInEx's IL2CPP assembly in this process, installs the interop bridge
        /// detours and proves they intercept (the hooked property must return the injected path).
        /// usage: --bridge-test &lt;coreDir&gt; &lt;image&gt; [&lt;sentinelRuntimePath&gt;]</summary>
        private static int BridgeTest(string[] args)
        {
            var coreDir = Arg(args, 1);
            var image = Arg(args, 2);
            var sentinel = args.Length > 3 ? args[3] : @"Z:\shim-sentinel\GameAssembly.dll";
            var bepInExRoot = Path.GetDirectoryName(Path.GetFullPath(coreDir));

            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                var candidate = Path.Combine(coreDir, name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };

            Assembly.LoadFrom(Path.Combine(coreDir, "LibCpp2IL.dll"));
            var bepInExAssembly = Assembly.LoadFrom(Path.Combine(coreDir, "BepInEx.Unity.IL2CPP.dll"));

            BepInExInteropBridge.Configure(
                imagePath: image, imageUsable: true, runtimePath: sentinel,
                defaultRuntimePath: sentinel, bepInExRoot: bepInExRoot, coreDir: coreDir);

            BepInExInteropBridge.InstallHooks(bepInExAssembly);
            if (!BepInExInteropBridge.HooksInstalled)
            {
                Console.WriteLine("RESULT=FAIL hooks not installed");
                return 4;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var manager = bepInExAssembly.GetType("BepInEx.Unity.IL2CPP.Il2CppInteropManager", throwOnError: true);
            var value = (string)manager.GetProperty("GameAssemblyPath", flags).GetValue(null);

            var intercepted = string.Equals(value, sentinel, StringComparison.Ordinal);
            Console.WriteLine("RESULT=" + (intercepted ? "OK" : "FAIL") + " GameAssemblyPath=" + (value ?? "<null>"));
            Console.WriteLine("HASH image=" + BepInExInteropBridge.ComputeHash(image));
            return intercepted ? 0 : 5;
        }

        private static string Arg(string[] args, int index)
        {
            if (args.Length <= index) throw new ArgumentException("missing argument #" + index);
            return args[index];
        }

        private static string OptionValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }
            return null;
        }
    }
}
