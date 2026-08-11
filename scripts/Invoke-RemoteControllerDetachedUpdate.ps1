[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UpdateScript,
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$InstallPath,
    [Parameter(Mandatory = $true)][string]$DataRoot,
    [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$TcpPort,
    [Parameter(Mandatory = $true)][string]$ReadyPath,
    [Parameter(Mandatory = $true)][string]$StartedPath,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [string]$StandardOutputPath,
    [string]$StandardErrorPath,
    [Parameter(Mandatory = $true)][string]$TaskName,
    [ValidateRange(1, 300)][int]$ReadyTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resultDirectory = Split-Path -Parent $ResultPath
if ([string]::IsNullOrWhiteSpace($StandardOutputPath)) {
    $StandardOutputPath = Join-Path $resultDirectory 'update-stdout.log'
}
if ([string]::IsNullOrWhiteSpace($StandardErrorPath)) {
    $StandardErrorPath = Join-Path $resultDirectory 'update-stderr.log'
}

$result = [ordered]@{
    succeeded = $false
    exitCode = 1
    failureMessage = $null
    standardOutputPath = $StandardOutputPath
    standardErrorPath = $StandardErrorPath
}

try {
    $deadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
    while (-not (Test-Path -LiteralPath $ReadyPath -PathType Leaf)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "The Agent did not persist the update-ready signal within $ReadyTimeoutSeconds seconds."
        }
        Start-Sleep -Milliseconds 100
    }

    Set-Content -LiteralPath $StartedPath -Value ([DateTimeOffset]::UtcNow.ToString('O')) -Encoding Ascii
    # Give the Agent time to send the Complete response before its service is stopped.
    Start-Sleep -Seconds 2

    $logDirectory = Split-Path -Parent $StandardOutputPath
    if (-not [string]::IsNullOrWhiteSpace($logDirectory)) {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    }
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell promotes native stderr to an ErrorRecord. Persist it,
        # but use the child process exit code as the update success boundary.
        $ErrorActionPreference = 'Continue'
        & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $UpdateScript `
            -SourcePath $SourcePath -InstallPath $InstallPath -DataRoot $DataRoot -TcpPort $TcpPort `
            1> $StandardOutputPath 2> $StandardErrorPath
        $updateExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($updateExitCode -ne 0) {
        $result.exitCode = $updateExitCode
        throw "Update-RemoteController.ps1 exited with code $updateExitCode."
    }

    $result.succeeded = $true
    $result.exitCode = 0
}
catch {
    $result.failureMessage = $_.Exception.Message
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($resultDirectory)) {
        New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    }
    $temporaryResult = "$ResultPath.tmp"
    $result | ConvertTo-Json -Compress | Set-Content -LiteralPath $temporaryResult -Encoding UTF8
    Move-Item -LiteralPath $temporaryResult -Destination $ResultPath -Force

    # Task cleanup is best-effort and happens only after the durable result exists.
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
}

if (-not $result.succeeded) { exit $result.exitCode }
