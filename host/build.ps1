# Builds the native Windows client and runs its unit tests.
#
# Usage:  .\host\build.ps1 [-Configuration Release]

param(
    [string]$Configuration = 'Release',
    [string]$CMake = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'

if (-not $CMake -or -not (Test-Path -LiteralPath $CMake)) {
    $found = Get-Command cmake -ErrorAction SilentlyContinue
    if ($found) {
        $CMake = $found.Source
    } else {
        throw "cmake not found"
    }
}

& $CMake -S $root -B $build -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }

& $CMake --build $build --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

& $CMake -E chdir $build ctest -C $Configuration --output-on-failure
if ($LASTEXITCODE -ne 0) { throw "unit tests failed" }

$dist = Join-Path (Split-Path $root -Parent) 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$source = Join-Path $build "$Configuration\skpdb.exe"
Copy-Item -LiteralPath $source -Destination (Join-Path $dist 'skpdb.exe') -Force
Write-Host "built: $(Join-Path $dist 'skpdb.exe')"
