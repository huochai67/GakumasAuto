// Offline Il2CppInterop generator for packed gakumas GameAssembly dumps.
// Usage: GakumasInteropGen <gameRoot> <binaryDump> <metadataPath> <outDir> <unityLibsDir>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using Il2CppInterop.Common;
using Il2CppInterop.Generator;
using Il2CppInterop.Generator.Runners;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using Microsoft.Extensions.Logging;

internal class ConsoleLogger : ILogger
{
    public IDisposable BeginScope<TState>(TState state) => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        => Console.WriteLine("[InteropGen] " + formatter(state, exception));
}

internal static class Program
{
    // Version-specific RVAs from the validated 2026-08 runtime dump.
    // Store RVAs, not absolute VAs, so runtime dumps and analysis PE images work.
    internal static ulong FallbackImageBase;
    internal static ulong GenericMethodPointersCount = 496142;
    internal const ulong GenericMethodPointersRva = 0x90B56E0UL;
    internal const ulong GenericAdjustorThunksRva = 0;
    internal static ulong CodeGenModulesCount = 185;
    internal const ulong AddrCodeGenModulePtrsRva = 0xA6A2140UL;

    static ulong ReadPeImageBase(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        fs.Position = 0x3C;
        var peOffset = br.ReadInt32();
        fs.Position = peOffset + 24 + 24;
        return br.ReadUInt64();
    }

    static void WireCpp2IlLogging()
    {
        Logger.InfoLog += (msg, src) => Console.WriteLine($"[Cpp2IL/{src}] {msg.Trim()}");
        Logger.WarningLog += (msg, src) => Console.WriteLine($"[Cpp2IL/{src}] WARN {msg.Trim()}");
        Logger.ErrorLog += (msg, src) => Console.WriteLine($"[Cpp2IL/{src}] ERROR {msg.Trim()}");
        Logger.VerboseLog += (msg, src) => Console.WriteLine($"[Cpp2IL/{src}] {msg.Trim()}");
    }

    static void InjectCodeRegFallback()
    {
        Il2CppBinary.OnRegistrationStructLocationFailure += OnRegistrationStructLocationFailure;
    }

    static void OnRegistrationStructLocationFailure(
        Il2CppBinary binary,
        LibCpp2IL.Metadata.Il2CppMetadata metadata,
        ref Il2CppCodeRegistration codeReg,
        ref Il2CppMetadataRegistration metaReg)
    {
        if (codeReg != null) return;
        var genericMethodPointers = FallbackImageBase + GenericMethodPointersRva;
        var genericAdjustorThunks = GenericAdjustorThunksRva == 0 ? 0UL : FallbackImageBase + GenericAdjustorThunksRva;
        var addrCodeGenModulePtrs = FallbackImageBase + AddrCodeGenModulePtrsRva;
        Console.WriteLine("[gen] fallback: injecting hand-built Il2CppCodeRegistration");
        Console.WriteLine($"[gen]   imageBase=0x{FallbackImageBase:X}");
        Console.WriteLine($"[gen]   genericMethodPointersCount={GenericMethodPointersCount} ptr=0x{genericMethodPointers:X} adjustor=0x{genericAdjustorThunks:X}");
        Console.WriteLine($"[gen]   codeGenModulesCount={CodeGenModulesCount} addr=0x{addrCodeGenModulePtrs:X}");
        codeReg = new Il2CppCodeRegistration
        {
            reversePInvokeWrapperCount = 0,
            reversePInvokeWrappers = 0,
            genericMethodPointersCount = GenericMethodPointersCount,
            genericMethodPointers = genericMethodPointers,
            genericAdjustorThunks = genericAdjustorThunks,
            invokerPointersCount = 0,
            invokerPointers = 0,
            unresolvedVirtualCallCount = 0,
            unresolvedVirtualCallPointers = 0,
            unresolvedInstanceCallPointers = 0,
            unresolvedStaticCallPointers = 0,
            interopDataCount = 0,
            interopData = 0,
            windowsRuntimeFactoryCount = 0,
            windowsRuntimeFactoryTable = 0,
            codeGenModulesCount = CodeGenModulesCount,
            addrCodeGenModulePtrs = addrCodeGenModulePtrs,
        };
    }

    static int Main(string[] args)
    {
        if (args.Length < 5 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("usage: GakumasInteropGen <gameRoot> <binaryDump> <metadataPath> <outDir> <unityLibsDir>");
            return args.Length > 0 ? 0 : 2;
        }

        var gameRoot = Path.GetFullPath(args[0]);
        var binary = Path.GetFullPath(args[1]);
        var metadata = Path.GetFullPath(args[2]);
        var outDir = Path.GetFullPath(args[3]);
        var unityLibs = Path.GetFullPath(args[4]);

        Console.WriteLine("[gen] gameRoot: " + gameRoot);
        Console.WriteLine("[gen] binary  : " + binary);
        Console.WriteLine("[gen] metadata: " + metadata);
        Console.WriteLine("[gen] outDir  : " + outDir);
        Console.WriteLine("[gen] unityLibs: " + unityLibs);

        if (!Directory.Exists(gameRoot))
        {
            Console.WriteLine("[gen] game root not found");
            return 3;
        }
        if (!File.Exists(binary))
        {
            Console.WriteLine("[gen] binary dump not found");
            return 3;
        }
        if (!File.Exists(metadata))
        {
            Console.WriteLine("[gen] metadata not found");
            return 3;
        }
        if (!Directory.Exists(unityLibs))
        {
            Console.WriteLine("[gen] Unity base-libs directory not found");
            return 3;
        }

        var parent = Directory.GetParent(outDir)?.FullName;
        if (string.IsNullOrEmpty(parent))
        {
            Console.WriteLine("[gen] output directory must have a parent directory");
            return 3;
        }
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, "." + Path.GetFileName(outDir) + ".interopgen-" + Guid.NewGuid().ToString("N"));
        var committed = false;

        try
        {
            Directory.CreateDirectory(staging);
            FallbackImageBase = ReadPeImageBase(binary);
            Console.WriteLine($"[gen] input imageBase: 0x{FallbackImageBase:X}");
            WireCpp2IlLogging();
            InjectCodeRegFallback();
            InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64);
            LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();

            var unityVersion = UnityVersion.Parse("6000.0.77f1");
            Console.WriteLine($"[gen] unity version: {unityVersion}");
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, unityVersion, false);
            Console.WriteLine("[gen] LibCpp2IL initialized; parsing done");

            var layers = new List<Cpp2IlProcessingLayer> { new AttributeInjectorProcessingLayer() };
            foreach (var layer in layers)
                layer.PreProcess(Cpp2IlApi.CurrentAppContext, layers);
            foreach (var layer in layers)
                layer.Process(Cpp2IlApi.CurrentAppContext, null);

            var dummy = new AsmResolverDllOutputFormatDefault().BuildAssemblies(Cpp2IlApi.CurrentAppContext);
            Console.WriteLine($"[gen] Cpp2IL produced {dummy.Count} dummy assemblies");

            LibCpp2IlMain.Reset();
            Cpp2IlApi.CurrentAppContext = null;

            var opts = new GeneratorOptions
            {
                GameAssemblyPath = null,
                Source = dummy,
                OutputDir = staging,
                UnityBaseLibsDir = unityLibs,
            };
            var logger = new ConsoleLogger();
            Console.WriteLine("[gen] running Il2CppInterop.Generator ...");
            Il2CppInteropGenerator.Create(opts)
                .AddLogger(logger)
                .AddInteropAssemblyGenerator()
                .Run();

            var generated = Directory.EnumerateFiles(staging, "*.dll", SearchOption.TopDirectoryOnly).ToArray();
            if (generated.Length == 0)
                throw new InvalidOperationException("generator produced no interop assemblies");

            CommitOutput(staging, outDir);
            committed = true;
            Console.WriteLine($"[gen] DONE: {generated.Length} interop assemblies in {outDir}");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("[gen] ERROR: " + e.Message);
            if (e.Message.Contains("Fatal Exception initializing LibCpp2IL", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("[gen] HINT: use a loader-decrypted process dump or rebuilt PE; the packed on-disk GameAssembly.dll is not a valid input");
            return 1;
        }
        finally
        {
            try
            {
                LibCpp2IlMain.Reset();
                Cpp2IlApi.CurrentAppContext = null;
            }
            catch { }

            if (!committed && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, true); }
                catch (Exception e) { Console.WriteLine("[gen] warning: staging cleanup failed: " + e.Message); }
            }
        }
    }

    static void CommitOutput(string staging, string outDir)
    {
        var backup = outDir + ".interopgen-backup-" + Guid.NewGuid().ToString("N");
        var movedExisting = false;
        try
        {
            if (Directory.Exists(outDir))
            {
                Directory.Move(outDir, backup);
                movedExisting = true;
            }

            Directory.Move(staging, outDir);
        }
        catch
        {
            if (movedExisting && !Directory.Exists(outDir) && Directory.Exists(backup))
            {
                try { Directory.Move(backup, outDir); }
                catch { }
            }
            throw;
        }

        if (movedExisting)
        {
            try { Directory.Delete(backup, true); }
            catch (Exception e) { Console.WriteLine("[gen] warning: old interop backup kept at " + backup + ": " + e.Message); }
        }
    }
}
