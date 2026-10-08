#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RepoPath = (Split-Path -Parent $PSScriptRoot),
    [string]$StateRoot,
    [ValidateRange(1024, 65535)][int]$Port,
    [string]$Subnet,
    [switch]$Follow
)

$ErrorActionPreference = 'Stop'
$RepoPath = (Resolve-Path -LiteralPath $RepoPath).Path
if (!$StateRoot) { $StateRoot = Join-Path $RepoPath '.homestead-local' }
$StateRoot = [IO.Path]::GetFullPath($StateRoot)
$runner = Join-Path $RepoPath '.agents/skills/homestead-local/scripts/homestead-local.ps1'
$stateFile = Join-Path $StateRoot 'persistent/state.json'
$action = if (Test-Path -LiteralPath $stateFile) { 'Upgrade' } else { 'Start' }

# Upgrade builds current source and backs up existing persistent data before replacement.
$runnerParameters = @{
    RepoPath = $RepoPath; StateRoot = $StateRoot
    Environment = 'Persistent'; Action = $action
}
if ($PSBoundParameters.ContainsKey('Port')) { $runnerParameters.Port = $Port }
if ($PSBoundParameters.ContainsKey('Subnet')) { $runnerParameters.Subnet = $Subnet }
& $runner @runnerParameters

if ($Follow) {
    $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    Write-Host "Homestead is ready at $($state.Url). Stopping this launch leaves the persistent container running."
    $composeFile = Join-Path $StateRoot 'persistent/compose.json'
    & docker compose -p $state.Project -f $composeFile logs --follow --tail 50 homestead caddy
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
