#requires -Version 7.0
[CmdletBinding()]
param([string]$RepoPath = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$RepoPath = (Resolve-Path -LiteralPath $RepoPath).Path
$launchDirectory = Join-Path $RepoPath '.homestead-local/vscode'
$environmentFile = Join-Path $launchDirectory '.env'
[IO.Directory]::CreateDirectory($launchDirectory) | Out-Null

if (!(Test-Path -LiteralPath $environmentFile)) {
    $setupToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    [IO.File]::WriteAllText($environmentFile, "HOMESTEAD_SETUP_TOKEN=$setupToken`n", [Text.UTF8Encoding]::new($false))
}

Write-Host "Local setup token is saved in $environmentFile. Use it at http://localhost:5080/setup on first launch."
