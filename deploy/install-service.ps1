<#
.SYNOPSIS
    Builds, installs and starts FileTracert as a Windows Service.

.DESCRIPTION
    Publishes the Host (SPA included), copies it under Program Files, registers the service to
    start automatically, and waits until it answers on loopback. Re-running the script upgrades an
    existing installation in place: the service is stopped, the files are replaced, the service is
    started again. The database is never touched.

    Must be run elevated: registering a service requires it, and so do the two things the product
    exists to do - reading the USN journal and moving files the user owns.

.PARAMETER SourcePublishDir
    Use an already-published folder instead of building one. The folder must contain
    FileTracert.Host.exe and wwwroot\index.html.

.PARAMETER InstallRoot
    Where the binaries go. Default: %ProgramFiles%\FileTracert.

.PARAMETER DataRoot
    Where the catalog and the log database live. Default: %ProgramData%\FileTracert, which is the
    default the Host itself resolves (see DatabaseLocation). Changing it here only changes the ACL
    this script grants - point the Host at it with FileTracert:DatabasePath.

.PARAMETER Port
    Loopback port for the API and the UI. Default 5005. Written into the installed appsettings.json.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\install-service.ps1
#>
[CmdletBinding()]
param(
    [string] $SourcePublishDir,
    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'FileTracert'),
    [string] $DataRoot = (Join-Path $env:ProgramData 'FileTracert'),
    [int]    $Port = 5005
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'FileTracert'
$DisplayName = 'FileTracert'
$Description = 'FileTracert by FAD.iT - indexes and organises files across local and removable drives. Serves its UI on loopback only.'

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated PowerShell (Run as administrator).'
    }
}

function Stop-ServiceIfPresent {
    param([string] $Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service) { return $false }

    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping the running $Name service..."
        # The stop sequence is the product's own: workers checkpoint, the log queue drains.
        # Give it more than the host's ShutdownTimeout (30 s) before calling it stuck.
        Stop-Service -Name $Name -Force
        try {
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(120))
        } catch [System.ServiceProcess.TimeoutException] {
            # Seen for real: a long-running query holds the host well past its ShutdownTimeout.
            # Refusing here is the safe end - the binaries have not been touched yet.
            throw "The $Name service did not stop within 120 s (a long-running request can hold it). " +
                  "Nothing has been changed. Wait for it to stop, or stop it by hand, then run this script again."
        }
    }
    return $true
}

<#
.SYNOPSIS
    Refuses install roots that a mirror-copy or a recursive delete must never be pointed at.
.DESCRIPTION
    Both this script (robocopy /MIR) and the uninstaller (Remove-Item -Recurse) act on whatever
    -InstallRoot names, elevated. A typo there is not a failed install, it is a deleted system
    folder - so the path has to be somewhere it is plausible to own: not a drive root, not a
    well-known Windows folder, and, if it already exists with content, an install of ours.
#>
function Assert-SafeInstallRoot {
    param([string] $Path)

    $full = [IO.Path]::GetFullPath($Path)

    if ($full -eq [IO.Path]::GetPathRoot($full)) {
        throw "Refusing to use a drive root as the install folder: $full"
    }

    $protected = @(
        $env:SystemRoot,
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:ProgramData,
        $env:USERPROFILE,
        (Join-Path $env:SystemRoot 'System32')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($reserved in $protected) {
        if ($full.TrimEnd('\') -ieq ([IO.Path]::GetFullPath($reserved)).TrimEnd('\')) {
            throw "Refusing to use a well-known Windows folder as the install folder: $full"
        }
    }

    if ((Test-Path $full) -and (Get-ChildItem $full -Force | Select-Object -First 1)) {
        if (-not (Test-Path (Join-Path $full 'FileTracert.Host.exe'))) {
            throw "'$full' already has content and does not look like a FileTracert installation " +
                  "(no FileTracert.Host.exe). Refusing to mirror over it - pick an empty folder."
        }
    }
}

function Publish-Host {
    param([string] $Destination)

    $repoRoot = Split-Path -Parent $PSScriptRoot
    $project = Join-Path $repoRoot 'src\backend\FileTracert.Host\FileTracert.Host.csproj'
    if (-not (Test-Path $project)) {
        throw "Cannot find $project. Run this script from a clone of the repository, or pass -SourcePublishDir."
    }

    Write-Host 'Publishing the Host (this also builds the Angular SPA into wwwroot)...'
    & dotnet publish $project -c Release -o $Destination --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

function Assert-PublishedArtifacts {
    param([string] $Directory)

    foreach ($relative in @('FileTracert.Host.exe', 'wwwroot\index.html')) {
        $full = Join-Path $Directory $relative
        if (-not (Test-Path $full)) {
            throw "Incomplete publish output: $full is missing."
        }
    }
}

function Set-ConfiguredPort {
    param([string] $Directory, [int] $Value)

    $settingsPath = Join-Path $Directory 'appsettings.json'
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $settings.FileTracert.Port = $Value
    $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8
}

function Grant-DataFolderAccess {
    param([string] $Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null

    # The catalog is machine-wide on purpose (DatabaseLocation): the service writes it as
    # LocalSystem, and the same file has to be openable by an elevated console run for
    # diagnostics or by the hardware harness, which run as the signed-in user. Inherited
    # ProgramData permissions give Users read-only, so that second reader would fail with an
    # access error on a database it is supposed to own. Granting Modify to the local Users group
    # is the trade-off, and it is deliberate: single-user personal machine, UI on loopback only.
    #
    # icacls, not Get-Acl/Set-Acl: those live in Microsoft.PowerShell.Security, and an install has
    # already been seen to die here because that module failed to autoload - after the service was
    # stopped and the binaries replaced, which is the worst possible moment. icacls is an
    # executable, and the group travels as its SID so the command works on a localised Windows.
    $output = & icacls $Path /grant '*S-1-5-32-545:(OI)(CI)M' 2>&1
    if ($LASTEXITCODE -ne 0) {
        # Not fatal, and deliberately so: the service itself runs as LocalSystem and can write
        # regardless. What is lost is the second reader (console diagnostics, the harness), and
        # that is worth a loud warning, not an install aborted halfway.
        Write-Warning "Could not grant Users:Modify on $Path (icacls exit $LASTEXITCODE): $output"
        Write-Warning "The service will still work. A non-elevated console run or the hardware harness may not be able to open the database."
    }
}

function Wait-ForService {
    param([int] $ListenPort, [int] $TimeoutSeconds = 60)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = 'no attempt was made'
    while ((Get-Date) -lt $deadline) {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.Status -eq 'Stopped') {
            throw "The $ServiceName service stopped right after starting. Check the Windows event log and $DataRoot\filetracert-logs.db."
        }

        try {
            # Any HTTP answer proves Kestrel is listening; the status code does not matter here
            # (unauthenticated calls are supposed to be refused).
            Invoke-WebRequest -Uri "http://127.0.0.1:$ListenPort/" -UseBasicParsing -TimeoutSec 5 | Out-Null
            return $true
        } catch [System.Net.WebException] {
            if ($null -ne $_.Exception.Response) { return $true }
            $lastError = $_.Exception.Message
        } catch {
            # Deliberately not matched against the message: "connection refused" is localised, and
            # a script that decides whether to keep waiting by reading translated text reports a
            # failed install on a machine whose only fault is not being in English. Keep polling
            # until the deadline; the last error is carried out with the failure if one happens.
            $lastError = $_.Exception.Message
        }

        Start-Sleep -Milliseconds 500
    }

    Write-Warning "Last error while waiting for http://127.0.0.1:$ListenPort/ : $lastError"
    return $false
}

Assert-Elevated

$temporaryPublish = $null
try {
    if ([string]::IsNullOrWhiteSpace($SourcePublishDir)) {
        $temporaryPublish = Join-Path ([IO.Path]::GetTempPath()) ("filetracert-publish-" + [Guid]::NewGuid().ToString('N'))
        Publish-Host -Destination $temporaryPublish
        $SourcePublishDir = $temporaryPublish
    }

    Assert-PublishedArtifacts -Directory $SourcePublishDir
    Assert-SafeInstallRoot -Path $InstallRoot

    $existed = Stop-ServiceIfPresent -Name $ServiceName

    Write-Host "Installing into $InstallRoot ..."
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
    # /MIR so an upgrade cannot leave a stale chunk-*.js behind that the new index.html
    # never references but the old one did. Exit codes below 8 are success for robocopy.
    & robocopy $SourcePublishDir $InstallRoot /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed with exit code $LASTEXITCODE while copying to $InstallRoot."
    }

    Set-ConfiguredPort -Directory $InstallRoot -Value $Port
    Grant-DataFolderAccess -Path $DataRoot

    $exePath = Join-Path $InstallRoot 'FileTracert.Host.exe'
    if (-not $existed) {
        Write-Host "Registering the $ServiceName service (automatic start)..."
        # sc.exe rather than New-Service: it is the one that also sets the restart policy below,
        # and the binPath quoting rules are the same for both.
        & sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto DisplayName= "$DisplayName" obj= LocalSystem | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }
        & sc.exe description $ServiceName "$Description" | Out-Null
        # Restart on failure, three times, then leave it alone: a service that crash-loops on a
        # corrupted database should stop trying and be visible as stopped.
        & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    } else {
        Write-Host "Updating the existing $ServiceName service registration..."
        & sc.exe config $ServiceName binPath= "`"$exePath`"" start= auto obj= LocalSystem | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed with exit code $LASTEXITCODE." }
    }

    Write-Host "Starting $ServiceName ..."
    Start-Service -Name $ServiceName

    if (-not (Wait-ForService -ListenPort $Port)) {
        throw "The service started but nothing answered on http://127.0.0.1:$Port/ within the timeout."
    }

    Write-Host ''
    Write-Host "FileTracert is installed and running." -ForegroundColor Green
    Write-Host "  UI            http://127.0.0.1:$Port/   (loopback only)"
    Write-Host "  Binaries      $InstallRoot"
    Write-Host "  Data          $DataRoot"
    Write-Host "  Uninstall     deploy\uninstall-service.ps1"
} finally {
    if ($null -ne $temporaryPublish -and (Test-Path $temporaryPublish)) {
        Remove-Item $temporaryPublish -Recurse -Force -ErrorAction SilentlyContinue
    }
}
