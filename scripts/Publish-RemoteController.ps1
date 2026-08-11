[CmdletBinding()]
param(
    [string]$OutputPath,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root 'artifacts\publish'
}
$output = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $root $OutputPath }))
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = 'dotnet' }
$projects = @{
    'Rc.Agent' = 'src\Rc.Agent\Rc.Agent.csproj'
    'Rc.PrivilegedBroker' = 'src\Rc.PrivilegedBroker\Rc.PrivilegedBroker.csproj'
    'Rc.TaskHost' = 'src\Rc.TaskHost\Rc.TaskHost.csproj'
    'Rc.UiAgent' = 'src\Rc.UiAgent\Rc.UiAgent.csproj'
    'Rc.UiTestApp' = 'src\Rc.UiTestApp\Rc.UiTestApp.csproj'
    'Rc.InteractiveTestApp' = 'src\Rc.InteractiveTestApp\Rc.InteractiveTestApp.csproj'
    'Rc.Cli' = 'src\Rc.Cli\Rc.Cli.csproj'
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($name in $projects.Keys) {
    $project = Join-Path $root $projects[$name]
    # Keep only the single-file executable in the package root. Older versions
    # of this script left every self-contained staging directory below the
    # package, duplicating hundreds of megabytes and potentially exceeding the
    # Agent's one-gigabyte update manifest limit.
    $legacyStaging = Join-Path $output $name
    if (Test-Path -LiteralPath $legacyStaging -PathType Container) {
        Remove-Item -LiteralPath $legacyStaging -Recurse -Force
    }
    $staging = Join-Path ([IO.Path]::GetTempPath()) ("RemoteController-publish-$name-" + [guid]::NewGuid().ToString('N'))
    try {
        & $dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output $staging
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $name." }
        $executable = Join-Path $staging "$name.exe"
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Publish output for $name has no executable." }
        Copy-Item -LiteralPath $executable -Destination (Join-Path $output "$name.exe") -Force
    }
    finally {
        if (Test-Path -LiteralPath $staging -PathType Container) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
}

$packageFiles = @('Install-RemoteController.ps1', 'Update-RemoteController.ps1', 'Invoke-RemoteControllerDetachedUpdate.ps1', 'Uninstall-RemoteController.ps1', 'Uninstall-RemoteController.cmd', 'Start-RemoteController.cmd', 'Start-RemoteControllerUiTest.cmd', 'Repair-RemoteControllerTlsIdentity.ps1', 'Test-RemoteControllerUi.ps1', 'Setup-RemoteControllerAgent.ps1', 'Setup-RemoteControllerAgent.cmd', 'RemoteController.Agent.config.json')
foreach ($name in $projects.Keys) {
    if (-not (Test-Path -LiteralPath (Join-Path $output "$name.exe") -PathType Leaf)) { throw "Missing packaged executable: $name.exe" }
}
foreach ($file in $packageFiles) {
    $sourceFile = Join-Path $PSScriptRoot $file
    if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "Missing deployment script: $file" }
    Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $output $file) -Force
}
Write-Host "Self-contained Windows x64 deployment package complete: $output"
