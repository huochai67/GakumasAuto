// Finds the IL2CPP native module inside the running game process.
//
// BepInEx resolves the native name "GameAssembly" to a *file path* and calls
// NativeLibrary.Load on it. Loading a second copy of the runtime is both impossible
// (the static image cannot initialize) and wrong (a fresh il2cpp runtime has no domain),
// so the shim determines which file the process already has loaded and hands BepInEx
// that exact path - the Windows loader then returns the existing module handle.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GakumasDoorstopShim
{
    internal static class Il2CppModuleProbe
    {
        private const string ProbeExport = "il2cpp_domain_get";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);

        /// <summary>Returns the file path of the loaded module that exports the il2cpp API, or null.</summary>
        internal static string FindLiveIl2CppModule()
        {
            try
            {
                foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                {
                    var name = module.ModuleName ?? string.Empty;
                    if (name.IndexOf("GameAssembly", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("il2cpp", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var handle = GetModuleHandleW(name);
                    if (handle == IntPtr.Zero) continue;

                    var export = GetProcAddress(handle, ProbeExport);
                    ShimLog.Write(string.Format(
                        "[probe] module {0} base=0x{1:X} {2}={3} path={4}",
                        name, module.BaseAddress.ToInt64(), ProbeExport, export != IntPtr.Zero ? "yes" : "NO", module.FileName));

                    if (export == IntPtr.Zero) continue;
                    if (string.IsNullOrEmpty(module.FileName) || !File.Exists(module.FileName))
                    {
                        ShimLog.Write("[probe] module exports the il2cpp API but its file is not on disk; " +
                                      "falling back to the default GameAssembly.dll path");
                        continue;
                    }
                    return module.FileName;
                }
            }
            catch (Exception e)
            {
                ShimLog.Write("[probe] module scan failed: " + e.Message);
            }
            return null;
        }
    }
}
