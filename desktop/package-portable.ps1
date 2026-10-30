# Package build files directly. Never copy user data or delete staging directories.
param([switch]$SkipBuild, [string]$ClientDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$client = if ($ClientDirectory) { (Resolve-Path -LiteralPath $ClientDirectory).Path } else { Join-Path $root 'dist/SKPDB' }
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $client
    if ($LASTEXITCODE -ne 0) { throw 'client build failed' }
}
if (-not (Test-Path -LiteralPath (Join-Path $client 'SKPDB.exe'))) { throw 'published client not found' }
$project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SkpdbDesktop/SkpdbDesktop.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'invalid desktop version' }
$archive = Join-Path $root "dist/SKPDB-$version-portable.zip"
if (Test-Path -LiteralPath $archive) { Move-Item -LiteralPath $archive -Destination ($archive + '.' + (Get-Date -Format 'yyyyMMdd_HHmmss_fffffff') + '.bak') }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $client -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($client, $file.FullName) -replace '\\','/'
        if ($relative -match '(^|/)(data|logs|screenshots)(/|$)' -or $relative -match '\.(log|dmp|tmp|bak|pfx|jks|keystore)$') { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "SKPDB-$version/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    foreach ($name in @('LICENSE', 'THIRD_PARTY_NOTICES.md')) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root $name), "SKPDB-$version/$name") | Out-Null }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'docs/third-party') -File) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "SKPDB-$version/third-party/$($file.Name)") | Out-Null }
    $entry = $zip.CreateEntry("SKPDB-$version/README.txt")
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write("SKPDB $version`nExtract and run SKPDB.exe.`nRequires .NET 8 Desktop Runtime and Windows App Runtime 1.7.`nUser settings/layouts are not included. See LICENSE for use and distribution restrictions.`n") } finally { $writer.Dispose() }
} finally { $zip.Dispose() }
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($archive + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($archive) + "`n", [Text.Encoding]::ASCII)
Write-Host "portable: $archive"
Write-Host "sha256: $hash"
