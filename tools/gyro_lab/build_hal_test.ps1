param([string]$Ndk=$env:ANDROID_NDK_ROOT, [string]$Sdk=$env:SKROOT_SDK_ROOT)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Ndk -or -not $Sdk) { throw 'Pass the NDK and authorized SDK paths' }
$compiler=Join-Path $Ndk 'toolchains/llvm/prebuilt/windows-x86_64/bin/aarch64-linux-android29-clang++.cmd'
$output=Join-Path $root '.scratch/gyro-lab'
New-Item -ItemType Directory -Force $output | Out-Null
& $compiler -std=c++20 -O2 -fPIC -shared -fvisibility=hidden -fno-rtti -static-libstdc++ `
    "-I$root/skpdb_probe/src" "-I$Sdk/include" `
    "$root/skpdb_probe/tests/gyro_hal_device.cpp" `
    "$root/skpdb_probe/obj/default-canary/local/arm64-v8a/libskpdb_core.a" `
    "$Sdk/lib/libkernel_module_kit_static.a" -ldl -lz '-Wl,--gc-sections' '-Wl,-s' `
    -o "$output/gyro_hal_device.so"
if ($LASTEXITCODE -ne 0) { throw 'Bounded HAL diagnostic build failed; build the Canary module first' }
