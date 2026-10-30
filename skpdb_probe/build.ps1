# Builds the SKPDB wireless module with one ndk-build invocation and packages it.
#
#   libskpdb_probe.so -> skpdb_dev_network.zip   (TCP)
#
# Usage:  .\skpdb_probe\build.ps1 [-Ndk <ndk-dir>] [-Sdk <skroot-sdk-dir>]

param(
    [string]$Ndk = $env:ANDROID_NDK_ROOT,
    [string]$Sdk = $env:SKROOT_SDK_ROOT,
    [string]$Jdk = $env:JAVA_HOME,
    [switch]$ExperimentalTouchGuard
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Ndk -or -not $Sdk -or -not $Jdk) { throw 'Set ANDROID_NDK_ROOT, SKROOT_SDK_ROOT and JAVA_HOME, or pass -Ndk -Sdk -Jdk' }
$ndkBuild = Join-Path $Ndk 'ndk-build.cmd'

if (-not (Test-Path -LiteralPath $ndkBuild)) {
    throw "ndk-build not found: $ndkBuild"
}
if (-not (Test-Path -LiteralPath (Join-Path $Sdk 'lib\libkernel_module_kit_static.a'))) {
    throw "SKRoot SDK not found: $Sdk"
}

# ndk-build runs under GNU make, which is happier with forward slashes.
& (Join-Path $root 'tools/build_webui.ps1')
& (Join-Path $root 'tools/build_touch_guard.ps1') -Ndk $Ndk
& (Join-Path $root 'tools/build_gyro.ps1') -Ndk $Ndk
& (Join-Path $root 'tools/build_cursor.ps1') -AndroidSdk (Split-Path -Parent (Split-Path -Parent $Ndk)) -Jdk $Jdk

$guardFlag = if ($ExperimentalTouchGuard) { 1 } else { 0 }
$flavor = if ($ExperimentalTouchGuard) { 'experimental' } else { 'default' }
$libFolder = if ($ExperimentalTouchGuard) { 'libs-experimental' } else { 'libs' }
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
    SKPDB_EXPERIMENTAL_TOUCH_GUARD=$guardFlag

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

$archiveName = if ($ExperimentalTouchGuard) { 'skpdb_dev_network-experimental.zip' } else { 'skpdb_dev_network.zip' }
Compress-Archive -LiteralPath $wireless -DestinationPath (Join-Path $root $archiveName) -Force

Write-Host "wireless : $(Join-Path $root $archiveName)"
