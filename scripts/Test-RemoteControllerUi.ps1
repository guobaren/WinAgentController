[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Endpoint,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$Fingerprint,
    [string]$RcCliPath = (Join-Path $PSScriptRoot '..\artifacts\publish\Rc.Cli.exe'),
    [ValidateRange(0, 10000)][int]$StepDelayMilliseconds = 800,
    [ValidateRange(0, 30)][int]$RetryableUiAttempts = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testWindowTitle = 'RemoteController UI Acceptance Test'
$verificationAutomationId = 'UiTestVerification'
$results = [System.Collections.Generic.List[string]]::new()

if (-not (Test-Path -LiteralPath $RcCliPath -PathType Leaf)) {
    throw "Rc.Cli.exe was not found: $RcCliPath"
}

function Invoke-RcJson([string[]]$Arguments) {
    for ($attempt = 0; $attempt -le $RetryableUiAttempts; $attempt++) {
        Write-Verbose ("rcctl " + ($Arguments -join ' '))
        # Native non-zero exits write a PowerShell error record. Keep it as data
        # here so a retryable Agent/UI response can be decoded and retried.
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $raw = @(& $RcCliPath @Arguments 2>&1)
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        $exitCode = $LASTEXITCODE
        $text = ($raw | Out-String).Trim()
        $jsonText = if ($text -match '(?s)(\{.*\})') { $Matches[1] } else { $text }
        try {
            $response = $jsonText | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            if ($exitCode -ne 0 -and $attempt -lt $RetryableUiAttempts) {
                Write-Verbose 'UI command returned undecodable native error output; retrying in one second.'
                Start-Sleep -Seconds 1
                continue
            }
            if ($exitCode -eq 0) { throw "rcctl did not return a JSON response: $text" }
            throw "rcctl failed with exit code ${exitCode}: $text"
        }
        if ($response.ok) {
            return $response.result
        }
        if ($response.error.retryable -and $attempt -lt $RetryableUiAttempts) {
            Write-Verbose "Retryable UI response ($($response.error.code)); retrying in one second."
            Start-Sleep -Seconds 1
            continue
        }
        throw "rcctl returned an error: $($response.error.message)"
    }
    throw 'rcctl retry loop ended without a response.'
}

function Invoke-Ui {
    param([Parameter(Mandatory)][string[]]$UiArguments)
    return Invoke-RcJson -Arguments ([string[]](@('ui') + $UiArguments))
}

function Wait-Step {
    if ($StepDelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $StepDelayMilliseconds }
}

function Find-UiElement($Node, [string]$AutomationId, [string]$Name = '') {
    if (($AutomationId -and $Node.automationId -eq $AutomationId) -or ($Name -and $Node.name -eq $Name)) {
        return $Node
    }
    foreach ($child in @($Node.children)) {
        $found = Find-UiElement $child $AutomationId $Name
        if ($null -ne $found) { return $found }
    }
    return $null
}

function Get-Tree {
    return (Invoke-Ui -UiArguments @('elements', $Endpoint, '--fingerprint', $Fingerprint, 'window', "$script:windowHandle", '--depth', '12', '--limit', '1000')).root
}

function Get-Element([string]$AutomationId, [string]$Name = '') {
    $element = Find-UiElement (Get-Tree) $AutomationId $Name
    if ($null -eq $element) {
        $identity = if ($AutomationId) { "AutomationId '$AutomationId'" } else { "Name '$Name'" }
        throw "The UI test element with $identity was not found. Ensure the visible test application is open and unobscured."
    }
    return $element
}

function Get-RuntimeId($Element) {
    return [string]::Join(',', @($Element.runtimeId))
}

function Assert-Verification([string]$Expected) {
    $element = Get-Element $verificationAutomationId
    $actual = [string]$element.name
    $expectedName = "Verification: $Expected"
    if ($actual -ne $expectedName) {
        throw "Expected visible verification '$expectedName', but received '$actual'."
    }
    $results.Add($Expected)
    Write-Host "[PASS] $Expected"
}

function Assert-VerificationPattern([string]$Pattern, [string]$Description) {
    $element = Get-Element $verificationAutomationId
    $actual = [string]$element.name
    if ($actual -notmatch $Pattern) {
        throw "Expected visible verification matching '$Pattern', but received '$actual'."
    }
    $results.Add($Description)
    Write-Host "[PASS] $Description"
}

function Invoke-ElementAction($Element, [string]$Action, [string]$Value = '') {
    $arguments = @('element', $Endpoint, '--fingerprint', $Fingerprint, 'window', "$script:windowHandle", (Get-RuntimeId $Element), $Action)
    if ($Action -eq 'setvalue') { $arguments += $Value }
    Invoke-Ui -UiArguments $arguments | Out-Null
}

$status = Invoke-Ui -UiArguments @('status', $Endpoint, '--fingerprint', $Fingerprint)
if (-not $status.session.isActive) {
    throw 'The endpoint has no active interactive UI session. Log in to the configured endpoint user before starting the UI test application.'
}

$windows = (Invoke-Ui -UiArguments @('windows', $Endpoint, '--fingerprint', $Fingerprint)).windows
$testWindow = @($windows | Where-Object { $_.title -eq $testWindowTitle -and $_.isVisible } | Select-Object -First 1)
if ($testWindow.Count -ne 1) {
    throw "The visible '$testWindowTitle' window was not found. On the endpoint desktop, run Start-RemoteControllerUiTest.cmd first."
}
$script:windowHandle = [long]$testWindow[0].handle
Write-Host "Testing visible UI window handle $script:windowHandle on $Endpoint."

$originalClipboard = Invoke-Ui -UiArguments @('clipboard', $Endpoint, '--fingerprint', $Fingerprint, 'read')
$originalClipboardBytes = [Convert]::FromBase64String([string]$originalClipboard.data)
try {
    $reset = Get-Element 'UiTestResetButton'
    Invoke-ElementAction $reset 'invoke'
    Wait-Step
    Assert-Verification 'ready'

    $invoke = Get-Element 'UiTestInvokeButton'
    Invoke-ElementAction $invoke 'invoke'
    Wait-Step
    Assert-Verification 'invoke:1'

    $value = 'remote-value'
    $valueBox = Get-Element 'UiTestValueBox'
    Invoke-ElementAction $valueBox 'setvalue' $value
    Wait-Step
    Assert-Verification "value:$value"

    $dropDown = Get-Element 'UiTestSelectionBox'
    Invoke-ElementAction $dropDown 'focus'
    Invoke-ElementAction $dropDown 'expand'
    Wait-Step
    Assert-Verification 'dropdown:expanded'
    Invoke-Ui -UiArguments @('key', $Endpoint, '--fingerprint', $Fingerprint, 'window', "$script:windowHandle", 'down', 'Down') | Out-Null
    Invoke-Ui -UiArguments @('key', $Endpoint, '--fingerprint', $Fingerprint, 'window', "$script:windowHandle", 'up', 'Down') | Out-Null
    Wait-Step
    Assert-Verification 'selection:Beta'
    Invoke-ElementAction $dropDown 'collapse'
    Wait-Step
    Assert-Verification 'dropdown:collapsed'
    $selectionList = Get-Element 'UiTestSelectionList'
    $beta = Find-UiElement $selectionList '' 'Beta'
    if ($null -eq $beta) { throw "The selection test's second option was not found." }
    Invoke-ElementAction $beta 'select'
    Wait-Step
    Assert-Verification 'selection:Beta'

    $topTreeNode = Get-Element '' 'Top expandable test node'
    Invoke-ElementAction $topTreeNode 'expand'
    Wait-Step
    Assert-Verification 'tree:top:expanded'
    $nestedTreeNode = Get-Element '' 'Nested expandable test node'
    Invoke-ElementAction $nestedTreeNode 'expand'
    Wait-Step
    Assert-Verification 'tree:nested:expanded'
    Invoke-ElementAction $nestedTreeNode 'collapse'
    Wait-Step
    Assert-Verification 'tree:nested:collapsed'
    Invoke-ElementAction $topTreeNode 'collapse'
    Wait-Step
    Assert-Verification 'tree:top:collapsed'

    $mouseSurface = Get-Element 'UiTestMouseSurface'
    $mouseStartX = [Math]::Min([int]$testWindow[0].width - 1, [Math]::Max(0, [int]($mouseSurface.x + ($mouseSurface.width / 3) - $testWindow[0].x)))
    $mouseStartY = [Math]::Min([int]$testWindow[0].height - 1, [Math]::Max(0, [int]($mouseSurface.y + ($mouseSurface.height / 3) - $testWindow[0].y)))
    $mouseEndX = [Math]::Min([int]$testWindow[0].width - 1, [Math]::Max(0, [int]($mouseSurface.x + (($mouseSurface.width * 2) / 3) - $testWindow[0].x)))
    $mouseEndY = [Math]::Min([int]$testWindow[0].height - 1, [Math]::Max(0, [int]($mouseSurface.y + (($mouseSurface.height * 2) / 3) - $testWindow[0].y)))
    foreach ($mouseButton in @('left', 'middle', 'right')) {
        Invoke-Ui -UiArguments @('mouse', $Endpoint, '--fingerprint', $Fingerprint, 'move', 'window', "$script:windowHandle", "$mouseStartX", "$mouseStartY") | Out-Null
        Wait-Step
        Invoke-Ui -UiArguments @('mouse', $Endpoint, '--fingerprint', $Fingerprint, 'button', 'window', "$script:windowHandle", $mouseButton, 'down') | Out-Null
        Wait-Step
        Assert-Verification "mouse:down:$mouseButton"
        Invoke-Ui -UiArguments @('mouse', $Endpoint, '--fingerprint', $Fingerprint, 'move', 'window', "$script:windowHandle", "$mouseEndX", "$mouseEndY") | Out-Null
        Wait-Step
        Assert-VerificationPattern '^Verification: mouse:drag:\d+,\d+$' "mouse:drag:$mouseButton"
        Invoke-Ui -UiArguments @('mouse', $Endpoint, '--fingerprint', $Fingerprint, 'button', 'window', "$script:windowHandle", $mouseButton, 'up') | Out-Null
        Wait-Step
        Assert-Verification "mouse:up:$mouseButton"
    }
    # 鼠标定位与按键/滚轮操作保持分离：滚轮测试开始前先单独移动一次，
    # 后续两次滚轮仅验证滚轮本身，不把定位动作混入每个滚轮步骤。
    Invoke-Ui -UiArguments @('mouse', $Endpoint, '--fingerprint', $Fingerprint, 'move', 'window', "$script:windowHandle", "$mouseStartX", "$mouseStartY") | Out-Null
    Wait-Step
    Invoke-Ui -UiArguments @('mouse', $Endpoint, '--fingerprint', $Fingerprint, 'wheel', 'window', "$script:windowHandle", '120') | Out-Null
    Wait-Step
    Assert-Verification 'mouse:wheel:up:4'
    Invoke-Ui -UiArguments @('mouse', $Endpoint, '--fingerprint', $Fingerprint, 'wheel', 'window', "$script:windowHandle", '-120') | Out-Null
    Wait-Step
    Assert-Verification 'mouse:wheel:down:5'

    $keyboard = Get-Element 'UiTestKeyboardBox'
    Invoke-ElementAction $keyboard 'focus'
    Wait-Step
    Invoke-Ui -UiArguments @('key', $Endpoint, '--fingerprint', $Fingerprint, 'window', "$script:windowHandle", 'down', 'A') | Out-Null
    Invoke-Ui -UiArguments @('key', $Endpoint, '--fingerprint', $Fingerprint, 'window', "$script:windowHandle", 'up', 'A') | Out-Null
    Wait-Step
    # Verify the virtual key event, not composed text: a Chinese or other IME
    # may defer or transform text while the key is still delivered correctly.
    Assert-Verification 'keyboard:key:A'

    $clipboardValue = 'RemoteController-UiAcceptance'
    Invoke-Ui -UiArguments @('clipboard', $Endpoint, '--fingerprint', $Fingerprint, 'write', $clipboardValue) | Out-Null
    Wait-Step
    $clipboardButton = Get-Element 'UiTestClipboardButton'
    Invoke-ElementAction $clipboardButton 'invoke'
    Wait-Step
    Assert-Verification "clipboard:$clipboardValue"
    $clipboardReadBack = Invoke-Ui -UiArguments @('clipboard', $Endpoint, '--fingerprint', $Fingerprint, 'read')
    $clipboardText = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([string]$clipboardReadBack.data))
    if ($clipboardText -ne $clipboardValue) {
        throw "Clipboard read-back mismatch. Expected '$clipboardValue', received '$clipboardText'."
    }
    $results.Add('clipboard:read-back')
    Write-Host '[PASS] clipboard:read-back'
}
finally {
    if ($originalClipboardBytes.Length -eq 0) {
        # 不依赖 Windows PowerShell 将空字符串原样传给原生 rcctl；使用显式 clear 保留空剪贴板语义。
        Invoke-Ui -UiArguments @('clipboard', $Endpoint, '--fingerprint', $Fingerprint, 'clear') | Out-Null
    }
    else {
        $originalClipboardText = [Text.Encoding]::UTF8.GetString($originalClipboardBytes)
        Invoke-Ui -UiArguments @('clipboard', $Endpoint, '--fingerprint', $Fingerprint, 'write', $originalClipboardText) | Out-Null
    }
}

Write-Host ''
Write-Host "UI acceptance passed ($($results.Count) checks): $($results -join ', ')"
[pscustomobject]@{
    ok = $true
    checkCount = $results.Count
    checks = @($results)
} | ConvertTo-Json -Compress
