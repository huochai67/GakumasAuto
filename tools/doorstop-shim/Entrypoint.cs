// Doorstop entry point for gakumas.
//
// doorstop_config.ini targets this assembly; doorstop invokes the method below on a CLR it
// hosts itself (no .NET/Mono required in the game directory). We then
//   1. pin LibCpp2IL to BepInEx's own copy and install the Il2CppCodeRegistration fallback
//      (the packed/decrypted image defeats LibCpp2IL's own registration-struct search),
//   2. find the IL2CPP native module the game already loaded (the path BepInEx must hand to
//      NativeLibrary.Load),
//   3. bridge BepInEx's interop manager so that generation reads the decrypted image while
//      everything else keeps using the live module path (BepInExInteropBridge),
//   4. hand over to BepInEx's real Doorstop.Entrypoint in BepInEx.Unity.IL2CPP.dll, so BepInEx
//      regenerates BepInEx\interop at runtime exactly like it would for an unpacked game.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using GakumasDoorstopShim;

namespace Doorstop
{
    public static class Entrypoint
    {
        private const string LibCpp2IlFileName = "LibCpp2IL.dll";
        private const string BepInExEntryFileName = "BepInEx.Unity.IL2CPP.dll";
        private const string ConfigFileName = "GakumasDoorstopShim.cfg";
        private const string DefaultCacheFileName = "gakumas-shim-codereg.cache";
        private const string DefaultImageName = "GameAssembly.dll";

        private static string _coreDir;
        private static Assembly _bepInExAssembly;

        /// <summary>
        /// Installs the core-directory assembly resolver. Called from the module initializer so it
        /// runs before any method of this assembly is JIT-compiled: the shim may be deployed
        /// outside <c>BepInEx\core</c> (see install.ps1), and CoreCLR's default probing path then
        /// cannot see <c>LibCpp2IL.dll</c> / <c>MonoMod.RuntimeDetour.dll</c>. JIT of a method whose
        /// callee signature mentions one of those types would otherwise fail with
        /// FileNotFoundException before a single line of this assembly ran.
        /// </summary>
        internal static void RegisterCoreResolver()
        {
            try
            {
                _coreDir = ResolveCoreDir(ResolveGameRoot());
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromCore;
            }
            catch (Exception e)
            {
                ShimLog.Write("[shim] core resolver not installed: " + e.Message);
            }
        }

        public static void Start()
        {
            string gameRoot = null;
            try { gameRoot = ResolveGameRoot(); } catch { }
            try { _coreDir = ResolveCoreDir(gameRoot); } catch { }

            var bepInExRoot = _coreDir != null ? Path.GetDirectoryName(_coreDir) : null;
            var logPath = Environment.GetEnvironmentVariable("GAKUMAS_SHIM_LOG");
            if (string.IsNullOrEmpty(logPath))
                logPath = bepInExRoot != null ? Path.Combine(bepInExRoot, "gakumas-shim.log") : "gakumas-shim.log";
            try { ShimLog.Init(Path.GetFullPath(logPath), true); } catch { }

            ShimLog.Write("[shim] start: gameRoot=" + (gameRoot ?? "<unknown>") + " core=" + (_coreDir ?? "<unknown>"));
            ShimLog.Write("[shim] base dir=" + AppContext.BaseDirectory + " runtime=" + Environment.Version);

            try { Bootstrap(gameRoot, bepInExRoot); }
            catch (Exception e) { ShimLog.Write("[shim] bootstrap FAILED: " + e); }

            // BepInEx's preloader opens with PlatformUtils.SetPlatform(); detour installation above
            // froze MonoMod's platform cache, so hand the decision back before the handover.
            BepInExInteropBridge.ResetPlatformCache();

            // Self-test escape hatch: in a host that has no IL2CPP runtime, BepInEx's preloader
            // would block forever waiting for the game, so probes stop right before the handover.
            if (Environment.GetEnvironmentVariable("GAKUMAS_SHIM_SKIP_HANDOVER") == "1")
            {
                ShimLog.Write("[shim] handover skipped (GAKUMAS_SHIM_SKIP_HANDOVER=1)");
                return;
            }

            try { HandOver(); }
            catch (Exception e) { ShimLog.Write("[shim] handover FAILED: " + e); }
        }

        private static void Bootstrap(string gameRoot, string bepInExRoot)
        {
            if (_coreDir == null) throw new InvalidOperationException("BepInEx core directory not found");
            if (bepInExRoot == null) throw new InvalidOperationException("BepInEx root directory not found");

            // Pin LibCpp2IL now: referencing the type loads BepInEx's own copy and keeps the
            // unqualified "LibCpp2IL" resolution away from any stale copy in the plugin dir.
            var libCpp2Il = Assembly.LoadFrom(Path.Combine(_coreDir, LibCpp2IlFileName));
            ShimLog.Write("[shim] LibCpp2IL pinned: " + libCpp2Il.Location);

            var cfg = ShimConfig.Load(Path.Combine(_coreDir, ConfigFileName));
            var gameAssemblyPath = Path.GetFullPath(Path.Combine(gameRoot ?? ".", DefaultImageName));
            var imagePath = ResolveImage(gameRoot, cfg);
            ShimLog.Write("[shim] image: " + imagePath);
            ShimLog.Write("[shim] image check: " + DescribeImage(imagePath));
            ShimLog.Write("[shim] packed module: " + gameAssemblyPath + " (" + DescribeImage(gameAssemblyPath) + ")");

            var imageUsable = IsImageNewerThanBuild(imagePath, gameAssemblyPath);
            if (!imageUsable)
                ShimLog.Write("[shim] WARNING: image is older than " + DefaultImageName +
                              "; BepInEx would generate from a stale image, so generation will use the packed file instead");

            // The native name "GameAssembly" is resolved to a *path* by BepInEx; loading a second
            // copy of the runtime is impossible and wrong, so point it at the already-loaded module.
            var runtimePath = Il2CppModuleProbe.FindLiveIl2CppModule() ?? cfg.RuntimePath;
            if (runtimePath == null)
                ShimLog.Write("[shim] live IL2CPP module not identified; BepInEx will fall back to " + gameAssemblyPath);
            else
                ShimLog.Write("[shim] live IL2CPP module path: " + runtimePath);

            // A user-set override would make the resolver load the decrypted image (and fail).
            var inherited = Environment.GetEnvironmentVariable("BEPINEX_GAME_ASSEMBLY_PATH");
            if (!string.IsNullOrEmpty(inherited))
            {
                Environment.SetEnvironmentVariable("BEPINEX_GAME_ASSEMBLY_PATH", null);
                ShimLog.Write("[shim] cleared inherited BEPINEX_GAME_ASSEMBLY_PATH (" + inherited + ")");
            }

            var cachePath = cfg.CachePath;
            if (string.IsNullOrEmpty(cachePath))
                cachePath = Path.Combine(bepInExRoot, DefaultCacheFileName);
            else if (!Path.IsPathRooted(cachePath) && gameRoot != null)
                cachePath = Path.Combine(gameRoot, cachePath);

            CodeRegFallback.PreDerive(imagePath, cachePath);
            CodeRegFallback.Install();

            _bepInExAssembly = Assembly.LoadFrom(Path.Combine(_coreDir, BepInExEntryFileName));
            ShimLog.Write("[shim] loaded " + _bepInExAssembly.FullName);

            BepInExInteropBridge.Configure(
                imagePath, imageUsable, runtimePath, gameAssemblyPath, bepInExRoot, _coreDir);
            BepInExInteropBridge.Preflight();

            try
            {
                BepInExInteropBridge.InstallHooks(_bepInExAssembly);
            }
            catch (Exception e)
            {
                ShimLog.Write("[shim] interop bridge unavailable: " + e.Message);
                // Without the bridge BepInEx can only generate from the packed file; hand it the
                // decrypted image anyway so generation (and only generation) has a chance.
                Environment.SetEnvironmentVariable("BEPINEX_GAME_ASSEMBLY_PATH", imagePath);
                ShimLog.Write("[shim] fallback: BEPINEX_GAME_ASSEMBLY_PATH=" + imagePath +
                              " (the native module load will fail after generation)");
            }
        }

        private static string ResolveImage(string gameRoot, ShimConfig cfg)
        {
            var configured = cfg.ImagePath;
            if (!string.IsNullOrEmpty(configured))
            {
                var path = Path.IsPathRooted(configured) || gameRoot == null
                    ? configured
                    : Path.Combine(gameRoot, configured);
                path = Path.GetFullPath(path);
                if (File.Exists(path)) return path;
                ShimLog.Write("[shim] configured image not found: " + path);
            }

            ShimLog.Write("[shim] no ImagePath configured; generation will read the packed image");
            return Path.GetFullPath(Path.Combine(gameRoot ?? ".", DefaultImageName));
        }

        private static bool IsImageNewerThanBuild(string imagePath, string gameAssemblyPath)
        {
            try
            {
                if (!File.Exists(imagePath)) return false;
                if (!File.Exists(gameAssemblyPath)) return true;
                return File.GetLastWriteTimeUtc(imagePath) >= File.GetLastWriteTimeUtc(gameAssemblyPath);
            }
            catch
            {
                return false;
            }
        }

        private static string DescribeImage(string imagePath)
        {
            try
            {
                if (!File.Exists(imagePath)) return "MISSING";
                using var fs = File.OpenRead(imagePath);
                var header = new byte[0x40];
                int read = fs.Read(header, 0, header.Length);
                if (read < 0x40) return "TRUNCATED (" + read + " bytes)";
                if (header[0] != 'M' || header[1] != 'Z') return "NOT A PE IMAGE (packed?)";
                int peOffset = BitConverter.ToInt32(header, 0x3C);
                fs.Position = peOffset;
                var sig = new byte[4];
                if (fs.Read(sig, 0, 4) != 4 || sig[0] != 'P' || sig[1] != 'E') return "BAD PE SIGNATURE";
                return "ok (" + new FileInfo(imagePath).Length + " bytes, " + File.GetLastWriteTimeUtc(imagePath).ToString("u") + ")";
            }
            catch (Exception e)
            {
                return "UNREADABLE: " + e.Message;
            }
        }

        private static void HandOver()
        {
            if (_coreDir == null) throw new InvalidOperationException("BepInEx core directory not found");

            var assembly = _bepInExAssembly;
            if (assembly == null)
            {
                var entryPath = Path.Combine(_coreDir, BepInExEntryFileName);
                if (!File.Exists(entryPath))
                    throw new FileNotFoundException("BepInEx IL2CPP entry assembly not found", entryPath);
                assembly = Assembly.LoadFrom(entryPath);
            }

            var type = assembly.GetType("Doorstop.Entrypoint", throwOnError: true);
            var method = type.GetMethod("Start", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(type.FullName, "Start");

            ShimLog.Write("[shim] handing over to " + assembly.GetName().Name + "!" + type.FullName + ".Start()");
            method.Invoke(null, null);
            ShimLog.Write("[shim] handover returned");
        }

        private static Assembly ResolveFromCore(object sender, ResolveEventArgs args)
        {
            try
            {
                var simpleName = new AssemblyName(args.Name).Name;
                foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                        return loaded;
                }

                var coreDir = _coreDir;
                if (string.IsNullOrEmpty(coreDir))
                {
                    coreDir = _coreDir = ResolveCoreDir(ResolveGameRoot());
                }

                var candidate = Path.Combine(coreDir, simpleName + ".dll");
                if (File.Exists(candidate))
                {
                    ShimLog.Write("[shim] resolving " + simpleName + " from core");
                    return Assembly.LoadFrom(candidate);
                }
            }
            catch (Exception e)
            {
                ShimLog.Write("[shim] resolve failed for " + args.Name + ": " + e.Message);
            }
            return null;
        }

        private static string ResolveGameRoot()
        {
            var doorstopProcessPath = Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH");
            if (!string.IsNullOrEmpty(doorstopProcessPath) && File.Exists(doorstopProcessPath))
                return Path.GetDirectoryName(Path.GetFullPath(doorstopProcessPath));

            var mainModule = Process.GetCurrentProcess().MainModule;
            if (mainModule != null && !string.IsNullOrEmpty(mainModule.FileName))
                return Path.GetDirectoryName(Path.GetFullPath(mainModule.FileName));

            return AppContext.BaseDirectory;
        }

        private static string ResolveCoreDir(string gameRoot)
        {
            // BepInEx's core directory is <gameRoot>\BepInEx\core no matter where this shim is
            // deployed (it may live in BepInEx\core\shim when core itself is held mapped by a
            // scanner). Only fall back to the doorstop-provided target path when the canonical
            // directory is absent.
            var canonical = Path.Combine(gameRoot ?? AppContext.BaseDirectory, "BepInEx", "core");
            if (Directory.Exists(canonical) && File.Exists(Path.Combine(canonical, BepInExEntryFileName)))
                return canonical;

            var target = Environment.GetEnvironmentVariable("DOORSTOP_TARGET_ASSEMBLY");
            if (!string.IsNullOrEmpty(target))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(target));
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }

            return canonical;
        }
    }

    /// <summary>
    /// Runs on assembly load, before any other method of this module is JIT-compiled, so the
    /// core-directory resolver is in place no matter where doorstop loaded the shim from.
    /// </summary>
    internal static class ModuleInit
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Init() => Entrypoint.RegisterCoreResolver();
    }
}
