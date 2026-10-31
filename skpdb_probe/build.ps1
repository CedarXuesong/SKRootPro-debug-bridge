# Builds the SKPDB wireless module with one ndk-build invocation and packages it.
#
#   libskpdb_probe.so -> skpdb_dev_network.zip   (TCP)
#
# Usage:  .\skpdb_probe\build.ps1 [-Ndk <ndk-dir>] [-Sdk <skroot-sdk-dir>]

param(
    [string]$Ndk = $env:ANDROID_NDK_ROOT,
    [string]$Sdk = $env:SKROOT_SDK_ROOT,
    [string]$Jdk = $env:JAVA_HOME,
    [ValidateSet('Release', 'Canary')]
    [string]$BuildChannel = 'Release',
    [string]$GitCommit = '',
    [switch]$ExperimentalTouchGuard
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$moduleInfo = [IO.File]::ReadAllText((Join-Path $root 'src/wireless/module_info.h'))
$baseVersion = [regex]::Match($moduleInfo, '#define SKPDB_MODULE_BASE_VERSION "([^"]+)"').Groups[1].Value
$identity = & (Join-Path (Split-Path -Parent $root) 'tools/build_identity.ps1') -Version $baseVersion -BuildChannel $BuildChannel -GitCommit $GitCommit
if (-not $Ndk -or -not $Sdk -or -not $Jdk) { throw 'Set ANDROID_NDK_ROOT, SKROOT_SDK_ROOT and JAVA_HOME, or pass -Ndk -Sdk -Jdk' }
$ndkBuild = Join-Path $Ndk 'ndk-build.cmd'

if (-not (Test-Path -LiteralPath $ndkBuild)) {
    throw "ndk-build not found: $ndkBuild"
}
if (-not (Test-Path -LiteralPath (Join-Path $Sdk 'lib\libkernel_module_kit_static.a'))) {
    throw "SKRoot SDK not found: $Sdk"
}

# ndk-build runs under GNU make, which is happier with forward slashes.
$revision = "#pragma once`n#define SKPDB_GIT_COMMIT `"$($identity.Commit)`"`n#define SKPDB_GIT_SHORT_COMMIT `"$($identity.ShortCommit)`"`n"
[IO.File]::WriteAllText((Join-Path $root 'src/common/build_revision.h'), $revision, [Text.Encoding]::ASCII)
& (Join-Path $root 'tools/build_webui.ps1')
& (Join-Path $root 'tools/build_touch_guard.ps1') -Ndk $Ndk
& (Join-Path $root 'tools/build_gyro.ps1') -Ndk $Ndk
& (Join-Path $root 'tools/build_cursor.ps1') -AndroidSdk (Split-Path -Parent (Split-Path -Parent $Ndk)) -Jdk $Jdk -BuildChannel $BuildChannel -GitCommit $identity.Commit

$guardFlag = if ($ExperimentalTouchGuard) { 1 } else { 0 }
$canaryFlag = if ($BuildChannel -eq 'Canary') { 1 } else { 0 }
$flavor = if ($ExperimentalTouchGuard) { 'experimental' } else { 'default' }
$libFolder = if ($ExperimentalTouchGuard) { 'libs-experimental' } else { 'libs' }
if ($BuildChannel -eq 'Canary') { $flavor += '-canary'; $libFolder += '-canary' }
$outDir = (Join-Path $root ('obj/' + $flavor)) -replace '\\', '/'
$libsDir = (Join-Path $root $libFolder) -replace '\\', '/'
$sdkDir = $Sdk -replace '\\', '/'

& $ndkBuild -C (Join-Path $root 'jni') `
    NDK_PROJECT_PATH=. `
    APP_BUILD_SCRIPT=Android.mk `
    NDK_APPLICATION_MK=Application.mk `
    NDK_OUT=$outDir `
    NDK_LIBS_OUT=$libsDir `
    SKROOT_SDK=$sdkDir `
    SKPDB_EXPERIMENTAL_TOUCH_GUARD=$guardFlag `
    SKPDB_CANARY=$canaryFlag

if ($LASTEXITCODE -ne 0) {
    throw "ndk-build failed with exit code $LASTEXITCODE"
}

$wireless = Join-Path $libsDir 'arm64-v8a/libskpdb_probe.so'
if (-not (Test-Path -LiteralPath $wireless)) {
    throw "missing build output: $wireless"
}

# Dynamic Bionic loader with no libc++_shared dependency. Its stdin carries
# the key, so diagnostic hot loading needs no key file or command-line secret.
$clang = Join-Path $Ndk 'toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android28-clang.cmd'
& $clang -O2 '-Wl,--gc-sections' '-Wl,-s' (Join-Path $root 'tools\loader\loader.cpp') `
    -ldl -o (Join-Path $libsDir 'arm64-v8a/skpdb_loader')
if ($LASTEXITCODE -ne 0) { throw 'diagnostic loader build failed' }

$archiveName = 'skpdb_dev_network'
if ($ExperimentalTouchGuard) { $archiveName += '-experimental' }
if ($BuildChannel -eq 'Canary') { $archiveName += '-canary' }
$archiveName += '.zip'
Compress-Archive -LiteralPath $wireless -DestinationPath (Join-Path $root $archiveName) -Force

Write-Host "wireless : $(Join-Path $root $archiveName)"
