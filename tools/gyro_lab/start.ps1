param(
    [Parameter(Mandatory=$true)][string]$DeviceHost,
    [string]$Ndk=$env:ANDROID_NDK_ROOT,
    [int]$Port=8767,
    [switch]$Background
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
& (Join-Path $PSScriptRoot 'build_probe.ps1') -Ndk $Ndk
if (-not (Test-Path -LiteralPath (Join-Path $root '.scratch/gyro-lab/gyro_hal_device.so'))) {
    throw 'Build the Canary module and run tools/gyro_lab/build_hal_test.ps1 first'
}
$launch=@((Join-Path $PSScriptRoot 'server.py'), '--host', $DeviceHost, '--port', "$Port")
if ($Background) { $launch+='--background' }
& python @launch
if ($LASTEXITCODE -ne 0) { throw 'Gyro lab stopped with an error' }
