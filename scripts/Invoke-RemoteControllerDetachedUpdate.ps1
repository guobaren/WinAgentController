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
    [Parameter(Mandatory = $true)][string]$TaskName,
    [ValidateRange(1, 300)][int]$ReadyTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$result = [ordered]@{
    succeeded = $false
    exitCode = 1
    failureMessage = $null
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

    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $UpdateScript `
        -SourcePath $SourcePath -InstallPath $InstallPath -DataRoot $DataRoot -TcpPort $TcpPort
    if ($LASTEXITCODE -ne 0) {
        throw "Update-RemoteController.ps1 exited with code $LASTEXITCODE."
    }

    $result.succeeded = $true
    $result.exitCode = 0
}
catch {
    $result.failureMessage = $_.Exception.Message
}
finally {
    $resultDirectory = Split-Path -Parent $ResultPath
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
