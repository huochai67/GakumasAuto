// Runtime fallback for the packed gakumas image.
//
// LibCpp2IL cannot locate Il2CppCodeRegistration in a packed (static-encrypted)
// GameAssembly image; BepInEx's Il2CppInterop generator dies there. This shim
// subscribes to LibCpp2IL's hook point and injects a hand-built
// Il2CppCodeRegistration whose constants were *derived by scanning the decrypted
// image* (CodeRegScanner), so no per-build hardcoded RVA is required.
using System;
using System.Threading.Tasks;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;

namespace GakumasDoorstopShim
{
    internal static class CodeRegFallback
    {
        private static Task<ImageConstants> _derivation;
        private static string _imagePath;
        private static string _cachePath;
        private static int _handlerInvocations;
        private static int _injected;

        internal static int HandlerInvocations => _handlerInvocations;
        internal static int Injected => _injected;

        /// <summary>Starts the (cached) scan in the background so constants are ready by the time
        /// BepInEx runs its interop generator. Never throws.</summary>
        internal static void PreDerive(string imagePath, string cachePath)
        {
            _imagePath = imagePath;
            _cachePath = cachePath;
            _derivation = Task.Run(() =>
            {
                try
                {
                    var constants = CodeRegScanner.GetOrScan(imagePath, cachePath);
                    ShimLog.Write("[scan] " + constants.Describe());
                    return constants;
                }
                catch (Exception e)
                {
                    ShimLog.Write("[scan] derivation failed: " + e.Message);
                    throw;
                }
            });
        }

        internal static void Install()
        {
            Il2CppBinary.OnRegistrationStructLocationFailure += OnRegistrationStructLocationFailure;
            ShimLog.Write("[shim] codereg fallback hook installed");
        }

        internal static ImageConstants WaitForConstants()
        {
            if (_derivation == null) throw new InvalidOperationException("PreDerive was not called");
            try
            {
                return _derivation.GetAwaiter().GetResult();
            }
            catch
            {
                // one synchronous retry: the background attempt may have raced a partially
                // written image; a real failure will throw again and be reported.
                ShimLog.Write("[scan] background derivation failed; retrying synchronously");
                return CodeRegScanner.GetOrScan(_imagePath, _cachePath);
            }
        }

        // Signature must match LibCpp2IL.LibCpp2IL.BinaryStructures... -> see tools/interop-gen/Program.cs
        private static void OnRegistrationStructLocationFailure(
            Il2CppBinary binary,
            LibCpp2IL.Metadata.Il2CppMetadata metadata,
            ref Il2CppCodeRegistration codeReg,
            ref Il2CppMetadataRegistration metaReg)
        {
            System.Threading.Interlocked.Increment(ref _handlerInvocations);
            if (codeReg != null) return;

            ImageConstants c;
            try
            {
                c = WaitForConstants();
            }
            catch (Exception e)
            {
                ShimLog.Write("[shim] codereg fallback: no constants, giving up: " + e.Message);
                throw;
            }

            ulong genericMethodPointers = c.ImageBase + c.GenericMethodPointersRva;
            ulong addrCodeGenModulePtrs = c.ImageBase + c.AddrCodeGenModulePtrsRva;

            ShimLog.Write("[shim] injecting Il2CppCodeRegistration: " + c.Describe());
            ShimLog.Write(string.Format(
                "[shim]   genericMethodPointersCount={0} ptr=0x{1:X} / codeGenModulesCount={2} addr=0x{3:X}",
                c.GenericMethodPointersCount, genericMethodPointers, c.CodeGenModulesCount, addrCodeGenModulePtrs));

            codeReg = new Il2CppCodeRegistration
            {
                reversePInvokeWrapperCount = 0,
                reversePInvokeWrappers = 0,
                genericMethodPointersCount = c.GenericMethodPointersCount,
                genericMethodPointers = genericMethodPointers,
                genericAdjustorThunks = 0,
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
                codeGenModulesCount = c.CodeGenModulesCount,
                addrCodeGenModulePtrs = addrCodeGenModulePtrs,
            };

            System.Threading.Interlocked.Increment(ref _injected);
        }
    }
}
