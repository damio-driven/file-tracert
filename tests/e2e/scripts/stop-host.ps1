<#
.SYNOPSIS
Asks a running FileTracert.Host to shut down the way Ctrl+Break does, and exits.

.DESCRIPTION
This script is deliberately sacrificial: attaching to another process's console detaches the
caller from its own, which would break whatever pipe the runner is reading. So the runner spawns
a fresh PowerShell for one shot and throws it away.

Why a console control event and not `taskkill`: `taskkill` without /F cannot stop a windowless
console app at all ("can only be terminated forcefully"), and with /F it is TerminateProcess —
the workers never run their stop sequence, the log queue never drains, and the test would prove
nothing about the shutdown path step 11c built. CTRL_C_EVENT reaches .NET as SIGINT, which is what
the generic host's console lifetime listens for, and the real stop sequence runs.

Sending to process group 0 means "every process attached to this console". That is only the Host,
because start-host.ps1 gave it a console of its own.

Exit codes: 0 sent, 2 could not attach (already gone, or no console), 3 the event was refused.
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][int]$TargetPid)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ConsoleCtrl
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
}
'@

$CTRL_C_EVENT = 0

[void][ConsoleCtrl]::FreeConsole()
if (-not [ConsoleCtrl]::AttachConsole([uint32]$TargetPid)) { exit 2 }

# Ignore the event we are about to raise: it is addressed to the whole console group, and this
# process is in it now. Ctrl+C rather than Ctrl+Break precisely because this flag makes it
# ignorable — Ctrl+Break would kill this script before it could report anything.
[void][ConsoleCtrl]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)

if (-not [ConsoleCtrl]::GenerateConsoleCtrlEvent($CTRL_C_EVENT, 0)) { exit 3 }
exit 0
