param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [ValidateSet('Release', 'Canary')]
    [string]$BuildChannel = 'Release',
    [string]$GitCommit = ''
)
$ErrorActionPreference = 'Stop'
if (-not $GitCommit) {
    if (Test-Path -LiteralPath (Join-Path $Root '.git')) {
        $GitCommit = & git -C $Root rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the source commit' }
    } elseif (Test-Path -LiteralPath (Join-Path $Root 'SOURCE-COMMIT.txt')) {
        $GitCommit = [IO.File]::ReadAllText((Join-Path $Root 'SOURCE-COMMIT.txt')).Trim()
    } else {
        throw 'Source commit unavailable. Build a Git checkout or pass -GitCommit with the full commit hash.'
    }
}
if ($GitCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'GitCommit must be a full 40-character Git commit hash' }
$commit = $GitCommit.ToLowerInvariant()
$short = $commit.Substring(0, 12)
$suffix = if ($BuildChannel -eq 'Canary') { '-canary' } else { '' }
[pscustomobject]@{
    Commit = $commit
    ShortCommit = $short
    Version = "$Version$suffix+$short"
    FileVersion = "$Version$suffix-$short"
}
