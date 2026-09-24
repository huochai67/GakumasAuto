// Bridges BepInEx's interop manager onto the decrypted image without breaking the
// native module load that follows.
//
// BepInEx.Unity.IL2CPP uses ONE source for two very different things (verified in
// be.785 IL, BepInEx.Unity.IL2CPP/Il2CppInteropManager.cs):
//
//   * Il2CppInteropManager.GameAssemblyPath ->
//       Environment.GetEnvironmentVariable("BEPINEX_GAME_ASSEMBLY_PATH")
//       ?? Path.Combine(Paths.GameRootPath, "GameAssembly.dll")
//     is the input of Cpp2IL / interop generation AND of ComputeHash() (the
//     BepInEx/interop/assembly-hash.txt gate), and
//   * Preloader.DllImportResolver maps the native name "GameAssembly" straight to
//     NativeLibrary.Load(Il2CppInteropManager.GameAssemblyPath, ...).
//
// Pointing the path at a decrypted image therefore fixes generation but makes the
// resolver load that image as a native DLL, which cannot initialize
// (0x8007045A = ERROR_DLL_INIT_FAILED) - and even if it loaded, it would be a second
// il2cpp runtime. The resolver must keep loading the module the process already has.
//
// So we detour the manager instead of the path resolution:
//   * get_GameAssemblyPath  -> decrypted image *while generating*, runtime path otherwise
//   * GenerateInteropAssemblies -> flips that phase and rewrites assembly-hash.txt over
//     the runtime path afterwards, so the next boot sees "up to date" and skips generation.
//
// The hash recipe, reproduced from the IL and verified against a hash BepInEx itself
// wrote (see README "验证"):
//   MD5( bytes(GameAssemblyPath)
//      + for each <unity-libs>/*.dll: UTF8(fileName) + bytes(file)
//      + bytes(BepInEx/DeobfuscationMap.csv.gz) if present
//      + UTF8(Il2CppInterop.Generator assembly version)
//      + UTF8(Cpp2IL.Core assembly version) ) -> lowercase hex
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MonoMod.RuntimeDetour;

namespace GakumasDoorstopShim
{
    internal static class BepInExInteropBridge
    {
        private const string ManagerTypeName = "BepInEx.Unity.IL2CPP.Il2CppInteropManager";

        private static string _imagePath;
        private static string _runtimePath;
        private static string _defaultRuntimePath;
        private static string _hashPath;
        private static string _unityLibsDir;
        private static string _renameMapPath;
        private static string _generatorPath;
        private static string _cpp2IlPath;
        private static string _preflightCachePath;
        private static bool _imageUsable;
        private static volatile bool _generating;

        private static Hook _pathHook;
        private static Hook _generateHook;

        internal static bool HooksInstalled => _pathHook != null && _generateHook != null;

        internal static void Configure(
            string imagePath, bool imageUsable, string runtimePath, string defaultRuntimePath,
            string bepInExRoot, string coreDir)
        {
            _imagePath = imagePath;
            _imageUsable = imageUsable;
            _runtimePath = runtimePath;
            _defaultRuntimePath = defaultRuntimePath;
            _hashPath = Path.Combine(bepInExRoot, "interop", "assembly-hash.txt");
            _unityLibsDir = Path.Combine(bepInExRoot, "unity-libs");
            _renameMapPath = Path.Combine(bepInExRoot, "DeobfuscationMap.csv.gz");
            _generatorPath = Path.Combine(coreDir, "Il2CppInterop.Generator.dll");
            _cpp2IlPath = Path.Combine(coreDir, "Cpp2IL.Core.dll");
            _preflightCachePath = Path.Combine(bepInExRoot, "gakumas-shim-preflight.cache");
        }

        internal static void InstallHooks(Assembly bepInExIl2CppAssembly)
        {
            var manager = bepInExIl2CppAssembly.GetType(ManagerTypeName, throwOnError: true);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            var pathProperty = manager.GetProperty("GameAssemblyPath", flags);
            var pathGetter = pathProperty?.GetGetMethod(nonPublic: true);
            var generate = manager.GetMethod("GenerateInteropAssemblies", flags);
            if (pathGetter == null || generate == null)
                throw new MissingMethodException(ManagerTypeName, "GameAssemblyPath/GenerateInteropAssemblies");

            _pathHook = new Hook(pathGetter, new Func<Func<string>, string>(GameAssemblyPathHook));
            _generateHook = new Hook(generate, new Action<Action>(GenerateInteropAssembliesHook));
            ShimLog.Write("[shim] hooked " + ManagerTypeName + " (GameAssemblyPath, GenerateInteropAssemblies)");
        }

        /// <summary>
        /// MonoMod's PlatformHelper latches on first read: the platform value lands in
        /// <c>_current</c> and <c>_currentLocked</c> flips to true, after which set_Current throws
        /// ("Cannot set the value of PlatformHelper.Current once it has been accessed."). Installing
        /// the detours above reads it, so BepInEx's PlatformUtils.SetPlatform() - the first thing
        /// PreloaderMain() does, setting the real platform (IL2CPP) - throws and aborts the
        /// preloader, leaving the game on BepInEx's error dialog.
        ///
        /// Clearing the latch restores the "not accessed yet" state so BepInEx makes the call; the
        /// platform then gets detected/set from the real runtime instead of from what MonoMod could
        /// see before the IL2CPP runtime announced itself.
        /// </summary>
        internal static void ResetPlatformCache()
        {
            try
            {
                Assembly utils = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == "MonoMod.Utils") { utils = assembly; break; }
                }

                var helper = utils == null ? null : utils.GetType("MonoMod.Utils.PlatformHelper");
                if (helper == null)
                {
                    ShimLog.Write("[shim] MonoMod.Utils.PlatformHelper not loaded; platform cache left alone");
                    return;
                }

                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var latch = helper.GetField("_currentLocked", flags);
                if (latch == null)
                {
                    // the field name is a MonoMod implementation detail: fall back to the only
                    // static bool that is already set, which is what latched the cache
                    foreach (var candidate in helper.GetFields(flags))
                    {
                        if (candidate.FieldType != typeof(bool)) continue;
                        if (!Convert.ToBoolean(candidate.GetValue(null) ?? false)) continue;
                        latch = candidate;
                        break;
                    }
                }

                if (latch == null)
                {
                    ShimLog.Write("[shim] MonoMod platform latch not found; BepInEx's SetPlatform may still fail");
                    return;
                }

                latch.SetValue(null, false);
                ShimLog.Write("[shim] unlocked MonoMod platform cache (" + latch.Name + " = false)");
            }
            catch (Exception e)
            {
                ShimLog.Write("[shim] platform cache reset failed: " + e.Message);
            }
        }

        private static string GameAssemblyPathHook(Func<string> original)
        {
            if (_generating)
                return _imageUsable ? _imagePath : original();
            return _runtimePath ?? original();
        }

        private static void GenerateInteropAssembliesHook(Action original)
        {
            ShimLog.Write("[shim] BepInEx: interop generation step (input: " +
                          (_imageUsable ? _imagePath : "<packed GameAssembly.dll>") +
                          ") - BepInEx decides inside this call whether it regenerates");
            if (!_imageUsable)
                ShimLog.Write("[shim] WARNING: the decrypted image predates the installed build; " +
                              "generation will read the packed file and fail - refresh ImagePath");

            var stopwatch = Stopwatch.StartNew();
            _generating = true;
            try
            {
                original();
            }
            finally
            {
                _generating = false;
            }
            stopwatch.Stop();
            ShimLog.Write("[shim] BepInEx: interop generation finished in " + stopwatch.Elapsed);

            // BepInEx writes assembly-hash.txt itself, over the input it just consumed - which is the
            // decrypted image, because this hook reports it for the whole generation call. The next
            // boot runs the same check inside the same hook window, so that stored value matches and
            // generation is skipped. Rewriting it with anything else (e.g. the runtime module's hash)
            // makes every boot mismatch and regenerate, so leave the file alone.
            try
            {
                var stored = File.Exists(_hashPath) ? File.ReadAllText(_hashPath).Trim() : "<none>";
                ShimLog.Write("[shim] assembly-hash.txt after generation: " + stored);
            }
            catch (Exception e)
            {
                ShimLog.Write("[shim] cannot read " + _hashPath + ": " + e.Message);
            }
        }

        private static string RuntimePath => _runtimePath ?? _defaultRuntimePath;

        /// <summary>One informational line per boot: two MD5 passes over ~340 MB cost ~0.8 s, and the
        /// verdict is BepInEx's own business (it hashes the same input inside GenerateInteropAssemblies).
        /// Cache the two hashes keyed by (size, mtime) of both files, so unchanged files reuse them.</summary>
        private sealed class PreflightCache
        {
            internal string Identity;
            internal string ImageHash;
            internal string RuntimeHash;
            internal string Source;

            internal static PreflightCache Parse(string text)
            {
                if (string.IsNullOrEmpty(text)) return null;
                var parts = text.Trim().Split('|');
                if (parts.Length != 4) return null;
                return new PreflightCache { Identity = parts[0], ImageHash = parts[1], RuntimeHash = parts[2], Source = parts[3] };
            }

            internal string Serialize() => string.Join("|", Identity, ImageHash, RuntimeHash, Source);
        }

        private static string FileIdentity(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "<missing>";
        }

        internal static void Preflight()
        {
            if (!File.Exists(_generatorPath) || !File.Exists(_cpp2IlPath))
            {
                ShimLog.Write("[shim] interop generator assemblies missing; hash gate disabled");
                return;
            }

            string stored = null;
            try { if (File.Exists(_hashPath)) stored = File.ReadAllText(_hashPath).Trim(); }
            catch (Exception e) { ShimLog.Write("[shim] cannot read " + _hashPath + ": " + e.Message); }

            var imageExists = File.Exists(_imagePath);
            var runtimePath = RuntimePath;
            var identity = "image=" + (imageExists ? FileIdentity(_imagePath) : "<missing>") +
                           ";runtime=" + FileIdentity(runtimePath);

            string imageHash = null, runtimeHash = null, source = _imageUsable ? "image" : "packed";
            try
            {
                var cached = PreflightCache.Parse(
                    File.Exists(_preflightCachePath) ? File.ReadAllText(_preflightCachePath) : null);
                if (cached != null && cached.Identity == identity)
                {
                    imageHash = cached.ImageHash;
                    runtimeHash = cached.RuntimeHash;
                    source = cached.Source;
                    ShimLog.Write("[shim] preflight hashes reused (image and runtime module unchanged)");
                }
                else
                {
                    if (imageExists) imageHash = ComputeHash(_imagePath);
                    runtimeHash = ComputeHash(runtimePath);
                    var cache = new PreflightCache
                    {
                        Identity = identity,
                        ImageHash = imageHash ?? "<missing>",
                        RuntimeHash = runtimeHash,
                        Source = source
                    };
                    File.WriteAllText(_preflightCachePath, cache.Serialize());
                }
            }
            catch (Exception e)
            {
                ShimLog.Write("[shim] hash computation failed: " + e.Message);
                return;
            }

            ShimLog.Write("[shim] hash runtime=" + runtimeHash + " image=" + (imageHash ?? "<missing>") +
                          " stored=" + (stored ?? "<none>") + " gate(" + source + ")=" +
                          (_imageUsable ? (imageHash ?? "<unknown>") : runtimeHash));

            if (!_imageUsable || imageHash == null)
            {
                ShimLog.Write("[shim] WARNING: " + (imageExists
                    ? "the decrypted image predates the installed build"
                    : "the decrypted image is missing") +
                    "; BepInEx must regenerate and that will fail - refresh ImagePath (install.ps1 -ImagePath ...)");
                return;
            }

            var expected = _imageUsable ? imageHash : runtimeHash;
            if (stored == expected)
                ShimLog.Write("[shim] interop is up to date for this gate; BepInEx will skip generation");
            else
                ShimLog.Write("[shim] BepInEx will regenerate interop assemblies once (gate hash mismatch)");
        }

        internal static string ComputeHash(string gameAssemblyPath)
        {
            var buffer = new byte[81920];
            using var md5 = MD5.Create();
            HashFile(md5, gameAssemblyPath, buffer);

            if (Directory.Exists(_unityLibsDir))
            {
                foreach (var file in Directory.EnumerateFiles(_unityLibsDir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    HashString(md5, Path.GetFileName(file));
                    HashFile(md5, file, buffer);
                }
            }

            if (File.Exists(_renameMapPath)) HashFile(md5, _renameMapPath, buffer);

            HashString(md5, AssemblyVersion(_generatorPath));
            HashString(md5, AssemblyVersion(_cpp2IlPath));

            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return ToHexLower(md5.Hash);
        }

        private static string AssemblyVersion(string path)
        {
            // metadata-only read: do not load the generator's dependency graph into this process
            var version = AssemblyName.GetAssemblyName(path).Version;
            if (version == null) throw new InvalidDataException("no assembly version in " + path);
            return version.ToString();
        }

        private static void HashFile(HashAlgorithm algorithm, string path, byte[] buffer)
        {
            using var stream = File.OpenRead(path);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                algorithm.TransformBlock(buffer, 0, read, buffer, 0);
            }
            stream.Dispose();
        }

        private static void HashString(HashAlgorithm algorithm, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            algorithm.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
        }

        private static string ToHexLower(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }
    }
}
