# GakumasInteropGen

离线 Il2CppInterop 生成器：从版本匹配的重建 `GameAssembly` PE、IL2CPP metadata 和 Unity 引用程序集生成 BepInEx interop。

## What it needs

The on-disk `gakumas.exe` / `GameAssembly.dll` is packed. This tool does **not** take the packed binary. It accepts a loader-decrypted or fully rebuilt PE image. In the reverse case inspected alongside this repository, the validated inputs are:

1. `gameassembly\GameAssembly_decrypted.dll` — runtime-image rebuild, ImageBase `0x7FFC08F00000`.
2. `gameassembly\GameAssembly_static_exact.dll` — complete static analysis PE, ImageBase `0x180000000`.
3. `gakumas_Data\il2cpp_data\Metadata\global-metadata.dat`.
4. `BepInEx\unity-libs\` (Unity 6 managed stubs).

The generator reads the input PE ImageBase and applies the version-specific fallback addresses as RVAs, so both validated PE layouts work. `tools\ga-static-decrypt` can now produce the complete `GameAssembly_static_exact.dll` when given the matching version profile.

The stage-2 constants and profile hashes are version-specific. After a game update they must be refreshed from a newly validated reverse case.

## Build

需要 .NET 8 SDK，以及目标游戏安装目录中的 BepInEx core 程序集。`GakumasRoot` 可通过 `GAKUMAS_ROOT` 设置，默认值为 `E:\DMM\gakumas`。

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build tools\interop-gen\GakumasInteropGen.csproj -c Release
```

生成器直接引用 `$(GakumasRoot)\BepInEx\core\` 下的 LibCpp2IL、Il2CppInterop、AsmResolver 等程序集，不从 NuGet 下载运行时依赖。

## Run

The packed `E:\DMM\gakumas\GameAssembly.dll` cannot be passed directly. First run `tools\ga-static-decrypt` with the matching profile, then pass its complete `GameAssembly_static_exact.dll` output to this generator.

```powershell
$profile = "<reverse-case>\gameassembly"
dotnet run --project tools\ga-static-decrypt\ga-static-decrypt.csproj -c Release -- `
  "$env:GAKUMAS_ROOT\GameAssembly.dll" `
  tools\ga-static-decrypt\out\GameAssembly_static_exact.dll `
  $profile
```

The output is an analysis PE, not a loadable replacement DLL. If the decryptor fails its profile hash, stage-2 signature, record-count, or stream-boundary checks, stop and refresh the version profile.

```text
GakumasInteropGen <gameRoot> <binaryDump> <metadataPath> <outDir> <unityLibsDir>
```

Example:

```powershell
$binary = "tools\ga-static-decrypt\out\GameAssembly_static_exact.dll"
dotnet run --project tools\interop-gen\GakumasInteropGen.csproj -c Release -- `
  $env:GAKUMAS_ROOT `
  $binary `
  "$env:GAKUMAS_ROOT\gakumas_Data\il2cpp_data\Metadata\global-metadata.dat" `
  "$env:GAKUMAS_ROOT\BepInEx\interop" `
  "$env:GAKUMAS_ROOT\BepInEx\unity-libs"
```

Generation runs in a staging directory. The existing `outDir` is replaced only after at least one interop DLL is produced; a failed run leaves the existing interop directory untouched.

After a successful run, write the 32 hexadecimal characters of the MD5 (16 bytes, no trailing newline) into `BepInEx\interop\assembly-hash.txt`, and keep `UpdateInteropAssemblies = false` in `BepInEx.cfg`.
