# install.ps1 — deploy the GakumasDoorstopShim into a gakumas install
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -ImagePath <decrypted GameAssembly image> [-RestoreDoorstopProxy] [-EnableInteropUpdate] [-DryRun]
#   powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -Revert [-DryRun]
#
# What it changes (everything backed up first):
#   BepInEx\core\GakumasDoorstopShim.dll        (new; falls back to BepInEx\core\shim\ when core is held mapped)
#   BepInEx\core\GakumasDoorstopShim.cfg        (new)
#   BepInEx\gakumas-decrypted\<image>           (copied decrypted image; -NoCopyImage to skip)
#   doorstop_config.ini                         (target_assembly -> the shim; backup kept)
#   winhttp.dll                                 (only with -RestoreDoorstopProxy, copied from winhttp2.dll)
#   BepInEx\config\BepInEx.cfg                  (only with -EnableInteropUpdate: UpdateInteropAssemblies = true)
#
# The script never touches game files, BepInEx binaries, BepInEx\interop or the plugin DLL.
param(
    [string]$GameRoot = 'E:\DMM\gakumas',
    [string]$ImagePath,
    [string]$ShimPath,
    [switch]$EnableInteropUpdate,
    [switch]$RestoreDoorstopProxy,
    [switch]$NoCopyImage,
    [switch]$Revert,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) { Write-Host "[+] $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "[!] $msg" -ForegroundColor Yellow }
function Write-Err([string]$msg)  { Write-Host "[X] $msg" -ForegroundColor Red }

function Write-File([string]$path, [string]$content) {
    if ($DryRun) { Write-Host "    (dry-run) write $path" ; return }
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -Path $path -Value $content -Encoding ASCII
}

function Copy-FileVerified([string]$source, [string]$dest) {
    if ($DryRun) { Write-Host "    (dry-run) copy $source -> $dest" ; return }
    $dir = Split-Path -Parent $dest
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Copy-Item $source $dest -Force
}

# Replaces $dest with $source under the canonical name. A file-system filter (scanner, backup or
# game-platform driver) commonly holds the deployed shim open with share read/write but denies
# delete/rename, which breaks Copy-Item's replace path while still allowing an in-place rewrite.
# Overwriting the bytes keeps the file name - the one thing doorstop's name-based lookup needs.
function Install-ShimFile([string]$source, [string]$dest) {
    if ($DryRun) { Write-Host "    (dry-run) install $source -> $dest" ; return $true }
    $dir = Split-Path -Parent $dest
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    if (Test-Path $dest) {
        try {
            Copy-Item $source $dest -Force -ErrorAction Stop
            return $true
        } catch {
            Write-Warn "replace of $dest failed: $($_.Exception.Message.Split([char]10)[0])"
        }

        # The target name cannot change (doorstop loads it by name), so rewrite the bytes in
        # place when a filter driver allows writes but denies delete/rename.
        try {
            $dst = [IO.File]::Open($dest, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
            try {
                $dst.SetLength(0)
                $src = [IO.File]::OpenRead($source)
                try { $src.CopyTo($dst) } finally { $src.Dispose() }
            } finally { $dst.Dispose() }
            Write-Ok "rewrote $dest in place"
            return $true
        } catch {
            Write-Warn "in-place rewrite of $dest failed: $($_.Exception.Message.Split([char]10)[0])"
            return $false
        }
    }

    Copy-Item $source $dest -Force -ErrorAction Stop
    return $true
}

function Backup-File([string]$path, [string]$suffix) {
    if (-not (Test-Path $path)) { return }
    $backup = "$path$suffix"
    if (Test-Path $backup) { return }
    Copy-FileVerified $path $backup
    Write-Ok "backup: $backup"
}

function Set-IniValue([string]$path, [string]$key, [string]$value) {
    $lines = Get-Content $path
    $found = $false
    $out = foreach ($line in $lines) {
        if ($line -match "^\s*$([regex]::Escape($key))\s*=") {
            $found = $true
            "$key = $value"
        } else { $line }
    }
    if (-not $found) { $out += "$key = $value" }
    Write-File $path ($out -join "`r`n")
}

# Doorstop 4.5 resolves the target *by assembly name*, and it takes that name from the target
# file name (src/bootstrap.c: get_file_name(config.target_assembly, FALSE) ->
# coreclr_create_delegate(host, domain, <file name minus extension>, "Doorstop.Entrypoint", ...)).
# The deployed file name therefore MUST equal the shim's assembly identity; a renamed copy
# (GakumasDoorstopShim.<stamp>.dll) makes doorstop fail *silently* (its LOG output is compiled
# out of release builds) and BepInEx simply never starts. The *directory*, however, is free:
# doorstop appends the target's own directory to APP_PATHS, which is how it resolves the target
# by name at all. So when <core> is unwritable (scanner / anti-cheat / running game holding a
# mapping), deploy to <core>\shim instead of renaming the assembly.
function Install-Shim([string]$coreDir, [string]$source) {
    $fileBase = 'GakumasDoorstopShim'
    $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($source).Name
    if ($assemblyName -ne $fileBase) {
        Write-Err "shim assembly name '$assemblyName' does not match the deployed file name '$fileBase'"
        Write-Err "doorstop loads the target by file name minus extension; fix AssemblyName in GakumasDoorstopShim.csproj"
        exit 5
    }

    foreach ($dir in @($coreDir, (Join-Path $coreDir 'shim'))) {
        $candidate = Join-Path $dir "$fileBase.dll"
        if (Install-ShimFile $source $candidate) { return $candidate }
        if ($dir -ne $coreDir) { continue }
        Write-Warn "cannot deploy to $candidate; falling back to $(Join-Path $coreDir 'shim')"
    }

    Write-Err "shim could not be deployed under $coreDir - close the game (and the DMM launcher), then rerun"
    exit 5
}

function Remove-StaleShims([string]$coreDir, [string]$keep) {
    $dirs = @($coreDir, (Join-Path $coreDir 'shim'))
    foreach ($file in Get-ChildItem -Path $dirs -Filter 'GakumasDoorstopShim*.dll' -ErrorAction SilentlyContinue) {
        if ($DryRun) { Write-Host "    (dry-run) remove stale $($file.FullName)" ; continue }
        if ($file.FullName -eq $keep) { continue }
        try { Remove-Item $file.FullName -Force -ErrorAction Stop; Write-Ok "removed stale $($file.Name)" }
        catch { Write-Warn "stale shim still locked: $($file.Name)" }
    }
}

$GameRoot = [IO.Path]::GetFullPath($GameRoot)
$coreDir = Join-Path $GameRoot 'BepInEx\core'
$doorstopIni = Join-Path $GameRoot 'doorstop_config.ini'
$bepInExCfg = Join-Path $GameRoot 'BepInEx\config\BepInEx.cfg'
$imageDir = Join-Path $GameRoot 'BepInEx\gakumas-decrypted'
$shimDest = Join-Path $coreDir 'GakumasDoorstopShim.dll'
$shimCfgDest = Join-Path $coreDir 'GakumasDoorstopShim.cfg'
$cacheDest = Join-Path $GameRoot 'BepInEx\gakumas-shim-codereg.cache'
$shimSuffix = '.gakumas-shim.bak'

if ([string]::IsNullOrWhiteSpace($ShimPath)) {
    $ShimPath = Join-Path $PSScriptRoot 'bin\Release\GakumasDoorstopShim.dll'
}

Write-Step "GakumasDoorstopShim installer"
Write-Step "game root: $GameRoot"
if ($DryRun) { Write-Warn "dry run: no file will be written" }

if (-not (Test-Path (Join-Path $GameRoot 'GameAssembly.dll'))) { Write-Err "not a gakumas root (GameAssembly.dll missing): $GameRoot"; exit 2 }
if (-not (Test-Path (Join-Path $coreDir 'BepInEx.Unity.IL2CPP.dll'))) { Write-Err "BepInEx IL2CPP entry missing: $coreDir"; exit 2 }
if (-not (Test-Path (Join-Path $coreDir 'LibCpp2IL.dll'))) { Write-Err "LibCpp2IL.dll missing: $coreDir"; exit 2 }

# --------------------------------------------------------------- revert
if ($Revert) {
    Write-Step "revert"

    if (Test-Path $doorstopIni) {
        if (Test-Path "$doorstopIni$shimSuffix") {
            Copy-FileVerified "$doorstopIni$shimSuffix" $doorstopIni
            Write-Ok "doorstop_config.ini restored from backup"
        } else {
            Set-IniValue $doorstopIni 'target_assembly' 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
            Write-Ok "doorstop_config.ini target_assembly reset to BepInEx"
        }
    }
    if (Test-Path "$bepInExCfg$shimSuffix") {
        Copy-FileVerified "$bepInExCfg$shimSuffix" $bepInExCfg
        Write-Ok "BepInEx.cfg restored from backup"
    }
    foreach ($path in @($shimCfgDest, $cacheDest, (Join-Path $GameRoot 'BepInEx\gakumas-shim-preflight.cache'))) {
        if (Test-Path $path) {
            if ($DryRun) { Write-Host "    (dry-run) remove $path" } else { Remove-Item $path -Force }
            Write-Ok "removed $path"
        }
    }
    foreach ($file in Get-ChildItem -Path @($coreDir, (Join-Path $coreDir 'shim')) -Filter 'GakumasDoorstopShim*.dll' -ErrorAction SilentlyContinue) {
        if ($DryRun) { Write-Host "    (dry-run) remove $($file.FullName)" ; continue }
        try { Remove-Item $file.FullName -Force -ErrorAction Stop; Write-Ok "removed $($file.Name)" }
        catch { Write-Warn "shim still locked (close the game): $($file.Name)" }
    }
    Write-Warn "decrypted image kept at $imageDir (delete manually if unwanted)"
    exit 0
}

# --------------------------------------------------------------- preflight
if (-not (Test-Path $ShimPath)) { Write-Err "shim not built: $ShimPath"; exit 3 }

# Windows keeps loaded PE images locked, and the shim must land under its canonical name, so a
# running game blocks deployment outright - fail before touching anything.
$gameProc = @(Get-Process -Name 'gakumas' -ErrorAction SilentlyContinue)
if ($gameProc.Count -gt 0 -and -not $DryRun) {
    Write-Err "gakumas.exe is running (pid $($gameProc.Id -join ',')): close it, then rerun"
    exit 5
}

$metadata = Get-ChildItem -Path $GameRoot -Filter 'global-metadata.dat' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'il2cpp_data[\\/]Metadata' } | Select-Object -First 1
if (-not $metadata) { Write-Warn "global-metadata.dat not found under $GameRoot (BepInEx still looks in the game dir)" }
else { Write-Ok "metadata: $($metadata.FullName)" }

if ([string]::IsNullOrWhiteSpace($ImagePath)) {
    $existing = @()
    if (Test-Path $imageDir) { $existing = @(Get-ChildItem -Path $imageDir -Filter '*.dll' -ErrorAction SilentlyContinue) }
    if ($existing.Count -eq 1) {
        $ImagePath = $existing[0].FullName
        Write-Ok "reusing installed image: $ImagePath"
    } else {
        Write-Err "-ImagePath is required (decrypted GameAssembly image, e.g. from tools\ga-static-decrypt)"
        Write-Host "    example: -ImagePath 'E:\code\reverse-skill-1.0.1\work\ga-static-decrypt\work\run-new-20260919\GameAssembly_static_exact_new.dll'"
        exit 3
    }
}
$ImagePath = [IO.Path]::GetFullPath($ImagePath)
if (-not (Test-Path $ImagePath)) { Write-Err "image not found: $ImagePath"; exit 3 }

# the image must be a parseable PE and must NOT look packed: run the shim's own scanner
Write-Step "scanning image (this validates the image and fills the cache)"
$selfTest = Join-Path $PSScriptRoot 'bin\Release\GakumasDoorstopShim.exe'
if (Test-Path $selfTest) {
    $scanArgs = @('--scan', $ImagePath)
    if (-not $DryRun) { $scanArgs += @('--cache', $cacheDest) }
    $scan = & $selfTest @scanArgs 2>&1
    $result = $scan | Where-Object { $_ -like 'RESULT*' } | Select-Object -First 1
    if ($LASTEXITCODE -ne 0 -or -not $result) {
        Write-Err "image rejected by the scanner:"
        $scan | Select-Object -First 4 | ForEach-Object { Write-Host "    $_" }
        exit 4
    }
    Write-Ok $result
} else {
    Write-Warn "self-test binary not found, skipping pre-scan: $selfTest"
}

# --------------------------------------------------------------- deploy
Write-Step "install shim"
$shimDest = Install-Shim $coreDir $ShimPath
Write-Ok $shimDest
Remove-StaleShims $coreDir $shimDest

$imageDest = $ImagePath
if (-not $NoCopyImage -and ([IO.Path]::GetFullPath((Split-Path -Parent $ImagePath)) -ne [IO.Path]::GetFullPath($imageDir))) {
    $imageDest = Join-Path $imageDir (Split-Path -Leaf $ImagePath)
    Write-Step "copy decrypted image ($([Math]::Round((Get-Item $ImagePath).Length / 1MB)) MB)"
    Copy-FileVerified $ImagePath $imageDest
    Write-Ok $imageDest
}

$imageForConfig = $imageDest
if ($imageDest.StartsWith($GameRoot, [StringComparison]::OrdinalIgnoreCase)) {
    $imageForConfig = $imageDest.Substring($GameRoot.Length).TrimStart('\', '/')
}
$cacheForConfig = $cacheDest.Substring($GameRoot.Length).TrimStart('\', '/')

$cfgContent = @"
# GakumasDoorstopShim configuration
# ImagePath: decrypted GameAssembly image used as the interop-generation input
ImagePath=$imageForConfig
# CachePath: derived Il2CppCodeRegistration constants, keyed by image size/mtime/head/tail hash
CachePath=$cacheForConfig
# RuntimePath: optional override for the IL2CPP module the native resolver must load
#              (normally auto-detected from the process module list)
Verbose=1
"@
Write-File $shimCfgDest $cfgContent
Write-Ok $shimCfgDest

Write-Step "point doorstop at the shim"
$shimRelative = $shimDest
if ($shimDest.StartsWith($GameRoot, [StringComparison]::OrdinalIgnoreCase)) {
    $shimRelative = $shimDest.Substring($GameRoot.Length).TrimStart('\', '/')
}
Backup-File $doorstopIni $shimSuffix
Set-IniValue $doorstopIni 'target_assembly' $shimRelative
Write-Ok "target_assembly = $shimRelative"

if ($RestoreDoorstopProxy) {
    Write-Step "restore doorstop proxy"
    $proxy = Join-Path $GameRoot 'winhttp.dll'
    $parked = Join-Path $GameRoot 'winhttp2.dll'
    if (Test-Path $proxy) {
        Write-Ok "winhttp.dll already present"
    } elseif (Test-Path $parked) {
        Copy-FileVerified $parked $proxy
        Write-Ok "winhttp.dll restored from winhttp2.dll (doorstop 4.x proxy)"
    } else {
        Write-Warn "neither winhttp.dll nor winhttp2.dll found; install doorstop 4.x first"
    }
}

if ($EnableInteropUpdate) {
    Write-Step "allow BepInEx to regenerate interop assemblies"
    Backup-File $bepInExCfg $shimSuffix
    Set-IniValue $bepInExCfg 'UpdateInteropAssemblies' 'true'
    Write-Ok "BepInEx.cfg: UpdateInteropAssemblies = true"
    Write-Warn "first boot after an image change regenerates BepInEx\interop (minutes); rebuild the plugin against it"
}

# --------------------------------------------------------------- verify
# Load the deployed shim exactly the way doorstop does (by name, from a directory this process
# does not probe) and assert that it came up. Catches the failure mode that is otherwise silent:
# doorstop's own logging is compiled out of release builds, so a shim that cannot be found or
# whose dependencies cannot be resolved leaves the game running without BepInEx.
$probe = Join-Path $PSScriptRoot 'host-probe\bin\Release\shim-host-probe.exe'
if ($DryRun) {
    Write-Step "verify (skipped: dry run)"
} elseif (Test-Path $probe) {
    Write-Step "verify shim load path"
    $probeLog = Join-Path ([IO.Path]::GetTempPath()) 'gakumas-shim-probe.log'
    Remove-Item $probeLog -Force -ErrorAction SilentlyContinue
    $probeOut = & $probe $GameRoot $shimDest $probeLog 2>&1
    $probeText = if (Test-Path $probeLog) { Get-Content $probeLog -Raw } else { '' }
    # shim lines land in the probe's log, probe lines only on stdout
    $probeAll = "$probeText`n$($probeOut -join "`n")"
    $expected = @(
        'LibCpp2IL pinned',
        'hooked BepInEx.Unity.IL2CPP.Il2CppInteropManager',
        'handover skipped',
        'PlatformUtils.SetPlatform() OK'
    )
    $missing = @($expected | Where-Object { $probeAll -notmatch [regex]::Escape($_) })
    if ($missing.Count -eq 0) {
        Write-Ok "shim loads from $(Split-Path -Leaf $shimDest) with no local BepInEx probing"
    } else {
        Write-Err "shim load check failed; missing log lines: $($missing -join ', ')"
        Write-Host "    probe log: $probeLog"
        $probeOut | Select-Object -Last 15 | ForEach-Object { Write-Host "    $_" }
        exit 6
    }
} else {
    Write-Warn "host probe not built; skipping load verification (dotnet build tools\doorstop-shim\host-probe -c Release)"
}

Write-Host ""
Write-Ok "done. next:"
Write-Host "    1. start the game (your login arguments), then watch:"
Write-Host "         $GameRoot\BepInEx\gakumas-shim.log        (shim: scan, injection, handover)"
Write-Host "         $GameRoot\BepInEx\LogOutput.log           (BepInEx: interop generation, plugins)"
Write-Host "    2. rollback: install.ps1 -Revert"
