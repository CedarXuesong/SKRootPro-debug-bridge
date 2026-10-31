# Builds the WinUI 3 desktop client and publishes a runnable folder to dist\SKPDB.
#
# The app is framework-dependent: it needs the .NET 8 Desktop Runtime and the
# Windows App Runtime 1.7 (the Microsoft Store installs both on most Windows 11
# machines). The source project uses Microsoft.WindowsAppSDK 1.7.

param(
    [string]$Configuration = 'Release',
    [ValidateSet('Release', 'Canary')]
    [string]$BuildChannel = 'Release',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'SkpdbDesktop\SkpdbDesktop.csproj'
$stubProject = Join-Path $PSScriptRoot 'build-stubs\AppxTasks\AppxTasks.csproj'
$dist = Join-Path $root 'dist\SKPDB'
if ($OutputDirectory) {
    $dist = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
}

$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $found = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $found) {
        throw 'dotnet SDK 8.0 not found. Install the .NET 8 SDK or place it in .tools\dotnet.'
    }
    $dotnet = $found.Source
}

# Keep SDK/NuGet state inside the workspace. This machine's user profile has
# restricted ACLs on the default dotnet/NuGet caches.
$env:TMP = Join-Path $root '.tools\tmp'
$env:TEMP = $env:TMP
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet_home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
$env:APPDATA = Join-Path $root '.tools\appdata'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
New-Item -ItemType Directory -Force -Path $env:TMP, $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:APPDATA | Out-Null

# Build Tools 2022 can compile WinUI, but in this checkout it does not ship the
# legacy AppxPackage MSBuild tasks. The tiny local task assembly removes
# duplicate payload entries; the app itself does not use MSIX.
& $dotnet build $stubProject -c $Configuration -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Appx task stub build failed' }

$nugetConfig = Join-Path $root '.scratch\NuGet.Config'
$restoreArgs = @('restore', $project, '-v:minimal')
if (Test-Path -LiteralPath $nugetConfig) {
    $restoreArgs += @('--configfile', $nugetConfig)
}
& $dotnet @restoreArgs
if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed' }

& $dotnet publish $project --no-restore -c $Configuration -p:Platform=x64 -r win-x64 `
    --self-contained false -p:BuildChannel=$BuildChannel -o $dist -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# XBF files are generated XAML payloads. The normal MSIX PRI target copies
# them; this environment uses the unpackaged path, so copy them explicitly.
$xbfSource = Join-Path $PSScriptRoot "SkpdbDesktop\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64"
if (-not (Test-Path -LiteralPath $xbfSource)) {
    $xbfSource = Join-Path $PSScriptRoot "SkpdbDesktop\bin\x64\$Configuration\net8.0-windows10.0.19041.0"
}
Get-ChildItem -LiteralPath $xbfSource -Filter '*.xbf' -File | Copy-Item -Destination $dist -Force
$pages = Join-Path $xbfSource 'Pages'
if (Test-Path -LiteralPath $pages) {
    New-Item -ItemType Directory -Force -Path (Join-Path $dist 'Pages') | Out-Null
    Get-ChildItem -LiteralPath $pages -Filter '*.xbf' -File |
        Copy-Item -Destination (Join-Path $dist 'Pages') -Force
}

Write-Host "published: $(Join-Path $dist 'SKPDB.exe')"
