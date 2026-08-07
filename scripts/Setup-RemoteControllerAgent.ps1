[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'RemoteController.Agent.config.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ConfigProperty([object]$Config, [string]$Name) {
    $property = $Config.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Resolve-ConfigPath([string]$Path, [string]$BaseDirectory) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A configuration path is required.'
    }
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return [IO.Path]::GetFullPath((Join-Path $BaseDirectory $Path))
}

function Resolve-ConfiguredPath([object]$Config, [string]$Name, [string]$Default, [string]$BaseDirectory) {
    $value = Get-ConfigProperty $Config $Name
    $text = if ($null -eq $value) { $Default } else { [string]$value }
    if ([string]::IsNullOrWhiteSpace($text)) { $text = $Default }
    return Resolve-ConfigPath $text $BaseDirectory
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This setup requires an elevated administrator session.'
    }
}

function Quote-PowerShellLiteral([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

function Invoke-ElevatedSelf {
    $outputPath = Join-Path ([IO.Path]::GetTempPath()) ("RemoteControllerAgentSetup-{0}.log" -f [Guid]::NewGuid().ToString('N'))
    $scriptLiteral = Quote-PowerShellLiteral $PSCommandPath
    $configLiteral = Quote-PowerShellLiteral $script:ConfigPath
    $outputLiteral = Quote-PowerShellLiteral $outputPath
    $command = "& $scriptLiteral -ConfigPath $configLiteral *> $outputLiteral; exit `$LASTEXITCODE"
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $exitCode = 1

    try {
        $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encodedCommand) `
            -Verb RunAs -Wait -PassThru
        $exitCode = $process.ExitCode

        if (Test-Path -LiteralPath $outputPath -PathType Leaf) {
            Get-Content -LiteralPath $outputPath | ForEach-Object { Write-Host $_ }
        }
    }
    finally {
        Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
    }

    exit $exitCode
}

function Stop-ManagedService([string]$ServiceName) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service -or $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        return
    }

    Stop-Service -Name $ServiceName -Force
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $service = Get-Service -Name $ServiceName
        if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Service '$ServiceName' did not stop within 30 seconds."
}

function Start-ManagedService([string]$ServiceName) {
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        Start-Service -Name $ServiceName
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $service = Get-Service -Name $ServiceName
        if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
            Write-Host "[OK] Service '$ServiceName' is running."
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Service '$ServiceName' did not reach the running state within 30 seconds."
}

function Invoke-AgentCommand([string]$AgentExe, [string]$Command) {
    $previousDataRoot = $env:RC_AGENT_DATA_ROOT
    $env:RC_AGENT_DATA_ROOT = $script:DataRoot
    try {
        $output = @(& $AgentExe $Command 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "Rc.Agent.exe $Command failed with exit code $LASTEXITCODE`: $($output -join [Environment]::NewLine)"
        }
        return $output
    }
    finally {
        $env:RC_AGENT_DATA_ROOT = $previousDataRoot
    }
}

$script:ConfigPath = Resolve-ConfigPath $ConfigPath $PSScriptRoot
if (-not (Test-Path -LiteralPath $script:ConfigPath -PathType Leaf)) {
    throw "Configuration file was not found: $script:ConfigPath"
}

$configDirectory = Split-Path -Parent $script:ConfigPath
$config = Get-Content -LiteralPath $script:ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$sourcePath = Resolve-ConfiguredPath $config 'SourcePath' '.' $configDirectory
$installPath = Resolve-ConfiguredPath $config 'InstallPath' (Join-Path $(if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }) 'RemoteController') $configDirectory
$dataRoot = Resolve-ConfiguredPath $config 'DataRoot' (Join-Path $env:ProgramData 'RemoteController') $configDirectory
$script:DataRoot = $dataRoot

$tcpPortValue = Get-ConfigProperty $config 'TcpPort'
$tcpPort = if ($null -eq $tcpPortValue) { 43001 } else { [int]$tcpPortValue }
if ($tcpPort -notin 1..65535) { throw "TcpPort must be between 1 and 65535: $tcpPort" }

$uiUserValue = Get-ConfigProperty $config 'UiUser'
$uiUser = if ($null -eq $uiUserValue -or [string]::IsNullOrWhiteSpace([string]$uiUserValue)) {
    [Security.Principal.WindowsIdentity]::GetCurrent().Name
} else {
    [string]$uiUserValue
}

$noFirewallRuleValue = Get-ConfigProperty $config 'NoFirewallRule'
$noFirewallRule = $false
if ($null -ne $noFirewallRuleValue) { $noFirewallRule = [bool]$noFirewallRuleValue }
$regenerateIdentityValue = Get-ConfigProperty $config 'RegenerateIdentity'
$regenerateIdentity = $true
if ($null -ne $regenerateIdentityValue) { $regenerateIdentity = [bool]$regenerateIdentityValue }
$armPairingValue = Get-ConfigProperty $config 'ArmPairing'
$armPairing = $true
if ($null -ne $armPairingValue) { $armPairing = [bool]$armPairingValue }

if (-not ([Security.Principal.WindowsIdentity]::GetCurrent())) { throw 'Unable to determine the current Windows identity.' }
try {
    Assert-Administrator
}
catch {
    if ($_.Exception.Message -like '*elevated administrator*') {
        Invoke-ElevatedSelf
    }
    throw
}

$installer = Join-Path $sourcePath 'Install-RemoteController.ps1'
$agentExe = Join-Path $installPath 'Rc.Agent.exe'
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath 'Rc.Agent.exe') -PathType Leaf)) {
    # When this script runs from the copied publish package, the package itself
    # is the first fallback. When it runs from scripts/, use the repository
    # artifacts\publish directory instead.
    $packageCandidates = @(
        [IO.Path]::GetFullPath($PSScriptRoot),
        [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\publish'))
    ) | Select-Object -Unique
    $repositoryPackage = $packageCandidates | Where-Object {
        Test-Path -LiteralPath (Join-Path $_ 'Rc.Agent.exe') -PathType Leaf
    } | Select-Object -First 1
    if ($null -ne $repositoryPackage) {
        $sourcePath = $repositoryPackage
        $installer = Join-Path $repositoryPackage 'Install-RemoteController.ps1'
        Write-Host "Using repository publish package: $sourcePath"
    }
}
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Missing installer: $installer" }
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath 'Rc.Agent.exe') -PathType Leaf)) {
    throw "Missing package artifact: Rc.Agent.exe. Checked source path: $sourcePath"
}

Write-Host "Using configuration: $script:ConfigPath"
Write-Host "Source package: $sourcePath"
Write-Host "Install path: $installPath"
Write-Host "Data root: $dataRoot"
Write-Host "TCP port: $tcpPort"
Write-Host "UI user: $uiUser"

$installerArguments = @{
    SourcePath = $sourcePath
    InstallPath = $installPath
    DataRoot = $dataRoot
    TcpPort = $tcpPort
    UiUser = $uiUser
}
if ($noFirewallRule) { $installerArguments.NoFirewallRule = $true }

Write-Host '[1/5] Installing or refreshing the service package...'
& $installer @installerArguments
if (-not $?) { throw 'Install-RemoteController.ps1 failed.' }

if (-not (Test-Path -LiteralPath $agentExe -PathType Leaf)) { throw "Installed Agent was not found: $agentExe" }

Write-Host '[2/5] Stopping services before local identity operations...'
Stop-ManagedService 'RemoteControllerAgent'
Stop-ManagedService 'RemoteControllerBroker'

if ($regenerateIdentity) {
    Write-Warning 'RegenerateIdentity=true: the existing controller pairing will be removed and a new TLS identity will be generated.'
    Write-Host '[3/5] Removing the old pairing and scheduling TLS identity regeneration...'
    $unpairOutput = Invoke-AgentCommand $agentExe 'unpair'
    $unpairOutput | ForEach-Object { Write-Host $_ }
    $repairOutput = Invoke-AgentCommand $agentExe 'repair-tls-identity'
    $repairOutput | ForEach-Object { Write-Host $_ }
    $pairingCodePath = Join-Path $dataRoot 'pairing-code.json'
    if (Test-Path -LiteralPath $pairingCodePath -PathType Leaf) {
        Remove-Item -LiteralPath $pairingCodePath -Force
        Write-Host 'Removed the previous one-time pairing code.'
    }
} else {
    Write-Host '[3/5] Keeping the existing TLS identity and controller pairing.'
}

Write-Host '[4/5] Starting services...'
Start-ManagedService 'RemoteControllerBroker'
Start-ManagedService 'RemoteControllerAgent'

Start-Sleep -Milliseconds 500
$identityOutput = Invoke-AgentCommand $agentExe 'identity'
$identityJson = ($identityOutput -join [Environment]::NewLine) | ConvertFrom-Json
if (-not $identityJson.ok) { throw "Agent identity response was not successful: $($identityOutput -join [Environment]::NewLine)" }
$identityResult = $identityJson.result
Write-Host "Device ID: $($identityResult.deviceId)"
Write-Host "TLS SHA-256 fingerprint: $($identityResult.certificateSha256Fingerprint)"
Write-Host 'The private key remains protected in the Agent data root and is not printed.'

$pairingResult = $null
if ($armPairing) {
    Write-Host '[5/5] Arming a one-time pairing code...'
    $pairingOutput = Invoke-AgentCommand $agentExe 'arm-pairing'
    $pairingJson = ($pairingOutput -join [Environment]::NewLine) | ConvertFrom-Json
    if (-not $pairingJson.ok) { throw "Pairing response was not successful: $($pairingOutput -join [Environment]::NewLine)" }
    $pairingResult = $pairingJson.result
    Write-Host "Pairing code: $($pairingResult.oneTimeCode)"
    Write-Host "Pairing expires at (UTC): $($pairingResult.expiresAtUtc)"
} else {
    Write-Host '[5/5] Pairing code generation is disabled by configuration.'
}

Write-Host ''
Write-Host '[OK] RemoteController Agent setup completed.'
Write-Host '=== Setup information (copyable) ==='
Write-Host "Device ID: $($identityResult.deviceId)"
Write-Host "TLS SHA-256 fingerprint: $($identityResult.certificateSha256Fingerprint)"
Write-Host "TCP port: $tcpPort"
Write-Host "Install path: $installPath"
Write-Host "Data root: $dataRoot"
Write-Host "UI user: $uiUser"
$pairedProperty = $identityResult.PSObject.Properties['paired']
if ($null -ne $pairedProperty) {
    Write-Host "Currently paired: $($pairedProperty.Value)"
} else {
    Write-Host 'Currently paired: not reported by the local identity command.'
}
if ($null -ne $pairingResult) {
    Write-Host "Pairing code: $($pairingResult.oneTimeCode)"
    Write-Host "Pairing code expires at (UTC): $($pairingResult.expiresAtUtc)"
} else {
    Write-Host 'Pairing code: not generated (ArmPairing=false)'
}
Write-Host 'The private key is not displayed and remains protected by the data-root ACL.'
Write-Host 'Run this script again to refresh the package and restart services.'
if ($regenerateIdentity) {
    Write-Host 'Because RegenerateIdentity=true, every run creates a new fingerprint and requires the Controller to pair again.'
}
