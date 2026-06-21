# build.ps1 — Strip-Boy patcher pipeline (Windows / PowerShell 7+).
#
# Reads the original APK (you supply it), produces a patched APK that
# can be sideloaded onto your Android handheld.
#
# Pipeline:
#   1. apktool d        → extracted source tree (cached)
#   2. manifest + smali (idempotent)
#   3. pipboy-patcher (Cecil) on Assembly-CSharp.dll
#   4. apktool b        → unsigned APK
#   5. zipalign         → aligned APK
#   6. apksigner sign   → signed APK
#
# Usage:
#     .\scripts\build.ps1 [path\to\original.apk]
#
# Environment overrides:
#     $env:ANDROID_BUILD_TOOLS   path to build-tools\<version>\
#     $env:APKTOOL               path to apktool launcher (.bat/.jar)
#     $env:KEYSTORE              keystore path  [default: apk\debug.keystore]
#     $env:KS_PASS               password       [default: android]
#     $env:KS_ALIAS              key alias      [default: pipboy-debug]
#     $env:FORCE_RESTART = "1"   wipe apk\extracted\ before decompile
#
# Outputs:
#     apk\out\pipboy-loopback.apk    — installable APK

#Requires -Version 7
$ErrorActionPreference = "Stop"

$Green = "$([char]27)[1;32m"; $Red = "$([char]27)[1;31m"; $Reset = "$([char]27)[0m"
function Log { param([string]$Msg) Write-Host "$Green[build]$Reset $Msg" }
function Die { param([string]$Msg) Write-Host "$Red[build]$Reset $Msg" -ForegroundColor Red; exit 1 }

# ----- Repo root, APK resolution --------------------------------------------
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $Root

$DefaultApkCandidates = @(
    "apk\original.apk",
    ".\fallout-pip-boy-1-2.apk",
    (Join-Path $env:USERPROFILE "Downloads\fallout-pip-boy-1-2.apk"),
    (Join-Path $env:USERPROFILE "Downloads\fallout-pip-boy.apk")
)

$ApkIn = $null
if ($args.Count -ge 1 -and $args[0]) { $ApkIn = $args[0] }
elseif ($env:APK_IN)                 { $ApkIn = $env:APK_IN }
else {
    foreach ($c in $DefaultApkCandidates) {
        if (Test-Path $c) { $ApkIn = $c; break }
    }
}

if (-not $ApkIn -or -not (Test-Path $ApkIn)) {
    Write-Host @"
ERROR: original Fallout 4 Pip-Boy APK not found.

This script doesn't (and can't) include Bethesda's APK. You need a
personal copy of v1.2 (com.bethsoft.falloutcompanionapp, versionCode 9,
~38 MB). Drop it at apk\original.apk or pass the path as the first arg:
    .\scripts\build.ps1 C:\path\to\your.apk
"@ -ForegroundColor Red
    exit 2
}

# ----- Paths ----------------------------------------------------------------
$ExtractedDir  = "apk\extracted"
$ManagedDir    = "apk\managed"
$WorkDir       = "apk\work"
$OutDir        = "apk\out"
$Keystore      = if ($env:KEYSTORE) { $env:KEYSTORE } else { "apk\debug.keystore" }
$PatcherProj   = "patcher\Patcher.csproj"
$PatcherDll    = "patcher\bin\Release\net10.0\pipboy-patcher.dll"
$LauncherSmali = "patcher\smali\io\pipboy\thor\LauncherActivity.smali"
$LedBridgeSmali = "patcher\smali\io\pipboy\thor\LEDBridge.smali"
$ManifestXml   = Join-Path $ExtractedDir "AndroidManifest.xml"

$ApkPath          = "assets/bin/Data/Managed/Assembly-CSharp.dll"
$OriginalDllPath  = Join-Path $ExtractedDir "assets\bin\Data\Managed\Assembly-CSharp.dll"
$DllBackup        = Join-Path $ManagedDir   "Assembly-CSharp.original.dll"
$DllPatched       = Join-Path $ManagedDir   "Assembly-CSharp.patched.dll"
$ApkFromApktool   = Join-Path $WorkDir      "from-apktool.apk"
$ApkAligned       = Join-Path $WorkDir      "pipboy-aligned.apk"
$ApkFinal         = Join-Path $OutDir       "pipboy-loopback.apk"

$KsAlias = if ($env:KS_ALIAS) { $env:KS_ALIAS } else { "pipboy-debug" }
$KsPass  = if ($env:KS_PASS)  { $env:KS_PASS  } else { "android" }

# ----- Tool discovery -------------------------------------------------------
function Find-BuildTools {
    if ($env:ANDROID_BUILD_TOOLS -and (Test-Path (Join-Path $env:ANDROID_BUILD_TOOLS "apksigner.bat"))) {
        return $env:ANDROID_BUILD_TOOLS
    }
    $roots = @()
    if ($env:ANDROID_HOME)     { $roots += (Join-Path $env:ANDROID_HOME     "build-tools") }
    if ($env:ANDROID_SDK_ROOT) { $roots += (Join-Path $env:ANDROID_SDK_ROOT "build-tools") }
    $roots += (Join-Path $env:LOCALAPPDATA "Android\Sdk\build-tools")
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $versions = Get-ChildItem $r -Directory | Sort-Object Name -Descending
        foreach ($v in $versions) {
            if (Test-Path (Join-Path $v.FullName "apksigner.bat")) { return $v.FullName }
        }
    }
    return $null
}

$Bt = Find-BuildTools
if (-not $Bt) {
    Die "Android build-tools not found. Install via Android Studio's SDK Manager, or set `$env:ANDROID_BUILD_TOOLS"
}
$Apksigner = Join-Path $Bt "apksigner.bat"
$Zipalign  = Join-Path $Bt "zipalign.exe"

# Apktool: accept env override, else try `apktool` on PATH, else `apktool.bat`.
$Apktool = $env:APKTOOL
if (-not $Apktool) {
    if (Get-Command apktool -ErrorAction SilentlyContinue) { $Apktool = "apktool" }
    elseif (Get-Command apktool.bat -ErrorAction SilentlyContinue) { $Apktool = "apktool.bat" }
    else { Die "apktool not found. Install from https://apktool.org/ or set `$env:APKTOOL" }
}

foreach ($cmd in @("dotnet", "keytool", "python3")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        if ($cmd -eq "python3" -and (Get-Command python -ErrorAction SilentlyContinue)) { continue }
        Die "$cmd not on PATH"
    }
}
# Prefer python3 if present, else fall back to python.
$Python = if (Get-Command python3 -ErrorAction SilentlyContinue) { "python3" } else { "python" }

New-Item -ItemType Directory -Force -Path $ManagedDir, $WorkDir, $OutDir | Out-Null

Log "input APK: $ApkIn"
Log "build-tools: $Bt"

# ----- 1. apktool decompile (cached) ---------------------------------------
$needDecompile = $false
if ($env:FORCE_RESTART -eq "1")                          { $needDecompile = $true }
elseif (-not (Test-Path $ManifestXml))                   { $needDecompile = $true }
elseif ((Get-Item $ApkIn).LastWriteTime -gt (Get-Item $ManifestXml).LastWriteTime) { $needDecompile = $true }

if ($needDecompile) {
    Log "1/6  apktool d  (decompile)"
    if (Test-Path $ExtractedDir) { Remove-Item -Recurse -Force $ExtractedDir }
    & $Apktool d $ApkIn -o $ExtractedDir -f | Select-Object -Last 1 | ForEach-Object { "     $_" }
    if ($LASTEXITCODE -ne 0) { Die "apktool d failed" }
} else {
    Log "1/6  apktool decompile cached at $ExtractedDir"
}

# ----- 2. Manifest + smali --------------------------------------------------
Log "2/6  manifest + smali"
& $Python (Join-Path $PSScriptRoot "patch_manifest.py") $ManifestXml | ForEach-Object { "     $_" }
if ($LASTEXITCODE -ne 0) { Die "patch_manifest.py failed" }

$smaliDst = Join-Path $ExtractedDir "smali\io\pipboy\thor"
New-Item -ItemType Directory -Force -Path $smaliDst | Out-Null
foreach ($src in @($LauncherSmali, $LedBridgeSmali)) {
    $name = Split-Path $src -Leaf
    $dst = Join-Path $smaliDst $name
    $copy = $true
    if (Test-Path $dst) {
        $a = (Get-FileHash $src -Algorithm SHA256).Hash
        $b = (Get-FileHash $dst -Algorithm SHA256).Hash
        if ($a -eq $b) { $copy = $false }
    }
    if ($copy) {
        Copy-Item $src $dst -Force
        Log "     copied $name"
    } else {
        Log "     $name already current"
    }
}

# ----- 3. Cecil patcher -----------------------------------------------------
$needBuild = -not (Test-Path $PatcherDll) -or `
             (Get-Item "patcher\Program.cs").LastWriteTime -gt (Get-Item $PatcherDll).LastWriteTime
if ($needBuild) {
    Log "3/6  build Cecil patcher (Release)"
    & dotnet build $PatcherProj -c Release | Out-Null
} else {
    Log "3/6  Cecil patcher up-to-date"
}

Copy-Item $OriginalDllPath $DllBackup -Force

Log "     run patcher"
& dotnet $PatcherDll $DllBackup $DllPatched | ForEach-Object { "     $_" }
if ($LASTEXITCODE -ne 0) { Die "patcher failed" }

Copy-Item $DllPatched $OriginalDllPath -Force

# ----- 4. apktool build -----------------------------------------------------
Log "4/6  apktool b  (rebuild)"
if (Test-Path $ApkFromApktool) { Remove-Item $ApkFromApktool }
& $Apktool b $ExtractedDir -o $ApkFromApktool | Select-Object -Last 3 | ForEach-Object { "     $_" }
if ($LASTEXITCODE -ne 0) { Die "apktool b failed" }

# ----- 5. zipalign ----------------------------------------------------------
Log "5/6  zipalign"
if (Test-Path $ApkAligned) { Remove-Item $ApkAligned }
& $Zipalign -p -f 4 $ApkFromApktool $ApkAligned
if ($LASTEXITCODE -ne 0) { Die "zipalign failed" }

# ----- 6. Sign --------------------------------------------------------------
if (-not (Test-Path $Keystore)) {
    Log "6/6  generate debug keystore (one-time)"
    & keytool -genkey -v `
        -keystore $Keystore `
        -alias $KsAlias `
        -keyalg RSA -keysize 2048 `
        -validity 10000 `
        -storepass $KsPass -keypass $KsPass `
        -dname "CN=Strip-Boy, OU=local, O=local, C=US" | Select-Object -Last 3
} else {
    Log "6/6  reuse existing debug keystore"
}

Log "     sign + verify"
if (Test-Path $ApkFinal) { Remove-Item $ApkFinal }
& $Apksigner sign `
    --ks $Keystore `
    --ks-pass "pass:$KsPass" `
    --key-pass "pass:$KsPass" `
    --ks-key-alias $KsAlias `
    --out $ApkFinal `
    $ApkAligned
if ($LASTEXITCODE -ne 0) { Die "apksigner sign failed" }

& $Apksigner verify $ApkFinal 2>&1 |
    Where-Object { $_ -notmatch '^WARNING' -and $_ } |
    ForEach-Object { "     $_" }

$sz = (Get-Item $ApkFinal).Length / 1MB
Log ("DONE — $ApkFinal ({0:F1} MB)" -f $sz)
Log ""
Log "Install:    adb install -r $ApkFinal"
Log "Uninstall:  adb uninstall com.bethsoft.falloutcompanionapp"
