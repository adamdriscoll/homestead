#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Sandbox', 'Persistent')][string]$Environment = 'Sandbox',
    [ValidateSet('Start', 'Upgrade', 'Recover', 'Backup', 'Rollback', 'Reset', 'Stop', 'Status', 'Logs', 'Token', 'CompleteSetup', 'TrustCertificate')]
    [string]$Action = 'Status',
    [string]$RepoPath = (Get-Location).Path,
    [string]$StateRoot,
    [string]$Image,
    [ValidateRange(1024, 65535)][int]$Port,
    [string]$Subnet,
    [string]$BackupPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Action -eq 'Reset' -and $Environment -ne 'Sandbox') { throw 'Reset is limited to Sandbox.' }
if ($Action -eq 'Recover' -and $Environment -ne 'Persistent') { throw 'Recover is limited to Persistent.' }
if ($Image -and $Action -notin @('Start', 'Upgrade', 'Recover', 'Reset')) { throw 'Image applies only to Start, Upgrade, Recover, or Reset.' }
$RepoPath = (Resolve-Path -LiteralPath $RepoPath).Path
if (!(Test-Path -LiteralPath (Join-Path $RepoPath 'deploy/Dockerfile'))) { throw 'RepoPath must be a Homestead checkout.' }
if (!$StateRoot) { $StateRoot = Join-Path $RepoPath '.homestead-local' }
$StateRoot = [IO.Path]::GetFullPath($StateRoot)
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($StateRoot.ToLowerInvariant()))).Substring(0, 10).ToLowerInvariant()
$mode = $Environment.ToLowerInvariant()
$project = "homestead-local-$hash-$mode"
$directory = Join-Path $StateRoot $mode
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$lock = $null
try { $lock = [IO.File]::Open((Join-Path $directory 'operation.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
catch { throw 'Another operation is already using this environment.' }

function Invoke-Docker([string[]]$Arguments) {
    $output = & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker failed ($LASTEXITCODE): $($Arguments[0])" }
    return $output
}
function Save-Json($Value, [string]$Path) {
    $temporary = "$Path.tmp"
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
$stateFile = Join-Path $directory 'state.json'
$composeFile = Join-Path $directory 'compose.json'
$caddyFile = Join-Path $directory 'Caddyfile'
$script:state = $null
function Save-State { Save-Json $script:state $stateFile }
function Write-Compose {
    $prefix = $script:state.Subnet.Split('/')[0].Split('.')[0..2] -join '.'
    @"
{
    default_sni $($script:state.Host)
    skip_install_trust
}
https://$($script:state.Host) {
    tls internal
    reverse_proxy homestead:8080
}
"@ | Set-Content -LiteralPath $caddyFile -Encoding utf8
    $configuration = @{
        services = @{
            homestead = @{
                image = $script:state.Image; pull_policy = 'never'; restart = 'unless-stopped'
                environment = @{
                    HOMESTEAD_BASE_URL = $script:state.Url
                    HOMESTEAD_DATA_PATH = '/data'; ASPNETCORE_ENVIRONMENT = 'Production'
                    HOMESTEAD_SETUP_TOKEN = $script:state.SetupToken
                    HOMESTEAD_TRUSTED_PROXIES = "$prefix.3"
                    AllowedHosts = "$($script:state.Host);localhost"
                }
                volumes = @('data:/data')
                networks = @{ local = @{ ipv4_address = "$prefix.2" } }
                security_opt = @('no-new-privileges:true'); cap_drop = @('ALL')
            }
            caddy = @{
                image = 'caddy:2.10.2-alpine'; restart = 'unless-stopped'
                labels = @{ 'homestead.local.config' = (Get-FileHash -LiteralPath $caddyFile -Algorithm SHA256).Hash }
                ports = @(@{ target = 443; published = [string]$script:state.Port; host_ip = '127.0.0.1'; protocol = 'tcp' })
                volumes = @(@{ type = 'bind'; source = $caddyFile; target = '/etc/caddy/Caddyfile'; read_only = $true }, 'tls:/data', 'tls_config:/config')
                networks = @{ local = @{ ipv4_address = "$prefix.3" } }
            }
        }
        networks = @{ local = @{ ipam = @{ config = @(@{ subnet = $script:state.Subnet }) } } }
        volumes = @{
            data = @{ name = $script:state.Volume; external = $true }
            tls = @{ name = "${project}_tls" }; tls_config = @{ name = "${project}_tls_config" }
        }
    }
    Save-Json $configuration $composeFile
}
function Compose([string[]]$Arguments) { Invoke-Docker (@('compose', '-p', $project, '-f', $composeFile) + $Arguments) }
function App-Id { return ((Compose @('ps', '-a', '-q', 'homestead')) -join '').Trim() }
function Is-Running {
    $id = App-Id
    return $id -and (((Invoke-Docker @('inspect', '--format', '{{.State.Running}}', $id)) -join '') -eq 'true')
}
function Probe([string]$Path) {
    try { return (Invoke-WebRequest -Uri ($script:state.Url + $Path) -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec 3).StatusCode }
    catch { return 0 }
}
function Wait-Ready {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ((Probe '/health/ready') -eq 200) { return }
        Start-Sleep -Seconds 1
    }
    throw "Readiness failed at $($script:state.Url). Inspect -Action Logs; data and backups are preserved."
}
function Start-App {
    Write-Compose
    Invoke-Docker @('volume', 'create', $script:state.Volume) | Out-Null
    Compose @('up', '-d', '--no-build') | Out-Host
    Wait-Ready
}
function Resolve-Image {
    if ($Image) {
        & docker image inspect $Image *> $null
        if ($LASTEXITCODE -ne 0) { Invoke-Docker @('pull', $Image) | Out-Host }
        $reference = $Image
    } else {
        $reference = "homestead-local:build-$hash-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
        Invoke-Docker @('build', '--pull', '-f', (Join-Path $RepoPath 'deploy/Dockerfile'), '-t', $reference, $RepoPath) | Out-Host
    }
    return ((Invoke-Docker @('image', 'inspect', '--format', '{{.Id}}', $reference)) -join '').Trim()
}
function Has-SavedImage {
    & docker image inspect $script:state.Image *> $null
    return $LASTEXITCODE -eq 0
}
function Require-SavedImage {
    if (!(Has-SavedImage)) {
        throw "Saved image $($script:state.Image) is unavailable. Data volume $($script:state.Volume) is preserved. Use -Action Recover to build current source and start on a copy of the data."
    }
}
function New-Backup([switch]$WithoutImage) {
    Invoke-Docker @('image', 'inspect', 'caddy:2.10.2-alpine') | Out-Null
    Invoke-Docker @('volume', 'inspect', $script:state.Volume) | Out-Null
    $id = App-Id
    if ($id) {
        $actual = ((Invoke-Docker @('inspect', '--format', '{{.Image}}', $id)) -join '').Trim()
        if ($actual -ne $script:state.Image) { throw 'Running container differs from saved image; resolve state before backing up.' }
    }
    $backup = Join-Path (Join-Path $directory 'backups') ("$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))-$([guid]::NewGuid().ToString('N').Substring(0, 8))")
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    $pin = $null
    if (!$WithoutImage) {
        $pin = "homestead-local:backup-$hash-$mode-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
        Invoke-Docker @('image', 'tag', $script:state.Image, $pin) | Out-Null
    }
    Compose @('stop', 'homestead') | Out-Host
    Invoke-Docker @('run', '--rm', '--user', '0', '--entrypoint', 'sh', '--mount', "type=volume,src=$($script:state.Volume),dst=/source,readonly", '--mount', "type=bind,src=$backup,dst=/backup", 'caddy:2.10.2-alpine', '-c', 'cd /source && tar -cpf /backup/data.tar .') | Out-Host
    Save-Json @{
        Project = $project; ImagePin = $pin; State = $script:state
        ArchiveSha256 = (Get-FileHash -LiteralPath (Join-Path $backup 'data.tar') -Algorithm SHA256).Hash
    } (Join-Path $backup 'metadata.json')
    $script:state.LastBackup = $backup
    Save-State
    Write-Host "Backup: $backup"
    return $backup
}
function Restore-BackupVolume([string]$Backup) {
    $restored = "${project}_restore_$([guid]::NewGuid().ToString('N'))"
    Invoke-Docker @('volume', 'create', $restored) | Out-Null
    Invoke-Docker @('run', '--rm', '--user', '0', '--entrypoint', 'sh', '--mount', "type=volume,src=$restored,dst=/restore", '--mount', "type=bind,src=$Backup,dst=/backup,readonly", 'caddy:2.10.2-alpine', '-c', 'cd /restore && tar -xpf /backup/data.tar') | Out-Host
    return $restored
}
function Show-Status {
    [pscustomobject]@{
        Environment = $Environment; Url = $script:state.Url
        Ready = ((Probe '/health/ready') -eq 200); Image = $script:state.Image
        DataVolume = $script:state.Volume; StatePath = $stateFile; LastBackup = $script:state.LastBackup
    } | Format-List | Out-Host
}
try {
    Invoke-Docker @('info', '--format', '{{.OSType}}') | ForEach-Object { if ($_ -ne 'linux') { throw 'Select Docker Linux containers.' } }
    if (Test-Path -LiteralPath $stateFile) {
        $script:state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json -AsHashtable
        if ($script:state.Project -ne $project) { throw 'State belongs to another project.' }
        if ($Port -and $Port -ne $script:state.Port) { throw 'Existing environment has a saved port.' }
        if ($Subnet -and $Subnet -ne $script:state.Subnet) { throw 'Existing environment has a saved subnet.' }
    } else {
        if ($Action -notin @('Start', 'Reset')) { throw 'Environment is not initialized. Run -Action Start.' }
        if (!$Port) { $Port = if ($Environment -eq 'Sandbox') { 5443 } else { 5444 } }
        if (!$Subnet) { $Subnet = if ($Environment -eq 'Sandbox') { '172.30.80.0/24' } else { '172.30.81.0/24' } }
        if ($Subnet -notmatch '^((?:\d{1,3}\.){3})0/24$') { throw 'Subnet must be an IPv4 /24 ending in .0/24.' }
        $null = [Net.IPAddress]::Parse($Subnet.Split('/')[0])
        $localHost = if ($Environment -eq 'Sandbox') { 'localhost' } else { '127.0.0.1' }
        $script:state = @{
            Project = $project; Host = $localHost; Port = $Port; Subnet = $Subnet
            Url = "https://${localHost}:$Port"; Image = ''; Volume = "${project}_data"
            SetupToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
            LastBackup = $null
        }
    }
    switch ($Action) {
        'Start' {
            if ($Environment -eq 'Sandbox' -or !$script:state.Image) { $script:state.Image = Resolve-Image }
            elseif ($Image) { throw 'Use Upgrade to replace an initialized persistent image.' }
            else { Require-SavedImage }
            Save-State; Start-App; Show-Status
        }
        'Upgrade' {
            Require-SavedImage
            $next = Resolve-Image
            Write-Compose
            $wasRunning = Is-Running
            try { $null = New-Backup }
            catch { if ($wasRunning) { Compose @('start', 'homestead') | Out-Host }; throw }
            $script:state.Image = $next
            Save-State; Start-App; Show-Status
        }
        'Recover' {
            if (Has-SavedImage) { throw 'Saved image is still available. Use -Action Upgrade for a paired image/data backup.' }
            Write-Host 'Saved image is unavailable. Recovery will preserve the original volume and start current source on a copy.'
            Write-Host 'The recovery archive has no paired image; image/data rollback requires recovering the exact old image separately.'
            $next = Resolve-Image
            Write-Compose
            $wasRunning = Is-Running
            try {
                $backup = New-Backup -WithoutImage
                $restored = Restore-BackupVolume $backup
            }
            catch { if ($wasRunning) { Compose @('start', 'homestead') | Out-Host }; throw }
            Write-Host "Preserved original data volume: $($script:state.Volume)"
            $script:state.Image = $next
            $script:state.Volume = $restored
            Save-State; Start-App; Show-Status
        }
        'Backup' {
            Require-SavedImage
            Write-Compose
            $wasRunning = Is-Running
            try { $null = New-Backup }
            finally { if ($wasRunning) { Compose @('start', 'homestead') | Out-Host; Wait-Ready } }
        }
        'Rollback' {
            if (!$BackupPath) { $BackupPath = $script:state.LastBackup }
            if (!$BackupPath) { throw 'No backup is available.' }
            $BackupPath = (Resolve-Path -LiteralPath $BackupPath).Path
            $metadata = Get-Content -LiteralPath (Join-Path $BackupPath 'metadata.json') -Raw | ConvertFrom-Json -AsHashtable
            if ($metadata.Project -ne $project) { throw 'Backup belongs to another environment.' }
            $archive = Join-Path $BackupPath 'data.tar'
            if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $metadata.ArchiveSha256) { throw 'Backup checksum mismatch.' }
            if (!$metadata.ImagePin) { throw 'This recovery archive has no paired image. Current and original data volumes are preserved; recover the exact old image before restoring its data.' }
            & docker image inspect $metadata.ImagePin *> $null
            if ($LASTEXITCODE -ne 0) { throw "Backup image $($metadata.ImagePin) is unavailable. Current data is preserved; restore the pinned image before rollback." }
            $restored = Restore-BackupVolume $BackupPath
            Write-Compose
            Compose @('stop', 'homestead') | Out-Host
            Write-Host "Preserved newer data volume: $($script:state.Volume)"
            $script:state = $metadata.State
            $script:state.Image = ((Invoke-Docker @('image', 'inspect', '--format', '{{.Id}}', $metadata.ImagePin)) -join '').Trim()
            $script:state.Volume = $restored; $script:state.LastBackup = $BackupPath
            Save-State; Start-App; Show-Status
        }
        'Reset' {
            $next = Resolve-Image
            if ($script:state.Image) {
                Write-Compose
                Compose @('down') | Out-Host
                Invoke-Docker @('volume', 'rm', $script:state.Volume) | Out-Host
            }
            $script:state.Volume = "${project}_data_$([guid]::NewGuid().ToString('N'))"
            $script:state.Image = $next
            $script:state.SetupToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
            $script:state.LastBackup = $null
            Save-State; Start-App; Show-Status
        }
        'Stop' { Write-Compose; Compose @('down') | Out-Host }
        'Status' { Write-Compose; Show-Status; Compose @('ps', '-a') | Out-Host }
        'Logs' { Write-Compose; Compose @('logs', '--tail', '100') | Out-Host }
        'Token' { Write-Output $script:state.SetupToken }
        'TrustCertificate' {
            if (!$IsWindows) { throw 'TrustCertificate currently supports the Windows current-user trust store.' }
            Write-Compose
            $proxyId = ((Compose @('ps', '-q', 'caddy')) -join '').Trim()
            if (!$proxyId) { throw 'Start this environment before trusting its certificate.' }
            $rootPath = Join-Path $directory 'root.crt'
            # Export only the public CA certificate from this environment's running proxy.
            Invoke-Docker @('cp', "${proxyId}:/data/caddy/pki/authorities/local/root.crt", $rootPath) | Out-Null
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($rootPath)
            try {
                $constraints = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' }
                if (!$constraints -or !$constraints.CertificateAuthority -or
                    $certificate.Subject -ne $certificate.Issuer -or
                    $certificate.Subject -notlike 'CN=Caddy Local Authority*' -or
                    [DateTime]::Now -lt $certificate.NotBefore -or [DateTime]::Now -gt $certificate.NotAfter) {
                    throw 'The exported certificate is not a currently valid Caddy root CA.'
                }
                $store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
                try {
                    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                    if (!($store.Certificates | Where-Object Thumbprint -EQ $certificate.Thumbprint)) {
                        Write-Host "Windows may require confirmation in a Security Warning dialog for $($certificate.Subject)."
                        Write-Host "Confirm the local CA thumbprint: $($certificate.Thumbprint)"
                        $store.Add($certificate)
                    }
                } finally { $store.Dispose() }
                Write-Host "Trusted local CA in CurrentUser/Root: $($certificate.Thumbprint)"
            } finally { $certificate.Dispose() }
            # This request must validate the certificate; readiness probes alone skip validation.
            $response = Invoke-WebRequest -Uri ($script:state.Url + '/health/ready') -TimeoutSec 5
            if ($response.StatusCode -ne 200) { throw 'Certificate installed, but application readiness failed.' }
            Write-Host "Verified trusted HTTPS: $($script:state.Url)"
        }
        'CompleteSetup' {
            if ((Probe '/health/ready') -ne 200 -or (Probe '/setup') -ne 404) { throw 'Finish administrator setup before clearing the token.' }
            $script:state.SetupToken = ''; Save-State; Write-Compose
            Compose @('up', '-d', '--no-build', '--force-recreate', 'homestead') | Out-Host
            Wait-Ready; Show-Status
        }
    }
} finally { if ($lock) { $lock.Dispose() } }

