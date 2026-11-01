param([string]$Ndk=$env:ANDROID_NDK_ROOT)
$ErrorActionPreference='Stop'
if (-not $Ndk) { throw 'Set ANDROID_NDK_ROOT or pass -Ndk' }
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output=Join-Path $root '.scratch/gyro-lab'
New-Item -ItemType Directory -Force $output | Out-Null
$compiler=Join-Path $Ndk 'toolchains/llvm/prebuilt/windows-x86_64/bin/aarch64-linux-android29-clang++.cmd'
& $compiler -std=c++17 -O2 -Wall -Wextra -Werror (Join-Path $root 'skpdb_probe/tests/gyro_probe.cpp') -landroid -o "$output/gyro_probe"
if ($LASTEXITCODE -ne 0) { throw 'Sensor receiver build failed' }
Write-Host "probe: $output/gyro_probe"
