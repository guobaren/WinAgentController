[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'tools\Rc.TestRemoteControl\Rc.TestRemoteControl.csproj'
$output = Join-Path $root 'artifacts\test-tools'
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = 'dotnet' }

New-Item -ItemType Directory -Path $output -Force | Out-Null
$executable = Join-Path $output 'Rc.TestRemoteControl.exe'
$staging = Join-Path ([IO.Path]::GetTempPath()) ('Rc-TestRemoteControl-' + [guid]::NewGuid().ToString('N'))
try {
    & $dotnet publish $project --configuration $Configuration --output $staging
    if ($LASTEXITCODE -ne 0) { throw 'Test remote-control tool publish failed.' }
    $stagedExecutable = Join-Path $staging 'Rc.TestRemoteControl.exe'
    if (-not (Test-Path -LiteralPath $stagedExecutable -PathType Leaf)) { throw "Missing staged test executable: $stagedExecutable" }
    Copy-Item -LiteralPath $stagedExecutable -Destination $executable -Force
}
finally {
    if (Test-Path -LiteralPath $staging -PathType Container) { Remove-Item -LiteralPath $staging -Recurse -Force }
}

foreach ($extra in @(Get-ChildItem -LiteralPath $output -File | Where-Object { $_.Name -ne 'Rc.TestRemoteControl.exe' })) {
    Remove-Item -LiteralPath $extra.FullName -Force
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Missing test executable: $executable" }
Write-Host "Test-only remote-control executable: $executable"
