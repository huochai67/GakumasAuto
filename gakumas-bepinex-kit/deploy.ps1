# deploy.ps1 — GakumasAuto 插件部署脚本（不捆绑 BepInEx）
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1 -TargetDir <游戏目录>
#   powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1 -PluginPath <GakumasAuto.dll>
#   powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1 -TargetDir <目录> -VerifyOnly
#
# 前置：用户已从官方渠道安装与游戏版本匹配的 BepInEx 6、Il2CppInterop 和 interop 程序集。
# 本脚本只复制 GakumasAuto.dll，不安装或覆盖 BepInEx、doorstop、interop、Unity libs 或游戏文件。
param(
    [string]$TargetDir = "E:\DMM\gakumas",
    [string]$PluginPath = "",
    [switch]$SkipBackup,
    [switch]$VerifyOnly
)

$ErrorActionPreference = "Stop"
$KitRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($PluginPath)) {
    $PluginPath = Join-Path $KitRoot "..\plugin\bin\Release\net6.0\GakumasAuto.dll"
}
$TargetDir = [IO.Path]::GetFullPath($TargetDir)
$PluginPath = [IO.Path]::GetFullPath($PluginPath)

function Write-Step([string]$msg) { Write-Host "[+] $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "[!] $msg" -ForegroundColor Yellow }
function Write-Err([string]$msg)  { Write-Host "[X] $msg" -ForegroundColor Red }

$RequiredCore = @(
    "BepInEx.Core.dll",
    "BepInEx.Unity.IL2CPP.dll",
    "Il2CppInterop.Runtime.dll"
)
$RequiredInterop = @(
    "Assembly-CSharp.dll",
    "Il2Cppmscorlib.dll",
    "UnityEngine.CoreModule.dll"
)

function Test-Deployed([string]$dir) {
    $errors = @()
    if (-not (Test-Path $dir -PathType Container)) {
        $errors += "target dir missing: $dir"
        return $errors
    }
    if (-not (Test-Path (Join-Path $dir "gakumas.exe") -PathType Leaf)) {
        $errors += "gakumas.exe missing: $dir"
    }
    foreach ($f in @("winhttp.dll", "doorstop_config.ini", ".doorstop_version")) {
        if (-not (Test-Path (Join-Path $dir $f) -PathType Leaf)) {
            $errors += "BepInEx boot file missing: $f"
        }
    }

    $bepInEx = Join-Path $dir "BepInEx"
    foreach ($sub in @("config", "core", "interop", "plugins")) {
        if (-not (Test-Path (Join-Path $bepInEx $sub) -PathType Container)) {
            $errors += "missing BepInEx\$sub; install BepInEx and matching interop first"
        }
    }
    foreach ($f in $RequiredCore) {
        if (-not (Test-Path (Join-Path $bepInEx "core\$f") -PathType Leaf)) {
            $errors += "missing BepInEx\core\$f"
        }
    }
    foreach ($f in $RequiredInterop) {
        if (-not (Test-Path (Join-Path $bepInEx "interop\$f") -PathType Leaf)) {
            $errors += "missing BepInEx\interop\$f; generate matching interop first"
        }
    }

    $cfg = Join-Path $bepInEx "config\BepInEx.cfg"
    if (-not (Test-Path $cfg -PathType Leaf)) {
        $errors += "missing BepInEx.cfg"
    }
    elseif ((Select-String -Path $cfg -Pattern "UpdateInteropAssemblies\s*=\s*false" -Quiet) -ne $true) {
        $errors += "BepInEx.cfg: UpdateInteropAssemblies must be false"
    }

    $hashFile = Join-Path $bepInEx "interop\assembly-hash.txt"
    if (-not (Test-Path $hashFile -PathType Leaf)) {
        $errors += "missing BepInEx\interop\assembly-hash.txt"
    }
    elseif ((Get-Item $hashFile).Length -ne 32) {
        $errors += "assembly-hash.txt must contain 32 hex characters without a newline"
    }

    if (-not (Test-Path (Join-Path $bepInEx "plugins\GakumasAuto.dll") -PathType Leaf)) {
        $errors += "missing BepInEx\plugins\GakumasAuto.dll"
    }
    return $errors
}

Write-Step "GakumasAuto external-BepInEx installer"
Write-Step "目标目录: $TargetDir"
Write-Step "插件输入: $PluginPath"

if ($VerifyOnly) {
    $errs = Test-Deployed $TargetDir
    if ($errs.Count -eq 0) { Write-Ok "目标目录完整性校验通过"; exit 0 }
    $errs | ForEach-Object { Write-Err $_ }
    Write-Err "校验失败，共 $($errs.Count) 项"
    exit 1
}

if (-not (Test-Path $TargetDir -PathType Container)) {
    Write-Err "目标目录不存在: $TargetDir"
    exit 1
}
if (-not (Test-Path $PluginPath -PathType Leaf)) {
    Write-Err "插件 DLL 不存在: $PluginPath"
    Write-Err "先构建 plugin\GakumasAuto.csproj，或通过 -PluginPath 指定 DLL"
    exit 1
}

$preflight = Test-Deployed $TargetDir
$preflight = @($preflight | Where-Object { $_ -notlike "missing BepInEx\plugins\GakumasAuto.dll" })
if ($preflight.Count -ne 0) {
    $preflight | ForEach-Object { Write-Err $_ }
    Write-Err "外部 BepInEx 前置检查失败；本脚本不会安装或修补 BepInEx"
    exit 1
}

$pluginDest = Join-Path $TargetDir "BepInEx\plugins\GakumasAuto.dll"
$backup = Join-Path $TargetDir ("BepInEx\.gakumas-auto-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
if ((Test-Path $pluginDest) -and -not $SkipBackup) {
    Write-Step "备份现有插件 -> $backup"
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    Copy-Item $pluginDest (Join-Path $backup "GakumasAuto.dll") -Force
    Write-Ok "备份完成"
}

Write-Step "只复制 GakumasAuto.dll"
Copy-Item $PluginPath $pluginDest -Force

$errs = Test-Deployed $TargetDir
if ($errs.Count -ne 0) {
    $errs | ForEach-Object { Write-Err $_ }
    Write-Err "部署校验失败，共 $($errs.Count) 项"
    if (-not $SkipBackup) { Write-Warn "可从备份恢复: $backup" }
    exit 1
}

Write-Ok "插件部署完成；BepInEx 和 interop 未被修改"
Write-Host "下一步：在独立的 PowerShell/CMD 中启动游戏，然后观察 BepInEx\LogOutput.log"
Write-Host "UI 指令通道：node mcp\cli.js state"
