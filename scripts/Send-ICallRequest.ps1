<#
.SYNOPSIS
Send a find/arrived/link/assign request to the iCallManager SMB JSON bridge.
.EXAMPLE
.\Send-ICallRequest.ps1 -PatientId '00011'
.EXAMPLE
.\Send-ICallRequest.ps1 -BridgeDirectory '\\iCallPC\iCallBridge' -PatientId '00011' -Action arrived
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PatientId,
    [ValidateSet('find', 'arrived', 'link', 'assign')]
    [string]$Action = 'find',
    [string]$BridgeDirectory = '',
    [string]$ExpectedReceptionNo = '',
    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PatientId) -or $PatientId -eq '-') {
    throw 'Specify a patient ID. Unassigned reservations cannot be operated by this API.'
}
if ([string]::IsNullOrWhiteSpace($BridgeDirectory)) {
    $settingsPath = Join-Path $env:LOCALAPPDATA 'iCallManager\settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
        $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $BridgeDirectory = $settings.bridgeDirectory
    }
    if ([string]::IsNullOrWhiteSpace($BridgeDirectory)) {
        $BridgeDirectory = Join-Path $env:LOCALAPPDATA 'iCallManager\Bridge'
    }
}
$requestDirectory = Join-Path $BridgeDirectory 'request'
$responseDirectory = Join-Path $BridgeDirectory 'response'
if (!(Test-Path -LiteralPath $requestDirectory -PathType Container) -or
    !(Test-Path -LiteralPath $responseDirectory -PathType Container)) {
    throw "Bridge folders were not found: $BridgeDirectory. Start iCallManager and check the share path."
}

function Send-BridgeCommand([string]$Command, $Reservation) {
    $requestId = [guid]::NewGuid().ToString('N')
    $request = [ordered]@{
        requestId = $requestId
        action = $Command
        patientId = $PatientId
    }
    if ($Command -eq 'assign') {
        if (![string]::IsNullOrWhiteSpace($ExpectedReceptionNo)) { $request.expectedReceptionNo = $ExpectedReceptionNo }
    }
    elseif ($Command -ne 'find') {
        $request.expectedReceptionNo = [string]$Reservation.receptionNo
        $request.expectedPatientName = [string]$Reservation.patientName
    }
    $temporaryPath = Join-Path $requestDirectory ($requestId + '.tmp')
    $requestPath = Join-Path $requestDirectory ($requestId + '.json')
    $responsePath = Join-Path $responseDirectory ($requestId + '.json')
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    try {
        [System.IO.File]::WriteAllText($temporaryPath, ($request | ConvertTo-Json), $utf8)
        [System.IO.File]::Move($temporaryPath, $requestPath)
    }
    finally {
        if ([System.IO.File]::Exists($temporaryPath)) { [System.IO.File]::Delete($temporaryPath) }
    }
    Write-Host "Sent $Command. requestId=$requestId"
    Write-Host "Response: $responsePath"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (Test-Path -LiteralPath $responsePath) {
            $reply = Get-Content -LiteralPath $responsePath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ([string]$reply.requestId -cne $requestId -or [string]$reply.patientId -cne $PatientId) {
                throw 'The response identity does not match the request.'
            }
            return $reply
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out. The request was NOT cancelled. Check $responsePath before running another operation."
}

if ($Action -eq 'assign') {
    if ($PatientId -notmatch '\A[0-9]{1,50}\z') { throw 'Assign requires a numeric chart number without a Dynamics branch digit.' }
    return Send-BridgeCommand 'assign' $null
}
$found = Send-BridgeCommand 'find' $null
if ($Action -eq 'find' -or $found.success -ne $true) { return $found }
if ($found.code -ne 'found' -or [string]::IsNullOrWhiteSpace($found.receptionNo) -or
    [string]::IsNullOrWhiteSpace($found.patientName)) {
    throw 'The reservation response is incomplete. No operation was sent.'
}
Send-BridgeCommand $Action $found
