param(
    [ValidateSet('self-test', 'suite', 'wire', 'mouse', 'mouse-load', 'mouse-240', 'mouse-prepare')][string]$Mode = 'self-test',
    [string]$HostAddress = '',
    [switch]$Lab
)
$ErrorActionPreference = 'Stop'
if ($Mode -ne 'self-test' -and -not $HostAddress) { throw 'Provide -HostAddress for live tests' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet_home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
$env:APPDATA = Join-Path $root '.tools\appdata'
$env:TEMP = Join-Path $root '.tools\tmp'
$env:TMP = $env:TEMP
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$project = Join-Path $PSScriptRoot '..\LookHarness\LookHarness.csproj'
Push-Location $root
try {
    & $dotnet build $project -c Release --configfile (Join-Path $PSScriptRoot '..\LookHarness\NuGet.Config') -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Harness build failed' }
    $dll = Join-Path $PSScriptRoot '..\LookHarness\bin\Release\net8.0\LookHarness.dll'
    if ($Mode -eq 'self-test') { $runArgs = @('--self-test') }
    elseif ($Mode -like 'mouse*') {
        $runArgs = @('--direct-test', $HostAddress)
        if ($Mode -eq 'mouse-load') { $runArgs += '--load' }
        if ($Mode -eq 'mouse-240') { $runArgs += @('--load', '--kernel') }
        if ($Mode -eq 'mouse-prepare') { $runArgs += '--prepare' }
    }
    elseif ($Mode -eq 'suite') { $runArgs = @($HostAddress, '--suite') }
    else { $runArgs = @($HostAddress, 'wire', '1000', '0', '1', '--wire-test') }
    if ($Lab) { $runArgs += '--lab' }
    & $dotnet $dll @runArgs
    if ($LASTEXITCODE -ne 0) { throw 'Harness failed' }
} finally { Pop-Location }
