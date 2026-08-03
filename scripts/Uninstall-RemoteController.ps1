[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$InstallPath = (Join-Path $(if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }) 'RemoteController'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'RemoteController'),
    [switch]$KeepData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ConfirmPreference = 'None'
$services = @('RemoteControllerAgent', 'RemoteControllerBroker')
$firewallRule = 'RemoteController Agent TCP'
$uiTaskName = 'RemoteControllerUiAgent'

function Invoke-ElevatedSelf {
    $command = "& '{0}' -InstallPath '{1}' -DataRoot '{2}'" -f $PSCommandPath, $InstallPath, $DataRoot
    if ($KeepData) { $command += ' -KeepData' }
    $arguments = '-NoProfile -ExecutionPolicy Bypass -Command "{0}"' -f $command

    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

function Stop-UiAgent {
    $task = Get-ScheduledTask -TaskName $uiTaskName -ErrorAction SilentlyContinue
    if ($null -ne $task -and $task.State -eq 'Running') {
        Stop-ScheduledTask -TaskName $uiTaskName
    }

    # The task can already be absent after a partial uninstall. In that case,
    # or when Task Scheduler has not yet ended it, terminate only this product's
    # UI Agent process so its executable is no longer locked for removal.
    foreach ($process in @(Get-Process -Name 'Rc.UiAgent', 'Rc.UiTestApp' -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $processes = @(Get-Process -Name 'Rc.UiAgent', 'Rc.UiTestApp' -ErrorAction SilentlyContinue)
        if ($processes.Count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'RemoteController UI processes did not stop within 30 seconds.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $PSBoundParameters.ContainsKey('WhatIf') -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Invoke-ElevatedSelf
}

if ($PSCmdlet.ShouldProcess($uiTaskName, 'Stop UI Agent before removal')) {
    Stop-UiAgent
}

foreach ($name in $services) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service -and $PSCmdlet.ShouldProcess($name, 'Stop and delete Windows service')) {
        if ($service.Status -ne 'Stopped') { Stop-Service -Name $name -Force -ErrorAction SilentlyContinue }
        & "$env:SystemRoot\System32\sc.exe" delete $name | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Could not delete service $name" }
    }
}
if ($PSCmdlet.ShouldProcess($uiTaskName, 'Remove UI Agent logon task')) {
    Unregister-ScheduledTask -TaskName $uiTaskName -Confirm:$false -ErrorAction SilentlyContinue
}
if ($PSCmdlet.ShouldProcess($firewallRule, 'Remove firewall rule')) {
    Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}
if (Test-Path -LiteralPath $InstallPath -PathType Container) {
    $resolvedInstall = (Resolve-Path -LiteralPath $InstallPath).Path
    $programFilesRoot = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $programFiles = [IO.Path]::GetFullPath($programFilesRoot).TrimEnd('\') + '\'
    if (-not $resolvedInstall.StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to remove install path outside Program Files: $resolvedInstall" }
    if ($PSCmdlet.ShouldProcess($resolvedInstall, 'Remove installed binaries')) { Remove-Item -LiteralPath $resolvedInstall -Recurse -Force }
}
if (-not $KeepData -and (Test-Path -LiteralPath $DataRoot -PathType Container)) {
    $resolvedData = (Resolve-Path -LiteralPath $DataRoot).Path
    $programData = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\') + '\'
    if (-not $resolvedData.StartsWith($programData, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to remove data path outside ProgramData: $resolvedData" }
    if ($PSCmdlet.ShouldProcess($resolvedData, 'Remove RemoteController data')) { Remove-Item -LiteralPath $resolvedData -Recurse -Force }
}
Write-Host 'RemoteController services uninstalled.'
