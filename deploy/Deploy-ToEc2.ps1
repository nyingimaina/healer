<#
.SYNOPSIS
Copies a built Healer .deb onto an Ubuntu EC2 box over scp, and optionally installs it there
immediately over ssh. Windows/PowerShell equivalent of deploy-to-ec2.sh alongside this file.

.PARAMETER KeyFile
Path to your EC2 .pem private key. Prompted for if omitted.

.PARAMETER Ip
EC2 instance public IP or hostname. Prompted for if omitted.

.PARAMETER User
SSH username. Defaults to "ubuntu", matching the Ubuntu .deb this ships.

.PARAMETER DebFile
Path to the .deb to deploy. Defaults to the newest dist\healer_*_amd64.deb.

.PARAMETER Install
Install it immediately over ssh after copying, no prompt.

.PARAMETER NoInstall
Copy only; don't ask about installing.

.EXAMPLE
.\Deploy-ToEc2.ps1
Fully guided — prompts for everything not already known.
#>
[CmdletBinding()]
param(
    [string]$KeyFile,
    [string]$Ip,
    [string]$User = "ubuntu",
    [string]$DebFile,
    [switch]$Install,
    [switch]$NoInstall
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not $DebFile) {
    $distDir = Join-Path $RepoRoot "dist"
    $candidate = Get-ChildItem -Path $distDir -Filter "healer_*_amd64.deb" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($candidate) {
        $DebFile = $candidate.FullName
    }
}
if (-not $DebFile -or -not (Test-Path $DebFile)) {
    Write-Error "Couldn't find a built .deb. Run deploy/build-deb.sh (via WSL) first, or pass one explicitly with -DebFile."
    exit 1
}

if (-not $KeyFile) {
    $KeyFile = Read-Host "Path to your EC2 .pem key file"
}
if (-not (Test-Path $KeyFile)) {
    Write-Error "Key file not found: $KeyFile"
    exit 1
}

if (-not $Ip) {
    $Ip = Read-Host "EC2 instance public IP or hostname"
}

# Lock down the key file's permissions the way AWS requires — same technique documented in
# docs/DEPLOY-FROM-WINDOWS.md's Part 4, Option 2.
icacls $KeyFile /inheritance:r | Out-Null
icacls $KeyFile /grant:r "$($env:USERNAME):(R)" | Out-Null

$remoteFileName = Split-Path -Leaf $DebFile
$remotePath = "/tmp/$remoteFileName"

Write-Host "Copying $remoteFileName to ${User}@${Ip}:$remotePath ..."
scp -i $KeyFile -o StrictHostKeyChecking=accept-new $DebFile "${User}@${Ip}:$remotePath"
Write-Host "Copied."

$doInstall = $Install.IsPresent
if (-not $Install.IsPresent -and -not $NoInstall.IsPresent) {
    $reply = Read-Host "Install it now over SSH? This runs the guided setup wizard right here. [Y/n]"
    $doInstall = -not ($reply -match '^[nN]')
}

if ($doInstall) {
    Write-Host "Connecting and installing — the setup wizard should appear below..."
    # -t forces a real pseudo-terminal even for this single remote command. Without it, ssh
    # running one command non-interactively does NOT allocate a tty, and healer-setup would
    # silently take its "no terminal, stage only" branch instead of showing the wizard — the exact
    # gotcha documented in docs/ARCHITECTURE.md's dpkg-postinst risk callout. This is what avoids it.
    ssh -t -i $KeyFile -o StrictHostKeyChecking=accept-new "${User}@${Ip}" "sudo dpkg -i '$remotePath'"
}
else {
    Write-Host "Done. To install later: ssh -i `"$KeyFile`" ${User}@${Ip}   then:   sudo dpkg -i $remotePath"
}
