<#
.SYNOPSIS
Starts FileTracert.Host in a console of its own and prints its process id.

.DESCRIPTION
The test runner cannot start the Host directly: Node's `spawn({ detached: true })` maps to
DETACHED_PROCESS on Windows, which leaves the child with no console at all, and a non-detached
child *shares* the runner's console — so a Ctrl+Break aimed at the Host would hit Playwright too.
`Start-Process` gives the Host its own (hidden) console, which is what makes the graceful stop in
stop-host.ps1 both possible and safe.

Configuration travels as environment variables (the double-underscore form ASP.NET Core binds to
`FileTracert:*`), set here so the child inherits them and the runner's own environment stays clean.

The process id is written to a file rather than to stdout, and the runner starts this script with
no pipes at all. `Start-Process` with file redirection creates the Host with handle inheritance on,
so the Host would hold a copy of whatever pipe the runner was reading — and a runner waiting for
that pipe to close would be waiting for the Host to exit, which is precisely backwards.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$ExePath,
  [Parameter(Mandatory = $true)][string]$WorkingDirectory,
  [Parameter(Mandatory = $true)][string]$LogPath,
  [Parameter(Mandatory = $true)][string]$PidPath,
  [Parameter(Mandatory = $true)][int]$Port,
  [Parameter(Mandatory = $true)][string]$DatabasePath,
  [int]$VolumeSyncIntervalSeconds = 3600,
  [int]$ScanPollIntervalSeconds = 3600,
  [string]$Environment = 'Production'
)

$ErrorActionPreference = 'Stop'

$env:ASPNETCORE_ENVIRONMENT = $Environment
$env:DOTNET_ENVIRONMENT = $Environment
$env:FileTracert__Port = "$Port"
$env:FileTracert__DatabasePath = $DatabasePath
$env:FileTracert__VolumeSyncIntervalSeconds = "$VolumeSyncIntervalSeconds"
$env:FileTracert__ScanPollIntervalSeconds = "$ScanPollIntervalSeconds"

$process = Start-Process `
  -FilePath $ExePath `
  -WorkingDirectory $WorkingDirectory `
  -WindowStyle Hidden `
  -RedirectStandardOutput $LogPath `
  -RedirectStandardError "$LogPath.err" `
  -PassThru

Set-Content -Path $PidPath -Value $process.Id -Encoding ascii
