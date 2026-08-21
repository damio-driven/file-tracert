<#
.SYNOPSIS
    Stops and removes the FileTracert Windows Service.

.DESCRIPTION
    Stops the service, deletes its registration, and removes the installed binaries. The database
    is left alone: it is the user's catalog, built over weeks, and an uninstall is not a request to
    throw it away. Pass -RemoveData to delete it as well - that asks for confirmation, and says
    exactly which folder it is about to delete.

.PARAMETER InstallRoot
    Where the binaries were installed. Default: %ProgramFiles%\FileTracert.

.PARAMETER DataRoot
    Where the catalog lives. Only read unless -RemoveData is given.
    Default: %ProgramData%\FileTracert.

.PARAMETER RemoveData
    Also delete the catalog and the log database. Prompts unless -Force is given too.

.PARAMETER Force
    Skip the confirmation prompt for -RemoveData.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\uninstall-service.ps1
#>
[CmdletBinding()]
param(
    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'FileTracert'),
    [string] $DataRoot = (Join-Path $env:ProgramData 'FileTracert'),
    [switch] $RemoveData,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'FileTracert'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run from an elevated PowerShell (Run as administrator).'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName ..."
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }

    Write-Host "Removing the $ServiceName service registration..."
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }

    # Deletion is asynchronous when anything still holds a handle on the service (services.msc
    # open, for instance): it is then marked for deletion and disappears at the next reboot.
    # Say so rather than reporting a clean uninstall that has not happened yet.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and $null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 500
    }
    if ($null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        Write-Warning "The service is marked for deletion but still registered. Close services.msc and any open Event Viewer, or it will disappear at the next reboot."
    }
} else {
    Write-Host "No $ServiceName service is registered; removing whatever files are left."
}

if (Test-Path $InstallRoot) {
    Write-Host "Removing $InstallRoot ..."
    Remove-Item $InstallRoot -Recurse -Force
}

if ($RemoveData) {
    if (-not $Force) {
        Write-Host ''
        Write-Warning "This deletes your catalog: $DataRoot"
        $answer = Read-Host "Type the word DELETE to confirm"
        if ($answer -cne 'DELETE') {
            Write-Host 'Left the data alone.'
            $RemoveData = $false
        }
    }
    if ($RemoveData -and (Test-Path $DataRoot)) {
        Remove-Item $DataRoot -Recurse -Force
        Write-Host "Deleted $DataRoot"
    }
} elseif (Test-Path $DataRoot) {
    Write-Host ''
    Write-Host "Left your catalog in place: $DataRoot"
    Write-Host "Delete it with: deploy\uninstall-service.ps1 -RemoveData"
}

Write-Host 'FileTracert is uninstalled.' -ForegroundColor Green
