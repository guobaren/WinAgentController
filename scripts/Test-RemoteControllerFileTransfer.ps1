[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Target,

    [Parameter(Mandatory = $true)]
    [string]$Fingerprint,

    [string]$CliPath = (Join-Path $PSScriptRoot '..\artifacts\publish\Rc.Cli.exe'),

    [string]$WorkRoot = (Join-Path $PSScriptRoot '..\artifacts\transfer-benchmark'),

    [string]$RemoteRoot = ('rc-transfer-benchmark-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),

    [ValidateRange(1, 67108864)]
    [int]$ChunkSize = 64 * 1024 * 1024,

    [switch]$SkipGenerate,

    # 默认在测试成功后清理本地 source/download 与远端基准目录；
    # 指定本开关可保留现场（排查失败或复测时使用）。
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'

function Write-PatternFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$Length,
        [Parameter(Mandatory = $true)][int]$Seed,
        [int]$BufferSize = 65536
    )

    $parent = Split-Path -Parent $Path
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    $buffer = New-Object byte[] ([Math]::Min($BufferSize, [Math]::Max(1L, $Length)))
    (New-Object System.Random $Seed).NextBytes($buffer)
    $stream = New-Object System.IO.FileStream(
        $Path,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None,
        1048576,
        [System.IO.FileOptions]::SequentialScan)
    try {
        $remaining = $Length
        while ($remaining -gt 0) {
            $count = [int][Math]::Min($buffer.Length, $remaining)
            $stream.Write($buffer, 0, $count)
            $remaining -= $count
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-CopyMeasurement {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][long]$Bytes
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $CliPath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) {
        throw "$Name could not start Rc.Cli.exe."
    }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $output = $stdoutTask.GetAwaiter().GetResult()
    $diagnostics = $stderrTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()
    $stopwatch.Stop()
    if (-not [string]::IsNullOrWhiteSpace($diagnostics)) {
        [Console]::Error.Write($diagnostics)
    }
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode. Output: $output Diagnostics: $diagnostics"
    }
    $seconds = [Math]::Max($stopwatch.Elapsed.TotalSeconds, 0.001)
    $measurement = [pscustomobject]@{
        Name = $Name
        Bytes = $Bytes
        Seconds = [Math]::Round($seconds, 3)
        MiBPerSecond = [Math]::Round(($Bytes / 1MB) / $seconds, 2)
    }
    [Console]::Error.WriteLine("[benchmark] $($measurement.Name) bytes=$($measurement.Bytes) seconds=$($measurement.Seconds) MiB/s=$($measurement.MiBPerSecond)")
    return $measurement
}

function Get-TreeDigest {
    param([Parameter(Mandatory = $true)][string]$Root)

    $resolved = [System.IO.Path]::GetFullPath($Root)
    $items = Get-ChildItem -LiteralPath $resolved -File -Recurse | Sort-Object FullName
    foreach ($item in $items) {
        [pscustomobject]@{
            Path = $item.FullName.Substring($resolved.Length).TrimStart('\').Replace('\', '/')
            Length = $item.Length
            Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        }
    }
}

function Assert-DigestEqual {
    param(
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $expectedJson = @($Expected) | ConvertTo-Json -Compress
    $actualJson = @($Actual) | ConvertTo-Json -Compress
    if ($expectedJson -cne $actualJson) {
        throw "$Label SHA-256 manifest mismatch."
    }
}

$CliPath = [System.IO.Path]::GetFullPath($CliPath)
$WorkRoot = [System.IO.Path]::GetFullPath($WorkRoot)
$sourceRoot = Join-Path $WorkRoot 'source'
$smallRoot = Join-Path $sourceRoot 'small'
$largePath = Join-Path $sourceRoot 'large.bin'
$downloadRoot = Join-Path $WorkRoot 'download'
$smallDownload = Join-Path $downloadRoot 'small'
$largeDownload = Join-Path $downloadRoot 'large.bin'

function Remove-BenchmarkArtifacts {
    # 清理本地生成数据（source/download）与远端基准目录。
    # 仅在测试成功路径调用；清理失败只警告，不影响已完成的测试结论。
    try {
        foreach ($path in @($sourceRoot, $downloadRoot)) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force
            }
        }
        # 远端文件根取 RC_AGENT_FILE_ROOT，未显式配置时按 Agent 文档回退到运行账户用户目录。
        $remoteCommand = '$root = $env:RC_AGENT_FILE_ROOT; if (-not $root) { $root = [Environment]::GetFolderPath(''UserProfile'') }; $target = Join-Path $root ' + "'$RemoteRoot'" + '; if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force; Write-Output ("removed:" + $target) } else { Write-Output ("absent:" + $target) }'
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $cleanupOutput = @(& $CliPath 'exec' $Target '--fingerprint' $Fingerprint '--command' $remoteCommand '--text' 2>&1)
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Remote benchmark root cleanup failed (exit $LASTEXITCODE): $($cleanupOutput -join ' ')"
        }
    }
    catch {
        Write-Warning "Benchmark artifact cleanup failed: $($_.Exception.Message)"
    }
}

if (-not (Test-Path -LiteralPath $CliPath -PathType Leaf)) {
    throw "Rc.Cli.exe was not found: $CliPath"
}

if (-not $SkipGenerate) {
    [System.IO.Directory]::CreateDirectory($smallRoot) | Out-Null
    $minimum = 1KB
    $maximum = 5MB
    for ($index = 0; $index -lt 100; $index++) {
        $ratio = $index / 99.0
        $length = [long][Math]::Round($minimum + (($maximum - $minimum) * $ratio * $ratio))
        $directory = Join-Path $smallRoot ('group-{0:D2}' -f [int][Math]::Floor($index / 10))
        Write-PatternFile -Path (Join-Path $directory ('file-{0:D3}.bin' -f $index)) -Length $length -Seed (1000 + $index)
    }
    Write-PatternFile -Path $largePath -Length 1GB -Seed 2000 -BufferSize 1MB
}

$smallSourceDigest = @(Get-TreeDigest -Root $smallRoot)
if ($smallSourceDigest.Count -ne 100) {
    throw "Expected 100 small files, found $($smallSourceDigest.Count)."
}
if (($smallSourceDigest | Measure-Object Length -Minimum).Minimum -ne 1KB -or
    ($smallSourceDigest | Measure-Object Length -Maximum).Maximum -ne 5MB) {
    throw 'Small-file sizes do not span exactly 1 KiB through 5 MiB.'
}
$largeLength = (Get-Item -LiteralPath $largePath).Length
if ($largeLength -ne 1GB) {
    throw "Expected a 1 GiB large file, found $largeLength bytes."
}

if (Test-Path -LiteralPath $downloadRoot) {
    Remove-Item -LiteralPath $downloadRoot -Recurse -Force
}
[System.IO.Directory]::CreateDirectory($downloadRoot) | Out-Null

$smallBytes = ($smallSourceDigest | Measure-Object Length -Sum).Sum
$common = @('--fingerprint', $Fingerprint, '--chunk-size', [string]$ChunkSize)
$measurements = @()
$measurements += Invoke-CopyMeasurement -Name 'copy-upload-small' -Bytes $smallBytes -Arguments (@(
    'copy', 'upload', $Target, $smallRoot, '--to', "$RemoteRoot/small") + $common)
$measurements += Invoke-CopyMeasurement -Name 'copy-download-small' -Bytes $smallBytes -Arguments (@(
    'copy', 'download', $Target, "$RemoteRoot/small", '--to', $smallDownload) + $common)

$smallDownloadedDigest = @(Get-TreeDigest -Root $smallDownload)
Assert-DigestEqual -Expected $smallSourceDigest -Actual $smallDownloadedDigest -Label 'Small-file transfer'

$measurements += Invoke-CopyMeasurement -Name 'copy-upload-large' -Bytes $largeLength -Arguments (@(
    'copy', 'upload', $Target, $largePath, '--to', "$RemoteRoot/large.bin") + $common)
$measurements += Invoke-CopyMeasurement -Name 'copy-download-large' -Bytes $largeLength -Arguments (@(
    'copy', 'download', $Target, "$RemoteRoot/large.bin", '--to', $largeDownload) + $common)

$largeSourceHash = (Get-FileHash -LiteralPath $largePath -Algorithm SHA256).Hash
$largeDownloadedHash = (Get-FileHash -LiteralPath $largeDownload -Algorithm SHA256).Hash
if ($largeSourceHash -cne $largeDownloadedHash) {
    throw 'Large-file SHA-256 mismatch.'
}

if (-not $KeepArtifacts) {
    Remove-BenchmarkArtifacts
}

[pscustomobject]@{
    Passed = $true
    RemoteRoot = $RemoteRoot
    ChunkSize = $ChunkSize
    SmallFileCount = $smallSourceDigest.Count
    SmallMinimumBytes = ($smallSourceDigest | Measure-Object Length -Minimum).Minimum
    SmallMaximumBytes = ($smallSourceDigest | Measure-Object Length -Maximum).Maximum
    SmallTotalBytes = $smallBytes
    LargeBytes = $largeLength
    SmallSha256Verified = $true
    LargeSha256Verified = $true
    Measurements = $measurements
} | ConvertTo-Json -Depth 5
