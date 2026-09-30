$ErrorActionPreference = 'Stop'
$bridgeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('iCall-script-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $bridgeRoot 'request'), (Join-Path $bridgeRoot 'response') | Out-Null
$clientScript = Join-Path $PSScriptRoot '..\scripts\Send-ICallRequest.ps1'
# Synthetic server only: no UIA, no real iCall request folder, no button operations.
$server = Start-Job -ArgumentList $bridgeRoot -ScriptBlock {
    param($root)
    $ErrorActionPreference = 'Stop'
    $seen = @()
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ($seen.Count -lt 7 -and [DateTime]::UtcNow -lt $deadline) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'request') -Filter '*.json') {
            $request = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            $seen += $request
            $found = $request.patientId -eq '00011'
            $reply = @{
                requestId = $request.requestId; patientId = $request.patientId
                success = $found; code = $(if (!$found) { 'not_found' } elseif ($request.action -eq 'find') { 'found' } elseif ($request.action -eq 'assign') { 'assigned' } else { 'invoked' })
                receptionNo = $(if ($found) { '102' } else { $null })
                patientName = $(if ($found) { 'Synthetic Patient' } else { $null })
            }
            $path = Join-Path (Join-Path $root 'response') $file.Name
            $reply | ConvertTo-Json | Set-Content -LiteralPath ($path + '.tmp') -Encoding UTF8
            Remove-Item -LiteralPath $file.FullName
            [System.IO.File]::Move(($path + '.tmp'), $path)
        }
        Start-Sleep -Milliseconds 50
    }
    if ($seen.Count -ne 7) { throw 'Unexpected request count' }
    $seen | ConvertTo-Json -Depth 5
}
try {
    foreach ($action in 'find', 'arrived', 'link') {
        $reply = & $clientScript -PatientId '00011' -Action $action -BridgeDirectory $bridgeRoot -TimeoutSeconds 10
        $expected = if ($action -eq 'find') { 'found' } else { 'invoked' }
        if (!$reply.success -or $reply.code -ne $expected -or $reply.receptionNo -ne '102') { throw "Unexpected $action response" }
    }
    $missing = & $clientScript -PatientId 'missing' -Action arrived -BridgeDirectory $bridgeRoot -TimeoutSeconds 10
    if ($missing.success -or $missing.code -ne 'not_found') { throw 'Failed lookup was not returned' }
    $assigned = & $clientScript -PatientId '00011' -Action assign -ExpectedReceptionNo '101' -BridgeDirectory $bridgeRoot -TimeoutSeconds 10
    if (!$assigned.success -or $assigned.code -ne 'assigned' -or $assigned.receptionNo -ne '102') { throw 'Unexpected assign response' }
    $completed = Wait-Job $server -Timeout 10
    if (!$completed -or $server.State -ne 'Completed') { throw 'Synthetic server did not complete' }
    $requests = Receive-Job $server | ConvertFrom-Json
    if (($requests.action -join ',') -ne 'find,find,arrived,find,link,find,assign') { throw 'Unexpected operation sequence' }
    foreach ($request in $requests | Where-Object { $_.action -ne 'find' -and $_.action -ne 'assign' }) {
        if ($request.expectedReceptionNo -ne '102' -or $request.expectedPatientName -ne 'Synthetic Patient') { throw 'Missing identity guard' }
    }
    if (($requests.requestId | Select-Object -Unique).Count -ne 7) { throw 'Request IDs reused' }
    if ($requests[-1].expectedReceptionNo -ne '101') { throw 'Missing explicit dummy target' }
    Write-Output 'PASS: find, arrived, link, assign, failed lookup, identity guards and unique IDs (synthetic server only).'
}
finally {
    Stop-Job $server
    Remove-Job $server
}
