# ga-static-decrypt

Complete offline unpacker for the validated gakumas `GameAssembly.dll` KONN build. It performs stage 1, stage-2 self-decryption, all 47,706 body records, stream decoding, and original six-section PE reconstruction. No running game or plaintext reference dump is required.

## Build

需要 .NET 8 SDK；profile 中的 `helper_fn.bin` 是当前版本的 Windows x64 native helper。工具本身不下载或生成游戏派生 profile。

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build tools\ga-static-decrypt\ga-static-decrypt.csproj -c Release
```

## Profile

The body decoder is version-specific and requires five artifacts extracted during the reverse-engineering case:

```text
helper_tab.bin
helper_fn.bin
ga_clean_key_pid13208.bin
ga_pass3tab_from_initA.bin
ga_guard_s2_pid19232.bin
```

Pass the directory containing these files as `profileDir`. They are intentionally not included in this publishable repository because they contain game-derived data. Before executing the native x64 helper, the tool verifies each file's exact size and SHA-256 against the validated profile.

## Run

```powershell
$profile = "<reverse-case>\gameassembly"
dotnet run --project tools\ga-static-decrypt\ga-static-decrypt.csproj -c Release -- `
  E:\DMM\gakumas\GameAssembly.dll `
  tools\ga-static-decrypt\out\GameAssembly_static_exact.dll `
  $profile
```

Optional fourth argument: a loader-decrypted reference dump. It is used only for comparison and is not needed to decrypt.

```text
ga-static-decrypt <packed.dll> <out.dll> <profileDir> [reference.dump] [--stage1]
```

`--stage1` stops after the outer-layer decryption (~1 s, only ~0.9 MB written) and is for diagnosis/comparison only:
that image is a packed-layout skeleton (99.5% zero bytes, `.data` entirely empty), the `genericMethodPointers` tables
are not materialized, and both `CodeRegScanner` and LibCpp2IL reject it — it is **not** a valid generator input.

Validated result for the current profile:

```text
47,706 body records
47,706 valid streams, 0 invalid
198,336,512-byte analysis PE
SHA-256 8f45c2b267434d349280457140f9ba6a5e9705e25321b69a8554c0a92358e127
il2cpp_init RVA 0x91DD40
```

The output is an analysis PE suitable for LibCpp2IL/Il2CppInterop generation, IDA, r2, and Ghidra. It is not a drop-in replacement for the packed game DLL.

## Version updates

The profile, stage-2 constants, section layout, and expected record count are tied to this GameAssembly build. A game update fails closed on profile hashes, stage-2 helper signatures, record count, or stream-boundary invariants; refresh those values from a newly validated reverse case rather than weakening the checks.

